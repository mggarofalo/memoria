using System.Net;
using System.Text.Json;

namespace Memoria.Api.Tests;

public sealed class OpenApiTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	[Fact]
	public async Task Document_IsServedWithEveryV1OperationNamed()
	{
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;

		Assert.StartsWith("3.0", root.GetProperty("openapi").GetString());
		Assert.Equal("Memoria API", root.GetProperty("info").GetProperty("title").GetString());
		JsonElement server = Assert.Single(root.GetProperty("servers").EnumerateArray());
		Assert.Equal("/", server.GetProperty("url").GetString());

		JsonElement paths = root.GetProperty("paths");
		Dictionary<string, string> operations = [];
		foreach (JsonProperty path in paths.EnumerateObject())
		{
			foreach (JsonProperty method in path.Value.EnumerateObject())
			{
				operations[$"{method.Name.ToUpperInvariant()} {path.Name}"] = method.Value.GetProperty("operationId").GetString()!;
				Assert.True(method.Value.TryGetProperty("summary", out _), $"{method.Name} {path.Name} has no summary");
				Assert.True(method.Value.TryGetProperty("tags", out _), $"{method.Name} {path.Name} has no tags");
			}
		}

		Assert.DoesNotContain(operations.Keys, key => key.Contains("/mcp", StringComparison.Ordinal));
		Assert.Equal("search", operations["GET /v1/search"]);
		Assert.Equal("getDocument", operations["GET /v1/docs/{id}"]);
		Assert.Equal("getDocuments", operations["POST /v1/docs:multiGet"]);
		Assert.Equal("listCollections", operations["GET /v1/collections"]);
		Assert.Equal("getStatus", operations["GET /v1/status"]);
		Assert.Equal("health", operations["GET /health"]);
		Assert.Equal("ready", operations["GET /ready"]);
		Assert.Equal("createApiKey", operations["POST /v1/admin/keys"]);
		Assert.Equal("listApiKeys", operations["GET /v1/admin/keys"]);
		Assert.Equal("revokeApiKey", operations["DELETE /v1/admin/keys/{id}"]);
		Assert.Equal("rotateApiKey", operations["POST /v1/admin/keys/{id}:rotate"]);
	}

	[Fact]
	public async Task Security_IsDeclaredOnScopedOperations_AndNotOnAnonymousOnes()
	{
		HttpClient client = factory.CreateClient();

		using JsonDocument document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
		JsonElement root = document.RootElement;

		JsonElement scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("apiKey");
		Assert.Equal("http", scheme.GetProperty("type").GetString());
		Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());

		JsonElement paths = root.GetProperty("paths");
		Assert.True(paths.GetProperty("/v1/search").GetProperty("get").TryGetProperty("security", out _));
		Assert.Contains("`read` scope", paths.GetProperty("/v1/search").GetProperty("get").GetProperty("description").GetString());
		Assert.True(paths.GetProperty("/v1/admin/keys").GetProperty("post").TryGetProperty("security", out _));
		Assert.Contains("`admin` scope", paths.GetProperty("/v1/admin/keys").GetProperty("post").GetProperty("description").GetString());
		Assert.False(paths.GetProperty("/health").GetProperty("get").TryGetProperty("security", out _));
		Assert.False(paths.GetProperty("/ready").GetProperty("get").TryGetProperty("security", out _));

		JsonElement searchResponses = paths.GetProperty("/v1/search").GetProperty("get").GetProperty("responses");
		Assert.True(searchResponses.TryGetProperty("401", out _));
		Assert.True(searchResponses.TryGetProperty("403", out _));
		Assert.True(searchResponses.TryGetProperty("429", out _));
	}

	[Fact]
	public async Task IssuedKeyFields_AppearOnlyOnIssuedApiKey_NotInTheListSchema()
	{
		HttpClient client = factory.CreateClient();

		using JsonDocument document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
		JsonElement schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

		Assert.True(schemas.GetProperty("IssuedApiKey").GetProperty("properties").TryGetProperty("key", out _));
		Assert.False(schemas.GetProperty("ApiKeyInfo").GetProperty("properties").TryGetProperty("key", out _));
		Assert.False(schemas.GetProperty("ApiKeyInfo").GetProperty("properties").TryGetProperty("sha256Hash", out _));
	}

	[Fact]
	public async Task Search_DeclaresQueryParametersAndProblemResponses()
	{
		HttpClient client = factory.CreateClient();

		using JsonDocument document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
		JsonElement search = document.RootElement.GetProperty("paths").GetProperty("/v1/search").GetProperty("get");

		HashSet<string> parameters = search.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToHashSet();
		Assert.Superset(new HashSet<string> { "query", "collection", "limit", "minScore", "rerank", "intent", "project", "branch", "since", "until", "tag", "candidateLimit" }, parameters);

		JsonElement responses = search.GetProperty("responses");
		Assert.True(responses.TryGetProperty("200", out _));
		Assert.True(responses.TryGetProperty("400", out _));
		Assert.True(responses.TryGetProperty("502", out _));
		Assert.True(responses.TryGetProperty("503", out _));
		Assert.True(responses.TryGetProperty("504", out _));
	}
}
