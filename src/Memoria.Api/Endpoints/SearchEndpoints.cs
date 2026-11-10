using System.ComponentModel;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Api.Search;
using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Endpoints;

public static class SearchEndpoints
{
	public static RouteGroupBuilder MapSearch(this RouteGroupBuilder group)
	{
		group.MapGet("/search", SearchAsync)
			.WithName("search")
			.WithSummary("Search the corpus")
			.WithDescription(
				"Hybrid search over the indexed documents: BM25 and vector retrieval fused by reciprocal rank, "
				+ "then reranked by a cross-encoder unless `rerank=false`. Reranked queries take a few seconds; "
				+ "`rerank=false` returns fused results in well under a second. Frontmatter filters "
				+ "(`project`, `branch`, `since`, `until`, `tag`) are applied after retrieval, so a filtered "
				+ "result set can be shorter than `limit` even when more matches exist. `degraded` means the search ran without "
				+ "something a full search uses, such as the reranker: results are valid but may rank worse, and `degradedReasons` says what.")
			.WithTags("Search")
			.ProducesValidationProblem();
		return group;
	}

	private static async Task<Results<Ok<SearchResult>, ValidationProblem>> SearchAsync(
		[AsParameters] SearchParameters parameters,
		ScopedCorpus corpus,
		ICallerScope scope,
		IOptions<MemoriaOptions> options,
		CancellationToken cancellationToken)
	{
		SearchInput input = new(
			parameters.Query,
			parameters.Collection,
			parameters.Limit,
			parameters.MinScore,
			parameters.Rerank,
			parameters.Intent,
			parameters.CandidateLimit,
			parameters.Project,
			parameters.Branch,
			parameters.Since,
			parameters.Until,
			parameters.Tag,
			parameters.Expand);

		SidecarSearchRequest? request = SearchRequestFactory.TryBuild(input, options.Value.Search, scope, out Dictionary<string, string[]> errors);
		if (request is null)
		{
			return TypedResults.ValidationProblem(errors);
		}

		return TypedResults.Ok(await corpus.SearchAsync(request, cancellationToken));
	}
}

/// <summary>Query string of <c>GET /v1/search</c>.</summary>
public sealed class SearchParameters
{
	[FromQuery(Name = "query")]
	[Description("Plain-text query. It is searched as itself, lexically and by vector, unless `expand` asks QMD to expand it.")]
	public string? Query { get; set; }

	[FromQuery(Name = "expand")]
	[Description("Let QMD's model expand the query into more lexical and semantic sub-queries first. Slower, and no better on the measured corpus. The server's default is usually false.")]
	public bool? Expand { get; set; }

	[FromQuery(Name = "collection")]
	[Description("Collection to search. Repeat for several; names are case-sensitive. Omitted, a collection-scoped key searches all its collections; other callers get the configured default set, else QMD's default-included collections. A collection outside the key's scope is refused with 403.")]
	public string[]? Collection { get; set; }

	[FromQuery(Name = "limit")]
	[Description("Maximum results, 1 to the configured ceiling (100). Default 10.")]
	public int? Limit { get; set; }

	[FromQuery(Name = "minScore")]
	[Description("Drop results scoring below this, 0 to 1.")]
	public double? MinScore { get; set; }

	[FromQuery(Name = "rerank")]
	[Description("Run the LLM reranker. Default true (full hybrid). false returns sub-second lexical plus vector fusion.")]
	public bool? Rerank { get; set; }

	[FromQuery(Name = "intent")]
	[Description("Background context that steers reranking and snippet selection. Does not search on its own.")]
	public string? Intent { get; set; }

	[FromQuery(Name = "candidateLimit")]
	[Description("Candidates handed to the reranker, 1 to 1000. QMD's default is 40.")]
	public int? CandidateLimit { get; set; }

	[FromQuery(Name = "project")]
	[Description("Frontmatter filter: `project` equals any of these (case-insensitive). Repeatable.")]
	public string[]? Project { get; set; }

	[FromQuery(Name = "branch")]
	[Description("Frontmatter filter: `git_branch` equals any of these (case-insensitive). Repeatable.")]
	public string[]? Branch { get; set; }

	[FromQuery(Name = "since")]
	[Description("Frontmatter filter: inclusive lower bound on the document's start time, `YYYY-MM-DD` or ISO timestamp.")]
	public string? Since { get; set; }

	[FromQuery(Name = "until")]
	[Description("Frontmatter filter: inclusive upper bound; a bare date covers the whole day.")]
	public string? Until { get; set; }

	[FromQuery(Name = "tag")]
	[Description("Frontmatter filter: every listed tag must be present (case-insensitive). Repeatable.")]
	public string[]? Tag { get; set; }
}
