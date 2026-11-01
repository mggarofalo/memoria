using Memoria.Api.Http;
using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Memoria.Api.Endpoints;

public static class CollectionEndpoints
{
	public static RouteGroupBuilder MapCollections(this RouteGroupBuilder group)
	{
		group.MapGet("/collections", ListCollectionsAsync)
			.WithName("listCollections")
			.WithSummary("List collections")
			.WithDescription("Every collection the caller may search, with its active document count and root context.")
			.WithTags("Collections");

		group.MapGet("/status", GetStatusAsync)
			.WithName("getStatus")
			.WithSummary("Index status")
			.WithDescription("Totals for the index: documents, documents awaiting embedding, whether a vector index exists, and per-collection counts.")
			.WithTags("Collections");

		return group;
	}

	private static async Task<Ok<CollectionList>> ListCollectionsAsync(
		IMemoriaQmdClient sidecar,
		ICallerScope scope,
		CancellationToken cancellationToken)
	{
		SidecarCollectionsResponse response = await sidecar.ListCollectionsAsync(cancellationToken);
		return TypedResults.Ok(new CollectionList(
			response.Collections
				.Where(c => scope.Allows(c.Name))
				.Select(ContractMapping.ToContract)
				.ToArray()));
	}

	private static async Task<Ok<IndexStatus>> GetStatusAsync(
		IMemoriaQmdClient sidecar,
		ICallerScope scope,
		CancellationToken cancellationToken)
	{
		SidecarStatus status = await sidecar.GetStatusAsync(cancellationToken);
		return TypedResults.Ok(new IndexStatus(
			status.TotalDocuments,
			status.NeedsEmbedding,
			status.HasVectorIndex,
			status.Collections
				.Where(c => scope.Allows(c.Name))
				.Select(ContractMapping.ToContract)
				.ToArray()));
	}
}
