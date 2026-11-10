using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Api.Search;
using Memoria.Core.Sidecar;

namespace Memoria.Api.Tests;

/// <summary>Whether a search is expanded: the server's setting, unless the caller says otherwise.</summary>
public sealed class SearchExpansionTests
{
	[Fact]
	public void ExpansionIsOffByDefault()
	{
		Assert.False(new SearchOptions().ExpandByDefault);
	}

	[Theory]
	[InlineData(false, null, false)]
	[InlineData(false, true, true)]
	[InlineData(false, false, false)]
	[InlineData(true, null, true)]
	[InlineData(true, false, false)]
	[InlineData(true, true, true)]
	public void TryBuild_TurnsExpansionOffOnlyWhenTheSettingOrTheCallerSays(bool expandByDefault, bool? expand, bool expanded)
	{
		SidecarSearchRequest? request = SearchRequestFactory.TryBuild(
			new SearchInput("api  key auth", Expand: expand),
			new SearchOptions { ExpandByDefault = expandByDefault },
			new Everything(),
			out Dictionary<string, string[]> errors);

		Assert.Empty(errors);
		Assert.NotNull(request);
		// The query goes as it came either way. Only turning expansion off is sent.
		Assert.Equal("api  key auth", request.Query);
		Assert.Null(request.Searches);
		Assert.Equal(expanded ? null : false, request.Expand);
	}

	private sealed class Everything : ICallerScope
	{
		public IReadOnlyCollection<string>? Collections => null;

		public bool SeesBackendDetails => false;
	}
}
