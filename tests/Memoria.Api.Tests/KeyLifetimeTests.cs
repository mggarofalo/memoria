using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Memoria.Api.Endpoints;
using Memoria.Api.Http;
using Memoria.Core.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Memoria.Api.Tests;

/// <summary>With <c>Memoria:Auth:DefaultKeyLifetime</c> set, a key that is not an admin key expires unless it says otherwise.</summary>
public sealed class KeyLifetimeTests : IDisposable
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private readonly AuthApiFactory _factory = new();

	private readonly WebApplicationFactory<Program> _limited;

	public KeyLifetimeTests()
	{
		_limited = _factory.WithWebHostBuilder(static builder => builder.UseSetting("Memoria:Auth:DefaultKeyLifetime", "30.00:00:00"));
	}

	public void Dispose()
	{
		_limited.Dispose();
		_factory.Dispose();
	}

	private async Task<ApiKeyInfo> CreateAsync(WebApplicationFactory<Program> app, CreateApiKeyRequest request)
	{
		(_, string token) = await _factory.IssueAsync([MemoriaScopes.Admin]);
		HttpResponseMessage response = await AuthApiFactory.Authorized(app.CreateClient(), token).PostAsJsonAsync("/v1/admin/keys", request, Json);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		return (await response.Content.ReadFromJsonAsync<IssuedApiKey>(Json))!.Info;
	}

	[Fact]
	public async Task A_key_that_names_no_expiry_gets_the_default_lifetime()
	{
		DateTimeOffset before = DateTimeOffset.UtcNow;

		ApiKeyInfo reader = await CreateAsync(_limited, new CreateApiKeyRequest("laptop", [MemoriaScopes.Read], ["c"], null, null));

		Assert.NotNull(reader.ExpiresAt);
		Assert.InRange(reader.ExpiresAt!.Value, before.AddDays(30), DateTimeOffset.UtcNow.AddDays(30));
	}

	[Fact]
	public async Task A_key_that_names_its_expiry_keeps_it()
	{
		DateTimeOffset chosen = DateTimeOffset.UtcNow.AddDays(400);

		ApiKeyInfo writer = await CreateAsync(_limited, new CreateApiKeyRequest("hook", [MemoriaScopes.Ingest], ["c"], null, chosen));

		Assert.Equal(chosen.ToUnixTimeSeconds(), writer.ExpiresAt!.Value.ToUnixTimeSeconds());
	}

	[Fact]
	public async Task An_admin_key_never_gets_one_even_beside_another_scope()
	{
		ApiKeyInfo admin = await CreateAsync(_limited, new CreateApiKeyRequest("ops", [MemoriaScopes.Admin], null, null, null));
		ApiKeyInfo mixed = await CreateAsync(_limited, new CreateApiKeyRequest("ops-reader", [MemoriaScopes.Admin, MemoriaScopes.Read], ["c"], null, null));

		Assert.Null(admin.ExpiresAt);
		Assert.Null(mixed.ExpiresAt);
	}

	[Fact]
	public async Task Unset_a_key_never_expires_as_before()
	{
		ApiKeyInfo reader = await CreateAsync(_factory, new CreateApiKeyRequest("laptop", [MemoriaScopes.Read], ["c"], null, null));

		Assert.Null(reader.ExpiresAt);
	}

	[Fact]
	public void The_default_is_measured_from_when_the_key_is_made()
	{
		DateTimeOffset now = DateTimeOffset.Parse("2026-11-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

		Assert.Equal(now.AddDays(365), AdminKeyEndpoints.DefaultExpiry([MemoriaScopes.Read], now, TimeSpan.FromDays(365)));
		Assert.Null(AdminKeyEndpoints.DefaultExpiry([MemoriaScopes.Read], now, null));
		Assert.Null(AdminKeyEndpoints.DefaultExpiry([MemoriaScopes.Admin], now, TimeSpan.FromDays(365)));
	}
}
