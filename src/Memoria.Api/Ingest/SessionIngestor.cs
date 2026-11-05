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
public sealed record SessionIngestInput(string Collection, SessionFormat Format, string SessionId, DateTimeOffset Updated, string? ProjectDirectory, bool AllowShrink = false);

/// <summary>
/// Renders a raw transcript and stores its parts. Parts are separate files, so an upload can stop halfway. What keeps that honest:
/// the ledger holds <c>partial</c> until the end, and an upload that lost content, or had none, is never called complete. Part paths are
/// deterministic, so sending the same upload again overwrites and finishes it. Fewer messages than last time is refused unless the caller says it is
/// intended, as a cut copy looks just like that. Superseded parts go last, only after everything else arrived, and only when
/// the ledger or their own frontmatter says they are this session's: a file name is not enough, as another session can share its start.
/// </summary>
public sealed class SessionIngestor(
	IMemoriaQmdClient sidecar,
	IIngestLedger ledger,
	IngestRedactionPolicy redaction,
	IngestGates gates,
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
		FamilyListing family = await ListFamilyAsync(input.Collection, session, identity, cancellationToken);
		session = family.HasForeignFiles ? session.WithDistinctPaths() : session;
		(session, IngestClaim claim) = await ClaimAsync(input, session, identity, cancellationToken);

		return await WriteAsync(input, session, identity, claim, family, cancellationToken);
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

	/// <summary>Reading can be slow and only takes this request's slot. Rendering takes the CPU and memory, so it has its own bound and starts only once the body is in.</summary>
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
		return await gates.WorkAsync(read.Render, cancellationToken);
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
		if (claim.Outcome == IngestClaimOutcome.StemClash)
		{
			session = session.WithDistinctPaths();
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
			input.AllowShrink);
	}

	private async Task<SessionIngestResult> WriteAsync(SessionIngestInput input, RedactedSession session, string identity, IngestClaim claim, FamilyListing family, CancellationToken cancellationToken)
	{
		List<SessionPartResult> parts = [];
		SupersededRun superseded = SupersededRun.None;
		string? jobId = null;
		string state = IngestStateName.Partial;
		try
		{
			jobId = await WritePartsAsync(input.Collection, identity, claim.LeaseToken!, session, parts, cancellationToken);
			if (AllWritten(parts))
			{
				superseded = await RemoveSupersededAsync(input.Collection, session, claim.PreviousPaths, family, cancellationToken);
				state = superseded.Finished ? IngestStateName.Complete : IngestStateName.Partial;
			}
		}
		finally
		{
			await ledger.FinishAsync(identity, claim.LeaseToken!, input.Collection, state, KnownPaths(session, claim, parts, superseded, state));
		}

		return Report(input, session, parts, superseded, jobId, state);
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

	/// <summary>Returns the last job id. A part the backend stopped answering for ends the upload here, while a part refused on its own merits does not stop the rest. Losing the lease stops it too, as someone else now holds these files.</summary>
	private async Task<string?> WritePartsAsync(string collection, string identity, string leaseToken, RedactedSession session, List<SessionPartResult> parts, CancellationToken cancellationToken)
	{
		string? jobId = null;
		bool stopped = false;
		foreach (RedactedSessionDocument document in session.Documents)
		{
			stopped = stopped || !await ledger.RenewAsync(identity, leaseToken, timeProvider.GetUtcNow(), cancellationToken);
			if (stopped)
			{
				parts.Add(new SessionPartResult(document.Part, document.Path, SessionPartState.NotAttempted, Retryable: true));
				continue;
			}

			(SessionPartResult result, string? job) = await WritePartAsync(collection, document, cancellationToken);
			parts.Add(result);
			jobId = job ?? jobId;
			stopped = result is { State: SessionPartState.Failed, Retryable: true };
		}

		return jobId;
	}

	private async Task<(SessionPartResult Result, string? JobId)> WritePartAsync(string collection, RedactedSessionDocument document, CancellationToken cancellationToken)
	{
		int limit = options.Value.Ingest.MaxDocumentBytes;
		if (Encoding.UTF8.GetByteCount(document.Content.Text) > limit)
		{
			return (new SessionPartResult(document.Part, document.Path, SessionPartState.Failed, "document_too_large", $"This part is over the {limit}-byte document limit."), null);
		}

		try
		{
			SidecarIngestResponse response = await sidecar.IngestAsync(new SidecarIngestRequest(collection, document.Path, document.Content, Overwrite: true), cancellationToken);
			return (new SessionPartResult(document.Part, document.Path, SessionPartState.Written), response.Job.Id);
		}
		catch (SidecarException exception) when (IsThisPartsFailure(exception))
		{
			return (FailedPart(document, exception), null);
		}
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
	/// What is on disk with this session's shape, sorted by whose it really is. A file the ledger assigns elsewhere is someone else's.
	/// A file nobody has claimed, from before there was a ledger, is judged by the full id in its frontmatter, as it was written by the same
	/// renderer. When a file cannot be judged, the listing is incomplete and the session is not called complete.
	/// </summary>
	private async Task<FamilyListing> ListFamilyAsync(string collection, RedactedSession session, string identity, CancellationToken cancellationToken)
	{
		IReadOnlyList<ListedFile>? listed = await ListIndexedAsync(collection, session, cancellationToken);
		if (listed is null)
		{
			return FamilyListing.Unknown;
		}

		IReadOnlyDictionary<string, string> owners = await ledger.OwnersAsync(collection, [.. listed.Select(static file => file.Path)], cancellationToken);
		List<string> mine = [];
		bool foreign = false;
		bool complete = true;
		foreach (ListedFile file in listed)
		{
			bool? isMine = owners.TryGetValue(file.Path, out string? owner)
				? owner == identity
				: await HeadSaysMineAsync(collection, session, file, cancellationToken);
			complete &= isMine is not null;
			foreign |= isMine == false && !owners.ContainsKey(file.Path);
			if (isMine == true)
			{
				mine.Add(file.Path);
			}
		}

		return new FamilyListing(mine, foreign, complete);
	}

	/// <summary>Null when the sidecar could not say.</summary>
	private async Task<bool?> HeadSaysMineAsync(string collection, RedactedSession session, ListedFile file, CancellationToken cancellationToken)
	{
		try
		{
			SidecarDocument document = await sidecar.GetDocumentAsync(file.Reference, new SidecarDocumentQuery(FromLine: 1, MaxLines: HeadLines, Collections: [collection], SimilarFiles: false), cancellationToken);
			return session.HeadNamesThisSession(document.Body ?? "");
		}
		catch (SidecarException)
		{
			return null;
		}
	}

	/// <summary>
	/// A session that shrank leaves old parts behind, which only a delete removes. An upload that lost content may be a cut copy
	/// rather than a smaller session, so its old parts stay and the session is not called complete.
	/// </summary>
	private async Task<SupersededRun> RemoveSupersededAsync(string collection, RedactedSession session, IReadOnlyList<string> previous, FamilyListing family, CancellationToken cancellationToken)
	{
		HashSet<string> current = [.. session.Documents.Select(static document => document.Path)];
		string[] candidates = [.. previous.Concat(family.Mine).Distinct(StringComparer.Ordinal).Where(path => !current.Contains(path))];
		if (session.Report.HasLostContent)
		{
			return new SupersededRun([], [], candidates, null, ReindexState.NotNeeded, false);
		}

		return await DeleteSupersededAsync(collection, candidates, family.Complete, cancellationToken);
	}

	private async Task<SupersededRun> DeleteSupersededAsync(string collection, string[] candidates, bool listed, CancellationToken cancellationToken)
	{
		List<string> deleted = [];
		List<string> failed = [];
		foreach (string path in candidates)
		{
			(await DeletedAsync(collection, path, cancellationToken) ? deleted : failed).Add(path);
		}

		ReindexOutcome reindex = await reindexer.RequestAsync(collection, deleted.Count > 0, cancellationToken);
		bool finished = listed && failed.Count == 0 && reindex.State != ReindexState.Failed;
		return new SupersededRun(deleted, failed, [], reindex.JobId, reindex.State, finished);
	}

	/// <summary>
	/// Null when the sidecar could not say. Then superseded parts may remain, so the session cannot be called complete. A document over the
	/// size limit is listed under skipped even though no body was asked for, and it counts as much as any other.
	/// </summary>
	private async Task<IReadOnlyList<ListedFile>?> ListIndexedAsync(string collection, RedactedSession session, CancellationToken cancellationToken)
	{
		try
		{
			SidecarMultiGetResponse listing = await sidecar.MultiGetAsync(new SidecarMultiGetRequest
			{
				Pattern = session.ExistingPathsGlob,
				Collections = [collection],
				IncludeBody = false,
				SimilarFiles = false,
			}, cancellationToken);
			string prefix = collection + "/";
			IEnumerable<ListedFile> docs = listing.Docs.Select(static doc => new ListedFile(doc.Path, doc.Docid));
			IEnumerable<ListedFile> skipped = listing.Skipped
				.Where(file => file.File.StartsWith(prefix, StringComparison.Ordinal))
				.Select(file => new ListedFile(file.File[prefix.Length..], file.File));
			return listing.Truncated ? null : [.. docs.Concat(skipped).Where(file => session.IsInPathFamily(file.Path))];
		}
		catch (SidecarException)
		{
			return null;
		}
	}

	private async Task<bool> DeletedAsync(string collection, string path, CancellationToken cancellationToken)
	{
		try
		{
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

		IEnumerable<string> written = parts.Where(static part => part.State == SessionPartState.Written).Select(static part => part.Path);
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
		audit.SetTarget(new AuditIngest("session", redactionName, complete, written, parts.Count - written, superseded.Deleted.Count, session.CountsByRule.Values.Sum(), session.Refusals.Count, paths));
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

	/// <summary>Sending it again helps only when what stopped it was the backend. A part refused for good, content the copy does not have, or a session with no messages stays that way.</summary>
	private static bool IsRetryable(RedactedSession session, List<SessionPartResult> parts, SupersededRun superseded)
	{
		return parts.Count > 0
			&& !session.Report.HasLostContent
			&& superseded.Kept.Count == 0
			&& !parts.Exists(static part => part is { State: SessionPartState.Failed, Retryable: false });
	}

	private static SessionReadSummary Summarize(SessionReadReport report)
	{
		return new SessionReadSummary(report.BytesRead, report.Lines, report.Messages, report.MalformedLines, report.OversizedLines, report.UnterminatedLines, report.MessagesOverLimit, report.HasLostContent);
	}

	private const string UnknownCollection = "unknown_collection";

	/// <summary>Room in a document for the frontmatter, title and headings around the longest message.</summary>
	private const int MessageHeadroomBytes = 16 * 1024;

	private const int DefaultRetryAfterSeconds = 5;

	/// <summary>Frontmatter is at most 8 KiB, and the id comes first, so this always reaches it.</summary>
	private const int HeadLines = 64;

	private sealed record ListedFile(string Path, string Reference);

	private sealed record FamilyListing(IReadOnlyList<string> Mine, bool HasForeignFiles, bool Complete)
	{
		public static readonly FamilyListing Unknown = new([], false, false);
	}

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
