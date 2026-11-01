using System.ComponentModel;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
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
				+ "result set can be shorter than `limit` even when more matches exist.")
			.WithTags("Search")
			.ProducesValidationProblem();
		return group;
	}

	private static async Task<Results<Ok<SearchResult>, ValidationProblem>> SearchAsync(
		[AsParameters] SearchParameters parameters,
		IMemoriaQmdClient sidecar,
		ICallerScope scope,
		IOptions<MemoriaOptions> options,
		CancellationToken cancellationToken)
	{
		SearchOptions defaults = options.Value.Search;
		Dictionary<string, string[]> errors = [];

		if (string.IsNullOrWhiteSpace(parameters.Query))
		{
			errors["query"] = ["A non-empty query is required."];
		}
		else if (parameters.Query.Length > 4000)
		{
			errors["query"] = ["The query may be at most 4000 characters."];
		}

		if (parameters.Limit is int limit && (limit < 1 || limit > defaults.MaxLimit))
		{
			errors["limit"] = [$"limit must be between 1 and {defaults.MaxLimit}."];
		}

		if (parameters.MinScore is double minScore && (minScore < 0 || minScore > 1))
		{
			errors["minScore"] = ["minScore must be between 0 and 1."];
		}

		if (parameters.CandidateLimit is int candidates && (candidates < 1 || candidates > 1000))
		{
			errors["candidateLimit"] = ["candidateLimit must be between 1 and 1000."];
		}

		if (errors.Count > 0)
		{
			return TypedResults.ValidationProblem(errors);
		}

		SidecarSearchFilters filters = new(
			Project: NonEmpty(parameters.Project),
			Branch: NonEmpty(parameters.Branch),
			Since: parameters.Since,
			Until: parameters.Until,
			Tags: NonEmpty(parameters.Tag));

		SidecarSearchRequest request = new()
		{
			Query = parameters.Query,
			Intent = string.IsNullOrWhiteSpace(parameters.Intent) ? null : parameters.Intent,
			Collections = scope.Resolve(NonEmpty(parameters.Collection)) ?? defaults.DefaultCollections,
			Filters = filters.IsEmpty ? null : filters,
			Limit = parameters.Limit ?? defaults.DefaultLimit,
			MinScore = parameters.MinScore,
			CandidateLimit = parameters.CandidateLimit,
			Rerank = parameters.Rerank ?? defaults.RerankByDefault,
		};

		SidecarSearchResponse response = await sidecar.SearchAsync(request, cancellationToken);
		return TypedResults.Ok(response.ToContract());
	}

	private static IReadOnlyList<string>? NonEmpty(string[]? values)
	{
		if (values is null)
		{
			return null;
		}

		string[] kept = values.Where(static v => !string.IsNullOrWhiteSpace(v)).ToArray();
		return kept.Length == 0 ? null : kept;
	}
}

/// <summary>Query string of <c>GET /v1/search</c>.</summary>
public sealed class SearchParameters
{
	[FromQuery(Name = "query")]
	[Description("Plain-text query. QMD expands it into lexical and semantic sub-queries.")]
	public string? Query { get; set; }

	[FromQuery(Name = "collection")]
	[Description("Collection to search. Repeat for several. Defaults to the configured default set, else QMD's default-included collections.")]
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
