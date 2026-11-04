using System.Text.Json;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Auth;

/// <summary>
/// One audited request. <see cref="Query"/> is null unless
/// <c>Memoria:Auth:Audit:IncludeQueries</c> is on. Document content is never recorded.
/// </summary>
public sealed record AuditRecord(
	DateTimeOffset Timestamp,
	string KeyId,
	string Operation,
	IReadOnlyList<string>? Collections,
	int? ResultCount,
	int Status,
	string TraceId,
	string? Query);

public interface IAuditLog
{
	/// <summary>Stores a record. Must not throw for a storage failure: the request has already been served.</summary>
	Task WriteAsync(AuditRecord record);
}

/// <summary>Used when authentication is disabled: there is no key to attribute a request to.</summary>
public sealed class NullAuditLog : IAuditLog
{
	public Task WriteAsync(AuditRecord record)
	{
		return Task.CompletedTask;
	}
}

/// <summary>Writes each record to the control database and to the log stream.</summary>
public sealed partial class SqliteAuditLog(
	ControlDatabase database,
	IOptions<MemoriaOptions> options,
	TimeProvider timeProvider,
	ILogger<SqliteAuditLog> logger) : IAuditLog
{
	private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

	private readonly int _retentionDays = options.Value.Auth.Audit.RetentionDays;

	private long _lastPruneTicks;

	public async Task WriteAsync(AuditRecord record)
	{
		ArgumentNullException.ThrowIfNull(record);
		string? collections = record.Collections is null ? null : JsonSerializer.Serialize(record.Collections);
		LogAudit(logger, record.KeyId, record.Operation, collections, record.ResultCount, record.Status, record.TraceId);

		try
		{
			await using SqliteConnection connection = await database.OpenAsync();
			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				INSERT INTO audit_log (timestamp, key_id, operation, collections, result_count, status, trace_id, query)
				VALUES ($timestamp, $key, $operation, $collections, $count, $status, $trace, $query);
				""";
			command.Parameters.AddWithValue("$timestamp", ControlDatabase.FormatTimestamp(record.Timestamp));
			command.Parameters.AddWithValue("$key", record.KeyId);
			command.Parameters.AddWithValue("$operation", record.Operation);
			command.Parameters.AddWithValue("$collections", (object?)collections ?? DBNull.Value);
			command.Parameters.AddWithValue("$count", (object?)record.ResultCount ?? DBNull.Value);
			command.Parameters.AddWithValue("$status", record.Status);
			command.Parameters.AddWithValue("$trace", record.TraceId);
			command.Parameters.AddWithValue("$query", (object?)record.Query ?? DBNull.Value);
			await command.ExecuteNonQueryAsync();
			await PruneIfDueAsync(connection);
		}
		catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
		{
			LogWriteFailed(logger, ex, record.TraceId);
		}
	}

	private async Task PruneIfDueAsync(SqliteConnection connection)
	{
		DateTimeOffset now = timeProvider.GetUtcNow();
		long last = Interlocked.Read(ref _lastPruneTicks);
		if (_retentionDays <= 0 || now.UtcTicks - last < PruneInterval.Ticks || Interlocked.CompareExchange(ref _lastPruneTicks, now.UtcTicks, last) != last)
		{
			return;
		}

		await using SqliteCommand prune = connection.CreateCommand();
		prune.CommandText = "DELETE FROM audit_log WHERE timestamp < $cutoff;";
		prune.Parameters.AddWithValue("$cutoff", ControlDatabase.FormatTimestamp(now.AddDays(-_retentionDays)));
		await prune.ExecuteNonQueryAsync();
	}

	[LoggerMessage(EventId = 3100, Level = LogLevel.Information, Message = "Audit: key {KeyId} {Operation} collections {Collections} results {ResultCount} status {Status} (trace {TraceId})")]
	private static partial void LogAudit(ILogger logger, string keyId, string operation, string? collections, int? resultCount, int status, string traceId);

	[LoggerMessage(EventId = 3101, Level = LogLevel.Error, Message = "Could not store the audit record for trace {TraceId}")]
	private static partial void LogWriteFailed(ILogger logger, Exception exception, string traceId);
}

/// <summary>What the current request's handler reports for its audit record.</summary>
public interface IRequestAudit
{
	/// <summary>Replaces the operation name taken from the route, as MCP does with the tool name.</summary>
	void SetOperation(string operation);

	/// <summary>The caller's query or document reference. Recorded only when explicitly enabled.</summary>
	void SetQuery(string? query);

	void SetResult(IEnumerable<string> collections, int resultCount);
}

/// <summary>Reaches the entry through the request itself, so it works from any DI scope serving that request.</summary>
public sealed class HttpRequestAudit(IHttpContextAccessor accessor) : IRequestAudit
{
	public void SetOperation(string operation)
	{
		Entry?.Operation = operation;
	}

	public void SetQuery(string? query)
	{
		Entry?.Query = query;
	}

	public void SetResult(IEnumerable<string> collections, int resultCount)
	{
		if (Entry is { } entry)
		{
			entry.Collections = collections.Distinct(StringComparer.Ordinal).ToArray();
			entry.ResultCount = resultCount;
		}
	}

	private AuditEntry? Entry => accessor.HttpContext?.Features.Get<AuditEntry>();
}

/// <summary>Mutable per-request state the audit middleware turns into an <see cref="AuditRecord"/>.</summary>
public sealed class AuditEntry
{
	public string? Operation { get; set; }

	public string? Query { get; set; }

	public IReadOnlyList<string>? Collections { get; set; }

	public int? ResultCount { get; set; }
}

/// <summary>
/// Records every request an API key makes to a scoped route, including ones
/// that end in 403 or 429. Anonymous failures are not recorded here: they have
/// no key to attribute, and letting them write would let anyone grow the table.
/// </summary>
public sealed class AuditMiddleware(RequestDelegate next, IAuditLog auditLog, IOptions<MemoriaOptions> options, TimeProvider timeProvider)
{
	private readonly bool _includeQueries = options.Value.Auth.Audit.IncludeQueries;

	public async Task InvokeAsync(HttpContext context)
	{
		string? keyId = context.User.FindFirst(MemoriaClaims.KeyId)?.Value;
		Endpoint? endpoint = context.GetEndpoint();
		if (keyId is null || endpoint?.Metadata.GetMetadata<MemoriaScopeAttribute>() is null)
		{
			await next(context);
			return;
		}

		AuditEntry entry = new();
		context.Features.Set(entry);
		int? failureStatus = null;
		try
		{
			await next(context);
		}
		catch (Exception ex)
		{
			failureStatus = ex switch
			{
				SidecarException sidecar => MemoriaExceptionHandler.Map(sidecar).Status,
				CollectionForbiddenException => StatusCodes.Status403Forbidden,
				_ => StatusCodes.Status500InternalServerError,
			};
			throw;
		}
		finally
		{
			await auditLog.WriteAsync(new AuditRecord(
				timeProvider.GetUtcNow(),
				keyId,
				entry.Operation ?? OperationName(endpoint),
				entry.Collections,
				entry.ResultCount,
				failureStatus ?? context.Response.StatusCode,
				RequestLoggingMiddleware.CurrentTraceId(context),
				_includeQueries ? entry.Query : null));
		}
	}

	private static string OperationName(Endpoint endpoint)
	{
		return endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
			?? endpoint.Metadata.GetMetadata<IRouteDiagnosticsMetadata>()?.Route
			?? endpoint.DisplayName
			?? "unknown";
	}
}
