using System.Net;
using System.Text.Json;
using Memoria.Core.Sidecar;

namespace Memoria.Core.Tests;

public sealed class MemoriaQmdClientTests
{
	private static readonly Uri BaseAddress = new("http://sidecar.test:8282/");

	private static (MemoriaQmdClient Client, FakeHttpHandler Handler) Create(TimeSpan? timeout = null)
	{
		FakeHttpHandler handler = new();
		HttpClient http = new(handler) { BaseAddress = BaseAddress, Timeout = timeout ?? TimeSpan.FromSeconds(10) };
		return (new MemoriaQmdClient(http), handler);
	}

	[Theory]
	[InlineData("claude-sessions/2026-02-11_alpha.md", "docs/claude-sessions/2026-02-11_alpha.md")]
	[InlineData("abc123", "docs/abc123")]
	[InlineData("#abc123", "docs/%23abc123")]
	[InlineData("qmd://claude-sessions/2026-02-11_alpha.md", "docs/qmd%3A//claude-sessions/2026-02-11_alpha.md")]
	[InlineData("notes/a b&c.md", "docs/notes/a%20b%26c.md")]
	public void DocumentPath_EncodesEverythingButSlashes(string reference, string expected)
	{
		Assert.Equal(expected, MemoriaQmdClient.DocumentPath(reference, null));
	}

	[Fact]
	public void DocumentPath_PutsLineRangeInQueryString()
	{
		string path = MemoriaQmdClient.DocumentPath("a/b.md", new SidecarDocumentQuery(FromLine: 42, MaxLines: 10, LineNumbers: true, Body: false));

		Assert.Equal("docs/a/b.md?fromLine=42&maxLines=10&lineNumbers=true&body=false", path);
	}

	[Fact]
	public async Task Search_PostsCamelCaseBodyWithoutNulls()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.OK, Fixtures.SearchResponse);

		SidecarSearchResponse response = await client.SearchAsync(new SidecarSearchRequest
		{
			Query = "api key auth",
			Collections = ["claude-sessions"],
			Filters = new SidecarSearchFilters(Project: ["Receipts"], Since: "2026-01-01"),
			Limit = 5,
			Rerank = false,
		});

		FakeHttpHandler.RecordedRequest request = Assert.Single(handler.Requests);
		Assert.Equal(HttpMethod.Post, request.Method);
		Assert.Equal(new Uri(BaseAddress, "search"), request.Uri);
		using JsonDocument body = JsonDocument.Parse(request.Body!);
		JsonElement root = body.RootElement;
		Assert.Equal("api key auth", root.GetProperty("query").GetString());
		Assert.Equal(5, root.GetProperty("limit").GetInt32());
		Assert.False(root.GetProperty("rerank").GetBoolean());
		Assert.Equal("Receipts", root.GetProperty("filters").GetProperty("project")[0].GetString());
		Assert.Equal("2026-01-01", root.GetProperty("filters").GetProperty("since").GetString());
		Assert.False(root.TryGetProperty("intent", out _));
		Assert.False(root.TryGetProperty("searches", out _));
		Assert.False(root.TryGetProperty("minScore", out _));
		Assert.False(root.GetProperty("filters").TryGetProperty("branch", out _));

		SidecarSearchHit hit = Assert.Single(response.Results);
		Assert.Equal("f6feba", hit.Docid);
		Assert.Equal(0.91, hit.Score);
		Assert.Equal(42, hit.Line);
		Assert.Equal("Receipts", hit.Metadata!.Project);
		Assert.Equal(["project/Receipts", "branch/master"], hit.Metadata.Tags);
		Assert.True(response.Query.Filtered);
	}

	[Fact]
	public async Task MultiGet_ResolvesColonPathAgainstBaseAddress()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.OK, """{"docs":[],"skipped":[{"file":"a/b.md","reason":"too large"}],"errors":[]}""");

		SidecarMultiGetResponse response = await client.MultiGetAsync(new SidecarMultiGetRequest { Ids = ["abc123"], MaxBytes = 1024 });

		FakeHttpHandler.RecordedRequest request = Assert.Single(handler.Requests);
		Assert.Equal("http://sidecar.test:8282/docs:multiGet", request.Uri.ToString());
		Assert.Contains("\"ids\":[\"abc123\"]", request.Body);
		Assert.Contains("\"maxBytes\":1024", request.Body);
		Assert.Equal("too large", Assert.Single(response.Skipped).Reason);
	}

	[Fact]
	public async Task GetDocument_UsesEncodedReferenceAndQuery()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.OK, Fixtures.Document);

		SidecarDocument document = await client.GetDocumentAsync("#f6feba", new SidecarDocumentQuery(FromLine: 40, MaxLines: 5));

		Assert.Equal(new Uri(BaseAddress, "docs/%23f6feba?fromLine=40&maxLines=5"), Assert.Single(handler.Requests).Uri);
		Assert.Equal("claude-sessions", document.Collection);
		Assert.Equal(40, document.FromLine);
		Assert.Null(document.Metadata!.Branch);
	}

	[Fact]
	public void DocumentPath_SendsSimilarFiles()
	{
		Assert.Equal("docs/a.md?similarFiles=false", MemoriaQmdClient.DocumentPath("a.md", new SidecarDocumentQuery(SimilarFiles: false)));
		Assert.Equal("docs/a.md?similarFiles=true", MemoriaQmdClient.DocumentPath("a.md", new SidecarDocumentQuery(SimilarFiles: true)));
	}

	[Fact]
	public async Task MultiGet_StopsReadingABodyPastTheBudget_InsteadOfBufferingIt()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		int budget = 1024;
		long limit = MemoriaQmdClient.MaxMultiGetWireBytes(new SidecarMultiGetRequest { MaxTotalBytes = budget });
		handler.Respond(HttpStatusCode.OK, $$"""{"docs":[],"skipped":[{"file":"{{new string('x', (int)limit + 10)}}","reason":"r"}],"errors":[]}""");

		SidecarException failure = await Assert.ThrowsAsync<SidecarException>(() => client.MultiGetAsync(new SidecarMultiGetRequest { Pattern = "a*", MaxTotalBytes = budget }));

		Assert.Equal(SidecarException.ResponseTooLarge, failure.Code);
	}

	[Fact]
	public void MultiGet_WireLimit_UsesTheDefaultBudgetWhenNoneIsSet()
	{
		Assert.Equal((16L * 1024 * 1024 * 6) + (16L * 1024 * 1024), MemoriaQmdClient.MaxMultiGetWireBytes(new SidecarMultiGetRequest()));
	}

	[Fact]
	public async Task MultiGet_MapsErrorsAndBadBodies()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.BadRequest, """{"error":"validation","message":"nope"}""");
		handler.Respond(HttpStatusCode.OK, "not json");
		handler.Respond(HttpStatusCode.OK, "null");
		handler.Respond(HttpStatusCode.OK, """{"docs":[],"skipped":[],"errors":[],"truncated":true}""");
		SidecarMultiGetRequest request = new() { Pattern = "a*" };

		Assert.Equal("validation", (await Assert.ThrowsAsync<SidecarException>(() => client.MultiGetAsync(request))).Code);
		Assert.Equal(SidecarException.Malformed, (await Assert.ThrowsAsync<SidecarException>(() => client.MultiGetAsync(request))).Code);
		Assert.Equal(SidecarException.Malformed, (await Assert.ThrowsAsync<SidecarException>(() => client.MultiGetAsync(request))).Code);
		Assert.True((await client.MultiGetAsync(request)).Truncated);
	}

	[Fact]
	public async Task BoundedReadStream_PassesThrough_AndCountsEveryReadPath()
	{
		using BoundedReadStream stream = new(new MemoryStream(new byte[10]), 4);
		byte[] buffer = new byte[2];

		Assert.True(stream.CanRead);
		Assert.False(stream.CanSeek);
		Assert.False(stream.CanWrite);
		Assert.Throws<NotSupportedException>(() => stream.Length);
		Assert.Throws<NotSupportedException>(() => stream.Position);
		Assert.Throws<NotSupportedException>(() => stream.Position = 0);
		Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
		Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
		Assert.Throws<NotSupportedException>(() => stream.Write(buffer, 0, 2));
		stream.Flush();

		Assert.Equal(2, stream.Read(buffer, 0, 2));
		Assert.Equal(2, await stream.ReadAsync(buffer, 0, 2, CancellationToken.None));
		Assert.Throws<SidecarException>(() => stream.Read(buffer, 0, 2));
	}

	[Fact]
	public void DocumentPath_RepeatsCollections_Encoded()
	{
		string path = MemoriaQmdClient.DocumentPath("a.md", new SidecarDocumentQuery(Body: false, Collections: ["x y", "z/&"]));

		Assert.Equal("docs/a.md?body=false&collections=x%20y&collections=z%2F%26", path);
	}

	[Theory]
	[InlineData("../status")]
	[InlineData("./status")]
	[InlineData("a/../../status")]
	[InlineData("qmd://a/../../../reindex/jobs")]
	public async Task GetDocument_RefusesDotSegments_WithoutAnyRequest(string reference)
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();

		SidecarException failure = await Assert.ThrowsAsync<SidecarException>(() => client.GetDocumentAsync(reference));

		Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
		Assert.Equal("not_found", failure.Code);
		Assert.Empty(handler.Requests);
	}

	[Theory]
	[InlineData("a/b..md", "docs/a/b..md")]
	[InlineData("a/.hidden.md", "docs/a/.hidden.md")]
	public async Task GetDocument_AllowsDotsInsideNames(string reference, string expected)
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.OK, Fixtures.Document);

		await client.GetDocumentAsync(reference);

		Assert.Equal(new Uri(BaseAddress, expected), Assert.Single(handler.Requests).Uri);
	}

	[Fact]
	public async Task GetReadiness_Returns503BodyAsNotReady()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.ServiceUnavailable, Fixtures.Readiness("warming", resident: false));

		SidecarReadinessResult result = await client.GetReadinessAsync(requireModels: true);

		Assert.Equal(new Uri(BaseAddress, "ready?requireModels=true"), Assert.Single(handler.Requests).Uri);
		Assert.False(result.Ready);
		Assert.Equal("warming", result.Details.Status);
		Assert.False(result.Details.Models.Resident);
		Assert.Equal(1502, result.Details.Index.TotalDocuments);
	}

	[Fact]
	public async Task GetReadiness_Returns200AsReady()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.OK, Fixtures.Readiness("ready", resident: true));

		SidecarReadinessResult result = await client.GetReadinessAsync(requireModels: false);

		Assert.Equal(new Uri(BaseAddress, "ready"), Assert.Single(handler.Requests).Uri);
		Assert.True(result.Ready);
		Assert.Null(result.Details.Warmup.Error);
	}

	[Fact]
	public async Task ErrorEnvelope_BecomesSidecarException()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.NotFound, """{"error":"not_found","message":"No document matches 'x'","details":{"reference":"x"}}""");

		SidecarException ex = await Assert.ThrowsAsync<SidecarException>(() => client.GetDocumentAsync("x"));

		Assert.Equal("not_found", ex.Code);
		Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
		Assert.Equal("No document matches 'x'", ex.Message);
		Assert.Equal("x", ex.Details!.Value.GetProperty("reference").GetString());
		Assert.False(ex.IsTransient);
	}

	[Fact]
	public async Task NonJsonError_BecomesMalformedResponse()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>nginx</html>", System.Text.Encoding.UTF8, "text/html") });

		SidecarException ex = await Assert.ThrowsAsync<SidecarException>(() => client.GetStatusAsync());

		Assert.Equal(SidecarException.Malformed, ex.Code);
		Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
		Assert.True(ex.IsTransient);
	}

	[Fact]
	public async Task ConnectionFailure_BecomesUnreachable()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Throw(new HttpRequestException("No connection could be made"));

		SidecarException ex = await Assert.ThrowsAsync<SidecarException>(() => client.ListCollectionsAsync());

		Assert.Equal(SidecarException.Unreachable, ex.Code);
		Assert.Null(ex.StatusCode);
		Assert.Contains("sidecar.test:8282", ex.Message);
		Assert.IsType<HttpRequestException>(ex.InnerException);
		Assert.True(ex.IsTransient);
	}

	[Fact]
	public async Task ClientTimeout_BecomesTimeout()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Throw(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException()));

		SidecarException ex = await Assert.ThrowsAsync<SidecarException>(() => client.GetStatusAsync());

		Assert.Equal(SidecarException.Timeout, ex.Code);
		Assert.Null(ex.StatusCode);
	}

	[Fact]
	public async Task CallerCancellation_IsNotTranslated()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		using CancellationTokenSource cts = new();
		handler.Respond(_ =>
		{
			cts.Cancel();
			throw new TaskCanceledException();
		});

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStatusAsync(cts.Token));
	}

	[Fact]
	public async Task Reindex_UnwrapsJobEnvelope()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.Accepted, """{"job":""" + Fixtures.Job + "}");

		SidecarJob job = await client.ReindexAsync(new SidecarReindexRequest(Collections: ["claude-sessions"], Embed: true));

		Assert.Equal("job-1", job.Id);
		Assert.Equal(SidecarJobState.Queued, job.State);
		Assert.Equal(["claude-sessions"], job.Request.Collections);
		Assert.Contains("\"collections\":[\"claude-sessions\"]", Assert.Single(handler.Requests).Body);
	}

	[Fact]
	public async Task Ingest_SendsDocumentAndReadsAcceptedJob()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.Accepted, """{"collection":"claude-sessions","path":"2026-11-01_x.md","file":"claude-sessions/2026-11-01_x.md","bytes":12,"created":true,"metadata":null,"job":""" + Fixtures.Job + "}");

		SidecarIngestResponse response = await client.IngestAsync(new SidecarIngestRequest("claude-sessions", "2026-11-01_x.md", new Memoria.Core.Redaction.Redactor().Redact(new Memoria.Core.Sessions.UnredactedMarkdown("# hello\n"))));

		Assert.True(response.Created);
		Assert.Null(response.Metadata);
		Assert.Equal("job-1", response.Job.Id);
		Assert.Equal(new Uri(BaseAddress, "documents"), Assert.Single(handler.Requests).Uri);
	}

	[Fact]
	public async Task Jobs_ListAndGet()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.OK, """{"jobs":[""" + Fixtures.Job + "]}");
		handler.Respond(HttpStatusCode.OK, """{"job":""" + Fixtures.Job + "}");

		IReadOnlyList<SidecarJob> jobs = await client.ListJobsAsync();
		SidecarJob job = await client.GetJobAsync("job 1");

		Assert.Single(jobs);
		Assert.Equal("job-1", job.Id);
		Assert.Equal(new Uri(BaseAddress, "reindex/jobs/job%201"), handler.Requests[1].Uri);
	}
}
