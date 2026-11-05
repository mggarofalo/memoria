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
public sealed record SessionIngestInput(string Collection, SessionFormat Format, string SessionId, DateTimeOffset Updated, string? ProjectDirectory);

/// <summary>
/// Renders a raw transcript and stores its parts. Parts are separate files, so an upload can stop halfway. Three
/// things keep that honest. The ledger holds <c>partial</c> until the end, so a half-written session never reads as
/// finished. Part paths are deterministic, so sending the same upload again overwrites and finishes it. And superseded
/// parts go last, only after everything else arrived, so a failed upload never loses text the old parts still held.
/// </summary>
public sealed class SessionIngestor(
	IMemoriaQmdClient sidecar,
	IIngestLedger ledger,
	IngestRedactionPolicy redaction,
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

		SessionSource source = new(input.Format, input.SessionId) { ProjectDirectory = input.ProjectDirectory };
		SessionRenderOptions renderOptions = RenderOptions(options.Value.Ingest.MaxDocumentBytes);
		RedactionDisabledCollection? disabled = redaction.For(input.Collection);
		RedactedSession session = disabled is null
			? await SessionRenderer.RenderAsync(payload, source, renderOptions, cancellationToken)
			: await SessionRenderer.RenderWithoutRedactionForDisabledCollectionAsync(payload, source, disabled, renderOptions, cancellationToken);

		string identity = IdentityOf(input);
		IngestClaim claim = await ledger.ClaimAsync(identity, input.Updated, timeProvider.GetUtcNow(), cancellationToken);
		IngestRules.RequireClaim(claim);

		List<SessionPartResult> parts = [];
		SupersededRun superseded = SupersededRun.None;
		string? jobId = null;
		string state = IngestStateName.Partial;
		try
		{
			jobId = await WritePartsAsync(input.Collection, identity, session, parts, cancellationToken);
			if (AllWritten(parts))
			{
				superseded = await RemoveSupersededAsync(input.Collection, session, claim.PreviousPaths, cancellationToken);
				state = superseded.Finished && !LostMessages(session.Report) ? IngestStateName.Complete : IngestStateName.Partial;
			}
		}
		finally
		{
			await ledger.FinishAsync(identity, state, KnownPaths(session, claim, parts, superseded, state));
		}

		return Report(input, session, parts, superseded, jobId, state);
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

	public async Task<SessionIngestStatus> StatusAsync(SessionIngestInput input, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(input);
		IngestRules.RequireCollection(scope, input.Collection);
		IngestRecord? record = await ledger.FindAsync(IdentityOf(input), cancellationToken);
		return new SessionIngestStatus(input.Collection, input.SessionId, record?.State ?? IngestStateName.None, record?.Version, record?.Paths ?? []);
	}

	internal static string IdentityOf(SessionIngestInput input)
	{
		return IngestIdentity.For("session", input.Collection, input.Format.ToString(), input.SessionId, input.ProjectDirectory);
	}

	/// <summary>Returns the last job id. A part the backend stopped answering for ends the upload here, while a part refused on its own merits does not stop the rest.</summary>
	private async Task<string?> WritePartsAsync(string collection, string identity, RedactedSession session, List<SessionPartResult> parts, CancellationToken cancellationToken)
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

			await ledger.RenewAsync(identity, timeProvider.GetUtcNow(), cancellationToken);
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
		int? retryAfter = exception.IsTransient ? RetryAfterOf(exception) : null;
		return new SessionPartResult(document.Part, document.Path, SessionPartState.Failed, view.Code, view.Message, exception.IsTransient, retryAfter);
	}

	private static int RetryAfterOf(SidecarException exception)
	{
		return exception.RetryAfter is { } after ? Math.Max(1, (int)Math.Ceiling(after.TotalSeconds)) : IngestRules.BusyRetrySeconds;
	}

	/// <summary>Whole messages or lines were left out for size. What is stored is a true prefix, but it is not the session, and sending it again changes nothing. A malformed line is different: live transcripts end in one.</summary>
	private static bool LostMessages(SessionReadReport report)
	{
		return report.MessagesOverLimit > 0 || report.OversizedLines > 0;
	}

	private static bool AllWritten(List<SessionPartResult> parts)
	{
		return parts.TrueForAll(static part => part.State == SessionPartState.Written);
	}

	/// <summary>
	/// A session that shrank leaves old parts behind, which only a delete removes. An upload that lost content or is empty may
	/// be a truncated copy rather than a smaller session, so its old parts stay, and the session is not called complete. Candidates come from the ledger, which knows files
	/// the index has not seen yet, and from the index, which knows files from before there was a ledger.
	/// </summary>
	private async Task<SupersededRun> RemoveSupersededAsync(string collection, RedactedSession session, IReadOnlyList<string> previous, CancellationToken cancellationToken)
	{
		IReadOnlyList<string>? indexed = await ListIndexedAsync(collection, session, cancellationToken);
		string[] candidates = previous.Concat(indexed ?? []).Distinct(StringComparer.Ordinal).Where(session.IsSupersededPath).ToArray();
		if (session.Documents.Count == 0 || session.Report.HasSkippedContent)
		{
			return new SupersededRun([], [], candidates, null, indexed is not null && candidates.Length == 0);
		}

		return await DeleteSupersededAsync(collection, candidates, indexed is not null, cancellationToken);
	}

	private async Task<SupersededRun> DeleteSupersededAsync(string collection, string[] candidates, bool listed, CancellationToken cancellationToken)
	{
		List<string> deleted = [];
		List<string> failed = [];
		foreach (string path in candidates)
		{
			(await DeletedAsync(collection, path, cancellationToken) ? deleted : failed).Add(path);
		}

		string? reindex = deleted.Count > 0 ? await ReindexNowAsync(collection, cancellationToken) : null;
		bool finished = listed && failed.Count == 0 && (deleted.Count == 0 || reindex is not null);
		return new SupersededRun(deleted, failed, [], reindex, finished);
	}

	/// <summary>Null when the sidecar could not say. Then superseded parts may remain, so the session cannot be called complete.</summary>
	private async Task<IReadOnlyList<string>?> ListIndexedAsync(string collection, RedactedSession session, CancellationToken cancellationToken)
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
			return listing.Truncated ? null : [.. listing.Docs.Select(static doc => doc.Path)];
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

	private async Task<string?> ReindexNowAsync(string collection, CancellationToken cancellationToken)
	{
		try
		{
			return (await sidecar.ReindexAsync(new SidecarReindexRequest([collection], Embed: false), cancellationToken)).Id;
		}
		catch (SidecarException)
		{
			return null;
		}
	}

	/// <summary>A finished upload knows exactly its own parts. An unfinished one keeps every path it may have left on disk, so the retry can still find them.</summary>
	private static string[] KnownPaths(RedactedSession session, IngestClaim claim, List<SessionPartResult> parts, SupersededRun superseded, string state)
	{
		IEnumerable<string> current = session.Documents.Select(static document => document.Path);
		if (state == IngestStateName.Complete)
		{
			return [.. current];
		}

		IEnumerable<string> written = parts.Where(static part => part.State == SessionPartState.Written).Select(static part => part.Path);
		return [.. claim.PreviousPaths.Except(superseded.Deleted, StringComparer.Ordinal).Concat(written).Distinct(StringComparer.Ordinal)];
	}

	private SessionIngestResult Report(SessionIngestInput input, RedactedSession session, List<SessionPartResult> parts, SupersededRun superseded, string? jobId, string state)
	{
		bool complete = state == IngestStateName.Complete;
		int written = parts.Count(static part => part.State == SessionPartState.Written);
		IngestRefusal[] refusals = [.. session.Refusals.Take(IngestRules.RefusalsReturned).Select(static refusal => new IngestRefusal(refusal.Part, refusal.Field, refusal.Rule))];
		audit.SetResult([input.Collection], written);
		string redactionName = IngestRedactionPolicy.NameOf(redaction.For(input.Collection));
		audit.SetTarget(new AuditIngest("session", redactionName, complete, written, parts.Count - written, superseded.Deleted.Count, session.CountsByRule.Values.Sum(), session.Refusals.Count));
		return new SessionIngestResult(
			input.Collection,
			input.SessionId,
			input.Format == SessionFormat.Codex ? IngestEndpointNames.Codex : IngestEndpointNames.Claude,
			complete,
			!complete && IsRetryable(parts) && superseded.Kept.Count == 0 && !LostMessages(session.Report),
			parts,
			new SessionSupersededResult(superseded.Deleted, superseded.Failed, superseded.Kept),
			redactionName,
			session.CountsByRule,
			refusals,
			session.Refusals.Count,
			Summarize(session.Report),
			jobId,
			superseded.ReindexJobId);
	}

	/// <summary>A part refused for good, such as one too large, stays refused however many times the upload is sent.</summary>
	private static bool IsRetryable(List<SessionPartResult> parts)
	{
		return !parts.Exists(static part => part is { State: SessionPartState.Failed, Retryable: false });
	}

	private static SessionReadSummary Summarize(SessionReadReport report)
	{
		return new SessionReadSummary(report.BytesRead, report.Lines, report.Messages, report.MalformedLines, report.OversizedLines, report.UnterminatedLines, report.MessagesOverLimit, report.HasSkippedContent);
	}

	private const string UnknownCollection = "unknown_collection";

	/// <summary>Room in a document for the frontmatter, title and headings around the longest message.</summary>
	private const int MessageHeadroomBytes = 16 * 1024;

	private sealed record SupersededRun(IReadOnlyList<string> Deleted, IReadOnlyList<string> Failed, IReadOnlyList<string> Kept, string? ReindexJobId, bool Finished)
	{
		public static readonly SupersededRun None = new([], [], [], null, false);
	}
}

/// <summary>The names the session route uses for formats.</summary>
public static class IngestEndpointNames
{
	public const string Claude = "claude";

	public const string Codex = "codex";
}
