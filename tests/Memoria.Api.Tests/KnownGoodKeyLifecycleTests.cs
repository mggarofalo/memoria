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
using Microsoft.Extensions.DependencyInjection;

namespace Memoria.Api.Tests;

/// <summary>
/// A key that is issued, rotated or revoked through the admin endpoints changes, at once, whether it may pass an
/// address that is over its failed-authentication budget: a new or rotated secret passes, a revoked one waits like a
/// guesser. Each test starts a host of its own after its keys exist, so only the endpoints can have taught it.
/// </summary>
public sealed class KnownGoodKeyLifecycleTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private const string Peer = "198.51.100.7";

	private const int Permits = 2;

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

	private WebApplicationFactory<Program> Limited()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		return factory.WithWebHostBuilder(static builder =>
		{
			builder.UseSetting("Memoria:Auth:FailedAuth:PermitLimit", Permits.ToString(System.Globalization.CultureInfo.InvariantCulture));
			builder.UseSetting("Memoria:Auth:FailedAuth:Window", "01:00:00");
			builder.ConfigureTestServices(static services => services.AddSingleton<IStartupFilter, PeerFromHeader>());
		});
	}

	private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, string? json = null)
	{
		HttpRequestMessage request = new(method, path);
		request.Headers.Add("X-Test-Peer", Peer);
		request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
		if (json is not null)
		{
			request.Content = new StringContent(json, Encoding.UTF8, "application/json");
		}

		return client.SendAsync(request);
	}

	private static async Task ExhaustTheBudgetAsync(HttpClient client)
	{
		HttpStatusCode last = HttpStatusCode.OK;
		for (int i = 0; i <= Permits; i++)
		{
			last = (await SendAsync(client, HttpMethod.Get, "/v1/collections", ApiKeyToken.NewToken(ApiKeyToken.NewId()))).StatusCode;
		}

		Assert.Equal(HttpStatusCode.TooManyRequests, last);
	}

	private static async Task<string> KeyIn(HttpResponseMessage response)
	{
		Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
		using JsonDocument issued = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return issued.RootElement.GetProperty("key").GetString()!;
	}

	[Fact]
	public async Task AKeyIssuedThroughTheEndpoint_PassesAnAddressOverItsBudget()
	{
		(_, string admin) = await factory.IssueAsync([MemoriaScopes.Admin]);
		using WebApplicationFactory<Program> limited = Limited();
		HttpClient client = limited.CreateClient();

		string issued = await KeyIn(await SendAsync(client, HttpMethod.Post, "/v1/admin/keys", admin, """{"label":"fresh","scopes":["read"],"allCollections":true}"""));
		await ExhaustTheBudgetAsync(client);

		Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/v1/collections", issued)).StatusCode);
	}

	[Fact]
	public async Task ARotatedSecret_PassesAnAddressOverItsBudget()
	{
		(_, string admin) = await factory.IssueAsync([MemoriaScopes.Admin]);
		(ApiKeyRecord target, _) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		using WebApplicationFactory<Program> limited = Limited();
		HttpClient client = limited.CreateClient();

		string rotated = await KeyIn(await SendAsync(client, HttpMethod.Post, $"/v1/admin/keys/{target.Id}:rotate", admin));
		await ExhaustTheBudgetAsync(client);

		Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/v1/collections", rotated)).StatusCode);
	}

	[Fact]
	public async Task ARevokedKey_WaitsOutTheBudget_LikeAnyGuesser()
	{
		(_, string admin) = await factory.IssueAsync([MemoriaScopes.Admin]);
		(ApiKeyRecord target, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		using WebApplicationFactory<Program> limited = Limited();
		HttpClient client = limited.CreateClient();
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Get, "/v1/collections", token)).StatusCode);

		Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Delete, $"/v1/admin/keys/{target.Id}", admin)).StatusCode);
		await ExhaustTheBudgetAsync(client);
		factory.Logs.Clear();

		Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(client, HttpMethod.Get, "/v1/collections", token)).StatusCode);
		// Throttled before the store is asked, so there is no rejection to log: a known key would have been looked up and found revoked.
		Assert.DoesNotContain(factory.Logs, log => log.EventId.Id == 3000 && Equals(log.State.GetValueOrDefault("KeyId"), target.Id));
	}
}
