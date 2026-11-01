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
	IReadOnlyList<string>? Tags = null);

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

		errors = [];

		if (string.IsNullOrWhiteSpace(input.Query))
		{
			errors["query"] = ["A non-empty query is required."];
		}
		else if (input.Query.Length > MaxQueryLength)
		{
			errors["query"] = [$"The query may be at most {MaxQueryLength} characters."];
		}

		if (input.Limit is int limit && (limit < 1 || limit > defaults.MaxLimit))
		{
			errors["limit"] = [$"limit must be between 1 and {defaults.MaxLimit}."];
		}

		if (input.MinScore is double minScore && (minScore < 0 || minScore > 1))
		{
			errors["minScore"] = ["minScore must be between 0 and 1."];
		}

		if (input.CandidateLimit is int candidates && (candidates < 1 || candidates > MaxCandidateLimit))
		{
			errors["candidateLimit"] = [$"candidateLimit must be between 1 and {MaxCandidateLimit}."];
		}

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

		return new SidecarSearchRequest
		{
			Query = input.Query,
			Intent = Trimmed(input.Intent),
			Collections = scope.Resolve(NonEmpty(input.Collections)) ?? defaults.DefaultCollections,
			Filters = filters.IsEmpty ? null : filters,
			Limit = input.Limit ?? defaults.DefaultLimit,
			MinScore = input.MinScore,
			CandidateLimit = input.CandidateLimit,
			Rerank = input.Rerank ?? defaults.RerankByDefault,
		};
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
