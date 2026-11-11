using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Memoria.Api.Auth;
using Memoria.Api.Http;
using Memoria.Core.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Memoria.Api.Tests;

/// <summary>
/// An admin key is all-or-nothing: it cannot be bound to collections, a bound one already in the database gets no admin
/// power, and no key makes a key wider than itself. The escape these close: a bound admin key minted an all-collections
/// key, read a collection outside its binding with it, and revoked the bootstrap key.
/// </summary>
public sealed class KeyBindingTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private static async Task<string?> CodeOf(HttpResponseMessage response)
	{
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return body.RootElement.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
	}

	private HttpClient Client(string token)
	{
		return AuthApiFactory.Authorized(factory.CreateClient(), token);
	}

	[Theory]
	[InlineData("""{"label":"bound-admin","scopes":["admin","read"],"collections":["work"]}""")]
	[InlineData("""{"label":"bound-admin","scopes":["admin"],"collections":["work"]}""")]
	[InlineData("""{"label":"bound-admin","scopes":["ingest","admin"],"collections":["work","secret"]}""")]
	[InlineData("""{"label":"bound-admin","scopes":["admin"],"collections":[]}""")]
	public async Task Issuing_admin_beside_a_collection_list_is_refused_with_a_code(string body)
	{
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsync("/v1/admin/keys", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal("admin_key_bound", await CodeOf(response));
		Assert.Contains("cannot list collections", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Other_validation_failures_carry_no_binding_code()
	{
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsJsonAsync("/v1/admin/keys", new CreateApiKeyRequest("x", [MemoriaScopes.Read], null, null, null), Json);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Null(await CodeOf(response));
	}

	[Fact]
	public async Task Admin_beside_a_data_scope_on_every_collection_is_still_issued()
	{
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsJsonAsync("/v1/admin/keys", new CreateApiKeyRequest("ops", [MemoriaScopes.Admin, MemoriaScopes.Read], null, true, null), Json);

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
	}

	[Fact]
	public async Task A_bound_admin_key_already_in_the_database_cannot_escape_its_binding()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		(ApiKeyRecord bootstrap, _) = await factory.IssueAsync([MemoriaScopes.Admin], label: "bootstrap");
		(ApiKeyRecord boundRecord, string boundToken) = await factory.IssueAsync([MemoriaScopes.Admin, MemoriaScopes.Read], ["claude-sessions"], label: "bound-admin");
		HttpClient bound = Client(boundToken);
		int before = (await factory.Store.ListAsync()).Count;

		HttpResponseMessage minted = await bound.PostAsJsonAsync("/v1/admin/keys", new CreateApiKeyRequest("escalated", [MemoriaScopes.Read, MemoriaScopes.Admin], null, true, null), Json);
		HttpResponseMessage revoked = await bound.DeleteAsync($"/v1/admin/keys/{bootstrap.Id}");
		HttpResponseMessage rotated = await bound.PostAsync($"/v1/admin/keys/{bootstrap.Id}:rotate", null);
		HttpResponseMessage listed = await bound.GetAsync("/v1/admin/keys");
		HttpResponseMessage queue = await bound.GetAsync("/v1/admin/queue");

		foreach (HttpResponseMessage refused in new[] { minted, revoked, rotated, listed, queue })
		{
			Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
			Assert.Equal("collection_scoped_key", await CodeOf(refused));
		}

		Assert.Equal(before, (await factory.Store.ListAsync()).Count);
		ApiKeyRecord after = (await factory.Store.FindAsync(bootstrap.Id))!;
		Assert.Null(after.RevokedAt);
		Assert.Equal(bootstrap.Sha256Hash, after.Sha256Hash);

		// It keeps what it was bound to.
		Assert.Equal(HttpStatusCode.OK, (await bound.GetAsync("/v1/collections")).StatusCode);
		Assert.False(boundRecord.IsActiveAdmin(DateTimeOffset.UtcNow));
	}

	[Fact]
	public async Task A_bound_admin_key_sees_no_more_of_readiness_than_anyone_else()
	{
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.Readiness("warming", resident: false));
		(_, string boundToken) = await factory.IssueAsync([MemoriaScopes.Admin, MemoriaScopes.Read], ["claude-sessions"]);
		HttpClient admin = await factory.AdminAsync();

		ReadinessReport scoped = (await (await Client(boundToken).GetAsync("/ready")).Content.ReadFromJsonAsync<ReadinessReport>(Json))!;
		ReadinessReport full = (await (await admin.GetAsync("/ready")).Content.ReadFromJsonAsync<ReadinessReport>(Json))!;

		Assert.Null(scoped.Reason);
		Assert.Null(scoped.Sidecar);
		Assert.NotNull(full.Reason);
		Assert.NotNull(full.Sidecar);
	}

	[Fact]
	public async Task A_bound_admin_key_is_logged_at_startup_by_id()
	{
		(ApiKeyRecord bound, _) = await factory.IssueAsync([MemoriaScopes.Ingest, MemoriaScopes.Admin], ["c"]);
		(ApiKeyRecord unbound, _) = await factory.IssueAsync([MemoriaScopes.Admin]);
		(ApiKeyRecord revoked, _) = await factory.IssueAsync([MemoriaScopes.Admin], ["c"]);
		await factory.Store.RevokeAsync(revoked.Id, DateTimeOffset.UtcNow);

		using WebApplicationFactory<Program> restarted = factory.WithWebHostBuilder(static _ => { });
		_ = restarted.CreateClient();

		List<MemoriaApiFactory.CapturedLog> warnings = [.. factory.Logs.Where(static l => l.EventId.Id == 3201)];
		Assert.Contains(warnings, l => l.Level == LogLevel.Warning && l.Message.Contains(bound.Id, StringComparison.Ordinal));
		Assert.DoesNotContain(warnings, l => l.Message.Contains(unbound.Id, StringComparison.Ordinal));
		Assert.DoesNotContain(warnings, l => l.Message.Contains(revoked.Id, StringComparison.Ordinal));
	}

	[Fact]
	public void Only_an_unbound_admin_key_counts_as_an_active_admin()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;

		Assert.True(ApiKeyRecord.Issue("admin", [MemoriaScopes.Admin], [], false, null, now).Record.IsActiveAdmin(now));
		Assert.True(ApiKeyRecord.Issue("ops", [MemoriaScopes.Admin, MemoriaScopes.Read], [], true, null, now).Record.IsActiveAdmin(now));
		Assert.False(ApiKeyRecord.Issue("bound", [MemoriaScopes.Admin], ["c"], false, null, now).Record.IsActiveAdmin(now));
		Assert.False(ApiKeyRecord.Issue("reader", [MemoriaScopes.Read], ["c"], false, null, now).Record.IsActiveAdmin(now));
	}

	[Fact]
	public async Task An_expiring_admin_key_cannot_make_or_rotate_a_key_that_outlives_it()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		(ApiKeyRecord shortRecord, string shortToken) = await factory.IssueAsync([MemoriaScopes.Admin], expiresAt: now.AddDays(10));
		HttpClient shortLived = Client(shortToken);
		(ApiKeyRecord forever, _) = await factory.IssueAsync([MemoriaScopes.Read], ["c"]);

		HttpResponseMessage longer = await shortLived.PostAsJsonAsync("/v1/admin/keys", new CreateApiKeyRequest("x", [MemoriaScopes.Read], ["c"], null, now.AddDays(20)), Json);
		HttpResponseMessage rotated = await shortLived.PostAsync($"/v1/admin/keys/{forever.Id}:rotate", null);

		foreach (HttpResponseMessage refused in new[] { longer, rotated })
		{
			Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
			Assert.Equal("key_exceeds_caller", await CodeOf(refused));
			Assert.Contains("\"dimension\":\"lifetime\"", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
		}

		Assert.Equal(forever.Sha256Hash, (await factory.Store.FindAsync(forever.Id))!.Sha256Hash);

		HttpResponseMessage shorter = await shortLived.PostAsJsonAsync("/v1/admin/keys", new CreateApiKeyRequest("x", [MemoriaScopes.Read], ["c"], null, now.AddDays(5)), Json);
		Assert.Equal(HttpStatusCode.Created, shorter.StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await shortLived.PostAsync($"/v1/admin/keys/{shortRecord.Id}:rotate", null)).StatusCode);
	}

	[Fact]
	public async Task A_key_with_no_expiry_of_its_own_ends_when_its_expiring_issuer_does()
	{
		DateTimeOffset until = DateTimeOffset.UtcNow.AddDays(10);
		(_, string shortToken) = await factory.IssueAsync([MemoriaScopes.Admin], expiresAt: until);

		HttpResponseMessage response = await Client(shortToken).PostAsJsonAsync("/v1/admin/keys", new CreateApiKeyRequest("x", [MemoriaScopes.Admin], null, null, null), Json);

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		IssuedApiKey issued = (await response.Content.ReadFromJsonAsync<IssuedApiKey>(Json))!;
		Assert.Equal(until.ToUnixTimeSeconds(), issued.Info.ExpiresAt!.Value.ToUnixTimeSeconds());
	}

	[Theory]
	[InlineData("9999-12-31T00:00:00Z")]
	[InlineData("+731")]
	public async Task ExpiresAt_more_than_two_years_ahead_is_refused(string expiresAt)
	{
		HttpClient admin = await factory.AdminAsync();
		DateTimeOffset value = expiresAt.StartsWith('+')
			? DateTimeOffset.UtcNow.AddDays(int.Parse(expiresAt, System.Globalization.CultureInfo.InvariantCulture))
			: DateTimeOffset.Parse(expiresAt, System.Globalization.CultureInfo.InvariantCulture);

		HttpResponseMessage response = await admin.PostAsJsonAsync("/v1/admin/keys", new CreateApiKeyRequest("x", [MemoriaScopes.Read], ["c"], null, value), Json);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Contains("730 days", problem.RootElement.GetProperty("errors").GetProperty("expiresAt")[0].GetString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExpiresAt_just_inside_two_years_is_issued()
	{
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsJsonAsync("/v1/admin/keys", new CreateApiKeyRequest("x", [MemoriaScopes.Read], ["c"], null, DateTimeOffset.UtcNow.AddDays(729)), Json);

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
	}

	[Fact]
	public async Task Health_names_the_version_only_to_a_caller_with_a_valid_key()
	{
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], ["c"]);

		using JsonDocument anonymous = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/health"));
		using JsonDocument wrong = JsonDocument.Parse(await Client("mem_0000000000000000_" + new string('0', 64)).GetStringAsync("/health"));
		using JsonDocument keyed = JsonDocument.Parse(await Client(token).GetStringAsync("/health"));

		Assert.Equal("ok", anonymous.RootElement.GetProperty("status").GetString());
		Assert.Equal(JsonValueKind.Null, anonymous.RootElement.GetProperty("version").ValueKind);
		Assert.Equal(JsonValueKind.Null, wrong.RootElement.GetProperty("version").ValueKind);
		Assert.StartsWith(Environment.GetEnvironmentVariable("MEMORIA_EXPECTED_VERSION") is { Length: > 0 } stamped ? stamped : "0.0.0-dev", keyed.RootElement.GetProperty("version").GetString());
	}
}

/// <summary>The width rule on its own, for shapes the routes no longer let through.</summary>
public sealed class KeyGrantTests
{
	private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

	private static ApiKeyRecord Caller(string[] scopes, string[] collections, bool allCollections = false, DateTimeOffset? expiresAt = null)
	{
		return ApiKeyRecord.Issue("caller", scopes, collections, allCollections, expiresAt, Now).Record;
	}

	[Fact]
	public void A_bound_caller_cannot_widen_its_collections()
	{
		ApiKeyRecord bound = Caller([MemoriaScopes.Admin, MemoriaScopes.Read], ["work", "notes"]);

		Assert.Equal("collections", KeyGrant.Exceeds(bound, [MemoriaScopes.Read, MemoriaScopes.Admin], [], true, null));
		Assert.Equal("collections", KeyGrant.Exceeds(bound, [MemoriaScopes.Admin], [], false, null));
		Assert.Equal("collections", KeyGrant.Exceeds(bound, [MemoriaScopes.Read], ["secret"], false, null));
		Assert.Equal("collections", KeyGrant.Exceeds(bound, [MemoriaScopes.Read], ["work", "Work"], false, null));
		Assert.Null(KeyGrant.Exceeds(bound, [MemoriaScopes.Read], ["notes"], false, null));
		Assert.Null(KeyGrant.Exceeds(bound, [MemoriaScopes.Read], ["work", "notes"], false, null));
	}

	[Fact]
	public void An_unbound_admin_reaches_every_collection()
	{
		Assert.Null(KeyGrant.Exceeds(Caller([MemoriaScopes.Admin], []), [MemoriaScopes.Read, MemoriaScopes.Ingest, MemoriaScopes.Admin], [], true, null));
		Assert.Null(KeyGrant.Exceeds(Caller([MemoriaScopes.Admin, MemoriaScopes.Read], [], allCollections: true), [MemoriaScopes.Read], ["any"], false, null));
	}

	[Fact]
	public void Admin_grants_the_data_scopes_and_nothing_else_does()
	{
		ApiKeyRecord reader = Caller([MemoriaScopes.Read], [], allCollections: true);

		Assert.Equal("scopes", KeyGrant.Exceeds(reader, [MemoriaScopes.Ingest], ["c"], false, null));
		Assert.Equal("scopes", KeyGrant.Exceeds(reader, [MemoriaScopes.Admin], [], false, null));
		Assert.Null(KeyGrant.Exceeds(reader, [MemoriaScopes.Read], ["c"], false, null));
		Assert.Equal("scopes", KeyGrant.Exceeds(Caller([MemoriaScopes.Admin], []), ["superuser"], [], false, null));
	}

	[Fact]
	public void A_key_cannot_outlive_an_expiring_caller()
	{
		ApiKeyRecord expiring = Caller([MemoriaScopes.Admin], [], expiresAt: Now.AddDays(10));

		Assert.Equal("lifetime", KeyGrant.Exceeds(expiring, [MemoriaScopes.Read], ["c"], false, null));
		Assert.Equal("lifetime", KeyGrant.Exceeds(expiring, [MemoriaScopes.Read], ["c"], false, Now.AddDays(10).AddSeconds(1)));
		Assert.Null(KeyGrant.Exceeds(expiring, [MemoriaScopes.Read], ["c"], false, Now.AddDays(10)));
		Assert.Null(KeyGrant.Exceeds(Caller([MemoriaScopes.Admin], []), [MemoriaScopes.Read], ["c"], false, null));
	}

	[Fact]
	public void Bound_admin_means_admin_beside_a_collection_list()
	{
		Assert.True(KeyGrant.IsBoundAdmin([MemoriaScopes.Admin], ["c"], false));
		Assert.False(KeyGrant.IsBoundAdmin([MemoriaScopes.Admin], [], false));
		Assert.False(KeyGrant.IsBoundAdmin([MemoriaScopes.Admin], ["c"], true));
		Assert.False(KeyGrant.IsBoundAdmin([MemoriaScopes.Read], ["c"], false));
	}
}

/// <summary>Disabled mode starts only on loopback unless the operator opts in to the network.</summary>
public sealed class DisabledAuthGuardTests
{
	[Theory]
	[InlineData("http://0.0.0.0:8080")]
	[InlineData("http://[::]:8080")]
	[InlineData("http://*:8080")]
	[InlineData("http://+:8080")]
	[InlineData("http://192.168.1.10:8080")]
	[InlineData("http://loopback:8080")]
	[InlineData("http://memoria.example:8080")]
	[InlineData("http://unix:/tmp/memoria.sock")]
	public void Disabled_mode_refuses_to_start_on_the_network(string urls)
	{
		using MemoriaApiFactory baseFactory = new();
		using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(builder => builder.UseSetting("urls", urls));

		Exception failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

		Assert.Contains("AllowDisabledOnNetwork", failure.ToString(), StringComparison.Ordinal);
		Assert.Contains(baseFactory.Logs, l => l.Level == LogLevel.Critical && l.EventId.Id == 3203);
	}

	[Fact]
	public async Task Disabled_mode_on_the_network_starts_when_told_to_and_says_so_loudly()
	{
		using MemoriaApiFactory baseFactory = new();
		using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(static builder => builder
			.UseSetting("urls", "http://0.0.0.0:8080")
			.UseSetting("Memoria:Auth:AllowDisabledOnNetwork", "true"));

		Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/health")).StatusCode);
		Assert.Contains(baseFactory.Logs, l => l.Level == LogLevel.Critical && l.EventId.Id == 3204 && l.Message.Contains("0.0.0.0", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Disabled_mode_on_loopback_starts_and_warns()
	{
		using MemoriaApiFactory factory = new();

		Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/health")).StatusCode);
		Assert.Contains(factory.Logs, l => l.Level == LogLevel.Warning && l.EventId.Id == 3202);
	}

	[Fact]
	public async Task A_network_bind_with_keys_on_is_not_this_guards_business()
	{
		using AuthApiFactory baseFactory = new();
		using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(static builder => builder.UseSetting("urls", "http://0.0.0.0:8080"));

		Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/health")).StatusCode);
	}

	[Theory]
	[InlineData("http://127.0.0.1:8080", true)]
	[InlineData("http://127.8.9.10:8080", true)]
	[InlineData("https://localhost:8443", true)]
	[InlineData("http://LOCALHOST:8080", true)]
	[InlineData("http://[::1]:8080", true)]
	[InlineData("http://0.0.0.0:8080", false)]
	[InlineData("http://loopback:8080", false)]
	[InlineData("http://localhost.example:8080", false)]
	[InlineData("ftp://127.0.0.1", false)]
	[InlineData("not a url", false)]
	[InlineData("", false)]
	public void Loopback_is_localhost_or_a_loopback_ip(string address, bool loopback)
	{
		Assert.Equal(loopback, DisabledAuthGuard.IsLoopback(address));
	}

	[Fact]
	public void Every_configured_address_is_read()
	{
		IConfiguration endpoints = Configuration(new()
		{
			["Kestrel:Endpoints:Local:Url"] = "http://127.0.0.1:8080",
			["Kestrel:Endpoints:Lan:Url"] = "http://0.0.0.0:8443",
			["urls"] = "http://localhost:5000; http://[::]:5001",
		});
		IConfiguration ports = Configuration(new() { ["http_ports"] = "8080;8081", ["https_ports"] = "8443" });

		// Kestrel ignores urls beside its own endpoints; the guard reads both, so it can only err toward refusing.
		Assert.Equal(["http://0.0.0.0:8443", "http://127.0.0.1:8080", "http://localhost:5000", "http://[::]:5001"], DisabledAuthGuard.ListenAddresses(endpoints));
		Assert.Equal(["http://0.0.0.0:8443", "http://[::]:5001"], DisabledAuthGuard.OnNetwork(DisabledAuthGuard.ListenAddresses(endpoints)));
		Assert.Equal(["http://*:8080", "http://*:8081", "https://*:8443"], DisabledAuthGuard.ListenAddresses(ports));
		Assert.Equal(["http://127.0.0.1:1"], DisabledAuthGuard.ListenAddresses(Configuration(new() { ["urls"] = "http://127.0.0.1:1", ["http_ports"] = "80" })));
		Assert.Equal([string.Empty], DisabledAuthGuard.ListenAddresses(Configuration(new() { ["Kestrel:Endpoints:Broken:Protocols"] = "Http1" })));
		Assert.Empty(DisabledAuthGuard.ListenAddresses(Configuration([])));
	}

	private static IConfiguration Configuration(Dictionary<string, string?> values)
	{
		return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
	}
}
