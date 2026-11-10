using System.Text.Json;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Memoria.Api.Search;
using Memoria.Core.Sidecar;

namespace Memoria.Api.Tests;

/// <summary>
/// The API half of searching without expansion. The sidecar's integration suite runs the same
/// cases through its own <c>plainQueries</c> on QMD's real validators, so the two cannot drift.
/// </summary>
public sealed class PlainSubQueriesTests
{
	private static readonly Lazy<JsonDocument> Cases = new(() =>
	{
		string? directory = AppContext.BaseDirectory;
		while (directory is not null && !File.Exists(Path.Combine(directory, "Memoria.slnx")))
		{
			directory = Path.GetDirectoryName(directory);
		}

		return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!, "sidecar", "test", "plain-query-cases.json")));
	});

	public static TheoryData<string, string[]> Shared()
	{
		TheoryData<string, string[]> data = [];
		foreach (JsonElement c in Cases.Value.RootElement.GetProperty("cases").EnumerateArray())
		{
			data.Add(c.GetProperty("query").GetString()!, c.GetProperty("searches").EnumerateArray().Select(Label).ToArray());
		}

		return data;
	}

	[Fact]
	public void TheSharedFileHasEnoughCasesToMeanSomething()
	{
		Assert.True(Cases.Value.RootElement.GetProperty("cases").GetArrayLength() >= 20);
	}

	[Theory]
	[MemberData(nameof(Shared))]
	public void For_GivesTheSidecarsAnswer(string query, string[] expected)
	{
		Assert.Equal(expected, PlainSubQueries.For(query).Select(static s => $"{s.Type}:{s.Query}").ToArray());
	}

	[Theory]
	[InlineData("")]
	[InlineData(" \t\n ")]
	[InlineData("﻿")]
	public void For_NothingButSpace_IsNoSubQuery(string query)
	{
		Assert.Empty(PlainSubQueries.For(query));
	}

	[Theory]
	[InlineData("api", true)]
	[InlineData("42", true)]
	[InlineData("日本", true)]
	[InlineData("\U0001D400", true)]
	[InlineData("\"!!\"", false)]
	[InlineData("--- ...", false)]
	[InlineData("🚀", false)]
	public void HasLetterOrDigit_ReadsCodePoints(string query, bool expected)
	{
		Assert.Equal(expected, PlainSubQueries.HasLetterOrDigit(query));
	}

	[Theory]
	[InlineData(false, null, false)]
	[InlineData(false, true, true)]
	[InlineData(true, null, true)]
	[InlineData(true, false, false)]
	public void TryBuild_ExpandsOnlyWhenTheSettingOrTheCallerAsks(bool expandByDefault, bool? expand, bool expanded)
	{
		SidecarSearchRequest? request = SearchRequestFactory.TryBuild(
			new SearchInput("api  key auth", Expand: expand),
			new SearchOptions { ExpandByDefault = expandByDefault },
			new Everything(),
			out Dictionary<string, string[]> errors);

		Assert.Empty(errors);
		Assert.NotNull(request);
		if (expanded)
		{
			Assert.Equal("api  key auth", request.Query);
			Assert.Null(request.Searches);
		}
		else
		{
			Assert.Null(request.Query);
			Assert.Equal(["lex:api key auth", "vec:api key auth"], request.Searches!.Select(static s => $"{s.Type}:{s.Query}").ToArray());
		}
	}

	[Fact]
	public void ExpansionIsOffByDefault()
	{
		Assert.False(new SearchOptions().ExpandByDefault);
	}

	private static string Label(JsonElement search)
	{
		return $"{search.GetProperty("type").GetString()}:{search.GetProperty("query").GetString()}";
	}

	private sealed class Everything : ICallerScope
	{
		public IReadOnlyCollection<string>? Collections => null;

		public bool SeesBackendDetails => false;
	}
}
