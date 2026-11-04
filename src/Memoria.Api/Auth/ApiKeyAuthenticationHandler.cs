using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Memoria.Api.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Memoria.Api.Auth;

/// <summary>Claim names on the principal the handlers build; <see cref="HttpCallerScope"/> reads them back.</summary>
public static class MemoriaClaims
{
	public const string KeyId = "memoria:key-id";
	public const string Scope = "memoria:scope";
	public const string Collection = "memoria:collection";
	public const string AllCollections = "memoria:all-collections";
	public const string AuthDisabled = "memoria:auth-disabled";
}

/// <summary>
/// Limits <c>last_used_at</c> to one write per key per <see cref="Interval"/>,
/// so reads do not each become a database write. The value is advisory, so a
/// restart re-writing it early does no harm.
/// </summary>
public sealed class LastUsedThrottle
{
	public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

	private readonly ConcurrentDictionary<string, long> _lastWrites = new(StringComparer.Ordinal);

	public bool ShouldWrite(string keyId, DateTimeOffset now)
	{
		long current = now.UtcTicks;
		while (true)
		{
			if (!_lastWrites.TryGetValue(keyId, out long previous))
			{
				if (_lastWrites.TryAdd(keyId, current))
				{
					return true;
				}

				continue;
			}

			if (current - previous < Interval.Ticks)
			{
				return false;
			}

			if (_lastWrites.TryUpdate(keyId, current, previous))
			{
				return true;
			}
		}
	}
}

/// <summary>
/// Bearer authentication for <c>mem_&lt;id&gt;_&lt;secret&gt;</c> keys. Every failure
/// looks the same to the caller; the distinguishing reason goes to the log, with
/// the public key id at most and never any of the token's secret part.
/// </summary>
public sealed partial class ApiKeyAuthenticationHandler(
	IOptionsMonitor<AuthenticationSchemeOptions> options,
	ILoggerFactory loggerFactory,
	UrlEncoder encoder,
	IApiKeyStore store,
	LastUsedThrottle throttle,
	FailedAuthLimiter failedAuth,
	TimeProvider timeProvider)
	: AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
	public const string SchemeName = "ApiKey";

	private const string BearerPrefix = "Bearer ";

	/// <summary>Compared against when the id is unknown, so that path does the same hash work.</summary>
	private static readonly byte[] PlaceholderHash = new byte[32];

	private readonly ILogger _log = loggerFactory.CreateLogger<ApiKeyAuthenticationHandler>();

	protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		if (!Request.Headers.TryGetValue(HeaderNames.Authorization, out StringValues values))
		{
			return AuthenticateResult.NoResult();
		}

		// Spent budget means no lookup and no log line, so a flood costs almost nothing.
		if (failedAuth.IsBlocked(ClientKey, timeProvider.GetUtcNow(), out TimeSpan retryAfter))
		{
			Context.Features.Set(new FailedAuthThrottled(retryAfter));
			return AuthenticateResult.Fail("throttled");
		}

		string? header = values.Count == 1 ? values[0] : null;
		if (header is null || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return Reject("malformed", null);
		}

		string token = header[BearerPrefix.Length..].Trim();
		if (!ApiKeyToken.TryParseId(token, out string? id))
		{
			return Reject("malformed", null);
		}

		ApiKeyRecord? record = await store.FindAsync(id, Context.RequestAborted);
		bool matches = ApiKeyToken.HashesMatch(ApiKeyToken.Hash(token), record?.Sha256Hash ?? PlaceholderHash);
		if (record is null || !matches)
		{
			return Reject(record is null ? "unknown id" : "wrong secret", id);
		}

		DateTimeOffset now = timeProvider.GetUtcNow();
		if (record.IsRevoked)
		{
			return Reject("revoked", id);
		}

		if (record.IsExpired(now))
		{
			return Reject("expired", id);
		}

		await TouchAsync(id, now);
		return AuthenticateResult.Success(new AuthenticationTicket(BuildPrincipal(record), Scheme.Name));
	}

	protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
	{
		if (Context.Features.Get<FailedAuthThrottled>() is { } throttled)
		{
			Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(throttled.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
			await WriteProblemAsync(StatusCodes.Status429TooManyRequests, "Too many failed authentication attempts", "This address exceeded its failed-authentication limit; retry after the interval in Retry-After.", "auth_rate_limited");
			return;
		}

		Response.Headers.WWWAuthenticate = "Bearer realm=\"memoria\"";
		await WriteProblemAsync(StatusCodes.Status401Unauthorized, "Unauthorized", "A valid API key is required: Authorization: Bearer <key>.", "unauthorized");
	}

	protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
	{
		string? required = Context.GetEndpoint()?.Metadata.GetMetadata<MemoriaScopeAttribute>()?.Scope;
		Response.Headers.WWWAuthenticate = "Bearer realm=\"memoria\", error=\"insufficient_scope\"";
		await WriteProblemAsync(StatusCodes.Status403Forbidden, "Insufficient scope", $"This API key lacks the '{required}' scope.", "insufficient_scope");
	}

	private static ClaimsPrincipal BuildPrincipal(ApiKeyRecord record)
	{
		List<Claim> claims = [new(MemoriaClaims.KeyId, record.Id), new(ClaimTypes.Name, record.Label)];
		claims.AddRange(record.Scopes.Select(static scope => new Claim(MemoriaClaims.Scope, scope)));
		if (record.AllCollections)
		{
			claims.Add(new Claim(MemoriaClaims.AllCollections, "true"));
		}
		else
		{
			claims.AddRange(record.Collections.Select(static collection => new Claim(MemoriaClaims.Collection, collection)));
		}

		return new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
	}

	private async Task TouchAsync(string id, DateTimeOffset now)
	{
		if (!throttle.ShouldWrite(id, now))
		{
			return;
		}

		try
		{
			await store.TouchLastUsedAsync(id, now, CancellationToken.None);
		}
		catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
		{
			// Advisory metadata; losing one update must not fail an authenticated request.
			LogTouchFailed(_log, ex, id);
		}
	}

	private AuthenticateResult Reject(string reason, string? keyId)
	{
		LogRejected(_log, reason, keyId);
		if (failedAuth.RecordFailure(ClientKey, timeProvider.GetUtcNow()))
		{
			LogAddressThrottled(_log, ClientKey);
		}

		return AuthenticateResult.Fail(reason);
	}

	private string ClientKey => FailedAuthLimiter.KeyFor(Context.Connection.RemoteIpAddress);

	private async Task WriteProblemAsync(int status, string title, string detail, string code)
	{
		Response.StatusCode = status;
		IProblemDetailsService problemDetails = Context.RequestServices.GetRequiredService<IProblemDetailsService>();
		await problemDetails.WriteAsync(new ProblemDetailsContext
		{
			HttpContext = Context,
			ProblemDetails = new ProblemDetails
			{
				Status = status,
				Title = title,
				Detail = detail,
				Extensions = { ["code"] = code },
			},
		});
	}

	[LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "API key rejected: {Reason} (key {KeyId})")]
	private static partial void LogRejected(ILogger logger, string reason, string? keyId);

	[LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "Address {ClientAddress} exceeded the failed-authentication limit and is refused until its window ends. If this is the reverse proxy's address, Memoria:Auth:TrustedProxies is not set and every client is sharing one budget.")]
	private static partial void LogAddressThrottled(ILogger logger, string clientAddress);

	[LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Could not update last_used_at for key {KeyId}")]
	private static partial void LogTouchFailed(ILogger logger, Exception exception, string keyId);
}

/// <summary>
/// <c>Memoria:Auth:Mode = Disabled</c>: every request is an all-scopes,
/// all-collections caller. Key management stays off (see <c>AdminKeyEndpoints</c>).
/// </summary>
public sealed class DisabledAuthenticationHandler(
	IOptionsMonitor<AuthenticationSchemeOptions> options,
	ILoggerFactory loggerFactory,
	UrlEncoder encoder)
	: AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
	private static readonly ClaimsPrincipal Everyone = new(new ClaimsIdentity(
		[
			new Claim(MemoriaClaims.AuthDisabled, "true"),
			new Claim(MemoriaClaims.AllCollections, "true"),
			new Claim(MemoriaClaims.Scope, MemoriaScopes.Read),
			new Claim(MemoriaClaims.Scope, MemoriaScopes.Ingest),
			new Claim(MemoriaClaims.Scope, MemoriaScopes.Admin),
		],
		ApiKeyAuthenticationHandler.SchemeName));

	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(Everyone, Scheme.Name)));
	}
}
