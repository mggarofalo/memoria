using Memoria.Api.Http;
using Memoria.Core.Contracts;
using Memoria.Core.Redaction;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Http.Features;

namespace Memoria.Api.Ingest;

/// <summary>An ingest the API turned away, with the status, a stable code, and how long to wait when waiting helps.</summary>
public sealed class IngestRefusedException(int status, string code, string message, int? retryAfterSeconds = null) : Exception(message)
{
	public int Status { get; } = status;

	public string Code { get; } = code;

	public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>What the audit log keeps about a write: counts and outcome, and never content, a source id, or a session id.</summary>
public sealed record AuditIngest(
	string Kind,
	string Redaction,
	bool Complete,
	int Written,
	int Failed,
	int Deleted,
	int Redactions,
	int Refusals);

/// <summary>Bounds how many transcripts are rendered at once, since each can hold about 100 MB. A caller past it is told to come back rather than queued.</summary>
public sealed class IngestGate(int capacity)
{
	private const int RetryAfterSeconds = 5;

	private readonly SemaphoreSlim _slots = new(capacity, capacity);

	public async Task<T> RunAsync<T>(Func<Task<T>> work)
	{
		if (!_slots.Wait(0))
		{
			throw new IngestRefusedException(StatusCodes.Status503ServiceUnavailable, "ingest_busy", "Too many transcripts are being processed. Try again shortly.", RetryAfterSeconds);
		}

		try
		{
			return await work();
		}
		finally
		{
			_slots.Release();
		}
	}
}

internal static class IngestRules
{
	/// <summary>How far ahead of the clock a version may be. A date far in the future would make a document unchangeable, and undeletable by a later retry.</summary>
	public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromDays(1);

	public const int MaxIdentityChars = 1024;

	public const int RefusalsReturned = 200;

	public const int BusyRetrySeconds = 5;

	/// <summary>Scope comes first, so a caller learns nothing about a collection or document they may not write to.</summary>
	public static void RequireCollection(ICallerScope scope, string collection)
	{
		if (!scope.Allows(collection))
		{
			throw new CollectionForbiddenException(collection, [collection]);
		}
	}

	public static void RequireClaim(IngestClaim claim)
	{
		if (claim.Outcome == IngestClaimOutcome.Stale)
		{
			throw new IngestRefusedException(StatusCodes.Status409Conflict, "stale_version", "A newer version is already stored, or this was deleted after this version. Nothing was changed.");
		}

		if (claim.Outcome == IngestClaimOutcome.Busy)
		{
			throw new IngestRefusedException(StatusCodes.Status409Conflict, "ingest_in_progress", "Another upload of this is running. Nothing was changed.", BusyRetrySeconds);
		}
	}

	public static IngestRefusedException TooLarge(string detail)
	{
		return new IngestRefusedException(StatusCodes.Status413PayloadTooLarge, "document_too_large", detail);
	}
}

public static class RequestBody
{
	/// <summary>
	/// The body as a stream that fails past <paramref name="limit"/>. A declared length over it is refused before anything
	/// is read, and the server's own limit is set to match, because a body with no declared length gets no other check.
	/// </summary>
	public static BoundedReadStream Open(HttpContext context, long limit)
	{
		if (context.Request.ContentLength > limit)
		{
			throw TooLarge(limit);
		}

		AllowUpTo(context, limit);
		return new BoundedReadStream(context.Request.Body, limit, () => TooLarge(limit));
	}

	/// <summary>
	/// Raises the server's own limit, which is 30 MB unless told otherwise. Call it before anything can refuse the request: the server
	/// discards an unread body after an answer, and a body over its limit breaks the connection, so the caller would see a reset instead of the answer.
	/// </summary>
	public static void AllowUpTo(HttpContext context, long limit)
	{
		if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
		{
			feature.MaxRequestBodySize = limit;
		}
	}

	private static IngestRefusedException TooLarge(long limit)
	{
		return new IngestRefusedException(StatusCodes.Status413PayloadTooLarge, "payload_too_large", $"The request body may be at most {limit} bytes.");
	}
}

/// <summary>Registers what the ingest routes use.</summary>
public static partial class IngestHosting
{
	/// <summary>Says loudly which collections are stored unredacted, and which settings were meant to do that and did not.</summary>
	public static void WarnAboutRedactionSettings(this WebApplication app)
	{
		IngestRedactionPolicy policy = app.Services.GetRequiredService<IngestRedactionPolicy>();
		ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Memoria.Api.Ingest");
		foreach (string collection in policy.DisabledCollections)
		{
			LogRedactionDisabled(logger, collection);
		}

		foreach (string collection in policy.UnrecognizedSettings)
		{
			LogRedactionSettingIgnored(logger, collection);
		}
	}

	[LoggerMessage(EventId = 3300, Level = LogLevel.Warning, Message = "REDACTION IS OFF for collection {Collection} (Memoria:Ingest:Collections:{Collection}:Redaction = Disabled). Text ingested into it is stored as it came, credentials included, and search results from it can reach cloud models.")]
	private static partial void LogRedactionDisabled(ILogger logger, string collection);

	[LoggerMessage(EventId = 3301, Level = LogLevel.Warning, Message = "Memoria:Ingest:Collections:{Collection}:Redaction is not exactly Disabled, so collection {Collection} stays redacted. Remove the setting if that is what you meant.")]
	private static partial void LogRedactionSettingIgnored(ILogger logger, string collection);

	public static IServiceCollection AddMemoriaIngest(this IServiceCollection services)
	{
		services.AddSingleton<IIngestLedger, SqliteIngestLedger>();
		services.AddSingleton(provider => new IngestGate(provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Configuration.MemoriaOptions>>().Value.Ingest.MaxConcurrentSessions));
		services.AddSingleton<IRedactor, Redactor>();
		services.AddSingleton<IngestRedactionPolicy>();
		services.AddScoped<DocumentIngestor>();
		services.AddScoped<SessionIngestor>();
		return services;
	}
}
