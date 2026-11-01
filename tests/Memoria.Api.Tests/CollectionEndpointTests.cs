using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Memoria.Core.Contracts;

namespace Memoria.Api.Tests;

public sealed class CollectionEndpointTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	[Fact]
	public async Task Collections_ListNameCountAndContext_WithoutHostPaths()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/v1/collections");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string raw = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain("corpus", raw);
		Assert.DoesNotContain("pattern", raw);
		CollectionList list = JsonSerializer.Deserialize<CollectionList>(raw, Json)!;
		Assert.Equal(2, list.Collections.Count);
		Assert.Equal("claude-sessions", list.Collections[0].Name);
		Assert.Equal(1200, list.Collections[0].Documents);
		Assert.Equal("Claude Code transcripts", list.Collections[0].Context);
		Assert.True(list.Collections[0].IncludeByDefault);
		Assert.Null(list.Collections[1].Context);
		Assert.Null(list.Collections[1].LastModified);
	}

	[Fact]
	public async Task Status_ReportsIndexTotals()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/status", HttpStatusCode.OK, SidecarFixtures.Status);
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/v1/status");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		IndexStatus status = (await response.Content.ReadFromJsonAsync<IndexStatus>(Json))!;
		Assert.Equal(1502, status.TotalDocuments);
		Assert.Equal(3, status.NeedsEmbedding);
		Assert.True(status.HasVectorIndex);
		Assert.Equal(2, status.Collections.Count);
		Assert.Equal("codex-sessions", status.Collections[1].Name);
	}

	[Fact]
	public async Task Collections_SidecarDown_Is503Problem()
	{
		factory.Sidecar.Unreachable();
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/v1/collections");

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("sidecar_unreachable", body.RootElement.GetProperty("code").GetString());
		Assert.Equal("Search backend unavailable", body.RootElement.GetProperty("title").GetString());
	}

	[Fact]
	public async Task Collections_SidecarRejectsSecret_Is502Problem()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.Unauthorized, """{"error":"unauthorized","message":"Missing or invalid bearer token"}""");
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/v1/collections");

		Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("unauthorized", body.RootElement.GetProperty("code").GetString());
		Assert.Equal(401, body.RootElement.GetProperty("upstreamStatus").GetInt32());
	}

	[Fact]
	public async Task Collections_SidecarInternalError_Is502Problem()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.InternalServerError, """{"error":"internal","message":"boom","details":{"stack":"..."}}""");
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/v1/collections");

		Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("internal", body.RootElement.GetProperty("code").GetString());
		Assert.Equal("...", body.RootElement.GetProperty("details").GetProperty("stack").GetString());
	}

	[Fact]
	public async Task UnknownRoute_IsProblemDetails()
	{
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/v1/nothing-here");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
	}
}
