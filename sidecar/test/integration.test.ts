/**
 * End-to-end against the real QMD SDK on a throwaway fixture index. Loads
 * better-sqlite3 and the SDK but no models: every search here is lex-only
 * with rerank=false, and ingest jobs run with embed=false.
 *
 * Opt in with MEMORIA_QMD_INTEGRATION=1 (CI sets it; `npm test` does not).
 */
import assert from "node:assert/strict";
import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { test } from "node:test";
import { buildApp } from "../src/app.ts";
import { openBackend } from "../src/store.ts";
import { FRONTMATTER_DOC } from "./fake-backend.ts";
import { authHeaders, tempDir, testConfig } from "./helpers.ts";

const enabled = process.env.MEMORIA_QMD_INTEGRATION === "1";

test("sidecar serves a real QMD index end to end", { skip: enabled ? false : "set MEMORIA_QMD_INTEGRATION=1" }, async () => {
  const root = await tempDir("memoria-qmd-it-");
  const corpus = path.join(root, "sessions");
  await mkdir(corpus, { recursive: true });
  const dbPath = path.join(root, "index.sqlite");

  const alpha = FRONTMATTER_DOC(
    { session_id: "alpha", date: "2026-02-11", start_time: "2026-02-11T04:47:57+00:00", project: "Receipts", git_branch: "master", tags: ["project/Receipts", "branch/master"] },
    "# Alpha\n\n## User\n\nHow should the AuthenticationHandler validate API keys?\n\n## Assistant\n\nCompare the bearer token in constant time.\n",
  );
  const beta = FRONTMATTER_DOC(
    { session_id: "beta", date: "2026-03-05", start_time: "2026-03-05T10:00:00+00:00", project: "Memoria", git_branch: "main", tags: ["project/Memoria", "branch/main"] },
    "# Beta\n\n## User\n\nDesign the AuthenticationHandler for Memoria.Api.\n\n## Assistant\n\nUse a scoped API key per collection.\n",
  );
  await writeFile(path.join(corpus, "2026-02-11_alpha.md"), alpha, "utf8");
  await writeFile(path.join(corpus, "2026-03-05_beta.md"), beta, "utf8");

  // Build the fixture index the way `qmd update` would, then close it so the
  // sidecar reopens it in DB-only mode exactly as it does on the desktop.
  const qmd = await import("@tobilu/qmd");
  const builder = await qmd.createStore({ dbPath, config: { collections: { sessions: { path: corpus, pattern: "**/*.md" } } } });
  const built = await builder.update();
  assert.equal(built.indexed, 2);
  await builder.close();

  const backend = await openBackend({ dbPath, keepWarm: false });
  const app = await buildApp({ backend, config: testConfig(), logger: false });
  try {
    await app.ready();

    const ready = (await app.inject({ method: "GET", url: "/ready", headers: authHeaders })).json();
    assert.equal(ready.index.totalDocuments, 2);
    assert.equal(ready.metadata.documents, 2);
    assert.equal(ready.models.resident, false);

    const collections = (await app.inject({ method: "GET", url: "/collections", headers: authHeaders })).json().collections;
    assert.equal(collections.length, 1);
    assert.equal(collections[0].name, "sessions");
    assert.equal(collections[0].documents, 2);

    const search = await app.inject({
      method: "POST",
      url: "/search",
      headers: authHeaders,
      payload: { searches: [{ type: "lex", query: "AuthenticationHandler" }], rerank: false, limit: 5 },
    });
    assert.equal(search.statusCode, 200, search.body);
    const hits = search.json();
    assert.equal(hits.total, 2);
    assert.ok(hits.results.every((r: { collection: string }) => r.collection === "sessions"));
    assert.ok(hits.results.every((r: { snippet: string }) => /AuthenticationHandler/.test(r.snippet)));
    assert.ok(hits.results.every((r: { line: number }) => r.line >= 1));
    assert.match(hits.results[0].docid, /^[0-9a-f]{6}$/);

    const filtered = await app.inject({
      method: "POST",
      url: "/search",
      headers: authHeaders,
      payload: { searches: [{ type: "lex", query: "AuthenticationHandler" }], rerank: false, filters: { project: "memoria", since: "2026-03-01" } },
    });
    const fb = filtered.json();
    assert.equal(fb.total, 1);
    assert.equal(fb.results[0].path, "2026-03-05_beta.md");
    assert.equal(fb.results[0].metadata.branch, "main");

    const byPath = await app.inject({ method: "GET", url: "/docs/sessions/2026-02-11_alpha.md:12:1", headers: authHeaders });
    assert.equal(byPath.statusCode, 200, byPath.body);
    const doc = byPath.json();
    assert.equal(doc.collection, "sessions");
    assert.equal(doc.path, "2026-02-11_alpha.md");
    assert.equal(doc.body, "# Alpha");
    assert.equal(doc.metadata.project, "Receipts");

    const byId = await app.inject({ method: "GET", url: `/docs/${doc.docid}?body=false`, headers: authHeaders });
    assert.equal(byId.statusCode, 200, byId.body);
    assert.equal(byId.json().file, "sessions/2026-02-11_alpha.md");

    const missing = await app.inject({ method: "GET", url: "/docs/sessions/nothing-here.md", headers: authHeaders });
    assert.equal(missing.statusCode, 404);
    assert.equal(missing.json().error, "not_found");

    const multi = await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { pattern: "sessions/2026-*.md", maxLines: 1 } });
    assert.equal(multi.statusCode, 200, multi.body);
    assert.deepEqual(multi.json().docs.map((d: { path: string }) => d.path).sort(), ["2026-02-11_alpha.md", "2026-03-05_beta.md"]);
    assert.equal(multi.json().docs[0].body, "---");

    // Ingest by value, then wait for the serialized update job to index it.
    const gamma = FRONTMATTER_DOC({ session_id: "gamma", date: "2026-10-31", project: "Memoria", git_branch: "feat/sidecar", tags: ["project/Memoria"] }, "# Gamma\n\nThe sidecar ingests transcripts by value.\n");
    const ingest = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "sessions", path: "2026-10-31_gamma.md", content: gamma, embed: false } });
    assert.equal(ingest.statusCode, 202, ingest.body);
    await app.ctx.jobs.idle();
    const job = (await app.inject({ method: "GET", url: `/reindex/jobs/${ingest.json().job.id}`, headers: authHeaders })).json().job;
    assert.equal(job.state, "succeeded", JSON.stringify(job));
    assert.equal(job.result.update.indexed, 1);
    assert.equal(job.result.embed, null);
    assert.equal(job.result.metadata.documents, 3);

    const after = await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { searches: [{ type: "lex", query: "ingests transcripts" }], rerank: false, filters: { branch: "feat/sidecar" } } });
    assert.equal(after.json().total, 1);
    assert.equal(after.json().results[0].path, "2026-10-31_gamma.md");

    const status = (await app.inject({ method: "GET", url: "/status", headers: authHeaders })).json();
    assert.equal(status.totalDocuments, 3);
  } finally {
    await app.close();
    await backend.close();
  }
});
