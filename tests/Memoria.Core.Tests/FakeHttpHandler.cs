using System.Net;
using System.Text;

namespace Memoria.Core.Tests;

/// <summary>Scripted <see cref="HttpMessageHandler"/>: records every request and answers from a queue.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
	private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

	public List<RecordedRequest> Requests { get; } = [];

	public FakeHttpHandler Respond(HttpStatusCode status, string? json)
	{
		_responders.Enqueue(_ => Json(status, json));
		return this;
	}

	public FakeHttpHandler Respond(Func<HttpRequestMessage, HttpResponseMessage> responder)
	{
		_responders.Enqueue(responder);
		return this;
	}

	public FakeHttpHandler Throw(Exception exception)
	{
		_responders.Enqueue(_ => throw exception);
		return this;
	}

	public static HttpResponseMessage Json(HttpStatusCode status, string? json)
	{
		HttpResponseMessage response = new(status);
		if (json is not null)
		{
			response.Content = new StringContent(json, Encoding.UTF8, "application/json");
		}

		return response;
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
		Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body, request.Headers));

		if (_responders.Count == 0)
		{
			throw new InvalidOperationException($"No scripted response for {request.Method} {request.RequestUri}");
		}

		return _responders.Dequeue()(request);
	}

	public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body, System.Net.Http.Headers.HttpRequestHeaders Headers);
}
