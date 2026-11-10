using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Memoria.Api.Endpoints;
using Memoria.Api.Http;
using static Memoria.Api.Tests.IngestKit;

namespace Memoria.Api.Tests;

/// <summary>
/// Who may use an ingest route is decided before what it reads: a key without the scope gets 403
/// whatever body it sends, and only a caller allowed in learns the route wanted another media type.
/// </summary>
public sealed class IngestContentTypeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	public static TheoryData<string, string> Routes => new()
	{
		{ "/v1/documents?collection=c", "application/json" },
		{ "/v1/ingest/rendered?collection=c&path=a.md&updated=2026-01-01T00%3A00%3A00Z", "text/markdown" },
		{ "/v1/ingest/session?collection=c&format=claude&sessionId=11111111-2222-3333-4444-555555555555&updated=2026-01-01T00%3A00%3A00Z", "application/x-ndjson" },
	};

	private static StringContent WrongType()
	{
		return new StringContent("not what this route reads", Encoding.UTF8, "text/plain");
	}

	[Theory]
	[MemberData(nameof(Routes))]
	public async Task A_read_key_gets_403_whatever_it_sends(string route, string _)
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient reader = await factory.ClientWithScopesAsync([MemoriaScopes.Read], ["c"]);

		HttpResponseMessage response = await reader.PostAsync(route, WrongType());

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Empty(corpus.Calls);
	}

	[Theory]
	[MemberData(nameof(Routes))]
	public async Task No_key_gets_401_whatever_it_sends(string route, string _)
	{
		HttpResponseMessage response = await factory.CreateClient().PostAsync(route, WrongType());

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Theory]
	[MemberData(nameof(Routes))]
	public async Task An_ingest_key_sending_the_wrong_media_type_gets_415_naming_it(string route, string expected)
	{
		FakeCorpus corpus = new(factory.Sidecar);
		HttpClient writer = await factory.ClientWithScopesAsync([MemoriaScopes.Ingest], ["c"]);

		HttpResponseMessage response = await writer.PostAsync(route, WrongType());

		Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
		Assert.Equal("unsupported_media_type", (await ReadAsync(response)).GetProperty("code").GetString());
		Assert.Contains(expected, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
		Assert.Empty(corpus.Calls);
	}

	[Theory]
	[MemberData(nameof(Routes))]
	public async Task The_right_media_type_with_parameters_passes_the_check(string route, string expected)
	{
		HttpClient writer = await factory.ClientWithScopesAsync([MemoriaScopes.Ingest], ["c"]);
		using StringContent body = new("{}");
		body.Headers.ContentType = MediaTypeHeaderValue.Parse($"{expected.ToUpperInvariant()}; charset=utf-8");

		HttpResponseMessage response = await writer.PostAsync(route, body);

		Assert.NotEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
	}

	[Theory]
	[InlineData(null, true)]
	[InlineData("", true)]
	[InlineData("text/markdown", true)]
	[InlineData("Text/Markdown; charset=utf-8", true)]
	[InlineData("text/plain", false)]
	[InlineData("text/markdownx", false)]
	[InlineData("not a media type", false)]
	public void Matches_ReadsTheMediaTypeAlone(string? sent, bool expected)
	{
		Assert.Equal(expected, IngestBody.Matches(sent, "text/markdown"));
	}
}
