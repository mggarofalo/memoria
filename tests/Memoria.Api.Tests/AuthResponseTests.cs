using System.Net;
using System.Text;
using System.Text.Json;
using Memoria.Api.Auth;
using Memoria.Api.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memoria.Api.Tests;

/// <summary>
/// What a refused caller is told, what the operator's log says about it, and which refusals cost the address its
/// failed-authentication budget. The body and log are the operator's and client's only clues, so their words are
/// part of the contract.
/// </summary>
public sealed class AuthResponseTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private sealed record Problem(int Status, string Title, string Detail, string Code);

	private static async Task<Problem> ProblemOf(HttpResponseMessage response)
	{
		using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = problem.RootElement;
		return new Problem(root.GetProperty("status").GetInt32(), root.GetProperty("title").GetString()!, root.GetProperty("detail").GetString()!, root.GetProperty("code").GetString()!);
	}

	private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? token, string? peer = null, string? forwardedFor = null)
	{
		HttpRequestMessage request = new(HttpMethod.Get, path);
		if (token is not null)
		{
			request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
		}

		if (peer is not null)
		{
			request.Headers.Add("X-Test-Peer", peer);
		}

		if (forwardedFor is not null)
		{
			request.Headers.Add("X-Forwarded-For", forwardedFor);
		}

		return client.SendAsync(request);
	}

	private static string BadToken()
	{
		return ApiKeyToken.NewToken(ApiKeyToken.NewId());
	}

	/// <summary>Plays the socket: <c>X-Test-Peer</c> is the peer's address.</summary>
	private sealed class PeerFromHeader : IStartupFilter
	{
		public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
		{
			return app =>
			{
				app.Use(static (HttpContext context, RequestDelegate inner) =>
				{
					if (context.Request.Headers.TryGetValue("X-Test-Peer", out Microsoft.Extensions.Primitives.StringValues peer))
					{
						context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
					}

					return inner(context);
				});
				next(app);
			};
		}
	}

	private WebApplicationFactory<Program> FailedAuthBudget(int permits, string? trustedProxy = null)
	{
		return factory.WithWebHostBuilder(builder =>
		{
			builder.UseSetting("Memoria:Auth:FailedAuth:PermitLimit", permits.ToString(System.Globalization.CultureInfo.InvariantCulture));
			builder.UseSetting("Memoria:Auth:FailedAuth:Window", "01:00:00");
			if (trustedProxy is not null)
			{
				builder.UseSetting("Memoria:Auth:TrustedProxies:0", trustedProxy);
			}

			builder.ConfigureTestServices(static services => services.AddSingleton<IStartupFilter, PeerFromHeader>());
		});
	}

	private string[] LoggedReasons()
	{
		return factory.Logs.Where(static l => l.EventId.Id == 3000).Select(static l => l.State.GetValueOrDefault("Reason") as string ?? "(none)").ToArray();
	}

	// --- what the caller is told --------------------------------------------------------------------------------

	[Fact]
	public async Task NoKey_Is401_SayingWhatIsNeeded()
	{
		HttpResponseMessage response = await GetAsync(factory.CreateClient(), "/v1/collections", null);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		Assert.Equal(new Problem(401, "Unauthorized", "A valid API key is required: Authorization: Bearer <key>.", "unauthorized"), await ProblemOf(response));
	}

	[Fact]
	public async Task AKeyWithoutTheScope_Is403_NamingTheScope_WithAnInsufficientScopeChallenge()
	{
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Ingest], ["c"]);

		HttpResponseMessage response = await GetAsync(factory.CreateClient(), "/v1/search?query=x", token);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Equal(new Problem(403, "Insufficient scope", "This API key lacks the 'read' scope.", "insufficient_scope"), await ProblemOf(response));
		Assert.Contains(response.Headers.WwwAuthenticate, static h => h.Scheme == "Bearer" && h.Parameter == "realm=\"memoria\", error=\"insufficient_scope\"");
	}

	[Fact]
	public async Task ABoundAdminKey_OnAnOperationsRoute_Is403_SayingTheKeyIsBound()
	{
		factory.Sidecar.Requests.Clear();
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Admin], ["c"]);

		HttpResponseMessage response = await GetAsync(factory.CreateClient(), "/v1/admin/workers", token);

		Assert.Equal(
			new Problem(403, "Key is bound to collections", "Operations and key management act on every collection, so they need an admin key that lists no collections. A key holding admin beside a collection list is not given the admin scope: issue an admin key without collections, then revoke this one.", "collection_scoped_key"),
			await ProblemOf(response));
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task ABoundKeyWithoutAdmin_OnAnOperationsRoute_IsTold_ItLacksTheScope_NotThatItIsBound()
	{
		factory.Sidecar.Requests.Clear();
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], ["c"]);

		HttpResponseMessage response = await GetAsync(factory.CreateClient(), "/v1/admin/workers", token);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Equal("insufficient_scope", (await ProblemOf(response)).Code);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task AnUnboundKeyWithoutAdmin_OnAnOperationsRoute_IsTold_ItLacksTheScope()
	{
		factory.Sidecar.Requests.Clear();
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);

		HttpResponseMessage response = await GetAsync(factory.CreateClient(), "/v1/admin/workers", token);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Equal("insufficient_scope", (await ProblemOf(response)).Code);
		Assert.Empty(factory.Sidecar.Requests);
	}

	[Fact]
	public async Task AnAddressOverItsFailedAuthBudget_Is429_SayingWhy()
	{
		using WebApplicationFactory<Program> limited = FailedAuthBudget(permits: 1);
		HttpClient client = limited.CreateClient();
		Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/v1/collections", BadToken(), "198.51.100.7")).StatusCode);

		HttpResponseMessage response = await GetAsync(client, "/v1/collections", BadToken(), "198.51.100.7");

		Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
		Assert.Equal(
			new Problem(429, "Too many failed authentication attempts", "This address exceeded its failed-authentication limit; retry after the interval in Retry-After.", "auth_rate_limited"),
			await ProblemOf(response));
	}

	// --- what the operator's log says ---------------------------------------------------------------------------

	[Fact]
	public async Task EachRefusal_LogsItsOwnReason_AndTheCallerCannotTellThemApart()
	{
		HttpClient client = factory.CreateClient();
		(ApiKeyRecord known, _) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		(ApiKeyRecord revoked, string revokedToken) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		await factory.Store.RevokeAsync(revoked.Id, DateTimeOffset.UtcNow);
		(_, string expiredToken) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true, expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));
		string[] tokens = ["not-a-key", BadToken(), ApiKeyToken.NewToken(known.Id), revokedToken, expiredToken];
		string[] expected = ["malformed", "unknown id", "wrong secret", "revoked", "expired"];

		List<string> bodies = [];
		for (int i = 0; i < tokens.Length; i++)
		{
			factory.Logs.Clear();
			HttpResponseMessage response = await GetAsync(client, "/v1/collections", tokens[i]);
			Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
			bodies.Add((await ProblemOf(response)).ToString());
			Assert.Equal([expected[i]], LoggedReasons());
		}

		Assert.Single(bodies.Distinct());
	}

	[Fact]
	public async Task ARequestWithNoKey_NeverSpendsTheAddresssBudget()
	{
		using WebApplicationFactory<Program> limited = FailedAuthBudget(permits: 2);
		HttpClient client = limited.CreateClient();
		const string Peer = "198.51.100.8";

		for (int i = 0; i < 5; i++)
		{
			Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/v1/collections", null, Peer)).StatusCode);
		}

		Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/v1/collections", BadToken(), Peer)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/v1/collections", BadToken(), Peer)).StatusCode);
		Assert.Equal(HttpStatusCode.TooManyRequests, (await GetAsync(client, "/v1/collections", BadToken(), Peer)).StatusCode);
	}

	/// <summary>
	/// The framework trusts loopback by default. Once an operator names a proxy, only that proxy may speak for a client:
	/// a loopback peer's X-Forwarded-For must not spend another address's budget.
	/// </summary>
	[Theory]
	[InlineData("127.0.0.1")]
	[InlineData("::1")]
	public async Task WithAProxyConfigured_ALoopbackPeerIsStillNotTrusted(string loopback)
	{
		using WebApplicationFactory<Program> limited = FailedAuthBudget(permits: 1, trustedProxy: "172.20.0.2");
		HttpClient client = limited.CreateClient();
		const string Victim = "203.0.113.20";

		Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/v1/collections", BadToken(), loopback, Victim)).StatusCode);

		Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/v1/collections", BadToken(), Victim)).StatusCode);
	}

	/// <summary>
	/// Authentication must run after the forwarded headers are read, or every client behind the proxy would share the
	/// proxy's one budget and a single guesser could lock them all out.
	/// </summary>
	[Fact]
	public async Task ThroughATrustedProxy_OneClientsFailures_DoNotSpendAnothersBudget()
	{
		const string Proxy = "172.20.0.2";
		using WebApplicationFactory<Program> limited = FailedAuthBudget(permits: 1, trustedProxy: Proxy);
		HttpClient client = limited.CreateClient();

		Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/v1/collections", BadToken(), Proxy, "198.51.100.9")).StatusCode);
		Assert.Equal(HttpStatusCode.TooManyRequests, (await GetAsync(client, "/v1/collections", BadToken(), Proxy, "198.51.100.9")).StatusCode);

		Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/v1/collections", BadToken(), Proxy, "203.0.113.21")).StatusCode);
	}

	[Fact]
	public async Task AFailedLastUsedWrite_DoesNotFailTheRequest_AndIsLogged()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		(ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
			services.AddSingleton<IApiKeyStore>(provider => new TouchFails(new SqliteApiKeyStore(provider.GetRequiredService<ControlDatabase>())))));
		factory.Logs.Clear();

		HttpResponseMessage response = await GetAsync(app.CreateClient(), "/v1/collections", token);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		MemoriaApiFactory.CapturedLog logged = Assert.Single(factory.Logs, static l => l.EventId.Id == 3001);
		Assert.Equal(LogLevel.Warning, logged.Level);
		Assert.Equal(record.Id, logged.State["KeyId"]);
	}

	// --- the per-key rate limit ---------------------------------------------------------------------------------

	[Fact]
	public async Task AKeyOverItsRateLimit_Is429_WithTheWholeWaitInRetryAfter()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		using WebApplicationFactory<Program> limited = factory.WithWebHostBuilder(static builder =>
		{
			builder.UseSetting("Memoria:Auth:RateLimit:PermitLimit", "1");
			builder.UseSetting("Memoria:Auth:RateLimit:Window", "00:10:00");
		});
		HttpClient client = limited.CreateClient();
		Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/v1/collections", token)).StatusCode);

		HttpResponseMessage response = await GetAsync(client, "/v1/collections", token);

		Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
		Assert.InRange(response.Headers.RetryAfter!.Delta!.Value, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
		Assert.Equal(new Problem(429, "Too many requests", "This API key exceeded its rate limit; retry after the interval in Retry-After.", "rate_limited"), await ProblemOf(response));
	}

	[Fact]
	public async Task AKeysRateLimit_RefillsWhenTheWindowEnds()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		using WebApplicationFactory<Program> limited = factory.WithWebHostBuilder(static builder =>
		{
			builder.UseSetting("Memoria:Auth:RateLimit:PermitLimit", "1");
			builder.UseSetting("Memoria:Auth:RateLimit:Window", "00:00:02");
		});
		HttpClient client = limited.CreateClient();
		Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/v1/collections", token)).StatusCode);
		HttpStatusCode status = (await GetAsync(client, "/v1/collections", token)).StatusCode;
		Assert.Equal(HttpStatusCode.TooManyRequests, status);

		for (DateTime deadline = DateTime.UtcNow.AddSeconds(15); status == HttpStatusCode.TooManyRequests && DateTime.UtcNow < deadline;)
		{
			await Task.Delay(100);
			status = (await GetAsync(client, "/v1/collections", token)).StatusCode;
		}

		Assert.Equal(HttpStatusCode.OK, status);
	}

	// --- the audit trail ----------------------------------------------------------------------------------------

	[Fact]
	public async Task AKeyOnARouteWithNoScope_LeavesNoAuditRow()
	{
		(ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);

		Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory.CreateClient(), "/health", token)).StatusCode);
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory.CreateClient(), "/v1/collections", token)).StatusCode);

		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(rows => rows.Any(r => r.KeyId == record.Id));
		AuthApiFactory.AuditRow row = Assert.Single(rows, r => r.KeyId == record.Id);
		Assert.Matches("^[0-9a-f]{32}$", row.AuditId);
	}

	[Fact]
	public async Task AnUnnamedRoute_IsAuditedUnderItsRoutePattern()
	{
		(ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), token);
		StringContent initialize = new("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""", Encoding.UTF8, "application/json");
		initialize.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
		using HttpRequestMessage request = new(HttpMethod.Post, "/mcp") { Content = initialize };
		request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

		Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);

		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(rows => rows.Any(r => r.KeyId == record.Id));
		Assert.Equal("/mcp/", Assert.Single(rows, r => r.KeyId == record.Id).Operation);
	}

	/// <summary>Fails every <c>last_used_at</c> write, as a locked database file would.</summary>
	private sealed class TouchFails(IApiKeyStore inner) : IApiKeyStore
	{
		public Task InitializeAsync(CancellationToken cancellationToken = default)
		{
			return inner.InitializeAsync(cancellationToken);
		}

		public Task InsertAsync(ApiKeyRecord record, CancellationToken cancellationToken = default)
		{
			return inner.InsertAsync(record, cancellationToken);
		}

		public Task<ApiKeyRecord?> FindAsync(string id, CancellationToken cancellationToken = default)
		{
			return inner.FindAsync(id, cancellationToken);
		}

		public Task<IReadOnlyList<ApiKeyRecord>> ListAsync(CancellationToken cancellationToken = default)
		{
			return inner.ListAsync(cancellationToken);
		}

		public Task<bool> RevokeAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default)
		{
			return inner.RevokeAsync(id, at, cancellationToken);
		}

		public Task<bool> ReplaceHashAsync(string id, byte[] sha256Hash, CancellationToken cancellationToken = default)
		{
			return inner.ReplaceHashAsync(id, sha256Hash, cancellationToken);
		}

		public Task TouchLastUsedAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default)
		{
			throw new SqliteException("database is locked", 5);
		}

		public Task<bool> HasActiveAdminAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
		{
			return inner.HasActiveAdminAsync(now, cancellationToken);
		}
	}
}
