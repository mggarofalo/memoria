using System.Net;
using Memoria.Api.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Memoria.Api.Tests;

/// <summary>The warning about an unconfigured reverse proxy comes at most once an hour, and again as soon as the hour is up.</summary>
public sealed class ForwardedHeaderWarningTests
{
	private static DefaultHttpContext FromAProxy()
	{
		DefaultHttpContext context = new();
		context.Request.Headers["X-Forwarded-For"] = "203.0.113.5";
		context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
		return context;
	}

	[Fact]
	public async Task TheWarningRepeats_ExactlyWhenTheIntervalIsUp()
	{
		MovableClock clock = new();
		ListLogger<ForwardedHeaderWarning> log = new();
		ForwardedHeaderWarning warning = new(static _ => Task.CompletedTask, log, clock);
		int Warnings() => log.Entries.Count(static entry => entry.EventId.Id == 3003 && entry.Level == LogLevel.Warning);

		await warning.InvokeAsync(FromAProxy());
		clock.Advance(ForwardedHeaderWarning.Interval - TimeSpan.FromTicks(1));
		await warning.InvokeAsync(FromAProxy());
		Assert.Equal(1, Warnings());

		clock.Advance(TimeSpan.FromTicks(1));
		await warning.InvokeAsync(FromAProxy());

		Assert.Equal(2, Warnings());
		Assert.Equal("10.0.0.5", log.Entries.Last().State["PeerAddress"]);
	}
}
