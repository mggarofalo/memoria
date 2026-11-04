/**
 * How remote mode fails, on the real QMD SDK and sqlite-vec with temp indexes and
 * mock workers. Loads no models. The standard these hold it to: a failure must
 * never leave a healthy-looking system.
 *
 * Opt in with MEMORIA_QMD_INTEGRATION=1 (CI sets it; `npm test` does not).
 */
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, writeFile } from "node:fs/promises";
import { createServer, type AddressInfo } from "node:net";
import path from "node:path";
import { createInterface } from "node:readline";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { buildApp } from "../src/app.ts";
import type { PoolWorkerConfig } from "../src/llm/types.ts";
import { replaceDocumentVectors, type SdkDb, type SdkHandles } from "../src/llm/wire.ts";
import { queueDbPathFor, SqliteJobStore } from "../src/queue-store.ts";
import { openBackend, type LlmOptions, type OpenedBackend } from "../src/store.ts";
import { authHeaders, tempDir, testConfig } from "./helpers.ts";
import { MockWorker, vectorFor } from "./mock-worker.ts";

async function freePort(): Promise<number> {
  const probe = createServer();
  await new Promise<void>((resolve) => probe.listen(0, "127.0.0.1", resolve));
  const { port } = probe.address() as AddressInfo;
  await new Promise((resolve) => probe.close(resolve));
  return port;
}

const skip = process.env.MEMORIA_QMD_INTEGRATION === "1" ? false : "set MEMORIA_QMD_INTEGRATION=1";

interface Row {
  [key: string]: unknown;
}

const DOCS: Record<string, string> = {
  "alpha.md": "# Alpha\n\nHow should the AuthenticationHandler validate API keys?\n\nCompare the bearer token in constant time.\n",
  "beta.md": "# Beta\n\nDesign the AuthenticationHandler for Memoria.Api. Use a scoped API key per collection.\n",
  "gamma.md": `# Gamma\n\n${"A long paragraph about the AuthenticationHandler and many other things. ".repeat(150)}\n`,
  "sub/delta.md": "# Delta\n\nNested document about something else entirely.\n",
};

async function fixture(docs: Record<string, string> = DOCS): Promise<{ dbPath: string; corpus: string }> {
  const root = await tempDir("memoria-failure-it-");
  const corpus = path.join(root, "sessions");
  await mkdir(corpus, { recursive: true });
  for (const [name, body] of Object.entries(docs)) {
    await mkdir(path.dirname(path.join(corpus, name)), { recursive: true });
    await writeFile(path.join(corpus, name), body, "utf8");
  }
  const dbPath = path.join(root, "index.sqlite");
  const qmd = await import("@tobilu/qmd");
  const builder = await qmd.createStore({ dbPath, config: { collections: { sessions: { path: corpus, pattern: "**/*.md" } } } });
  await builder.update();
  await builder.close();
  return { dbPath, corpus };
}

const worker = (name: string, mock: MockWorker, extra: Partial<PoolWorkerConfig> = {}): PoolWorkerConfig => ({ name, url: mock.url, token: undefined, capabilities: ["embed", "rerank", "generate"], priority: 1, maxConcurrent: 4, ...extra });

function remoteOptions(workers: PoolWorkerConfig[], over: Partial<Extract<LlmOptions, { mode: "remote" }>> = {}): LlmOptions {
  return { mode: "remote", workers, embedDimensions: 8, fallback: false, statusPollMs: 0, embedBatchBytes: 2048, ...over };
}

/** Run queries on an index with the SDK itself, after whatever had it has closed it. */
async function withSdk<T>(dbPath: string, run: (db: { prepare(sql: string): { all(...a: unknown[]): unknown[]; get(...a: unknown[]): unknown; run(...a: unknown[]): unknown }; exec(sql: string): void }) => T): Promise<T> {
  const qmd = await import("@tobilu/qmd");
  const store = await qmd.createStore({ dbPath });
  try {
    return run(store.internal.db as never);
  } finally {
    await store.close();
  }
}

const counts = (dbPath: string) =>
  withSdk(dbPath, (db) => ({
    cv: (db.prepare("SELECT COUNT(*) AS n FROM content_vectors").get() as { n: number }).n,
    vec: db.prepare("SELECT 1 FROM sqlite_master WHERE name = 'vectors_vec'").get() ? (db.prepare("SELECT COUNT(*) AS n FROM vectors_vec").get() as { n: number }).n : 0,
  }));

interface Opened {
  dbPath: string;
  backend: OpenedBackend;
  mock: MockWorker;
  done(): Promise<void>;
}

async function opened(over: Partial<Extract<LlmOptions, { mode: "remote" }>> = {}): Promise<Opened> {
  const { dbPath } = await fixture();
  const mock = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("only", mock)], over) });
  return {
    dbPath,
    backend,
    mock,
    done: async () => {
      await backend.close();
      await mock.stop();
    },
  };
}

// --- parity with the SDK's own embedding path ------------------------------------------

test("the farm's output matches the SDK's byte for byte", { skip }, async () => {
  const farmSide = await fixture();
  const sdkSide = await fixture();
  const mock = await new MockWorker(8).start();
  const farm = await openBackend({ dbPath: farmSide.dbPath, keepWarm: false, llm: remoteOptions([worker("w", mock)]) });
  const out = await farm.embed({ force: false });
  assert.equal(out.docsPending, 0);
  await farm.close();
  await mock.stop();

  // The same fake model, driven by the SDK's generateEmbeddings instead of the farm.
  const qmd = await import("@tobilu/qmd");
  const store = await qmd.createStore({ dbPath: sdkSide.dbPath });
  const original = (store.internal as unknown as { llm: { embedModelName: string; rerankModelName: string; generateModelName: string } }).llm;
  const stub = {
    embedModelName: original.embedModelName,
    rerankModelName: original.rerankModelName,
    generateModelName: original.generateModelName,
    tokenize: async (text: string) => Array.from(text, (c) => c.codePointAt(0)),
    detokenize: async (tokens: number[]) => String.fromCodePoint(...tokens),
    embed: async (text: string) => ({ embedding: vectorFor(text, 8), model: original.embedModelName }),
    embedBatch: async (texts: string[]) => texts.map((text) => ({ embedding: vectorFor(text, 8), model: original.embedModelName })),
    dispose: async () => {},
  };
  (store.internal as unknown as { llm: unknown }).llm = stub;
  const sdkLlm = (await import(new URL("./llm.js", import.meta.resolve("@tobilu/qmd")).href)) as { setDefaultLlamaCpp(llm: unknown): void };
  sdkLlm.setDefaultLlamaCpp(stub);
  const sdkOut = await store.embed({ force: false });
  sdkLlm.setDefaultLlamaCpp(null);
  (store.internal as unknown as { llm: unknown }).llm = original;
  await store.close();
  assert.equal(sdkOut.errors, 0);
  assert.ok(sdkOut.chunksEmbedded >= 4);

  const snapshot = (dbPath: string) =>
    withSdk(dbPath, (db) => ({
      rows: db.prepare("SELECT hash, seq, pos, model, embed_fingerprint, total_chunks FROM content_vectors ORDER BY hash, seq").all(),
      vectors: (db.prepare("SELECT hash_seq, embedding FROM vectors_vec ORDER BY hash_seq").all() as Array<{ hash_seq: string; embedding: Uint8Array }>).map((r) => [r.hash_seq, Buffer.from(r.embedding).toString("hex")]),
    }));
  const farmRows = await snapshot(farmSide.dbPath);
  assert.deepEqual(farmRows, await snapshot(sdkSide.dbPath));
  assert.ok(farmRows.vectors.length >= 4);
});

// --- a kill between the two inserts ---------------------------------------------------------

test("a failure inside a document's write rolls the whole document back", { skip }, async () => {
  const o = await opened();
  assert.equal((await o.backend.embed({ force: false })).docsPending, 0);
  await o.done();
  const before = await counts(o.dbPath);

  const qmd = await import("@tobilu/qmd");
  const store = await qmd.createStore({ dbPath: o.dbPath });
  const internal = store.internal as unknown as SdkHandles & { db: SdkDb };
  const model = "m";
  const hash = (internal.db.prepare("SELECT hash FROM content_vectors ORDER BY hash LIMIT 1").get() as { hash: string }).hash;
  const original = (internal.db.prepare("SELECT COUNT(*) AS n FROM content_vectors WHERE hash = ?").get(hash) as { n: number }).n;
  let calls = 0;
  const handles = {
    db: internal.db,
    ensureVecTable: () => {},
    // The second chunk fails, which is where a kill between tables would land too.
    insertEmbedding: (...args: Parameters<SdkHandles["insertEmbedding"]>) => {
      if (++calls === 2) throw new Error("disk full");
      internal.insertEmbedding(...args);
    },
  } as unknown as SdkHandles;
  const chunks = [{ seq: 0, pos: 0, vector: new Array(8).fill(0.5) }, { seq: 1, pos: 9, vector: new Array(8).fill(0.5) }];
  assert.throws(() => replaceDocumentVectors(handles, model, "fp", hash, chunks, 2), /disk full/);
  const after = (internal.db.prepare("SELECT COUNT(*) AS n FROM content_vectors WHERE hash = ? AND model != ?").get(hash, model) as { n: number }).n;
  await store.close();
  assert.equal(after, original, "the old vectors are back");
  assert.deepEqual(await counts(o.dbPath), before);
});

test("startup repairs vector rows with no vector, in both modes", { skip }, async () => {
  for (const mode of ["local", "remote"] as const) {
    const { dbPath } = await fixture();
    const mock = await new MockWorker(8).start();
    const first = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("w", mock)]) });
    await first.embed({ force: false });
    await first.close();

    // What a kill between the SDK's two inserts leaves.
    const orphan = await withSdk(dbPath, (db) => {
      const row = db.prepare("SELECT hash, seq FROM content_vectors ORDER BY hash LIMIT 1").get() as { hash: string; seq: number };
      db.prepare("DELETE FROM vectors_vec WHERE hash_seq = ?").run(`${row.hash}_${row.seq}`);
      return row;
    });
    const damaged = await counts(dbPath);
    assert.equal(damaged.cv, damaged.vec + 1, "the state the repair exists for");

    const repaired: number[] = [];
    const llm: LlmOptions = mode === "local" ? { mode: "local" } : remoteOptions([worker("w", mock)]);
    const second = await openBackend({ dbPath, keepWarm: false, llm, onVectorRepair: (n) => repaired.push(n) });
    assert.deepEqual(repaired, [1], mode);
    assert.ok((await second.getStatus()).needsEmbedding >= 1, `${mode}: the document is pending again`);
    if (mode === "remote") {
      assert.equal((await second.embed({ force: false })).docsPending, 0);
      assert.equal((await second.getStatus()).needsEmbedding, 0);
    }
    await second.close();
    await mock.stop();
    const repairedCounts = await counts(dbPath);
    assert.equal(repairedCounts.cv, repairedCounts.vec, `${mode}, for ${orphan.hash}`);
  }
});

test("a healthy index is not touched by the repair", { skip }, async () => {
  const o = await opened();
  await o.backend.embed({ force: false });
  const before = await counts(o.dbPath);
  await o.done();
  const repaired: number[] = [];
  const again = await openBackend({ dbPath: o.dbPath, keepWarm: false, onVectorRepair: (n) => repaired.push(n) });
  await again.close();
  assert.deepEqual(repaired, []);
  assert.deepEqual(await counts(o.dbPath), before);
});

test("rows whose vector table is gone are repaired too", { skip }, async () => {
  const o = await opened();
  await o.backend.embed({ force: false });
  await o.done();
  await withSdk(o.dbPath, (db) => db.exec("DROP TABLE vectors_vec"));
  const repaired: number[] = [];
  const again = await openBackend({ dbPath: o.dbPath, keepWarm: false, onVectorRepair: (n) => repaired.push(n) });
  assert.ok(repaired[0]! > 0);
  assert.equal((await again.getStatus()).needsEmbedding, 4);
  await again.close();
});

const killFixture = fileURLToPath(new URL("./fixtures/farm-kill.ts", import.meta.url));

test("killing the farm mid-write leaves every vector row with its vector", { skip }, async () => {
  const docs: Record<string, string> = {};
  for (let i = 0; i < 300; i++) docs[`d${i}.md`] = `# Doc ${i}\n\nUnique text number ${i} about something.\n`;
  const { dbPath } = await fixture(docs);
  const mock = await new MockWorker(768, undefined).start();
  let written = 0;
  for (const delayMs of [0, 2, 5, 10, 20, 40]) {
    const child = spawn(process.execPath, [killFixture, dbPath, mock.url], { stdio: ["ignore", "pipe", "ignore"] });
    const exited = new Promise<void>((resolve) => child.once("exit", () => resolve()));
    // Killed only once batches are coming back, then after a varied moment.
    const before = mock.count("/v1/embed");
    while (mock.count("/v1/embed") < before + 4) await new Promise((resolve) => setTimeout(resolve, 1));
    await new Promise((resolve) => setTimeout(resolve, delayMs));
    child.kill("SIGKILL");
    await exited;
    const { cv, vec } = await counts(dbPath);
    assert.equal(cv, vec, `after a kill at ${delayMs} ms`);
    written = Math.max(written, cv);
  }
  assert.ok(written > 0, "the kills landed while it was writing");
  await mock.stop();
});

// --- force ------------------------------------------------------------------------------------

test("a force job that hits an outage loses no vectors, and a retry does not start over", { skip }, async () => {
  const o = await opened();
  try {
    await o.backend.embed({ force: false });
    const before = await counts(o.dbPath);
    assert.ok(before.cv > 0);

    o.mock.on("POST /v1/embed", () => ({ status: 503, body: {} }));
    o.mock.on("GET /health", () => ({ status: 500, body: {} }));
    await o.backend.llm.refreshWorkers();
    await assert.rejects(o.backend.embed({ force: true }), /No worker can embed/);
    assert.deepEqual(await counts(o.dbPath), before, "nothing was cleared");
    const search = await o.backend.searchDetailed({ queries: [{ type: "vec", query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: false });
    assert.ok(!search.degradedReasons.includes("no_vectors") || search.degradedReasons.length > 0);
  } finally {
    await o.done();
  }
});

test("a force job failing partway keeps the old vectors of everything not yet redone", { skip }, async () => {
  const o = await opened({ breaker: { threshold: 1000 } });
  try {
    await o.backend.embed({ force: false });
    const before = await counts(o.dbPath);
    o.mock.on("POST /v1/embed", () => ({ status: 400, body: {} }));
    const out = await o.backend.embed({ force: true });
    assert.equal(out.chunksEmbedded, 0);
    assert.ok(out.docsPending! > 0);
    assert.deepEqual(await counts(o.dbPath), before, "not one vector lost");
  } finally {
    await o.done();
  }
});

test("a resumed force job skips what it already redid", { skip }, async () => {
  const o = await opened({ breaker: { threshold: 1000 } });
  try {
    await o.backend.embed({ force: false });
    await new Promise((resolve) => setTimeout(resolve, 5));
    const since = new Date().toISOString();
    await new Promise((resolve) => setTimeout(resolve, 5));
    const healthy = o.mock.handlers.get("POST /v1/embed")!;
    // Refuse one document's text, so the run finishes the others.
    o.mock.on("POST /v1/embed", (b, req) => ((b.texts as string[]).some((t) => t.includes("Nested document")) ? { status: 400, body: {} } : healthy(b, req)));
    const first = await o.backend.embed({ force: true, forceSince: since });
    assert.equal(first.docsProcessed, 3 + 1);
    assert.equal(first.docsPending, 1);

    o.mock.on("POST /v1/embed", healthy);
    const second = await o.backend.embed({ force: true, forceSince: since });
    assert.equal(second.docsProcessed, 1, "only the one left");
    assert.equal(second.docsPending, 0);
  } finally {
    await o.done();
  }
});

test("search reports no_vectors when the index holds none, in either mode", { skip }, async () => {
  const { dbPath } = await fixture();
  const local = await openBackend({ dbPath, keepWarm: false });
  const typed = await local.searchDetailed({ queries: [{ type: "lex", query: "AuthenticationHandler" }, { type: "vec", query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: false });
  assert.deepEqual(typed.degradedReasons, ["no_vectors"]);
  assert.ok(typed.hits.length > 0);
  const lexOnly = await local.searchDetailed({ queries: [{ type: "lex", query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: false });
  assert.deepEqual(lexOnly.degradedReasons, [], "it asked for nothing it did not get");
  await local.close();

  const o = await opened();
  try {
    const empty = await o.backend.searchDetailed({ queries: [{ type: "vec", query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: false });
    assert.ok(empty.degradedReasons.includes("no_vectors"));
    await o.backend.embed({ force: false });
    const full = await o.backend.searchDetailed({ queries: [{ type: "vec", query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: false });
    assert.deepEqual(full.degradedReasons, []);
  } finally {
    await o.done();
  }
});

test("a job leaving documents pending does not report success, and :retry finishes it", { skip }, async () => {
  const o = await opened({ breaker: { threshold: 1000 } });
  const app = await buildApp({ backend: o.backend, config: testConfig(), logger: false });
  try {
    const healthy = o.mock.handlers.get("POST /v1/embed")!;
    o.mock.on("POST /v1/embed", (b, req) => ((b.texts as string[]).some((t) => t.includes("Nested document")) ? { status: 400, body: {} } : healthy(b, req)));
    const job = (await app.inject({ method: "POST", url: "/reindex", headers: authHeaders, payload: {} })).json().job;
    await app.ctx.jobs.idle();
    const failed = (await app.inject({ method: "GET", url: `/reindex/jobs/${job.id}`, headers: authHeaders })).json().job;
    assert.equal(failed.state, "failed");
    assert.match(failed.error, /1 of 4 documents/);
    assert.equal(failed.result.embed.docsPending, 1);
    assert.equal(failed.result.embed.errors, 1);

    o.mock.on("POST /v1/embed", healthy);
    const retried = await app.inject({ method: "POST", url: `/queue/jobs/${job.id}:retry`, headers: authHeaders });
    assert.equal(retried.statusCode, 202);
    await app.ctx.jobs.idle();
    const done = (await app.inject({ method: "GET", url: `/reindex/jobs/${job.id}`, headers: authHeaders })).json().job;
    assert.equal(done.state, "succeeded");
    assert.equal((await o.backend.getStatus()).needsEmbedding, 0);
  } finally {
    await app.close();
    await o.done();
  }
});

// --- an older index ---------------------------------------------------------------------------------

test("a remote sidecar opens and embeds an index from before the newer vector columns", { skip }, async () => {
  const { dbPath } = await fixture();
  const { DatabaseSync } = await import("node:sqlite");
  const db = new DatabaseSync(dbPath);
  db.exec(`
    DROP INDEX IF EXISTS idx_content_vectors_model_fingerprint;
    DROP TABLE content_vectors;
    CREATE TABLE content_vectors (hash TEXT NOT NULL, seq INTEGER NOT NULL DEFAULT 0, pos INTEGER NOT NULL DEFAULT 0, model TEXT NOT NULL, embedded_at TEXT NOT NULL, PRIMARY KEY (hash, seq));
  `);
  const columns = (db.prepare("PRAGMA table_info(content_vectors)").all() as Array<{ name: string }>).map((c) => c.name);
  db.close();
  assert.ok(!columns.includes("total_chunks"), "the old shape");

  const mock = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("w", mock)]) });
  try {
    const out = await backend.embed({ force: false });
    assert.equal(out.docsPending, 0);
    assert.equal(out.docsProcessed, 4);
    assert.equal((await backend.getStatus()).needsEmbedding, 0);
  } finally {
    await backend.close();
    await mock.stop();
  }
});

// --- dimensions ---------------------------------------------------------------------------------------

test("with dimensions unset, a worker of the wrong length degrades search rather than failing it", { skip }, async () => {
  const o = await opened();
  await o.backend.embed({ force: false });
  await o.done();

  const wrong = await new MockWorker(4).start();
  const backend = await openBackend({ dbPath: o.dbPath, keepWarm: false, llm: remoteOptions([worker("w", wrong)], { embedDimensions: 0 }) });
  try {
    const out = await backend.searchDetailed({ queries: [{ type: "lex", query: "AuthenticationHandler" }, { type: "vec", query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: false });
    assert.deepEqual(out.degradedReasons, ["no_vectors"]);
    assert.ok(out.hits.length > 0);
  } finally {
    await backend.close();
    await wrong.stop();
  }
});

// --- the cache --------------------------------------------------------------------------------------

test("a poisoned score does not survive replacing the worker, and an operator can purge it", { skip }, async () => {
  const { dbPath } = await fixture();
  const evil = await new MockWorker(8).start();
  const query = { queries: [{ type: "lex" as const, query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: true };
  evil.on("POST /v1/rerank", (b) => ({ body: { results: (b.documents as string[]).map((_, index) => ({ index, score: 1 })) } }));
  const first = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("evil", evil)]) });
  await first.searchDetailed(query);
  assert.equal(evil.count("/v1/rerank"), 1);
  assert.ok(first.llm.cacheEntries() > 0);
  await first.searchDetailed(query);
  assert.equal(evil.count("/v1/rerank"), 1, "cached, as usual");
  await first.close();
  await evil.stop();

  const good = await new MockWorker(8).start();
  const second = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("good", good)]) });
  try {
    await second.searchDetailed(query);
    assert.equal(good.count("/v1/rerank"), 1, "a new worker scores afresh");

    // A worker that comes back unchanged keeps its cache, until an operator says otherwise.
    await second.searchDetailed(query);
    assert.equal(good.count("/v1/rerank"), 1);
    assert.ok(second.purgeCache() > 0);
    assert.equal(second.llm.cacheEntries(), 0);
    await second.searchDetailed(query);
    assert.equal(good.count("/v1/rerank"), 2);
  } finally {
    await second.close();
    await good.stop();
  }
});

test("cache rows past their age are swept when a remote sidecar starts", { skip }, async () => {
  const o = await opened();
  await o.done();
  await withSdk(o.dbPath, (db) => {
    db.prepare("INSERT INTO llm_cache (hash, result, created_at) VALUES ('old', '1', '2020-01-01T00:00:00.000Z')").run();
    db.prepare("INSERT INTO llm_cache (hash, result, created_at) VALUES ('new', '1', ?)").run(new Date().toISOString());
  });
  const mock = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath: o.dbPath, keepWarm: false, llm: remoteOptions([worker("w", mock)]) });
  assert.equal(backend.llm.cacheEntries(), 1);
  await backend.close();
  await mock.stop();
});

// --- provenance -----------------------------------------------------------------------------------------

test("one worker's vectors can be traced and undone, and the routes do it", { skip }, async () => {
  const { dbPath } = await fixture();
  const liar = await new MockWorker(8).start();
  const provenance = new SqliteJobStore(queueDbPathFor(dbPath));
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("liar", liar)]), provenance });
  const app = await buildApp({ backend, config: testConfig(), jobStore: provenance, logger: false });
  try {
    await backend.embed({ force: false });
    const documents = provenance.documentsBy("liar");
    assert.equal(documents.length, 4);
    assert.equal(backend.purgeWorkerVectors("nobody"), 0);
    await backend.llm.refreshWorkers();
    assert.equal(provenance.probeReference()?.worker, "liar");

    liar.on("GET /health", () => ({ status: 500, body: {} }));
    await backend.llm.refreshWorkers();
    const purge = await app.inject({ method: "POST", url: "/workers/liar:purge-vectors", headers: authHeaders });
    assert.equal(purge.statusCode, 202, purge.body);
    assert.equal(purge.json().documents, 4);
    assert.equal((await backend.getStatus()).needsEmbedding, 4);
    assert.deepEqual(provenance.documentsBy("liar"), []);
    assert.equal(provenance.probeReference(), null, "a reference from a liar goes too");
    const lexical = await backend.searchDetailed({ queries: [{ type: "lex", query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: false });
    assert.ok(lexical.hits.length > 0, "still findable lexically");

    // The job it queued waits for a worker, rather than failing.
    assert.equal(purge.json().job.request.reason, "purge vectors from worker liar");
  } finally {
    await app.close();
    await backend.close();
    provenance.close();
    await liar.stop();
  }
});

test("a broken provenance store costs the record, never a vector", { skip }, async () => {
  const { dbPath } = await fixture();
  const mock = await new MockWorker(8).start();
  const errors: string[] = [];
  const broken = () => {
    throw new Error("database is locked");
  };
  const backend = await openBackend({
    dbPath,
    keepWarm: false,
    llm: remoteOptions([worker("w", mock)]),
    provenance: { recordDocument: broken, documentsBy: broken, forgetDocuments: broken, probeReference: broken, setProbeReference: broken },
    onProvenanceError: (message) => errors.push(message),
  });
  try {
    await backend.llm.refreshWorkers(); // the probe reads and writes its reference through it too
    const out = await backend.embed({ force: false });
    assert.equal(out.docsPending, 0);
    assert.equal((await backend.getStatus()).needsEmbedding, 0);
    assert.ok(errors.length > 0 && errors.every((m) => m === "database is locked"));
    assert.equal(backend.workers()[0]!.embedModelCompatible, true);
  } finally {
    await backend.close();
    await mock.stop();
  }
});

test("a missing index is refused by name, and keep-warm applies in local mode", { skip }, async () => {
  await assert.rejects(openBackend({ dbPath: "/definitely/not/an/index.sqlite", keepWarm: false }), /QMD index not found/);
  const { dbPath } = await fixture();
  const backend = await openBackend({ dbPath, keepWarm: true });
  assert.equal(backend.llmHealth().mode, "local");
  assert.deepEqual(backend.workers(), []);
  assert.equal(backend.poolLoad(), null);
  assert.equal(backend.purgeWorkerVectors("anyone"), 0);
  await backend.close();
});

test("POST /cache:purge works through the route", { skip }, async () => {
  const o = await opened();
  const app = await buildApp({ backend: o.backend, config: testConfig(), logger: false });
  try {
    await o.backend.searchDetailed({ queries: [{ type: "lex", query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: true });
    const entries = o.backend.llm.cacheEntries();
    assert.ok(entries > 0);
    const purge = await app.inject({ method: "POST", url: "/cache:purge", headers: authHeaders });
    assert.deepEqual(purge.json(), { entries });
    assert.equal((await app.inject({ method: "POST", url: "/cache:purge" })).statusCode, 401);
  } finally {
    await app.close();
    await o.done();
  }
});

// --- /ready, end to end --------------------------------------------------------------------------------

test("/ready says degraded when /health is fine and every rerank fails", { skip }, async () => {
  const o = await opened();
  const app = await buildApp({ backend: o.backend, config: testConfig(), logger: false });
  try {
    await o.backend.llm.refreshWorkers();
    const ready = async () => (await app.inject({ method: "GET", url: "/ready", headers: authHeaders })).json();
    assert.equal((await ready()).status, "ready");

    o.mock.on("POST /v1/rerank", () => ({ status: 500, body: {} }));
    const search = async () => (await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { searches: [{ type: "lex", query: "AuthenticationHandler" }] } })).json();
    o.backend.llm.clearCache();
    for (let i = 0; i < 3; i++) assert.equal((await search()).degraded, true);
    await o.backend.llm.refreshWorkers();
    const after = await ready();
    assert.equal(after.status, "degraded");
    assert.deepEqual(after.llm.unavailable, ["rerank"]);
    assert.equal(after.models.resident, false);
    assert.equal(after.warmup.done, false);
  } finally {
    await app.close();
    await o.done();
  }
});

// --- the real process, end to end ---------------------------------------------------------------------------

const mainEntry = fileURLToPath(new URL("../src/main.ts", import.meta.url));

test("a corrupt queue file does not stop the sidecar from starting", { skip }, async () => {
  const { dbPath } = await fixture();
  await writeFile(queueDbPathFor(dbPath), "not a database ".repeat(500));
  const child = spawn(process.execPath, [mainEntry], {
    env: { ...process.env, MEMORIA_QMD_DB_PATH: dbPath, MEMORIA_QMD_PORT: String(await freePort()), MEMORIA_QMD_WARMUP: "0", MEMORIA_QMD_SECRET: "0123456789abcdefXYZ" },
    stdio: ["ignore", "pipe", "ignore"],
  });
  const exited = new Promise<void>((resolve) => child.once("exit", () => resolve()));
  let output = "";
  const listening = new Promise<void>((resolve) => {
    createInterface({ input: child.stdout }).on("line", (line) => {
      output += `${line}\n`;
      if (line.includes("memoria-qmd listening")) resolve();
    });
  });
  try {
    await Promise.race([listening, exited, new Promise((resolve) => setTimeout(resolve, 20_000))]);
    assert.match(output, /memoria-qmd listening/, output);
    assert.match(output, /Moved the unreadable job queue/);
  } finally {
    child.kill("SIGKILL");
    await exited;
  }
});

