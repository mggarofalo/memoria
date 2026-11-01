using System.Diagnostics;

namespace Memoria.Api.Sidecar;

/// <summary>
/// Stamps every sidecar call with the current trace id as <c>request-id</c>.
/// The sidecar's Fastify instance adopts that header as its <c>reqId</c>, so
/// one trace id joins the API's request log line, the sidecar's pino lines
/// and the <c>X-Trace-Id</c> header the client received. The W3C
/// <c>traceparent</c> header is injected too, so the sidecar (or anything
/// in between) can join the same distributed trace.
/// </summary>
public sealed class SidecarCorrelationHandler : DelegatingHandler
{
	public const string HeaderName = "request-id";

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		if (Activity.Current is { } activity)
		{
			if (!request.Headers.Contains(HeaderName))
			{
				request.Headers.TryAddWithoutValidation(HeaderName, activity.TraceId.ToString());
			}

			// SocketsHttpHandler injects traceparent itself, but only when it is the
			// primary handler; inject here so the contract holds whatever sits below.
			if (!request.Headers.Contains("traceparent"))
			{
				DistributedContextPropagator.Current.Inject(activity, request, static (carrier, name, value) =>
				{
					if (carrier is HttpRequestMessage message && !message.Headers.Contains(name))
					{
						message.Headers.TryAddWithoutValidation(name, value);
					}
				});
			}
		}

		return base.SendAsync(request, cancellationToken);
	}
}
