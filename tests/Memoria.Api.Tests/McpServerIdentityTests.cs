using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;

namespace Memoria.Api.Tests;

/// <summary>
/// How the MCP server introduces itself. The server is set up once per host, so each test starts a host of its own rather than
/// sharing the fixture's.
/// </summary>
public sealed class McpServerIdentityTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	[Fact]
	public async Task The_server_gives_its_title_and_how_to_use_it()
	{
		using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(static _ => { });
		HttpClient http = host.CreateClient();
		await using McpClient client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			TransportMode = HttpTransportMode.StreamableHttp,
			Name = "memoria-tests",
		}, http));

		Assert.Equal("memoria", client.ServerInfo!.Name);
		Assert.Equal("Memoria", client.ServerInfo.Title);
		Assert.Equal(
			"Memoria searches the user's own accumulated record: Claude Code and Codex transcripts, issues, notes. "
			+ "Start with `search` (add `intent` for better ranking), then `get_document` with the hit's `line` as fromLine "
			+ "to read the passage. `list_collections` shows what is indexed. Reranked searches take a few seconds.",
			client.ServerInstructions);
	}

	/// <summary>Stateless: no session to keep, so any number of clients on any machine can share the endpoint without affinity.</summary>
	[Fact]
	public async Task Initializing_opens_no_session()
	{
		using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(static _ => { });
		using HttpRequestMessage initialize = new(HttpMethod.Post, "/mcp")
		{
			Content = new StringContent(
				"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"memoria-tests","version":"1.0"}}}""",
				Encoding.UTF8,
				"application/json"),
		};
		initialize.Headers.Accept.ParseAdd("application/json");
		initialize.Headers.Accept.ParseAdd("text/event-stream");

		using HttpResponseMessage response = await host.CreateClient().SendAsync(initialize);

		Assert.True(response.IsSuccessStatusCode, $"initialize answered {(int)response.StatusCode}");
		Assert.False(response.Headers.Contains("Mcp-Session-Id"));
	}
}
