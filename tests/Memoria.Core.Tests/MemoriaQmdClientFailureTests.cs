using System.Net;
using Memoria.Core.Sidecar;

namespace Memoria.Core.Tests;

/// <summary>Where each call goes, what it refuses before leaving, and what it says when the sidecar misbehaves.</summary>
public sealed class MemoriaQmdClientFailureTests
{
	private static (MemoriaQmdClient Client, FakeHttpHandler Handler) Create(TimeSpan? timeout = null)
	{
		FakeHttpHandler handler = new();
		HttpClient http = new(handler) { BaseAddress = new Uri("http://sidecar.test:8282/"), Timeout = timeout ?? TimeSpan.FromSeconds(10) };
		return (new MemoriaQmdClient(http), handler);
	}

	private static async Task<string> PathOf(Func<MemoriaQmdClient, Task> call)
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.InternalServerError, null);

		await Assert.ThrowsAsync<SidecarException>(() => call(client));

		return handler.Requests.Single().Uri.PathAndQuery;
	}

	[Fact]
	public async Task EachCall_GoesToItsOwnRoute()
	{
		Assert.Equal("/health", await PathOf(static c => c.GetHealthAsync()));
		Assert.Equal("/status", await PathOf(static c => c.GetStatusAsync()));
		Assert.Equal("/collections", await PathOf(static c => c.ListCollectionsAsync()));
		Assert.Equal("/reindex", await PathOf(static c => c.ReindexAsync(new SidecarReindexRequest())));
		Assert.Equal("/reindex/jobs", await PathOf(static c => c.ListJobsAsync()));
		Assert.Equal("/reindex/jobs/a%20b", await PathOf(static c => c.GetJobAsync("a b")));
		Assert.Equal("/search", await PathOf(static c => c.SearchAsync(new SidecarSearchRequest { Query = "q" })));
		Assert.Equal("/docs:multiGet", await PathOf(static c => c.MultiGetAsync(new SidecarMultiGetRequest { Pattern = "*" })));
	}

	[Fact]
	public async Task NullAndBlankArguments_AreRefused_BeforeAnythingIsSent()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();

		await Assert.ThrowsAsync<ArgumentNullException>(() => client.SearchAsync(null!));
		await Assert.ThrowsAsync<ArgumentNullException>(() => client.MultiGetAsync(null!));
		await Assert.ThrowsAsync<ArgumentNullException>(() => client.IngestAsync(null!));
		await Assert.ThrowsAsync<ArgumentNullException>(() => client.ReindexAsync(null!));
		await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetDocumentAsync(null!));
		await Assert.ThrowsAsync<ArgumentException>(() => client.GetDocumentAsync(" "));
		await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetJobAsync(null!));
		await Assert.ThrowsAsync<ArgumentException>(() => client.GetJobAsync(" "));

		Assert.Empty(handler.Requests);
	}

	[Theory]
	[InlineData("x/../../status")]
	[InlineData("./status")]
	public async Task ADotSegment_IsAMiss_NotAWalkToAnotherRoute(string reference)
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();

		SidecarException error = await Assert.ThrowsAsync<SidecarException>(() => client.GetDocumentAsync(reference));

		Assert.Equal("not_found", error.Code);
		Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
		Assert.Equal($"Document not found: {reference}", error.Message);
		Assert.Empty(handler.Requests);
	}

	[Fact]
	public void TheMultiGetLimit_AllowsForWorstCaseEscaping_AndTheDefaultBudget()
	{
		const long metadata = 16 * 1024 * 1024;
		Assert.Equal((1000L * 6) + metadata, MemoriaQmdClient.MaxMultiGetWireBytes(new SidecarMultiGetRequest { MaxTotalBytes = 1000 }));
		Assert.Equal((16L * 1024 * 1024 * 6) + metadata, MemoriaQmdClient.MaxMultiGetWireBytes(new SidecarMultiGetRequest()));
	}

	[Fact]
	public async Task AnEmptyOrBadBody_IsNamedAsMalformed_OnBothReadPaths()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.OK, "null");
		handler.Respond(HttpStatusCode.OK, "not json");
		handler.Respond(HttpStatusCode.OK, "null");
		handler.Respond(HttpStatusCode.OK, "not json");
		SidecarMultiGetRequest request = new() { Pattern = "*" };

		SidecarException emptyMulti = await Assert.ThrowsAsync<SidecarException>(() => client.MultiGetAsync(request));
		Assert.Equal(SidecarException.Malformed, emptyMulti.Code);
		Assert.Equal("The sidecar returned an empty body.", emptyMulti.Message);

		SidecarException badMulti = await Assert.ThrowsAsync<SidecarException>(() => client.MultiGetAsync(request));
		Assert.StartsWith("The sidecar returned a body that is not valid JSON: ", badMulti.Message, StringComparison.Ordinal);

		SidecarException emptyGet = await Assert.ThrowsAsync<SidecarException>(() => client.GetStatusAsync());
		Assert.Equal("The sidecar returned an empty body.", emptyGet.Message);

		SidecarException badGet = await Assert.ThrowsAsync<SidecarException>(() => client.GetStatusAsync());
		Assert.StartsWith("The sidecar returned a body that is not valid JSON for SidecarStatus: ", badGet.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnErrorEnvelopeWithNoCode_IsNotTrusted()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create();
		handler.Respond(HttpStatusCode.InternalServerError, """{"error":"","message":"from the body"}""");

		SidecarException error = await Assert.ThrowsAsync<SidecarException>(() => client.GetStatusAsync());

		Assert.Equal(SidecarException.Malformed, error.Code);
		Assert.Equal("The sidecar answered 500 Internal Server Error without an error envelope.", error.Message);
	}

	[Fact]
	public async Task ATimeout_NamesTheSidecarAndTheLimit()
	{
		(MemoriaQmdClient client, FakeHttpHandler handler) = Create(TimeSpan.FromSeconds(7));
		handler.Throw(new TaskCanceledException());

		SidecarException error = await Assert.ThrowsAsync<SidecarException>(() => client.GetStatusAsync());

		Assert.Equal(SidecarException.Timeout, error.Code);
		Assert.Equal("The memoria-qmd sidecar at http://sidecar.test:8282/ did not answer within 00:00:07.", error.Message);
	}

	[Fact]
	public void TheBoundedStream_StopsPastItsLimit_AndNamesIt()
	{
		using MemoryStream inner = new(new byte[10]);
		using BoundedReadStream bounded = new(inner, 4);
		byte[] buffer = new byte[4];

		Assert.Equal(4, bounded.Read(buffer, 0, 4));
		SidecarException error = Assert.Throws<SidecarException>(() => bounded.Read(buffer, 0, 1));

		Assert.Equal(SidecarException.ResponseTooLarge, error.Code);
		Assert.Equal("The sidecar's response passed the 4-byte limit.", error.Message);
	}

	[Fact]
	public void DisposingTheBoundedStream_ReleasesTheResponse()
	{
		MemoryStream inner = new(new byte[1]);
		BoundedReadStream bounded = new(inner, 4);

		bounded.Dispose();

		Assert.False(inner.CanRead);
	}
}
