using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Core.Redaction;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Ingest;

/// <summary>An ingest the API turned away, with the status, a stable code, and how long to wait when waiting helps.</summary>
public sealed class IngestRefusedException(int status, string code, string message, int? retryAfterSeconds = null) : Exception(message)
{
	public int Status { get; } = status;

	public string Code { get; } = code;

	public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// What the audit log keeps about a write: counts, outcome, and which files, and never content, a source id, or a session id.
/// A path is safe here: it is a digest for a document, and for a session it holds at most the id's first characters.
/// </summary>
public sealed record AuditIngest(
	string Kind,
	string Redaction,
	bool Complete,
	int Written,
	int Failed,
	int Deleted,
	int Redactions,
	int Refusals,
	IReadOnlyList<string> Paths);

/// <summary>
/// Bounds how many requests of one kind run at once, overall and per key, so one key's slow uploads leave room for everyone else's.
/// A caller past either bound is told to come back rather than queued.
/// </summary>
public sealed class IngestGate(int capacity, int perKey, string busyMessage)
{
	private const int RetryAfterSeconds = 5;

	private readonly Lock _lock = new();
	private readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);
	private int _running;

	public Slot Enter(string keyId)
	{
		lock (_lock)
		{
			int mine = _byKey.GetValueOrDefault(keyId);
			if (_running >= capacity || mine >= perKey)
			{
				throw new IngestRefusedException(StatusCodes.Status503ServiceUnavailable, "ingest_busy", busyMessage, RetryAfterSeconds);
			}

			_running++;
			_byKey[keyId] = mine + 1;
		}

		return new Slot(this, keyId);
	}

	private void Leave(string keyId)
	{
		lock (_lock)
		{
			_running--;
			int left = _byKey[keyId] - 1;
			if (left == 0)
			{
				_byKey.Remove(keyId);
			}
			else
			{
				_byKey[keyId] = left;
			}
		}
	}

	public sealed class Slot(IngestGate gate, string keyId) : IDisposable
	{
		public void Dispose()
		{
			gate.Leave(keyId);
		}
	}
}

/// <summary>The gates ingest passes through. Reading a request and doing the expensive work have separate bounds, so a slow client never holds up the work.</summary>
public sealed class IngestGates(IOptions<MemoriaOptions> options)
{
	private const int BusyRetrySeconds = 5;

	private static readonly TimeSpan WorkWait = TimeSpan.FromSeconds(30);

	private readonly SemaphoreSlim _work = new(options.Value.Ingest.MaxConcurrentRenders, options.Value.Ingest.MaxConcurrentRenders);

	public IngestGate Sessions { get; } = new(
		options.Value.Ingest.MaxConcurrentSessions,
		options.Value.Ingest.MaxConcurrentSessionsPerKey,
		"Too many transcripts are being processed. Try again shortly.");

	public IngestGate Documents { get; } = new(
		options.Value.Ingest.MaxConcurrentDocuments,
		options.Value.Ingest.MaxConcurrentDocumentsPerKey,
		"Too many documents are being processed. Try again shortly.");

	/// <summary>Runs redaction and rendering, which is what takes the CPU and memory. Someone who already sent a whole body gets to wait a little rather than send it again.</summary>
	public async Task<T> WorkAsync<T>(Func<T> work, CancellationToken cancellationToken)
	{
		if (!await _work.WaitAsync(WorkWait, cancellationToken))
		{
			throw new IngestRefusedException(StatusCodes.Status503ServiceUnavailable, "ingest_busy", "The server is busy redacting. Try again shortly.", BusyRetrySeconds);
		}

		try
		{
			return work();
		}
		finally
		{
			_work.Release();
		}
	}
}

internal static class IngestRules
{
	/// <summary>How far ahead of the clock a version may be. A version in the future blocks every honest update until that time, so this allows for clock skew and no more.</summary>
	public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromHours(1);

	public const int MaxIdentityChars = 1024;

	public const int RefusalsReturned = 200;

	public const int MaxAuditPaths = 64;

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
		IngestRefusedException? refusal = claim.Outcome switch
		{
			IngestClaimOutcome.Stale => new IngestRefusedException(StatusCodes.Status409Conflict, "stale_version", "A newer version is already stored, or this was deleted after this version. Nothing was changed."),
			IngestClaimOutcome.Busy => new IngestRefusedException(StatusCodes.Status409Conflict, "ingest_in_progress", "Another upload of this is running. Nothing was changed.", claim.RetryAfterSeconds),
			IngestClaimOutcome.PathConflict or IngestClaimOutcome.StemClash => new IngestRefusedException(StatusCodes.Status409Conflict, "path_owned", "A file this would write belongs to another document or session. Nothing was changed."),
			IngestClaimOutcome.Shrunk => new IngestRefusedException(StatusCodes.Status409Conflict, "session_shrank", "This upload has fewer messages than the one stored, so it may be a cut copy. Nothing was changed. Send allowShrink=true if the session really did shrink."),
			_ => null,
		};
		if (refusal is not null)
		{
			throw refusal;
		}
	}

	public static IngestRefusedException TooLarge(string detail)
	{
		return new IngestRefusedException(StatusCodes.Status413PayloadTooLarge, "document_too_large", detail);
	}

	/// <summary>Stops reading a body at a deadline, so a client sending a byte at a time cannot hold a slot for days.</summary>
	public static async Task<T> WithinDeadlineAsync<T>(TimeSpan deadline, Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
	{
		using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timer.CancelAfter(deadline);
		try
		{
			return await read(timer.Token);
		}
		catch (OperationCanceledException) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			throw new IngestRefusedException(StatusCodes.Status408RequestTimeout, "upload_too_slow", "The request body did not arrive in time. Nothing was changed.");
		}
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
		ArgumentNullException.ThrowIfNull(context);
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
		ArgumentNullException.ThrowIfNull(context);
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

		foreach (string collection in policy.AmbiguousSettings)
		{
			LogRedactionSettingAmbiguous(logger, collection);
		}
	}

	[LoggerMessage(EventId = 3300, Level = LogLevel.Warning, Message = "REDACTION IS OFF for collection {Collection} (Memoria:Ingest:Collections:{Collection}:Redaction = Disabled). Text ingested into it is stored as it came, credentials included, and search results from it can reach cloud models.")]
	private static partial void LogRedactionDisabled(ILogger logger, string collection);

	[LoggerMessage(EventId = 3301, Level = LogLevel.Warning, Message = "Memoria:Ingest:Collections:{Collection}:Redaction is not exactly Disabled, so collection {Collection} stays redacted. Remove the setting if that is what you meant.")]
	private static partial void LogRedactionSettingIgnored(ILogger logger, string collection);

	[LoggerMessage(EventId = 3302, Level = LogLevel.Warning, Message = "Memoria:Ingest:Collections names {Collection} in more than one spelling of its case across configuration sources. Configuration keys ignore case, so which spelling a setting landed on is an accident. Every spelling stays redacted until only one remains.")]
	private static partial void LogRedactionSettingAmbiguous(ILogger logger, string collection);

	public static IServiceCollection AddMemoriaIngest(this IServiceCollection services)
	{
		services.AddSingleton<IIngestLedger, SqliteIngestLedger>();
		services.AddSingleton<IngestGates>();
		services.AddSingleton<IRedactor, Redactor>();
		services.AddSingleton<IngestRedactionPolicy>();
		services.AddSingleton<CollectionReindexer>();
		services.AddScoped<DocumentIngestor>();
		services.AddScoped<SessionIngestor>();
		return services;
	}
}
