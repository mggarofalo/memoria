using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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

	/// <summary>The key holds <c>admin</c> beside a collection list, and was not given the scope.</summary>
	public const string AdminWithheld = "memoria:admin-withheld";
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

				// Stryker disable once Statement : without it, the loop still comes round: previous is 0 here, so the TryUpdate below fails and the loop retries.
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
	KnownGoodKeys knownGood,
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

		if (!TryReadToken(values, out string? token, out string? id))
		{
			return SpendBudget() ?? Rejected("malformed", null);
		}

		// A secret this process knows skips the address's budget, so a valid key is not
		// locked out by an attacker sharing its address. Anything else reserves one
		// failure before it may touch the store, which makes the limit hold under
		// concurrency, and gets it back only if it turns out to be valid.
		byte[] hash = ApiKeyToken.Hash(token);
		bool known = knownGood.Matches(id, hash);
		if (!known && SpendBudget() is { } throttled)
		{
			return throttled;
		}

		ApiKeyRecord? record = await store.FindAsync(id, Context.RequestAborted);
		DateTimeOffset now = timeProvider.GetUtcNow();
		if (WhyRejected(record, hash, now) is { } reason)
		{
			return RejectedAfterLookup(reason, id, known);
		}

		if (!known)
		{
			failedAuth.Refund(ClientKey);
		}

		knownGood.Remember(id, hash);
		await TouchAsync(id, now);
		return AuthenticateResult.Success(new AuthenticationTicket(BuildPrincipal(record!), Scheme.Name));
	}

	private static bool TryReadToken(StringValues values, [NotNullWhen(true)] out string? token, [NotNullWhen(true)] out string? id)
	{
		token = null;
		id = null;
		string? header = values.Count == 1 ? values[0] : null;
		if (header is null || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		token = header[BearerPrefix.Length..].Trim();
		return ApiKeyToken.TryParseId(token, out id);
	}

	private static string? WhyRejected(ApiKeyRecord? record, byte[] hash, DateTimeOffset now)
	{
		bool matches = ApiKeyToken.HashesMatch(hash, record?.Sha256Hash ?? PlaceholderHash);
		if (record is null)
		{
			return "unknown id";
		}

		if (!matches)
		{
			return "wrong secret";
		}

		if (record.IsRevoked)
		{
			return "revoked";
		}

		return record.IsExpired(now) ? "expired" : null;
	}

	/// <summary>Reserves one failure. Null when there was budget; otherwise the throttled result, with no log line, so a flood costs almost nothing.</summary>
	private AuthenticateResult? SpendBudget()
	{
		FailedAuthDecision decision = failedAuth.TryReserve(ClientKey, timeProvider.GetUtcNow());
		if (decision.Crossed)
		{
			LogAddressThrottled(_log, ClientKey);
		}

		if (decision.Admitted)
		{
			return null;
		}

		Context.Features.Set(new FailedAuthThrottled(decision.RetryAfter));
		// Stryker disable once String : the challenge answers from the feature above; this text reaches only the framework's own debug log.
		return AuthenticateResult.Fail("throttled");
	}

	/// <summary>A known key that failed was revoked, expired or rotated elsewhere: it stops being known, and pays like any other failure.</summary>
	private AuthenticateResult RejectedAfterLookup(string reason, string id, bool known)
	{
		if (known)
		{
			knownGood.Forget(id);
			_ = SpendBudget();
		}

		return Rejected(reason, id);
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
		Endpoint? endpoint = Context.GetEndpoint();
		string? required = endpoint?.Metadata.GetMetadata<MemoriaScopeAttribute>()?.Scope;
		if (IsBoundKeyOnUnboundRoute(endpoint, required))
		{
			await WriteProblemAsync(
				StatusCodes.Status403Forbidden,
				"Key is bound to collections",
				"Operations and key management act on every collection, so they need an admin key that lists no collections. "
					+ "A key holding admin beside a collection list is not given the admin scope: issue an admin key without collections, then revoke this one.",
				"collection_scoped_key");
			return;
		}

		Response.Headers.WWWAuthenticate = "Bearer realm=\"memoria\", error=\"insufficient_scope\"";
		await WriteProblemAsync(StatusCodes.Status403Forbidden, "Insufficient scope", $"This API key lacks the '{required}' scope.", "insufficient_scope");
	}

	/// <summary>
	/// The key has the scope, or had admin withheld for being bound, so what failed is the route's refusal of a key bound
	/// to collections.
	/// </summary>
	private bool IsBoundKeyOnUnboundRoute(Endpoint? endpoint, string? required)
	{
		bool withheld = required == MemoriaScopes.Admin && Context.User.HasClaim(static c => c.Type == MemoriaClaims.AdminWithheld);
		return endpoint?.Metadata.GetMetadata<MemoriaUnboundKeyAttribute>() is not null
			&& (required is null || withheld || Context.User.HasClaim(MemoriaClaims.Scope, required))
			// Stryker disable once Equality : a key that holds the route's scope is refused there only for a collection claim, so when this line runs, a claim of any other type means one of that type too.
			&& Context.User.HasClaim(static c => c.Type == MemoriaClaims.Collection);
	}

	/// <summary>
	/// A key that holds <c>admin</c> beside a collection list, which can no longer be issued, does not get the scope: admin is
	/// all-or-nothing, and honouring it on a bound key would let the key escape its binding. It keeps its other scopes, and
	/// <see cref="MemoriaClaims.AdminWithheld"/> marks it so a refusal can say why.
	/// </summary>
	private static ClaimsPrincipal BuildPrincipal(ApiKeyRecord record)
	{
		List<Claim> claims = [new(MemoriaClaims.KeyId, record.Id), new(ClaimTypes.Name, record.Label)];
		bool withhold = record.HoldsBoundAdmin;
		claims.AddRange(record.Scopes
			.Where(scope => !withhold || scope != MemoriaScopes.Admin)
			.Select(static scope => new Claim(MemoriaClaims.Scope, scope)));
		if (withhold)
		{
			claims.Add(new Claim(MemoriaClaims.AdminWithheld, "true"));
		}

		if (record.AllCollections)
		{
			// Stryker disable once String : only the claim's type is ever read (HttpCallerScope), never its value.
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

	private AuthenticateResult Rejected(string reason, string? keyId)
	{
		LogRejected(_log, reason, keyId);
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
	// Stryker disable String : only these claims' types are ever read (HttpCallerScope, HealthEndpoints), never their values.
	private static readonly ClaimsPrincipal Everyone = new(new ClaimsIdentity(
		[
			new Claim(MemoriaClaims.AuthDisabled, "true"),
			new Claim(MemoriaClaims.AllCollections, "true"),
			new Claim(MemoriaClaims.Scope, MemoriaScopes.Read),
			new Claim(MemoriaClaims.Scope, MemoriaScopes.Ingest),
			new Claim(MemoriaClaims.Scope, MemoriaScopes.Admin),
		],
		ApiKeyAuthenticationHandler.SchemeName));
	// Stryker restore String

	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(Everyone, Scheme.Name)));
	}
}
