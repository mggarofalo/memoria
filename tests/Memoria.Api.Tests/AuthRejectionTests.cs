using System.Net;
using Memoria.Api.Auth;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Tests;

/// <summary>What a refused caller hears, and what the operator's log says about why. The two must differ.</summary>
public sealed class AuthRejectionTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
	private async Task<HttpResponseMessage> GetAsync(string path, string? authorization)
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		using HttpRequestMessage request = new(HttpMethod.Get, path);
		if (authorization is not null)
		{
			request.Headers.TryAddWithoutValidation("Authorization", authorization);
		}

		return await factory.CreateClient().SendAsync(request);
	}

	private void AssertLoggedReason(string reason)
	{
		Assert.Contains(factory.Logs, line => line.Message.Contains($"API key rejected: {reason} ", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ARefusal_SaysTheSameThingToEveryCaller_AndNamesTheReasonOnlyInTheLog()
	{
		(ApiKeyRecord revoked, string revokedToken) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);
		await factory.Store.RevokeAsync(revoked.Id, DateTimeOffset.UtcNow);
		(_, string expiredToken) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true, expiresAt: DateTimeOffset.UtcNow.AddSeconds(-5));
		(ApiKeyRecord real, _) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);

		(string Token, string Reason)[] cases =
		[
			("not-a-key", "malformed"),
			(ApiKeyToken.NewToken(ApiKeyToken.NewId()), "unknown id"),
			(ApiKeyToken.NewToken(real.Id), "wrong secret"),
			(revokedToken, "revoked"),
			(expiredToken, "expired"),
		];

		string? firstBody = null;
		foreach ((string token, string reason) in cases)
		{
			factory.Logs.Clear();
			HttpResponseMessage response = await GetAsync("/v1/collections", $"Bearer {token}");
			string body = await response.Content.ReadAsStringAsync();

			Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
			AssertLoggedReason(reason);
			Assert.DoesNotContain(reason, body, StringComparison.Ordinal);
			Assert.Equal("Bearer realm=\"memoria\"", response.Headers.WwwAuthenticate.ToString());
			Assert.Contains("\"title\":\"Unauthorized\"", body, StringComparison.Ordinal);
			Assert.Contains("A valid API key is required", body, StringComparison.Ordinal);
			string withoutTrace = System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", string.Empty);
			Assert.Equal(firstBody ?? withoutTrace, withoutTrace);
			firstBody ??= withoutTrace;
		}
	}

	[Fact]
	public async Task AKeyWithoutTheScope_IsForbidden_AndTheChallengeSaysWhy()
	{
		(_, string token) = await factory.IssueAsync([MemoriaScopes.Read], allCollections: true);

		HttpResponseMessage response = await GetAsync("/v1/admin/keys", $"Bearer {token}");
		string body = await response.Content.ReadAsStringAsync();

		Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		Assert.Equal("Bearer realm=\"memoria\", error=\"insufficient_scope\"", response.Headers.WwwAuthenticate.ToString());
		Assert.Contains("\"title\":\"Insufficient scope\"", body, StringComparison.Ordinal);
		Assert.Contains($"This API key lacks the '{MemoriaScopes.Admin}' scope.", body, StringComparison.Ordinal);
		Assert.Contains("\"code\":\"insufficient_scope\"", body, StringComparison.Ordinal);
	}
}

public sealed class ApiKeyLimitTests
{
	private static ApiKeyRecord Record(DateTimeOffset? expiresAt)
	{
		return new ApiKeyRecord("id", new byte[32], "label", [MemoriaScopes.Read], [], true, DateTimeOffset.UnixEpoch, expiresAt, null, null);
	}

	[Fact]
	public void AKey_ExpiresAtTheExactInstant_NotAfterIt()
	{
		DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		Assert.True(Record(now).IsExpired(now));
		Assert.True(Record(now.AddTicks(-1)).IsExpired(now));
		Assert.False(Record(now.AddTicks(1)).IsExpired(now));
		Assert.False(Record(null).IsExpired(now));
	}

	private static FailedAuthLimiter Limiter(Action<FailedAuthOptions> configure)
	{
		MemoriaOptions options = new();
		configure(options.Auth.FailedAuth);
		return new FailedAuthLimiter(Options.Create(options));
	}

	[Fact]
	public void TheLimiter_NeedsOptions()
	{
		Assert.Throws<ArgumentNullException>(() => new FailedAuthLimiter(null!));
	}

	[Fact]
	public void AnAddress_MovesBetweenGenerations_WithoutBeingHeldTwice()
	{
		DateTimeOffset now = DateTimeOffset.UnixEpoch;
		FailedAuthLimiter limiter = Limiter(static o =>
		{
			o.MaxTrackedAddresses = 4;
			o.PermitLimit = 10;
			o.Window = TimeSpan.FromHours(1);
		});

		// Two fill a generation, the third starts a new one, and the first is then in the old one.
		limiter.TryReserve("a", now);
		limiter.TryReserve("b", now);
		limiter.TryReserve("c", now);
		Assert.Equal(3, limiter.TrackedAddresses);

		limiter.TryReserve("a", now);
		Assert.Equal(3, limiter.TrackedAddresses);
	}

	[Fact]
	public void WhenDisabled_EveryoneIsAdmitted_AndNothingIsReportedAsCrossed()
	{
		FailedAuthLimiter limiter = Limiter(static o =>
		{
			o.Enabled = false;
			o.PermitLimit = 1;
		});

		for (int i = 0; i < 3; i++)
		{
			FailedAuthDecision decision = limiter.TryReserve("a", DateTimeOffset.UnixEpoch);
			Assert.True(decision.Admitted);
			Assert.False(decision.Crossed);
			Assert.Equal(TimeSpan.Zero, decision.RetryAfter);
		}

		Assert.Equal(0, limiter.TrackedAddresses);
	}

	[Theory]
	[InlineData("not-an-address")]
	[InlineData("0.0.0.0/0")]
	public void ABadTrustedProxy_StopsStartup_ByName(string entry)
	{
		IApplicationBuilder app = WebApplication.CreateBuilder().Build();

		InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => app.UseMemoriaForwardedHeaders([entry]));

		Assert.Contains($"invalid entry: '{entry}'", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void NoTrustedProxies_IsNotAnError()
	{
		IApplicationBuilder app = WebApplication.CreateBuilder().Build();

		Assert.Same(app, app.UseMemoriaForwardedHeaders(null));
		Assert.Same(app, app.UseMemoriaForwardedHeaders([" "]));
	}
}
