using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Memoria.Core.Contracts;

namespace Memoria.Api.Tests;

public sealed class DocumentEndpointTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	[Fact]
	public async Task GetDocument_ByDisplayPath_KeepsSlashesAndForwardsRange()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/docs/claude-sessions/2026-02-11_2e39c784.md", HttpStatusCode.OK, SidecarFixtures.Document);
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/v1/docs/claude-sessions/2026-02-11_2e39c784.md?fromLine=40&maxLines=5&lineNumbers=true");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Document document = (await response.Content.ReadFromJsonAsync<Document>(Json))!;
		Assert.Equal("f6feba", document.Docid);
		Assert.Equal("# Receipts\n\nhello", document.Body);
		Assert.Equal("Receipts", document.Metadata!.Project);

		FakeSidecar.RecordedRequest sent = factory.Sidecar.Single();
		Assert.Equal("/docs/claude-sessions/2026-02-11_2e39c784.md", sent.Uri.AbsolutePath);
		Assert.Equal("?fromLine=40&maxLines=5&lineNumbers=true", sent.Uri.Query);
	}

	[Fact]
	public async Task GetDocument_ByDocid_WithBodyFalse()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/docs/f6feba", HttpStatusCode.OK, SidecarFixtures.Document);
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/v1/docs/f6feba?body=false");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("?body=false", factory.Sidecar.Single().Uri.Query);
	}

	[Fact]
	public async Task GetDocument_Missing_Is404ProblemWithSidecarCode()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/docs/nope.md", HttpStatusCode.NotFound, """{"error":"not_found","message":"Document not found: nope.md"}""");
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/v1/docs/nope.md");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(404, body.RootElement.GetProperty("status").GetInt32());
		Assert.Equal("not_found", body.RootElement.GetProperty("code").GetString());
		Assert.Equal("Document not found: nope.md", body.RootElement.GetProperty("detail").GetString());
		Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("traceId").GetString()));
	}

	[Fact]
	public async Task MultiGet_ForwardsBodyAndMapsResult()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/docs:multiGet", HttpStatusCode.OK, SidecarFixtures.MultiGet);
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.PostAsJsonAsync("/v1/docs:multiGet", new MultiGetRequest(["f6feba", "codex-sessions/b.md"], "claude-sessions/2026-02*.md", 4096, 50, false, true), Json);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		MultiGetResult result = (await response.Content.ReadFromJsonAsync<MultiGetResult>(Json))!;
		Assert.Equal(2, result.Docs.Count);
		Assert.Equal("exceeds maxBytes", Assert.Single(result.Skipped).Reason);
		Assert.Empty(result.Errors);

		FakeSidecar.RecordedRequest sent = factory.Sidecar.Single();
		Assert.Equal("/docs:multiGet", sent.Uri.AbsolutePath);
		using JsonDocument body = JsonDocument.Parse(sent.Body!);
		Assert.Equal(2, body.RootElement.GetProperty("ids").GetArrayLength());
		Assert.Equal("claude-sessions/2026-02*.md", body.RootElement.GetProperty("pattern").GetString());
		Assert.Equal(4096, body.RootElement.GetProperty("maxBytes").GetInt32());
		Assert.Equal(50, body.RootElement.GetProperty("maxLines").GetInt32());
		Assert.False(body.RootElement.GetProperty("lineNumbers").GetBoolean());
		Assert.True(body.RootElement.GetProperty("includeBody").GetBoolean());
	}

	[Fact]
	public async Task MultiGet_WithoutIdsOrPattern_Is400()
	{
		factory.Sidecar.Requests.Clear();
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.PostAsync("/v1/docs:multiGet", new StringContent("{}", Encoding.UTF8, "application/json"));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("ids", out _));
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task MultiGet_TooManyIds_Is400()
	{
		factory.Sidecar.Requests.Clear();
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.PostAsJsonAsync("/v1/docs:multiGet", new MultiGetRequest(Enumerable.Range(0, 201).Select(i => $"id{i}").ToArray(), null, null, null, null, null), Json);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Empty(factory.Sidecar.Requests);
	}
}
