using System.Net;
using System.Text;
using Memoria.Api.Auth;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Core.Contracts;
using Memoria.Core.Redaction;
using Memoria.Core.Sessions;
using Memoria.Core.Sidecar;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Ingest;

/// <summary>What a caller says about a raw transcript.</summary>
public sealed record SessionIngestInput(string Collection, SessionFormat Format, string SessionId, DateTimeOffset Updated, string? ProjectDirectory, bool AllowShrink = false, bool Restore = false);

/// <summary>
/// Renders a raw transcript and stores its parts. Parts are separate files, so an upload can stop halfway. What keeps that honest:
/// the ledger holds <c>partial</c> until the end, and an upload that lost content, or had none, is never called complete. Part paths are
/// deterministic, so sending the same upload again overwrites and finishes it. An upload whose messages do not start with the ones stored is refused,
/// as a cut or damaged copy looks just like that. A path stops being part of a session only through the ledger, so an upload removes only what it wrote before.
/// </summary>
public sealed class SessionIngestor(
	IMemoriaQmdClient sidecar,
	IIngestLedger ledger,
	IngestRedactionPolicy redaction,
	IngestGates gates,
	IngestDigests digests,
	IngestPathLocks locks,
	IngestCollections collections,
	CollectionReindexer reindexer,
	ICallerScope scope,
	IRequestAudit audit,
	IOptions<MemoriaOptions> options,
	TimeProvider timeProvider)
{
	public async Task<SessionIngestResult> IngestAsync(SessionIngestInput input, Stream payload, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(input);
		IngestRules.RequireCollection(scope, input.Collection);
		audit.SetResult([input.Collection], 0);
		await collections.RequireOpenAsync(input.Collection, sidecar, cancellationToken);

		RedactedSession session = await RenderAsync(input, payload, cancellationToken);
		if (session.NamesAnotherSession)
		{
			throw new IngestRefusedException(StatusCodes.Status400BadRequest, "session_id_mismatch", "The transcript's session_meta names a different session from sessionId. Nothing was changed.");
		}

		if (session.Documents.Count == 0)
		{
			return Report(input, session, [], SupersededRun.None, null, IngestStateName.Partial);
		}

		string identity = IngestIdentity.For("session", input.Collection, session.PathIdentity);
		(session, IngestClaim claim) = await ClaimAsync(input, session, identity, cancellationToken);
		return await WriteAsync(input, session, identity, claim, cancellationToken);
	}

	public async Task<SessionIngestStatus> StatusAsync(SessionIngestInput input, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(input);
		IngestRules.RequireCollection(scope, input.Collection);
		IReadOnlyList<IngestRecord> records = await ledger.FindByLookupAsync(LookupOf(input), cancellationToken);
		DateTimeOffset now = timeProvider.GetUtcNow();
		return new SessionIngestStatus(
			input.Collection,
			input.SessionId,
			WorstState(records.Select(record => ledger.EffectiveState(record, now))),
			records.Count == 0 ? null : records.Max(static record => record.Version),
			[.. records.SelectMany(static record => record.Paths)]);
	}

	/// <summary>Reading can be slow and takes no slot. Rendering takes the CPU and memory, so it has its own bound and starts only once the body is in.</summary>
	private async Task<RedactedSession> RenderAsync(SessionIngestInput input, Stream payload, CancellationToken cancellationToken)
	{
		SessionSource source = new(input.Format, input.SessionId) { ProjectDirectory = input.ProjectDirectory };
		SessionRenderOptions renderOptions = RenderOptions(options.Value.Ingest.MaxDocumentBytes);
		RedactionDisabledCollection? disabled = redaction.For(input.Collection);
		ReadSessionTranscript read = await IngestRules.WithinDeadlineAsync(
			options.Value.Ingest.SessionUploadDeadline,
			token => disabled is null
				? SessionRenderer.ReadAsync(payload, source, renderOptions, token)
				: SessionRenderer.ReadWithoutRedactionForDisabledCollectionAsync(payload, source, disabled, renderOptions, token),
			cancellationToken);
		return await gates.WorkAsync(read.BytesRead, read.Render, cancellationToken);
	}

	/// <summary>A part of many messages is measured in characters, of up to four bytes each, so a low document limit has to bring it down too.</summary>
	private static SessionRenderOptions RenderOptions(int maxDocumentBytes)
	{
		int messageBytes = maxDocumentBytes - MessageHeadroomBytes;
		return new SessionRenderOptions
		{
			MaxMessageBytes = messageBytes,
			MaxPartChars = Math.Min(SessionRenderOptions.Default.MaxPartChars, messageBytes / 4),
		};
	}

	private async Task<(RedactedSession Session, IngestClaim Claim)> ClaimAsync(SessionIngestInput input, RedactedSession session, string identity, CancellationToken cancellationToken)
	{
		IngestClaim claim = await ledger.ClaimAsync(ClaimRequest(input, session, identity), cancellationToken);
		while (claim.Outcome == IngestClaimOutcome.StemClash && session.WithDistinctPaths() is { } longer)
		{
			session = longer;
			claim = await ledger.ClaimAsync(ClaimRequest(input, session, identity), cancellationToken);
		}

		IngestRules.RequireClaim(claim);
		return (session, claim);
	}

	private IngestClaimRequest ClaimRequest(SessionIngestInput input, RedactedSession session, string identity)
	{
		return new IngestClaimRequest(
			identity,
			input.Collection,
			input.Updated,
			timeProvider.GetUtcNow(),
			[.. session.Documents.Select(static document => document.Path)],
			LookupOf(input),
			session.PathStem,
			session.Report.Messages,
			input.AllowShrink,
			Restore: input.Restore,
			// A copy that lost lines has a chain that is no later copy's start, so it records none, and only the count applies to what follows it.
			MessagesDigest: session.Report.HasHoles ? null : digests.Of(session.MessageDigestAt(session.Report.Messages) ?? ""),
			StartsWithStored: (count, stored) => session.MessageDigestAt(count) is { } chain && digests.Matches(stored, chain))
		{
			RenderedLookup = IngestIdentity.ForRenderedSession(input.Collection, input.SessionId),
		};
	}

	private async Task<SessionIngestResult> WriteAsync(SessionIngestInput input, RedactedSession session, string identity, IngestClaim claim, CancellationToken cancellationToken)
	{
		List<SessionPartResult> parts = [];
		SupersededRun superseded = SupersededRun.None;
		string? jobId = null;
		string state = IngestStateName.Partial;
		try
		{
			jobId = await WritePartsAsync(input.Collection, identity, claim, session, parts, cancellationToken);
			if (AllWritten(parts))
			{
				superseded = await RemoveSupersededAsync(input.Collection, session, claim.PreviousPaths, cancellationToken);
				state = superseded.Finished ? IngestStateName.Complete : IngestStateName.Partial;
			}
		}
		finally
		{
			bool landed = parts.Exists(static part => part.State == SessionPartState.Written);
			if (!await ledger.FinishAsync(identity, claim.LeaseToken!, input.Collection, state, KnownPaths(session, claim, parts, superseded, state), landed ? ClaimRequest(input, session, identity) : null))
			{
				state = IngestStateName.Partial;
				await MarkDeletedPartsAsync(identity, parts);
			}
		}

		return Report(input, session, parts, superseded, jobId, state);
	}

	/// <summary>A delete took the lease during this upload, so what this request wrote or was about to may be gone. It says so rather than calling those parts written.</summary>
	private async Task MarkDeletedPartsAsync(string identity, List<SessionPartResult> parts)
	{
		IngestRecord? row = await ledger.FindAsync(identity, CancellationToken.None);
		IReadOnlyList<string> deleted = row?.DeletedPaths ?? [];
		for (int i = 0; i < parts.Count; i++)
		{
			if (deleted.Contains(parts[i].Path, StringComparer.Ordinal))
			{
				parts[i] = new SessionPartResult(parts[i].Part, parts[i].Path, SessionPartState.Deleted, "part_deleted", "This part was deleted, and stays deleted however often the session is sent.");
			}
		}
	}

	private static string LookupOf(SessionIngestInput input)
	{
		return IngestIdentity.For("session-name", input.Collection, input.Format.ToString(), input.SessionId);
	}

	private static string WorstState(IEnumerable<string> states)
	{
		string[] all = [.. states];
		foreach (string state in new[] { IngestStateName.Writing, IngestStateName.Partial, IngestStateName.Deleted, IngestStateName.Complete })
		{
			if (all.Contains(state))
			{
				return state;
			}
		}

		return IngestStateName.None;
	}

	/// <summary>Returns the last job id. A part the backend stopped answering for ends the upload here, while a part refused on its own merits does not stop the rest. Losing the lease stops it too, as a delete has taken over.</summary>
	private async Task<string?> WritePartsAsync(string collection, string identity, IngestClaim claim, RedactedSession session, List<SessionPartResult> parts, CancellationToken cancellationToken)
	{
		string? jobId = null;
		bool stopped = false;
		foreach (RedactedSessionDocument document in session.Documents)
		{
			if (stopped)
			{
				parts.Add(new SessionPartResult(document.Part, document.Path, SessionPartState.NotAttempted, Retryable: true));
				continue;
			}

			(SessionPartResult result, string? job) = await WritePartAsync(collection, identity, document, claim, cancellationToken);
			parts.Add(result);
			jobId = job ?? jobId;
			stopped = result is { State: SessionPartState.NotAttempted } or { State: SessionPartState.Failed, Retryable: true };
		}

		return jobId;
	}

	/// <summary>
	/// The write runs to its answer even if the caller goes away, as the sidecar drops it only if this process dies or gives up on it. The path's lock is held for all of that,
	/// and the lease is checked under it, so a delete either comes first and stops this write, or waits for it.
	/// </summary>
	private async Task<(SessionPartResult Result, string? JobId)> WritePartAsync(string collection, string identity, RedactedSessionDocument document, IngestClaim claim, CancellationToken cancellationToken)
	{
		if (claim.DeletedPaths.Contains(document.Path, StringComparer.Ordinal))
		{
			return (DeletedPart(document), null);
		}

		int limit = options.Value.Ingest.MaxDocumentBytes;
		if (Encoding.UTF8.GetByteCount(document.Content.Text) > limit)
		{
			return (new SessionPartResult(document.Part, document.Path, SessionPartState.Failed, "document_too_large", $"This part is over the {limit}-byte document limit."), null);
		}

		using IDisposable held = (await locks.TryHoldAsync(collection, document.Path, IngestRules.NoLimit, cancellationToken))!;
		if (!await ledger.BeginWriteAsync(identity, claim.LeaseToken!, collection, document.Path, timeProvider.GetUtcNow(), cancellationToken))
		{
			return (new SessionPartResult(document.Part, document.Path, SessionPartState.NotAttempted, Retryable: true), null);
		}

		try
		{
			SidecarIngestResponse response = await sidecar.IngestAsync(new SidecarIngestRequest(collection, document.Path, document.Content), CancellationToken.None);
			return (new SessionPartResult(document.Part, document.Path, SessionPartState.Written), response.Job.Id);
		}
		catch (SidecarException exception) when (IsThisPartsFailure(exception))
		{
			return (FailedPart(document, exception), null);
		}
	}

	/// <summary>Sending the session again does not bring this part back: only an operator can, with <c>restoreDeleted</c>.</summary>
	private static SessionPartResult DeletedPart(RedactedSessionDocument document)
	{
		return new SessionPartResult(document.Part, document.Path, SessionPartState.Deleted, "part_deleted", "This part was deleted, and stays deleted however often the session is sent.");
	}

	/// <summary>A collection that does not exist or a sidecar that rejects Memoria's secret is no fault of one part, and is the whole request's answer.</summary>
	private static bool IsThisPartsFailure(SidecarException exception)
	{
		return exception.Code != UnknownCollection && exception.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
	}

	private SessionPartResult FailedPart(RedactedSessionDocument document, SidecarException exception)
	{
		SidecarFailureView view = SidecarDisclosure.For(exception, scope);
		int? retryAfter = exception.IsTransient ? MemoriaExceptionHandler.RetryAfterSeconds(exception) ?? DefaultRetryAfterSeconds : null;
		return new SessionPartResult(document.Part, document.Path, SessionPartState.Failed, view.Code, view.Message, exception.IsTransient, retryAfter);
	}

	private static bool AllWritten(List<SessionPartResult> parts)
	{
		return parts.TrueForAll(static part => part.State == SessionPartState.Written);
	}

	/// <summary>
	/// A session that now renders to fewer parts leaves old ones behind, which only a delete removes. The ledger is the only source of what
	/// to delete, so nothing it does not own is touched. An upload that lost content keeps them, and the session is not called complete.
	/// </summary>
	private async Task<SupersededRun> RemoveSupersededAsync(string collection, RedactedSession session, IReadOnlyList<string> previous, CancellationToken cancellationToken)
	{
		string[] candidates = [.. previous.Where(path => !session.Documents.Any(document => document.Path == path))];
		if (session.Report.HasLostContent)
		{
			return new SupersededRun([], [], candidates, null, ReindexState.NotNeeded, false);
		}

		List<string> deleted = [];
		List<string> failed = [];
		foreach (string path in candidates)
		{
			(await DeletedAsync(collection, path, cancellationToken) ? deleted : failed).Add(path);
		}

		ReindexOutcome reindex = await reindexer.RequestAsync(collection, deleted.Count > 0, cancellationToken);
		return new SupersededRun(deleted, failed, [], reindex.JobId, reindex.State, failed.Count == 0 && reindex.State != ReindexState.Failed);
	}

	private async Task<bool> DeletedAsync(string collection, string path, CancellationToken cancellationToken)
	{
		try
		{
			using IDisposable held = (await locks.TryHoldAsync(collection, path, IngestRules.NoLimit, cancellationToken))!;
			await sidecar.DeleteDocumentAsync(collection, path, cancellationToken);
			return true;
		}
		catch (SidecarException)
		{
			return false;
		}
	}

	/// <summary>A finished upload knows exactly its own parts. An unfinished one keeps every path it may have left on disk, so the retry can still find them.</summary>
	private static string[] KnownPaths(RedactedSession session, IngestClaim claim, List<SessionPartResult> parts, SupersededRun superseded, string state)
	{
		if (state == IngestStateName.Complete)
		{
			return [.. session.Documents.Select(static document => document.Path)];
		}

		IEnumerable<string> written = parts.Where(static part => part.State is SessionPartState.Written or SessionPartState.Failed).Select(static part => part.Path);
		return [.. claim.PreviousPaths.Except(superseded.Deleted, StringComparer.Ordinal).Concat(written).Distinct(StringComparer.Ordinal)];
	}

	private SessionIngestResult Report(SessionIngestInput input, RedactedSession session, List<SessionPartResult> parts, SupersededRun superseded, string? jobId, string state)
	{
		bool complete = state == IngestStateName.Complete;
		int written = parts.Count(static part => part.State == SessionPartState.Written);
		IngestRefusal[] refusals = [.. session.Refusals.Take(IngestRules.RefusalsReturned).Select(static refusal => new IngestRefusal(refusal.Part, refusal.Field, refusal.Rule))];
		string redactionName = IngestRedactionPolicy.NameOf(redaction.For(input.Collection));
		string[] paths = [.. parts.Select(static part => part.Path).Concat(superseded.Deleted).Take(IngestRules.MaxAuditPaths)];
		audit.SetResult([input.Collection], written);
		audit.SetTarget(new AuditIngest("session", redactionName, complete, written, parts.Count - written, superseded.Deleted.Count, session.CountsByRule.Values.Sum(), session.Refusals.Count, paths, input.Restore));
		return new SessionIngestResult(
			input.Collection,
			input.SessionId,
			input.Format == SessionFormat.Codex ? IngestEndpointNames.Codex : IngestEndpointNames.Claude,
			complete,
			!complete && IsRetryable(session, parts, superseded),
			parts,
			new SessionSupersededResult(superseded.Deleted, superseded.Failed, superseded.Kept),
			redactionName,
			session.CountsByRule,
			refusals,
			session.Refusals.Count,
			Summarize(session.Report),
			jobId,
			superseded.ReindexJobId,
			superseded.ReindexState);
	}

	/// <summary>Sending it again helps only when what stopped it was the backend. A part refused for good or deleted, content the copy does not have, or a session with no messages stays that way.</summary>
	private static bool IsRetryable(RedactedSession session, List<SessionPartResult> parts, SupersededRun superseded)
	{
		return parts.Count > 0
			&& !session.Report.HasLostContent
			&& superseded.Kept.Count == 0
			&& !parts.Exists(static part => part.State == SessionPartState.Deleted || part is { State: SessionPartState.Failed, Retryable: false });
	}

	private static SessionReadSummary Summarize(SessionReadReport report)
	{
		return new SessionReadSummary(report.BytesRead, report.Lines, report.Messages, report.MalformedLines, report.OversizedLines, report.UnterminatedLines, report.MessagesOverLimit, report.HasLostContent);
	}

	private const string UnknownCollection = "unknown_collection";

	/// <summary>Room in a document for the frontmatter, title and headings around the longest message.</summary>
	private const int MessageHeadroomBytes = 16 * 1024;

	private const int DefaultRetryAfterSeconds = 5;

	private sealed record SupersededRun(IReadOnlyList<string> Deleted, IReadOnlyList<string> Failed, IReadOnlyList<string> Kept, string? ReindexJobId, string ReindexState, bool Finished)
	{
		public static readonly SupersededRun None = new([], [], [], null, Memoria.Core.Contracts.ReindexState.NotNeeded, false);
	}
}

/// <summary>The names the session route uses for formats.</summary>
public static class IngestEndpointNames
{
	public const string Claude = "claude";

	public const string Codex = "codex";
}
