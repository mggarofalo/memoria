using System.Net;
using System.Text;
using System.Text.Json;
using Memoria.Api.Http;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Memoria.Api.Tests;

public sealed class McpAuthTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private const string InitializeBody = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

	private static StringContent Rpc()
	{
		StringContent content = new(InitializeBody, Encoding.UTF8, "application/json");
		content.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
		return content;
	}

	private async Task<McpClient> ConnectAsync(string token)
	{
		HttpClient http = AuthApiFactory.Authorized(factory.CreateClient(), token);
		HttpClientTransport transport = new(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			TransportMode = HttpTransportMode.StreamableHttp,
			Name = "memoria-tests",
		}, http);
		return await McpClient.CreateAsync(transport);
	}

	private async Task<McpClient> ConnectAsReaderOfAsync(params string[] collections)
	{
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], collections);
		return await ConnectAsync(token);
	}

	private static string ErrorText(CallToolResult result)
	{
		return string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
	}

	[Fact]
	public async Task Anonymous_Is401()
	{
		HttpResponseMessage response = await factory.CreateClient().PostAsync("/mcp", Rpc());

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
	}

	[Fact]
	public async Task InvalidKey_Is401()
	{
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), "mem_nope");

		Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/mcp", Rpc())).StatusCode);
	}

	[Theory]
	[InlineData(MemoriaScopes.Admin)]
	[InlineData(MemoriaScopes.Ingest)]
	public async Task KeyWithoutReadScope_Is403(string scope)
	{
		HttpClient client = await factory.ClientWithScopesAsync([scope], allCollections: true);

		Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/mcp", Rpc())).StatusCode);
	}

	[Fact]
	public async Task TwoKeys_OnTheStatelessServer_GetTheirOwnScopes_Concurrently()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		await using McpClient claude = await ConnectAsReaderOfAsync("claude-sessions");
		await using McpClient codex = await ConnectAsReaderOfAsync("codex-sessions");

		// Interleave many calls: a scope captured once, or shared between callers, would show up as a wrong name.
		Task<string[]>[] calls = Enumerable.Range(0, 40).Select(async i =>
		{
			McpClient client = i % 2 == 0 ? claude : codex;
			CallToolResult result = await client.CallToolAsync("list_collections", new Dictionary<string, object?>());
			return result.StructuredContent!.Value.GetProperty("collections").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray();
		}).ToArray();
		string[][] seen = await Task.WhenAll(calls);

		for (int i = 0; i < seen.Length; i++)
		{
			Assert.Equal([i % 2 == 0 ? "claude-sessions" : "codex-sessions"], seen[i]);
		}
	}

	[Fact]
	public async Task TwoKeys_SearchSendsEachKeysOwnAllowlist_AndFiltersEachResult()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.SearchResponse);
		await using McpClient claude = await ConnectAsReaderOfAsync("claude-sessions");
		await using McpClient codex = await ConnectAsReaderOfAsync("codex-sessions");

		CallToolResult claudeResult = await claude.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "x" });
		CallToolResult codexResult = await codex.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "x" });

		Assert.Equal("f6feba", Assert.Single(claudeResult.StructuredContent!.Value.GetProperty("results").EnumerateArray()).GetProperty("docid").GetString());
		Assert.Equal("a1b2c3", Assert.Single(codexResult.StructuredContent!.Value.GetProperty("results").EnumerateArray()).GetProperty("docid").GetString());

		string[] sent = factory.Sidecar.Requests.Where(r => r.Uri.AbsolutePath == "/search").Select(r =>
		{
			using JsonDocument body = JsonDocument.Parse(r.Body!);
			return body.RootElement.GetProperty("collections").GetRawText();
		}).ToArray();
		Assert.Equal(["[\"claude-sessions\"]", "[\"codex-sessions\"]"], sent);
	}

	[Fact]
	public async Task Search_OutsideTheScope_IsAToolErrorWithoutCallingTheSidecar()
	{
		factory.Sidecar.Requests.Clear();
		await using McpClient client = await ConnectAsReaderOfAsync("claude-sessions");

		CallToolResult result = await client.CallToolAsync("search", new Dictionary<string, object?>
		{
			["query"] = "x",
			["collections"] = new[] { "CODEX-SESSIONS" },
		});

		Assert.True(result.IsError);
		Assert.Contains("outside this caller's scope", ErrorText(result));
		Assert.DoesNotContain(factory.Sidecar.Requests, r => r.Uri.AbsolutePath == "/search");
	}

	[Fact]
	public async Task Search_EmptyAllowlist_ReturnsNothing_WithoutCallingTheSidecar()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.SearchResponse);
		await using McpClient client = await ConnectAsReaderOfAsync();

		CallToolResult result = await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "x" });

		Assert.NotEqual(true, result.IsError);
		Assert.Equal(0, result.StructuredContent!.Value.GetProperty("results").GetArrayLength());
		Assert.DoesNotContain(factory.Sidecar.Requests, r => r.Uri.AbsolutePath == "/search");
	}

	[Fact]
	public async Task GetDocument_OutOfScopeAndMissing_AreTheSameError_WithNoSimilarFiles()
	{
		factory.Sidecar.On("GET", "/docs/a1b2c3", HttpStatusCode.OK, """{"docid":"a1b2c3","file":"codex-sessions/secret.md","collection":"codex-sessions","path":"secret.md","title":"S","context":null,"modifiedAt":"2026-02-11T12:00:00.000Z","bodyLength":1,"body":"classified","metadata":null}""");
		factory.Sidecar.On("GET", "/docs/nope.md", HttpStatusCode.NotFound, """{"error":"not_found","message":"Document not found: nope.md","details":{"similarFiles":["codex-sessions/secret.md"]}}""");
		await using McpClient client = await ConnectAsReaderOfAsync("claude-sessions");

		CallToolResult outOfScope = await client.CallToolAsync("get_document", new Dictionary<string, object?> { ["reference"] = "a1b2c3" });
		CallToolResult missing = await client.CallToolAsync("get_document", new Dictionary<string, object?> { ["reference"] = "nope.md" });

		Assert.True(outOfScope.IsError);
		Assert.True(missing.IsError);
		Assert.Equal(ErrorText(missing), ErrorText(outOfScope));
		Assert.Contains("not_found", ErrorText(missing));
		Assert.DoesNotContain("secret", ErrorText(missing));
		Assert.DoesNotContain("classified", ErrorText(outOfScope));
	}

	[Fact]
	public async Task GetDocument_DotSegments_NeverReachAnotherSidecarRoute()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/status", HttpStatusCode.OK, SidecarFixtures.Status);
		await using McpClient client = await ConnectAsReaderOfAsync("claude-sessions");

		foreach (string reference in new[] { "../status", "claude-sessions/../../status", "qmd://claude-sessions/../../../status", "./status" })
		{
			CallToolResult result = await client.CallToolAsync("get_document", new Dictionary<string, object?> { ["reference"] = reference });
			Assert.True(result.IsError);
			Assert.Contains("not_found", ErrorText(result));
		}

		Assert.DoesNotContain(factory.Sidecar.Requests, r => r.Uri.AbsolutePath == "/status");
	}

	[Fact]
	public async Task GetDocuments_FiltersEveryList()
	{
		factory.Sidecar.On("POST", "/docs:multiGet", HttpStatusCode.OK, """
			{
			  "docs": [
			    { "docid": "f6feba", "file": "claude-sessions/a.md", "collection": "claude-sessions", "path": "a.md", "title": "A", "context": null, "modifiedAt": "2026-02-11T12:00:00.000Z", "bodyLength": 10, "body": "visible", "metadata": null },
			    { "docid": "a1b2c3", "file": "codex-sessions/b.md", "collection": "codex-sessions", "path": "b.md", "title": "B", "context": null, "modifiedAt": "2026-02-11T12:00:00.000Z", "bodyLength": 10, "body": "classified", "metadata": null }
			  ],
			  "skipped": [{ "file": "codex-sessions/huge-secret.md", "reason": "File too large" }],
			  "errors": ["File not found: x.md (did you mean: codex-sessions/secret-plans.md?)"]
			}
			""");
		await using McpClient client = await ConnectAsReaderOfAsync("claude-sessions");

		CallToolResult result = await client.CallToolAsync("get_documents", new Dictionary<string, object?> { ["pattern"] = "*/*.md" });

		JsonElement structured = result.StructuredContent!.Value;
		Assert.Equal(1, structured.GetProperty("docs").GetArrayLength());
		Assert.Equal(0, structured.GetProperty("skipped").GetArrayLength());
		Assert.Equal(0, structured.GetProperty("errors").GetArrayLength());
		string raw = structured.GetRawText() + ErrorText(result);
		Assert.DoesNotContain("codex-sessions", raw);
		Assert.DoesNotContain("secret", raw);
	}

	[Fact]
	public async Task Status_ReportsScopedTotals()
	{
		factory.Sidecar.On("GET", "/status", HttpStatusCode.OK, SidecarFixtures.Status);
		await using McpClient client = await ConnectAsReaderOfAsync("claude-sessions");

		CallToolResult result = await client.CallToolAsync("status", new Dictionary<string, object?>());

		JsonElement status = result.StructuredContent!.Value;
		Assert.Equal(1200, status.GetProperty("totalDocuments").GetInt32());
		Assert.False(status.TryGetProperty("needsEmbedding", out _), "the MCP serializer omits nulls; a global backlog must not appear");
		Assert.DoesNotContain("1502", status.GetRawText());
		Assert.DoesNotContain("codex-sessions", status.GetRawText());
	}

	[Fact]
	public async Task SidecarFailure_ToolErrorHidesTheBackendAddress()
	{
		factory.Sidecar.Unreachable();
		await using McpClient client = await ConnectAsReaderOfAsync("claude-sessions");

		CallToolResult result = await client.CallToolAsync("list_collections", new Dictionary<string, object?>());

		Assert.True(result.IsError);
		Assert.Contains("sidecar_unreachable", ErrorText(result));
		Assert.DoesNotContain("sidecar.test", ErrorText(result));
	}
}
