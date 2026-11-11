using System.Threading.RateLimiting;
using Memoria.Api.Configuration;
using Memoria.Api.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Memoria.Api.Auth;

public static partial class AuthHosting
{
	/// <summary>
	/// Registers authentication, scope authorization, rate limiting and audit.
	/// Fail closed by construction: the fallback policy demands an authenticated
	/// caller on any endpoint that does not explicitly allow anonymous access.
	/// </summary>
	public static IServiceCollection AddMemoriaAuth(this IServiceCollection services, AuthOptions auth)
	{
		ArgumentNullException.ThrowIfNull(auth);

		services.AddHttpContextAccessor();
		services.TryAddTimeProvider();
		services.AddSingleton(new ControlDatabase(auth.DatabasePath));
		services.AddSingleton<IApiKeyStore, SqliteApiKeyStore>();
		services.AddSingleton<LastUsedThrottle>();
		services.AddSingleton<FailedAuthLimiter>();
		services.AddSingleton(new KnownGoodKeys());
		services.AddScoped<ICallerScope, HttpCallerScope>();
		services.AddScoped<IRequestAudit, HttpRequestAudit>();
		services.AddSingleton<IAuthorizationHandler, MemoriaScopeHandler>();
		services.AddSingleton<IAuthorizationHandler, MemoriaUnboundKeyHandler>();

		AuthenticationBuilder authentication = services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName);
		if (auth.IsDisabled)
		{
			authentication.AddScheme<AuthenticationSchemeOptions, DisabledAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
			services.AddSingleton<IAuditLog, NullAuditLog>();
		}
		else
		{
			authentication.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
			services.AddSingleton<IAuditLog, SqliteAuditLog>();
		}

		services.AddAuthorizationBuilder()
			.SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

		services.AddRateLimiter(options =>
		{
			options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
			options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => Partition(context, auth.RateLimit));
			options.OnRejected = WriteRejectionAsync;
		});

		return services;
	}

	/// <summary>
	/// Order matters. Audit wraps the rest so it sees what they decide. The limiter
	/// runs before authorization so a key hammering routes it may not use spends
	/// its own permits, instead of getting unlimited 403s and audit rows.
	/// </summary>
	public static IApplicationBuilder UseMemoriaAuth(this IApplicationBuilder app)
	{
		app.UseAuthentication();
		app.UseMiddleware<AuditMiddleware>();
		app.UseRateLimiter();
		app.UseAuthorization();
		return app;
	}

	/// <summary>Opens the control database, or warns loudly that nothing is protecting the corpus.</summary>
	public static async Task InitializeMemoriaAuthAsync(this WebApplication app)
	{
		AuthOptions auth = app.Services.GetRequiredService<IOptions<MemoriaOptions>>().Value.Auth;
		ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Memoria.Api.Auth");
		if (auth.IsDisabled)
		{
			RefuseDisabledOnNetwork(logger, app.Configuration, auth.AllowDisabledOnNetwork);
			return;
		}

		IApiKeyStore store = app.Services.GetRequiredService<IApiKeyStore>();
		await store.InitializeAsync();
		DateTimeOffset now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow();
		await app.Services.GetRequiredService<KnownGoodKeys>().LoadAsync(store, now);
		foreach (ApiKeyRecord bound in (await store.ListAsync()).Where(record => record.IsActive(now) && record.HoldsBoundAdmin))
		{
			LogBoundAdmin(logger, bound.Id);
		}
	}

	/// <summary>Disabled mode starts only on loopback, unless the operator opted in to the network. It says so loudly either way.</summary>
	internal static void RefuseDisabledOnNetwork(ILogger logger, IConfiguration configuration, bool allowOnNetwork)
	{
		LogAuthDisabled(logger);
		IReadOnlyList<string> addresses = DisabledAuthGuard.ListenAddresses(configuration);
		string[] network = DisabledAuthGuard.OnNetwork(addresses);
		if (network.Length == 0)
		{
			LogDisabledOnLoopback(logger, string.Join(';', addresses));
			return;
		}

		string listed = string.Join(';', network);
		if (!allowOnNetwork)
		{
			LogDisabledRefused(logger, listed);
			throw new InvalidOperationException(
				$"Memoria:Auth:Mode is Disabled and the host would listen on {listed}, which is not loopback. Every caller there could read every collection. "
				+ "Listen on loopback only (ASPNETCORE_URLS=http://127.0.0.1:8080), turn API keys on, or set Memoria:Auth:AllowDisabledOnNetwork to true if you mean it.");
		}

		LogDisabledOnNetwork(logger, listed);
	}

	private static void TryAddTimeProvider(this IServiceCollection services)
	{
		if (!services.Any(static service => service.ServiceType == typeof(TimeProvider)))
		{
			services.AddSingleton(TimeProvider.System);
		}
	}

	/// <summary>Partitions on the key id, so limits survive a reverse proxy that makes every caller the same IP.</summary>
	private static RateLimitPartition<string> Partition(HttpContext context, RateLimitOptions limits)
	{
		string? keyId = context.User.FindFirst(MemoriaClaims.KeyId)?.Value;
		if (!limits.Enabled || keyId is null)
		{
			return RateLimitPartition.GetNoLimiter(string.Empty);
		}

		return RateLimitPartition.GetFixedWindowLimiter(keyId, _ => new FixedWindowRateLimiterOptions
		{
			PermitLimit = limits.PermitLimit,
			Window = limits.Window,
			QueueLimit = 0,
			AutoReplenishment = true,
		});
	}

	private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
	{
		HttpContext http = context.HttpContext;
		if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
		{
			http.Response.Headers[HeaderNames.RetryAfter] = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
		await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
		{
			HttpContext = http,
			ProblemDetails = new ProblemDetails
			{
				Status = StatusCodes.Status429TooManyRequests,
				Title = "Too many requests",
				Detail = "This API key exceeded its rate limit; retry after the interval in Retry-After.",
				Extensions = { ["code"] = "rate_limited" },
			},
		});
	}

	[LoggerMessage(EventId = 3200, Level = LogLevel.Warning, Message = "AUTHENTICATION IS DISABLED (Memoria:Auth:Mode = Disabled). Every caller can read every collection. Use this only for development; never expose this host.")]
	private static partial void LogAuthDisabled(ILogger logger);

	[LoggerMessage(EventId = 3201, Level = LogLevel.Warning, Message = "API key {KeyId} holds the admin scope beside a collection list, so it is not given the admin scope. Admin acts on every collection. Issue an admin key without collections, and a read or ingest key for the collections, then revoke this one.")]
	private static partial void LogBoundAdmin(ILogger logger, string keyId);

	[LoggerMessage(EventId = 3202, Level = LogLevel.Warning, Message = "Authentication is disabled, listening on loopback only: {Addresses}")]
	private static partial void LogDisabledOnLoopback(ILogger logger, string addresses);

	[LoggerMessage(EventId = 3203, Level = LogLevel.Critical, Message = "REFUSING TO START: authentication is disabled and the host would listen on {Addresses}, which is not loopback. Listen on loopback, turn API keys on, or set Memoria:Auth:AllowDisabledOnNetwork to true.")]
	private static partial void LogDisabledRefused(ILogger logger, string addresses);

	[LoggerMessage(EventId = 3204, Level = LogLevel.Critical, Message = "AUTHENTICATION IS DISABLED ON THE NETWORK at {Addresses} (Memoria:Auth:AllowDisabledOnNetwork = true). Anyone who can reach these addresses can read every collection.")]
	private static partial void LogDisabledOnNetwork(ILogger logger, string addresses);
}
