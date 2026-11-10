using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Memoria.Core.Contracts;

namespace Memoria.Api.Tests;

public sealed class SearchEndpointTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private HttpClient Client()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.SearchResponse);
		return factory.CreateClient();
	}

	[Fact]
	public async Task Search_DefaultsToRerankedHybridWithConfiguredLimit()
	{
		HttpClient client = Client();

		HttpResponseMessage response = await client.GetAsync("/v1/search?query=api%20key%20auth");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		SearchResult result = (await response.Content.ReadFromJsonAsync<SearchResult>(Json))!;
		Assert.Equal(2, result.Total);
		Assert.Equal(3700, result.TookMs);
		Assert.Equal("f6feba", result.Results[0].Docid);
		Assert.Equal(42, result.Results[0].Line);
		Assert.Equal("Receipts", result.Results[0].Metadata!.Project);
		Assert.Null(result.Results[1].Metadata);
		Assert.True(result.Query.Rerank);

		FakeSidecar.RecordedRequest sent = factory.Sidecar.Single();
		Assert.Equal("/search", sent.Uri.AbsolutePath);
		using JsonDocument body = JsonDocument.Parse(sent.Body!);
		JsonElement root = body.RootElement;
		// Unexpanded by default: the plain query, with expansion turned off for it.
		Assert.Equal("api key auth", root.GetProperty("query").GetString());
		Assert.False(root.GetProperty("expand").GetBoolean());
		Assert.True(root.GetProperty("rerank").GetBoolean());
		Assert.Equal(10, root.GetProperty("limit").GetInt32());
		Assert.False(root.TryGetProperty("collections", out _));
		Assert.False(root.TryGetProperty("filters", out _));
		Assert.False(root.TryGetProperty("minScore", out _));
		Assert.False(root.TryGetProperty("intent", out _));
		Assert.False(root.TryGetProperty("searches", out _));
	}

	[Fact]
	public async Task Search_ExpandTrue_LeavesExpandOut_SoAnySidecarTakesIt()
	{
		HttpClient client = Client();

		HttpResponseMessage response = await client.GetAsync("/v1/search?query=api%20key%20auth&expand=true");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(factory.Sidecar.Single().Body!);
		Assert.Equal("api key auth", body.RootElement.GetProperty("query").GetString());
		Assert.False(body.RootElement.TryGetProperty("expand", out _));
	}

	[Fact]
	public async Task Search_ExpandFalse_KeepsEverythingElseAsItWas()
	{
		HttpClient client = Client();

		HttpResponseMessage response = await client.GetAsync("/v1/search?query=docker%20-compose&expand=false&intent=deploy&rerank=false");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(factory.Sidecar.Single().Body!);
		Assert.Equal("docker -compose", body.RootElement.GetProperty("query").GetString());
		Assert.False(body.RootElement.GetProperty("expand").GetBoolean());
		Assert.Equal("deploy", body.RootElement.GetProperty("intent").GetString());
		Assert.False(body.RootElement.GetProperty("rerank").GetBoolean());
	}

	[Fact]
	public async Task Search_ForwardsEveryParameterAndFilter()
	{
		HttpClient client = Client();

		HttpResponseMessage response = await client.GetAsync(
			"/v1/search?query=auth&collection=claude-sessions&collection=codex-sessions&limit=5&minScore=0.25&rerank=false"
			+ "&intent=asp.net%20core%20auth&candidateLimit=80&project=Receipts&project=Memoria&branch=main&since=2026-01-01&until=2026-02-01&tag=a&tag=b");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(factory.Sidecar.Single().Body!);
		JsonElement root = body.RootElement;
		Assert.Equal(["claude-sessions", "codex-sessions"], root.GetProperty("collections").EnumerateArray().Select(c => c.GetString()!).ToArray());
		Assert.Equal(5, root.GetProperty("limit").GetInt32());
		Assert.Equal(0.25, root.GetProperty("minScore").GetDouble());
		Assert.False(root.GetProperty("rerank").GetBoolean());
		Assert.Equal("asp.net core auth", root.GetProperty("intent").GetString());
		Assert.Equal(80, root.GetProperty("candidateLimit").GetInt32());
		JsonElement filters = root.GetProperty("filters");
		Assert.Equal(["Receipts", "Memoria"], filters.GetProperty("project").EnumerateArray().Select(c => c.GetString()!).ToArray());
		Assert.Equal(["main"], filters.GetProperty("branch").EnumerateArray().Select(c => c.GetString()!).ToArray());
		Assert.Equal("2026-01-01", filters.GetProperty("since").GetString());
		Assert.Equal("2026-02-01", filters.GetProperty("until").GetString());
		Assert.Equal(["a", "b"], filters.GetProperty("tags").EnumerateArray().Select(c => c.GetString()!).ToArray());
	}

	[Theory]
	[InlineData("/v1/search", "query")]
	[InlineData("/v1/search?query=", "query")]
	[InlineData("/v1/search?query=%20%20", "query")]
	[InlineData("/v1/search?query=x&limit=0", "limit")]
	[InlineData("/v1/search?query=x&limit=51", "limit")]
	[InlineData("/v1/search?query=x&minScore=1.5", "minScore")]
	[InlineData("/v1/search?query=x&candidateLimit=0", "candidateLimit")]
	public async Task Search_RejectsInvalidParametersWithValidationProblem(string url, string field)
	{
		HttpClient client = Client();

		HttpResponseMessage response = await client.GetAsync(url);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.True(body.RootElement.GetProperty("errors").TryGetProperty(field, out _), body.RootElement.ToString());
		Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("traceId").GetString()));
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task Search_NonNumericLimit_Is400NotCrash()
	{
		HttpClient client = Client();

		HttpResponseMessage response = await client.GetAsync("/v1/search?query=x&limit=ten");

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task Search_SidecarValidationError_PassesThroughAs400()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/search", HttpStatusCode.BadRequest, """{"error":"validation","message":"body/collections/0 must NOT have fewer than 1 characters"}""");
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/v1/search?query=x");

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("validation", body.RootElement.GetProperty("code").GetString());
		Assert.Equal(400, body.RootElement.GetProperty("upstreamStatus").GetInt32());
		Assert.Contains("fewer than 1", body.RootElement.GetProperty("detail").GetString());
	}
}
