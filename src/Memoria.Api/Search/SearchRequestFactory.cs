using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Core.Sidecar;

namespace Memoria.Api.Search;

/// <summary>What a caller asked for, before defaults, ceilings and scope are applied. Shared by REST and MCP.</summary>
public sealed record SearchInput(
	string? Query,
	IReadOnlyList<string>? Collections = null,
	int? Limit = null,
	double? MinScore = null,
	bool? Rerank = null,
	string? Intent = null,
	int? CandidateLimit = null,
	IReadOnlyList<string>? Project = null,
	IReadOnlyList<string>? Branch = null,
	string? Since = null,
	string? Until = null,
	IReadOnlyList<string>? Tags = null,
	bool? Expand = null);

/// <summary>
/// One place that turns a caller's search into a sidecar request: validation
/// against the sidecar's own limits, the configured defaults (full reranked
/// hybrid, limit 10), and the caller's collection scope. REST and MCP both go
/// through here so they can never disagree about what a default search is.
/// </summary>
public static class SearchRequestFactory
{
	public const int MaxQueryLength = 4000;
	public const int MaxCandidateLimit = 1000;

	/// <summary>Returns the request, or null with one message per offending field in <paramref name="errors"/>.</summary>
	public static SidecarSearchRequest? TryBuild(SearchInput input, SearchOptions defaults, ICallerScope scope, out Dictionary<string, string[]> errors)
	{
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(defaults);
		ArgumentNullException.ThrowIfNull(scope);

		errors = Validate(input, defaults);
		if (errors.Count > 0)
		{
			return null;
		}

		SidecarSearchFilters filters = new(
			Project: NonEmpty(input.Project),
			Branch: NonEmpty(input.Branch),
			Since: Trimmed(input.Since),
			Until: Trimmed(input.Until),
			Tags: NonEmpty(input.Tags));

		// Unexpanded, the query goes as typed sub-queries, which QMD searches as they are.
		bool expand = input.Expand ?? defaults.ExpandByDefault;
		return new SidecarSearchRequest
		{
			Query = expand ? input.Query : null,
			Searches = expand ? null : PlainSubQueries.For(input.Query!),
			Intent = Trimmed(input.Intent),
			Collections = scope.Resolve(NonEmpty(input.Collections)) ?? defaults.DefaultCollections,
			Filters = filters.IsEmpty ? null : filters,
			Limit = input.Limit ?? defaults.DefaultLimit,
			MinScore = input.MinScore,
			CandidateLimit = input.CandidateLimit,
			Rerank = input.Rerank ?? defaults.RerankByDefault,
		};
	}

	private static Dictionary<string, string[]> Validate(SearchInput input, SearchOptions defaults)
	{
		Dictionary<string, string[]> errors = [];

		if (QueryProblem(input.Query) is string problem)
		{
			errors["query"] = [problem];
		}

		if (IsOutside(input.Limit, defaults.MaxLimit))
		{
			errors["limit"] = [$"limit must be between 1 and {defaults.MaxLimit}."];
		}

		if (input.MinScore is double minScore && (minScore < 0 || minScore > 1))
		{
			errors["minScore"] = ["minScore must be between 0 and 1."];
		}

		if (IsOutside(input.CandidateLimit, MaxCandidateLimit))
		{
			errors["candidateLimit"] = [$"candidateLimit must be between 1 and {MaxCandidateLimit}."];
		}

		return errors;
	}

	private static bool IsOutside(int? value, int max)
	{
		return value is int actual && (actual < 1 || actual > max);
	}

	private static string? QueryProblem(string? query)
	{
		if (string.IsNullOrWhiteSpace(query))
		{
			return "A non-empty query is required.";
		}

		if (query.Length > MaxQueryLength)
		{
			return $"The query may be at most {MaxQueryLength} characters.";
		}

		// The sidecar refuses a plain query with neither, and QMD would embed typed ones regardless.
		return PlainSubQueries.HasLetterOrDigit(query) ? null : "A search needs at least one letter or number.";
	}

	private static string? Trimmed(string? value)
	{
		return string.IsNullOrWhiteSpace(value) ? null : value;
	}

	private static IReadOnlyList<string>? NonEmpty(IReadOnlyList<string>? values)
	{
		if (values is null)
		{
			return null;
		}

		string[] kept = values.Where(static v => !string.IsNullOrWhiteSpace(v)).ToArray();
		return kept.Length == 0 ? null : kept;
	}
}
