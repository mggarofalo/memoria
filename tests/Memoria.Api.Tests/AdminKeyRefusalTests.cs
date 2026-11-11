using System.Net;
using System.Text;
using System.Text.Json;
using Memoria.Api.Auth;
using Memoria.Api.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memoria.Api.Tests;

/// <summary>
/// What an operator managing keys is told when a request is refused, word for word and field by field,
/// where each limit falls, and what the log records about who did what.
/// </summary>
public sealed class AdminKeyRefusalTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private const string LabelMessage = "A label of 1 to 200 characters is required.";

	private const string NoScopeMessage = "At least one scope is required: read, ingest, or admin.";

	private const string UnknownScopeMessage = "Scopes must be read, ingest, or admin, in lower case.";

	private const string OmittedMessage = "List the collections this key may read, or set allCollections to true to request every collection.";

	private const string BothMessage = "Set allCollections or list collections, not both.";

	private const string EmptyMessage = "An empty list is not allowed. List the collections this key may read, or set allCollections to true to request every collection.";

	private const string TooManyMessage = "At most 50 collections per key.";

	private const string NamesMessage = "Collection names must be 1 to 200 characters with no slash or surrounding whitespace. They are case-sensitive.";

	private const string PastMessage = "expiresAt must be in the future.";

	private sealed record Problem(int Status, string Title, string Detail, string Code);

	private static async Task<Problem> ProblemOf(HttpResponseMessage response)
	{
		using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = problem.RootElement;
		return new Problem(root.GetProperty("status").GetInt32(), root.GetProperty("title").GetString()!, root.GetProperty("detail").GetString()!, root.GetProperty("code").GetString()!);
	}

	private static async Task<Dictionary<string, string[]>> ErrorsOf(HttpResponseMessage response)
	{
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return problem.RootElement.GetProperty("errors").EnumerateObject()
			.ToDictionary(static field => field.Name, static field => field.Value.EnumerateArray().Select(static message => message.GetString()!).ToArray());
	}

	private static StringContent Body(string json)
	{
		return new StringContent(json, Encoding.UTF8, "application/json");
	}

	private static string Names(IEnumerable<string> names)
	{
		return string.Join(',', names.Select(static name => $"\"{name}\""));
	}

	private async Task<HttpResponseMessage> CreateAsync(string json)
	{
		HttpClient admin = await factory.AdminAsync();
		return await admin.PostAsync("/v1/admin/keys", Body(json));
	}

	public static TheoryData<string, string, string> OneMistake => new()
	{
		{ """{"label":"","scopes":["read"],"allCollections":true}""", "label", LabelMessage },
		{ $$"""{"label":"{{new string('l', 201)}}","scopes":["read"],"allCollections":true}""", "label", LabelMessage },
		{ """{"label":"x","scopes":["read","Read"],"collections":["c"]}""", "scopes", UnknownScopeMessage },
		{ """{"label":"x","scopes":["admin","read"]}""", "collections", OmittedMessage },
		{ """{"label":"x","scopes":["read"],"collections":["c"],"allCollections":true}""", "collections", BothMessage },
		{ """{"label":"x","scopes":["read"],"collections":[]}""", "collections", EmptyMessage },
		{ $$"""{"label":"x","scopes":["read"],"collections":[{{Names(Enumerable.Range(0, 51).Select(static i => $"c{i}"))}}]}""", "collections", TooManyMessage },
		{ """{"label":"x","scopes":["read"],"collections":["fine","a/b"]}""", "collections", NamesMessage },
		{ $$"""{"label":"x","scopes":["read"],"collections":["{{new string('n', 201)}}"]}""", "collections", NamesMessage },
		{ """{"label":"x","scopes":["read"],"allCollections":true,"expiresAt":"2020-01-01T00:00:00Z"}""", "expiresAt", PastMessage },
	};

	[Theory]
	[MemberData(nameof(OneMistake))]
	public async Task OneMistake_IsRefused_WithOnlyItsOwnMessage(string json, string field, string message)
	{
		HttpResponseMessage response = await CreateAsync(json);

		Dictionary<string, string[]> errors = await ErrorsOf(response);
		Assert.Equal(field, Assert.Single(errors.Keys));
		Assert.Equal([message], errors[field]);
	}

	/// <summary>An empty scope list is not an admin-only key, so it does not escape listing its collections.</summary>
	[Fact]
	public async Task NoScopes_IsRefused_AndStillOwesItsCollections()
	{
		HttpResponseMessage response = await CreateAsync("""{"label":"x","scopes":[]}""");

		Dictionary<string, string[]> errors = await ErrorsOf(response);
		Assert.Equal(["collections", "scopes"], errors.Keys.Order(StringComparer.Ordinal));
		Assert.Equal([NoScopeMessage], errors["scopes"]);
		Assert.Equal([OmittedMessage], errors["collections"]);
	}

	[Fact]
	public async Task EveryLimit_IsInclusive()
	{
		string[] collections = [new string('n', 200), .. Enumerable.Range(1, 49).Select(static i => $"c{i}")];

		HttpResponseMessage response = await CreateAsync($$"""{"label":"{{new string('l', 200)}}","scopes":["read"],"collections":[{{Names(collections)}}]}""");

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
	}

	[Fact]
	public async Task RevokingOrRotatingAnUnknownId_Is404_SayingNoKeyHasIt()
	{
		HttpClient admin = await factory.AdminAsync();
		string id = ApiKeyToken.NewId();

		HttpResponseMessage revoke = await admin.DeleteAsync($"/v1/admin/keys/{id}");
		HttpResponseMessage rotate = await admin.PostAsync($"/v1/admin/keys/{id}:rotate", null);

		Problem expected = new(404, "Not found", "No API key has that id.", "not_found");
		Assert.Equal(expected, await ProblemOf(revoke));
		Assert.Equal(expected, await ProblemOf(rotate));
	}

	[Fact]
	public async Task RotatingARevokedKey_Is409_SayingToIssueANewOne()
	{
		HttpClient admin = await factory.AdminAsync();
		(ApiKeyRecord record, _) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1/admin/keys/{record.Id}")).StatusCode);

		HttpResponseMessage response = await admin.PostAsync($"/v1/admin/keys/{record.Id}:rotate", null);

		Assert.Equal(new Problem(409, "Key is not active", "A revoked or expired key cannot be rotated. Issue a new one.", "key_inactive"), await ProblemOf(response));
	}

	[Fact]
	public async Task IssuingAndRevoking_AreLogged_WithTheAdminWhoDidIt()
	{
		(ApiKeyRecord actor, string token) = await factory.IssueAsync([MemoriaScopes.Admin], label: "acting admin");
		HttpClient admin = AuthApiFactory.Authorized(factory.CreateClient(), token);
		factory.Logs.Clear();

		HttpResponseMessage created = await admin.PostAsync("/v1/admin/keys", Body("""{"label":"logged","scopes":["read"],"allCollections":true}"""));
		using JsonDocument issued = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
		string id = issued.RootElement.GetProperty("info").GetProperty("id").GetString()!;
		await admin.DeleteAsync($"/v1/admin/keys/{id}");

		MemoriaApiFactory.CapturedLog issuedLine = Assert.Single(factory.Logs, static log => log.EventId.Id == 3300);
		MemoriaApiFactory.CapturedLog revokedLine = Assert.Single(factory.Logs, static log => log.EventId.Id == 3301);
		Assert.Equal((LogLevel.Information, id, actor.Id), (issuedLine.Level, issuedLine.State["KeyId"], issuedLine.State["ActorKeyId"]));
		Assert.Equal((LogLevel.Information, id, actor.Id), (revokedLine.Level, revokedLine.State["KeyId"], revokedLine.State["ActorKeyId"]));
	}

	/// <summary>The store's own check that the key is still active is the last word: a key revoked between the lookup and the write is not rotated.</summary>
	[Fact]
	public async Task AKeyRevokedWhileItIsRotated_Is409_AndKeepsNoNewSecret()
	{
		(ApiKeyRecord target, string oldToken) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		using WebApplicationFactory<Program> racing = factory.WithWebHostBuilder(builder =>
			builder.ConfigureTestServices(services => services.AddSingleton<IApiKeyStore>(new LosesTheRace(factory.Store))));
		(_, string adminToken) = await factory.IssueAsync([MemoriaScopes.Admin]);
		HttpClient admin = AuthApiFactory.Authorized(racing.CreateClient(), adminToken);
		factory.Logs.Clear();

		HttpResponseMessage response = await admin.PostAsync($"/v1/admin/keys/{target.Id}:rotate", null);

		Assert.Equal(new Problem(409, "Key is not active", "A revoked or expired key cannot be rotated. Issue a new one.", "key_inactive"), await ProblemOf(response));
		Assert.Null(response.Headers.CacheControl);
		Assert.DoesNotContain(factory.Logs, static log => log.EventId.Id == 3302);
		Assert.Equal(ApiKeyToken.Hash(oldToken), (await factory.Store.FindAsync(target.Id))!.Sha256Hash);
	}

	/// <summary>Answers every write of a new hash as if the key had been revoked a moment before.</summary>
	private sealed class LosesTheRace(IApiKeyStore inner) : IApiKeyStore
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
			return Task.FromResult(false);
		}

		public Task TouchLastUsedAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default)
		{
			return inner.TouchLastUsedAsync(id, at, cancellationToken);
		}

		public Task<bool> HasActiveAdminAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
		{
			return inner.HasActiveAdminAsync(now, cancellationToken);
		}
	}
}

/// <summary>With authentication off, key management is not there, and says why.</summary>
public sealed class AdminKeyDisabledModeTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	[Fact]
	public async Task KeyManagement_Is404_NamingTheSetting()
	{
		HttpResponseMessage response = await factory.CreateClient().GetAsync("/v1/admin/keys");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = problem.RootElement;
		Assert.Equal("Unavailable without API keys", root.GetProperty("title").GetString());
		Assert.Equal("Memoria:Auth:Mode is Disabled. Key management, operations and writing to the corpus need API keys.", root.GetProperty("detail").GetString());
		Assert.Equal("auth_disabled", root.GetProperty("code").GetString());
	}
}
