using System.Collections.Concurrent;
using Memoria.Api.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memoria.Api.Tests;

/// <summary>
/// The logger factory that keeps request details out of the log still behaves as a logger factory:
/// providers added later get lines and are disposed with the host, scopes reach them, and a capped
/// category lets through exactly the levels from its floor up. Each test builds a host of its own,
/// because the factory is made once per host.
/// </summary>
public sealed class SafeLoggerFactoryTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	private sealed class Recorder : ILoggerProvider
	{
		public ConcurrentQueue<string> Lines { get; } = new();

		public ConcurrentQueue<object> Scopes { get; } = new();

		public bool Disposed { get; private set; }

		public ILogger CreateLogger(string categoryName)
		{
			return new Line(this);
		}

		public void Dispose()
		{
			Disposed = true;
		}

		private sealed class Line(Recorder recorder) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull
			{
				recorder.Scopes.Enqueue(state);
				return null;
			}

			public bool IsEnabled(LogLevel logLevel)
			{
				return true;
			}

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
			{
				recorder.Lines.Enqueue(formatter(state, exception));
			}
		}
	}

	[Fact]
	public void AProviderAddedLater_GetsEveryLine_AndIsDisposedWithTheHost()
	{
		Recorder recorder = new();
		WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static _ => { });
		ILoggerFactory loggers = app.Services.GetRequiredService<ILoggerFactory>();

		loggers.AddProvider(recorder);
		loggers.CreateLogger("Memoria.Api.Tests.Probe").LogInformation("added later");
		app.Dispose();

		// The program's own thread disposes the services once its host has stopped, which can be just after Dispose returns.
		DateTime deadline = DateTime.UtcNow.AddSeconds(10);
		while (!recorder.Disposed && DateTime.UtcNow < deadline)
		{
			Thread.Sleep(10);
		}

		Assert.Contains("added later", recorder.Lines);
		Assert.True(recorder.Disposed);
	}

	[Fact]
	public void ACappedCategory_AllowsItsFloorAndAbove_AndKeepsScopes()
	{
		Recorder recorder = new();
		using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static _ => { });
		ILoggerFactory loggers = app.Services.GetRequiredService<ILoggerFactory>();
		loggers.AddProvider(recorder);

		ILogger framework = loggers.CreateLogger("Microsoft.AspNetCore.Routing.EndpointMiddleware");
		ILogger mcp = loggers.CreateLogger("ModelContextProtocol.Server.McpServer");
		using (framework.BeginScope("scope from the framework"))
		{
			framework.LogWarning("a framework warning");
		}

		Assert.True(framework.IsEnabled(LogLevel.Warning));
		Assert.False(framework.IsEnabled(LogLevel.Information));
		Assert.True(mcp.IsEnabled(LogLevel.Critical));
		Assert.False(mcp.IsEnabled(LogLevel.Error));
		Assert.Contains("scope from the framework", recorder.Scopes);
		Assert.Contains("a framework warning", recorder.Lines);
	}

	[Fact]
	public void TurningRequestDetailsOn_IsAnnouncedUnderTheApisHttpCategory()
	{
		factory.Logs.Clear();
		using WebApplicationFactory<Program> app = factory.WithWebHostBuilder(static builder => builder.UseSetting(RequestDetailLogging.Setting, "Enabled"));

		_ = app.Services;

		MemoriaApiFactory.CapturedLog warning = Assert.Single(factory.Logs, static log => log.EventId.Id == 1100);
		Assert.Equal(("Memoria.Api.Http", LogLevel.Warning), (warning.Category, warning.Level));
	}
}
