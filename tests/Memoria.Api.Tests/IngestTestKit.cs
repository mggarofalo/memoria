using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Memoria.Api.Tests;

/// <summary>
/// A sidecar with a memory: it keeps what is written so tests can read back exactly what would have been
/// stored, and records the order of writes, deletes and reindexes. Tests script failures per path.
/// </summary>
internal sealed class FakeCorpus
{
	private int _jobs;

	public FakeCorpus(FakeSidecar sidecar)
	{
		sidecar.Requests.Clear();
		sidecar.On("POST", "/documents", Write);
		sidecar.On("DELETE", "/documents", Delete);
		sidecar.OnPrefix("GET", "/collections/", (_, _) => FakeSidecar.Json(HttpStatusCode.OK, $$"""{"collection":"c","empty":{{(HoldsForeignFiles ? "false" : "true")}}}"""));
		sidecar.On("POST", "/reindex", (_, _) => FakeSidecar.Json(HttpStatusCode.Accepted, $$"""{"job":{{Job()}}}"""));

	}

	/// <summary>Collection-relative path to stored content.</summary>
	public ConcurrentDictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

	/// <summary>Every call in order: <c>write path</c>, <c>delete path</c>, <c>list</c>.</summary>
	public ConcurrentQueue<string> Calls { get; } = new();

	/// <summary>Says the directory has files in it, as it would for a collection someone else filled.</summary>
	public bool HoldsForeignFiles { get; set; }

	/// <summary>Answers a write with something else: return null to let it through.</summary>
	public Func<string, HttpResponseMessage?> OnWrite { get; set; } = static _ => null;

	public Func<string, HttpResponseMessage?> OnDelete { get; set; } = static _ => null;


	private HttpResponseMessage Write(HttpRequestMessage request, string? body)
	{
		using JsonDocument parsed = JsonDocument.Parse(body!);
		string path = parsed.RootElement.GetProperty("path").GetString()!;
		string collection = parsed.RootElement.GetProperty("collection").GetString()!;
		Calls.Enqueue("write " + path);
		if (OnWrite(path) is { } scripted)
		{
			return scripted;
		}

		string content = parsed.RootElement.GetProperty("content").GetString()!;
		bool created = !Files.ContainsKey(path);
		Files[path] = content;
		return FakeSidecar.Json(HttpStatusCode.Accepted, $$"""
			{"collection":"{{collection}}","path":{{JsonSerializer.Serialize(path)}},"file":{{JsonSerializer.Serialize(collection + "/" + path)}},"bytes":{{Encoding.UTF8.GetByteCount(content)}},"created":{{(created ? "true" : "false")}},"metadata":null,"job":{{Job()}}}
			""");
	}

	private HttpResponseMessage Delete(HttpRequestMessage request, string? body)
	{
		var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
		string path = query["path"]!;
		string collection = query["collection"]!;
		Calls.Enqueue("delete " + path);
		if (OnDelete(path) is { } scripted)
		{
			return scripted;
		}

		bool deleted = Files.TryRemove(path, out _);
		return FakeSidecar.Json(HttpStatusCode.Accepted, $$"""
			{"collection":"{{collection}}","path":{{JsonSerializer.Serialize(path)}},"file":{{JsonSerializer.Serialize(collection + "/" + path)}},"deleted":{{(deleted ? "true" : "false")}},"job":{{Job()}}}
			""");
	}

	private string Job()
	{
		int id = Interlocked.Increment(ref _jobs);
		return $$"""{"id":"job-{{id}}","state":"queued","request":{"collections":["c"],"embed":true,"force":false,"reason":"t"},"createdAt":"2026-01-01T00:00:00.000Z","startedAt":null,"finishedAt":null,"result":null,"error":null}""";
	}
}

internal static class IngestKit
{
	public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	public static readonly DateTimeOffset Stamp = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

	/// <summary>A credential the redactor recognizes, built at runtime so no scanner mistakes this file for a leak.</summary>
	public static string Token(int seed = 1)
	{
		const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
		StringBuilder body = new();
		for (int i = 0; i < 36; i++)
		{
			body.Append(Alphabet[((i * 7) + (seed * 13) + (i * i)) % Alphabet.Length]);
		}

		return "gh" + "p_" + body;
	}

	public static Task<HttpResponseMessage> PostDocumentAsync(
		HttpClient client,
		string collection = "c",
		string source = "notes",
		string sourceId = "one",
		string body = "hello",
		DateTimeOffset? updated = null,
		string? title = null)
	{
		return client.PostAsJsonAsync("/v1/documents", new { collection, source, sourceId, body, updated = updated ?? Stamp, title });
	}

	public static Task<HttpResponseMessage> PostSessionAsync(
		HttpClient client,
		string jsonl,
		string collection = "c",
		string format = "claude",
		string sessionId = "11111111-2222-3333-4444-555555555555",
		DateTimeOffset? updated = null,
		bool allowShrink = false,
		string? projectDirectory = null,
		bool restoreDeleted = false)
	{
		string query = $"/v1/ingest/session?collection={Uri.EscapeDataString(collection)}&format={format}&sessionId={Uri.EscapeDataString(sessionId)}&updated={Uri.EscapeDataString((updated ?? Stamp).ToString("O"))}{(allowShrink ? "&allowShrink=true" : "")}{(projectDirectory is null ? "" : "&projectDirectory=" + Uri.EscapeDataString(projectDirectory))}{(restoreDeleted ? "&restoreDeleted=true" : "")}";
		return client.PostAsync(query, new StringContent(jsonl, Encoding.UTF8, "application/x-ndjson"));
	}

	/// <summary>One Claude Code line. Text this long fills a part by itself, so <paramref name="messages"/> gives as many parts.</summary>
	public static string Transcript(int messages, char fill = 'w', string day = "2026-01-15")
	{
		StringBuilder lines = new();
		string text = string.Join(' ', Enumerable.Repeat(new string(fill, 60), 1200));
		for (int i = 0; i < messages; i++)
		{
			lines.Append(ClaudeLine(i % 2 == 0 ? "user" : "assistant", text, i, day));
		}

		return lines.ToString();
	}

	public static string ClaudeLine(string role, string text, int minute = 0, string day = "2026-01-15", string cwd = "/srv/demo")
	{
		string message = JsonSerializer.Serialize(new { role, content = text });
		return $$"""{"type":"{{role}}","timestamp":"{{day}}T10:{{minute % 60:D2}}:00.000Z","cwd":"{{cwd}}","message":{{message}}}""" + "\n";
	}

	public static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
	{
		return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
	}

	public static string[] States(JsonElement result)
	{
		return [.. result.GetProperty("parts").EnumerateArray().Select(static part => part.GetProperty("state").GetString()!)];
	}

	public static HttpResponseMessage Busy(int? retryAfterSeconds = null)
	{
		HttpResponseMessage response = FakeSidecar.Json(HttpStatusCode.ServiceUnavailable, """{"error":"busy","message":"Too much is running."}""");
		if (retryAfterSeconds is { } seconds)
		{
			response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
		}

		return response;
	}

	public static HttpResponseMessage TooLarge()
	{
		return FakeSidecar.Json(HttpStatusCode.RequestEntityTooLarge, """{"error":"document_too_large","message":"Over the document cap."}""");
	}
}
