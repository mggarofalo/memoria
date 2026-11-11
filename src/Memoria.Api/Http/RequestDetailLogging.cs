using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Memoria.Api.Http;

/// <summary>
/// Keeps query strings and request paths out of the log whatever configuration says, because they carry search text and document paths,
/// and a secret-safety guarantee must not depend on a settings file being present. Configuration cannot raise these, as the
/// logger factory itself is replaced, which no filter rule can reach. <see cref="Setting"/> is the one way to turn it off, for debugging.
/// </summary>
public static partial class RequestDetailLogging
{
	/// <summary>Exactly <c>Enabled</c> lets the framework log request details again. Anything else leaves them out.</summary>
	public const string Setting = "Memoria:Logging:RequestDetails";

	private const string Enabled = "Enabled";

	/// <summary>Writes every request's path and query, and its scope carries the path into every other line of the request.</summary>
	private const string RequestLogCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

	/// <summary>What logs paths, URIs, tool names or tool arguments, and the least that can be trusted not to.</summary>
	private static readonly (string Category, LogLevel Least)[] s_capped =
	[
		("Microsoft.AspNetCore", LogLevel.Warning),
		("System.Net.Http.HttpClient", LogLevel.Warning),
		("Microsoft.Extensions.Http", LogLevel.Warning),
		("ModelContextProtocol", LogLevel.Critical),
	];

	public static void AddSafeRequestLogging(this IServiceCollection services, IConfiguration configuration)
	{
		if (IsAllowed(configuration))
		{
			return;
		}

		services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(provider => new SafeLoggerFactory(new LoggerFactory(
			provider.GetServices<ILoggerProvider>(),
			provider.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>(),
			provider.GetRequiredService<IOptions<LoggerFactoryOptions>>(),
			provider.GetService<IExternalScopeProvider>()))));
	}

	public static void WarnIfRequestDetailsAreLogged(this WebApplication app)
	{
		if (IsAllowed(app.Configuration))
		{
			LogRequestDetailsOn(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Memoria.Api.Http"));
		}
	}

	private static bool IsAllowed(IConfiguration configuration)
	{
		return string.Equals(configuration[Setting], Enabled, StringComparison.Ordinal);
	}

	[LoggerMessage(EventId = 1100, Level = LogLevel.Warning, Message = "REQUEST DETAILS ARE BEING LOGGED (Memoria:Logging:RequestDetails = Enabled). At the configured log levels, the log can now hold search text, document paths and anything a caller puts in a URL, credentials included. Turn this off when you finish debugging.")]
	private static partial void LogRequestDetailsOn(ILogger logger);

	private sealed class SafeLoggerFactory(ILoggerFactory inner) : ILoggerFactory
	{
		public ILogger CreateLogger(string categoryName)
		{
			if (categoryName.StartsWith(RequestLogCategory, StringComparison.Ordinal))
			{
				return new SilentRequestLog();
			}

			ILogger logger = inner.CreateLogger(categoryName);
			foreach ((string category, LogLevel least) in s_capped)
			{
				if (categoryName.StartsWith(category, StringComparison.Ordinal))
				{
					return new Capped(logger, least);
				}
			}

			return logger;
		}

		public void AddProvider(ILoggerProvider provider)
		{
			inner.AddProvider(provider);
		}

		public void Dispose()
		{
			inner.Dispose();
		}
	}

	/// <summary>
	/// Writes nothing and opens no scope. It still answers yes for Critical, because the server only starts each request's trace
	/// activity when its log is on, and every trace id comes from that activity.
	/// </summary>
	private sealed class SilentRequestLog : ILogger
	{
		// Stryker disable once Block : the body only returns null, which is what the emptied block returns too.
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return logLevel == LogLevel.Critical;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
		}
	}

	private sealed class Capped(ILogger inner, LogLevel least) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull
		{
			return inner.BeginScope(state);
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return logLevel >= least && inner.IsEnabled(logLevel);
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (logLevel >= least)
			{
				inner.Log(logLevel, eventId, state, exception, formatter);
			}
		}
	}
}
