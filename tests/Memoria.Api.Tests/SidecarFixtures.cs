namespace Memoria.Api.Tests;

/// <summary>Sidecar bodies shaped as sidecar/openapi.json describes them.</summary>
public static class SidecarFixtures
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
		      "metadata": { "project": "Receipts", "branch": "master", "date": "2026-02-11", "tags": ["project/Receipts"] }
		    },
		    {
		      "docid": "a1b2c3",
		      "file": "codex-sessions/2026-03-01_x.md",
		      "collection": "codex-sessions",
		      "path": "2026-03-01_x.md",
		      "title": "Codex: auth",
		      "score": 0.4,
		      "context": "codex transcripts",
		      "line": 1,
		      "snippet": "...",
		      "metadata": null
		    }
		  ],
		  "total": 2,
		  "tookMs": 3700,
		  "query": { "collections": ["claude-sessions", "codex-sessions"], "rerank": true, "filtered": false, "fetched": 40, "limit": 10 }
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
		  "fromLine": null,
		  "maxLines": null,
		  "body": "# Receipts\n\nhello",
		  "metadata": { "project": "Receipts", "tags": [] }
		}
		""";

	public const string MultiGet = """
		{
		  "docs": [
		    { "docid": "f6feba", "file": "claude-sessions/a.md", "collection": "claude-sessions", "path": "a.md", "title": "A", "context": null, "modifiedAt": "2026-02-11T12:00:00.000Z", "bodyLength": 10, "body": "a", "metadata": null },
		    { "docid": "a1b2c3", "file": "codex-sessions/b.md", "collection": "codex-sessions", "path": "b.md", "title": "B", "context": null, "modifiedAt": "2026-02-11T12:00:00.000Z", "bodyLength": 10, "body": "b", "metadata": null }
		  ],
		  "skipped": [{ "file": "claude-sessions/huge.md", "reason": "exceeds maxBytes" }],
		  "errors": []
		}
		""";

	public const string Collections = """
		{
		  "collections": [
		    { "name": "claude-sessions", "path": "C:\\corpus\\claude", "pattern": "**/*.md", "documents": 1200, "totalDocuments": 1250, "lastModified": "2026-11-01T01:00:00.000Z", "includeByDefault": true, "context": "Claude Code transcripts" },
		    { "name": "codex-sessions", "path": "C:\\corpus\\codex", "pattern": "**/*.md", "documents": 302, "totalDocuments": 302, "lastModified": null, "includeByDefault": true, "context": null }
		  ]
		}
		""";

	public const string Status = """
		{
		  "totalDocuments": 1502,
		  "needsEmbedding": 3,
		  "hasVectorIndex": true,
		  "collections": [
		    { "name": "claude-sessions", "path": "C:\\corpus\\claude", "pattern": "**/*.md", "documents": 1200, "lastUpdated": "2026-11-01T01:00:00.000Z" },
		    { "name": "codex-sessions", "path": null, "pattern": null, "documents": 302, "lastUpdated": "2026-10-30T01:00:00.000Z" }
		  ]
		}
		""";

	public static string Readiness(string status, bool resident, int needsEmbedding = 0, string? warmupError = null)
	{
		string flag = resident ? "true" : "false";
		string error = warmupError is null ? "null" : $"\"{warmupError}\"";
		return $$"""
			{
			  "status": "{{status}}",
			  "version": "0.1.0",
			  "uptimeSeconds": 120,
			  "index": { "dbPath": "/index.sqlite", "totalDocuments": 1502, "needsEmbedding": {{needsEmbedding}}, "hasVectorIndex": true },
			  "models": { "embed": "embed.gguf", "rerank": "rerank.gguf", "generate": "gen.gguf", "loaded": { "embed": {{flag}}, "rerank": {{flag}}, "generate": {{flag}} }, "resident": {{flag}} },
			  "warmup": { "requested": true, "done": {{flag}}, "error": {{error}}, "durationMs": null },
			  "metadata": { "documents": 1501, "refreshedAt": null },
			  "jobs": { "pending": 0 }
			}
			""";
	}
}
