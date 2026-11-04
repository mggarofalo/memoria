using System.Net;
using Memoria.Api.Auth;
using Memoria.Api.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Memoria.Api.Tests;

public sealed class RateLimitTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private WebApplicationFactory<Program> Limited(string permits)
	{
		return factory.WithWebHostBuilder(builder =>
		{
			builder.UseSetting("Memoria:Auth:RateLimit:PermitLimit", permits);
			builder.UseSetting("Memoria:Auth:RateLimit:Window", "01:00:00");
		});
	}

	private async Task<string> TokenAsync()
	{
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		return token;
	}

	[Fact]
	public async Task OverTheLimit_Is429WithRetryAfter_AndNeverReachesTheSidecar()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		string token = await TokenAsync();
		using WebApplicationFactory<Program> limited = Limited("2");
		HttpClient client = AuthApiFactory.Authorized(limited.CreateClient(), token);

		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/collections")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/collections")).StatusCode);
		factory.Sidecar.Requests.Clear();

		HttpResponseMessage rejected = await client.GetAsync("/v1/collections");

		Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
		Assert.True(rejected.Headers.RetryAfter?.Delta > TimeSpan.Zero);
		Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
		Assert.Contains("rate_limited", await rejected.Content.ReadAsStringAsync());
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task Limits_ArePerKey_NotPerAddress()
	{
		// Every test client shares one remote address, as every caller does behind the reverse proxy.
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		string first = await TokenAsync();
		string second = await TokenAsync();
		using WebApplicationFactory<Program> limited = Limited("1");
		HttpClient firstClient = AuthApiFactory.Authorized(limited.CreateClient(), first);
		HttpClient secondClient = AuthApiFactory.Authorized(limited.CreateClient(), second);

		Assert.Equal(HttpStatusCode.OK, (await firstClient.GetAsync("/v1/collections")).StatusCode);
		Assert.Equal(HttpStatusCode.TooManyRequests, (await firstClient.GetAsync("/v1/collections")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await secondClient.GetAsync("/v1/collections")).StatusCode);
	}

	[Fact]
	public async Task ForwardedForHeaders_DoNotEscapeTheLimit()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		string token = await TokenAsync();
		using WebApplicationFactory<Program> limited = Limited("1");
		HttpClient client = AuthApiFactory.Authorized(limited.CreateClient(), token);

		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/collections")).StatusCode);
		client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");

		Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/v1/collections")).StatusCode);
	}

	[Fact]
	public async Task Mcp_IsLimitedToo()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		string token = await TokenAsync();
		using WebApplicationFactory<Program> limited = Limited("1");
		HttpClient client = AuthApiFactory.Authorized(limited.CreateClient(), token);

		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/collections")).StatusCode);

		HttpResponseMessage response = await client.PostAsync("/mcp", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

		Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
	}

	[Fact]
	public async Task AnonymousRoutes_AreNotLimited()
	{
		using WebApplicationFactory<Program> limited = Limited("1");
		HttpClient anonymous = limited.CreateClient();

		for (int i = 0; i < 5; i++)
		{
			Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
		}
	}

	[Fact]
	public async Task LimitsCanBeTurnedOff()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		string token = await TokenAsync();
		using WebApplicationFactory<Program> unlimited = factory.WithWebHostBuilder(builder =>
		{
			builder.UseSetting("Memoria:Auth:RateLimit:Enabled", "false");
			builder.UseSetting("Memoria:Auth:RateLimit:PermitLimit", "1");
		});
		HttpClient client = AuthApiFactory.Authorized(unlimited.CreateClient(), token);

		for (int i = 0; i < 3; i++)
		{
			Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/collections")).StatusCode);
		}
	}
}

public sealed class AuditTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private async Task<AuthApiFactory.AuditRow> RowForAsync(HttpResponseMessage response)
	{
		string traceId = Assert.Single(response.Headers.GetValues(RequestLoggingMiddleware.TraceIdHeader));
		return Assert.Single(await factory.ReadAuditAsync(), row => row.TraceId == traceId);
	}

	[Fact]
	public async Task Search_RecordsKeyOperationCollectionsCountTimeAndTrace_ButNeverTheQuery()
	{
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.SearchResponse);
		(ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Read], ["claude-sessions"]);
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), token);
		DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);

		HttpResponseMessage response = await client.GetAsync("/v1/search?query=the-private-question&intent=the-private-intent");

		AuthApiFactory.AuditRow row = await RowForAsync(response);
		Assert.Equal(record.Id, row.KeyId);
		Assert.Equal("search", row.Operation);
		Assert.Equal("[\"claude-sessions\"]", row.Collections);
		Assert.Equal(1, row.ResultCount);
		Assert.Equal(200, row.Status);
		Assert.InRange(ControlDatabase.ParseTimestamp(row.Timestamp), before, DateTimeOffset.UtcNow.AddSeconds(1));
		Assert.Null(row.Query);
		Assert.DoesNotContain("the-private", row.ToString());
		Assert.DoesNotContain(factory.Logs, l => l.Category.Contains("SqliteAuditLog", StringComparison.Ordinal) && l.Message.Contains("the-private", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Document_RecordsItsCollection_AndNoContent()
	{
		factory.Sidecar.On("GET", "/docs/f6feba", HttpStatusCode.OK, SidecarFixtures.Document);
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], ["claude-sessions"]);
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), token);

		HttpResponseMessage response = await client.GetAsync("/v1/docs/f6feba");

		AuthApiFactory.AuditRow row = await RowForAsync(response);
		Assert.Equal("getDocument", row.Operation);
		Assert.Equal("[\"claude-sessions\"]", row.Collections);
		Assert.Equal(1, row.ResultCount);
		Assert.DoesNotContain("Receipts", row.ToString());
		Assert.DoesNotContain("hello", row.ToString());
	}

	[Fact]
	public async Task OutOfScopeDocument_RecordsNoCollectionAndZeroResults()
	{
		factory.Sidecar.On("GET", "/docs/f6feba", HttpStatusCode.OK, SidecarFixtures.Document);
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], ["codex-sessions"]);
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), token);

		HttpResponseMessage response = await client.GetAsync("/v1/docs/f6feba");

		AuthApiFactory.AuditRow row = await RowForAsync(response);
		Assert.Equal(404, row.Status);
		Assert.Equal("[]", row.Collections);
		Assert.Equal(0, row.ResultCount);
	}

	[Fact]
	public async Task ForbiddenCollection_IsAudited_WithItsStatus()
	{
		(ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Read], ["claude-sessions"]);
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), token);

		HttpResponseMessage response = await client.GetAsync("/v1/search?query=x&collection=codex-sessions");

		AuthApiFactory.AuditRow row = await RowForAsync(response);
		Assert.Equal(record.Id, row.KeyId);
		Assert.Equal(403, row.Status);
	}

	[Fact]
	public async Task InsufficientScope_IsAudited()
	{
		(ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Ingest], allCollections: true);
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), token);

		HttpResponseMessage response = await client.GetAsync("/v1/collections");

		AuthApiFactory.AuditRow row = await RowForAsync(response);
		Assert.Equal(record.Id, row.KeyId);
		Assert.Equal(403, row.Status);
	}

	[Fact]
	public async Task BackendFailure_IsAudited_WithTheMappedStatus()
	{
		factory.Sidecar.On("GET", "/status", HttpStatusCode.InternalServerError, """{"error":"internal","message":"boom"}""");
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), token);

		HttpResponseMessage response = await client.GetAsync("/v1/status");

		Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
		Assert.Equal(502, (await RowForAsync(response)).Status);
	}

	[Fact]
	public async Task AnonymousAndInvalidKeys_WriteNoRows()
	{
		int before = (await factory.ReadAuditAsync()).Count;
		HttpClient anonymous = factory.CreateClient();
		HttpClient invalid = AuthApiFactory.Authorized(factory.CreateClient(), ApiKeyToken.NewToken(ApiKeyToken.NewId()));

		await anonymous.GetAsync("/v1/collections");
		await invalid.GetAsync("/v1/collections");
		await anonymous.GetAsync("/health");
		await anonymous.GetAsync("/ready");

		Assert.Equal(before, (await factory.ReadAuditAsync()).Count);
	}

	[Fact]
	public async Task AdminOperations_AreAudited_WithoutKeyMaterial()
	{
		(ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Admin]);
		HttpClient admin = AuthApiFactory.Authorized(factory.CreateClient(), token);

		HttpResponseMessage response = await admin.GetAsync("/v1/admin/keys");

		AuthApiFactory.AuditRow row = await RowForAsync(response);
		Assert.Equal(record.Id, row.KeyId);
		Assert.Equal("listApiKeys", row.Operation);
		Assert.DoesNotContain(token.Split('_')[2], row.ToString());
	}

	[Fact]
	public async Task McpToolCall_IsAuditedUnderItsToolName_WithTheRightKey()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		(ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Read], ["claude-sessions"]);
		HttpClient http = AuthApiFactory.Authorized(factory.CreateClient(), token);
		HttpClientTransport transport = new(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			TransportMode = HttpTransportMode.StreamableHttp,
			Name = "memoria-tests",
		}, http);
		await using (McpClient client = await McpClient.CreateAsync(transport))
		{
			await client.CallToolAsync("list_collections", new Dictionary<string, object?>());
		}

		AuthApiFactory.AuditRow row = Assert.Single(await factory.ReadAuditAsync(), r => r.KeyId == record.Id && r.Operation == "mcp.list_collections");
		Assert.Equal("[\"claude-sessions\"]", row.Collections);
		Assert.Equal(1, row.ResultCount);
		Assert.Equal(200, row.Status);
	}

	[Fact]
	public async Task IncludeQueries_RecordsTheQueryOnlyWhenExplicitlyEnabled()
	{
		factory.Sidecar.On("POST", "/search", HttpStatusCode.OK, SidecarFixtures.SearchResponse);
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], ["claude-sessions"]);
		using WebApplicationFactory<Program> enabled = factory.WithWebHostBuilder(builder => builder.UseSetting("Memoria:Auth:Audit:IncludeQueries", "true"));
		HttpClient client = AuthApiFactory.Authorized(enabled.CreateClient(), token);

		HttpResponseMessage response = await client.GetAsync("/v1/search?query=opted-in-question");

		Assert.Equal("opted-in-question", (await RowForAsync(response)).Query);
	}

	[Fact]
	public async Task StorageFailure_DoesNotFailTheRequest()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), token);
		ControlDatabase database = factory.Services.GetRequiredService<ControlDatabase>();
		await using (Microsoft.Data.Sqlite.SqliteConnection connection = await database.OpenAsync())
		await using (Microsoft.Data.Sqlite.SqliteCommand rename = connection.CreateCommand())
		{
			rename.CommandText = "ALTER TABLE audit_log RENAME TO audit_log_gone;";
			await rename.ExecuteNonQueryAsync();
		}

		try
		{
			HttpResponseMessage response = await client.GetAsync("/v1/collections");

			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			Assert.Contains(factory.Logs, l => l.Message.Contains("Could not store the audit record", StringComparison.Ordinal));
		}
		finally
		{
			await using Microsoft.Data.Sqlite.SqliteConnection connection = await database.OpenAsync();
			await using Microsoft.Data.Sqlite.SqliteCommand restore = connection.CreateCommand();
			restore.CommandText = "ALTER TABLE audit_log_gone RENAME TO audit_log;";
			await restore.ExecuteNonQueryAsync();
		}
	}

	[Fact]
	public async Task Retention_PrunesOldRecords_OnWrite()
	{
		SqliteAuditLog log = ActivatorUtilities.CreateInstance<SqliteAuditLog>(factory.Services);
		string key = ApiKeyToken.NewId();

		await log.WriteAsync(new AuditRecord(DateTimeOffset.UtcNow.AddDays(-400), key, "old", null, null, 200, "t-old", null));
		await log.WriteAsync(new AuditRecord(DateTimeOffset.UtcNow, key, "new", null, null, 200, "t-new", null));

		List<AuthApiFactory.AuditRow> rows = (await factory.ReadAuditAsync()).Where(r => r.KeyId == key).ToList();
		Assert.DoesNotContain(rows, r => r.Operation == "old");
		Assert.Contains(rows, r => r.Operation == "new");
	}
}
