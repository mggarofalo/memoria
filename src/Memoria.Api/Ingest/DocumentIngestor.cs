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

		DocumentEnvelope envelope = new(request.Source!, request.SourceId!, request.Url, request.Title, request.Updated, request.Project, request.Tags);
		string path = envelope.StoragePath();
		RedactionDisabledCollection? disabled = redaction.For(collection);
		List<EnvelopeFieldRedaction> fields = [];
		RedactedMarkdown decided = disabled is null
			? redactor.Redact(envelope.Render(request.Body!, fields))
			: envelope.RenderWithoutRedactionForDisabledCollection(request.Body!, disabled);
		Dictionary<string, int> counts = new(decided.CountsByRule, StringComparer.Ordinal);
		fields.ForEach(field => counts[field.Rule] = counts.GetValueOrDefault(field.Rule) + field.Count);
		IngestRefusal[] refusals = [.. fields.Select(static field => new IngestRefusal(1, field.Field, field.Rule))];
		string redactionName = IngestRedactionPolicy.NameOf(disabled);
		RequireFits(decided);

		string identity = IdentityOf(collection, path);
		IngestClaim claim = await ledger.ClaimAsync(identity, request.Updated!.Value, timeProvider.GetUtcNow(), cancellationToken);
		IngestRules.RequireClaim(claim);

		string state = IngestStateName.Partial;
		try
		{
			SidecarIngestResponse response = await sidecar.IngestAsync(new SidecarIngestRequest(collection, path, decided, Overwrite: true), cancellationToken);
			state = IngestStateName.Complete;
			audit.SetResult([collection], 1);
			audit.SetTarget(new AuditIngest("document", redactionName, true, 1, 0, 0, counts.Values.Sum(), refusals.Length));
			return new DocumentIngestResult(collection, response.Path, response.File, response.Bytes, response.Created, redactionName, counts, refusals, response.Job.Id);
		}
		finally
		{
			await ledger.FinishAsync(identity, state, [path]);
		}
	}

	/// <summary>
	/// Removes a document, then asks for the index update at once: the document stays searchable until the index
	/// changes, so it must not wait out the debounce. A tombstone goes first, so an older copy replayed later cannot bring it
	/// back. Safe to retry, including after the file is gone, which is how a failed update is finished.
	/// </summary>
	public async Task<DocumentDeleteResult> DeleteAsync(string collection, string path, CancellationToken cancellationToken)
	{
		IngestRules.RequireCollection(scope, collection);
		audit.SetResult([collection], 0);
		if (!await ledger.TombstoneAsync(IdentityOf(collection, path), timeProvider.GetUtcNow(), cancellationToken))
		{
			IngestRules.RequireClaim(new IngestClaim(IngestClaimOutcome.Busy, []));
		}

		SidecarDeleteResponse response = await sidecar.DeleteDocumentAsync(collection, path, cancellationToken);
		string? reindexJobId = await ReindexNowAsync(collection, cancellationToken);
		audit.SetResult([collection], response.Deleted ? 1 : 0);
		audit.SetTarget(new AuditIngest("delete", IngestRedactionPolicy.NameOf(redaction.For(collection)), reindexJobId is not null, 0, 0, response.Deleted ? 1 : 0, 0, 0));
		return new DocumentDeleteResult(collection, response.Path, response.Deleted, reindexJobId);
	}

	/// <summary>Null when the update could not be queued, which the caller reports. The sidecar's own debounced job still stands behind it.</summary>
	private async Task<string?> ReindexNowAsync(string collection, CancellationToken cancellationToken)
	{
		try
		{
			SidecarJob job = await sidecar.ReindexAsync(new SidecarReindexRequest([collection], Embed: false), cancellationToken);
			return job.Id;
		}
		catch (SidecarException)
		{
			return null;
		}
	}

	private void RequireFits(RedactedMarkdown decided)
	{
		int limit = options.Value.Ingest.MaxDocumentBytes;
		if (Encoding.UTF8.GetByteCount(decided.Text) > limit)
		{
			throw IngestRules.TooLarge($"A document can be at most {limit} bytes once rendered. Split it.");
		}
	}

	public static string IdentityOf(string collection, string path)
	{
		return IngestIdentity.For("document", collection, path);
	}
}
