using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Memoria.Api.Tests;

/// <summary>
/// What an operator is told when the control plane refuses a request, or the sidecar does: each field of the
/// problem, word for word, and the log line a sidecar refusal leaves.
/// </summary>
public sealed class AdminControlRefusalTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private const string Job = """
		{"job":{"id":"5b0c2a8e-1c1d-4c7e-9a43-0d4a2f6f9b11","state":"queued","request":{"collections":null,"embed":true,"force":false,"reason":"POST /reindex"},
		 "createdAt":"2026-11-01T10:00:00.000Z","startedAt":null,"finishedAt":null,"result":null,"error":null,"priority":"background","attempts":0,"worker":null}}
		""";

	private const string NamesMessage = "Collection names must be 1 to 200 characters with no slash or surrounding whitespace. They are case-sensitive.";

	private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
	{
		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.Clone();
	}

	private static async Task<Dictionary<string, string[]>> ErrorsOf(HttpResponseMessage response)
	{
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		JsonElement body = await BodyOf(response);
		return body.GetProperty("errors").EnumerateObject()
			.ToDictionary(static field => field.Name, static field => field.Value.EnumerateArray().Select(static message => message.GetString()!).ToArray());
	}

	private static string Collections(int count)
	{
		return $$"""{"collections":[{{string.Join(',', Enumerable.Range(0, count).Select(static i => $"\"c{i}\""))}}]}""";
	}

	[Fact]
	public async Task ASidecarWithoutTheRoute_SaysToRunOneRelease_WithTheUpstreamStatus()
	{
		factory.Sidecar.On("POST", "/queue:resume", HttpStatusCode.NotFound, """{"error":"no_route","message":"POST /queue:resume is not a route"}""");
		HttpClient admin = await factory.AdminAsync();

		JsonElement body = await BodyOf(await admin.PostAsync("/v1/admin/queue:resume", null));

		Assert.Equal(502, body.GetProperty("status").GetInt32());
		Assert.Equal("Search backend does not offer this operation", body.GetProperty("title").GetString());
		Assert.Equal("The sidecar does not have this route. Run the API and the sidecar from the same release.", body.GetProperty("detail").GetString());
		Assert.Equal("sidecar_route_missing", body.GetProperty("code").GetString());
		Assert.Equal(404, body.GetProperty("upstreamStatus").GetInt32());
	}

	[Fact]
	public async Task ASidecarRefusal_PassesThrough_AndIsLoggedAtInformation()
	{
		factory.Sidecar.On("POST", "/workers/ghost:enable", HttpStatusCode.NotFound, """{"error":"unknown_worker","message":"No worker is named ghost"}""");
		HttpClient admin = await factory.AdminAsync();
		factory.Logs.Clear();

		JsonElement body = await BodyOf(await admin.PostAsync("/v1/admin/workers/ghost:enable", null));

		Assert.Equal(404, body.GetProperty("status").GetInt32());
		Assert.Equal("Not found", body.GetProperty("title").GetString());
		Assert.Equal("No worker is named ghost", body.GetProperty("detail").GetString());
		Assert.Equal("unknown_worker", body.GetProperty("code").GetString());
		Assert.Equal(404, body.GetProperty("upstreamStatus").GetInt32());
		MemoriaApiFactory.CapturedLog line = Assert.Single(factory.Logs, static log => log.EventId.Id == 3400);
		Assert.Equal((LogLevel.Information, "unknown_worker", 404), (line.Level, line.State["Code"], line.State["UpstreamStatus"]));
	}

	[Theory]
	[InlineData("""{"collections":[]}""", "Omit collections to reindex every collection. An empty list is refused.")]
	[InlineData("""{"collections":["fine","a/b"]}""", NamesMessage)]
	[InlineData("""{"collections":["fine"," padded"]}""", NamesMessage)]
	public async Task AReindexList_ThatCouldMeanSomethingElse_IsRefusedWithItsReason(string json, string message)
	{
		factory.Sidecar.Requests.Clear();
		HttpClient admin = await factory.AdminAsync();

		Dictionary<string, string[]> errors = await ErrorsOf(await admin.PostAsync("/v1/admin/reindex", new StringContent(json, Encoding.UTF8, "application/json")));

		Assert.Equal("collections", Assert.Single(errors.Keys));
		Assert.Equal([message], errors["collections"]);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task AReindex_TakesFiftyCollections_AndRefusesFiftyOne()
	{
		factory.Sidecar.On("POST", "/reindex", HttpStatusCode.Accepted, Job);
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage fifty = await admin.PostAsync("/v1/admin/reindex", new StringContent(Collections(50), Encoding.UTF8, "application/json"));
		Dictionary<string, string[]> errors = await ErrorsOf(await admin.PostAsync("/v1/admin/reindex", new StringContent(Collections(51), Encoding.UTF8, "application/json")));

		Assert.Equal(HttpStatusCode.Accepted, fifty.StatusCode);
		Assert.Equal(["At most 50 collections per request."], Assert.Single(errors).Value);
		Assert.Equal("collections", Assert.Single(errors).Key);
	}

	[Theory]
	[InlineData("/v1/admin/workers/bad%20name:drain")]
	[InlineData("/v1/admin/workers/..:enable")]
	public async Task AWorkerNameTheSidecarCouldNotHold_SaysWhatANameIs(string path)
	{
		HttpClient admin = await factory.AdminAsync();

		Dictionary<string, string[]> errors = await ErrorsOf(await admin.PostAsync(path, null));

		Assert.Equal("name", Assert.Single(errors.Keys));
		Assert.Equal(["A worker name is 1 to 64 letters, digits, dots, dashes or underscores, or a host and port, as `GET /v1/admin/workers` lists it."], errors["name"]);
	}

	[Theory]
	[InlineData("/v1/admin/queue/jobs/not_an_id:retry")]
	[InlineData("/v1/admin/queue/jobs/not_an_id:cancel")]
	public async Task AJobIdTheSidecarCouldNotHold_SaysWhatAnIdIs(string path)
	{
		HttpClient admin = await factory.AdminAsync();

		Dictionary<string, string[]> errors = await ErrorsOf(await admin.PostAsync(path, null));

		Assert.Equal("id", Assert.Single(errors.Keys));
		Assert.Equal(["A job id is 1 to 64 letters, digits or dashes, as `GET /v1/admin/queue` lists them."], errors["id"]);
	}
}
