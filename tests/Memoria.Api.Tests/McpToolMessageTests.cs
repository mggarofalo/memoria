using System.Net;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Memoria.Api.Tests;

/// <summary>What a model reads when a tool refuses or fails, word for word, and what the batch tool leaves out of its request.</summary>
public sealed class McpToolMessageTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	private async Task<McpClient> ConnectAsync()
	{
		HttpClient http = factory.CreateClient();
		return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			TransportMode = HttpTransportMode.StreamableHttp,
			Name = "memoria-tests",
		}, http));
	}

	private async Task<CallToolResult> CallAsync(string tool, Dictionary<string, object?> arguments)
	{
		await using McpClient client = await ConnectAsync();
		return await client.CallToolAsync(tool, arguments);
	}

	private static string Text(CallToolResult result)
	{
		return Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
	}

	[Fact]
	public async Task A_refused_search_names_each_field_with_its_message()
	{
		CallToolResult result = await CallAsync("search", new Dictionary<string, object?> { ["query"] = " ", ["limit"] = 500 });

		Assert.True(result.IsError);
		Assert.Equal("An error occurred invoking 'search': query: A non-empty query is required. limit: limit must be between 1 and 50.", Text(result));
	}

	[Fact]
	public async Task A_blank_reference_says_what_is_missing()
	{
		CallToolResult result = await CallAsync("get_document", new Dictionary<string, object?> { ["reference"] = " " });

		Assert.True(result.IsError);
		Assert.Equal("An error occurred invoking 'get_document': reference is required.", Text(result));
	}

	[Fact]
	public async Task A_refused_batch_names_each_field_with_its_message()
	{
		CallToolResult result = await CallAsync("get_documents", new Dictionary<string, object?> { ["maxBytes"] = 0 });

		Assert.True(result.IsError);
		Assert.Equal("An error occurred invoking 'get_documents': ids: Provide `ids`, `pattern`, or both. maxBytes: maxBytes must be between 1 and 1048576.", Text(result));
	}

	[Fact]
	public async Task A_sidecar_failure_without_a_retry_time_ends_at_its_message()
	{
		factory.Sidecar.On("GET", "/docs/gone.md", HttpStatusCode.NotFound, """{"error":"not_found","message":"Document not found: gone.md"}""");

		CallToolResult result = await CallAsync("get_document", new Dictionary<string, object?> { ["reference"] = "gone.md" });

		Assert.True(result.IsError);
		Assert.Equal("An error occurred invoking 'get_document': not_found: Document not found: gone.md", Text(result));
	}

	[Fact]
	public async Task An_empty_id_list_beside_a_pattern_is_not_sent()
	{
		JsonElement body = await MultiGetBodyAsync(new Dictionary<string, object?> { ["ids"] = Array.Empty<string>(), ["pattern"] = "claude-sessions/*.md" });

		Assert.False(body.TryGetProperty("ids", out _));
		Assert.Equal("claude-sessions/*.md", body.GetProperty("pattern").GetString());
	}

	[Fact]
	public async Task A_blank_pattern_beside_ids_is_not_sent()
	{
		JsonElement body = await MultiGetBodyAsync(new Dictionary<string, object?> { ["ids"] = new[] { "claude-sessions/a.md" }, ["pattern"] = "  " });

		Assert.False(body.TryGetProperty("pattern", out _));
		Assert.Equal("claude-sessions/a.md", body.GetProperty("ids")[0].GetString());
	}

	private async Task<JsonElement> MultiGetBodyAsync(Dictionary<string, object?> arguments)
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/docs:multiGet", HttpStatusCode.OK, SidecarFixtures.MultiGet);

		CallToolResult result = await CallAsync("get_documents", arguments);

		Assert.NotEqual(true, result.IsError);
		FakeSidecar.RecordedRequest sent = Assert.Single(factory.Sidecar.Requests, static r => r.Uri.AbsolutePath == "/docs:multiGet");
		using JsonDocument document = JsonDocument.Parse(sent.Body!);
		return document.RootElement.Clone();
	}
}
