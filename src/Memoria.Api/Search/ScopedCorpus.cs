using System.Net;
using Memoria.Api.Auth;
using Memoria.Api.Endpoints;
using Memoria.Api.Http;
using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;

namespace Memoria.Api.Search;

/// <summary>
/// The corpus as one caller may see it. REST endpoints and MCP tools both read
/// through here, so scope filtering and audit reporting exist once. Every
/// result is checked again on the way out even when the sidecar was asked for
/// the right collections: the sidecar is not a security boundary.
/// </summary>
public sealed class ScopedCorpus(IMemoriaQmdClient sidecar, ICallerScope scope, IRequestAudit audit)
{
	private bool ReadsNothing => scope.Collections is { Count: 0 };

	private bool IsRestricted => scope.Collections is not null;

	public async Task<SearchResult> SearchAsync(SidecarSearchRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		audit.SetQuery(request.Query);

		// The sidecar reads an empty collection list as "the defaults". A caller
		// with no collections must get nothing, so the request never leaves.
		if (ReadsNothing)
		{
			audit.SetResult([], 0);
			return new SearchResult([], 0, 0, new SearchQueryInfo([], request.Rerank ?? false, request.Filters is not null, 0, request.Limit ?? 0));
		}

		SidecarSearchResponse response = await sidecar.SearchAsync(request, cancellationToken);
		SearchHit[] hits = response.Results.Where(hit => scope.Allows(hit.Collection) && scope.AllowsFile(hit.File)).Select(ContractMapping.ToContract).ToArray();
		int removed = response.Results.Count - hits.Length;

		string[] collections = response.Query.Collections.Where(scope.Allows).ToArray();
		audit.SetResult(collections, hits.Length);
		return new SearchResult(
			hits,
			response.Total - removed,
			response.TookMs,
			new SearchQueryInfo(collections, response.Query.Rerank, response.Query.Filtered, response.Query.Fetched - removed, response.Query.Limit));
	}

	/// <summary>
	/// The document, or null when it is outside the scope. For a scoped caller a
	/// sidecar miss is null too, so an out-of-scope document and a missing one
	/// are indistinguishable.
	/// </summary>
	public async Task<Document?> GetDocumentAsync(string reference, SidecarDocumentQuery query, CancellationToken cancellationToken)
	{
		audit.SetQuery(reference);
		if (ReadsNothing)
		{
			audit.SetResult([], 0);
			return null;
		}

		SidecarDocument document;
		try
		{
			document = await sidecar.GetDocumentAsync(reference, query, cancellationToken);
		}
		catch (SidecarException ex) when (IsRestricted && ex.StatusCode == HttpStatusCode.NotFound)
		{
			audit.SetResult([], 0);
			return null;
		}

		if (!scope.Allows(document.Collection))
		{
			audit.SetResult([], 0);
			return null;
		}

		audit.SetResult([document.Collection], 1);
		return document.ToContract();
	}

	/// <summary>
	/// Filters each list per result. A scoped caller gets no sidecar errors: they
	/// quote "did you mean" paths from any collection and ambiguous-path
	/// candidates, and their presence or absence would reveal whether a reference
	/// or pattern matched something out of scope. What did not resolve is absent.
	/// </summary>
	public async Task<MultiGetResult> MultiGetAsync(SidecarMultiGetRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		audit.SetQuery(request.Pattern ?? string.Join(',', request.Ids ?? []));
		if (ReadsNothing)
		{
			audit.SetResult([], 0);
			return new MultiGetResult([], [], []);
		}

		SidecarMultiGetResponse response = await sidecar.MultiGetAsync(request, cancellationToken);
		SidecarDocument[] docs = response.Docs.Where(doc => scope.Allows(doc.Collection)).ToArray();
		audit.SetResult(docs.Select(static doc => doc.Collection), docs.Length);
		return new MultiGetResult(
			docs.Select(ContractMapping.ToContract).ToArray(),
			response.Skipped.Where(skipped => scope.AllowsFile(skipped.File)).Select(static skipped => new SkippedDocument(skipped.File, skipped.Reason)).ToArray(),
			IsRestricted ? [] : response.Errors);
	}

	public async Task<CollectionList> ListCollectionsAsync(CancellationToken cancellationToken)
	{
		SidecarCollectionsResponse response = await sidecar.ListCollectionsAsync(cancellationToken);
		CollectionInfo[] collections = response.Collections.Where(collection => scope.Allows(collection.Name)).Select(ContractMapping.ToContract).ToArray();
		audit.SetResult(collections.Select(static collection => collection.Name), collections.Length);
		return new CollectionList(collections);
	}

	/// <summary>
	/// A scoped caller's totals cover only their collections. The sidecar reports
	/// its embedding backlog for the whole index alone, so a scoped caller gets
	/// null for it rather than a global number or a made-up zero.
	/// </summary>
	public async Task<IndexStatus> GetStatusAsync(CancellationToken cancellationToken)
	{
		SidecarStatus status = await sidecar.GetStatusAsync(cancellationToken);
		SidecarStatusCollection[] collections = status.Collections.Where(collection => scope.Allows(collection.Name)).ToArray();
		audit.SetResult(collections.Select(static collection => collection.Name), collections.Length);
		return new IndexStatus(
			IsRestricted ? collections.Sum(static collection => collection.Documents) : status.TotalDocuments,
			IsRestricted ? null : status.NeedsEmbedding,
			status.HasVectorIndex,
			collections.Select(ContractMapping.ToContract).ToArray());
	}
}
