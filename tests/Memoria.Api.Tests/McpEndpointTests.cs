using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Memoria.Api.Tests;

public sealed class McpEndpointTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	private async Task<McpClient> ConnectAsync()
	{
		HttpClient http = factory.CreateClient();
		HttpClientTransport transport = new(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			TransportMode = HttpTransportMode.StreamableHttp,
			Name = "memoria-tests",
		}, http);
		return await McpClient.CreateAsync(transport);
	}

	[Fact]
	public async Task Server_IdentifiesItselfAndListsTheFiveTools()
	{
		await using McpClient client = await ConnectAsync();

		Assert.Equal("memoria", client.ServerInfo!.Name);
		Assert.False(string.IsNullOrEmpty(client.ServerInstructions));

		IList<McpClientTool> tools = await client.ListToolsAsync();
		Assert.Equal(["get_document", "get_documents", "list_collections", "search", "status"], tools.Select(t => t.Name).Order().ToArray());

		McpClientTool search = tools.Single(t => t.Name == "search");
		Assert.Contains("hybrid", search.Description, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(["query"], search.JsonSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).ToArray());
		Assert.True(search.JsonSchema.GetProperty("properties").TryGetProperty("intent", out _));
		Assert.True(search.ProtocolTool.Annotations?.ReadOnlyHint);
	}

	[Fact]
	public async Task Search_UsesTheSameDefaultsAsRest_AndReturnsStructuredContent()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.SearchResponse);
		await using McpClient client = await ConnectAsync();

		CallToolResult result = await client.CallToolAsync("search", new Dictionary<string, object?>
		{
			["query"] = "api key auth",
			["intent"] = "asp.net core auth",
			["collections"] = new[] { "claude-sessions" },
			["project"] = new[] { "Receipts" },
		});

		Assert.NotEqual(true, result.IsError);
		Assert.NotNull(result.StructuredContent);
		JsonElement structured = result.StructuredContent!.Value;
		Assert.Equal(2, structured.GetProperty("total").GetInt32());
		Assert.Equal("f6feba", structured.GetProperty("results")[0].GetProperty("docid").GetString());
		Assert.Equal(42, structured.GetProperty("results")[0].GetProperty("line").GetInt32());
		string text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
		Assert.Contains("f6feba", text);

		FakeSidecar.RecordedRequest sent = Assert.Single(factory.Sidecar.Requests, r => r.Uri.AbsolutePath == "/search");
		using JsonDocument body = JsonDocument.Parse(sent.Body!);
		Assert.Equal("api key auth", body.RootElement.GetProperty("query").GetString());
		Assert.False(body.RootElement.GetProperty("expand").GetBoolean());
		Assert.Equal("asp.net core auth", body.RootElement.GetProperty("intent").GetString());
		Assert.True(body.RootElement.GetProperty("rerank").GetBoolean());
		Assert.Equal(10, body.RootElement.GetProperty("limit").GetInt32());
		Assert.Equal("claude-sessions", body.RootElement.GetProperty("collections")[0].GetString());
		Assert.Equal("Receipts", body.RootElement.GetProperty("filters").GetProperty("project")[0].GetString());
		Assert.True(sent.Headers.ContainsKey("request-id"));
	}

	[Fact]
	public async Task Search_ExpandTrue_SendsThePlainQuery()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.SearchResponse);
		await using McpClient client = await ConnectAsync();

		CallToolResult result = await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "api key auth", ["expand"] = true });

		Assert.NotEqual(true, result.IsError);
		FakeSidecar.RecordedRequest sent = Assert.Single(factory.Sidecar.Requests, r => r.Uri.AbsolutePath == "/search");
		using JsonDocument body = JsonDocument.Parse(sent.Body!);
		Assert.Equal("api key auth", body.RootElement.GetProperty("query").GetString());
		Assert.False(body.RootElement.TryGetProperty("expand", out _));
	}

	[Fact]
	public async Task Search_InvalidArguments_IsToolErrorNamingTheField()
	{
		factory.Sidecar.Requests.Clear();
		await using McpClient client = await ConnectAsync();

		CallToolResult result = await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "x", ["limit"] = 500 });

		Assert.True(result.IsError);
		string text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
		Assert.Contains("limit", text);
		Assert.DoesNotContain(factory.Sidecar.Requests, r => r.Uri.AbsolutePath == "/search");
	}

	[Fact]
	public async Task GetDocument_ForwardsRangeAndReturnsDocument()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/docs/claude-sessions/2026-02-11_2e39c784.md", HttpStatusCode.OK, SidecarFixtures.Document);
		await using McpClient client = await ConnectAsync();

		CallToolResult result = await client.CallToolAsync("get_document", new Dictionary<string, object?>
		{
			["reference"] = "claude-sessions/2026-02-11_2e39c784.md",
			["fromLine"] = 40,
			["maxLines"] = 5,
		});

		Assert.NotEqual(true, result.IsError);
		Assert.Equal("f6feba", result.StructuredContent!.Value.GetProperty("docid").GetString());
		Assert.Equal("# Receipts\n\nhello", result.StructuredContent.Value.GetProperty("body").GetString());
		FakeSidecar.RecordedRequest sent = Assert.Single(factory.Sidecar.Requests, r => r.Uri.AbsolutePath.StartsWith("/docs/", StringComparison.Ordinal));
		Assert.Equal("?fromLine=40&maxLines=5&similarFiles=false", sent.Uri.Query);
	}

	[Fact]
	public async Task SidecarErrors_SurfaceTheirCodeToTheModel()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/docs/nope.md", HttpStatusCode.NotFound, """{"error":"not_found","message":"Document not found: nope.md"}""");
		await using McpClient client = await ConnectAsync();

		CallToolResult result = await client.CallToolAsync("get_document", new Dictionary<string, object?> { ["reference"] = "nope.md" });

		Assert.True(result.IsError);
		string text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
		Assert.Contains("not_found", text);
		Assert.Contains("nope.md", text);
	}

	[Fact]
	public async Task ListCollections_And_Status_MapLikeRest()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		factory.Sidecar.On("GET", "/status", HttpStatusCode.OK, SidecarFixtures.Status);
		await using McpClient client = await ConnectAsync();

		CallToolResult collections = await client.CallToolAsync("list_collections", new Dictionary<string, object?>());
		CallToolResult status = await client.CallToolAsync("status", new Dictionary<string, object?>());

		JsonElement first = collections.StructuredContent!.Value.GetProperty("collections")[0];
		Assert.Equal(2, collections.StructuredContent.Value.GetProperty("collections").GetArrayLength());
		Assert.Equal("Claude Code transcripts", first.GetProperty("context").GetString());
		Assert.False(first.TryGetProperty("path", out _));
		Assert.Equal(1502, status.StructuredContent!.Value.GetProperty("totalDocuments").GetInt32());
	}

	[Fact]
	public async Task GetDocuments_RequiresIdsOrPattern()
	{
		await using McpClient client = await ConnectAsync();

		CallToolResult result = await client.CallToolAsync("get_documents", new Dictionary<string, object?>());

		Assert.True(result.IsError);
	}

	[Fact]
	public async Task Disabled_RemovesTheEndpoint()
	{
		using WebApplicationFactoryDerived disabled = new(factory);
		HttpClient http = disabled.CreateClient();

		HttpResponseMessage response = await http.PostAsync("/mcp", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
	}

	private sealed class WebApplicationFactoryDerived(MemoriaApiFactory parent) : IDisposable
	{
		private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory =
			parent.WithWebHostBuilder(builder => builder.UseSetting("Memoria:Mcp:Enabled", "false"));

		public HttpClient CreateClient()
		{
			return _factory.CreateClient();
		}

		public void Dispose()
		{
			_factory.Dispose();
		}
	}
}
