import assert from "node:assert/strict";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { test } from "node:test";
import { authHeaders, makeApp, ManualScheduler, sampleDocs, TEST_SECRET, testConfig } from "./helpers.ts";
import { FRONTMATTER_DOC } from "./fake-backend.ts";
import { buildApp, warmup } from "../src/app.ts";
import { resolveVersion } from "../src/version.ts";
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
    const unauthDelete = await app.inject({ method: "DELETE", url: "/documents?collection=claude-sessions&path=x.md" });
    assert.equal(unauthDelete.statusCode, 401);
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
    assert.equal(health.version, resolveVersion());
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
    assert.deepEqual(ib.errors, ["File not found: claude-sessions/missing.md"]);

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

test("POST /docs:multiGet resolves ids and pattern separately and merges them by file", async () => {
  const { app, backend } = await makeApp();
  const paths = (body: { docs: { path: string }[] }) => body.docs.map((d) => d.path).sort();
  const multiGet = (payload: object) => app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload });
  try {
    const gamma = (await app.inject({ method: "GET", url: "/docs/codex-sessions/2026-08-31_gamma.md", headers: authHeaders })).json();
    const alpha = (await app.inject({ method: "GET", url: "/docs/claude-sessions/2026-02-11_alpha.md", headers: authHeaders })).json();

    // ids only, one of them: a lone docid and a lone path both resolve.
    assert.deepEqual(paths((await multiGet({ ids: [gamma.docid] })).json()), ["2026-08-31_gamma.md"]);
    assert.deepEqual(paths((await multiGet({ ids: ["claude-sessions/2026-02-11_alpha.md"] })).json()), ["2026-02-11_alpha.md"]);

    // pattern only.
    assert.deepEqual(paths((await multiGet({ pattern: "claude-sessions/2026-03*.md" })).json()), ["2026-03-05_beta.md"]);

    // Both: this is the combination that used to report no documents at all.
    backend.calls.length = 0;
    const both = await multiGet({ pattern: "claude-sessions/2026-03*.md", ids: [gamma.docid] });
    assert.equal(both.statusCode, 200, both.body);
    assert.deepEqual(paths(both.json()), ["2026-03-05_beta.md", "2026-08-31_gamma.md"]);
    assert.deepEqual(both.json().errors, []);
    assert.deepEqual(backend.calls.filter((c) => c.method === "multiGet").map((c) => c.args[0]), ["qmd://claude-sessions/2026-03-05_beta.md,", `${gamma.docid},`]);

    // A file selected by both comes back once.
    const overlap = await multiGet({ pattern: "claude-sessions/2026-*.md", ids: [alpha.docid, "claude-sessions/2026-02-11_alpha.md"] });
    assert.deepEqual(paths(overlap.json()), ["2026-02-11_alpha.md", "2026-03-05_beta.md"]);

    // The same mix written as one string resolves the same way.
    const mixed = await multiGet({ pattern: `claude-sessions/2026-03*.md,${gamma.docid}` });
    assert.deepEqual(paths(mixed.json()), ["2026-03-05_beta.md", "2026-08-31_gamma.md"]);

    // A half that matches nothing is reported without losing the other half.
    const halfMiss = (await multiGet({ pattern: "nowhere/*.md", ids: [gamma.docid, "claude-sessions/gone.md"] })).json();
    assert.deepEqual(paths(halfMiss), ["2026-08-31_gamma.md"]);
    assert.deepEqual(halfMiss.errors, ["No files matched pattern: nowhere/*.md", "File not found: claude-sessions/gone.md"]);

    // Oversized files stay under `skipped`, also once each.
    const skipped = (await multiGet({ pattern: "claude-sessions/2026-02*.md", ids: [alpha.docid], maxBytes: 10 })).json();
    assert.equal(skipped.docs.length, 0);
    assert.deepEqual(skipped.skipped.map((s: { file: string }) => s.file), ["claude-sessions/2026-02-11_alpha.md"]);
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

// --- debounced reindex ---------------------------------------------------------

test("a burst of POST /documents shares one job and one update, with the contract intact", async () => {
  const scheduler = new ManualScheduler();
  const { app, backend, roots } = await makeApp({ scheduler, config: { reindexDebounceMs: 60_000 } });
  try {
    const ids = new Set<string>();
    for (let i = 0; i < 25; i++) {
      const r = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: `burst-${i}.md`, content: `# Burst ${i}\n\nflurrymarker\n` } });
      assert.equal(r.statusCode, 202, r.body);
      assert.equal(r.json().job.state, "queued");
      ids.add(r.json().job.id);
      await scheduler.advance(1_000);
    }
    assert.equal(ids.size, 1);
    assert.equal(backend.calls.filter((c) => c.method === "update").length, 0);
    // Every file is durable before any index work happens.
    assert.equal(await readFile(path.join(roots.claude, "burst-24.md"), "utf8"), "# Burst 24\n\nflurrymarker\n");

    const [id] = ids;
    const held = (await app.inject({ method: "GET", url: `/reindex/jobs/${id}`, headers: authHeaders })).json().job;
    assert.equal(held.state, "queued");
    assert.deepEqual(held.request, { collections: ["claude-sessions"], embed: true, force: false, reason: "POST /documents claude-sessions/burst-0.md (+24 more)" });
    const ready = (await app.inject({ method: "GET", url: "/ready", headers: authHeaders })).json();
    assert.equal(ready.jobs.pending, 1);

    await scheduler.advance(60_000);
    await app.ctx.jobs.idle();
    assert.equal(backend.calls.filter((c) => c.method === "update").length, 1);
    assert.equal(backend.calls.filter((c) => c.method === "embed").length, 1);
    const done = (await app.inject({ method: "GET", url: `/reindex/jobs/${id}`, headers: authHeaders })).json().job;
    assert.equal(done.state, "succeeded");
    assert.equal(done.result.update.indexed, 25);
    const found = await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { query: "flurrymarker", limit: 100 } });
    assert.equal(found.json().total, 25);
  } finally {
    await app.close();
  }
});

test("a document arriving mid-run triggers exactly one follow-up", async () => {
  const scheduler = new ManualScheduler();
  const { app, backend } = await makeApp({ scheduler, config: { reindexDebounceMs: 60_000 } });
  const ingest = (name: string) => app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: name, content: `# ${name}\n` } });
  try {
    let open = () => {};
    backend.updateGate = new Promise((resolve) => (open = resolve));
    const first = (await ingest("a.md")).json().job;
    await scheduler.advance(60_000);
    const running = (await app.inject({ method: "GET", url: `/reindex/jobs/${first.id}`, headers: authHeaders })).json().job;
    assert.equal(running.state, "running");

    const second = (await ingest("b.md")).json().job;
    const third = (await ingest("c.md")).json().job;
    assert.notEqual(second.id, first.id);
    assert.equal(third.id, second.id);

    await scheduler.advance(60_000);
    backend.updateGate = null;
    open();
    await app.ctx.jobs.idle();
    assert.equal(backend.calls.filter((c) => c.method === "update").length, 2);
    const jobs = (await app.inject({ method: "GET", url: "/reindex/jobs", headers: authHeaders })).json().jobs;
    assert.deepEqual(jobs.map((j: { id: string; state: string }) => [j.id, j.state]), [[second.id, "succeeded"], [first.id, "succeeded"]]);
  } finally {
    await app.close();
  }
});

test("POST /reindex does not wait on the debounce and starts an identical held job", async () => {
  const scheduler = new ManualScheduler();
  const { app, backend } = await makeApp({ scheduler, config: { reindexDebounceMs: 60_000 } });
  try {
    const now = (await app.inject({ method: "POST", url: "/reindex", headers: authHeaders, payload: { collections: ["codex-sessions"] } })).json().job;
    await app.ctx.jobs.idle();
    assert.equal(backend.calls.filter((c) => c.method === "update").length, 1);
    assert.equal(now.request.reason, "POST /reindex");

    const held = (await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "x.md", content: "# x\n" } })).json().job;
    const flushed = (await app.inject({ method: "POST", url: "/reindex", headers: authHeaders, payload: { collections: ["claude-sessions"] } })).json().job;
    assert.equal(flushed.id, held.id);
    await app.ctx.jobs.idle();
    assert.equal(backend.calls.filter((c) => c.method === "update").length, 2);
    assert.equal(scheduler.pendingTimers, 0);
  } finally {
    await app.close();
  }
});

// --- delete --------------------------------------------------------------------

const deleteUrl = (collection: string, p: string) => `/documents?collection=${encodeURIComponent(collection)}&path=${encodeURIComponent(p)}`;

test("DELETE /documents removes the file, queues an update, and the document stops being searchable", async () => {
  const { app, backend, roots } = await makeApp();
  try {
    const content = FRONTMATTER_DOC({ project: "Memoria", date: "2026-11-01" }, "# Tombstone\n\nzebrafinchmarker\n");
    await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "sub/doomed.md", content } });
    await app.ctx.jobs.idle();
    const search = () => app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { query: "zebrafinchmarker" } });
    assert.equal((await search()).json().total, 1);
    assert.equal(app.ctx.metadata.get("claude-sessions/sub/doomed.md")?.project, "Memoria");

    const r = await app.inject({ method: "DELETE", url: deleteUrl("claude-sessions", "sub/doomed.md"), headers: authHeaders });
    assert.equal(r.statusCode, 202, r.body);
    const body = r.json();
    assert.equal(body.collection, "claude-sessions");
    assert.equal(body.path, "sub/doomed.md");
    assert.equal(body.file, "claude-sessions/sub/doomed.md");
    assert.equal(body.deleted, true);
    assert.deepEqual(body.job.request, { collections: ["claude-sessions"], embed: false, force: false, reason: "DELETE /documents claude-sessions/sub/doomed.md" });
    await assert.rejects(readFile(path.join(roots.claude, "sub", "doomed.md")), /ENOENT/);
    assert.equal(app.ctx.metadata.get("claude-sessions/sub/doomed.md"), undefined);

    await app.ctx.jobs.idle();
    const job = (await app.inject({ method: "GET", url: `/reindex/jobs/${body.job.id}`, headers: authHeaders })).json().job;
    assert.equal(job.state, "succeeded");
    assert.equal(job.result.update.removed, 1);
    assert.equal(job.result.embed, null);
    assert.equal((await search()).json().total, 0);
    const gone = await app.inject({ method: "GET", url: "/docs/claude-sessions/sub/doomed.md", headers: authHeaders });
    assert.equal(gone.statusCode, 404);
    assert.equal(backend.calls.filter((c) => c.method === "embed").length, 1, "only the ingest embedded");
  } finally {
    await app.close();
  }
});

test("DELETE /documents is idempotent on a missing file and still queues the update", async () => {
  const { app } = await makeApp();
  try {
    const r = await app.inject({ method: "DELETE", url: deleteUrl("claude-sessions", "never-there.md"), headers: authHeaders });
    assert.equal(r.statusCode, 202, r.body);
    assert.equal(r.json().deleted, false);
    assert.ok(r.json().job.id);
    await app.ctx.jobs.idle();
    const again = await app.inject({ method: "DELETE", url: deleteUrl("claude-sessions", "never-there.md"), headers: authHeaders });
    assert.equal(again.statusCode, 202);
    assert.equal(again.json().deleted, false);
  } finally {
    await app.close();
  }
});

test("DELETE /documents joins the debounce with ingest", async () => {
  const scheduler = new ManualScheduler();
  const { app, backend } = await makeApp({ scheduler, config: { reindexDebounceMs: 60_000 } });
  try {
    const put = (await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "x.md", content: "# x\n" } })).json().job;
    const del = (await app.inject({ method: "DELETE", url: deleteUrl("claude-sessions", "y.md"), headers: authHeaders })).json().job;
    assert.equal(del.id, put.id);
    assert.equal(del.request.embed, true, "the ingest's embed survives the merge");
    await scheduler.advance(60_000);
    await app.ctx.jobs.idle();
    assert.equal(backend.calls.filter((c) => c.method === "update").length, 1);
  } finally {
    await app.close();
  }
});

test("DELETE /documents cannot reach outside the collection root", async () => {
  const { app, roots } = await makeApp();
  try {
    // A real file one level above the root, plus the sibling collection's root:
    // what a successful traversal would destroy.
    const outside = path.join(path.dirname(roots.claude), `${path.basename(roots.claude)}-outside.md`);
    await writeFile(outside, "keep");
    const sibling = path.join(roots.codex, "sibling.md");
    await writeFile(sibling, "keep");
    const name = path.basename(outside);
    const codexName = path.basename(roots.codex);

    const invalid = [
      `../${name}`,
      `..\\${name}`,
      `sub/../../${name}`,
      `./../${name}`,
      `../${codexName}/sibling.md`,
      `..\\${codexName}\\sibling.md`,
      "/etc/hosts.md",
      "\\\\server\\share\\x.md",
      "//server/share/x.md",
      "sub//x.md",
      "with\0nul.md",
      // These are ordinary names on POSIX, and only Windows reads them as something else.
      ...(process.platform === "win32" ? [`.. /${name}`, `sub/.. /.. /${name}`, outside, "C:/Windows/win.md", "C:\\Windows\\win.md", "x.md:stream"] : [outside]),
    ];
    for (const p of invalid) {
      const r = await app.inject({ method: "DELETE", url: deleteUrl("claude-sessions", p), headers: authHeaders });
      assert.equal(r.statusCode, 400, p);
      assert.equal(r.json().error, "invalid_path", p);
    }
    assert.deepEqual(app.ctx.jobs.list(), [], "rejected deletes queued nothing");

    // One decode only: a doubly encoded traversal stays a literal, harmless name.
    const encoded = await app.inject({ method: "DELETE", url: `/documents?collection=claude-sessions&path=${encodeURIComponent(`%2e%2e%2f${name}`)}`, headers: authHeaders });
    assert.equal(encoded.statusCode, 202, encoded.body);
    assert.equal(encoded.json().deleted, false);

    // Single-encoded traversal is decoded by the router and rejected above.
    const single = await app.inject({ method: "DELETE", url: `/documents?collection=claude-sessions&path=%2e%2e%2f${name}`, headers: authHeaders });
    assert.equal(single.statusCode, 400);

    assert.equal(await readFile(outside, "utf8"), "keep");
    assert.equal(await readFile(sibling, "utf8"), "keep");
  } finally {
    await app.close();
  }
});

test("DELETE /documents validates collection and query", async () => {
  const { app, roots } = await makeApp();
  try {
    const ghost = await app.inject({ method: "DELETE", url: deleteUrl("ghost", "x.md"), headers: authHeaders });
    assert.equal(ghost.statusCode, 404);
    assert.equal(ghost.json().error, "unknown_collection");

    for (const url of ["/documents", "/documents?collection=claude-sessions", "/documents?path=x.md", "/documents?collection=claude-sessions&path="]) {
      const r = await app.inject({ method: "DELETE", url, headers: authHeaders });
      assert.equal(r.statusCode, 400, url);
      assert.equal(r.json().error, "validation", url);
    }
    const twice = await app.inject({ method: "DELETE", url: "/documents?collection=claude-sessions&path=a.md&path=b.md", headers: authHeaders });
    assert.equal(twice.statusCode, 400);

    await mkdir(path.join(roots.claude, "folder.md"));
    const dir = await app.inject({ method: "DELETE", url: deleteUrl("claude-sessions", "folder.md"), headers: authHeaders });
    assert.equal(dir.statusCode, 409);
    assert.equal(dir.json().error, "is_directory");
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

    // Several explicit collections embed each one separately, never the whole index.
    backend.calls.length = 0;
    const multi = await app.inject({ method: "POST", url: "/reindex", headers: authHeaders, payload: { collections: ["claude-sessions", "codex-sessions"], force: true } });
    assert.equal(multi.statusCode, 202, multi.body);
    await app.ctx.jobs.idle();
    const multiJob = (await app.inject({ method: "GET", url: `/reindex/jobs/${multi.json().job.id}`, headers: authHeaders })).json().job;
    assert.equal(multiJob.state, "succeeded");
    assert.deepEqual(
      backend.calls.filter((c) => c.method === "embed").map((c) => c.args[0]),
      [{ force: true, collection: "claude-sessions" }, { force: true, collection: "codex-sessions" }],
    );
    assert.deepEqual(multiJob.result.embed, { docsProcessed: 0, chunksEmbedded: 0, errors: 0, durationMs: 2 });

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
    assert.deepEqual(paths, ["/cache:purge", "/collections", "/docs/{reference}", "/docs:multiGet", "/documents", "/health", "/openapi.json", "/queue", "/queue/dead-letters", "/queue/dead-letters:clear", "/queue/jobs/{id}:cancel", "/queue/jobs/{id}:retry", "/ready", "/reindex", "/reindex/jobs", "/reindex/jobs/{id}", "/search", "/status", "/workers"]);
    assert.ok(spec.components.securitySchemes.bearer);
    assert.deepEqual(spec.paths["/health"].get.security, []);
    assert.equal(spec.paths["/search"].post.security, undefined); // inherits the global requirement

    const committed = JSON.parse(await readFile(new URL("../openapi.json", import.meta.url), "utf8"));
    assert.deepEqual(spec, committed, "openapi.json is stale; run `npm run openapi`");
  } finally {
    await app.close();
  }
});

// --- errors a caller's malformed request can cause ------------------------------------

test("a malformed request gets the error that names the problem, not a 500", async () => {
  const { app } = await makeApp({ config: { bodyLimitBytes: 64 } });
  try {
    const search = { method: "POST" as const, url: "/search", headers: authHeaders };
    const big = await app.inject({ ...search, payload: { query: "x".repeat(200) } });
    assert.equal(big.statusCode, 413);
    assert.equal(big.json().error, "payload_too_large");
    const media = await app.inject({ ...search, headers: { ...authHeaders, "content-type": "application/xml" }, payload: "<x/>" });
    assert.equal(media.statusCode, 415);
    assert.equal(media.json().error, "unsupported_media_type");
    const json = await app.inject({ ...search, headers: { ...authHeaders, "content-type": "application/json" }, payload: "{nope" });
    assert.equal(json.statusCode, 400);
    assert.equal(json.json().error, "bad_request");
  } finally {
    await app.close();
  }
});

// --- warmup ---------------------------------------------------------------------------

const quietLog = () => {
  const warned: string[] = [];
  return { warned, log: { info: () => {}, warn: (_: object, message: string) => void warned.push(message) } };
};

test("warmup in local mode walks the pipeline and records that it finished", async () => {
  const { app, backend } = await makeApp({ modelsResident: false });
  try {
    app.ctx.warmup.done = false;
    const { log, warned } = quietLog();
    await warmup(app.ctx, log);
    assert.equal(app.ctx.warmup.done, true);
    assert.equal(typeof app.ctx.warmup.durationMs, "number");
    assert.deepEqual(warned, []);
    assert.ok(backend.calls.some((c) => c.method === "search"));
  } finally {
    await app.close();
  }
});

test("a warmup that fails records why, and models load on first use instead", async () => {
  const { app, backend } = await makeApp({ modelsResident: false });
  try {
    app.ctx.warmup.done = false;
    backend.search = async () => {
      throw new Error("no model");
    };
    const { log, warned } = quietLog();
    await warmup(app.ctx, log);
    assert.equal(app.ctx.warmup.done, false);
    assert.equal(app.ctx.warmup.error, "no model");
    assert.match(warned[0]!, /load lazily/);
  } finally {
    await app.close();
  }
});

test("warmup leaves a remote sidecar alone: workers warm themselves", async () => {
  const { app, backend } = await makeApp({ modelsResident: false });
  try {
    backend.health = { mode: "remote", degraded: false, unavailable: [], workersUp: 1, workersTotal: 1, embedDisagreement: [], documentsSetAside: 0 };
    backend.search = async () => {
      throw new Error("must not be called");
    };
    const { log, warned } = quietLog();
    await warmup(app.ctx, log);
    assert.deepEqual(warned, []);
    assert.equal(app.ctx.warmup.error, null);
  } finally {
    await app.close();
  }
});
