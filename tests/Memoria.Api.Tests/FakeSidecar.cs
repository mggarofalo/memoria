using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Memoria.Api.Tests;

/// <summary>
/// Stands in for the memoria-qmd sidecar at the <see cref="HttpMessageHandler"/>
/// level. Tests script responses per route and inspect what the API sent.
/// </summary>
public sealed class FakeSidecar : HttpMessageHandler
{
	private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, string?, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

	private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, string?, HttpResponseMessage>> _prefixes = new(StringComparer.Ordinal);

	public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

	/// <summary>For each answered request, whether the caller had cancelled it by the time the answer was ready.</summary>
	public ConcurrentQueue<bool> CancelledBeforeAnswer { get; } = new();

	/// <summary>Scripts a JSON response for <c>METHOD /path</c> (path without query string).</summary>
	public FakeSidecar On(string method, string path, HttpStatusCode status, string json)
	{
		_routes.TryRemove("*", out _);
		_routes[Key(method, path)] = (_, _) => Json(status, json);
		return this;
	}

	/// <summary>Scripts an arbitrary responder; the second argument is the request body.</summary>
	public FakeSidecar On(string method, string path, Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
	{
		_routes.TryRemove("*", out _);
		_routes[Key(method, path)] = responder;
		return this;
	}

	/// <summary>Scripts a responder for every <c>METHOD /path/...</c> with no exact route.</summary>
	public FakeSidecar OnPrefix(string method, string prefix, Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
	{
		_prefixes[Key(method, prefix)] = responder;
		return this;
	}

	/// <summary>Makes every request fail at the transport level, as when the sidecar host is down.</summary>
	public FakeSidecar Unreachable()
	{
		_routes.Clear();
		_routes["*"] = (_, _) => throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
		return this;
	}

	/// <summary>Makes every request hang until the caller's token is cancelled, as when the sidecar is wedged.</summary>
	public FakeSidecar Hanging()
	{
		_routes.Clear();
		_routes["*"] = (_, _) => throw new HangException();
		return this;
	}

	public RecordedRequest Single()
	{
		return Assert.Single(Requests);
	}

	public RecordedRequest Last()
	{
		Assert.NotEmpty(Requests);
		return Requests.Last();
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
		Dictionary<string, string> headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
		Requests.Enqueue(new RecordedRequest(request.Method.Method, request.RequestUri!, body, headers));

		if (_routes.TryGetValue("*", out Func<HttpRequestMessage, string?, HttpResponseMessage>? any))
		{
			try
			{
				return any(request, body);
			}
			catch (HangException)
			{
				await Task.Delay(Timeout.Infinite, cancellationToken);
				throw;
			}
		}

		if (_routes.TryGetValue(Key(request.Method.Method, request.RequestUri!.AbsolutePath), out Func<HttpRequestMessage, string?, HttpResponseMessage>? responder))
		{
			HttpResponseMessage answer = responder(request, body);
			CancelledBeforeAnswer.Enqueue(cancellationToken.IsCancellationRequested);
			return answer;
		}

		string key = Key(request.Method.Method, request.RequestUri!.AbsolutePath);
		foreach ((string prefix, Func<HttpRequestMessage, string?, HttpResponseMessage> match) in _prefixes)
		{
			if (key.StartsWith(prefix, StringComparison.Ordinal))
			{
				return match(request, body);
			}
		}

		return Json(HttpStatusCode.NotFound, $$"""{"error":"not_found","message":"fake sidecar has no route {{request.Method}} {{request.RequestUri.AbsolutePath}}"}""");
	}

	public static HttpResponseMessage Json(HttpStatusCode status, string json)
	{
		return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
	}

	private static string Key(string method, string path)
	{
		return method.ToUpperInvariant() + " " + path;
	}

	public sealed record RecordedRequest(string Method, Uri Uri, string? Body, IReadOnlyDictionary<string, string> Headers);

	private sealed class HangException : Exception;
}
