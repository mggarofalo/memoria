import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { test } from "node:test";
import { authHeaders, makeApp, sampleDocs, TEST_SECRET, testConfig } from "./helpers.ts";
import { FRONTMATTER_DOC } from "./fake-backend.ts";
import { buildApp } from "../src/app.ts";
import { FakeBackend } from "./fake-backend.ts";

// --- auth ----------------------------------------------------------------------

test("every route except /health and /openapi.json requires the bearer secret", async () => {
  const { app } = await makeApp();
  try {
    for (const url of ["/ready", "/status", "/collections", "/docs/claude-sessions/2026-02-11_alpha.md", "/reindex/jobs"]) {
      const r = await app.inject({ method: "GET", url });
      assert.equal(r.statusCode, 401, url);
      assert.equal(r.headers["www-authenticate"], 'Bearer realm="memoria-qmd"');
      assert.deepEqual(r.json(), { error: "unauthorized", message: "Missing or invalid bearer token" });
    }
    for (const url of ["/search", "/docs:multiGet", "/documents", "/reindex"]) {
      const r = await app.inject({ method: "POST", url, payload: {} });
      assert.equal(r.statusCode, 401, url);
    }
    const wrong = await app.inject({ method: "GET", url: "/status", headers: { authorization: `Bearer ${TEST_SECRET}x` } });
    assert.equal(wrong.statusCode, 401);
    const health = await app.inject({ method: "GET", url: "/health" });
    assert.equal(health.statusCode, 200);
    const spec = await app.inject({ method: "GET", url: "/openapi.json" });
    assert.equal(spec.statusCode, 200);
    const ok = await app.inject({ method: "GET", url: "/status", headers: authHeaders });
    assert.equal(ok.statusCode, 200);
  } finally {
    await app.close();
  }
});

test("without a configured secret on loopback, routes are open", async () => {
  const backend = new FakeBackend({ collections: [], docs: [] });
  const app = await buildApp({ backend, config: testConfig({ secret: undefined }), logger: false });
  try {
    const r = await app.inject({ method: "GET", url: "/collections" });
    assert.equal(r.statusCode, 200);
  } finally {
    await app.close();
  }
});

// --- system --------------------------------------------------------------------

test("/health, /ready and /status report process, model and index state", async () => {
  const { app, backend } = await makeApp({ modelsResident: false });
  try {
    const health = (await app.inject({ method: "GET", url: "/health" })).json();
    assert.equal(health.status, "ok");
    assert.equal(typeof health.uptimeSeconds, "number");

    let ready = await app.inject({ method: "GET", url: "/ready", headers: authHeaders });
    assert.equal(ready.statusCode, 200);
    let body = ready.json();
    assert.equal(body.status, "warming");
    assert.equal(body.models.resident, false);
    assert.equal(body.index.totalDocuments, 4);
    assert.equal(body.index.dbPath, "/fake/index.sqlite");
    assert.equal(body.metadata.documents, 3);
    assert.equal(body.warmup.requested, false);
    assert.equal(body.jobs.pending, 0);

    ready = await app.inject({ method: "GET", url: "/ready?requireModels=true", headers: authHeaders });
    assert.equal(ready.statusCode, 503);

    backend.modelsResident = true;
    ready = await app.inject({ method: "GET", url: "/ready?requireModels=true", headers: authHeaders });
    assert.equal(ready.statusCode, 200);
    body = ready.json();
    assert.equal(body.status, "ready");
    assert.equal(body.models.resident, true);

    const status = (await app.inject({ method: "GET", url: "/status", headers: authHeaders })).json();
    assert.equal(status.totalDocuments, 4);
    assert.equal(status.collections.length, 2);
  } finally {
    await app.close();
  }
});

test("unknown routes return the error envelope", async () => {
  const { app } = await makeApp();
  try {
    const r = await app.inject({ method: "GET", url: "/nope", headers: authHeaders });
    assert.equal(r.statusCode, 404);
    assert.equal(r.json().error, "no_route");
  } finally {
    await app.close();
  }
});

// --- search --------------------------------------------------------------------

test("POST /search with typed sub-queries returns ranked results with snippets and metadata", async () => {
  const { app, backend } = await makeApp();
  try {
    const r = await app.inject({
      method: "POST",
      url: "/search",
      headers: authHeaders,
      payload: { searches: [{ type: "lex", query: "AuthenticationHandler" }, { type: "vec", query: "api key auth" }], intent: "asp.net core auth", limit: 5 },
    });
    assert.equal(r.statusCode, 200, r.body);
    const body = r.json();
    assert.equal(body.total, 2);
    assert.equal(body.results.length, 2);
    assert.deepEqual(body.query, { collections: ["claude-sessions", "codex-sessions"], rerank: true, filtered: false, fetched: 2, limit: 5 });
    const first = body.results[0];
    assert.equal(first.collection, "claude-sessions");
    assert.equal(first.file, `claude-sessions/${first.path}`);
    assert.match(first.docid, /^[0-9a-f]{6}$/);
    assert.equal(typeof first.score, "number");
    assert.ok(first.line >= 1);
    assert.match(first.snippet, /AuthenticationHandler/);
    assert.equal(first.metadata.project, "Receipts");
    assert.deepEqual(first.metadata.tags, ["project/Receipts", "branch/master"]);

    const call = backend.calls.find((c) => c.method === "search")!;
    const opts = call.args[0] as Record<string, unknown>;
    assert.deepEqual(opts.queries, [{ type: "lex", query: "AuthenticationHandler" }, { type: "vec", query: "api key auth" }]);
    assert.equal(opts.intent, "asp.net core auth");
    assert.equal(opts.rerank, true);
    assert.equal(opts.limit, 5);
    assert.equal(opts.candidateLimit, undefined);
  } finally {
    await app.close();
  }
});

test("POST /search with a plain query passes it through and honours rerank=false, minScore and collections", async () => {
  const { app, backend } = await makeApp();
  try {
    const r = await app.inject({
      method: "POST",
      url: "/search",
      headers: authHeaders,
      payload: { query: "api keys", collections: ["codex-sessions"], rerank: false, minScore: 0.5, limit: 3 },
    });
    assert.equal(r.statusCode, 200, r.body);
    const body = r.json();
    assert.equal(body.total, 1);
    assert.equal(body.results[0].collection, "codex-sessions");
    assert.equal(body.query.rerank, false);
    const opts = backend.calls.find((c) => c.method === "search")!.args[0] as Record<string, unknown>;
    assert.equal(opts.query, "api keys");
    assert.equal(opts.rerank, false);
    assert.equal(opts.minScore, 0.5);
    assert.deepEqual(opts.collections, ["codex-sessions"]);
  } finally {
    await app.close();
  }
});

test("POST /search validates its body", async () => {
  const { app } = await makeApp();
  try {
    const cases: Array<[unknown, RegExp]> = [
      [{}, /exactly one/],
      [{ query: "x", searches: [{ type: "lex", query: "y" }] }, /exactly one/],
      [{ searches: [] }, /searches/],
      [{ searches: [{ type: "nope", query: "y" }] }, /type/],
      [{ query: "x", limit: 0 }, /limit/],
      [{ query: "x", limit: 101 }, /limit/],
      [{ query: "x", minScore: 2 }, /minScore/],
      [{ query: "x", bogus: true }, /additional/],
      [{ query: "x", filters: { since: "not a date" } }, /Invalid since/],
      [{ query: "x", collections: ["ghost"] }, /Unknown collection/],
    ];
    for (const [payload, re] of cases) {
      const r = await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: payload as object });
      assert.equal(r.statusCode, 400, JSON.stringify(payload));
      assert.match(r.json().message, re, JSON.stringify(payload));
    }
  } finally {
    await app.close();
  }
});

test("POST /search applies frontmatter filters after over-fetching", async () => {
  const { app, backend } = await makeApp();
  try {
    const r = await app.inject({
      method: "POST",
      url: "/search",
      headers: authHeaders,
      payload: { searches: [{ type: "lex", query: "api keys" }], filters: { project: "receipts", since: "2026-03-01" }, limit: 2 },
    });
    assert.equal(r.statusCode, 200, r.body);
    const body = r.json();
    assert.equal(body.total, 1);
    assert.equal(body.results[0].path, "2026-08-31_gamma.md");
    assert.equal(body.query.filtered, true);
    // Three docs mention "api keys": alpha (Feb, Receipts), gamma (Aug, receipts), no-frontmatter.
    assert.equal(body.query.fetched, 3);
    const opts = backend.calls.find((c) => c.method === "search")!.args[0] as Record<string, unknown>;
    assert.equal(opts.limit, 8); // limit * filterOverfetch (4)

    const branch = await app.inject({
      method: "POST",
      url: "/search",
      headers: authHeaders,
      payload: { query: "AuthenticationHandler", filters: { branch: ["main"], tags: ["project/memoria"] } },
    });
    assert.equal(branch.json().results.map((x: { path: string }) => x.path).join(), "2026-03-05_beta.md");

    const none = await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { query: "api keys", filters: { until: "2025-12-31" } } });
    assert.equal(none.json().total, 0);
  } finally {
    await app.close();
  }
});

test("backend failures surface as 500 without leaking internals", async () => {
  const { app, backend } = await makeApp();
  try {
    backend.searchError = new Error("sqlite exploded at C:/secret/path");
    const r = await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { query: "x" } });
    assert.equal(r.statusCode, 500);
    assert.deepEqual(r.json(), { error: "internal", message: "Internal error; see sidecar logs" });
  } finally {
    await app.close();
  }
});

// --- docs ----------------------------------------------------------------------

test("GET /docs/{id} resolves paths, docids and line ranges", async () => {
  const { app } = await makeApp();
  try {
    const full = await app.inject({ method: "GET", url: "/docs/claude-sessions/2026-02-11_alpha.md", headers: authHeaders });
    assert.equal(full.statusCode, 200, full.body);
    const doc = full.json();
    assert.equal(doc.file, "claude-sessions/2026-02-11_alpha.md");
    assert.equal(doc.collection, "claude-sessions");
    assert.equal(doc.path, "2026-02-11_alpha.md");
    assert.equal(doc.fromLine, null);
    assert.match(doc.body, /^---\nsession_id/);
    assert.equal(doc.metadata.project, "Receipts");
    assert.equal(doc.bodyLength, Buffer.byteLength(sampleDocs()[0]!.body));

    const byId = await app.inject({ method: "GET", url: `/docs/${doc.docid}`, headers: authHeaders });
    assert.equal(byId.statusCode, 200);
    assert.equal(byId.json().file, doc.file);
    const hashId = await app.inject({ method: "GET", url: `/docs/%23${doc.docid}`, headers: authHeaders });
    assert.equal(hashId.statusCode, 200, hashId.body);
    assert.equal(hashId.json().file, doc.file);
    const uri = await app.inject({ method: "GET", url: "/docs/qmd://claude-sessions/2026-02-11_alpha.md", headers: authHeaders });
    assert.equal(uri.statusCode, 200);

    // Frontmatter occupies lines 1-10, line 11 is blank, 12 is the heading.
    const ranged = await app.inject({ method: "GET", url: "/docs/claude-sessions/2026-02-11_alpha.md:12:2?lineNumbers=true", headers: authHeaders });
    assert.equal(ranged.statusCode, 200, ranged.body);
    const rb = ranged.json();
    assert.equal(rb.fromLine, 12);
    assert.equal(rb.maxLines, 2);
    assert.equal(rb.body, "12: # Alpha\n13: ");

    const override = await app.inject({ method: "GET", url: "/docs/claude-sessions/2026-02-11_alpha.md:12:2?fromLine=14&maxLines=1", headers: authHeaders });
    assert.equal(override.json().body, "## User");

    const noBody = await app.inject({ method: "GET", url: "/docs/claude-sessions/2026-02-11_alpha.md?body=false", headers: authHeaders });
    assert.equal(noBody.json().body, null);
  } finally {
    await app.close();
  }
});

test("GET /docs/{id} reports missing and ignored documents as 404 with details", async () => {
  const { app } = await makeApp({ docs: [...sampleDocs(), { collection: "claude-sessions", path: "ignored-one.md", body: "x" }] });
  try {
    const missing = await app.inject({ method: "GET", url: "/docs/claude-sessions/2026-nothing.md", headers: authHeaders });
    assert.equal(missing.statusCode, 404);
    assert.equal(missing.json().error, "not_found");
    assert.ok(Array.isArray(missing.json().details.similarFiles));

    const ignored = await app.inject({ method: "GET", url: "/docs/claude-sessions/ignored-one.md", headers: authHeaders });
    assert.equal(ignored.statusCode, 404);
    assert.equal(ignored.json().error, "excluded_by_ignore");
    assert.equal(ignored.json().details.rule, "**/ignored*");

    const empty = await app.inject({ method: "GET", url: "/docs/", headers: authHeaders });
    assert.equal(empty.statusCode, 400);
  } finally {
    await app.close();
  }
});

test("POST /docs:multiGet fetches by glob and by ids, skipping oversized files", async () => {
  const { app } = await makeApp();
  try {
    const glob = await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { pattern: "claude-sessions/2026-*.md" } });
    assert.equal(glob.statusCode, 200, glob.body);
    const gb = glob.json();
    assert.deepEqual(gb.docs.map((d: { path: string }) => d.path).sort(), ["2026-02-11_alpha.md", "2026-03-05_beta.md"]);
    assert.ok(gb.docs[0].body.length > 0);
    assert.deepEqual(gb.errors, []);

    const ids = await app.inject({
      method: "POST",
      url: "/docs:multiGet",
      headers: authHeaders,
      payload: { ids: ["codex-sessions/2026-08-31_gamma.md", "claude-sessions/missing.md"], maxLines: 2, lineNumbers: true, includeBody: true },
    });
    const ib = ids.json();
    assert.equal(ib.docs.length, 1);
    assert.equal(ib.docs[0].body, "1: ---\n2: session_id: \"gamma\"");
    assert.equal(ib.docs[0].maxLines, 2);
    assert.deepEqual(ib.errors, ["Document not found: claude-sessions/missing.md"]);

    const small = await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { pattern: "claude-sessions/*.md", maxBytes: 100 } });
    const sb = small.json();
    assert.ok(sb.skipped.length >= 2);
    assert.match(sb.skipped[0].reason, /too large/);

    const metaOnly = await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { pattern: "codex-sessions/*.md", includeBody: false } });
    assert.equal(metaOnly.json().docs[0].body, null);

    const bad = await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: {} });
    assert.equal(bad.statusCode, 400);
  } finally {
    await app.close();
  }
});

// --- collections ---------------------------------------------------------------

test("GET /collections lists name, counts and root context", async () => {
  const { app, roots } = await makeApp();
  try {
    const r = await app.inject({ method: "GET", url: "/collections", headers: authHeaders });
    assert.equal(r.statusCode, 200);
    assert.deepEqual(r.json(), {
      collections: [
        { name: "claude-sessions", path: roots.claude, pattern: "**/*.md", documents: 3, totalDocuments: 3, lastModified: "2026-01-01T00:00:00.000Z", includeByDefault: true, context: null },
        { name: "codex-sessions", path: roots.codex, pattern: "**/*.md", documents: 1, totalDocuments: 1, lastModified: "2026-01-01T00:00:00.000Z", includeByDefault: true, context: "Local Codex chat transcripts." },
      ],
    });
  } finally {
    await app.close();
  }
});

// --- ingest --------------------------------------------------------------------

test("POST /documents writes the file into the collection and queues an update+embed job", async () => {
  const { app, backend, roots } = await makeApp();
  try {
    const content = FRONTMATTER_DOC({ project: "Memoria", date: "2026-10-31", git_branch: "main", tags: ["project/Memoria"] }, "# New\n\nIngested by value.\n");
    const r = await app.inject({
      method: "POST",
      url: "/documents",
      headers: authHeaders,
      payload: { collection: "claude-sessions", path: "2026-10-31_new.md", content },
    });
    assert.equal(r.statusCode, 202, r.body);
    const body = r.json();
    assert.equal(body.file, "claude-sessions/2026-10-31_new.md");
    assert.equal(body.created, true);
    assert.equal(body.bytes, Buffer.byteLength(content));
    assert.equal(body.metadata.project, "Memoria");
    assert.ok(["queued", "running", "succeeded"].includes(body.job.state));
    assert.deepEqual(body.job.request, { collections: ["claude-sessions"], embed: true, force: false, reason: "POST /documents claude-sessions/2026-10-31_new.md" });
    assert.equal(await readFile(path.join(roots.claude, "2026-10-31_new.md"), "utf8"), content);

    await app.ctx.jobs.idle();
    const job = (await app.inject({ method: "GET", url: `/reindex/jobs/${body.job.id}`, headers: authHeaders })).json().job;
    assert.equal(job.state, "succeeded");
    assert.deepEqual(job.result.embed, { docsProcessed: 0, chunksEmbedded: 0, errors: 0, durationMs: 1 });
    assert.deepEqual(backend.calls.find((c) => c.method === "update")!.args[0], { collections: ["claude-sessions"] });
    assert.deepEqual(backend.calls.find((c) => c.method === "embed")!.args[0], { force: false, collection: "claude-sessions" });
    // Metadata is visible immediately, before the scan completes.
    assert.equal(app.ctx.metadata.get("claude-sessions/2026-10-31_new.md")?.project, "Memoria");

    const again = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "2026-10-31_new.md", content: "# v2", overwrite: false } });
    assert.equal(again.statusCode, 409);
    assert.equal(again.json().error, "exists");

    const noEmbed = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "2026-10-31_new.md", content: "# v2", embed: false } });
    assert.equal(noEmbed.statusCode, 202);
    assert.equal(noEmbed.json().created, false);
    assert.equal(noEmbed.json().metadata, null);
  } finally {
    await app.close();
  }
});

test("POST /documents rejects unknown collections and unsafe paths", async () => {
  const { app, roots } = await makeApp();
  try {
    const ghost = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "ghost", path: "x.md", content: "x" } });
    assert.equal(ghost.statusCode, 404);
    assert.equal(ghost.json().error, "unknown_collection");

    for (const p of ["../escape.md", "/abs.md", "C:/abs.md", "note.txt"]) {
      const r = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: p, content: "x" } });
      assert.equal(r.statusCode, 400, p);
    }
    const missing = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "x.md" } });
    assert.equal(missing.statusCode, 400);
    assert.equal(missing.json().error, "validation");
    const { readdir } = await import("node:fs/promises");
    assert.deepEqual(await readdir(roots.claude), [], "nothing was written for rejected requests");
  } finally {
    await app.close();
  }
});

// --- reindex -------------------------------------------------------------------

test("POST /reindex queues serialized jobs and exposes them", async () => {
  const { app, backend } = await makeApp();
  try {
    backend.updateDelayMs = 10;
    const all = await app.inject({ method: "POST", url: "/reindex", headers: authHeaders, payload: {} });
    assert.equal(all.statusCode, 202, all.body);
    const j1 = all.json().job;
    assert.deepEqual(j1.request, { collections: null, embed: true, force: false, reason: "POST /reindex" });

    const one = await app.inject({ method: "POST", url: "/reindex", headers: authHeaders, payload: { collections: ["codex-sessions"], embed: false } });
    const j2 = one.json().job;
    const dup = await app.inject({ method: "POST", url: "/reindex", headers: authHeaders, payload: { collections: ["codex-sessions"], embed: false } });
    assert.equal(dup.json().job.id, j2.id);

    const list = (await app.inject({ method: "GET", url: "/reindex/jobs", headers: authHeaders })).json().jobs;
    assert.deepEqual(list.map((j: { id: string }) => j.id), [j2.id, j1.id]);

    await app.ctx.jobs.idle();
    const done = (await app.inject({ method: "GET", url: `/reindex/jobs/${j1.id}`, headers: authHeaders })).json().job;
    assert.equal(done.state, "succeeded");
    assert.equal(done.result.update.collections, 2);
    assert.ok(done.result.embed);
    const done2 = (await app.inject({ method: "GET", url: `/reindex/jobs/${j2.id}`, headers: authHeaders })).json().job;
    assert.equal(done2.result.embed, null);
    assert.deepEqual(backend.calls.filter((c) => c.method === "embed").map((c) => c.args[0]), [{ force: false }]);

    const unknown = await app.inject({ method: "POST", url: "/reindex", headers: authHeaders, payload: { collections: ["ghost"] } });
    assert.equal(unknown.statusCode, 400);
    const noJob = await app.inject({ method: "GET", url: "/reindex/jobs/nope", headers: authHeaders });
    assert.equal(noJob.statusCode, 404);
  } finally {
    await app.close();
  }
});

// --- contract ------------------------------------------------------------------

test("GET /openapi.json describes every route and matches the committed contract", async () => {
  const { app } = await makeApp();
  try {
    const r = await app.inject({ method: "GET", url: "/openapi.json" });
    assert.equal(r.statusCode, 200);
    const spec = r.json();
    assert.equal(spec.openapi, "3.1.0");
    const paths = Object.keys(spec.paths).sort();
    assert.deepEqual(paths, ["/collections", "/docs/{reference}", "/docs:multiGet", "/documents", "/health", "/openapi.json", "/ready", "/reindex", "/reindex/jobs", "/reindex/jobs/{id}", "/search", "/status"]);
    assert.ok(spec.components.securitySchemes.bearer);
    assert.deepEqual(spec.paths["/health"].get.security, []);
    assert.equal(spec.paths["/search"].post.security, undefined); // inherits the global requirement

    const committed = JSON.parse(await readFile(new URL("../openapi.json", import.meta.url), "utf8"));
    assert.deepEqual(spec, committed, "openapi.json is stale; run `npm run openapi`");
  } finally {
    await app.close();
  }
});
