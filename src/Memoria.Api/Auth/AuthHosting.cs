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
		if (auth.IsDisabled)
		{
			LogAuthDisabled(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Memoria.Api.Auth"));
			return;
		}

		IApiKeyStore store = app.Services.GetRequiredService<IApiKeyStore>();
		await store.InitializeAsync();
		await app.Services.GetRequiredService<KnownGoodKeys>().LoadAsync(store, app.Services.GetRequiredService<TimeProvider>().GetUtcNow());
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

	[LoggerMessage(EventId = 3200, Level = LogLevel.Warning, Message = "AUTHENTICATION IS DISABLED (Memoria:Auth:Mode = Disabled). Every caller can read every collection. Use this only for development on a trusted LAN; never expose this host.")]
	private static partial void LogAuthDisabled(ILogger logger);
}
