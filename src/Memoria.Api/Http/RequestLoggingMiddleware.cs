using System.Diagnostics;
using Memoria.Api.Auth;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Metadata;

namespace Memoria.Api.Http;

/// <summary>
/// One structured log line per request and an <c>X-Trace-Id</c> response
/// header so a client can quote the id that ties the API's line to the
/// sidecar's. Registered outermost so it observes the final status code,
/// including the one the exception handler wrote. Probes log at Debug to
/// keep the stream readable. It logs the route template and the public key id,
/// and never the query string, request headers or any part of a key's secret.
/// </summary>
public sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
	public const string TraceIdHeader = "X-Trace-Id";

	public async Task InvokeAsync(HttpContext context)
	{
		long started = Stopwatch.GetTimestamp();
		string traceId = CurrentTraceId(context);

		context.Response.OnStarting(static state =>
		{
			(HttpContext ctx, string id) = ((HttpContext, string))state;
			ctx.Response.Headers[TraceIdHeader] = id;
			return Task.CompletedTask;
		}, (context, traceId));

		try
		{
			await next(context);
		}
		finally
		{
			double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			int status = context.Response.StatusCode;
			string route = RouteOf(context);
			string method = context.Request.Method;
			string? remote = context.Connection.RemoteIpAddress?.ToString();
			string? keyId = context.User.FindFirst(MemoriaClaims.KeyId)?.Value;

			if (status >= 500)
			{
				LogServerError(logger, method, route, status, elapsedMs, traceId, remote, keyId);
			}
			else if (IsProbe(context.Request.Path))
			{
				LogProbe(logger, method, route, status, elapsedMs, traceId, remote, keyId);
			}
			else
			{
				LogRequest(logger, method, route, status, elapsedMs, traceId, remote, keyId);
			}
		}
	}

	/// <summary>The W3C trace id of the current request, falling back to Kestrel's connection-scoped identifier.</summary>
	public static string CurrentTraceId(HttpContext context)
	{
		return Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
	}

	/// <summary>The pattern, never the raw path, which can carry a document path. The exception handler clears the endpoint, so a failed request has it on the feature.</summary>
	private static string RouteOf(HttpContext context)
	{
		Endpoint? endpoint = context.GetEndpoint() ?? context.Features.Get<IExceptionHandlerFeature>()?.Endpoint;
		return endpoint?.Metadata.GetMetadata<IRouteDiagnosticsMetadata>()?.Route ?? "(no route)";
	}

	private static bool IsProbe(PathString path)
	{
		return path.Equals("/health", StringComparison.OrdinalIgnoreCase) || path.Equals("/ready", StringComparison.OrdinalIgnoreCase);
	}

	[LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "HTTP {Method} {Route} -> {StatusCode} in {ElapsedMs:0.0} ms (trace {TraceId}, from {RemoteIp}, key {KeyId})")]
	private static partial void LogRequest(ILogger logger, string method, string route, int statusCode, double elapsedMs, string traceId, string? remoteIp, string? keyId);

	[LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "HTTP {Method} {Route} -> {StatusCode} in {ElapsedMs:0.0} ms (trace {TraceId}, from {RemoteIp}, key {KeyId})")]
	private static partial void LogProbe(ILogger logger, string method, string route, int statusCode, double elapsedMs, string traceId, string? remoteIp, string? keyId);

	[LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "HTTP {Method} {Route} -> {StatusCode} in {ElapsedMs:0.0} ms (trace {TraceId}, from {RemoteIp}, key {KeyId})")]
	private static partial void LogServerError(ILogger logger, string method, string route, int statusCode, double elapsedMs, string traceId, string? remoteIp, string? keyId);
}
