using System.Net;
using System.Security.Claims;
using Memoria.Api.Auth;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Memoria.Api.Tests;

public sealed class MemoriaOptionsValidatorTests
{
	private static MemoriaOptions Valid()
	{
		return new MemoriaOptions { Auth = new AuthOptions { DatabasePath = "control.db" }, Ingest = new IngestOptions { DigestKey = "a-key-of-at-least-thirty-two-characters" } };
	}

	public static TheoryData<string, Action<MemoriaOptions>> Invalid => new()
	{
		{ "Sidecar:BaseUrl", static o => o.Sidecar.BaseUrl = "not a url" },
		{ "Sidecar:BaseUrl", static o => o.Sidecar.BaseUrl = "ftp://host" },
		{ "Sidecar:Secret", static o => o.Sidecar.BaseUrl = "http://192.168.1.20:8282" },
		{ "Sidecar:Timeout", static o => o.Sidecar.Timeout = TimeSpan.Zero },
		{ "Sidecar:ReadinessTimeout", static o => o.Sidecar.ReadinessTimeout = TimeSpan.Zero },
		{ "Search:MaxLimit", static o => o.Search.MaxLimit = 0 },
		{ "Search:MaxLimit", static o => o.Search.MaxLimit = 101 },
		{ "Search:DefaultLimit", static o => o.Search.DefaultLimit = 0 },
		{ "Search:DefaultLimit", static o => o.Search.DefaultLimit = 101 },
		{ "Search:MaxMultiGetBytes", static o => o.Search.MaxMultiGetBytes = 0 },
		{ "Search:MaxMultiGetBytes", static o => o.Search.MaxMultiGetBytes = (256 * 1024 * 1024) + 1 },
		{ "Mcp:Path", static o => o.Mcp.Path = " " },
		{ "Mcp:Path", static o => o.Mcp.Path = "mcp" },
		{ "Mcp:Path", static o => o.Mcp.Path = "/V1/mcp" },
		{ "Auth:Mode", static o => o.Auth.Mode = "disabled" },
		{ "Auth:Mode", static o => o.Auth.Mode = null },
		{ "Auth:DatabasePath", static o => o.Auth.DatabasePath = " " },
		{ "Auth:TrustedProxies", static o => o.Auth.TrustedProxies = ["nope"] },
		{ "Auth:FailedAuth:PermitLimit", static o => o.Auth.FailedAuth.PermitLimit = 0 },
		{ "Auth:FailedAuth:Window", static o => o.Auth.FailedAuth.Window = TimeSpan.Zero },
		{ "Auth:FailedAuth:MaxTrackedAddresses", static o => o.Auth.FailedAuth.MaxTrackedAddresses = 1 },
		{ "Auth:RateLimit:PermitLimit", static o => o.Auth.RateLimit.PermitLimit = 0 },
		{ "Auth:RateLimit:Window", static o => o.Auth.RateLimit.Window = TimeSpan.Zero },
		{ "Auth:Audit:MaxRowsPerKey", static o => o.Auth.Audit.MaxRowsPerKey = 0 },
	};

	public static TheoryData<Action<MemoriaOptions>> Acceptable => new()
	{
		static o => { },
		static o => o.Sidecar.BaseUrl = "https://[::1]:8282",
		static o => { o.Sidecar.BaseUrl = "http://192.168.1.20:8282"; o.Sidecar.Secret = "s"; },
		static o => { o.Mcp.Enabled = false; o.Mcp.Path = "nonsense"; },
		static o => { o.Auth.Mode = AuthModes.Disabled; o.Auth.DatabasePath = null; },
		static o => o.Auth.TrustedProxies = ["172.20.0.2", " ", "10.0.0.0/8"],
		static o => o.Search.MaxMultiGetBytes = 256 * 1024 * 1024,
	};

	[Theory]
	[MemberData(nameof(Invalid))]
	public void Invalid_NamesTheKey(string key, Action<MemoriaOptions> change)
	{
		MemoriaOptions options = Valid();
		change(options);

		var result = new MemoriaOptionsValidator().Validate(null, options);

		Assert.True(result.Failed);
		Assert.Contains($"Memoria:{key}", result.FailureMessage);
	}

	[Theory]
	[MemberData(nameof(Acceptable))]
	public void Acceptable_Passes(Action<MemoriaOptions> change)
	{
		MemoriaOptions options = Valid();
		change(options);

		Assert.True(new MemoriaOptionsValidator().Validate(null, options).Succeeded);
	}
}

public sealed class HttpCallerScopeTests
{
	private static HttpCallerScope ScopeFor(ClaimsPrincipal? user, bool hasContext = true)
	{
		HttpContextAccessor accessor = new();
		if (hasContext)
		{
			accessor.HttpContext = new DefaultHttpContext { User = user ?? new ClaimsPrincipal() };
		}

		return new HttpCallerScope(accessor);
	}

	private static ClaimsPrincipal Authenticated(params (string Type, string Value)[] claims)
	{
		return new ClaimsPrincipal(new ClaimsIdentity(claims.Select(static c => new Claim(c.Type, c.Value)), "test"));
	}

	[Fact]
	public void NoRequest_MeansNoAccess()
	{
		HttpCallerScope scope = ScopeFor(null, hasContext: false);

		Assert.Empty(scope.Collections!);
		Assert.False(scope.SeesBackendDetails);
	}

	[Fact]
	public void UnauthenticatedCaller_MeansNoAccess_EvenWithClaims()
	{
		ClaimsPrincipal forged = new(new ClaimsIdentity([new Claim(MemoriaClaims.AllCollections, "true"), new Claim(MemoriaClaims.Scope, MemoriaScopes.Admin)]));
		HttpCallerScope scope = ScopeFor(forged);

		Assert.Empty(scope.Collections!);
		Assert.False(scope.SeesBackendDetails);
	}

	[Fact]
	public void Collections_ComeFromTheClaims()
	{
		Assert.Equal(["a", "b"], ScopeFor(Authenticated((MemoriaClaims.Collection, "a"), (MemoriaClaims.Collection, "b"))).Collections!);
		Assert.Null(ScopeFor(Authenticated((MemoriaClaims.AllCollections, "true"))).Collections);
	}

	[Theory]
	[InlineData(MemoriaClaims.Scope, MemoriaScopes.Admin, true)]
	[InlineData(MemoriaClaims.AuthDisabled, "true", true)]
	[InlineData(MemoriaClaims.Scope, MemoriaScopes.Read, false)]
	public void BackendDetails_AreForAdminsAndDisabledMode(string type, string value, bool expected)
	{
		Assert.Equal(expected, ScopeFor(Authenticated((type, value))).SeesBackendDetails);
	}
}

public sealed class MemoriaScopeHandlerTests
{
	private static async Task<bool> SucceedsAsync(ClaimsPrincipal user, string scope)
	{
		MemoriaScopeRequirement requirement = new(scope);
		AuthorizationHandlerContext context = new([requirement], user, null);
		await new MemoriaScopeHandler().HandleAsync(context);
		return context.HasSucceeded;
	}

	[Fact]
	public async Task RequiresAnAuthenticatedCaller_WithExactlyThatScope()
	{
		Claim read = new(MemoriaClaims.Scope, MemoriaScopes.Read);

		Assert.True(await SucceedsAsync(new ClaimsPrincipal(new ClaimsIdentity([read], "test")), MemoriaScopes.Read));
		Assert.False(await SucceedsAsync(new ClaimsPrincipal(new ClaimsIdentity([read], "test")), MemoriaScopes.Admin));
		Assert.False(await SucceedsAsync(new ClaimsPrincipal(new ClaimsIdentity([read])), MemoriaScopes.Read));
		Assert.False(await SucceedsAsync(new ClaimsPrincipal(), MemoriaScopes.Read));
	}
}

public sealed class ControlDatabaseTests
{
	[Theory]
	[InlineData(null)]
	[InlineData("  ")]
	public async Task NoPath_SaysWhichKeyIsMissing(string? path)
	{
		ControlDatabase database = new(path);

		InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => database.OpenAsync());

		Assert.Contains("Memoria:Auth:DatabasePath", failure.Message);
	}
}

public sealed class SidecarStatusMappingTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	[Theory]
	[InlineData(HttpStatusCode.BadRequest, HttpStatusCode.BadRequest)]
	[InlineData(HttpStatusCode.Conflict, HttpStatusCode.Conflict)]
	[InlineData(HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.RequestEntityTooLarge)]
	[InlineData(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable)]
	[InlineData(HttpStatusCode.Forbidden, HttpStatusCode.BadGateway)]
	[InlineData(HttpStatusCode.UnprocessableEntity, HttpStatusCode.BadGateway)]
	public async Task EachUpstreamStatus_MapsAsDocumented(HttpStatusCode upstream, HttpStatusCode expected)
	{
		factory.Sidecar.On("GET", "/collections", upstream, """{"error":"x","message":"m"}""");

		Assert.Equal(expected, (await factory.CreateClient().GetAsync("/v1/collections")).StatusCode);
	}

	[Fact]
	public async Task WarmingWithModelsLoaded_NamesTheEmbeddingBacklog()
	{
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.ServiceUnavailable, SidecarFixtures.Readiness("warming", resident: true, needsEmbedding: 7));

		Assert.Contains("7 documents await embedding", await (await factory.CreateClient().GetAsync("/ready")).Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task WarmingWithNothingToSay_ReportsTheSidecarsStatus()
	{
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.ServiceUnavailable, SidecarFixtures.Readiness("warming", resident: true));

		Assert.Contains("sidecar reports status 'warming'", await (await factory.CreateClient().GetAsync("/ready")).Content.ReadAsStringAsync());
	}
}

public sealed class McpFailureOutcomeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private async Task<(McpClient Client, string KeyId)> ConnectAsync()
	{
		(ApiKeyRecord record, string token) = await factory.IssueAsync([MemoriaScopes.Read], ["claude-sessions"]);
		HttpClient http = AuthApiFactory.Authorized(factory.CreateClient(), token);
		McpClient client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			TransportMode = HttpTransportMode.StreamableHttp,
			Name = "memoria-tests",
		}, http));
		return (client, record.Id);
	}

	[Fact]
	public async Task BlankReference_IsAuditedAsRejected()
	{
		(McpClient client, string keyId) = await ConnectAsync();
		await using (client)
		{
			CallToolResult result = await client.CallToolAsync("get_document", new Dictionary<string, object?> { ["reference"] = "  " });
			Assert.True(result.IsError);
		}

		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(found => found.Any(r => r.KeyId == keyId && r.Operation == "mcp.get_document"));
		Assert.Equal(400, Assert.Single(rows, r => r.KeyId == keyId && r.Operation == "mcp.get_document").Status);
	}

	[Fact]
	public async Task IdsAndPatternTogether_BothReachTheSidecar()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("POST", "/docs:multiGet", HttpStatusCode.OK, SidecarFixtures.MultiGet);
		(McpClient client, _) = await ConnectAsync();
		await using (client)
		{
			await client.CallToolAsync("get_documents", new Dictionary<string, object?> { ["ids"] = new[] { "a.md" }, ["pattern"] = "b*.md" });
		}

		FakeSidecar.RecordedRequest sent = Assert.Single(factory.Sidecar.Requests, static r => r.Uri.AbsolutePath == "/docs:multiGet");
		Assert.Contains("a.md", sent.Body);
		Assert.Contains("b*.md", sent.Body);
	}

	[Fact]
	public async Task UnexpectedFailure_IsAuditedAs500_AndHidesItsMessage()
	{
		factory.Sidecar.On("GET", "/collections", (_, _) => throw new InvalidOperationException("internal detail"));
		(McpClient client, string keyId) = await ConnectAsync();
		await using (client)
		{
			CallToolResult result = await client.CallToolAsync("list_collections", new Dictionary<string, object?>());
			Assert.True(result.IsError);
			Assert.DoesNotContain("internal detail", string.Join(" ", result.Content.OfType<TextContentBlock>().Select(static c => c.Text)));
		}

		List<AuthApiFactory.AuditRow> rows = await factory.WaitForAuditAsync(found => found.Any(r => r.KeyId == keyId && r.Operation == "mcp.list_collections"));
		Assert.Equal(500, Assert.Single(rows, r => r.KeyId == keyId && r.Operation == "mcp.list_collections").Status);
	}
}
