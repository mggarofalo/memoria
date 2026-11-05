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
public sealed class DocumentIngestor(
	IMemoriaQmdClient sidecar,
	IIngestLedger ledger,
	IRedactor redactor,
	IngestRedactionPolicy redaction,
	IngestGates gates,
	CollectionReindexer reindexer,
	ICallerScope scope,
	IRequestAudit audit,
	IOptions<MemoriaOptions> options,
	TimeProvider timeProvider)
{
	/// <summary>The request has been validated: every field it needs is set.</summary>
	public async Task<DocumentIngestResult> IngestAsync(DocumentIngestRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		string collection = request.Collection!;
		IngestRules.RequireCollection(scope, collection);
		audit.SetResult([collection], 0);
		RequireFits(Encoding.UTF8.GetByteCount(request.Body!), "The body alone");

		DocumentEnvelope envelope = new(request.Source!, request.SourceId!, request.Url, request.Title, request.Updated, request.Project, request.Tags);
		string path = envelope.StoragePath();
		RedactionDisabledCollection? disabled = redaction.For(collection);
		string redactionName = IngestRedactionPolicy.NameOf(disabled);
		(RedactedMarkdown decided, List<EnvelopeFieldRedaction> fields) = await gates.WorkAsync(() => Decide(envelope, request.Body!, disabled), cancellationToken);
		Dictionary<string, int> counts = new(decided.CountsByRule, StringComparer.Ordinal);
		fields.ForEach(field => counts[field.Rule] = counts.GetValueOrDefault(field.Rule) + field.Count);
		IngestRefusal[] refusals = [.. fields.Select(static field => new IngestRefusal(1, field.Field, field.Rule))];
		RequireFits(Encoding.UTF8.GetByteCount(decided.Text), "A document once rendered");

		string identity = IdentityOf(collection, path);
		IngestClaim claim = await ledger.ClaimAsync(new IngestClaimRequest(identity, collection, request.Updated!.Value, timeProvider.GetUtcNow(), [path]), cancellationToken);
		IngestRules.RequireClaim(claim);
		audit.SetTarget(new AuditIngest("document", redactionName, false, 0, 1, 0, counts.Values.Sum(), refusals.Length, [path]));

		string state = IngestStateName.Partial;
		try
		{
			SidecarIngestResponse response = await sidecar.IngestAsync(new SidecarIngestRequest(collection, path, decided, Overwrite: true), cancellationToken);
			state = IngestStateName.Complete;
			audit.SetResult([collection], 1);
			audit.SetTarget(new AuditIngest("document", redactionName, true, 1, 0, 0, counts.Values.Sum(), refusals.Length, [path]));
			return new DocumentIngestResult(collection, response.Path, response.File, response.Bytes, response.Created, redactionName, counts, refusals, response.Job.Id);
		}
		finally
		{
			await ledger.FinishAsync(identity, claim.LeaseToken!, collection, state, [path]);
		}
	}

	/// <summary>
	/// Removes a document. An index update follows only when something was removed, or an earlier one is still owed: a deleted document
	/// stays searchable until the index changes, and each update is a scan that blocks the sidecar. Safe to retry, including after the file is
	/// gone, which is how a failed update is finished. The ledger is told first, so an older copy replayed later cannot bring it back.
	/// </summary>
	public async Task<DocumentDeleteResult> DeleteAsync(string collection, string path, CancellationToken cancellationToken)
	{
		IngestRules.RequireCollection(scope, collection);
		audit.SetResult([collection], 0);
		audit.SetTarget(new AuditIngest("delete", IngestRedactionPolicy.NameOf(redaction.For(collection)), false, 0, 0, 0, 0, 0, [path]));

		string identity = IdentityOf(collection, path);
		IngestDeleteGate gate = await ledger.PrepareDeleteAsync(collection, path, identity, timeProvider.GetUtcNow(), cancellationToken);
		if (gate.Outcome == IngestDeleteOutcome.Busy)
		{
			IngestRules.RequireClaim(new IngestClaim(IngestClaimOutcome.Busy, [], null, gate.RetryAfterSeconds));
		}

		SidecarDeleteResponse response = await sidecar.DeleteDocumentAsync(collection, path, cancellationToken);
		if (gate.Outcome == IngestDeleteOutcome.NoRecord && response.Deleted)
		{
			await ledger.RecordDeletedAsync(identity, timeProvider.GetUtcNow(), cancellationToken);
		}

		ReindexOutcome reindex = await reindexer.RequestAsync(collection, response.Deleted, cancellationToken);
		int deleted = response.Deleted ? 1 : 0;
		audit.SetResult([collection], deleted);
		audit.SetTarget(new AuditIngest("delete", IngestRedactionPolicy.NameOf(redaction.For(collection)), reindex.State != ReindexState.Failed, 0, 0, deleted, 0, 0, [path]));
		return new DocumentDeleteResult(collection, response.Path, response.Deleted, reindex.State, reindex.JobId, reindex.StartsInSeconds);
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
}
