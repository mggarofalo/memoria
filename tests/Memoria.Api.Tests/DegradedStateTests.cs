using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Memoria.Api.Http;
using Memoria.Core.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Memoria.Api.Tests;

public sealed class DegradedStateTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private async Task<McpClient> ConnectAsync(params string[] collections)
	{
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], collections);
		HttpClient http = AuthApiFactory.Authorized(factory.CreateClient(), token);
		return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			TransportMode = HttpTransportMode.StreamableHttp,
			Name = "memoria-tests",
		}, http));
	}

	// --- search ---------------------------------------------------------------

	[Fact]
	public async Task Rest_ScopedCaller_SeesTheState_AndOnlyContractTokens()
	{
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.DegradedSearchResponse);
		HttpClient reader = await factory.ReaderOfAsync("claude-sessions");

		string raw = await reader.GetStringAsync("/v1/search?query=x");

		SearchResult result = JsonSerializer.Deserialize<SearchResult>(raw, Json)!;
		Assert.True(result.Degraded);
		Assert.Equal([DegradedReason.NoRerank, DegradedReason.NoExpansion], result.DegradedReasons);
		Assert.DoesNotContain("192.168.1.30", raw);
		Assert.DoesNotContain("worker", raw);
	}

	[Fact]
	public async Task Rest_HealthySearch_IsNotDegraded_EvenFromASidecarThatOmitsTheFields()
	{
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.SearchResponse);
		HttpClient reader = await factory.ReaderOfAsync("claude-sessions");

		SearchResult result = (await reader.GetFromJsonAsync<SearchResult>("/v1/search?query=x", Json))!;

		Assert.False(result.Degraded);
		Assert.Empty(result.DegradedReasons);
	}

	[Fact]
	public async Task Rest_EmptyAllowlist_IsNotDegraded()
	{
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], []);
		HttpClient reader = AuthApiFactory.Authorized(factory.CreateClient(), token);

		SearchResult result = (await reader.GetFromJsonAsync<SearchResult>("/v1/search?query=x", Json))!;

		Assert.False(result.Degraded);
		Assert.Empty(result.DegradedReasons);
	}

	[Fact]
	public async Task Mcp_SearchTool_CarriesTheState_AndOnlyContractTokens()
	{
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.DegradedSearchResponse);
		await using McpClient client = await ConnectAsync("claude-sessions");

		CallToolResult result = await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "x" });

		JsonElement structured = result.StructuredContent!.Value;
		Assert.True(structured.GetProperty("degraded").GetBoolean());
		Assert.Equal(["no_rerank", "no_expansion"], structured.GetProperty("degradedReasons").EnumerateArray().Select(static r => r.GetString()!).ToArray());
		Assert.DoesNotContain("192.168.1.30", structured.GetRawText());
		Assert.Contains("no_rerank", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
	}

	// --- /ready ---------------------------------------------------------------

	[Fact]
	public async Task Ready_LosingWorkers_Is200AndDegraded_NotOutOfRotation()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.DegradedReadiness);

		HttpResponseMessage response = await factory.CreateClient().GetAsync("/ready");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string raw = await response.Content.ReadAsStringAsync();
		ReadinessReport report = JsonSerializer.Deserialize<ReadinessReport>(raw, Json)!;
		Assert.Equal(ReadinessStatus.Degraded, report.Status);
		Assert.True(report.Ready);
		Assert.Null(report.Reason);
		Assert.Null(report.Sidecar);
		foreach (string detail in new[] { "192.168.1.30", "refused", "stuck", "index.sqlite", "documentsSetAside", "rerank" })
		{
			Assert.DoesNotContain(detail, raw);
		}

		Assert.Equal(string.Empty, factory.Sidecar.Single().Uri.Query);
	}

	[Theory]
	[InlineData("/ready?strict=true", "ready", HttpStatusCode.OK)]
	[InlineData("/ready?strict=false", "degraded", HttpStatusCode.OK)]
	[InlineData("/ready?strict=true", "degraded", HttpStatusCode.ServiceUnavailable)]
	[InlineData("/ready?strict=true", "warming", HttpStatusCode.ServiceUnavailable)]
	public async Task Ready_Strict_AnswersOnlyForFullyReady(string url, string sidecarStatus, HttpStatusCode expected)
	{
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.Readiness(sidecarStatus, resident: sidecarStatus == "ready"));

		HttpResponseMessage response = await factory.CreateClient().GetAsync(url);

		Assert.Equal(expected, response.StatusCode);
		ReadinessReport report = (await response.Content.ReadFromJsonAsync<ReadinessReport>(Json))!;
		Assert.Equal(sidecarStatus, report.Status);
		Assert.Equal(expected == HttpStatusCode.OK, report.Ready);
	}

	[Fact]
	public async Task Ready_SidecarUnreachable_Is503()
	{
		factory.Sidecar.Unreachable();

		Assert.Equal(HttpStatusCode.ServiceUnavailable, (await factory.CreateClient().GetAsync("/ready")).StatusCode);
	}

	[Fact]
	public async Task Ready_NonAdminKey_GetsTheStatusWordAlone()
	{
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.DegradedReadiness);
		HttpClient reader = await factory.ClientWithScopesAsync([MemoriaScopes.Read], allCollections: true);

		string raw = await reader.GetStringAsync("/ready");

		ReadinessReport report = JsonSerializer.Deserialize<ReadinessReport>(raw, Json)!;
		Assert.Equal(ReadinessStatus.Degraded, report.Status);
		Assert.Null(report.Sidecar);
		Assert.DoesNotContain("documentsSetAside", raw);
		Assert.DoesNotContain("192.168.1.30", raw);
	}

	[Fact]
	public async Task Ready_AdminKey_GetsStuckJobsAndDocumentsSetAside()
	{
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.DegradedReadiness);
		HttpClient admin = await factory.AdminAsync();

		ReadinessReport report = (await admin.GetFromJsonAsync<ReadinessReport>("/ready", Json))!;

		Assert.Equal(ReadinessStatus.Degraded, report.Status);
		Assert.Equal("a job is waiting to retry after 3 attempts: worker 192.168.1.30 refused the batch", report.Reason);
		Assert.Equal(3, report.Sidecar!.Jobs.Stuck!.Attempts);
		Assert.Equal(4, report.Sidecar.Llm!.DocumentsSetAside);
		Assert.Equal(["rerank"], report.Sidecar.Llm.Unavailable);
	}

	// --- /v1/status -----------------------------------------------------------

	private const string StatusWithSetAside = """
		{
		  "totalDocuments": 1502,
		  "needsEmbedding": 3,
		  "documentsSetAside": 4,
		  "hasVectorIndex": true,
		  "collections": [{ "name": "claude-sessions", "path": null, "pattern": null, "documents": 1200, "lastUpdated": "2026-11-01T01:00:00.000Z" }]
		}
		""";

	[Fact]
	public async Task Status_DocumentsSetAside_IsForAdminKeysOnly()
	{
		factory.Sidecar.On("GET", "/status", HttpStatusCode.OK, StatusWithSetAside);
		HttpClient reader = await factory.ClientWithScopesAsync([MemoriaScopes.Read], allCollections: true);
		HttpClient admin = await factory.ClientWithScopesAsync([MemoriaScopes.Read, MemoriaScopes.Admin], allCollections: true);

		Assert.Null((await reader.GetFromJsonAsync<IndexStatus>("/v1/status", Json))!.DocumentsSetAside);
		Assert.Equal(4, (await admin.GetFromJsonAsync<IndexStatus>("/v1/status", Json))!.DocumentsSetAside);
	}
}
