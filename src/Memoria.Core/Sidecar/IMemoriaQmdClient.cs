namespace Memoria.Core.Sidecar;

/// <summary>
/// Typed client for the memoria-qmd sidecar (sidecar/openapi.json). Every
/// method throws <see cref="SidecarException"/> on a non-success response or a
/// transport failure. Implementations are the only code that speaks the
/// sidecar's wire format; the API, the MCP server and ingest all go through
/// this interface.
/// </summary>
public interface IMemoriaQmdClient
{
	/// <summary>Liveness. Unauthenticated; does not touch the index.</summary>
	Task<SidecarHealth> GetHealthAsync(CancellationToken cancellationToken = default);

	/// <summary>Readiness. With <paramref name="requireModels"/> the sidecar answers 503 until the models are resident; the body is returned either way.</summary>
	Task<SidecarReadinessResult> GetReadinessAsync(bool requireModels, CancellationToken cancellationToken = default);

	/// <summary>QMD's index status: totals and per-collection counts.</summary>
	Task<SidecarStatus> GetStatusAsync(CancellationToken cancellationToken = default);

	/// <summary>Hybrid search: BM25 + vectors fused by RRF, reranked unless <c>Rerank</c> is false.</summary>
	Task<SidecarSearchResponse> SearchAsync(SidecarSearchRequest request, CancellationToken cancellationToken = default);

	/// <summary>One document by <c>collection/path</c>, <c>qmd://</c> URI or docid (with or without a leading <c>#</c>).</summary>
	Task<SidecarDocument> GetDocumentAsync(string reference, SidecarDocumentQuery? query = null, CancellationToken cancellationToken = default);

	/// <summary>Several documents by glob and/or a list of references.</summary>
	Task<SidecarMultiGetResponse> MultiGetAsync(SidecarMultiGetRequest request, CancellationToken cancellationToken = default);

	/// <summary>Every collection the sidecar serves.</summary>
	Task<SidecarCollectionsResponse> ListCollectionsAsync(CancellationToken cancellationToken = default);

	/// <summary>Whether a collection's directory holds anything, asked once before ingest opens it.</summary>
	Task<SidecarCollectionEmptyResponse> IsCollectionEmptyAsync(string collection, CancellationToken cancellationToken = default);

	/// <summary>Write a (redacted) markdown document into a collection and queue indexing. Returns the accepted job.</summary>
	Task<SidecarIngestResponse> IngestAsync(SidecarIngestRequest request, CancellationToken cancellationToken = default);

	/// <summary>Queue an update (and optionally embed) job for some or all collections.</summary>
	/// <summary>Removes a document's file and queues a debounced update. Follow with <see cref="ReindexAsync"/> when the document must stop being searchable now.</summary>
	Task<SidecarDeleteResponse> DeleteDocumentAsync(string collection, string path, CancellationToken cancellationToken = default);

	Task<SidecarJob> ReindexAsync(SidecarReindexRequest request, CancellationToken cancellationToken = default);

	/// <summary>Jobs this sidecar process has seen.</summary>
	Task<IReadOnlyList<SidecarJob>> ListJobsAsync(CancellationToken cancellationToken = default);

	/// <summary>One job by id.</summary>
	Task<SidecarJob> GetJobAsync(string id, CancellationToken cancellationToken = default);
}
