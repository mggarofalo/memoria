using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Memoria.Api.Auth;
using Memoria.Api.Http;
using Memoria.Core.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memoria.Api.Tests;

public sealed class AuthenticationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> GetCollectionsWithHeaderAsync(string? authorization)
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		HttpClient client = factory.CreateClient();
		using HttpRequestMessage request = new(HttpMethod.Get, "/v1/collections");
		if (authorization is not null)
		{
			request.Headers.TryAddWithoutValidation("Authorization", authorization);
		}

		HttpResponseMessage response = await client.SendAsync(request);
		return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
	}

	[Fact]
	public async Task MissingKey_Is401WithChallenge_AndNeverReachesTheSidecar()
	{
		factory.Sidecar.Requests.Clear();

		(HttpStatusCode status, string body, HttpResponseMessage response) = await GetCollectionsWithHeaderAsync(null);

		Assert.Equal(HttpStatusCode.Unauthorized, status);
		Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
		Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
		Assert.Contains("\"code\":\"unauthorized\"", body);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Theory]
	[InlineData("Bearer")]
	[InlineData("Bearer ")]
	[InlineData("Basic dXNlcjpwYXNz")]
	[InlineData("mem_0123456789abcdef_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
	[InlineData("Bearer not-a-key")]
	[InlineData("Bearer mem_0123456789abcdef")]
	[InlineData("Bearer MEM_0123456789abcdef_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
	public async Task MalformedKey_Is401(string header)
	{
		factory.Sidecar.Requests.Clear();

		(HttpStatusCode status, _, _) = await GetCollectionsWithHeaderAsync(header);

		Assert.Equal(HttpStatusCode.Unauthorized, status);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task TwoAuthorizationHeaders_Are401()
	{
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		HttpClient client = factory.CreateClient();
		using HttpRequestMessage request = new(HttpMethod.Get, "/v1/collections");
		request.Headers.TryAddWithoutValidation("Authorization", [$"Bearer {token}", $"Bearer {token}"]);

		HttpResponseMessage response = await client.SendAsync(request);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task WrongSecret_ForARealId_Is401()
	{
		(ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		string forged = ApiKeyToken.NewToken(record.Id);
		Assert.NotEqual(token, forged);

		(HttpStatusCode status, _, _) = await GetCollectionsWithHeaderAsync($"Bearer {forged}");

		Assert.Equal(HttpStatusCode.Unauthorized, status);
	}

	[Fact]
	public async Task UnknownId_Is401()
	{
		(HttpStatusCode status, _, _) = await GetCollectionsWithHeaderAsync($"Bearer {ApiKeyToken.NewToken(ApiKeyToken.NewId())}");

		Assert.Equal(HttpStatusCode.Unauthorized, status);
	}

	[Fact]
	public async Task ExpiredKey_Is401()
	{
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true, expiresAt: DateTimeOffset.UtcNow.AddSeconds(-5));

		(HttpStatusCode status, _, _) = await GetCollectionsWithHeaderAsync($"Bearer {token}");

		Assert.Equal(HttpStatusCode.Unauthorized, status);
	}

	[Fact]
	public async Task RevokedKey_Is401_OnTheVeryNextRequest()
	{
		(ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		Assert.Equal(HttpStatusCode.OK, (await GetCollectionsWithHeaderAsync($"Bearer {token}")).Status);

		await factory.Store.RevokeAsync(record.Id, DateTimeOffset.UtcNow);

		Assert.Equal(HttpStatusCode.Unauthorized, (await GetCollectionsWithHeaderAsync($"Bearer {token}")).Status);
	}

	[Fact]
	public async Task StoreFailure_FailsClosed()
	{
		factory.Sidecar.Requests.Clear();
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		ControlDatabase database = factory.Services.GetRequiredService<ControlDatabase>();
		await RenameKeysTableAsync(database, "api_keys", "api_keys_gone");

		try
		{
			HttpResponseMessage response = (await GetCollectionsWithHeaderAsync($"Bearer {token}")).Response;

			Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
			Assert.Empty(factory.Sidecar.Requests);
			Assert.DoesNotContain("api_keys", await response.Content.ReadAsStringAsync());
		}
		finally
		{
			await RenameKeysTableAsync(database, "api_keys_gone", "api_keys");
		}
	}

	private static async Task RenameKeysTableAsync(ControlDatabase database, string from, string to)
	{
		await using Microsoft.Data.Sqlite.SqliteConnection connection = await database.OpenAsync();
		await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
		command.CommandText = $"ALTER TABLE {from} RENAME TO {to};";
		await command.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task EveryRejection_LooksTheSameToTheCaller()
	{
		(ApiKeyRecord real, _) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		(_, string expired) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true, expiresAt: DateTimeOffset.UtcNow.AddSeconds(-5));
		(ApiKeyRecord revokedRecord, string revoked) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		await factory.Store.RevokeAsync(revokedRecord.Id, DateTimeOffset.UtcNow);

		string?[] headers = [null, "Bearer garbage", $"Bearer {ApiKeyToken.NewToken(ApiKeyToken.NewId())}", $"Bearer {ApiKeyToken.NewToken(real.Id)}", $"Bearer {expired}", $"Bearer {revoked}"];
		HashSet<string> bodies = [];
		foreach (string? header in headers)
		{
			(HttpStatusCode status, string body, _) = await GetCollectionsWithHeaderAsync(header);
			Assert.Equal(HttpStatusCode.Unauthorized, status);
			using JsonDocument parsed = JsonDocument.Parse(body);
			bodies.Add($"{parsed.RootElement.GetProperty("title")}|{parsed.RootElement.GetProperty("detail")}|{parsed.RootElement.GetProperty("code")}");
		}

		Assert.Single(bodies);
	}

	[Fact]
	public async Task ValidKey_IsAccepted_CaseInsensitiveBearer_AndLastUsedIsWritten()
	{
		(ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);

		(HttpStatusCode status, _, _) = await GetCollectionsWithHeaderAsync($"bearer {token}");

		Assert.Equal(HttpStatusCode.OK, status);
		Assert.NotNull((await factory.Store.FindAsync(record.Id))!.LastUsedAt);
	}

	[Fact]
	public async Task RepeatedRequests_WriteLastUsedOnce()
	{
		(ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		await GetCollectionsWithHeaderAsync($"Bearer {token}");
		DateTimeOffset first = (await factory.Store.FindAsync(record.Id))!.LastUsedAt!.Value;

		await Task.Delay(5);
		await GetCollectionsWithHeaderAsync($"Bearer {token}");

		Assert.Equal(first, (await factory.Store.FindAsync(record.Id))!.LastUsedAt);
	}

	[Fact]
	public async Task SidecarNeverSeesTheCallersKey_OnlyItsOwnSecret()
	{
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		factory.Sidecar.Requests.Clear();

		await GetCollectionsWithHeaderAsync($"Bearer {token}");

		FakeSidecar.RecordedRequest sent = factory.Sidecar.Single();
		Assert.Equal("Bearer " + MemoriaApiFactory.SidecarSecret, sent.Headers["Authorization"]);
	}

	[Fact]
	public async Task KeyWithoutReadScope_Is403OnEveryReadRoute()
	{
		factory.Sidecar.Requests.Clear();
		HttpClient ingestOnly = await factory.ClientWithScopesAsync([MemoriaScopes.Ingest], allCollections: true);

		foreach (string route in new[] { "/v1/search?query=x", "/v1/docs/a.md", "/v1/collections", "/v1/status" })
		{
			HttpResponseMessage response = await ingestOnly.GetAsync(route);
			Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
			string body = await response.Content.ReadAsStringAsync();
			Assert.Contains("\"code\":\"insufficient_scope\"", body);
			Assert.Contains("'read'", body);
		}

		Assert.Equal(HttpStatusCode.Forbidden, (await ingestOnly.PostAsync("/v1/docs:multiGet", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task AdminDoesNotImplyRead_EvenWithAllCollections()
	{
		factory.Sidecar.Requests.Clear();
		HttpClient admin = await factory.ClientWithScopesAsync([MemoriaScopes.Admin], allCollections: true);

		Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/v1/search?query=x")).StatusCode);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task ReadDoesNotImplyAdmin()
	{
		HttpClient reader = await factory.ClientWithScopesAsync([MemoriaScopes.Read], allCollections: true);

		Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/v1/admin/keys")).StatusCode);
	}

	[Fact]
	public async Task HealthAndOpenApi_AreAnonymous()
	{
		HttpClient anonymous = factory.CreateClient();

		Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/openapi/v1.json")).StatusCode);
	}

	[Fact]
	public async Task Ready_Anonymous_RevealsOnlyReady()
	{
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.Readiness("ready", resident: true));

		HttpResponseMessage response = await factory.CreateClient().GetAsync("/ready");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		AssertBareReadiness(await response.Content.ReadAsStringAsync(), ReadinessStatus.Ready);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Ready_Anonymous_NotReady_HidesWhy(bool sidecarAnswers)
	{
		if (sidecarAnswers)
		{
			factory.Sidecar.On("GET", "/ready", HttpStatusCode.ServiceUnavailable, SidecarFixtures.Readiness("warming", resident: false, needsEmbedding: 7, warmupError: "CUDA out of memory"));
		}
		else
		{
			factory.Sidecar.Unreachable();
		}

		HttpResponseMessage response = await factory.CreateClient().GetAsync("/ready");

		Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
		AssertBareReadiness(await response.Content.ReadAsStringAsync(), ReadinessStatus.NotReady);
	}

	[Fact]
	public async Task Ready_BadKeyOrNonAdminKey_StaysAnonymousInShape()
	{
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.Readiness("ready", resident: true));
		HttpClient badKey = AuthApiFactory.Authorized(factory.CreateClient(), "garbage");
		HttpClient reader = await factory.ClientWithScopesAsync([MemoriaScopes.Read], allCollections: true);

		HttpResponseMessage bad = await badKey.GetAsync("/ready");
		HttpResponseMessage nonAdmin = await reader.GetAsync("/ready");

		Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
		AssertBareReadiness(await bad.Content.ReadAsStringAsync(), ReadinessStatus.Ready);
		AssertBareReadiness(await nonAdmin.Content.ReadAsStringAsync(), ReadinessStatus.Ready);
	}

	[Fact]
	public async Task Ready_AdminKey_GetsTheFullReport()
	{
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.Readiness("ready", resident: true));
		HttpClient admin = await factory.AdminAsync();

		string body = await (await admin.GetAsync("/ready")).Content.ReadAsStringAsync();

		Assert.Contains("/index.sqlite", body);
		Assert.Contains("1502", body);
	}

	[Fact]
	public async Task NothingLogged_ContainsKeyMaterial_OrTheAuthorizationHeader()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		(ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		string forged = ApiKeyToken.NewToken(record.Id);
		factory.Logs.Clear();

		await GetCollectionsWithHeaderAsync($"Bearer {token}");
		await GetCollectionsWithHeaderAsync($"Bearer {forged}");
		await GetCollectionsWithHeaderAsync("Bearer not-a-key-but-possibly-a-secret");

		Assert.NotEmpty(factory.Logs);
		string secret = token.Split('_')[2];
		string forgedSecret = forged.Split('_')[2];
		foreach (MemoriaApiFactory.CapturedLog line in factory.Logs)
		{
			string text = line.Message + " " + string.Join(" ", line.State.Values);
			Assert.DoesNotContain(secret, text);
			Assert.DoesNotContain(forgedSecret, text);
			Assert.DoesNotContain("not-a-key-but-possibly-a-secret", text);
			Assert.DoesNotContain("Bearer ", text);
		}
	}

	[Fact]
	public async Task RequestLogLine_NamesThePublicKeyId()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		(ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		factory.Logs.Clear();

		HttpResponseMessage response = (await GetCollectionsWithHeaderAsync($"Bearer {token}")).Response;

		string traceId = Assert.Single(response.Headers.GetValues(RequestLoggingMiddleware.TraceIdHeader));
		MemoriaApiFactory.CapturedLog line = Assert.Single(factory.Logs, l => l.Category == typeof(RequestLoggingMiddleware).FullName && (string?)l.State.GetValueOrDefault("TraceId") == traceId);
		Assert.Equal(record.Id, line.State["KeyId"]);
	}

	private static void AssertBareReadiness(string body, string status)
	{
		using JsonDocument parsed = JsonDocument.Parse(body);
		Assert.Equal(status, parsed.RootElement.GetProperty("status").GetString());
		Assert.Equal(status == ReadinessStatus.Ready, parsed.RootElement.GetProperty("ready").GetBoolean());
		Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("reason").ValueKind);
		Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("sidecar").ValueKind);

		foreach (string secret in new[] { "index.sqlite", "1502", "sidecar.test", "CUDA", "gguf", "warming", "unavailable" })
		{
			Assert.DoesNotContain(secret, body);
		}
	}
}

public sealed class AuthConfigurationTests
{
	[Fact]
	public void ModeOmitted_WithoutADatabase_FailsAtStartup()
	{
		using UnconfiguredFactory factory = new(builder => { });

		Exception failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

		Assert.Contains("Memoria:Auth:DatabasePath", failure.ToString());
	}

	[Fact]
	public void UnknownMode_FailsAtStartup()
	{
		using UnconfiguredFactory factory = new(builder => builder.UseSetting("Memoria:Auth:Mode", "Off"));

		Assert.ThrowsAny<Exception>(() => factory.CreateClient());
	}

	[Fact]
	public void BadRateLimit_FailsAtStartup()
	{
		using UnconfiguredFactory factory = new(builder =>
		{
			builder.UseSetting("Memoria:Auth:Mode", "Disabled");
			builder.UseSetting("Memoria:Auth:RateLimit:PermitLimit", "0");
		});

		Exception failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

		Assert.Contains("PermitLimit", failure.ToString());
	}

	[Fact]
	public async Task ModeOmitted_WithADatabase_RequiresKeys()
	{
		string path = Path.Combine(Path.GetTempPath(), "memoria-tests", Guid.NewGuid().ToString("N"), "control.db");
		using UnconfiguredFactory factory = new(builder => builder.UseSetting("Memoria:Auth:DatabasePath", path));

		HttpResponseMessage response = await factory.CreateClient().GetAsync("/v1/collections");

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
	}

	[Fact]
	public async Task DisabledMode_LogsALoudWarning_AndAllowsAnonymousRead()
	{
		using MemoriaApiFactory factory = new();
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);

		HttpResponseMessage response = await factory.CreateClient().GetAsync("/v1/collections");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains(factory.Logs, l => l.Level == LogLevel.Warning && l.Message.Contains("AUTHENTICATION IS DISABLED", StringComparison.Ordinal));
	}

	[Fact]
	public async Task DisabledMode_KeyManagementIsUnavailable()
	{
		using MemoriaApiFactory factory = new();
		HttpClient anonymous = factory.CreateClient();

		HttpResponseMessage response = await anonymous.GetAsync("/v1/admin/keys");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Contains("auth_disabled", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task DisabledMode_NoAuditIsAttempted_AndNoDatabaseIsNeeded()
	{
		using MemoriaApiFactory factory = new();

		HttpResponseMessage response = await factory.CreateClient().GetAsync("/health");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.IsType<NullAuditLog>(factory.Services.GetRequiredService<IAuditLog>());
	}

	private sealed class UnconfiguredFactory(Action<IWebHostBuilder> configure) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Production");
			configure(builder);
		}
	}
}
