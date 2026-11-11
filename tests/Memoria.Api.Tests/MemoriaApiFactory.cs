using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memoria.Api.Tests;

/// <summary>
/// Boots the API against a <see cref="FakeSidecar"/> and captures its log
/// entries. One factory per test class; tests reset the sidecar's routes. This
/// factory opts in to disabled authentication, so tests of everything else need
/// no key; <see cref="AuthApiFactory"/> runs with keys enforced.
/// </summary>
public class MemoriaApiFactory : WebApplicationFactory<Program>
{
	public FakeSidecar Sidecar { get; } = new();

	public ConcurrentQueue<CapturedLog> Logs { get; } = new();

	public const string SidecarBaseUrl = "http://sidecar.test:8282";

	public const string SidecarSecret = "test-secret-of-sixteen-chars";

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Production");
		// The test server binds nothing, but disabled mode refuses to start unless configuration names loopback only.
		builder.UseSetting("urls", "http://127.0.0.1:0");
		builder.UseSetting("Memoria:Sidecar:BaseUrl", SidecarBaseUrl);
		builder.UseSetting("Memoria:Sidecar:Secret", SidecarSecret);
		builder.UseSetting("Memoria:Sidecar:ReadinessTimeout", "00:00:01");
		// Each test scripts its own sidecar answer, so none may be served another's.
		builder.UseSetting("Memoria:Sidecar:ReadinessCacheTime", "00:00:00");
		builder.UseSetting("Memoria:Search:MaxLimit", "50");
		builder.UseSetting("Memoria:Search:DefaultLimit", "10");
		builder.UseSetting("Memoria:Ingest:MinReindexInterval", "00:00:00");
		builder.UseSetting("Logging:LogLevel:Default", "Debug");
		ConfigureAuth(builder);

		builder.ConfigureLogging(logging =>
		{
			logging.ClearProviders();
			logging.AddProvider(new CaptureLoggerProvider(Logs));
		});

		builder.ConfigureTestServices(services =>
		{
			services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => Sidecar));
		});
	}

	protected virtual void ConfigureAuth(IWebHostBuilder builder)
	{
		builder.UseSetting("Memoria:Auth:Mode", "Disabled");
	}

	public sealed record CapturedLog(string Category, LogLevel Level, EventId EventId, string Message, IReadOnlyDictionary<string, object?> State);

	private sealed class CaptureLoggerProvider(ConcurrentQueue<CapturedLog> sink) : ILoggerProvider
	{
		public ILogger CreateLogger(string categoryName)
		{
			return new CaptureLogger(categoryName, sink);
		}

		public void Dispose()
		{
		}
	}

	private sealed class CaptureLogger(string category, ConcurrentQueue<CapturedLog> sink) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull
		{
			return null;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return true;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			Dictionary<string, object?> values = [];
			if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
			{
				foreach ((string key, object? value) in pairs)
				{
					values[key] = value;
				}
			}

			sink.Enqueue(new CapturedLog(category, logLevel, eventId, formatter(state, exception), values));
		}
	}
}
