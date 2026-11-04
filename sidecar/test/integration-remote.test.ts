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
import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { buildApp, warmup } from "../src/app.ts";
import { loadConfig } from "../src/config.ts";
import type { LlmCallLog } from "../src/llm/types.ts";
import { openBackend, type OpenedBackend } from "../src/store.ts";
import { authHeaders, tempDir, testConfig } from "./helpers.ts";
import { MockWorker, WORKER_TOKEN } from "./mock-worker.ts";

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
    llm: { mode: "remote", worker: { url: worker.url, token: WORKER_TOKEN, embedDimensions: 8 }, fallback: false, statusPollMs: 0 },
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
    assert.equal(r.backend.llm.rerankTransport, "llama-server-rerank");
  } finally {
    await r.done();
  }
});

test("bad worker scores fail the search and leave nothing in QMD's cache", { skip }, async () => {
  const r = await openRemote();
  try {
    await r.backend.embed({ force: false });
    const search = () => r.backend.search({ searches: undefined, queries: [{ type: "lex", query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: true } as never);
    r.worker.on("POST /v1/rerank", (b) => ({ body: { results: (b.documents as string[]).map((_, index) => ({ index, score: 42 })) } }));
    await assert.rejects(search(), /invalid_response/);

    r.worker.on("POST /v1/rerank", (b) => ({ body: { results: (b.documents as string[]).map((_, index) => ({ index, score: 0.75 })) } }));
    const hits = await search();
    assert.equal(hits.length, 2);
    assert.equal(r.worker.count("/v1/rerank"), 2, "the bad answer was not cached");
    assert.ok(hits.every((h) => h.score !== 42));
  } finally {
    await r.done();
  }
});

test("vectors of the wrong size never reach sqlite-vec", { skip }, async () => {
  const r = await openRemote();
  try {
    r.worker.on("POST /v1/embed", (b) => ({ body: { embeddings: (b.texts as string[]).map(() => [1, 2, 3]) } }));
    await assert.rejects(r.backend.embed({ force: false }), /embedding dimensions/);
    assert.equal((await r.backend.getStatus()).hasVectorIndex, false);
  } finally {
    await r.done();
  }
});

test("lexical search still works with the worker down when reranking is off", { skip }, async () => {
  const r = await openRemote();
  try {
    await r.worker.stop();
    const hits = await r.backend.search({ queries: [{ type: "lex", query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: false });
    assert.equal(hits.length, 2);
    await assert.rejects(r.backend.search({ queries: [{ type: "lex", query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: true }), /unavailable/);
  } finally {
    await r.backend.close();
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
    assert.equal(search.statusCode, 500);
    assert.deepEqual(search.json(), { error: "internal", message: "Internal error; see sidecar logs" });
    replies.push(search.body);

    // Failed embed work surfaces through the job record and /ready, both of which reach callers.
    const job = await app.inject({ method: "POST", url: "/reindex", headers: authHeaders, payload: { embed: true } });
    await app.ctx.jobs.idle();
    const record = await app.inject({ method: "GET", url: `/reindex/jobs/${job.json().job.id}`, headers: authHeaders });
    assert.ok(record.json().job.state === "failed" || record.json().job.result.embed.errors > 0, record.body);
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
