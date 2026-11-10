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

/// <summary>
/// Writes and deletes single documents. The redaction decision is made here, before the sidecar call, and only
/// from operator configuration: <see cref="SidecarIngestRequest"/> takes only a <see cref="RedactedMarkdown"/>, and nothing
/// in a request can reach <see cref="IngestRedactionPolicy"/> except the collection name it looks up.
/// </summary>
public sealed partial class DocumentIngestor(
	IMemoriaQmdClient sidecar,
	IIngestLedger ledger,
	IRedactor redactor,
	IngestRedactionPolicy redaction,
	IngestGates gates,
	IngestDigests digests,
	IngestPathLocks locks,
	IngestCollections collections,
	CollectionReindexer reindexer,
	ICallerScope scope,
	IRequestAudit audit,
	IOptions<MemoriaOptions> options,
	TimeProvider timeProvider,
	ILogger<DocumentIngestor> logger)
{
	/// <summary>The request has been validated: every field it needs is set.</summary>
	public async Task<DocumentIngestResult> IngestAsync(DocumentIngestRequest request, bool restore, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		string collection = request.Collection!;
		IngestRules.RequireCollection(scope, collection);
		audit.SetResult([collection], 0);
		await collections.RequireOpenAsync(collection, sidecar, cancellationToken);
		int bodyBytes = Encoding.UTF8.GetByteCount(request.Body!);
		RequireFits(bodyBytes, "The body alone");

		DocumentEnvelope envelope = new(request.Source!, request.SourceId!, request.Url, request.Title, request.Updated, request.Project, request.Tags);
		string path = envelope.StoragePath();
		RedactionDisabledCollection? disabled = redaction.For(collection);
		string redactionName = IngestRedactionPolicy.NameOf(disabled);
		(RedactedMarkdown decided, List<EnvelopeFieldRedaction> fields) = await gates.WorkAsync(bodyBytes, () => Decide(envelope, request.Body!, disabled), cancellationToken);
		Dictionary<string, int> counts = new(decided.CountsByRule, StringComparer.Ordinal);
		fields.ForEach(field => counts[field.Rule] = counts.GetValueOrDefault(field.Rule) + field.Count);
		IngestRefusal[] refusals = [.. fields.Select(static field => new IngestRefusal(1, field.Field, field.Rule))];
		RequireFits(Encoding.UTF8.GetByteCount(decided.Text), "A document once rendered");

		string identity = IdentityOf(collection, path);
		string content = ContentOf(decided.Text);
		IngestClaimRequest claiming = new(identity, collection, request.Updated!.Value, timeProvider.GetUtcNow(), [path], ContentHash: digests.Of(content), Restore: restore, IsSameContent: stored => digests.Matches(stored, content));
		IngestClaim claim = await ledger.ClaimAsync(claiming, cancellationToken);
		IngestRules.RequireClaim(claim);
		audit.SetTarget(new AuditIngest("document", redactionName, false, 0, 1, 0, counts.Values.Sum(), refusals.Length, [path], restore));

		SidecarIngestResponse response = await WriteAndFinishAsync(new SidecarIngestRequest(collection, path, decided), identity, claim, claiming, cancellationToken);
		audit.SetResult([collection], 1);
		audit.SetTarget(new AuditIngest("document", redactionName, true, 1, 0, 0, counts.Values.Sum(), refusals.Length, [path], restore));
		return new DocumentIngestResult(collection, response.Path, response.File, response.Bytes, response.Created, redactionName, counts, refusals, response.Job.Id);
	}

	/// <summary>Checks a caller may write here, before a body is read.</summary>
	public async Task RequireWritableAsync(string collection, CancellationToken cancellationToken)
	{
		IngestRules.RequireCollection(scope, collection);
		audit.SetResult([collection], 0);
		await collections.RequireOpenAsync(collection, sidecar, cancellationToken);
	}

	/// <summary>
	/// Stores a document someone else rendered, at the path it already has, for moving an old corpus. Its whole text goes through the same redaction decision as any other, frontmatter
	/// included, and it is otherwise kept as it came. Sending it again is safe: the path is the identity, and an equal version overwrites with the same result.
	/// </summary>
	public async Task<RenderedDocumentResult> StoreRenderedAsync(string collection, string path, string text, DateTimeOffset updated, CancellationToken cancellationToken)
	{
		RedactionDisabledCollection? disabled = redaction.For(collection);
		string redactionName = IngestRedactionPolicy.NameOf(disabled);
		RedactedMarkdown decided = await gates.WorkAsync(
			Encoding.UTF8.GetByteCount(text),
			() => disabled is null ? redactor.Redact(RenderedDocument.ForRedaction(text)) : RenderedDocument.WithoutRedactionForDisabledCollection(text, disabled),
			cancellationToken);
		int limit = options.Value.Ingest.MaxDocumentBytes;
		if (Encoding.UTF8.GetByteCount(decided.Text) > limit)
		{
			throw IngestRules.TooLarge($"A document once redacted can be at most {limit} bytes.");
		}

		string identity = IdentityOf(collection, path);
		IngestClaimRequest claiming = new(identity, collection, updated, timeProvider.GetUtcNow(), [path], Lookup: RenderedDocument.SessionIdOf(decided.Text) is { } sessionId ? IngestIdentity.ForRenderedSession(collection, sessionId, path) : null, MessageCount: RenderedDocument.MessagesThroughOf(decided.Text), ContentHash: digests.Of(decided.Text), IsSameContent: stored => digests.Matches(stored, decided.Text));
		IngestClaim claim = await ledger.ClaimAsync(claiming, cancellationToken);
		RequireRenderedClaim(claim, collection, identity);
		int redactions = decided.CountsByRule.Values.Sum();
		audit.SetTarget(new AuditIngest("rendered", redactionName, false, 0, 1, 0, redactions, 0, [path]));

		SidecarIngestResponse response = await WriteAndFinishAsync(new SidecarIngestRequest(collection, path, decided), identity, claim, claiming, cancellationToken);
		audit.SetResult([collection], 1);
		audit.SetTarget(new AuditIngest("rendered", redactionName, true, 1, 0, 0, redactions, 0, [path]));
		return new RenderedDocumentResult(collection, response.Path, response.File, response.Bytes, response.Created, redactionName, decided.CountsByRule, RenderedDocument.FrontmatterOf(decided.Text, response.Metadata is not null), response.Job.Id);
	}

	private void RequireRenderedClaim(IngestClaim claim, string collection, string identity)
	{
		if (claim.Outcome == IngestClaimOutcome.Shrunk)
		{
			LogRenderedLooksCut(logger, collection, identity);
			throw IngestRules.RenderedShrank();
		}

		IngestRules.RequireClaim(claim);
	}

	/// <summary>What the ledger holds for a path, so a migration can check by path what arrived.</summary>
	public async Task<RenderedDocumentStatus> RenderedStatusAsync(string collection, string path, CancellationToken cancellationToken)
	{
		IngestRules.RequireCollection(scope, collection);
		IngestRecord? record = await ledger.FindAsync(IdentityOf(collection, path), cancellationToken);
		return record is null
			? new RenderedDocumentStatus(collection, path, IngestStateName.None, null)
			: new RenderedDocumentStatus(collection, path, ledger.EffectiveState(record, timeProvider.GetUtcNow()), record.Version.UtcTicks == 0 ? null : record.Version);
	}

	/// <summary>
	/// Removes a document. It waits for any write of the path to answer, leaves the tombstone, and then removes the file. An index update follows only when something was removed: a
	/// deleted document stays searchable until the index changes, and each update is a scan that blocks the sidecar. Anything short of the sidecar's confirmation answers 503, and repeating it is safe.
	/// </summary>
	public async Task<DocumentDeleteResult> DeleteAsync(string collection, string path, CancellationToken cancellationToken)
	{
		IngestRules.RequireCollection(scope, collection);
		audit.SetResult([collection], 0);
		string redactionName = IngestRedactionPolicy.NameOf(redaction.For(collection));
		audit.SetTarget(new AuditIngest("delete", redactionName, false, 0, 0, 0, 0, 0, [path]));
		await collections.RequireOpenAsync(collection, sidecar, cancellationToken);

		using IDisposable held = await locks.TryHoldAsync(collection, path, options.Value.Ingest.DeleteWriteWait, cancellationToken) ?? throw IngestRules.DeleteUnconfirmed();
		await ledger.MarkDeletedAsync(collection, path, IdentityOf(collection, path), timeProvider.GetUtcNow());
		SidecarDeleteResponse response = await RemoveAsync(collection, path);
		await ledger.ForgetPathAsync(collection, path);

		ReindexOutcome reindex = await reindexer.RequestAsync(collection, response.Deleted, CancellationToken.None);
		int deleted = response.Deleted ? 1 : 0;
		audit.SetResult([collection], deleted);
		audit.SetTarget(new AuditIngest("delete", redactionName, reindex.State != ReindexState.Failed, 0, 0, deleted, 0, 0, [path]));
		return new DocumentDeleteResult(collection, path, response.Deleted, reindex.State, reindex.JobId, reindex.StartsInSeconds);
	}

	/// <summary>Runs to its answer even if the caller goes away. A refusal on the merits, such as a directory at the path, is the answer; no answer, or a sidecar too busy to give one, is not.</summary>
	private async Task<SidecarDeleteResponse> RemoveAsync(string collection, string path)
	{
		try
		{
			return await sidecar.DeleteDocumentAsync(collection, path, CancellationToken.None);
		}
		catch (SidecarException exception) when (exception.StatusCode is null || exception.IsTransient)
		{
			throw IngestRules.DeleteUnconfirmed();
		}
	}

	/// <summary>
	/// A write the sidecar refused, with a 4xx answer, wrote nothing: it validates before the file lands. So the row keeps only what earlier writes left, and a session
	/// taking it over is not held up by a count it never had. Without an answer, or with a 5xx one after the file may have landed, the path stays claimed, as what it
	/// holds is unknown.
	/// </summary>
	private async Task<SidecarIngestResponse> WriteAndFinishAsync(SidecarIngestRequest request, string identity, IngestClaim claim, IngestClaimRequest claiming, CancellationToken cancellationToken)
	{
		SidecarIngestResponse? response = null;
		IReadOnlyList<string> paths = [.. claim.PreviousPaths.Union([request.Path], StringComparer.Ordinal)];
		bool kept;
		try
		{
			response = await WriteAsync(request, identity, claim.LeaseToken!, cancellationToken);
		}
		catch (SidecarException exception) when (exception.StatusCode is { } status && (int)status < 500)
		{
			paths = claim.PreviousPaths;
			throw;
		}
		finally
		{
			kept = await ledger.FinishAsync(identity, claim.LeaseToken!, request.Collection, response is null ? IngestStateName.Partial : IngestStateName.Complete, response is null ? paths : [request.Path], response is null ? null : claiming);
		}

		// The write landed, and then a delete took the path before the upload finished, so the file is gone.
		return kept ? response : throw IngestRules.DeletedDuringUpload();
	}

	/// <summary>
	/// The write runs to its answer even if the caller goes away, as the sidecar drops it only if this process dies or gives up on it. The path's lock is held for all of that,
	/// and the lease is checked under it, so a delete either comes first and stops this write, or waits for it.
	/// </summary>
	private async Task<SidecarIngestResponse> WriteAsync(SidecarIngestRequest request, string identity, string leaseToken, CancellationToken cancellationToken)
	{
		using IDisposable held = (await locks.TryHoldAsync(request.Collection, request.Path, IngestRules.NoLimit, cancellationToken))!;
		if (!await ledger.BeginWriteAsync(identity, leaseToken, request.Collection, request.Path, timeProvider.GetUtcNow(), cancellationToken))
		{
			throw IngestRules.DeletedDuringUpload();
		}

		return await sidecar.IngestAsync(request, CancellationToken.None);
	}

	/// <summary>
	/// What a document says, leaving out its date, so a tombstone can tell the deleted copy from a real new version whatever date it claims.
	/// Only ever stored through <see cref="IngestDigests"/>: redaction alone would not stop a hash confirming a guess at text no rule matches.
	/// </summary>
	private static string ContentOf(string text)
	{
		const string Fence = "\n---\n";
		int close = text.IndexOf(Fence, 3, StringComparison.Ordinal);
		IEnumerable<string> fields = text[..close].Split('\n').Where(static line => !line.StartsWith("updated:", StringComparison.Ordinal) && !line.StartsWith("date:", StringComparison.Ordinal));
		return string.Join('\n', fields) + text[close..];
	}

	private (RedactedMarkdown Decided, List<EnvelopeFieldRedaction> Fields) Decide(DocumentEnvelope envelope, string body, RedactionDisabledCollection? disabled)
	{
		List<EnvelopeFieldRedaction> fields = [];
		RedactedMarkdown decided = disabled is null
			? redactor.Redact(envelope.Render(body, fields))
			: envelope.RenderWithoutRedactionForDisabledCollection(body, disabled);
		return (decided, fields);
	}

	private void RequireFits(int bytes, string what)
	{
		int limit = options.Value.Ingest.MaxDocumentBytes;
		if (bytes > limit)
		{
			throw IngestRules.TooLarge($"{what} can be at most {limit} bytes. Split it.");
		}
	}

	public static string IdentityOf(string collection, string path)
	{
		return IngestIdentity.For("document", collection, path);
	}

	[LoggerMessage(EventId = 3312, Level = LogLevel.Warning, Message = "A rendered document for collection {Collection} was refused: it says it holds fewer messages than the copy at its path, and nothing was changed. The document is ingest_state identity {Identity}. If it really is shorter, delete the path and send it again dated after the delete.")]
	private static partial void LogRenderedLooksCut(ILogger logger, string collection, string identity);
}
