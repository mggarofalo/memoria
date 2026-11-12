using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Api.Search;
using Memoria.Core.Sidecar;

namespace Memoria.Api.Tests;

/// <summary>What REST and MCP both send the sidecar for a caller's search, and what they say when they refuse it.</summary>
public sealed class SearchRequestRulesTests
{
	private static readonly SearchOptions Defaults = new() { MaxLimit = 50, DefaultLimit = 10 };

	private sealed class Scope(IReadOnlyCollection<string>? collections) : ICallerScope
	{
		public IReadOnlyCollection<string>? Collections => collections;

		public bool SeesBackendDetails => true;
	}

	private static SidecarSearchRequest Accepted(SearchInput input, SearchOptions? defaults = null)
	{
		SidecarSearchRequest? request = SearchRequestFactory.TryBuild(input, defaults ?? Defaults, new Scope(null), out Dictionary<string, string[]> errors);
		Assert.Empty(errors);
		return Assert.IsType<SidecarSearchRequest>(request);
	}

	[Fact]
	public void Each_refused_field_gets_its_own_message()
	{
		SidecarSearchRequest? request = SearchRequestFactory.TryBuild(new SearchInput(" ", Limit: 0, MinScore: 1.5, CandidateLimit: 1001), Defaults, new Scope(null), out Dictionary<string, string[]> errors);

		Assert.Null(request);
		Assert.Equal(["A non-empty query is required."], errors["query"]);
		Assert.Equal(["limit must be between 1 and 50."], errors["limit"]);
		Assert.Equal(["minScore must be between 0 and 1."], errors["minScore"]);
		Assert.Equal(["candidateLimit must be between 1 and 1000."], errors["candidateLimit"]);
		Assert.Equal(4, errors.Count);
	}

	[Fact]
	public void A_query_one_character_too_long_is_refused()
	{
		SearchRequestFactory.TryBuild(new SearchInput(new string('q', SearchRequestFactory.MaxQueryLength + 1)), Defaults, new Scope(null), out Dictionary<string, string[]> errors);

		Assert.Equal(["The query may be at most 4000 characters."], Assert.Single(errors, e => e.Key == "query").Value);
	}

	[Fact]
	public void A_query_of_the_longest_length_is_accepted()
	{
		Assert.Equal(SearchRequestFactory.MaxQueryLength, Accepted(new SearchInput(new string('q', SearchRequestFactory.MaxQueryLength))).Query!.Length);
	}

	[Theory]
	[InlineData(1, 1000)]
	[InlineData(50, 1)]
	public void Limits_on_their_edges_are_accepted(int limit, int candidateLimit)
	{
		SidecarSearchRequest request = Accepted(new SearchInput("q", Limit: limit, CandidateLimit: candidateLimit));

		Assert.Equal(limit, request.Limit);
		Assert.Equal(candidateLimit, request.CandidateLimit);
	}

	[Theory]
	[InlineData(0.0)]
	[InlineData(1.0)]
	public void A_min_score_on_its_edge_is_accepted(double minScore)
	{
		Assert.Equal(minScore, Accepted(new SearchInput("q", MinScore: minScore)).MinScore);
	}

	[Fact]
	public void Named_collections_win_over_the_configured_default()
	{
		SearchOptions defaults = new() { MaxLimit = 50, DefaultLimit = 10, DefaultCollections = ["default-set"] };

		Assert.Equal(["asked-for"], Accepted(new SearchInput("q", Collections: ["asked-for"]), defaults).Collections!);
	}

	[Fact]
	public void With_no_collections_named_the_configured_default_is_searched()
	{
		SearchOptions defaults = new() { MaxLimit = 50, DefaultLimit = 10, DefaultCollections = ["default-set"] };

		Assert.Equal(["default-set"], Accepted(new SearchInput("q"), defaults).Collections!);
	}

	[Fact]
	public void Blank_text_and_blank_lists_are_not_sent()
	{
		SidecarSearchRequest request = Accepted(new SearchInput("q", Intent: "  ", Project: ["Receipts"], Since: " ", Tags: [" "]));

		Assert.Null(request.Intent);
		SidecarSearchFilters filters = Assert.IsType<SidecarSearchFilters>(request.Filters);
		Assert.Equal(["Receipts"], filters.Project!);
		Assert.Null(filters.Since);
		Assert.Null(filters.Tags);
	}

	[Fact]
	public void Every_argument_is_required()
	{
		SearchInput input = new("q");
		Scope scope = new(null);

		Assert.Throws<ArgumentNullException>(() => SearchRequestFactory.TryBuild(null!, Defaults, scope, out _));
		Assert.Throws<ArgumentNullException>(() => SearchRequestFactory.TryBuild(input, null!, scope, out _));
		Assert.Throws<ArgumentNullException>(() => SearchRequestFactory.TryBuild(input, Defaults, null!, out _));
	}
}
