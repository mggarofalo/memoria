using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;

namespace Memoria.Api.Endpoints;

/// <summary>
/// Sidecar wire types to public v1 contracts. Kept as one explicit mapping so
/// the public API can stay stable while the sidecar contract moves.
/// </summary>
internal static class ContractMapping
{
	public static SearchResult ToContract(this SidecarSearchResponse response)
	{
		return new SearchResult(
			response.Results.Select(ToContract).ToArray(),
			response.Total,
			response.TookMs,
			new SearchQueryInfo(
				response.Query.Collections,
				response.Query.Rerank,
				response.Query.Filtered,
				response.Query.Fetched,
				response.Query.Limit));
	}

	public static SearchHit ToContract(this SidecarSearchHit hit)
	{
		return new SearchHit(
			hit.Docid,
			hit.File,
			hit.Collection,
			hit.Path,
			hit.Title,
			hit.Score,
			hit.Line,
			hit.Snippet,
			hit.Context,
			hit.Metadata);
	}

	public static Document ToContract(this SidecarDocument document)
	{
		return new Document(
			document.Docid,
			document.File,
			document.Collection,
			document.Path,
			document.Title,
			document.Context,
			document.ModifiedAt,
			document.BodyLength,
			document.FromLine,
			document.MaxLines,
			document.Body,
			document.Metadata);
	}

	public static CollectionInfo ToContract(this SidecarCollection collection)
	{
		return new CollectionInfo(
			collection.Name,
			collection.Documents,
			collection.LastModified,
			collection.IncludeByDefault,
			collection.Context);
	}

	public static IndexCollectionStatus ToContract(this SidecarStatusCollection collection)
	{
		return new IndexCollectionStatus(collection.Name, collection.Documents, collection.LastUpdated);
	}
}
