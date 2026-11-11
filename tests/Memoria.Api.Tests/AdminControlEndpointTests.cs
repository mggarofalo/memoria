using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Memoria.Api.Http;
using Memoria.Core.Contracts;

namespace Memoria.Api.Tests;

/// <summary>
/// The control plane through a real auth pipeline: who may call it, what reaches the
/// sidecar, and what comes back. A refused caller must never reach the sidecar.
/// </summary>
public sealed class AdminControlEndpointTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private const string Worker = """
		{"name":"desktop","host":"10.0.0.2:8383","state":"up","circuit":"closed","circuits":{"embed":"closed","rerank":"closed","generate":"closed","documents":"closed"},
		 "embedDisagrees":false,"capabilities":["embed","rerank","generate"],"priority":1,"maxConcurrent":8,"embedModelCompatible":true,"rerankModelCompatible":false,"generateModelCompatible":null,"rerankTransport":"llama-server-qmd-tokens",
		 "lastHeartbeat":"2026-11-01T10:00:00.000Z","inFlight":2,"queueDepth":0,"calls":40,"errorRate":0.025,"drained":DRAINED,"token":"never-forwarded-0123456789"}
		""";

	private const string Counts = """{"queued":1,"held":0,"running":1,"succeeded":3,"failed":1}""";

	private const string Job = """
		{"id":"5b0c2a8e-1c1d-4c7e-9a43-0d4a2f6f9b11","state":"failed","request":{"collections":null,"embed":true,"force":false,"reason":"POST /reindex"},
		 "createdAt":"2026-11-01T10:00:00.000Z","startedAt":"2026-11-01T10:00:01.000Z","finishedAt":"2026-11-01T10:00:09.000Z","result":null,"error":"no worker","priority":"background","attempts":2,"worker":"desktop"}
		""";

	private const string JobId = "5b0c2a8e-1c1d-4c7e-9a43-0d4a2f6f9b11";

	/// <summary>Every control route, as a method and path a caller could send.</summary>
	public static TheoryData<string, string> Routes => new()
	{
		{ "GET", "/v1/admin/workers" },
		{ "POST", "/v1/admin/workers/desktop:drain" },
		{ "POST", "/v1/admin/workers/desktop:enable" },
		{ "GET", "/v1/admin/queue" },
		{ "POST", "/v1/admin/queue:pause" },
		{ "POST", "/v1/admin/queue:resume" },
		{ "POST", $"/v1/admin/queue/jobs/{JobId}:retry" },
		{ "POST", $"/v1/admin/queue/jobs/{JobId}:cancel" },
		{ "POST", "/v1/admin/reindex" },
	};

	private static string WorkerJson(bool drained)
	{
		return Worker.Replace("DRAINED", drained ? "true" : "false", StringComparison.Ordinal);
	}

	private void ScriptSidecar()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/workers", HttpStatusCode.OK, $$"""{"mode":"remote","workers":[{{WorkerJson(false)}}]}""");
		factory.Sidecar.On("POST", "/workers/desktop:drain", HttpStatusCode.OK, $$"""{"worker":{{WorkerJson(true)}}}""");
		factory.Sidecar.On("POST", "/workers/desktop:enable", HttpStatusCode.OK, $$"""{"worker":{{WorkerJson(false)}}}""");
		factory.Sidecar.On("GET", "/queue", HttpStatusCode.OK, $$$"""{"paused":true,"jobs":{{{Counts}}},"persistence":{"enabled":true,"healthy":true},"deadLetters":2,"classes":{"interactive":{"inFlight":1,"waiting":0},"background":{"inFlight":3,"waiting":5}},"workers":[]}""");
		factory.Sidecar.On("GET", "/reindex/jobs", HttpStatusCode.OK, $$"""{"jobs":[{{Job}}]}""");
		factory.Sidecar.On("POST", "/queue:pause", HttpStatusCode.OK, $$"""{"paused":true,"jobs":{{Counts}}}""");
		factory.Sidecar.On("POST", "/queue:resume", HttpStatusCode.OK, $$"""{"paused":false,"jobs":{{Counts}}}""");
		factory.Sidecar.On("POST", $"/queue/jobs/{JobId}:retry", HttpStatusCode.Accepted, $$"""{"job":{{Job}}}""");
		factory.Sidecar.On("POST", $"/queue/jobs/{JobId}:cancel", HttpStatusCode.Accepted, $$"""{"job":{{Job}}}""");
		factory.Sidecar.On("POST", "/reindex", HttpStatusCode.Accepted, $$"""{"job":{{Job}}}""");
	}

	private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path)
	{
		HttpRequestMessage request = new(new HttpMethod(method), path);
		if (path.EndsWith("/reindex", StringComparison.Ordinal))
		{
			request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
		}

		return client.SendAsync(request);
	}

	private static async Task<string?> CodeOf(HttpResponseMessage response)
	{
		using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return problem.RootElement.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
	}

	[Theory]
	[MemberData(nameof(Routes))]
	public async Task AnUnboundAdminKey_ReachesEveryRoute(string method, string path)
	{
		ScriptSidecar();
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await SendAsync(admin, method, path);

		Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
		Assert.NotEmpty(factory.Sidecar.Requests);
	}

	[Theory]
	[MemberData(nameof(Routes))]
	public async Task AnAllCollectionsAdminKey_ReachesEveryRoute(string method, string path)
	{
		ScriptSidecar();
		HttpClient admin = await factory.ClientWithScopesAsync([MemoriaScopes.Admin, MemoriaScopes.Read], allCollections: true);

		HttpResponseMessage response = await SendAsync(admin, method, path);

		Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode}");
	}

	[Theory]
	[MemberData(nameof(Routes))]
	public async Task AKeyWithoutAdmin_Gets403_AndTheSidecarHearsNothing(string method, string path)
	{
		ScriptSidecar();
		foreach (HttpClient client in new[]
		{
			await factory.ReaderOfAsync("claude-sessions"),
			await factory.ClientWithScopesAsync([MemoriaScopes.Read, MemoriaScopes.Ingest], allCollections: true),
		})
		{
			HttpResponseMessage response = await SendAsync(client, method, path);

			Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		}

		Assert.Empty(factory.Sidecar.Requests);
	}

	[Theory]
	[MemberData(nameof(Routes))]
	public async Task ACollectionScopedAdminKey_Gets403_AndTheSidecarHearsNothing(string method, string path)
	{
		ScriptSidecar();
		HttpClient scoped = await factory.ClientWithScopesAsync([MemoriaScopes.Admin, MemoriaScopes.Read], ["claude-sessions"]);

		HttpResponseMessage response = await SendAsync(scoped, method, path);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Equal("collection_scoped_key", await CodeOf(response));
		Assert.Empty(factory.Sidecar.Requests);
	}

	/// <summary>The scope check runs before the body is read, so no body can turn the refusal into a 400.</summary>
	[Theory]
	[InlineData("{not json", "application/json")]
	[InlineData("""{"collections":[]}""", "application/json")]
	[InlineData("""{"collections":"notes"}""", "application/json")]
	[InlineData("", "application/json")]
	public async Task ACollectionScopedAdminKey_Gets403_WhateverTheReindexBody(string body, string contentType)
	{
		ScriptSidecar();
		(Memoria.Api.Auth.ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Admin, MemoriaScopes.Read], ["claude-sessions"], label: "scoped reindex");
		HttpClient scoped = AuthApiFactory.Authorized(factory.CreateClient(), token);

		HttpResponseMessage response = await scoped.PostAsync("/v1/admin/reindex", new StringContent(body, Encoding.UTF8, contentType));

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Equal("collection_scoped_key", await CodeOf(response));
		Assert.Empty(factory.Sidecar.Requests);
		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(r => r.Any(a => a.KeyId == record.Id));
		Assert.Equal(403, Assert.Single(rows, r => r.KeyId == record.Id).Status);
	}

	/// <summary>
	/// Who may call is decided before what the body is: no key gets 401 and a collection-scoped
	/// admin key 403, whatever it sends. Only an admin key that may call learns that the body has to
	/// be JSON, with 415. The sidecar never sees any of it.
	/// </summary>
	[Fact]
	public async Task ANonJsonReindexBody_IsJudgedAfterTheKey_AndTheSidecarHearsNothing()
	{
		ScriptSidecar();
		StringContent Body() => new("collections=notes", Encoding.UTF8, "text/plain");
		HttpClient scoped = await factory.ClientWithScopesAsync([MemoriaScopes.Admin, MemoriaScopes.Read], ["claude-sessions"]);
		HttpClient reader = await factory.ClientWithScopesAsync([MemoriaScopes.Read], ["claude-sessions"]);

		HttpResponseMessage refused = await scoped.PostAsync("/v1/admin/reindex", Body());
		Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
		Assert.Contains("collection_scoped_key", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
		Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/v1/admin/reindex", Body())).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/v1/admin/keys", Body())).StatusCode);
		Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await (await factory.AdminAsync()).PostAsync("/v1/admin/reindex", Body())).StatusCode);
		Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await (await factory.AdminAsync()).PostAsync("/v1/admin/keys", Body())).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync("/v1/admin/reindex", Body())).StatusCode);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Theory]
	[MemberData(nameof(Routes))]
	public async Task NoKey_Gets401(string method, string path)
	{
		ScriptSidecar();

		HttpResponseMessage response = await SendAsync(factory.CreateClient(), method, path);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task ListWorkers_MapsTheView_AndDropsAnythingUndeclared()
	{
		ScriptSidecar();
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.GetAsync("/v1/admin/workers");

		string body = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain("never-forwarded", body, StringComparison.Ordinal);
		WorkerList list = (await response.Content.ReadFromJsonAsync<WorkerList>(Json))!;
		Assert.Equal("remote", list.Mode);
		WorkerInfo worker = Assert.Single(list.Workers);
		Assert.Equal("desktop", worker.Name);
		Assert.Equal("10.0.0.2:8383", worker.Host);
		Assert.False(worker.Drained);
		Assert.Equal(2, worker.InFlight);
		Assert.Equal(0.025, worker.ErrorRate);
		Assert.Equal("closed", worker.Circuits.Documents);
		Assert.Equal(["embed", "rerank", "generate"], worker.Capabilities);
		Assert.Equal((true, false, (bool?)null), (worker.EmbedModelCompatible, worker.RerankModelCompatible, worker.GenerateModelCompatible));
	}

	[Fact]
	public async Task DrainAndEnable_ForwardTheName_AndReturnTheWorker()
	{
		ScriptSidecar();
		HttpClient admin = await factory.AdminAsync();

		WorkerInfo drained = (await (await admin.PostAsync("/v1/admin/workers/desktop:drain", null)).Content.ReadFromJsonAsync<WorkerInfo>(Json))!;
		WorkerInfo enabled = (await (await admin.PostAsync("/v1/admin/workers/desktop:enable", null)).Content.ReadFromJsonAsync<WorkerInfo>(Json))!;

		Assert.True(drained.Drained);
		Assert.False(enabled.Drained);
		Assert.Equal(["POST /workers/desktop:drain", "POST /workers/desktop:enable"], factory.Sidecar.Requests.Select(r => $"{r.Method} {r.Uri.AbsolutePath}"));
		Assert.All(factory.Sidecar.Requests, r => Assert.Equal($"Bearer {MemoriaApiFactory.SidecarSecret}", r.Headers["Authorization"]));
	}

	[Fact]
	public async Task Drain_TheShorthandsHostAndPortName_ReachesTheSidecarEncoded()
	{
		ScriptSidecar();
		factory.Sidecar.OnPrefix("POST", "/workers/", (_, _) => FakeSidecar.Json(HttpStatusCode.OK, $$"""{"worker":{{WorkerJson(true)}}}"""));
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsync("/v1/admin/workers/127.0.0.1:9:drain", null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("/workers/127.0.0.1%3A9:drain", factory.Sidecar.Single().Uri.AbsolutePath);
	}

	[Fact]
	public async Task Drain_AnIpv6ShorthandName_ReachesTheSidecarEncoded()
	{
		ScriptSidecar();
		factory.Sidecar.OnPrefix("POST", "/workers/", (_, _) => FakeSidecar.Json(HttpStatusCode.OK, $$"""{"worker":{{WorkerJson(true)}}}"""));
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsync("/v1/admin/workers/%5B::1%5D:8383:drain", null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("/workers/%5B%3A%3A1%5D%3A8383:drain", factory.Sidecar.Single().Uri.AbsolutePath);
	}

	[Theory]
	[InlineData("""{"error":"no_route","message":"POST /queue:pause is not a route"}""")]
	[InlineData("")]
	public async Task ASidecarWithoutTheRoute_Is502_NotAMissingWorker(string body)
	{
		ScriptSidecar();
		factory.Sidecar.On("POST", "/queue:pause", (_, _) => body == "" ? new HttpResponseMessage(HttpStatusCode.NotFound) : FakeSidecar.Json(HttpStatusCode.NotFound, body));
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsync("/v1/admin/queue:pause", null);

		Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
		Assert.Equal("sidecar_route_missing", await CodeOf(response));
	}

	[Fact]
	public async Task Drain_AnUnknownWorker_Is404WithTheSidecarsCode()
	{
		ScriptSidecar();
		factory.Sidecar.On("POST", "/workers/ghost:drain", HttpStatusCode.NotFound, """{"error":"unknown_worker","message":"No worker is named ghost"}""");
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsync("/v1/admin/workers/ghost:drain", null);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Equal("unknown_worker", await CodeOf(response));
	}

	[Theory]
	[InlineData("/v1/admin/workers/bad%20name:drain")]
	[InlineData("/v1/admin/workers/..:drain")]
	[InlineData("/v1/admin/workers/a%2Fb:enable")]
	[InlineData("/v1/admin/queue/jobs/not_an_id:retry")]
	[InlineData("/v1/admin/queue/jobs/a%2F..%2Fx:cancel")]
	public async Task ANameOrIdTheSidecarCouldNotHold_Is400_BeforeTheSidecar(string path)
	{
		ScriptSidecar();
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsync(path, null);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task QueueStatus_JoinsCountsAndJobs()
	{
		ScriptSidecar();
		HttpClient admin = await factory.AdminAsync();

		QueueStatus status = (await admin.GetFromJsonAsync<QueueStatus>("/v1/admin/queue", Json))!;

		Assert.True(status.Paused);
		Assert.True(status.Persistent);
		Assert.Equal(2, status.DeadLetters);
		Assert.Equal(new JobCounts(1, 0, 1, 3, 1), status.Counts);
		Assert.Equal(new PriorityLoad(3, 5), status.Background);
		JobInfo job = Assert.Single(status.Jobs);
		Assert.Equal(JobId, job.Id);
		Assert.Null(job.Collections);
		Assert.Equal(2, job.Attempts);
		Assert.Equal("desktop", job.Worker);
		Assert.Equal("no worker", job.Error);
	}

	[Fact]
	public async Task PauseAndResume_ReportTheQueuesState()
	{
		ScriptSidecar();
		HttpClient admin = await factory.AdminAsync();

		QueueState paused = (await (await admin.PostAsync("/v1/admin/queue:pause", null)).Content.ReadFromJsonAsync<QueueState>(Json))!;
		QueueState resumed = (await (await admin.PostAsync("/v1/admin/queue:resume", null)).Content.ReadFromJsonAsync<QueueState>(Json))!;

		Assert.True(paused.Paused);
		Assert.False(resumed.Paused);
		Assert.Equal(1, paused.Counts.Running);
	}

	[Fact]
	public async Task RetryAndCancel_Are202WithTheJob_AndAConflictPassesThrough()
	{
		ScriptSidecar();
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage retried = await admin.PostAsync($"/v1/admin/queue/jobs/{JobId}:retry", null);
		factory.Sidecar.On("POST", $"/queue/jobs/{JobId}:cancel", HttpStatusCode.Conflict, """{"error":"not_cancellable","message":"This job has already finished"}""");
		HttpResponseMessage conflict = await admin.PostAsync($"/v1/admin/queue/jobs/{JobId}:cancel", null);

		Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
		Assert.Equal(JobId, (await retried.Content.ReadFromJsonAsync<JobInfo>(Json))!.Id);
		Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
		Assert.Equal("not_cancellable", await CodeOf(conflict));
	}

	[Fact]
	public async Task Reindex_ForwardsTheRequest_WithDuplicatesFolded()
	{
		ScriptSidecar();
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsJsonAsync("/v1/admin/reindex", new ReindexRequest(["claude-sessions", "claude-sessions", "notes"], Embed: false, Force: true), Json);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument sent = JsonDocument.Parse(factory.Sidecar.Single().Body!);
		Assert.Equal(["claude-sessions", "notes"], sent.RootElement.GetProperty("collections").EnumerateArray().Select(c => c.GetString()));
		Assert.False(sent.RootElement.GetProperty("embed").GetBoolean());
		Assert.True(sent.RootElement.GetProperty("force").GetBoolean());
	}

	[Fact]
	public async Task Reindex_WithNoBody_CoversEveryCollection()
	{
		ScriptSidecar();
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsync("/v1/admin/reindex", null);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using JsonDocument sent = JsonDocument.Parse(factory.Sidecar.Single().Body!);
		Assert.False(sent.RootElement.TryGetProperty("collections", out _), "omitted, not an empty list");
	}

	[Theory]
	[InlineData("""{"collections":[]}""")]
	[InlineData("""{"collections":["a/b"]}""")]
	[InlineData("""{"collections":[" padded "]}""")]
	public async Task Reindex_RefusesAListThatCouldMeanSomethingElse(string body)
	{
		ScriptSidecar();
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsync("/v1/admin/reindex", new StringContent(body, Encoding.UTF8, "application/json"));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task Reindex_RefusesMoreThanFiftyCollections()
	{
		ScriptSidecar();
		HttpClient admin = await factory.AdminAsync();
		string[] many = [.. Enumerable.Range(0, 51).Select(i => $"c{i}")];

		HttpResponseMessage response = await admin.PostAsJsonAsync("/v1/admin/reindex", new ReindexRequest(many, null, null), Json);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task Reindex_AnUnknownCollection_PassesTheSidecarsRefusalThrough()
	{
		ScriptSidecar();
		factory.Sidecar.On("POST", "/reindex", HttpStatusCode.BadRequest, """{"error":"unknown_collection","message":"Unknown collection(s): nope"}""");
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsJsonAsync("/v1/admin/reindex", new ReindexRequest(["nope"], null, null), Json);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal("unknown_collection", await CodeOf(response));
	}

	[Fact]
	public async Task AnUnreachableSidecar_Is503()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.Unreachable();
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.GetAsync("/v1/admin/workers");

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		ScriptSidecar();
	}

	[Fact]
	public async Task AReadinessProbeWithAKey_LogsItsTraceAtInformation_SoAnOperatorCanFindIt()
	{
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.Readiness("ready", resident: true));
		factory.Logs.Clear();
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage keyed = await admin.GetAsync("/ready");
		HttpResponseMessage anonymous = await factory.CreateClient().GetAsync("/ready");

		string keyedTrace = Assert.Single(keyed.Headers.GetValues(RequestLoggingMiddleware.TraceIdHeader));
		string anonymousTrace = Assert.Single(anonymous.Headers.GetValues(RequestLoggingMiddleware.TraceIdHeader));
		List<MemoriaApiFactory.CapturedLog> lines = [.. factory.Logs.Where(l => l.Category == typeof(RequestLoggingMiddleware).FullName)];
		Assert.Contains(lines, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Information && (string?)l.State.GetValueOrDefault("TraceId") == keyedTrace);
		Assert.Contains(lines, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Debug && (string?)l.State.GetValueOrDefault("TraceId") == anonymousTrace);
	}

	[Fact]
	public async Task EachCall_IsAudited_UnderItsOperationName()
	{
		ScriptSidecar();
		(Memoria.Api.Auth.ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Admin], label: "audited ops");
		HttpClient admin = AuthApiFactory.Authorized(factory.CreateClient(), token);
		(Memoria.Api.Auth.ApiKeyRecord scopedRecord, string scopedToken) = await factory.IssueAsync([MemoriaScopes.Admin, MemoriaScopes.Read], ["claude-sessions"], label: "scoped ops");
		HttpClient scoped = AuthApiFactory.Authorized(factory.CreateClient(), scopedToken);

		await admin.PostAsync("/v1/admin/workers/desktop:drain", null);
		await scoped.PostAsync("/v1/admin/workers/desktop:drain", null);

		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(r => r.Any(a => a.KeyId == record.Id) && r.Any(a => a.KeyId == scopedRecord.Id));
		Assert.Contains(rows, r => r.KeyId == record.Id && r.Operation == "drainWorker" && r.Status == 200);
		Assert.Contains(rows, r => r.KeyId == scopedRecord.Id && r.Operation == "drainWorker" && r.Status == 403);
	}
}

/// <summary>With authentication off there is no key to hold the admin scope, so the control plane is not there at all.</summary>
public sealed class AdminControlDisabledModeTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	[Theory]
	[InlineData("GET", "/v1/admin/workers")]
	[InlineData("POST", "/v1/admin/workers/desktop:drain")]
	[InlineData("POST", "/v1/admin/queue:pause")]
	[InlineData("POST", "/v1/admin/reindex")]
	public async Task EveryRoute_Is404_AndTheSidecarHearsNothing(string method, string path)
	{
		factory.Sidecar.Requests.Clear();

		HttpResponseMessage response = await factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("auth_disabled", problem.RootElement.GetProperty("code").GetString());
		Assert.Empty(factory.Sidecar.Requests);
	}
}
