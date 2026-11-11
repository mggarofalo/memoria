using System.Collections.Concurrent;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Core.Redaction;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
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
	IReadOnlyList<string> Paths,
	bool Restored = false);

/// <summary>
/// Which document a read found, for an operator working out what happened. Only in the table, never the log. Caller text is kept only once it has resolved to a real
/// document: nothing can tell a credential by its look, so a reference that found nothing is recorded as a placeholder.
/// </summary>
public sealed record AuditRead(string Reference, bool Found);

/// <summary>
/// Keeps the API alive under its memory limit with plain counts: past a size, a request holds a place until it ends, and past the limit it gets an honest 503, as a
/// runtime out of memory can kill the process outright. Transcripts and documents count separately, and small ones never get this far, so neither turns them away.
/// </summary>
public sealed class LargeUploads(IOptions<MemoriaOptions> options)
{
	public const long LargeBytes = 8L * 1024 * 1024;

	private const int RetryAfterSeconds = 30;

	public const long LargeDocumentBytes = 256L * 1024;

	private readonly SemaphoreSlim _transcripts = new(options.Value.Ingest.MaxConcurrentLargeUploads, options.Value.Ingest.MaxConcurrentLargeUploads);
	private readonly SemaphoreSlim _documents = new(options.Value.Ingest.MaxConcurrentLargeDocuments, options.Value.Ingest.MaxConcurrentLargeDocuments);

	/// <summary>A transcript's body: a declared length over the line takes the place before anything is read, and otherwise the byte that crosses it does.</summary>
	public Stream Watch(Stream body, long? declaredLength)
	{
		return Watched(body, declaredLength, _transcripts, LargeBytes);
	}

	/// <summary>The same for a document. Hold it until the request ends, as a document's text stays in memory while it waits to be redacted and written.</summary>
	public Stream WatchDocument(Stream body, long? declaredLength)
	{
		return Watched(body, declaredLength, _documents, LargeDocumentBytes);
	}

	private static WatchedBody Watched(Stream body, long? declaredLength, SemaphoreSlim places, long line)
	{
		WatchedBody watched = new(body, places, line);
		watched.Note(declaredLength ?? 0);
		return watched;
	}

	private sealed class WatchedBody(Stream inner, SemaphoreSlim places, long line) : Stream
	{
		private long _seen;
		private bool _holding;

		public void Note(long total)
		{
			_seen = Math.Max(_seen, total);
			if (_seen > line && !_holding)
			{
				_holding = places.Wait(0) ? true : throw new IngestRefusedException(StatusCodes.Status503ServiceUnavailable, "ingest_busy", "Too many large uploads are running. Try again shortly.", RetryAfterSeconds);
			}
		}

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => throw new NotSupportedException();

		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override int Read(byte[] buffer, int offset, int count)
		{
			int read = inner.Read(buffer, offset, count);
			Note(_seen + read);
			return read;
		}

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			int read = await inner.ReadAsync(buffer, cancellationToken);
			Note(_seen + read);
			return read;
		}

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		{
			return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
		}

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing && _holding)
			{
				_holding = false;
				places.Release();
			}

			// Stryker disable once Statement : Stream.Dispose(bool) is empty in the base class, so calling it does nothing.
			base.Dispose(disposing);
		}
	}
}

/// <summary>
/// One lock per path, for the single API instance this design assumes. A write holds it until the sidecar answers. A delete waits for it, so it cannot miss a write in
/// flight, and an API crash releases it along with the connections its writes were on, which makes those writes drop themselves in the sidecar.
/// </summary>
public sealed class IngestPathLocks
{
	private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

	/// <summary>Null when the lock did not come free in time.</summary>
	public async Task<IDisposable?> TryHoldAsync(string collection, string path, TimeSpan wait, CancellationToken cancellationToken)
	{
		string key = collection + "\0" + path;
		Entry entry;
		lock (_entries)
		{
			entry = _entries.TryGetValue(key, out Entry? known) ? known : _entries[key] = new Entry();
			entry.Users++;
		}

		bool taken = false;
		try
		{
			taken = await entry.Gate.WaitAsync(wait, cancellationToken);
			return taken ? new Held(this, key, entry) : null;
		}
		finally
		{
			if (!taken)
			{
				Leave(key, entry);
			}
		}
	}

	private void Leave(string key, Entry entry)
	{
		lock (_entries)
		{
			if (--entry.Users == 0)
			{
				_entries.Remove(key);
			}
		}
	}

	private sealed class Entry
	{
		public SemaphoreSlim Gate { get; } = new(1, 1);

		public int Users { get; set; }
	}

	private sealed class Held(IngestPathLocks locks, string key, Entry entry) : IDisposable
	{
		public void Dispose()
		{
			entry.Gate.Release();
			locks.Leave(key, entry);
		}
	}
}

/// <summary>
/// Which collections ingest may touch. An operator names one in configuration, and it opens only if its root is empty the first time ingest uses it. From then on everything
/// inside is Memoria's, which is why nothing else in ingest asks who a file belongs to. The record is in the control database: the sidecar keeps none.
/// </summary>
public sealed class IngestCollections(IIngestLedger ledger, IOptions<MemoriaOptions> options, TimeProvider timeProvider)
{
	private readonly SemaphoreSlim _opening = new(1, 1);

	public async Task RequireOpenAsync(string collection, IMemoriaQmdClient sidecar, CancellationToken cancellationToken)
	{
		if (!options.Value.Ingest.Collections.TryGetValue(collection, out IngestCollectionOptions? settings) || !settings.Enabled)
		{
			throw new IngestRefusedException(StatusCodes.Status409Conflict, "ingest_not_enabled", $"Collection {collection} is not open to ingest. An operator enables it with Memoria:Ingest:Collections:{collection}:Enabled, and its directory must be empty when ingest first uses it.");
		}

		if (await ledger.IsCollectionOpenAsync(collection, cancellationToken))
		{
			// Stryker disable once Statement : without it, OpenAsync asks the ledger again and returns at once, as the ledger never closes a collection it opened. Only the wait for the opening lock is added.
			return;
		}

		await _opening.WaitAsync(cancellationToken);
		try
		{
			await OpenAsync(collection, sidecar, cancellationToken);
		}
		finally
		{
			_opening.Release();
		}
	}

	private async Task OpenAsync(string collection, IMemoriaQmdClient sidecar, CancellationToken cancellationToken)
	{
		if (await ledger.IsCollectionOpenAsync(collection, cancellationToken))
		{
			return;
		}

		SidecarCollectionEmptyResponse answer = await sidecar.IsCollectionEmptyAsync(collection, cancellationToken);
		if (!answer.Empty)
		{
			throw new IngestRefusedException(StatusCodes.Status409Conflict, "collection_not_empty", $"Collection {collection} has files in its directory, or its directory does not exist, so it cannot be opened to ingest. Ingest owns everything in a collection it writes to, so it must start empty, and its volume must be mounted.");
		}

		await ledger.OpenCollectionAsync(collection, timeProvider.GetUtcNow(), cancellationToken);
	}
}

/// <summary>Bounds the expensive work, which reading a request does not hold up.</summary>
public sealed class IngestGates(IOptions<MemoriaOptions> options)
{
	private const int BusyRetrySeconds = 5;

	private static readonly TimeSpan WorkWait = TimeSpan.FromSeconds(30);

	private const long SmallWorkBytes = 1024 * 1024;

	private const long LargeWorkBytes = 32L * 1024 * 1024;

	private readonly SemaphoreSlim _smallWork = new(2, 2);
	private readonly SemaphoreSlim _largeWork = new(1, 1);
	private readonly SemaphoreSlim _work = new(options.Value.Ingest.MaxConcurrentRenders, options.Value.Ingest.MaxConcurrentRenders);

	/// <summary>Runs redaction and rendering, which is what takes the CPU and memory. A small job takes a moment and little memory, so it has a lane of its own that large uploads cannot keep waiting.</summary>
	public async Task<T> WorkAsync<T>(long bytes, Func<T> work, CancellationToken cancellationToken)
	{
		SemaphoreSlim lane = bytes <= SmallWorkBytes ? _smallWork : _work;
		await AcquireAsync(lane, cancellationToken);
		try
		{
			return bytes < LargeWorkBytes ? work() : await LargeWorkAsync(work, cancellationToken);
		}
		finally
		{
			lane.Release();
		}
	}

	/// <summary>
	/// A large render takes several times its text in memory, so only one runs at a time. It asks for one piece as long as all that text, and reading the body has
	/// left the large object heap in holes, which the runtime does not reliably compact first. Under a memory limit that request could fail with plenty free in total.
	/// </summary>
	private async Task<T> LargeWorkAsync<T>(Func<T> work, CancellationToken cancellationToken)
	{
		await AcquireAsync(_largeWork, cancellationToken);
		try
		{
			GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
			GC.Collect();
			return work();
		}
		finally
		{
			_largeWork.Release();
		}
	}

	private static async Task AcquireAsync(SemaphoreSlim slots, CancellationToken cancellationToken)
	{
		if (!await slots.WaitAsync(WorkWait, cancellationToken))
		{
			throw new IngestRefusedException(StatusCodes.Status503ServiceUnavailable, "ingest_busy", "The server is busy redacting. Try again shortly.", BusyRetrySeconds);
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

	private const int UnconfirmedRetrySeconds = 5;

	/// <summary>A write waits its turn however long that is, as the holder's own time is bounded by the sidecar timeout.</summary>
	public static readonly TimeSpan NoLimit = Timeout.InfiniteTimeSpan;

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
			IngestClaimOutcome.Shrunk => Shrank(),
			IngestClaimOutcome.LostContent => LostContent(),
			_ => null,
		};
		if (refusal is not null)
		{
			throw refusal;
		}
	}

	public static IngestRefusedException Shrank()
	{
		return new IngestRefusedException(StatusCodes.Status409Conflict, "session_shrank", "This upload has fewer messages than what is stored, or a migrated document it would replace does not say how many it holds, so it may be a cut copy. Nothing was changed. If this copy really is the whole session, send it again with allowShrink=true, and it replaces what is stored.");
	}

	public static IngestRefusedException PathOwnedUntilFolderIsKnown()
	{
		return new IngestRefusedException(StatusCodes.Status409Conflict, "path_owned", "A file this would write belongs to another document or session, and this upload did not show it is the copy in its own project folder, so it replaces no export. Nothing was changed. If that file is this session's export, send projectDirectory, and the transcript's first cwd must match it.");
	}

	public static IngestRefusedException RenderedShrank()
	{
		return new IngestRefusedException(StatusCodes.Status409Conflict, "session_shrank", "This document says it holds fewer messages than the copy stored at its path, so it may be a cut copy. Nothing was changed. If it really is shorter, delete the path and send it again dated after the delete.");
	}

	public static IngestRefusedException LostContent()
	{
		return new IngestRefusedException(StatusCodes.Status409Conflict, "lost_content", "This upload's messages do not start with the ones stored, so storing it would replace text it does not have. Nothing was changed. Send a whole copy, or allowShrink=true if the history really did change.");
	}

	public static IngestRefusedException DeletedDuringUpload()
	{
		return new IngestRefusedException(StatusCodes.Status409Conflict, "deleted_during_upload", "A delete took this over while it was being written. It was not stored.");
	}

	/// <summary>What was asked is not confirmed done, and asking again is safe.</summary>
	public static IngestRefusedException DeleteUnconfirmed()
	{
		return new IngestRefusedException(StatusCodes.Status503ServiceUnavailable, "delete_unconfirmed", "This delete could not be confirmed, so the file may still be there. Repeat the request: it is safe to.", UnconfirmedRetrySeconds);
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

/// <summary>
/// Digests of content, kept so an upload or a tombstone can be compared with what was stored. Keyed with a secret outside the database, because
/// a plain hash would let anyone holding the database confirm a guessed text, including a secret no rule matched. A previous key still
/// matches, so rotating refuses nothing. New writes use the current key.
/// </summary>
public sealed class IngestDigests(IOptions<MemoriaOptions> options)
{
	public string Of(string value)
	{
		return Keyed(Current(), value);
	}

	public bool Matches(string stored, string value)
	{
		ArgumentNullException.ThrowIfNull(stored);
		return options.Value.Ingest.PreviousDigestKeys.Prepend(Current()).Any(key => Keyed(key, value) == stored);
	}

	/// <summary>An empty key would make every digest confirmable. Ingest cannot run without API keys, and with them the host does not start without a key, so reaching this is a bug.</summary>
	private string Current()
	{
		return string.IsNullOrEmpty(options.Value.Ingest.DigestKey) ? throw new InvalidOperationException("Memoria:Ingest:DigestKey is not set.") : options.Value.Ingest.DigestKey;
	}

	private static string Keyed(string key, string value)
	{
		return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value)));
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
	/// Raises the server's own limit, which is 30 MB unless told otherwise. Call it before anything can refuse the request.
	/// <para>
	/// The server's limit is set past <paramref name="limit"/>, by as much again, and <see cref="Open"/> enforces the limit itself.
	/// A body that runs past the server's limit is cut off with a reset, which can reach the caller before the answer does: a
	/// caller still sending, as one that sends the whole body before reading, sees a broken connection, not the 413 and its code.
	/// The room past the limit is what <see cref="DiscardRestAsync"/> reads into before the 413 is written. Nothing past the
	/// limit is kept: it is read only to be thrown away.
	/// </para>
	/// </summary>
	public static void AllowUpTo(HttpContext context, long limit)
	{
		ArgumentNullException.ThrowIfNull(context);
		if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
		{
			feature.MaxRequestBodySize = limit > long.MaxValue / 2 ? long.MaxValue : limit * 2;
		}
	}

	/// <summary>Lets the server cut off a body arriving slower than this, however long it would take, while a slow link sending steadily gets all the time it needs.</summary>
	public static void RequireRate(HttpContext context, int bytesPerSecond)
	{
		ArgumentNullException.ThrowIfNull(context);
		if (context.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } feature)
		{
			feature.MinDataRate = new MinDataRate(bytesPerSecond, RateGracePeriod);
		}
	}

	private static readonly TimeSpan RateGracePeriod = TimeSpan.FromSeconds(10);

	/// <summary>The longest a refused body is read for, so a slow sender holds the request no longer than this.</summary>
	public static readonly TimeSpan DiscardDeadline = TimeSpan.FromMinutes(2);

	/// <summary>
	/// Reads and throws away what is left of a body refused as too large, before the 413 is written, so a caller still sending
	/// reaches the end of its body and then reads the answer. Kestrel's own reading of the rest, after an answer, stops after a
	/// few seconds and resets the connection, which a slower link does not outlast. This stops at the server's limit, which
	/// <see cref="AllowUpTo"/> set to twice the route's, at <see cref="DiscardDeadline"/>, at the request's rate floor, or when
	/// the request is aborted, and then the answer goes as it would have. A declared length past the server's limit is not
	/// read at all, as reading it could only end in a reset, and nor is a body the caller sends only once told to continue.
	/// </summary>
	public static async Task DiscardRestAsync(HttpContext context, TimeSpan deadline, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		// With no limit of the server's own to stop at, as under the test server, or a caller waiting to be told to send, nothing is read.
		if (context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize is not long allowed
			|| context.Request.ContentLength > allowed
			|| context.Request.Headers.Expect.Count > 0)
		{
			return;
		}

		using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		stop.CancelAfter(deadline);
		byte[] buffer = new byte[64 * 1024];
		try
		{
			while (await context.Request.Body.ReadAsync(buffer, stop.Token) > 0)
			{
			}
		}
		catch (Exception exception) when (exception is OperationCanceledException or Microsoft.AspNetCore.Http.BadHttpRequestException or IOException)
		{
			// Past the server's limit, too slow, too long, or gone: the answer goes as it is, and the connection may not survive it.
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
		services.AddSingleton<LargeUploads>();
		services.AddSingleton<IngestPathLocks>();
		services.AddSingleton<IngestCollections>();
		services.AddSingleton<IngestDigests>();
		services.AddSingleton<IRedactor, Redactor>();
		services.AddSingleton<IngestRedactionPolicy>();
		services.AddSingleton<CollectionReindexer>();
		services.AddScoped<DocumentIngestor>();
		services.AddScoped<SessionIngestor>();
		return services;
	}
}
