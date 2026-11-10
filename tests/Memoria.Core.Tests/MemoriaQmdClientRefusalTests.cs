using System.Net;
using System.Text;
using Memoria.Core.Redaction;
using Memoria.Core.Sessions;
using Memoria.Core.Sidecar;

namespace Memoria.Core.Tests;

/// <summary>
/// What the client refuses before it sends anything, and what its failures say. An argument
/// the sidecar could not use must not become a request, and an error must say enough for an
/// operator to act on: which reference, which limit, which status.
/// </summary>
public sealed class MemoriaQmdClientRefusalTests
{
	private static readonly Uri BaseAddress = new("http://sidecar.test:8282/");

	private static (MemoriaQmdClient Client, FakeHttpHandler Handler) Create()
	{
		FakeHttpHandler handler = new();
		return (new MemoriaQmdClient(new HttpClient(handler) { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(7) }), handler);
	}

	public static TheoryData<string, Func<MemoriaQmdClient, Task>> MissingRequests => new()
	{
		{ "search", c => c.SearchAsync(null!) },
		{ "multi-get", c => c.MultiGetAsync(null!) },
		{ "ingest", c => c.IngestAsync(null!) },
		{ "reindex", c => c.ReindexAsync(null!) },
	};

	[Theory]
	[MemberData(nameof(MissingRequests))]
	public async Task ANullRequest_IsRefused_WithoutARequest(string operation, Func<MemoriaQmdClient, Task> call)
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();

		await Assert.ThrowsAsync<ArgumentNullException>(() => call(client));

		Assert.True(handler.Requests.Count == 0, $"{operation} sent a request");
	}

	public static TheoryData<string, Func<MemoriaQmdClient, string, Task>> BlankArguments => new()
	{
		{ "get document", (c, v) => c.GetDocumentAsync(v) },
		{ "is collection empty", (c, v) => c.IsCollectionEmptyAsync(v) },
		{ "delete: collection", (c, v) => c.DeleteDocumentAsync(v, "a.md") },
		{ "delete: path", (c, v) => c.DeleteDocumentAsync("notes", v) },
		{ "get job", (c, v) => c.GetJobAsync(v) },
		{ "retry job", (c, v) => c.RetryJobAsync(v) },
		{ "drain worker", (c, v) => c.DrainWorkerAsync(v) },
	};

	[Theory]
	[MemberData(nameof(BlankArguments))]
	public async Task ABlankNameOrReference_IsRefused_WithoutARequest(string operation, Func<MemoriaQmdClient, string, Task> call)
	{
		foreach (string blank in new[] { "", " " })
		{
			(MemoriaQmdClient client, FakeHttpHandler handler) = Create();

			await Assert.ThrowsAnyAsync<ArgumentException>(() => call(client, blank));

			Assert.True(handler.Requests.Count == 0, $"{operation} sent a request for {blank.Length} spaces");
		}
	}

	[Fact]
	public async Task ADotSegmentReference_NamesTheReference()
	{
		(MemoriaQmdClient client, _) = Create();

		SidecarException failure = await Assert.ThrowsAsync<SidecarException>(() => client.GetDocumentAsync("notes/../status"));

		Assert.Contains("notes/../status", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ADotSegmentWorkerName_SaysWhy()
	{
		(MemoriaQmdClient client, _) = Create();

		ArgumentException failure = await Assert.ThrowsAsync<ArgumentException>(() => client.EnableWorkerAsync(".."));

		Assert.Contains("dot segment", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TextPassedThroughForOneCollection_CannotGoToAnother_AndTheRefusalSaysSo()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		RedactionDisabledCollection family = RedactionDisabledCollection.FromOperatorSetting("family", RedactionDisabledCollection.SettingValue)!;
		RedactedMarkdown text = RedactedMarkdown.PassThroughForDisabledCollection(new UnredactedMarkdown("# hi\n"), family);

		InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.IngestAsync(new SidecarIngestRequest("work", "a.md", text)));

		Assert.Contains("redaction", failure.Message, StringComparison.Ordinal);
		Assert.Empty(handler.Requests);
	}

	[Fact]
	public async Task MultiGet_BadBodies_SayWhatWasWrong()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.OK, "null");
		handler.Respond(HttpStatusCode.OK, "not json");
		SidecarMultiGetRequest request = new() { Pattern = "a*" };

		SidecarException empty = await Assert.ThrowsAsync<SidecarException>(() => client.MultiGetAsync(request));
		SidecarException invalid = await Assert.ThrowsAsync<SidecarException>(() => client.MultiGetAsync(request));

		Assert.Contains("empty body", empty.Message, StringComparison.Ordinal);
		Assert.Contains("not valid JSON", invalid.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task MultiGet_AnOversizeBody_NamesTheLimitItPassed()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		long limit = MemoriaQmdClient.MaxMultiGetWireBytes(new SidecarMultiGetRequest { MaxTotalBytes = 10 });
		handler.Respond(HttpStatusCode.OK, $$"""{"docs":[],"skipped":[{"file":"{{new string('x', (int)limit + 10)}}","reason":"r"}],"errors":[]}""");

		SidecarException failure = await Assert.ThrowsAsync<SidecarException>(() => client.MultiGetAsync(new SidecarMultiGetRequest { Pattern = "a*", MaxTotalBytes = 10 }));

		Assert.Contains($"{limit}-byte", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void MultiGet_WireLimit_FollowsTheCallersBudget()
	{
		Assert.Equal((1000L * 6) + (16L * 1024 * 1024), MemoriaQmdClient.MaxMultiGetWireBytes(new SidecarMultiGetRequest { MaxTotalBytes = 1000 }));
	}

	[Fact]
	public async Task Health_IsRead_FromItsRoute()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.OK, """{"status":"ok","version":"1.2.3","uptimeSeconds":5}""");

		SidecarHealth health = await client.GetHealthAsync();

		Assert.Equal("1.2.3", health.Version);
		Assert.Equal(new Uri(BaseAddress, "health"), Assert.Single(handler.Requests).Uri);
	}

	[Fact]
	public async Task ATypedResponse_ThatIsNullOrNotJson_SaysWhichAndForWhat()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.OK, "null");
		handler.Respond(HttpStatusCode.OK, "{\"totalDocuments\":");

		SidecarException empty = await Assert.ThrowsAsync<SidecarException>(() => client.GetStatusAsync());
		SidecarException invalid = await Assert.ThrowsAsync<SidecarException>(() => client.GetStatusAsync());

		Assert.Equal(SidecarException.Malformed, empty.Code);
		Assert.Contains("empty body", empty.Message, StringComparison.Ordinal);
		Assert.Equal(SidecarException.Malformed, invalid.Code);
		Assert.Contains(nameof(SidecarStatus), invalid.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnErrorEnvelopeWithNoCode_IsNotTakenAsOne()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.BadRequest, """{"error":"","message":"something"}""");

		SidecarException failure = await Assert.ThrowsAsync<SidecarException>(() => client.GetStatusAsync());

		Assert.Equal(SidecarException.Malformed, failure.Code);
		Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
	}

	[Fact]
	public async Task AnErrorWithoutAnEnvelope_SaysTheStatus()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>nginx</html>", Encoding.UTF8, "text/html") });

		SidecarException failure = await Assert.ThrowsAsync<SidecarException>(() => client.GetStatusAsync());

		Assert.Contains("502", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ATimeout_SaysWhereAndHowLong()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Throw(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException()));

		SidecarException failure = await Assert.ThrowsAsync<SidecarException>(() => client.GetStatusAsync());

		Assert.Contains("sidecar.test:8282", failure.Message, StringComparison.Ordinal);
		Assert.Contains(TimeSpan.FromSeconds(7).ToString(), failure.Message, StringComparison.Ordinal);
	}
}
