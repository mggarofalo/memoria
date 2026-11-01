namespace Memoria.Core.Tests;

/// <summary>Sidecar response bodies shaped exactly as sidecar/openapi.json describes them.</summary>
public static class Fixtures
{
	public const string SearchResponse = """
		{
		  "results": [
		    {
		      "docid": "f6feba",
		      "file": "claude-sessions/2026-02-11_2e39c784.md",
		      "collection": "claude-sessions",
		      "path": "2026-02-11_2e39c784.md",
		      "title": "Receipts: API key auth",
		      "score": 0.91,
		      "context": null,
		      "line": 42,
		      "snippet": "...AuthenticationHandler...",
		      "metadata": { "project": "Receipts", "branch": "master", "date": "2026-02-11", "startTime": "2026-02-11T10:00:00Z", "tags": ["project/Receipts", "branch/master"] }
		    }
		  ],
		  "total": 1,
		  "tookMs": 3700,
		  "query": { "collections": ["claude-sessions"], "rerank": false, "filtered": true, "fetched": 20, "limit": 5 }
		}
		""";

	public const string Document = """
		{
		  "docid": "f6feba",
		  "file": "claude-sessions/2026-02-11_2e39c784.md",
		  "collection": "claude-sessions",
		  "path": "2026-02-11_2e39c784.md",
		  "title": "Receipts: API key auth",
		  "context": null,
		  "modifiedAt": "2026-02-11T12:00:00.000Z",
		  "bodyLength": 5120,
		  "fromLine": 40,
		  "maxLines": 5,
		  "body": "line 40\nline 41\nline 42\nline 43\nline 44",
		  "metadata": { "project": "Receipts", "tags": [] }
		}
		""";

	public const string Job = """
		{
		  "id": "job-1",
		  "state": "queued",
		  "request": { "collections": ["claude-sessions"], "embed": true, "force": false, "reason": "reindex" },
		  "createdAt": "2026-11-01T10:00:00.000Z",
		  "startedAt": null,
		  "finishedAt": null,
		  "result": null,
		  "error": null
		}
		""";

	public static string Readiness(string status, bool resident)
	{
		string flag = resident ? "true" : "false";
		return $$"""
			{
			  "status": "{{status}}",
			  "version": "0.1.0",
			  "uptimeSeconds": 120,
			  "index": { "dbPath": "C:\\Users\\x\\.cache\\qmd\\index.sqlite", "totalDocuments": 1502, "needsEmbedding": 0, "hasVectorIndex": true },
			  "models": { "embed": "embed.gguf", "rerank": "rerank.gguf", "generate": "gen.gguf", "loaded": { "embed": {{flag}}, "rerank": {{flag}}, "generate": {{flag}} }, "resident": {{flag}} },
			  "warmup": { "requested": true, "done": {{flag}}, "error": null, "durationMs": {{(resident ? "11000" : "null")}} },
			  "metadata": { "documents": 1501, "refreshedAt": "2026-11-01T10:00:00.000Z" },
			  "jobs": { "pending": 0 }
			}
			""";
	}
}
