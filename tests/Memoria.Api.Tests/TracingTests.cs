using System.Net;
using Memoria.Api.Http;
using Memoria.Api.Sidecar;
using Microsoft.Extensions.Logging;

namespace Memoria.Api.Tests;

public sealed class TracingTests(MemoriaApiFactory factory) : IClassFixture<MemoriaApiFactory>
{
	[Fact]
	public async Task Response_CarriesTraceIdThatSidecarCallAlsoCarried()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/v1/collections");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string traceId = Assert.Single(response.Headers.GetValues(RequestLoggingMiddleware.TraceIdHeader));
		Assert.Equal(32, traceId.Length);

		FakeSidecar.RecordedRequest sent = factory.Sidecar.Single();
		Assert.Equal(traceId, sent.Headers[SidecarCorrelationHandler.HeaderName]);
		Assert.True(sent.Headers.TryGetValue("traceparent", out string? traceparent), "W3C traceparent should be propagated to the sidecar");
		Assert.Contains(traceId, traceparent);
		Assert.StartsWith("Memoria.Api/", sent.Headers["User-Agent"]);
	}

	[Fact]
	public async Task IncomingTraceparent_IsContinuedNotReplaced()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		HttpClient client = factory.CreateClient();
		const string incomingTrace = "4bf92f3577b34da6a3ce929d0e0e4736";
		client.DefaultRequestHeaders.Add("traceparent", $"00-{incomingTrace}-00f067aa0ba902b7-01");

		HttpResponseMessage response = await client.GetAsync("/v1/collections");

		Assert.Equal(incomingTrace, Assert.Single(response.Headers.GetValues(RequestLoggingMiddleware.TraceIdHeader)));
		Assert.Equal(incomingTrace, factory.Sidecar.Single().Headers[SidecarCorrelationHandler.HeaderName]);
	}

	[Fact]
	public async Task EveryRequest_EmitsOneStructuredLogLine()
	{
		factory.Sidecar.On("GET", "/collections", HttpStatusCode.OK, SidecarFixtures.Collections);
		factory.Logs.Clear();
		HttpClient client = factory.CreateClient();

		HttpResponseMessage response = await client.GetAsync("/v1/collections");
		string traceId = Assert.Single(response.Headers.GetValues(RequestLoggingMiddleware.TraceIdHeader));

		MemoriaApiFactory.CapturedLog line = Assert.Single(factory.Logs, l => l.Category == typeof(RequestLoggingMiddleware).FullName && (string?)l.State.GetValueOrDefault("TraceId") == traceId);
		Assert.Equal(LogLevel.Information, line.Level);
		Assert.Equal("GET", line.State["Method"]);
		Assert.Equal("/v1/collections", line.State["Route"]);
		Assert.Equal(200, line.State["StatusCode"]);
		Assert.IsType<double>(line.State["ElapsedMs"]);
		Assert.Contains("HTTP GET /v1/collections -> 200", line.Message);
	}

	[Fact]
	public async Task Probes_LogAtDebug_AndServerErrorsAtError()
	{
		factory.Sidecar.Requests.Clear();
		factory.Sidecar.On("GET", "/ready", HttpStatusCode.OK, SidecarFixtures.Readiness("ready", resident: true));
		factory.Sidecar.On("GET", "/status", HttpStatusCode.InternalServerError, """{"error":"internal","message":"boom"}""");
		factory.Logs.Clear();
		HttpClient client = factory.CreateClient();

		await client.GetAsync("/ready");
		await client.GetAsync("/v1/status");

		List<MemoriaApiFactory.CapturedLog> lines = factory.Logs.Where(l => l.Category == typeof(RequestLoggingMiddleware).FullName).ToList();
		Assert.Contains(lines, l => l.Level == LogLevel.Debug && (string?)l.State["Route"] == "/ready");
		Assert.Contains(lines, l => l.Level == LogLevel.Error && (string?)l.State["Route"] == "/v1/status" && (int)l.State["StatusCode"]! == 502);
	}

	[Fact]
	public async Task RouteTemplate_IsLoggedInsteadOfRawPath()
	{
		factory.Sidecar.On("GET", "/docs/claude-sessions/x.md", HttpStatusCode.OK, SidecarFixtures.Document);
		factory.Logs.Clear();
		HttpClient client = factory.CreateClient();

		await client.GetAsync("/v1/docs/claude-sessions/x.md");

		MemoriaApiFactory.CapturedLog line = Assert.Single(factory.Logs, l => l.Category == typeof(RequestLoggingMiddleware).FullName);
		Assert.Equal("/v1/docs/{**id}", line.State["Route"]);
	}
}
