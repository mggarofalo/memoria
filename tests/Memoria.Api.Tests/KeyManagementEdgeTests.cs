using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Memoria.Api.Auth;
using Memoria.Api.Http;
using Memoria.Core.Contracts;
using Memoria.Core.Sidecar;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Memoria.Api.Tests;

/// <summary>
/// Key management at its edges: a calling key that stops being valid mid-request, what a refusal tells the admin, and the
/// longest lifetime a key may be given, to the tick.
/// </summary>
public sealed class KeyManagementEdgeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private sealed record Problem(string Title, string Detail, string Code);

	private static async Task<Problem> ProblemOf(HttpResponseMessage response)
	{
		using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = body.RootElement;
		return new Problem(root.GetProperty("title").GetString()!, root.GetProperty("detail").GetString()!, root.GetProperty("code").GetString()!);
	}

	[Theory]
	[InlineData("revoked")]
	[InlineData("expired")]
	public async Task A_calling_key_that_lapses_while_its_request_runs_issues_nothing(string how)
	{
		(ApiKeyRecord admin, string token) = await factory.IssueAsync([MemoriaScopes.Admin]);
		LapsesAfterAuthentication store = new(factory.Store, admin.Id, how);
		using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IApiKeyStore>(store)));
		HttpClient client = AuthApiFactory.Authorized(app.CreateClient(), token);

		HttpResponseMessage response = await client.PostAsJsonAsync("/v1/admin/keys", new CreateApiKeyRequest("x", [MemoriaScopes.Read], ["c"], null, null), Json);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Equal(new Problem("Calling key is not active", "The key making this request was revoked or expired while it ran.", "caller_inactive"), await ProblemOf(response));
		Assert.DoesNotContain(await factory.Store.ListAsync(), static k => k.Label == "x");
	}

	[Fact]
	public async Task A_key_that_would_outlive_its_issuer_is_refused_saying_so()
	{
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Admin], expiresAt: DateTimeOffset.UtcNow.AddDays(1));
		HttpClient client = AuthApiFactory.Authorized(factory.CreateClient(), token);

		HttpResponseMessage response = await client.PostAsJsonAsync("/v1/admin/keys", new CreateApiKeyRequest("x", [MemoriaScopes.Read], ["c"], null, DateTimeOffset.UtcNow.AddDays(30)), Json);

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Equal(
			new Problem(
				"Key is wider than the caller",
				"A key cannot issue, rotate or revoke a key wider than itself. A key that expires cannot make, rotate or revoke one that outlives it: name an earlier expiresAt, or use a key that does not expire.",
				"key_exceeds_caller"),
			await ProblemOf(response));
	}

	[Fact]
	public async Task An_old_bound_admin_key_is_not_rotated_and_the_admin_is_told_what_to_do_instead()
	{
		(ApiKeyRecord bound, _) = ApiKeyRecord.Issue("old bound admin", [MemoriaScopes.Admin, MemoriaScopes.Read], ["c"], false, null, DateTimeOffset.UtcNow);
		await factory.Store.InsertAsync(bound);
		HttpClient admin = await factory.AdminAsync();

		HttpResponseMessage response = await admin.PostAsync($"/v1/admin/keys/{bound.Id}:rotate", null);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.Equal(
			new Problem(
				"Key holds admin beside a collection list",
				"Such a key can no longer be issued, so it is not rotated either. Issue an admin key without collections, and a read or ingest key for the collections, then revoke this one.",
				"bound_admin_key"),
			await ProblemOf(response));
	}

	[Fact]
	public async Task A_key_may_expire_exactly_two_years_out_and_not_a_tick_later()
	{
		MovableClock clock = new();
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Admin]);
		using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock)));
		HttpClient client = AuthApiFactory.Authorized(app.CreateClient(), token);
		DateTimeOffset limit = clock.GetUtcNow() + KeyGrant.MaxLifetime;

		HttpResponseMessage atLimit = await client.PostAsJsonAsync("/v1/admin/keys", new CreateApiKeyRequest("at-limit", [MemoriaScopes.Read], ["c"], null, limit), Json);
		HttpResponseMessage past = await client.PostAsJsonAsync("/v1/admin/keys", new CreateApiKeyRequest("past-limit", [MemoriaScopes.Read], ["c"], null, limit.AddTicks(1)), Json);

		Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
	}

	[Fact]
	public void A_scoped_caller_never_gets_a_backend_faults_details_even_if_it_may_see_backend_detail()
	{
		using JsonDocument details = JsonDocument.Parse("""{"similarFiles":["elsewhere/secret.md"]}""");
		SidecarException fault = new("internal", "boom", HttpStatusCode.InternalServerError, details.RootElement.Clone());

		SidecarFailureView scoped = SidecarDisclosure.For(fault, new Scope(["c"], seesBackendDetails: true));
		SidecarFailureView everything = SidecarDisclosure.For(fault, new Scope(null, seesBackendDetails: true));

		Assert.Null(scoped.Details);
		Assert.NotNull(everything.Details);
	}

	private sealed class Scope(string[]? collections, bool seesBackendDetails) : ICallerScope
	{
		public IReadOnlyCollection<string>? Collections => collections;

		public bool SeesBackendDetails => seesBackendDetails;
	}

	/// <summary>Answers the first lookup of the calling key as the database holds it, and every later one as revoked or expired.</summary>
	private sealed class LapsesAfterAuthentication(IApiKeyStore inner, string id, string how) : IApiKeyStore
	{
		private int _lookups;

		public Task InitializeAsync(CancellationToken cancellationToken = default)
		{
			return inner.InitializeAsync(cancellationToken);
		}

		public Task InsertAsync(ApiKeyRecord record, CancellationToken cancellationToken = default)
		{
			return inner.InsertAsync(record, cancellationToken);
		}

		public async Task<ApiKeyRecord?> FindAsync(string keyId, CancellationToken cancellationToken = default)
		{
			ApiKeyRecord? record = await inner.FindAsync(keyId, cancellationToken);
			if (record is null || keyId != id || Interlocked.Increment(ref _lookups) == 1)
			{
				return record;
			}

			DateTimeOffset past = DateTimeOffset.UtcNow.AddMinutes(-1);
			return how == "revoked" ? record with { RevokedAt = past } : record with { ExpiresAt = past };
		}

		public Task<IReadOnlyList<ApiKeyRecord>> ListAsync(CancellationToken cancellationToken = default)
		{
			return inner.ListAsync(cancellationToken);
		}

		public Task<bool> RevokeAsync(string keyId, DateTimeOffset at, CancellationToken cancellationToken = default)
		{
			return inner.RevokeAsync(keyId, at, cancellationToken);
		}

		public Task<bool> ReplaceHashAsync(string keyId, byte[] sha256Hash, CancellationToken cancellationToken = default)
		{
			return inner.ReplaceHashAsync(keyId, sha256Hash, cancellationToken);
		}

		public Task TouchLastUsedAsync(string keyId, DateTimeOffset at, CancellationToken cancellationToken = default)
		{
			return inner.TouchLastUsedAsync(keyId, at, cancellationToken);
		}

		public Task<bool> HasActiveAdminAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
		{
			return inner.HasActiveAdminAsync(now, cancellationToken);
		}
	}
}
