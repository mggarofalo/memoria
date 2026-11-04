using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Memoria.Api.Auth;
using Memoria.Api.Http;
using Memoria.Core.Contracts;

namespace Memoria.Api.Tests;

public sealed class AdminKeyEndpointTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private static CreateApiKeyRequest Reader(params string[] collections)
	{
		return new CreateApiKeyRequest("laptop", [MemoriaScopes.Read], collections, null, null);
	}

	private async Task<IssuedApiKey> CreateAsync(HttpClient admin, CreateApiKeyRequest request)
	{
		HttpResponseMessage response = await admin.PostAsJsonAsync("/v1/admin/keys", request, Json);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<IssuedApiKey>(Json))!;
	}

	[Fact]
	public async Task Create_ReturnsWorkingPlaintextOnce_WithNoStore()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsJsonAsync("/v1/admin/keys", Reader("claude-sessions"), Json);

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		Assert.True(response.Headers.CacheControl?.NoStore);
		IssuedApiKey issued = (await response.Content.ReadFromJsonAsync<IssuedApiKey>(Json))!;
		Assert.Equal($"/v1/admin/keys/{issued.Info.Id}", response.Headers.Location?.ToString());
		Assert.Matches($"^mem_{issued.Info.Id}_[0-9a-f]{{64}}$", issued.Key);
		Assert.Equal(["read"], issued.Info.Scopes);
		Assert.Equal(["claude-sessions"], issued.Info.Collections);
		Assert.False(issued.Info.AllCollections);

		HttpClient reader = AuthApiFactory.Authorized(factory.CreateClient(), issued.Key);
		Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/v1/collections")).StatusCode);
	}

	[Fact]
	public async Task Create_StoresOnlyTheHash()
	{
		HttpClient admin = await factory.AdminAsync();

		IssuedApiKey issued = await CreateAsync(admin, Reader("a"));

		ApiKeyRecord stored = (await factory.Store.FindAsync(issued.Info.Id))!;
		Assert.Equal(ApiKeyToken.Hash(issued.Key), stored.Sha256Hash);
		byte[] file = await ReadDatabaseFilesAsync();
		Assert.True(file.Length > 0);
		Assert.DoesNotContain(issued.Key.Split('_')[2], System.Text.Encoding.UTF8.GetString(file));
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("null")]
	public async Task Create_RefusesEmptyOrOmittedCollections_ForAReadKey(string collections)
	{
		HttpClient admin = await factory.AdminAsync();
		StringContent body = new($$"""{"label":"x","scopes":["read"],"collections":{{collections}}}""", System.Text.Encoding.UTF8, "application/json");

		HttpResponseMessage response = await admin.PostAsync("/v1/admin/keys", body);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("collections", out _));
	}

	[Fact]
	public async Task Create_EmptyListIsRefused_EvenForAnAdminOnlyKey()
	{
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsJsonAsync("/v1/admin/keys", new CreateApiKeyRequest("x", [MemoriaScopes.Admin], [], null, null), Json);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Create_AllCollectionsMustBeExplicit()
	{
		HttpClient admin = await factory.AdminAsync();

		IssuedApiKey issued = await CreateAsync(admin, new CreateApiKeyRequest("everything", [MemoriaScopes.Read], null, true, null));

		Assert.True(issued.Info.AllCollections);
		Assert.Empty(issued.Info.Collections);
		Assert.True((await factory.Store.FindAsync(issued.Info.Id))!.AllCollections);
	}

	[Fact]
	public async Task Create_AllCollectionsFalse_IsNotAWayToGetEverything()
	{
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsJsonAsync("/v1/admin/keys", new CreateApiKeyRequest("x", [MemoriaScopes.Read], null, false, null), Json);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Create_AdminOnlyKey_NeedsNoCollections_AndReadsNothing()
	{
		HttpClient admin = await factory.AdminAsync();

		IssuedApiKey issued = await CreateAsync(admin, new CreateApiKeyRequest("second admin", [MemoriaScopes.Admin], null, null, null));

		HttpClient second = AuthApiFactory.Authorized(factory.CreateClient(), issued.Key);
		Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/v1/admin/keys")).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await second.GetAsync("/v1/collections")).StatusCode);
	}

	[Fact]
	public async Task Create_ValidatesEveryField()
	{
		HttpClient admin = await factory.AdminAsync();
		CreateApiKeyRequest[] bad =
		[
			new(null, [MemoriaScopes.Read], ["a"], null, null),
			new("   ", [MemoriaScopes.Read], ["a"], null, null),
			new(new string('x', 201), [MemoriaScopes.Read], ["a"], null, null),
			new("x", null, ["a"], null, null),
			new("x", [], ["a"], null, null),
			new("x", ["superuser"], ["a"], null, null),
			new("x", ["READ"], ["a"], null, null),
			new("x", [MemoriaScopes.Read], ["a"], true, null),
			new("x", [MemoriaScopes.Read], ["a/b"], null, null),
			new("x", [MemoriaScopes.Read], [" a"], null, null),
			new("x", [MemoriaScopes.Read], [""], null, null),
			new("x", [MemoriaScopes.Read], Enumerable.Range(0, 51).Select(i => $"c{i}").ToArray(), null, null),
			new("x", [MemoriaScopes.Read], ["a"], null, DateTimeOffset.UtcNow.AddMinutes(-1)),
		];

		foreach (CreateApiKeyRequest request in bad)
		{
			HttpResponseMessage response = await admin.PostAsJsonAsync("/v1/admin/keys", request, Json);
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		}
	}

	[Fact]
	public async Task List_ShowsMetadata_AndNoKeyMaterial()
	{
		HttpClient admin = await factory.AdminAsync();
		IssuedApiKey issued = await CreateAsync(admin, Reader("claude-sessions"));

		HttpResponseMessage response = await admin.GetAsync("/v1/admin/keys");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string raw = await response.Content.ReadAsStringAsync();
		Assert.DoesNotContain(issued.Key, raw);
		Assert.DoesNotContain(issued.Key.Split('_')[2], raw);
		Assert.DoesNotContain("hash", raw, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(Convert.ToBase64String(ApiKeyToken.Hash(issued.Key)), raw);
		ApiKeyList list = JsonSerializer.Deserialize<ApiKeyList>(raw, Json)!;
		ApiKeyInfo info = Assert.Single(list.Keys, k => k.Id == issued.Info.Id);
		Assert.Equal("laptop", info.Label);
		Assert.Equal(["claude-sessions"], info.Collections);
	}

	[Fact]
	public async Task Revoke_StopsTheKey_IsIdempotent_And404sOnUnknown()
	{
		HttpClient admin = await factory.AdminAsync();
		IssuedApiKey issued = await CreateAsync(admin, Reader("a"));
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		HttpClient reader = AuthApiFactory.Authorized(factory.CreateClient(), issued.Key);
		Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/v1/collections")).StatusCode);

		Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1/admin/keys/{issued.Info.Id}")).StatusCode);

		Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/v1/collections")).StatusCode);
		Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1/admin/keys/{issued.Info.Id}")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/v1/admin/keys/0000000000000000")).StatusCode);
	}

	[Fact]
	public async Task Rotate_InvalidatesTheOldToken_AndKeepsIdentityAndScope()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		HttpClient admin = await factory.AdminAsync();
		IssuedApiKey issued = await CreateAsync(admin, Reader("claude-sessions"));
		HttpClient before = AuthApiFactory.Authorized(factory.CreateClient(), issued.Key);
		Assert.Equal(HttpStatusCode.OK, (await before.GetAsync("/v1/collections")).StatusCode);

		HttpResponseMessage response = await admin.PostAsync($"/v1/admin/keys/{issued.Info.Id}:rotate", null);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.True(response.Headers.CacheControl?.NoStore);
		IssuedApiKey rotated = (await response.Content.ReadFromJsonAsync<IssuedApiKey>(Json))!;
		Assert.Equal(issued.Info.Id, rotated.Info.Id);
		Assert.NotEqual(issued.Key, rotated.Key);
		Assert.Equal(["claude-sessions"], rotated.Info.Collections);

		Assert.Equal(HttpStatusCode.Unauthorized, (await before.GetAsync("/v1/collections")).StatusCode);
		HttpClient after = AuthApiFactory.Authorized(factory.CreateClient(), rotated.Key);
		Assert.Equal(HttpStatusCode.OK, (await after.GetAsync("/v1/collections")).StatusCode);
	}

	[Fact]
	public async Task Rotate_UnknownIs404_RevokedAndExpiredAre409()
	{
		HttpClient admin = await factory.AdminAsync();
		IssuedApiKey revoked = await CreateAsync(admin, Reader("a"));
		await admin.DeleteAsync($"/v1/admin/keys/{revoked.Info.Id}");
		(ApiKeyRecord expired, _) = await factory.IssueAsync([MemoriaScopes.Read], ["a"], expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));

		Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/v1/admin/keys/0000000000000000:rotate", null)).StatusCode);
		Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/v1/admin/keys/{revoked.Info.Id}:rotate", null)).StatusCode);
		Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/v1/admin/keys/{expired.Id}:rotate", null)).StatusCode);
	}

	[Fact]
	public async Task NonAdminCallers_AreRefusedOnEveryRoute()
	{
		HttpClient reader = await factory.ClientWithScopesAsync([MemoriaScopes.Read, MemoriaScopes.Ingest], allCollections: true);
		HttpClient anonymous = factory.CreateClient();
		(ApiKeyRecord target, _) = await factory.IssueAsync([MemoriaScopes.Read], ["a"]);

		foreach ((HttpClient client, HttpStatusCode expected) in new[] { (reader, HttpStatusCode.Forbidden), (anonymous, HttpStatusCode.Unauthorized) })
		{
			Assert.Equal(expected, (await client.GetAsync("/v1/admin/keys")).StatusCode);
			Assert.Equal(expected, (await client.PostAsJsonAsync("/v1/admin/keys", Reader("a"), Json)).StatusCode);
			Assert.Equal(expected, (await client.DeleteAsync($"/v1/admin/keys/{target.Id}")).StatusCode);
			Assert.Equal(expected, (await client.PostAsync($"/v1/admin/keys/{target.Id}:rotate", null)).StatusCode);
		}

		Assert.Null((await factory.Store.FindAsync(target.Id))!.RevokedAt);
	}

	[Fact]
	public async Task KeyMaterial_NeverReachesTheLogs()
	{
		HttpClient admin = await factory.AdminAsync();
		factory.Logs.Clear();

		IssuedApiKey issued = await CreateAsync(admin, Reader("a"));
		HttpResponseMessage rotateResponse = await admin.PostAsync($"/v1/admin/keys/{issued.Info.Id}:rotate", null);
		IssuedApiKey rotated = (await rotateResponse.Content.ReadFromJsonAsync<IssuedApiKey>(Json))!;
		await admin.GetAsync("/v1/admin/keys");

		Assert.Contains(factory.Logs, l => l.Message.Contains($"API key {issued.Info.Id} issued", StringComparison.Ordinal));
		Assert.Contains(factory.Logs, l => l.Message.Contains($"API key {issued.Info.Id} rotated", StringComparison.Ordinal));
		foreach (MemoriaApiFactory.CapturedLog line in factory.Logs)
		{
			string text = line.Message + " " + string.Join(" ", line.State.Values);
			Assert.DoesNotContain(issued.Key.Split('_')[2], text);
			Assert.DoesNotContain(rotated.Key.Split('_')[2], text);
		}
	}

	private async Task<byte[]> ReadDatabaseFilesAsync()
	{
		using MemoryStream all = new();
		foreach (string path in Directory.GetFiles(Path.GetDirectoryName(factory.DatabasePath)!))
		{
			await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			await stream.CopyToAsync(all);
		}

		return all.ToArray();
	}
}
