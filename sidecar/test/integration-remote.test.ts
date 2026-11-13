/**
 * Remote mode end to end against the real QMD SDK and sqlite-vec on a throwaway
 * fixture index, with the worker mocked over HTTP. Loads no models. This shows
 * the store accepts the adapter on every path that uses it; the mock's numbers
 * are made up, so it shows nothing about scoring.
 *
 * Opt in with MEMORIA_QMD_INTEGRATION=1 (CI sets it; `npm test` does not).
 */
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { buildApp, warmup } from "../src/app.ts";
import { loadConfig } from "../src/config.ts";
import type { LlmCallLog } from "../src/llm/types.ts";
import { queueDbPathFor, SqliteJobStore } from "../src/queue-store.ts";
import { openBackend, type OpenedBackend } from "../src/store.ts";
import { authHeaders, tempDir, testConfig } from "./helpers.ts";
import { EMBED_FILE, MockWorker, WORKER_TOKEN } from "./mock-worker.ts";

const skip = process.env.MEMORIA_QMD_INTEGRATION === "1" ? false : "set MEMORIA_QMD_INTEGRATION=1";

const ALPHA = "# Alpha\n\n## User\n\nHow should the AuthenticationHandler validate API keys?\n\n## Assistant\n\nCompare the bearer token in constant time.\n";
const BETA = "# Beta\n\n## User\n\nDesign the AuthenticationHandler for Memoria.Api.\n\n## Assistant\n\nUse a scoped API key per collection.\n";

async function fixtureIndex(): Promise<{ dbPath: string; corpus: string }> {
  const root = await tempDir("memoria-remote-it-");
  const corpus = path.join(root, "sessions");
  await mkdir(corpus, { recursive: true });
  const dbPath = path.join(root, "index.sqlite");
  await writeFile(path.join(corpus, "alpha.md"), ALPHA, "utf8");
  await writeFile(path.join(corpus, "beta.md"), BETA, "utf8");
  const qmd = await import("@tobilu/qmd");
  const builder = await qmd.createStore({ dbPath, config: { collections: { sessions: { path: corpus, pattern: "**/*.md" } } } });
  await builder.update();
  await builder.close();
  return { dbPath, corpus };
}

test("local mode loads no adapter code and calls nothing remote, even with a worker URL set", { skip }, async () => {
  const { dbPath } = await fixtureIndex();
  const worker = await new MockWorker().start();
  try {
    const probe = fileURLToPath(new URL("./fixtures/local-mode-probe.ts", import.meta.url));
    // A worker URL and token in the environment must have no effect in local mode.
    const env: NodeJS.ProcessEnv = { ...process.env, MEMORIA_QMD_WORKER_URL: worker.url, MEMORIA_QMD_WORKER_TOKEN: WORKER_TOKEN };
    delete env.MEMORIA_QMD_LLM;
    delete env.QMD_FORCE_CPU;
    const run = spawnSync(process.execPath, [probe, dbPath], { env, encoding: "utf8", timeout: 60_000 });
    assert.equal(run.status, 0, run.stderr);
    const report = JSON.parse(run.stdout.trim().split("\n").at(-1)!);
    assert.equal(report.mode, "local");
    assert.equal(report.probeMode, "local");
    assert.equal(report.transport, "in-process");
    assert.deepEqual(report.loaded, [], "no module under src/llm/ was loaded");
    assert.deepEqual(report.fetched, [], "nothing was fetched");
    assert.equal(report.forceCpu, null, "local mode leaves GPU selection alone");
    assert.equal(report.hits, 2);
    assert.equal(report.models.resident, false);
    assert.ok(report.models.rerank.startsWith("hf:") && !report.models.rerank.includes("#worker"));
    assert.equal(worker.requests.length, 0, "the mock worker saw no request");
  } finally {
    await worker.stop();
  }
});

interface Remote {
  backend: OpenedBackend;
  worker: MockWorker;
  calls: LlmCallLog[];
  logs: string[];
  dbPath: string;
  done(): Promise<void>;
}

async function openRemote(): Promise<Remote> {
  const { dbPath } = await fixtureIndex();
  const worker = await new MockWorker(8).start();
  const calls: LlmCallLog[] = [];
  const logs: string[] = [];
  const backend = await openBackend({
    dbPath,
    keepWarm: true,
    llm: { mode: "remote", workers: [{ name: "only", url: worker.url, token: WORKER_TOKEN, capabilities: ["embed", "rerank", "generate"], priority: 1, maxConcurrent: 4 }], embedDimensions: 8, fallback: false, statusPollMs: 0, breaker: { threshold: 1000 } },
    onLlmCall: (e) => {
      calls.push(e);
      logs.push(JSON.stringify(e));
    },
    onLlmDegraded: (e) => logs.push(JSON.stringify(e)),
  });
  return {
    backend,
    worker,
    calls,
    logs,
    dbPath,
    done: async () => {
      await backend.close();
      await worker.stop();
    },
  };
}

test("documents embed through the worker, including the chunker's tokenizer", { skip }, async () => {
  const r = await openRemote();
  try {
    // Without batches, as with a worker from before that route, so the chunker's calls show.
    r.worker.on("POST /v1/tokenize-batch", () => ({ status: 404, body: { error: "no_route" } }));
    const embed = await r.backend.embed({ force: false });
    assert.equal(embed.errors, 0);
    assert.ok(embed.chunksEmbedded >= 2, `chunks: ${embed.chunksEmbedded}`);
    // The chunker uses the SDK's global singleton, not store.llm. Seeing tokenize
    // here is what proves that singleton was replaced.
    assert.ok(r.worker.count("/v1/tokenize") >= 2);
    assert.ok(r.worker.count("/v1/embed") >= 1);
    const texts = r.worker.requests.filter((q) => q.path === "/v1/embed").flatMap((q) => q.body.texts as string[]);
    assert.ok(texts.some((t) => t.startsWith("title: ") && t.includes(" | text: ")), "QMD's document format is kept");
    assert.ok(r.worker.requests.every((q) => q.authorization === `Bearer ${WORKER_TOKEN}`));
    const status = await r.backend.getStatus();
    assert.equal(status.needsEmbedding, 0);
    assert.equal(status.hasVectorIndex, true);
  } finally {
    await r.done();
  }
});

test("vectors under stock QMD's fingerprint read as pending, stay until replaced, and are replaced", { skip }, async () => {
  const r = await openRemote();
  try {
    await r.backend.embed({ force: false });
    // A second handle through the SDK, which loads sqlite-vec for vectors_vec.
    const qmd = await import("@tobilu/qmd");
    const side = await qmd.createStore({ dbPath: r.dbPath });
    const db = side.internal.db as unknown as { prepare(sql: string): { all(): unknown[]; get(): unknown; run(): unknown } };
    const fingerprints = () => db.prepare("SELECT DISTINCT embed_fingerprint AS f FROM content_vectors ORDER BY f").all().map((row) => (row as { f: string }).f);
    try {
      const ours = fingerprints();
      assert.equal(ours.length, 1);
      assert.notEqual(ours[0], "c37385", "whole-chunk vectors are not written under the stock fingerprint");
      // As an index built in-process by stock QMD holds them: each covering a chunk's last 512 tokens.
      db.prepare("UPDATE content_vectors SET embed_fingerprint = 'c37385'").run();
      const stored = (db.prepare("SELECT COUNT(*) AS n FROM vectors_vec").get() as { n: number }).n;
      assert.equal((await r.backend.getStatus()).needsEmbedding, 2, "both documents need embedding again");
      assert.equal((db.prepare("SELECT COUNT(*) AS n FROM vectors_vec").get() as { n: number }).n, stored, "nothing is dropped before it is replaced");
      const again = await r.backend.embed({ force: false });
      assert.equal(again.errors, 0);
      assert.deepEqual(fingerprints(), ours, "every vector was embedded again, none left mixed in");
      assert.equal((await r.backend.getStatus()).needsEmbedding, 0);
    } finally {
      await side.close();
    }
  } finally {
    await r.done();
  }
});

test("an unexpanded search runs QMD's hybrid search on the query alone, with no expansion call", { skip }, async () => {
  const r = await openRemote();
  try {
    await r.backend.embed({ force: false });
    r.worker.requests.length = 0;

    // An intent turns off QMD's strong-signal shortcut, so only expand=false can skip expansion here.
    const out = await r.backend.searchDetailed({ query: "AuthenticationHandler", intent: "api key auth", expand: false, limit: 5, minScore: 0, rerank: true });
    assert.equal(out.hits.length, 2);
    assert.deepEqual(out.degradedReasons, []);
    assert.equal(r.worker.count("/v1/complete"), 0, "no expansion");
    assert.ok(r.worker.count("/v1/embed") >= 1, "the query's own vector");
    assert.equal(r.worker.count("/v1/rerank"), 1, "still reranked");

    // The same search expanded calls the model, so the switch is what made the difference.
    r.backend.llm.clearCache();
    r.worker.requests.length = 0;
    await r.backend.searchDetailed({ query: "AuthenticationHandler", intent: "api key auth", limit: 5, minScore: 0, rerank: true });
    assert.equal(r.worker.count("/v1/complete"), 1, "expansion");
  } finally {
    await r.done();
  }
});

test("a reranked search runs the whole pipeline remotely and caches apart from local results", { skip }, async () => {
  const r = await openRemote();
  try {
    await r.backend.embed({ force: false });
    r.worker.requests.length = 0;

    const hits = await r.backend.search({ query: "AuthenticationHandler", limit: 5, minScore: 0, rerank: true });
    assert.equal(hits.length, 2);
    assert.ok(hits.every((h) => h.score > 0 && h.score <= 1));
    assert.equal(r.worker.count("/v1/complete"), 1, "expansion");
    assert.ok(r.worker.count("/v1/embed") >= 1, "query vectors");
    assert.equal(r.worker.count("/v1/rerank"), 1, "rerank");
    const texts = r.worker.requests.filter((q) => q.path === "/v1/embed").flatMap((q) => q.body.texts as string[]);
    assert.ok(texts.every((t) => t.startsWith("task: search result | query: ")), "QMD's query format is kept");
    assert.ok(r.calls.some((c) => c.operation === "rerank" && c.ok));

    // QMD's caches apply on repeat, under the worker's own keys.
    r.worker.requests.length = 0;
    await r.backend.search({ query: "AuthenticationHandler", limit: 5, minScore: 0, rerank: true });
    assert.equal(r.worker.count("/v1/rerank"), 0);
    assert.equal(r.worker.count("/v1/complete"), 0);
    r.backend.llm.clearCache();
    await r.backend.search({ query: "AuthenticationHandler", limit: 5, minScore: 0, rerank: true });
    assert.equal(r.worker.count("/v1/rerank"), 1);

    // /ready works from worker state.
    const models = r.backend.models();
    assert.equal(models.resident, false, "the status poll is off here");
    assert.ok(models.rerank.startsWith("hf:") && !models.rerank.includes("#worker"));
    assert.equal(r.backend.llm.mode, "remote");
    assert.equal(r.backend.llm.rerankTransport, "worker-rerank");
  } finally {
    await r.done();
  }
});

const LEX = [{ type: "lex" as const, query: "AuthenticationHandler" }];

test("bad worker scores degrade the search and leave nothing in QMD's cache", { skip }, async () => {
  const r = await openRemote();
  try {
    await r.backend.embed({ force: false });
    const search = () => r.backend.searchDetailed({ queries: LEX, limit: 5, minScore: 0, rerank: true });
    r.worker.on("POST /v1/rerank", (b) => ({ body: { results: (b.documents as string[]).map((_, index) => ({ index, score: 42 })) } }));
    r.backend.llm.clearCache();
    const degraded = await search();
    assert.deepEqual(degraded.degradedReasons, ["no_rerank"]);
    assert.equal(degraded.hits.length, 2);
    assert.equal(r.backend.llm.cacheEntries(), 0, "nothing bogus was cached");

    r.worker.on("POST /v1/rerank", (b) => ({ body: { results: (b.documents as string[]).map((_, index) => ({ index, score: 0.75 })) } }));
    const healthy = await search();
    assert.deepEqual(healthy.degradedReasons, []);
    assert.equal(healthy.hits.length, 2);
    assert.equal(r.worker.count("/v1/rerank"), 2, "the bad answer was not replayed");
    assert.ok(healthy.hits.every((h) => h.score !== 42));
    assert.ok(r.backend.llm.cacheEntries() > 0, "a real answer is cached as usual");
  } finally {
    await r.done();
  }
});

test("vectors of the wrong size never reach sqlite-vec", { skip }, async () => {
  const r = await openRemote();
  try {
    r.worker.on("POST /v1/embed", (b) => ({ body: { model: EMBED_FILE, embeddings: (b.texts as string[]).map(() => [1, 2, 3]) } }));
    // Even the known-good text comes back wrong, so that is the worker at fault, not the documents.
    await assert.rejects(r.backend.embed({ force: false }), /No worker can embed/);
    assert.deepEqual(r.backend.deadLetters(), []);
    const status = await r.backend.getStatus();
    assert.equal(status.hasVectorIndex, false);
    assert.equal(status.needsEmbedding, 2, "the documents stay pending");
  } finally {
    await r.done();
  }
});

// --- degraded mode ------------------------------------------------------------------

test("with no worker up, every search kind answers, says so, and caches nothing", { skip }, async () => {
  const r = await openRemote();
  try {
    await r.backend.embed({ force: false });
    r.backend.llm.clearCache();
    await r.worker.stop();
    await r.backend.llm.refreshWorkers();

    // A plain query would need expansion and reranking.
    const plain = await r.backend.searchDetailed({ query: "AuthenticationHandler", limit: 5, minScore: 0.9, rerank: true });
    assert.equal(plain.hits.length, 2, "minScore is not applied to rank-derived scores");
    assert.deepEqual([...plain.degradedReasons].sort(), ["no_expansion", "no_rerank", "no_vectors"]);

    const typed = await r.backend.searchDetailed({ queries: LEX, limit: 5, minScore: 0, rerank: true });
    assert.equal(typed.hits.length, 2);
    assert.deepEqual(typed.degradedReasons, ["no_rerank"]);

    // A query QMD's lexical syntax would refuse must not turn into a 500 here.
    const awkward = await r.backend.searchDetailed({ query: 'AuthenticationHandler "unbalanced', limit: 5, minScore: 0, rerank: true });
    assert.ok(awkward.degradedReasons.includes("no_rerank"));

    // Unreranked was asked for, so nothing was given up except vectors, had any been asked for.
    const lexOnly = await r.backend.searchDetailed({ queries: LEX, limit: 5, minScore: 0, rerank: false });
    assert.deepEqual(lexOnly.degradedReasons, []);

    assert.equal(r.backend.llm.cacheEntries(), 0, "no placeholder score or expansion was written");
    assert.deepEqual(r.backend.models().loaded, { embed: false, rerank: false, generate: false });
    assert.equal(r.backend.models().resident, false);
    const health = r.backend.llmHealth();
    assert.equal(health.degraded, true);
    assert.deepEqual(health.unavailable, ["embed", "rerank", "generate"]);
    assert.equal(health.workersUp, 0);
  } finally {
    await r.backend.close();
  }
});

test("a worker dying mid-search degrades that search, then recovery gets real scores", { skip }, async () => {
  const r = await openRemote();
  try {
    await r.backend.embed({ force: false });
    r.backend.llm.clearCache();
    r.worker.on("POST /v1/rerank", () => ({ status: 503, body: {} }));
    const query = { queries: LEX, limit: 5, minScore: 0, rerank: true };
    const during = await r.backend.searchDetailed(query);
    assert.deepEqual(during.degradedReasons, ["no_rerank"]);
    assert.equal(during.hits.length, 2);
    assert.equal(r.backend.llm.cacheEntries(), 0);

    r.worker.on("POST /v1/rerank", (b) => ({ body: { results: (b.documents as string[]).map((_, index) => ({ index, score: 0.9 })) } }));
    const after = await r.backend.searchDetailed(query);
    assert.deepEqual(after.degradedReasons, []);
    assert.ok(r.worker.count("/v1/rerank") === 2, "asked again: nothing stale stood in for it");
    assert.ok(r.backend.llm.cacheEntries() > 0);
  } finally {
    await r.done();
  }
});

test("a search error that is not about workers is still an error", { skip }, async () => {
  const r = await openRemote();
  try {
    await assert.rejects(r.backend.searchDetailed({ queries: [{ type: "lex", query: "two\nlines" }], limit: 5, minScore: 0, rerank: true }), /single-line/);
  } finally {
    await r.done();
  }
});

test("/ready over a real backend reads degraded with no worker, and healthy with one", { skip }, async () => {
  const r = await openRemote();
  const app = await buildApp({ backend: r.backend, config: testConfig(), logger: false });
  try {
    await r.backend.llm.refreshWorkers();
    await warmup(app.ctx, { info: () => {}, warn: () => {} });
    const up = (await app.inject({ method: "GET", url: "/ready", headers: authHeaders })).json();
    assert.equal(up.status, "ready");
    assert.equal(up.models.resident, true);
    assert.equal(up.warmup.done, true);
    assert.equal(up.llm.degraded, false);
    assert.equal(up.llm.workersUp, 1);

    r.worker.on("GET /health", () => ({ status: 500, body: {} }));
    await r.backend.llm.refreshWorkers();
    const down = await app.inject({ method: "GET", url: "/ready?requireModels=true", headers: authHeaders });
    assert.equal(down.statusCode, 503);
    const body = down.json();
    assert.equal(body.status, "degraded");
    assert.equal(body.models.resident, false);
    assert.equal(body.warmup.done, false);
    assert.equal(body.llm.workersUp, 0);
    assert.deepEqual(body.llm.unavailable, ["embed", "rerank", "generate"]);

    const search = (await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { query: "AuthenticationHandler" } })).json();
    assert.equal(search.degraded, true);
    assert.ok(search.degradedReasons.includes("no_rerank"));
    assert.equal(search.total, 2);
  } finally {
    await app.close();
    await r.done();
  }
});

// --- the farm and the queue on the real SDK ------------------------------------------

test("embedding work spreads across workers, on one model, through QMD's own tables", { skip }, async () => {
  const { dbPath } = await fixtureIndex();
  const a = await new MockWorker(8).start();
  const b = await new MockWorker(8).start();
  const wrong = await new MockWorker(8, "some-other-model.gguf").start();
  const backend = await openBackend({
    dbPath,
    keepWarm: true,
    llm: {
      mode: "remote",
      workers: [
        { name: "a", url: a.url, token: undefined, capabilities: ["embed"], priority: 1, maxConcurrent: 2 },
        { name: "b", url: b.url, token: undefined, capabilities: ["embed"], priority: 2, maxConcurrent: 2 },
        { name: "wrong", url: wrong.url, token: undefined, capabilities: ["embed"], priority: 0, maxConcurrent: 8 },
      ],
      embedDimensions: 8,
      fallback: false,
      statusPollMs: 0,
      embedBatchBytes: 1024,
    },
  });
  try {
    await backend.llm.refreshWorkers();
    const seen = new Set<string>();
    const out = await backend.embed({ force: false, onWorker: (n) => seen.add(n) });
    assert.equal(out.errors, 0);
    assert.ok(out.chunksEmbedded >= 2);
    assert.equal(wrong.count("/v1/embed"), 0, "the other model never embedded");
    assert.equal(wrong.count("/v1/tokenize"), 0);
    assert.ok([...seen].every((n) => n !== "wrong"));
    const status = await backend.getStatus();
    assert.equal(status.needsEmbedding, 0);
    assert.equal(status.hasVectorIndex, true);

    // QMD can search what the farm wrote, and considers the work done.
    const vec = await backend.searchDetailed({ queries: [{ type: "vec", query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: false });
    assert.ok(vec.hits.length > 0);
    assert.equal((await backend.embed({ force: false })).docsProcessed, 0, "nothing left");

    // Force re-embeds everything, and works in one collection.
    const forced = await backend.embed({ force: true, collection: "sessions" });
    assert.equal(forced.docsProcessed, 2);
    assert.equal((await backend.getStatus()).needsEmbedding, 0);
  } finally {
    await backend.close();
    await a.stop();
    await b.stop();
    await wrong.stop();
  }
});

test("a failed batch recovers within the run, and the document ends up whole", { skip }, async () => {
  const { dbPath, corpus } = await fixtureIndex();
  // Long enough to make several chunks, one chunk to a batch.
  await writeFile(path.join(corpus, "long.md"), `# Long\n\n${"paragraph about the AuthenticationHandler. ".repeat(600)}\n`, "utf8");
  const qmd = await import("@tobilu/qmd");
  const seed = await qmd.createStore({ dbPath, config: { collections: { sessions: { path: corpus, pattern: "**/*.md" } } } });
  await seed.update();
  await seed.close();

  const worker = await new MockWorker(8).start();
  const open = () => openBackend({
    dbPath,
    keepWarm: true,
    llm: { mode: "remote", workers: [{ name: "w", url: worker.url, token: undefined, capabilities: ["embed"], priority: 1, maxConcurrent: 1 }], embedDimensions: 8, fallback: false, statusPollMs: 0, embedBatchBytes: 1024 },
  });
  const backend = await open();
  try {
    // Fail one batch: its document's other chunks must not stand.
    let batches = 0;
    const healthy = worker.handlers.get("POST /v1/embed")!;
    worker.on("POST /v1/embed", (b, req) => {
      batches++;
      return batches === 2 ? { status: 500, body: {} } : healthy(b, req);
    });
    const first = await backend.embed({ force: false });
    assert.equal(batches > 2, true);
    assert.equal(first.errors, 0, "the second try got through");
    assert.equal(first.docsPending, 0);
    assert.equal((await backend.getStatus()).needsEmbedding, 0);
  } finally {
    await backend.close();
    await worker.stop();
  }
});

test("a job with no worker keeps documents searchable and finishes when one appears", { skip }, async () => {
  const r = await openRemote();
  const store = new SqliteJobStore(`${r.dbPath}.queue.sqlite`);
  const app = await buildApp({ backend: r.backend, config: testConfig(), jobStore: store, logger: false });
  try {
    const healthy = r.worker.handlers.get("GET /health")!;
    r.worker.on("GET /health", () => ({ status: 500, body: {} }));
    await r.backend.llm.refreshWorkers();

    const ingest = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "sessions", path: "gamma.md", content: "# Gamma\n\nzebrafinch deferral marker\n" } });
    assert.equal(ingest.statusCode, 202);
    const id = ingest.json().job.id;
    // The scan runs on a thread it starts first, so give it the time that takes.
    const job = async () => (await app.inject({ method: "GET", url: `/reindex/jobs/${id}`, headers: authHeaders })).json().job;
    for (let waited = 0; (await job()).error === null && waited < 20_000; waited += 50) await new Promise((resolve) => setTimeout(resolve, 50));
    const waiting = await job();
    assert.equal(waiting.state, "queued");
    assert.match(waiting.error, /No worker can embed/);
    const lexical = await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { searches: [{ type: "lex", query: "zebrafinch" }], rerank: false } });
    assert.equal(lexical.json().total, 1, "the scan ran, so it is findable");

    r.worker.on("GET /health", healthy);
    await r.backend.llm.refreshWorkers();
    app.ctx.jobs.flush();
    await app.ctx.jobs.idle();
    const done = (await app.inject({ method: "GET", url: `/reindex/jobs/${id}`, headers: authHeaders })).json().job;
    assert.equal(done.state, "succeeded");
    assert.equal(done.worker, "only");
    assert.equal(done.error, null);
    assert.equal((await r.backend.getStatus()).needsEmbedding, 0);
  } finally {
    await app.close();
    store.close();
    await r.done();
  }
});

test("the queue's database sits beside the index, and QMD's has no trace of it", { skip }, async () => {
  const r = await openRemote();
  const queueFile = queueDbPathFor(r.dbPath);
  const store = new SqliteJobStore(queueFile);
  const app = await buildApp({ backend: r.backend, config: testConfig(), jobStore: store, logger: false });
  try {
    await app.inject({ method: "POST", url: "/reindex", headers: authHeaders, payload: { embed: false } });
    await app.ctx.jobs.idle();
    assert.equal(store.load().length, 1);
    assert.equal(path.dirname(queueFile), path.dirname(r.dbPath));
  } finally {
    await app.close();
    store.close();
    await r.done();
  }
  const { DatabaseSync } = await import("node:sqlite");
  const index = new DatabaseSync(r.dbPath, { readOnly: true });
  const tables = index.prepare("SELECT name FROM sqlite_master WHERE type='table'").all() as Array<{ name: string }>;
  index.close();
  assert.ok(tables.some((t) => t.name === "documents"), "this is QMD's index");
  assert.ok(!tables.some((t) => t.name === "jobs"));
});

test("two workers' tokens stay out of everything callers can see", { skip }, async () => {
  const { dbPath } = await fixtureIndex();
  const TOKENS = ["pool-token-one-0123456789", "pool-token-two-0123456789"];
  const a = await new MockWorker(8).start();
  const b = await new MockWorker(8).start();
  const logs: string[] = [];
  const backend = await openBackend({
    dbPath,
    keepWarm: true,
    llm: {
      mode: "remote",
      workers: [
        { name: "one", url: a.url, token: TOKENS[0], capabilities: ["embed", "rerank", "generate"], priority: 1, maxConcurrent: 4 },
        { name: "two", url: b.url, token: TOKENS[1], capabilities: ["embed", "rerank", "generate"], priority: 2, maxConcurrent: 4 },
      ],
      embedDimensions: 8,
      fallback: false,
      statusPollMs: 0,
    },
    onLlmCall: (e) => logs.push(JSON.stringify(e)),
    onLlmDegraded: (e) => logs.push(JSON.stringify(e)),
  });
  const store = new SqliteJobStore(`${dbPath}.queue.sqlite`);
  const app = await buildApp({ backend, config: testConfig(), jobStore: store, logger: { level: "trace", stream: { write: (line: string) => void logs.push(line) } } });
  try {
    await app.ready();
    await backend.llm.refreshWorkers();
    for (const worker of [a, b]) {
      const echo = (_: unknown, request: { headers: { authorization?: string } }) => ({ status: 500, body: { error: request.headers.authorization, message: `token: ${request.headers.authorization}` } });
      for (const route of ["POST /v1/rerank", "POST /v1/embed", "POST /v1/tokenize", "POST /v1/complete"]) worker.on(route, echo as never);
    }
    const replies: string[] = [];
    const get = async (url: string) => replies.push((await app.inject({ method: "GET", url, headers: authHeaders })).body);
    replies.push((await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { query: "AuthenticationHandler" } })).body);
    const job = (await app.inject({ method: "POST", url: "/reindex", headers: authHeaders, payload: {} })).json().job;
    await new Promise((resolve) => setTimeout(resolve, 200));
    await get(`/reindex/jobs/${job.id}`);
    await get("/workers");
    await get("/queue");
    await get("/ready");
    assert.ok(a.requests.some((q) => q.authorization === `Bearer ${TOKENS[0]}`), "the worker did get its token");
    assert.ok(b.requests.every((q) => q.authorization === `Bearer ${TOKENS[1]}`), "and only its own");
    const queueBytes = (await readFile(`${dbPath}.queue.sqlite`)).toString("latin1");
    const queueLog = await readFile(`${dbPath}.queue.sqlite-wal`).then((x) => x.toString("latin1"), () => "");
    for (const token of TOKENS) {
      for (const text of [...replies, ...logs, queueBytes, queueLog]) assert.ok(!text.includes(token), text.slice(0, 300));
    }
    const workers = JSON.parse(replies[2]!).workers as Array<{ name: string; host: string }>;
    assert.deepEqual(workers.map((w) => w.name), ["one", "two"]);
  } finally {
    await app.close();
    store.close();
    await backend.close();
    await a.stop();
    await b.stop();
  }
});

test("the token reaches no API response or log line, whatever the worker sends back", { skip }, async () => {
  const r = await openRemote();
  const fastifyLogs: string[] = [];
  const app = await buildApp({
    backend: r.backend,
    config: testConfig(),
    logger: { level: "trace", stream: { write: (line: string) => void fastifyLogs.push(line) } },
  });
  try {
    await app.ready();
    const echo = (_: unknown, request: { headers: { authorization?: string } }) => ({ status: 500, body: { error: request.headers.authorization, message: `token: ${request.headers.authorization}` } });
    for (const route of ["POST /v1/rerank", "POST /v1/embed", "POST /v1/tokenize", "POST /v1/complete", "GET /capabilities", "GET /health"]) r.worker.on(route, echo as never);

    const replies: string[] = [];
    const search = await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { searches: [{ type: "lex", query: "AuthenticationHandler" }], rerank: true } });
    assert.equal(search.statusCode, 200, "it degrades rather than fails");
    assert.equal(search.json().degraded, true);
    replies.push(search.body);

    // Failed embed work surfaces through the job record and /ready, both of which reach callers.
    const job = await app.inject({ method: "POST", url: "/reindex", headers: authHeaders, payload: { embed: true } });
    // Every embed fails, so it waits for a worker: it does not finish.
    const fetchRecord = () => app.inject({ method: "GET", url: `/reindex/jobs/${job.json().job.id}`, headers: authHeaders });
    for (let i = 0; i < 2000 && (await fetchRecord()).json().job.error === null; i++) await new Promise((resolve) => setTimeout(resolve, 10));
    const record = await fetchRecord();
    assert.match(record.json().job.error, /No worker can embed/);
    replies.push(record.body);

    // Whether warmup records an error depends on its query finding candidates to
    // rerank; either way its state is in the /ready body checked below.
    await warmup(app.ctx, { info: () => {}, warn: () => {} });
    const ready = await app.inject({ method: "GET", url: "/ready", headers: authHeaders });
    replies.push(ready.body);

    assert.ok(r.worker.requests.some((q) => q.authorization === `Bearer ${WORKER_TOKEN}`), "the worker did get the token");
    for (const text of [...replies, ...fastifyLogs, ...r.logs]) assert.ok(!text.includes(WORKER_TOKEN), text.slice(0, 300));
  } finally {
    await app.close();
    await r.done();
  }
});

test("config selects local by default and validates remote settings only in remote mode", { skip: false }, () => {
  assert.deepEqual(loadConfig({}).llm, { mode: "local" });
  assert.deepEqual(loadConfig({ MEMORIA_QMD_WORKER_URL: "not a url", MEMORIA_QMD_WORKER_TOKEN: "x" }).llm, { mode: "local" });
});
