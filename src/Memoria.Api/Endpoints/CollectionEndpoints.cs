using Memoria.Api.Http;
using Memoria.Api.Ingest;
using Memoria.Api.Search;
using Memoria.Core.Contracts;
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
			.WithDescription(
				"Totals for the collections the caller may read, whether a vector index exists, and per-collection counts. "
				+ "`needsEmbedding` is the backlog for the whole index, so it is null for a collection-scoped key. "
				+ "`unredactedCollections` lists what an operator stored unredacted. Only a key with the `admin` scope as well sees it; it is null for anyone else.")
			.WithTags("Collections");

		return group;
	}

	private static async Task<Ok<CollectionList>> ListCollectionsAsync(ScopedCorpus corpus, CancellationToken cancellationToken)
	{
		return TypedResults.Ok(await corpus.ListCollectionsAsync(cancellationToken));
	}

	private static async Task<Ok<IndexStatus>> GetStatusAsync(ScopedCorpus corpus, ICallerScope scope, IngestRedactionPolicy redaction, CancellationToken cancellationToken)
	{
		IndexStatus status = await corpus.GetStatusAsync(cancellationToken);
		return TypedResults.Ok(scope.SeesBackendDetails ? status with { UnredactedCollections = redaction.DisabledCollections } : status);
	}
}
