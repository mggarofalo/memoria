using Memoria.Api.Http;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Logging;

namespace Memoria.Api.Tests;

/// <summary>The one line each request leaves in the operator's log: its event id, level and route.</summary>
public sealed class RequestLogLineTests
{
	private readonly ListLogger<RequestLoggingMiddleware> _log = new();

	private sealed record Route(string Pattern) : IRouteDiagnosticsMetadata
	{
		string IRouteDiagnosticsMetadata.Route => Pattern;
	}

	private static Endpoint EndpointAt(string pattern)
	{
		return new Endpoint(null, new EndpointMetadataCollection(new Route(pattern)), pattern);
	}

	private async Task<MemoriaApiFactory.CapturedLog> LogOfAsync(string path, int status, Action<HttpContext>? arrange = null)
	{
		DefaultHttpContext context = new();
		context.Request.Method = "GET";
		context.Request.Path = path;
		arrange?.Invoke(context);
		RequestLoggingMiddleware middleware = new(inner =>
		{
			inner.Response.StatusCode = status;
			return Task.CompletedTask;
		}, _log);

		await middleware.InvokeAsync(context);

		return Assert.Single(_log.Entries);
	}

	[Fact]
	public async Task AServerError_IsLoggedAsAnError_FromExactly500()
	{
		MemoriaApiFactory.CapturedLog line = await LogOfAsync("/v1/search", 500);

		Assert.Equal((1002, LogLevel.Error), (line.EventId.Id, line.Level));
		Assert.Equal(500, line.State["StatusCode"]);
	}

	[Fact]
	public async Task AClientError_IsLoggedAtInformation()
	{
		MemoriaApiFactory.CapturedLog line = await LogOfAsync("/v1/search", 499);

		Assert.Equal((1000, LogLevel.Information), (line.EventId.Id, line.Level));
	}

	[Fact]
	public async Task ARequestNoRouteMatched_SaysSo_InsteadOfItsPath()
	{
		MemoriaApiFactory.CapturedLog line = await LogOfAsync("/v1/docs/secret/plans.md", 404);

		Assert.Equal("(no route)", line.State["Route"]);
		Assert.DoesNotContain("secret", line.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TheRouteIsThePattern_OfTheEndpointThatRan()
	{
		MemoriaApiFactory.CapturedLog line = await LogOfAsync("/v1/docs/secret/plans.md", 200, static context => context.SetEndpoint(EndpointAt("/v1/docs/{**id}")));

		Assert.Equal("/v1/docs/{**id}", line.State["Route"]);
	}

	/// <summary>The exception handler clears the endpoint and keeps it on its feature; while there is a current endpoint, that one is what ran.</summary>
	[Fact]
	public async Task TheCurrentEndpoint_WinsOverTheOneAFailureRecorded()
	{
		MemoriaApiFactory.CapturedLog failed = await LogOfAsync("/v1/search", 503, static context => context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature
		{
			Error = new InvalidOperationException(),
			Path = "/v1/search",
			Endpoint = EndpointAt("/v1/search"),
		}));
		_log.Entries.Clear();
		MemoriaApiFactory.CapturedLog both = await LogOfAsync("/v1/status", 200, static context =>
		{
			context.SetEndpoint(EndpointAt("/v1/status"));
			context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature
			{
				Error = new InvalidOperationException(),
				Path = "/v1/search",
				Endpoint = EndpointAt("/v1/search"),
			});
		});

		Assert.Equal("/v1/search", failed.State["Route"]);
		Assert.Equal("/v1/status", both.State["Route"]);
	}

	[Theory]
	[InlineData("/health")]
	[InlineData("/HEALTH")]
	[InlineData("/ready")]
	public async Task AnAnonymousProbe_IsLoggedAtDebug(string path)
	{
		MemoriaApiFactory.CapturedLog line = await LogOfAsync(path, 200);

		Assert.Equal((1001, LogLevel.Debug), (line.EventId.Id, line.Level));
	}

	[Theory]
	[InlineData("/healthz")]
	[InlineData("/")]
	public async Task AnythingElse_IsLoggedAtInformation(string path)
	{
		MemoriaApiFactory.CapturedLog line = await LogOfAsync(path, 200);

		Assert.Equal((1000, LogLevel.Information), (line.EventId.Id, line.Level));
	}
}
