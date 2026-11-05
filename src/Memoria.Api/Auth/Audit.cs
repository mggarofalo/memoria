using System.Collections.Concurrent;
using System.Text.Json;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Core.Redaction;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Auth;

/// <summary>The key an admin operation acted on. Identity and permissions only, never the secret.</summary>
public sealed record AuditTarget(string KeyId, IReadOnlyList<string> Scopes, IReadOnlyList<string> Collections, bool AllCollections);

/// <summary>
/// One audited request. <see cref="AuditId"/> is generated here and is the row's
/// identity. <see cref="TraceId"/> correlates with logs but comes from the
/// caller's <c>traceparent</c> when they send one, so it proves nothing.
/// <see cref="Status"/> is how the operation ended, which for an MCP tool call is
/// not the HTTP status. <see cref="Query"/> is null unless
/// <c>Memoria:Auth:Audit:IncludeQueries</c> is on. Document content is never recorded.
/// </summary>
public sealed record AuditRecord(
	string AuditId,
	DateTimeOffset Timestamp,
	string KeyId,
	string Operation,
	IReadOnlyList<string>? Collections,
	IReadOnlyList<string>? AttemptedCollections,
	int? ResultCount,
	int Status,
	string TraceId,
	object? Target,
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

	private readonly int _maxRowsPerKey = options.Value.Auth.Audit.MaxRowsPerKey;

	private readonly ConcurrentDictionary<string, int> _writesSinceTrim = new(StringComparer.Ordinal);

	private readonly ConcurrentDictionary<string, byte> _keysTrimmed = new(StringComparer.Ordinal);

	private long _lastPruneTicks;

	/// <summary>Writes between per-key trims, so a key's rows stay within the cap plus this many.</summary>
	private int TrimEvery => Math.Clamp(_maxRowsPerKey / 10, 1, 1000);

	public async Task WriteAsync(AuditRecord record)
	{
		ArgumentNullException.ThrowIfNull(record);
		string? collections = record.Collections is null ? null : JsonSerializer.Serialize(record.Collections);
		string? attempted = record.AttemptedCollections is null ? null : JsonSerializer.Serialize(record.AttemptedCollections);
		string? target = record.Target is null ? null : JsonSerializer.Serialize(record.Target, record.Target.GetType());
		LogAudit(logger, record.AuditId, record.KeyId, record.Operation, collections, attempted, record.ResultCount, record.Status, record.TraceId, target);

		try
		{
			await using SqliteConnection connection = await database.OpenAsync();
			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				INSERT INTO audit_log (audit_id, timestamp, key_id, operation, collections, attempted_collections, result_count, status, trace_id, target, query)
				VALUES ($audit, $timestamp, $key, $operation, $collections, $attempted, $count, $status, $trace, $target, $query);
				""";
			command.Parameters.AddWithValue("$audit", record.AuditId);
			command.Parameters.AddWithValue("$timestamp", ControlDatabase.FormatTimestamp(record.Timestamp));
			command.Parameters.AddWithValue("$key", record.KeyId);
			command.Parameters.AddWithValue("$operation", record.Operation);
			command.Parameters.AddWithValue("$collections", (object?)collections ?? DBNull.Value);
			command.Parameters.AddWithValue("$attempted", (object?)attempted ?? DBNull.Value);
			command.Parameters.AddWithValue("$count", (object?)record.ResultCount ?? DBNull.Value);
			command.Parameters.AddWithValue("$status", record.Status);
			command.Parameters.AddWithValue("$trace", record.TraceId);
			command.Parameters.AddWithValue("$target", (object?)target ?? DBNull.Value);
			command.Parameters.AddWithValue("$query", (object?)record.Query ?? DBNull.Value);
			await command.ExecuteNonQueryAsync();
			await TrimKeyIfDueAsync(connection, record.KeyId);
			await PruneIfDueAsync(connection);
		}
		catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
		{
			LogWriteFailed(logger, ex, record.AuditId);
		}
	}

	private async Task TrimKeyIfDueAsync(SqliteConnection connection, string keyId)
	{
		if (_writesSinceTrim.AddOrUpdate(keyId, 1, static (_, count) => count + 1) < TrimEvery)
		{
			return;
		}

		_writesSinceTrim[keyId] = 0;
		await using SqliteCommand trim = connection.CreateCommand();
		trim.CommandText = """
			DELETE FROM audit_log WHERE key_id = $key AND id <= (
				SELECT id FROM audit_log WHERE key_id = $key ORDER BY id DESC LIMIT 1 OFFSET $cap
			);
			""";
		trim.Parameters.AddWithValue("$key", keyId);
		trim.Parameters.AddWithValue("$cap", _maxRowsPerKey);
		if (await trim.ExecuteNonQueryAsync() > 0 && _keysTrimmed.TryAdd(keyId, 0))
		{
			LogFirstTrim(logger, keyId, _maxRowsPerKey);
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

	[LoggerMessage(EventId = 3100, Level = LogLevel.Information, Message = "Audit {AuditId}: key {KeyId} {Operation} collections {Collections} attempted {AttemptedCollections} results {ResultCount} status {Status} (trace {TraceId}) target {Target}")]
	private static partial void LogAudit(ILogger logger, string auditId, string keyId, string operation, string? collections, string? attemptedCollections, int? resultCount, int status, string traceId, string? target);

	[LoggerMessage(EventId = 3102, Level = LogLevel.Warning, Message = "Audit rows for key {KeyId} reached the {MaxRowsPerKey}-row cap, and its oldest rows are now being removed. Logged once per key per process.")]
	private static partial void LogFirstTrim(ILogger logger, string keyId, int maxRowsPerKey);

	[LoggerMessage(EventId = 3101, Level = LogLevel.Error, Message = "Could not store audit record {AuditId}")]
	private static partial void LogWriteFailed(ILogger logger, Exception exception, string auditId);
}

/// <summary>What the current request's handler reports for its audit record.</summary>
public interface IRequestAudit
{
	/// <summary>Replaces the operation name taken from the route, as MCP does with the tool name.</summary>
	void SetOperation(string operation);

	/// <summary>The caller's query or document reference. Recorded only when explicitly enabled.</summary>
	void SetQuery(string? query);

	void SetResult(IEnumerable<string> collections, int resultCount);

	/// <summary>How the operation ended, when the HTTP status does not say, and which collections a denied caller asked for.</summary>
	void SetOutcome(int status, IEnumerable<string>? attemptedCollections = null);

	/// <summary>What an operation acted on: an <see cref="AuditTarget"/> or an <c>AuditIngest</c>. Never content or a secret.</summary>
	void SetTarget(object target);
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

	public void SetOutcome(int status, IEnumerable<string>? attemptedCollections = null)
	{
		if (Entry is { } entry)
		{
			entry.Status = status;
			entry.AttemptedCollections = attemptedCollections?.Distinct(StringComparer.Ordinal).ToArray();
		}
	}

	public void SetTarget(object target)
	{
		Entry?.Target = target;
	}

	private AuditEntry? Entry => accessor.HttpContext?.Features.Get<AuditEntry>();
}

/// <summary>Mutable per-request state the audit middleware turns into an <see cref="AuditRecord"/>.</summary>
public sealed class AuditEntry
{
	public string? Operation { get; set; }

	public string? Query { get; set; }

	public IReadOnlyList<string>? Collections { get; set; }

	public IReadOnlyList<string>? AttemptedCollections { get; set; }

	public int? ResultCount { get; set; }

	public int? Status { get; set; }

	public object? Target { get; set; }
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
		try
		{
			await next(context);
		}
		catch (Exception ex)
		{
			entry.Status ??= ex switch
			{
				SidecarException sidecar => MemoriaExceptionHandler.Map(sidecar).Status,
				CollectionForbiddenException => StatusCodes.Status403Forbidden,
				IngestRefusedException refused => refused.Status,
				RedactionException => StatusCodes.Status422UnprocessableEntity,
				BadHttpRequestException bad => bad.StatusCode,
				_ => StatusCodes.Status500InternalServerError,
			};
			if (ex is CollectionForbiddenException forbidden)
			{
				entry.AttemptedCollections ??= forbidden.Requested;
			}

			throw;
		}
		finally
		{
			await auditLog.WriteAsync(new AuditRecord(
				Guid.CreateVersion7().ToString("N"),
				timeProvider.GetUtcNow(),
				keyId,
				entry.Operation ?? OperationName(endpoint),
				entry.Collections,
				entry.AttemptedCollections,
				entry.ResultCount,
				entry.Status ?? context.Response.StatusCode,
				RequestLoggingMiddleware.CurrentTraceId(context),
				entry.Target,
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
