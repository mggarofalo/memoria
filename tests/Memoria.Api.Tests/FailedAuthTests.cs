using System.Net;
using Memoria.Api.Auth;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Tests;

public sealed class FailedAuthLimiterTests
{
	private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

	private static FailedAuthLimiter Limiter(int permits = 3, int maxTracked = 10_000, bool enabled = true)
	{
		return new FailedAuthLimiter(Options.Create(new MemoriaOptions
		{
			Auth = new AuthOptions { FailedAuth = new FailedAuthOptions { PermitLimit = permits, MaxTrackedAddresses = maxTracked, Enabled = enabled, Window = TimeSpan.FromMinutes(1) } },
		}));
	}

	[Fact]
	public void BlocksAtTheLimit_ReportsTheCrossingOnce_AndRecoversAfterTheWindow()
	{
		FailedAuthLimiter limiter = Limiter();

		Assert.False(limiter.RecordFailure("a", Now));
		Assert.False(limiter.RecordFailure("a", Now.AddSeconds(1)));
		Assert.False(limiter.IsBlocked("a", Now.AddSeconds(1), out _));
		Assert.True(limiter.RecordFailure("a", Now.AddSeconds(2)));

		Assert.True(limiter.IsBlocked("a", Now.AddSeconds(10), out TimeSpan retryAfter));
		Assert.Equal(TimeSpan.FromSeconds(50), retryAfter);
		Assert.False(limiter.RecordFailure("a", Now.AddSeconds(11)));
		Assert.False(limiter.IsBlocked("b", Now.AddSeconds(10), out _));
		Assert.False(limiter.IsBlocked("a", Now.AddMinutes(1), out _));
	}

	[Fact]
	public void Disabled_NeverBlocks()
	{
		FailedAuthLimiter limiter = Limiter(permits: 1, enabled: false);

		limiter.RecordFailure("a", Now);

		Assert.False(limiter.IsBlocked("a", Now, out _));
		Assert.Equal(0, limiter.TrackedAddresses);
	}

	[Fact]
	public void MemoryIsBounded_UnderManyDistinctAddresses()
	{
		FailedAuthLimiter limiter = Limiter(maxTracked: 1000);

		for (int i = 0; i < 200_000; i++)
		{
			limiter.RecordFailure($"10.{i >> 16 & 255}.{i >> 8 & 255}.{i & 255}", Now);
			Assert.True(limiter.TrackedAddresses <= 1000);
		}
	}

	[Fact]
	public void RecentlyActiveAddress_SurvivesARotation_WhileItKeepsFailing()
	{
		FailedAuthLimiter limiter = Limiter(permits: 3, maxTracked: 100);
		limiter.RecordFailure("attacker", Now);
		limiter.RecordFailure("attacker", Now);
		limiter.RecordFailure("attacker", Now);

		for (int i = 0; i < 60; i++)
		{
			limiter.RecordFailure($"other-{i}", Now);
		}

		Assert.True(limiter.IsBlocked("attacker", Now, out _));
	}

	[Theory]
	[InlineData("203.0.113.9", "203.0.113.9")]
	[InlineData("::ffff:203.0.113.9", "203.0.113.9")]
	[InlineData("2001:db8:1:2:aaaa::1", "2001:db8:1:2::/64")]
	[InlineData("2001:db8:1:2:bbbb::2", "2001:db8:1:2::/64")]
	[InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
	public void KeyFor_NormalizesMappedAddresses_AndGroupsIpv6By64(string address, string expected)
	{
		Assert.Equal(expected, FailedAuthLimiter.KeyFor(IPAddress.Parse(address)));
	}

	[Fact]
	public void KeyFor_HandlesNoAddress()
	{
		Assert.Equal("unknown", FailedAuthLimiter.KeyFor(null));
	}

	[Theory]
	[InlineData("172.20.0.2", true)]
	[InlineData("172.20.0.0/16", true)]
	[InlineData("2001:db8::/32", true)]
	[InlineData("not-an-ip", false)]
	[InlineData("0.0.0.0/0", false)]
	[InlineData("::/0", false)]
	[InlineData("172.20.0.0/99", false)]
	public void TrustedProxyEntries_AreParsed_AndTrustAllIsRefused(string entry, bool valid)
	{
		Assert.Equal(valid, TrustedProxies.TryParse(entry, out _, out _));
	}
}

/// <summary>Runs through the real pipeline. A startup filter plays the socket: <c>X-Test-Peer</c> is the peer's address.</summary>
public sealed class FailedAuthPipelineTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private const string Attacker = "198.51.100.66";

	private const string Victim = "203.0.113.10";

	private const string Proxy = "172.20.0.2";

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

	private sealed class CountingStore(IApiKeyStore inner) : IApiKeyStore
	{
		private int _lookups;

		public int Lookups => Volatile.Read(ref _lookups);

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
			Interlocked.Increment(ref _lookups);
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
			return inner.TouchLastUsedAsync(id, at, cancellationToken);
		}

		public Task<bool> HasActiveAdminAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
		{
			return inner.HasActiveAdminAsync(now, cancellationToken);
		}
	}

	private WebApplicationFactory<Program> Limited(int permits = 5, string? trustedProxy = null, CountingStore? counting = null)
	{
		return factory.WithWebHostBuilder(builder =>
		{
			builder.UseSetting("Memoria:Auth:FailedAuth:PermitLimit", permits.ToString(System.Globalization.CultureInfo.InvariantCulture));
			builder.UseSetting("Memoria:Auth:FailedAuth:Window", "01:00:00");
			if (trustedProxy is not null)
			{
				builder.UseSetting("Memoria:Auth:TrustedProxies:0", trustedProxy);
			}

			builder.ConfigureTestServices(services =>
			{
				services.AddSingleton<IStartupFilter, PeerFromHeader>();
				if (counting is not null)
				{
					services.AddSingleton<IApiKeyStore>(counting);
				}
			});
		});
	}

	private static Task<HttpResponseMessage> SendAsync(HttpClient client, string peer, string? token, string? forwardedFor = null)
	{
		HttpRequestMessage request = new(HttpMethod.Get, "/v1/collections");
		request.Headers.Add("X-Test-Peer", peer);
		if (token is not null)
		{
			request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
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

	private async Task<string> ValidTokenAsync()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		return token;
	}

	[Fact]
	public async Task OverBudget_Is429WithRetryAfter_AndStopsTouchingTheDatabase()
	{
		CountingStore counting = new(factory.Store);
		using WebApplicationFactory<Program> limited = Limited(counting: counting);
		HttpClient client = limited.CreateClient();

		for (int i = 0; i < 5; i++)
		{
			Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, Attacker, BadToken())).StatusCode);
		}

		Assert.Equal(5, counting.Lookups);

		for (int i = 0; i < 50; i++)
		{
			HttpResponseMessage response = await SendAsync(client, Attacker, BadToken());
			Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
			Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
			Assert.Contains("auth_rate_limited", await response.Content.ReadAsStringAsync());
		}

		Assert.Equal(5, counting.Lookups);
	}

	[Fact]
	public async Task ValidKeys_KeepWorking_WhileAnotherAddressFloods()
	{
		string token = await ValidTokenAsync();
		using WebApplicationFactory<Program> limited = Limited();
		HttpClient client = limited.CreateClient();

		Task<HttpResponseMessage[]> flood = Task.WhenAll(Enumerable.Range(0, 400).Select(_ => SendAsync(client, Attacker, BadToken())));
		HttpResponseMessage[] valid = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => SendAsync(client, Victim, token)));
		HttpResponseMessage[] flooded = await flood;

		Assert.All(valid, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
		Assert.Contains(flooded, response => response.StatusCode == HttpStatusCode.TooManyRequests);
		Assert.DoesNotContain(flooded, response => response.StatusCode == HttpStatusCode.OK);
	}

	[Fact]
	public async Task ValidKeyRequests_DoNotSpendTheBudget()
	{
		string token = await ValidTokenAsync();
		using WebApplicationFactory<Program> limited = Limited(permits: 2);
		HttpClient client = limited.CreateClient();

		for (int i = 0; i < 20; i++)
		{
			Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, Victim, token)).StatusCode);
		}
	}

	[Fact]
	public async Task ValidKey_FromAnAddressOverBudget_WaitsLikeAnyoneThere()
	{
		string token = await ValidTokenAsync();
		using WebApplicationFactory<Program> limited = Limited(permits: 2);
		HttpClient client = limited.CreateClient();
		await SendAsync(client, Attacker, BadToken());
		await SendAsync(client, Attacker, BadToken());

		Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(client, Attacker, token)).StatusCode);
	}

	[Fact]
	public async Task NoProxyConfigured_ForwardedForIsIgnored_SoASpooferCannotEscape_OrSpendAVictimsBudget()
	{
		string token = await ValidTokenAsync();
		using WebApplicationFactory<Program> limited = Limited(permits: 3);
		HttpClient client = limited.CreateClient();

		// The attacker rotates the header and names the victim. Only the socket address counts.
		for (int i = 0; i < 3; i++)
		{
			Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, Attacker, BadToken(), $"{Victim}")).StatusCode);
		}

		Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(client, Attacker, BadToken(), "192.0.2.1")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, Victim, token)).StatusCode);
	}

	[Fact]
	public async Task UntrustedPeer_ForwardedForIsIgnored_EvenWithAProxyConfigured()
	{
		string token = await ValidTokenAsync();
		using WebApplicationFactory<Program> limited = Limited(permits: 2, trustedProxy: Proxy);
		HttpClient client = limited.CreateClient();

		await SendAsync(client, Attacker, BadToken(), Victim);
		await SendAsync(client, Attacker, BadToken(), Victim);

		Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(client, Attacker, BadToken(), "192.0.2.1")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, Proxy, token, Victim)).StatusCode);
	}

	[Fact]
	public async Task TrustedProxy_EachForwardedClientHasItsOwnBudget()
	{
		string token = await ValidTokenAsync();
		using WebApplicationFactory<Program> limited = Limited(permits: 2, trustedProxy: Proxy);
		HttpClient client = limited.CreateClient();

		await SendAsync(client, Proxy, BadToken(), Attacker);
		await SendAsync(client, Proxy, BadToken(), Attacker);

		Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(client, Proxy, BadToken(), Attacker)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, Proxy, token, Victim)).StatusCode);
	}

	[Fact]
	public async Task TrustedProxy_AClientWritingOnTheLeftOfTheChain_IsStillIdentifiedByWhatTheProxyAppended()
	{
		string token = await ValidTokenAsync();
		using WebApplicationFactory<Program> limited = Limited(permits: 2, trustedProxy: "172.20.0.0/16");
		HttpClient client = limited.CreateClient();

		// `$proxy_add_x_forwarded_for` appends the real peer to whatever the client sent.
		await SendAsync(client, Proxy, BadToken(), $"{Victim}, {Attacker}");
		await SendAsync(client, Proxy, BadToken(), $"192.0.2.1, {Attacker}");

		Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(client, Proxy, BadToken(), $"192.0.2.2, {Attacker}")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, Proxy, token, Victim)).StatusCode);
	}

	[Fact]
	public async Task LoopbackIsNotTrustedByDefault()
	{
		string token = await ValidTokenAsync();
		using WebApplicationFactory<Program> limited = Limited(permits: 1);
		HttpClient client = limited.CreateClient();

		await SendAsync(client, "127.0.0.1", BadToken(), Attacker);

		Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(client, "127.0.0.1", BadToken(), Victim)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, Attacker, token)).StatusCode);
	}

	[Fact]
	public async Task AnonymousProbes_StillAnswer_FromAnAddressOverBudget()
	{
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.Readiness("ready", resident: true));
		using WebApplicationFactory<Program> limited = Limited(permits: 1);
		HttpClient client = limited.CreateClient();
		await SendAsync(client, Attacker, BadToken());

		HttpRequestMessage probe = new(HttpMethod.Get, "/ready");
		probe.Headers.Add("X-Test-Peer", Attacker);
		probe.Headers.TryAddWithoutValidation("Authorization", $"Bearer {BadToken()}");

		Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(probe)).StatusCode);
	}

	[Fact]
	public async Task Crossing_IsLoggedOnce_AndAFloodLogsNoMoreRejections()
	{
		using WebApplicationFactory<Program> limited = Limited(permits: 2);
		HttpClient client = limited.CreateClient();
		factory.Logs.Clear();

		for (int i = 0; i < 30; i++)
		{
			await SendAsync(client, Attacker, BadToken());
		}

		Assert.Single(factory.Logs, l => l.EventId.Id == 3002 && l.Message.Contains(Attacker, StringComparison.Ordinal));
		Assert.Equal(2, factory.Logs.Count(l => l.EventId.Id == 3000));
	}

	[Fact]
	public async Task BlankTrustedProxy_AsComposeSendsWhenUnset_MeansNoProxy()
	{
		string token = await ValidTokenAsync();
		using WebApplicationFactory<Program> limited = Limited(permits: 1, trustedProxy: string.Empty);
		HttpClient client = limited.CreateClient();

		await SendAsync(client, Attacker, BadToken(), Victim);

		Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(client, Attacker, BadToken(), Victim)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, Victim, token)).StatusCode);
	}

	[Theory]
	[InlineData("not-an-ip")]
	[InlineData("0.0.0.0/0")]
	public void InvalidTrustedProxy_StopsTheHost(string entry)
	{
		using WebApplicationFactory<Program> broken = factory.WithWebHostBuilder(builder => builder.UseSetting("Memoria:Auth:TrustedProxies:0", entry));

		Exception failure = Assert.ThrowsAny<Exception>(() => broken.CreateClient());

		Assert.Contains("TrustedProxies", failure.ToString());
	}
}
