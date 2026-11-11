using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Memoria.Api.Http;
using Memoria.Core.Contracts;

namespace Memoria.Api.Tests;

/// <summary>Scope-escape attempts against REST. <see cref="McpAuthTests"/> repeats the important ones over MCP.</summary>
public sealed class CollectionScopeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private const string Allowed = "claude-sessions";

	private const string OtherCollection = "codex-sessions";

	private const string OutOfScopeDocument = """
		{
		  "docid": "a1b2c3", "file": "codex-sessions/secret-plans.md", "collection": "codex-sessions", "path": "secret-plans.md",
		  "title": "Secret", "context": null, "modifiedAt": "2026-02-11T12:00:00.000Z", "bodyLength": 10, "body": "classified body", "metadata": null
		}
		""";

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private static string Sent(FakeSidecar.RecordedRequest request, string property)
	{
		using JsonDocument body = JsonDocument.Parse(request.Body!);
		return body.RootElement.GetProperty(property).GetRawText();
	}

	// --- search ---------------------------------------------------------------

	[Fact]
	public async Task Search_NamingNoCollection_IsNarrowedToTheAllowlist()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.SearchResponse);
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/v1/search?query=x")).StatusCode);

		Assert.Equal($"[\"{Allowed}\"]", Sent(factory.Sidecar.Single(), "collections"));
	}

	[Theory]
	[InlineData(OtherCollection)]
	[InlineData("does-not-exist")]
	[InlineData("CLAUDE-SESSIONS")]
	[InlineData("Claude-Sessions")]
	public async Task Search_NamingACollectionOutsideTheScope_IsRefused_WithoutAskingTheSidecar(string collection)
	{
		factory.Sidecar.Requests.Clear();
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		HttpResponseMessage response = await reader.GetAsync($"/v1/search?query=x&collection={collection}");

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Contains("collection_forbidden", await response.Content.ReadAsStringAsync());
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task Search_MixingAllowedAndForbidden_IsRefused()
	{
		factory.Sidecar.Requests.Clear();
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		HttpResponseMessage response = await reader.GetAsync($"/v1/search?query=x&collection={Allowed}&collection={OtherCollection}");

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task Search_RefusalDoesNotRevealWhetherTheCollectionExists()
	{
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		string existing = await (await reader.GetAsync($"/v1/search?query=x&collection={OtherCollection}")).Content.ReadAsStringAsync();
		string missing = await (await reader.GetAsync("/v1/search?query=x&collection=does-not-exist")).Content.ReadAsStringAsync();

		static string Shape(string body, string name)
		{
			using JsonDocument parsed = JsonDocument.Parse(body);
			return $"{parsed.RootElement.GetProperty("status")}|{parsed.RootElement.GetProperty("code")}|{parsed.RootElement.GetProperty("detail").GetString()!.Replace(name, "*", StringComparison.Ordinal)}";
		}

		Assert.Equal(Shape(existing, OtherCollection), Shape(missing, "does-not-exist"));
	}

	[Fact]
	public async Task Search_KeyWithEmptyAllowlist_ReturnsNothing_AndNeverCallsTheSidecar()
	{
		// The sidecar reads `collections: []` as "the default collections": sending it would be a scope escape.
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.SearchResponse);
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], []);
		HttpClient reader = AuthApiFactory.Authorized(factory.CreateClient(), token);

		HttpResponseMessage response = await reader.GetAsync("/v1/search?query=x");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		SearchResult result = (await response.Content.ReadFromJsonAsync<SearchResult>(Json))!;
		Assert.Empty(result.Results);
		Assert.Equal(0, result.Total);
		Assert.Empty(result.Query.Collections);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task EmptyAllowlist_SeesNothingOnAnyRoute()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		factory.Sidecar.On("GET", "/status", HttpStatusCode.OK, SidecarFixtures.Status);
		factory.Sidecar.On("POST", "/docs:multiGet", HttpStatusCode.OK, SidecarFixtures.MultiGet);
		factory.Sidecar.On("GET", "/docs/f6feba", HttpStatusCode.OK, SidecarFixtures.Document);
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], []);
		HttpClient reader = AuthApiFactory.Authorized(factory.CreateClient(), token);

		Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/v1/search?query=x&collection={Allowed}")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/v1/docs/f6feba")).StatusCode);
		MultiGetResult multi = (await (await reader.PostAsJsonAsync("/v1/docs:multiGet", new MultiGetRequest(null, "*/*.md", null, null, null, null), Json)).Content.ReadFromJsonAsync<MultiGetResult>(Json))!;
		Assert.Empty(multi.Docs);
		Assert.Empty(multi.Skipped);
		CollectionList collections = (await reader.GetFromJsonAsync<CollectionList>("/v1/collections", Json))!;
		Assert.Empty(collections.Collections);
		IndexStatus status = (await reader.GetFromJsonAsync<IndexStatus>("/v1/status", Json))!;
		Assert.Equal(0, status.TotalDocuments);
		Assert.Empty(status.Collections);
	}

	[Fact]
	public async Task Search_HitsAreCheckedAgain_EvenIfTheSidecarReturnsOtherCollections()
	{
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.SearchResponse);
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		HttpResponseMessage response = await reader.GetAsync("/v1/search?query=x");

		string raw = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain(OtherCollection, raw);
		SearchResult result = JsonSerializer.Deserialize<SearchResult>(raw, Json)!;
		SearchHit hit = Assert.Single(result.Results);
		Assert.Equal(Allowed, hit.Collection);
		Assert.Equal(1, result.Total);
		Assert.Equal(39, result.Query.Fetched);
		Assert.Equal([Allowed], result.Query.Collections);
	}

	[Fact]
	public async Task Search_AllCollectionsKey_DefersToDefaultsAndSeesEverything()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.SearchResponse);
		HttpClient everything = await factory.ClientWithScopesAsync([MemoriaScopes.Read], allCollections: true);

		SearchResult result = (await everything.GetFromJsonAsync<SearchResult>("/v1/search?query=x", Json))!;

		Assert.Equal(2, result.Results.Count);
		using JsonDocument sent = JsonDocument.Parse(factory.Sidecar.Single().Body!);
		Assert.False(sent.RootElement.TryGetProperty("collections", out _));
	}

	// --- single document --------------------------------------------------------

	[Theory]
	[InlineData("codex-sessions/secret-plans.md", "/docs/codex-sessions/secret-plans.md")]
	[InlineData("a1b2c3", "/docs/a1b2c3")]
	[InlineData("CODEX-SESSIONS/secret-plans.md", "/docs/CODEX-SESSIONS/secret-plans.md")]
	[InlineData("qmd://codex-sessions/secret-plans.md", "/docs/qmd%3A//codex-sessions/secret-plans.md")]
	[InlineData("codex-sessions%2Fsecret-plans.md", "/docs/codex-sessions/secret-plans.md")]
	[InlineData("codex-sessions%2fsecret-plans.md", "/docs/codex-sessions/secret-plans.md")]
	[InlineData("qmd%3A%2F%2Fcodex-sessions%2Fsecret-plans.md", "/docs/qmd%3A//codex-sessions/secret-plans.md")]
	public async Task GetDocument_ResolvingOutsideTheScope_Is404_ExactlyLikeAMiss(string reference, string sidecarPath)
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", sidecarPath, HttpStatusCode.OK, OutOfScopeDocument);
		factory.Sidecar.On("GET", "/docs/claude-sessions/no-such.md", HttpStatusCode.NotFound, """{"error":"not_found","message":"Document not found: claude-sessions/no-such.md","details":{"similarFiles":["codex-sessions/secret-plans.md"]}}""");
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		HttpResponseMessage outOfScope = await reader.GetAsync($"/v1/docs/{reference}");
		HttpResponseMessage miss = await reader.GetAsync("/v1/docs/claude-sessions/no-such.md");

		Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);
		string outOfScopeBody = await outOfScope.Content.ReadAsStringAsync();
		Assert.DoesNotContain("classified", outOfScopeBody);
		Assert.DoesNotContain("secret-plans", outOfScopeBody);
		Assert.Equal(WithoutTraceId(await miss.Content.ReadAsStringAsync()), WithoutTraceId(outOfScopeBody));
	}

	[Fact]
	public async Task GetDocument_InScope_IsReturned()
	{
		factory.Sidecar.On("GET", "/docs/f6feba", HttpStatusCode.OK, SidecarFixtures.Document);
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		Document document = (await reader.GetFromJsonAsync<Document>("/v1/docs/f6feba", Json))!;

		Assert.Equal(Allowed, document.Collection);
	}

	// A client generated from the spec encodes the slash in collection/path.md.
	[Theory]
	[InlineData("claude-sessions/a.md")]
	[InlineData("claude-sessions%2Fa.md")]
	[InlineData("claude-sessions%2fa.md")]
	public async Task GetDocument_SlashesMayBeEncoded(string reference)
	{
		factory.Sidecar.On("GET", "/docs/claude-sessions/a.md", HttpStatusCode.OK, SidecarFixtures.Document);
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/v1/docs/{reference}")).StatusCode);
	}

	[Fact]
	public async Task GetDocument_CollectionsDifferingOnlyByCase_AreDifferentCollections()
	{
		// QMD resolves `mixed/x.md` to canonical `Mixed/x.md`; the canonical name is what is checked.
		factory.Sidecar.On("GET", "/docs/mixed/x.md", HttpStatusCode.OK, """{"docid":"abc123","file":"Mixed/x.md","collection":"Mixed","path":"x.md","title":"X","context":null,"modifiedAt":"2026-02-11T12:00:00.000Z","bodyLength":1,"body":"x","metadata":null}""");
		HttpClient lower = await factory.ReaderOfAsync("mixed");
		HttpClient upper = await factory.ReaderOfAsync("Mixed");

		Assert.Equal(HttpStatusCode.NotFound, (await lower.GetAsync("/v1/docs/mixed/x.md")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await upper.GetAsync("/v1/docs/mixed/x.md")).StatusCode);
	}

	[Theory]
	[InlineData("""{"error":"not_found","message":"Document not found: codex-sessions/secret-plan.md","details":{"similarFiles":["codex-sessions/secret-plans.md"]}}""")]
	[InlineData("""{"error":"excluded_by_ignore","message":"Document is excluded by an ignore rule: codex-sessions/secret-plans.md","details":{"collection":"codex-sessions","path":"secret-plans.md","rule":"secret*"}}""")]
	public async Task GetDocument_SidecarErrorDetails_NeverReachAScopedCaller(string sidecarError)
	{
		factory.Sidecar.On("GET", "/docs/codex-sessions/secret-plan.md", HttpStatusCode.NotFound, sidecarError);
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		HttpResponseMessage response = await reader.GetAsync("/v1/docs/codex-sessions/secret-plan.md");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		string raw = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain("secret", raw);
		Assert.DoesNotContain("similarFiles", raw);
		Assert.DoesNotContain("upstreamStatus", raw);
		Assert.Contains("\"code\":\"not_found\"", raw);
	}

	[Fact]
	public async Task GetDocument_AllCollectionsKey_GetsNoSuggestions_EvenIfASidecarSendsThem()
	{
		factory.Sidecar.On("GET", "/docs/near-miss.md", HttpStatusCode.NotFound, """{"error":"not_found","message":"Document not found: near-miss.md","details":{"similarFiles":["codex-sessions/near-miss-2.md"]}}""");
		HttpClient everything = await factory.ClientWithScopesAsync([MemoriaScopes.Read], allCollections: true);

		string raw = await (await everything.GetAsync("/v1/docs/near-miss.md")).Content.ReadAsStringAsync();

		Assert.DoesNotContain("near-miss-2.md", raw);
		Assert.DoesNotContain("similarFiles", raw);
	}

	[Theory]
	[InlineData("claude-sessions/..%2f..%2fstatus")]
	[InlineData("..%2fstatus")]
	[InlineData("%2e%2e%2fstatus")]
	public async Task GetDocument_EncodedTraversal_NeverReachesAnotherSidecarRoute(string reference)
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/status", HttpStatusCode.OK, SidecarFixtures.Status);
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		HttpResponseMessage response = await reader.GetAsync($"/v1/docs/{reference}");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.DoesNotContain(factory.Sidecar.Requests, r => r.Uri.AbsolutePath == "/status");
	}

	// --- batch ------------------------------------------------------------------

	private const string CrossCollectionMultiGet = """
		{
		  "docs": [
		    { "docid": "f6feba", "file": "claude-sessions/a.md", "collection": "claude-sessions", "path": "a.md", "title": "A", "context": null, "modifiedAt": "2026-02-11T12:00:00.000Z", "bodyLength": 10, "body": "visible", "metadata": null },
		    { "docid": "a1b2c3", "file": "codex-sessions/b.md", "collection": "codex-sessions", "path": "b.md", "title": "B", "context": null, "modifiedAt": "2026-02-11T12:00:00.000Z", "bodyLength": 10, "body": "classified", "metadata": null }
		  ],
		  "skipped": [
		    { "file": "claude-sessions/huge.md", "reason": "File too large (100KB > 64KB)" },
		    { "file": "codex-sessions/huge-secret.md", "reason": "File too large (900KB > 64KB)" },
		    { "file": "no-separator.md", "reason": "File too large (900KB > 64KB)" }
		  ],
		  "errors": [
		    "File not found: nope.md (did you mean: codex-sessions/secret-plans.md?)",
		    "Ambiguous path b.md: qmd://claude-sessions/b.md, qmd://codex-sessions/b.md",
		    "No files matched pattern: x*"
		  ]
		}
		""";

	[Fact]
	public async Task MultiGet_CrossCollectionGlob_IsFilteredPerResult_InEveryList()
	{
		factory.Sidecar.On("POST", "/docs:multiGet", HttpStatusCode.OK, CrossCollectionMultiGet);
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		HttpResponseMessage response = await reader.PostAsJsonAsync("/v1/docs:multiGet", new MultiGetRequest(null, "*/*.md", null, null, null, null), Json);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string raw = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain("codex-sessions", raw);
		Assert.DoesNotContain("classified", raw);
		Assert.DoesNotContain("secret", raw);
		MultiGetResult result = JsonSerializer.Deserialize<MultiGetResult>(raw, Json)!;
		Assert.Equal("a.md", Assert.Single(result.Docs).Path);
		Assert.Equal("claude-sessions/huge.md", Assert.Single(result.Skipped).File);
		Assert.Empty(result.Errors);
	}

	[Fact]
	public async Task MultiGet_MissingAndOutOfScopeIds_AreIndistinguishable()
	{
		factory.Sidecar.On("POST", "/docs:multiGet", (request, body) => FakeSidecar.Json(
			HttpStatusCode.OK,
			body!.Contains("exists", StringComparison.Ordinal)
				? """{"docs":[{ "docid": "a1b2c3", "file": "codex-sessions/exists.md", "collection": "codex-sessions", "path": "exists.md", "title": "B", "context": null, "modifiedAt": "2026-02-11T12:00:00.000Z", "bodyLength": 10, "body": "x", "metadata": null }],"skipped":[],"errors":[]}"""
				: """{"docs":[],"skipped":[],"errors":["File not found: codex-sessions/missing.md"]}"""));
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		string exists = await (await reader.PostAsJsonAsync("/v1/docs:multiGet", new MultiGetRequest(["codex-sessions/exists.md"], null, null, null, null, null), Json)).Content.ReadAsStringAsync();
		string missing = await (await reader.PostAsJsonAsync("/v1/docs:multiGet", new MultiGetRequest(["codex-sessions/missing.md"], null, null, null, null, null), Json)).Content.ReadAsStringAsync();

		Assert.Equal(exists, missing);
	}

	[Fact]
	public async Task MultiGet_AllCollectionsKey_KeepsEverything()
	{
		factory.Sidecar.On("POST", "/docs:multiGet", HttpStatusCode.OK, CrossCollectionMultiGet);
		HttpClient everything = await factory.ClientWithScopesAsync([MemoriaScopes.Read], allCollections: true);

		HttpResponseMessage response = await everything.PostAsJsonAsync("/v1/docs:multiGet", new MultiGetRequest(null, "*/*.md", null, null, null, null), Json);

		MultiGetResult result = (await response.Content.ReadFromJsonAsync<MultiGetResult>(Json))!;
		Assert.Equal(2, result.Docs.Count);
		Assert.Equal(["claude-sessions/huge.md", "codex-sessions/huge-secret.md"], result.Skipped.Select(s => s.File).ToArray());
		Assert.Equal(3, result.Errors.Count);
	}

	// --- collections and status --------------------------------------------------

	[Fact]
	public async Task Collections_ShowOnlyTheAllowlist()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		string raw = await reader.GetStringAsync("/v1/collections");

		Assert.DoesNotContain(OtherCollection, raw);
		Assert.Equal(Allowed, Assert.Single(JsonSerializer.Deserialize<CollectionList>(raw, Json)!.Collections).Name);
	}

	[Fact]
	public async Task Status_ScopedCaller_GetsScopedTotals_AndNoGlobalNumbers()
	{
		factory.Sidecar.On("GET", "/status", HttpStatusCode.OK, SidecarFixtures.Status);
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		string raw = await reader.GetStringAsync("/v1/status");

		Assert.DoesNotContain("1502", raw);
		Assert.DoesNotContain(OtherCollection, raw);
		Assert.DoesNotContain("corpus", raw);
		IndexStatus status = JsonSerializer.Deserialize<IndexStatus>(raw, Json)!;
		Assert.Equal(1200, status.TotalDocuments);
		Assert.Null(status.NeedsEmbedding);
		Assert.Equal(Allowed, Assert.Single(status.Collections).Name);
	}

	[Fact]
	public async Task Status_AllCollectionsKey_GetsGlobalTotals()
	{
		factory.Sidecar.On("GET", "/status", HttpStatusCode.OK, SidecarFixtures.Status);
		HttpClient everything = await factory.ClientWithScopesAsync([MemoriaScopes.Read], allCollections: true);

		IndexStatus status = (await everything.GetFromJsonAsync<IndexStatus>("/v1/status", Json))!;

		Assert.Equal(1502, status.TotalDocuments);
		Assert.Equal(3, status.NeedsEmbedding);
		Assert.Equal(2, status.Collections.Count);
	}

	// --- backend failures --------------------------------------------------------

	[Fact]
	public async Task BackendFailure_NonAdminSeesNoAddressNoDetails()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.InternalServerError, """{"error":"internal","message":"SQLITE_ERROR at C:\\corpus\\index.sqlite","details":{"stack":"C:\\corpus"}}""");
		HttpClient reader = await factory.ClientWithScopesAsync([MemoriaScopes.Read], allCollections: true);

		HttpResponseMessage response = await reader.GetAsync("/v1/collections");

		Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
		string raw = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain("corpus", raw);
		Assert.DoesNotContain("SQLITE", raw);
		Assert.DoesNotContain("details", raw);
		Assert.Contains("\"code\":\"internal\"", raw);

		factory.Sidecar.Unreachable();
		string unreachable = await (await reader.GetAsync("/v1/collections")).Content.ReadAsStringAsync();
		Assert.DoesNotContain("sidecar.test", unreachable);
		Assert.Contains("sidecar_unreachable", unreachable);
	}

	[Fact]
	public async Task BackendFailure_UnparseableSuccessBody_HidesTheParserMessage()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, "{ this is not json: C:\\corpus }");
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		HttpResponseMessage response = await reader.GetAsync("/v1/collections");

		Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
		string raw = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain("not valid JSON", raw);
		Assert.DoesNotContain("corpus", raw);
	}

	[Fact]
	public async Task BackendFailure_AnUnboundAdminWithReadSeesTheFailure()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.InternalServerError, """{"error":"internal","message":"boom at sidecar","details":{"similarFiles":["codex-sessions/secret.md"]}}""");
		HttpClient admin = await factory.ClientWithScopesAsync([MemoriaScopes.Read, MemoriaScopes.Admin], allCollections: true);

		string raw = await (await admin.GetAsync("/v1/collections")).Content.ReadAsStringAsync();

		Assert.Contains("boom at sidecar", raw);
		Assert.Contains("upstreamStatus", raw);
	}

	[Fact]
	public async Task BackendFailure_IsHiddenFromAnAdminKeyBoundToCollections()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.InternalServerError, """{"error":"internal","message":"boom at sidecar","details":{"similarFiles":["codex-sessions/secret.md"]}}""");
		HttpClient scopedAdmin = await factory.ClientWithScopesAsync([MemoriaScopes.Read, MemoriaScopes.Admin], [Allowed]);

		string raw = await (await scopedAdmin.GetAsync("/v1/collections")).Content.ReadAsStringAsync();

		Assert.DoesNotContain("boom at sidecar", raw);
		Assert.DoesNotContain("upstreamStatus", raw);
		Assert.DoesNotContain("secret.md", raw);
	}

	[Fact]
	public async Task SidecarValidationErrors_StillReachAScopedCaller()
	{
		factory.Sidecar.On("POST", "/search", HttpStatusCode.BadRequest, """{"error":"unknown_collection","message":"Unknown collection(s): claude-sessions"}""");
		HttpClient reader = await factory.ReaderOfAsync(Allowed);

		HttpResponseMessage response = await reader.GetAsync("/v1/search?query=x");

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("Unknown collection(s): claude-sessions", await response.Content.ReadAsStringAsync());
	}

	private static string WithoutTraceId(string body)
	{
		using JsonDocument parsed = JsonDocument.Parse(body);
		return string.Join("|", parsed.RootElement.EnumerateObject().Where(static p => p.Name != "traceId").Select(static p => $"{p.Name}={p.Value}"));
	}
}
