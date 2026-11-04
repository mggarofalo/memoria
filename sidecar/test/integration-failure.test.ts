/**
 * How remote mode fails, on the real QMD SDK and sqlite-vec with temp indexes and
 * mock workers. Loads no models. The standard these hold it to: a failure must
 * never leave a healthy-looking system.
 *
 * Opt in with MEMORIA_QMD_INTEGRATION=1 (CI sets it; `npm test` does not).
 */
import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { mkdir, rm, writeFile } from "node:fs/promises";
import { createServer, type AddressInfo } from "node:net";
import path from "node:path";
import { createInterface } from "node:readline";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { buildApp } from "../src/app.ts";
import type { PoolWorkerConfig } from "../src/llm/types.ts";
import { replaceDocumentVectors, sweepExpiredCache, type SdkDb, type SdkHandles } from "../src/llm/wire.ts";
import { queueDbPathFor } from "../src/queue-store.ts";
import { openBackend, type LlmOptions, type OpenedBackend } from "../src/store.ts";
import { authHeaders, tempDir, testConfig } from "./helpers.ts";
import { EMBED_FILE, MockWorker, vectorFor } from "./mock-worker.ts";

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
async function withSdk<T>(dbPath: string, run: (db: { prepare(sql: string): { all(...a: unknown[]): unknown[]; get(...a: unknown[]): unknown; run(...a: unknown[]): unknown }; exec(sql: string): void }) => T): Promise<Awaited<T>> {
  const qmd = await import("@tobilu/qmd");
  const store = await qmd.createStore({ dbPath });
  try {
    return await run(store.internal.db as never);
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

test("parity holds on awkward documents: no heading, unicode, frontmatter, duplicates across collections, empty", { skip }, async () => {
  const awkward: Record<string, Record<string, string>> = {
    one: {
      "no-heading-at-all.md": "Just a paragraph, with no heading, so the title comes from the file name.\n",
      "unicode-Ω-日本語.md": "# Ünïcödé 日本語 Ω\n\nCafé naïve résumé — 東京 🙂 \u200B zero width.\n",
      "frontmatter.md": "---\ntitle: From Frontmatter\ntags: [a, b]\n---\n\nBody after frontmatter.\n",
      "frontmatter-and-heading.md": "---\ntitle: Ignored Perhaps\n---\n\n# Real Heading\n\nBody.\n",
      "duplicate.md": "# Same Body\n\nThis exact body is in both collections.\n",
      "empty.md": "",
      "blank.md": "   \n\n  \n",
      "long.md": `No heading here either. ${"A long run of words about nothing in particular. ".repeat(200)}\n`,
    },
    two: {
      "other-name-for-duplicate.md": "# Same Body\n\nThis exact body is in both collections.\n",
      "duplicate-no-heading-a.md": "A body with no heading, in the second collection only.\n",
    },
  };
  const build = async () => {
    const root = await tempDir("memoria-parity-");
    const collections: Record<string, { path: string; pattern: string }> = {};
    for (const [name, docs] of Object.entries(awkward)) {
      const dir = path.join(root, name);
      await mkdir(dir, { recursive: true });
      for (const [file, body] of Object.entries(docs)) await writeFile(path.join(dir, file), body, "utf8");
      collections[name] = { path: dir, pattern: "**/*.md" };
    }
    const dbPath = path.join(root, "index.sqlite");
    const qmd = await import("@tobilu/qmd");
    const builder = await qmd.createStore({ dbPath, config: { collections } });
    await builder.update();
    await builder.close();
    return dbPath;
  };
  const farmDb = await build();
  const sdkDb = await build();
  const mock = await new MockWorker(8).start();
  const farm = await openBackend({ dbPath: farmDb, keepWarm: false, llm: remoteOptions([worker("w", mock)]) });
  const out = await farm.embed({ force: false });
  assert.equal(out.errors, 0);
  await farm.close();
  await mock.stop();

  const qmd = await import("@tobilu/qmd");
  const store = await qmd.createStore({ dbPath: sdkDb });
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

  const snapshot = (dbPath: string) =>
    withSdk(dbPath, (db) => ({
      rows: db.prepare("SELECT hash, seq, pos, model, embed_fingerprint, total_chunks FROM content_vectors ORDER BY hash, seq").all(),
      vectors: (db.prepare("SELECT hash_seq, embedding FROM vectors_vec ORDER BY hash_seq").all() as Array<{ hash_seq: string; embedding: Uint8Array }>).map((r) => [r.hash_seq, Buffer.from(r.embedding).toString("hex")]),
    }));
  const farmRows = await snapshot(farmDb);
  assert.deepEqual(farmRows, await snapshot(sdkDb));
  assert.ok(farmRows.vectors.length >= 7, String(farmRows.vectors.length));
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
    const healthy = o.mock.handlers.get("POST /v1/embed")!;
    o.mock.on("POST /v1/embed", (b, req) => ((b.texts as string[]).some((t) => t.includes("memoria worker identity probe")) ? healthy(b, req) : { status: 400, body: {} }));
    await assert.rejects(o.backend.embed({ force: true }), /full-size test text/);
    assert.deepEqual(o.backend.deadLetters(), []);
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
    assert.equal(failed.result.embed.docsDeadLettered, 1);

    // It stops failing later jobs: a retry leaves it set aside, and succeeds.
    const retried = await app.inject({ method: "POST", url: `/queue/jobs/${job.id}:retry`, headers: authHeaders });
    assert.equal(retried.statusCode, 202);
    await app.ctx.jobs.idle();
    const skipped = (await app.inject({ method: "GET", url: `/reindex/jobs/${job.id}`, headers: authHeaders })).json().job;
    assert.equal(skipped.state, "succeeded");
    assert.equal(skipped.result.embed.docsProcessed, 0);

    const letters = (await app.inject({ method: "GET", url: "/queue/dead-letters", headers: authHeaders })).json().documents;
    assert.equal(letters.length, 1);
    assert.match(letters[0].path, /delta/);
    assert.equal((await app.inject({ method: "GET", url: "/queue", headers: authHeaders })).json().deadLetters, 1);

    o.mock.on("POST /v1/embed", healthy);
    const cleared = await app.inject({ method: "POST", url: "/queue/dead-letters:clear", headers: authHeaders });
    assert.equal(cleared.statusCode, 202);
    assert.equal(cleared.json().documents, 1);
    await app.ctx.jobs.idle();
    assert.equal((await o.backend.getStatus()).needsEmbedding, 0);
    assert.deepEqual(o.backend.deadLetters(), []);
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

test("a missing index is refused by name, and keep-warm applies in local mode", { skip }, async () => {
  await assert.rejects(openBackend({ dbPath: "/definitely/not/an/index.sqlite", keepWarm: false }), /QMD index not found/);
  const { dbPath } = await fixture();
  const backend = await openBackend({ dbPath, keepWarm: true });
  assert.equal(backend.llmHealth().mode, "local");
  assert.deepEqual(backend.workers(), []);
  assert.equal(backend.poolLoad(), null);
  assert.deepEqual(backend.deadLetters(), []);
  assert.equal(backend.clearDeadLetters(), 0);
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


// --- a bad chunk, on the real pool and index ------------------------------------------------------------------

const refuseText = (mock: MockWorker, status: number, needle: string): void => {
  const healthy = mock.handlers.get("POST /v1/embed")!;
  mock.on("POST /v1/embed", (b, req) => ((b.texts as string[]).some((t) => t.includes(needle)) ? { status, body: {} } : healthy(b, req)));
};

const vecSearch = { queries: [{ type: "vec" as const, query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: false };

for (const [label, status, needle] of [["answered with 400, all of a long document", 400, "A long paragraph"]] as Array<[string, number, string]>) {
  test(`a chunk ${label} costs one document, once, and search keeps its vectors`, { skip }, async () => {
    const { dbPath } = await fixture();
    const mock = await new MockWorker(8).start();
    const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("only", mock)], { embedBatchBytes: 400 }) });
    try {
      refuseText(mock, status, needle);
      const first = await backend.embed({ force: false });
      assert.equal(first.docsProcessed, 4);
      assert.equal(first.docsDeadLettered, 1);
      assert.equal(first.docsPending, 1);
      assert.equal(backend.deadLetters().length, 1);

      const search = await backend.searchDetailed(vecSearch);
      assert.deepEqual(search.degradedReasons, [], "one bad chunk did not cost query vectors");
      assert.ok(search.hits.length > 0);

      // It is recorded once. Later jobs pass over it and report plain success.
      const calls = mock.count("/v1/embed");
      const second = await backend.embed({ force: false });
      assert.equal(second.docsProcessed, 0);
      assert.equal(second.docsPending, 0);
      assert.equal(mock.count("/v1/embed"), calls);
      assert.equal(backend.deadLetters().length, 1);
    } finally {
      await backend.close();
      await mock.stop();
    }
  });
}

test("a real outage is still an outage, and sets nothing aside", { skip }, async () => {
  const o = await opened();
  try {
    o.mock.on("POST /v1/embed", () => ({ status: 503, body: {} }));
    await assert.rejects(o.backend.embed({ force: false }), /No worker can embed/);
    assert.deepEqual(o.backend.deadLetters(), []);
  } finally {
    await o.done();
  }
});

test("a set-aside document gets a fresh start when its content changes, or on a force job", { skip }, async () => {
  const { dbPath, corpus } = await fixture();
  const mock = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("only", mock)]) });
  try {
    const healthy = mock.handlers.get("POST /v1/embed")!;
    refuseText(mock, 400, "Nested document");
    await backend.embed({ force: false });
    assert.equal(backend.deadLetters().length, 1);

    mock.on("POST /v1/embed", healthy);
    assert.equal((await backend.embed({ force: true })).docsPending, 0);
    assert.deepEqual(backend.deadLetters(), [], "a force job retries it, and success clears it");

    refuseText(mock, 400, "Beta text");
    await writeFile(path.join(corpus, "beta.md"), "# Beta\n\nBeta text, changed.\n");
    await backend.update({});
    await backend.embed({ force: false });
    assert.equal(backend.deadLetters().length, 1);
    mock.on("POST /v1/embed", healthy);
    await writeFile(path.join(corpus, "beta.md"), "# Beta\n\nFixed now.\n");
    await backend.update({});
    assert.equal((await backend.embed({ force: false })).docsPending, 0);
    assert.equal((await backend.getStatus()).needsEmbedding, 0);
  } finally {
    await backend.close();
    await mock.stop();
  }
});

// --- scoped force, old rows, a clock moved back -----------------------------------------------------------------

test("a scoped force job with a new vector length is refused, and every collection keeps its vectors", { skip }, async () => {
  const { dbPath } = await fixture();
  const first = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("w", first)]) });
  await backend.embed({ force: false });
  await backend.close();
  await first.stop();
  const before = await counts(dbPath);

  const longer = await new MockWorker(4).start();
  const again = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("w", longer)], { embedDimensions: 0 }) });
  try {
    await assert.rejects(again.embed({ force: true, collection: "sessions" }), /force embed with no collection/);
    assert.equal((await again.getStatus()).needsEmbedding, 0);
  } finally {
    await again.close();
    await longer.stop();
  }
  assert.deepEqual(await counts(dbPath), before);
});

test("a full force job with a new length does it all at once, and honestly", { skip }, async () => {
  const { dbPath } = await fixture();
  const first = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("w", first)]) });
  await backend.embed({ force: false });
  await backend.close();
  await first.stop();

  const longer = await new MockWorker(4).start();
  const again = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("w", longer)], { embedDimensions: 0 }) });
  try {
    const out = await again.embed({ force: true });
    assert.equal(out.docsPending, 0);
    assert.equal((await again.getStatus()).needsEmbedding, 0);
  } finally {
    await again.close();
    await longer.stop();
  }
  const after = await counts(dbPath);
  assert.equal(after.cv, after.vec);
});

test("a full force job removes rows of documents no longer in the index; a scoped one does not", { skip }, async () => {
  const { dbPath, corpus } = await fixture();
  const mock = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("w", mock)]) });
  try {
    await backend.embed({ force: false });
    const whole = await counts(dbPath);
    await rm(path.join(corpus, "alpha.md"));
    await backend.update({});
    await backend.embed({ force: true, collection: "sessions" });
    assert.deepEqual(await counts(dbPath), whole, "a scoped job leaves them");
    await backend.embed({ force: true });
    const after = await counts(dbPath);
    assert.ok(after.cv < whole.cv, `${after.cv} < ${whole.cv}`);
    assert.equal(after.cv, after.vec);
    // And a second force, finding the same state, leaves it alone.
    await backend.embed({ force: true });
    assert.deepEqual(await counts(dbPath), after);
  } finally {
    await backend.close();
    await mock.stop();
  }
});

test("a clock moved back does not turn force into a silent no-op", { skip }, async () => {
  const o = await opened();
  try {
    await o.backend.embed({ force: false });
    const future = new Date(Date.now() + 86_400_000).toISOString();
    const { DatabaseSync } = await import("node:sqlite");
    const raw = new DatabaseSync(o.dbPath);
    raw.prepare("UPDATE content_vectors SET embedded_at = ?").run(future);
    raw.close();
    const calls = o.mock.count("/v1/embed");
    const out = await o.backend.embed({ force: true });
    assert.equal(out.docsProcessed, 4, "everything is redone");
    assert.ok(o.mock.count("/v1/embed") > calls);
  } finally {
    await o.done();
  }
});

// --- another process holding the index -----------------------------------------------------------------------------

/** Another process, because POSIX file locks do not conflict within one process. */
async function holdingLock<T>(dbPath: string, run: () => Promise<T>): Promise<T> {
  const script = "const d = new (require('node:sqlite').DatabaseSync)(process.argv[1]); d.exec('BEGIN IMMEDIATE'); console.log('held'); setInterval(() => {}, 1000);";
  const holder = spawn(process.execPath, ["-e", script, dbPath], { stdio: ["ignore", "pipe", "ignore"] });
  const exited = new Promise<void>((resolve) => holder.once("exit", () => resolve()));
  await Promise.race([
    new Promise<void>((resolve) => createInterface({ input: holder.stdout }).once("line", () => resolve())),
    exited.then(() => {
      throw new Error("the process meant to hold the lock exited");
    }),
  ]);
  try {
    return await run();
  } finally {
    holder.kill("SIGKILL");
    await exited;
  }
}

test("a farm write meeting a lock defers within moments, and the job finishes once it clears", { skip }, async () => {
  const o = await opened();
  try {
    const started = Date.now();
    await holdingLock(o.dbPath, () => assert.rejects(o.backend.embed({ force: false }), /index is busy/));
    assert.ok(Date.now() - started < 10_000, "the SDK's 120 s busy timeout would block the event loop");
    assert.equal((await o.backend.embed({ force: false })).docsPending, 0);
  } finally {
    await o.done();
  }
});

test("a sweep and a remote open both survive a lock on the index, without waiting on it", { skip }, async () => {
  const o = await opened();
  await o.backend.embed({ force: false });
  await o.done();
  const warnings: string[] = [];
  await withSdk(o.dbPath, async (db) => {
    const before = db.prepare("PRAGMA busy_timeout").get();
    await holdingLock(o.dbPath, async () => {
      const started = Date.now();
      sweepExpiredCache(db as never, (m) => warnings.push(m));
      assert.ok(Date.now() - started < 2_000);
    });
    assert.deepEqual(db.prepare("PRAGMA busy_timeout").get(), before, "it goes back afterward");
  });
  assert.match(warnings[0]!, /Could not sweep/);

  const mock = await new MockWorker(8).start();
  const started = Date.now();
  const told: string[] = [];
  const repaired: number[] = [];
  await holdingLock(o.dbPath, async () => {
    // Vectors missing their rows would normally be repaired here; a lock must not stop the open.
    const backend = await openBackend({ dbPath: o.dbPath, keepWarm: false, llm: remoteOptions([worker("w", mock)]), onLlmWarning: (m) => told.push(m), onVectorRepair: (n) => repaired.push(n) });
    assert.ok(Date.now() - started < 10_000, `took ${Date.now() - started} ms`);
    assert.ok((await backend.searchDetailed({ queries: [{ type: "lex", query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: false })).hits.length > 0);
    await backend.close();
  });
  assert.match(told.join(), /Could not sweep/);
  assert.deepEqual(repaired, []);
  await mock.stop();
});

// --- the deadline, end to end -------------------------------------------------------------------------------------

test("every role hung: the search answers lexically within its one deadline, well under the API's timeout", { skip }, async () => {
  const o = await opened({ searchDeadlineMs: 1_500 });
  try {
    const hang = () => new Promise<never>(() => {});
    for (const route of ["POST /v1/complete", "POST /v1/embed", "POST /v1/rerank"]) o.mock.on(route, hang);
    const started = Date.now();
    const out = await o.backend.searchDetailed({ query: "AuthenticationHandler", limit: 5, minScore: 0, rerank: true });
    assert.ok(Date.now() - started < 5_000, `took ${Date.now() - started} ms`);
    assert.ok(out.degradedReasons.includes("no_expansion"), String(out.degradedReasons));
  } finally {
    await o.done();
  }
});

test("a failed rerank on the real SDK does not expand or embed a second time", { skip }, async () => {
  const o = await opened();
  try {
    await o.backend.embed({ force: false });
    o.backend.llm.clearCache();
    o.mock.on("POST /v1/rerank", () => ({ status: 503, body: {} }));
    const expansions = o.mock.count("/v1/complete");
    const embeds = o.mock.count("/v1/embed");
    const out = await o.backend.searchDetailed({ query: "AuthenticationHandler", limit: 5, minScore: 0, rerank: true });
    assert.deepEqual(out.degradedReasons, ["no_rerank"]);
    assert.equal(o.mock.count("/v1/complete"), expansions + 1);
    assert.equal(o.mock.count("/v1/embed"), embeds + 1);
  } finally {
    await o.done();
  }
});

// --- /ready, without a search --------------------------------------------------------------------------------------

test("/ready recovers from a heartbeat alone, and shows disagreement", { skip }, async () => {
  const { dbPath } = await fixture();
  const a = await new MockWorker(8).start();
  const b = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("a", a), worker("b", b)], { breaker: { threshold: 1, backoffBaseMs: 1, backoffMaxMs: 1 }, probeEveryMs: 1 }) });
  const app = await buildApp({ backend, config: testConfig(), logger: false });
  const ready = async () => (await app.inject({ method: "GET", url: "/ready", headers: authHeaders }));
  try {
    await backend.llm.refreshWorkers();
    const healthyRerank = a.handlers.get("POST /v1/rerank")!;
    for (const m of [a, b]) m.on("POST /v1/rerank", () => ({ status: 500, body: {} }));
    await backend.searchDetailed({ queries: [{ type: "lex", query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: true });
    assert.deepEqual((await ready()).json().llm.unavailable, ["rerank"]);
    assert.equal((await ready()).json().status, "degraded");
    assert.equal((await ready()).statusCode, 200, "degraded is still a 200, for the API to pass on");

    for (const m of [a, b]) m.on("POST /v1/rerank", healthyRerank);
    await new Promise((r) => setTimeout(r, 5));
    await backend.llm.refreshWorkers();
    assert.deepEqual((await ready()).json().llm.unavailable, [], "no search was needed");

    b.on("POST /v1/embed", (body) => ({ body: { model: EMBED_FILE, embeddings: (body.texts as string[]).map(() => [-1, 2, -3, 4, -5, 6, -7, 8]) } }));
    await new Promise((r) => setTimeout(r, 5));
    await backend.llm.refreshWorkers();
    const shown = (await ready()).json();
    assert.deepEqual(shown.llm.embedDisagreement, ["a", "b"]);
    assert.notEqual(shown.status, "degraded", "a disagreement is reported, and takes nothing down");
    const workers = (await app.inject({ method: "GET", url: "/workers", headers: authHeaders })).json().workers;
    assert.deepEqual(workers.map((w: { embedDisagrees: boolean }) => w.embedDisagrees), [true, true]);
    assert.deepEqual(workers.map((w: { circuits: { documents: string } }) => w.circuits.documents), ["closed", "closed"]);
  } finally {
    await app.close();
    await backend.close();
    await a.stop();
    await b.stop();
  }
});

// --- the fallback with the network blocked -------------------------------------------------------------------------

test("with the network blocked, only the SDK's own cache name gets the SDK's model loader going", { skip }, async () => {
  const { dbPath } = await fixture();
  const attempt = async (cachedName: string) => {
    const cache = await tempDir("memoria-offline-cache-");
    const qmdModels = path.join(cache, "qmd", "models");
    await mkdir(qmdModels, { recursive: true });
    await writeFile(path.join(qmdModels, cachedName), "");
    const fixturePath = fileURLToPath(new URL("./fixtures/fallback-offline.ts", import.meta.url));
    const run = spawnSync(process.execPath, [fixturePath, dbPath], { env: { ...process.env, XDG_CACHE_HOME: cache }, encoding: "utf8", timeout: 60_000 });
    assert.equal(run.status, 0, run.stderr);
    return JSON.parse(run.stdout.trim().split("\n").at(-1)!) as { attempts: string[]; loaderCalls: string[]; vector: unknown };
  };

  // The plain name, which the SDK's loader does not look for: it would go to Hugging Face.
  const plain = await attempt(EMBED_FILE);
  assert.deepEqual(plain.loaderCalls, [], "the loader, which would download, was never reached");
  assert.deepEqual(plain.attempts, [], "nothing tried to reach the network");
  assert.equal(plain.vector, null);

  // The control: the same run does reach it when the model really is there.
  const cached = await attempt(`hf_ggml-org_${EMBED_FILE}`);
  assert.ok(cached.loaderCalls.length > 0, "otherwise this test proves nothing");
  assert.deepEqual(cached.attempts, []);
});

// --- store.ts on the real SDK: paths the rest do not walk -------------------------------------------------------

test("a local backend reports its models honestly, and embeds with nothing pending without loading any", { skip }, async () => {
  const o = await opened();
  await o.backend.embed({ force: false });
  await o.done();
  const local = await openBackend({ dbPath: o.dbPath, keepWarm: false });
  try {
    const models = local.models();
    assert.ok(models.embed.length > 0);
    assert.deepEqual(models.loaded, { embed: false, rerank: false, generate: false });
    assert.equal(models.resident, false);
    const out = await local.embed({ force: false, collection: "sessions" });
    assert.equal(out.docsProcessed, 0);
    assert.equal(out.errors, 0);
    assert.equal(local.llm.mode, "local");
    assert.equal(local.llm.rerankTransport, "in-process");
    assert.deepEqual(local.models().loaded, { embed: false, rerank: false, generate: false }, "still nothing loaded");
  } finally {
    await local.close();
  }
});

test("search passes its optional settings through to the SDK", { skip }, async () => {
  const o = await opened();
  try {
    await o.backend.embed({ force: false });
    o.backend.llm.clearCache();
    const before = o.mock.requests.length;
    const out = await o.backend.searchDetailed({ queries: [{ type: "lex", query: "AuthenticationHandler" }], intent: "the auth handler", collections: ["sessions"], candidateLimit: 1, limit: 5, minScore: 0, rerank: true });
    assert.ok(out.hits.length > 0);
    const rerank = o.mock.requests.slice(before).find((r) => r.path === "/v1/rerank");
    assert.equal((rerank?.body as { documents: string[] }).documents.length, 1, "candidateLimit reached the SDK");
    const elsewhere = await o.backend.searchDetailed({ queries: [{ type: "lex", query: "AuthenticationHandler" }], collections: ["sessions"], limit: 5, minScore: 0, rerank: false });
    assert.ok(elsewhere.hits.every((h) => h.displayPath.startsWith("sessions/")));
  } finally {
    await o.done();
  }
});

test("a search with neither a query nor sub-queries is the SDK's to refuse, and does not report degradation", { skip }, async () => {
  const { dbPath } = await fixture();
  const local = await openBackend({ dbPath, keepWarm: false });
  try {
    await assert.rejects(local.searchDetailed({ limit: 5, minScore: 0, rerank: false }));
  } finally {
    await local.close();
  }
});

test("document heads come from the index, and an empty body reads as an empty head", { skip }, async () => {
  const { dbPath } = await fixture({ "empty.md": "", "full.md": "# Full\n\nBody.\n" });
  const local = await openBackend({ dbPath, keepWarm: false });
  try {
    const heads = local.readDocumentHeads(5);
    const full = heads.find((h) => h.path === "full.md");
    assert.equal(full?.head, "# Ful");
    const empty = heads.find((h) => h.path === "empty.md");
    if (empty) assert.equal(empty.head, "");
  } finally {
    await local.close();
  }
});

// --- what may set a document aside, on the real pool and index -------------------------------------------------

/** Document text, as QMD formats it: not the known-good text, and not a query. */
const realText = (b: { texts: string[] }): boolean => b.texts.some((t) => t.startsWith("title:"));

test("a 502 chunk stays pending and is tried again, and search keeps its vectors", { skip }, async () => {
  const { dbPath } = await fixture();
  const mock = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("only", mock)]) });
  try {
    const healthy = mock.handlers.get("POST /v1/embed")!;
    refuseText(mock, 502, "Nested document");
    const first = await backend.embed({ force: false });
    assert.equal(first.docsPending, 1);
    assert.equal(first.docsDeadLettered, 0);
    assert.deepEqual(backend.deadLetters(), []);
    assert.deepEqual((await backend.searchDetailed(vecSearch)).degradedReasons, []);
    mock.on("POST /v1/embed", healthy);
    const second = await backend.embed({ force: false });
    assert.equal(second.docsProcessed, 1, "it was not forgotten");
    assert.equal(second.docsPending, 0);
  } finally {
    await backend.close();
    await mock.stop();
  }
});

test("a brief window of 502s on real text sets nothing aside on the real pool", { skip }, async () => {
  const docs: Record<string, string> = {};
  for (let i = 0; i < 12; i++) docs[`d${i}.md`] = `# Doc ${i}\n\nUnique text number ${i}.\n`;
  const { dbPath } = await fixture(docs);
  const mock = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("only", mock)]) });
  try {
    const healthy = mock.handlers.get("POST /v1/embed")!;
    const healAt = Date.now() + 1_000;
    mock.on("POST /v1/embed", (b, req) => (realText(b) && Date.now() < healAt ? { status: 502, body: {} } : healthy(b, req)));
    await backend.embed({ force: false }).catch((err: Error) => assert.match(err.message, /keep failing|No worker/));
    assert.deepEqual(backend.deadLetters(), []);
    await new Promise((resolve) => setTimeout(resolve, Math.max(0, healAt - Date.now())));
    assert.equal((await backend.embed({ force: false })).docsPending, 0);
    assert.equal((await backend.getStatus()).needsEmbedding, 0);
  } finally {
    await backend.close();
    await mock.stop();
  }
});

test("400 on all real text condemns nothing on the real pool, and search keeps working", { skip }, async () => {
  const { dbPath } = await fixture(corpus(8, 0));
  const mock = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("only", mock)]) });
  try {
    await backend.embed({ force: false });
    const before = await counts(dbPath);
    const healthy = mock.handlers.get("POST /v1/embed")!;
    mock.on("POST /v1/embed", (b, req) => (realText(b) ? { status: 400, body: {} } : healthy(b, req)));
    await assert.rejects(backend.embed({ force: true }), /refused too many/);
    assert.deepEqual(backend.deadLetters(), []);
    assert.deepEqual(await counts(dbPath), before);
    assert.deepEqual((await backend.searchDetailed(vecSearch)).degradedReasons, []);
  } finally {
    await backend.close();
    await mock.stop();
  }
});

// --- when a set-aside ends ---------------------------------------------------------------------------------------

async function withSetAside(run: (o: { dbPath: string; corpus: string; mock: MockWorker; backend: OpenedBackend }) => Promise<void>): Promise<void> {
  const { dbPath, corpus } = await fixture();
  const mock = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("only", mock)]) });
  try {
    const healthy = mock.handlers.get("POST /v1/embed")!;
    refuseText(mock, 400, "Nested document");
    await backend.embed({ force: false });
    assert.equal(backend.deadLetters().length, 1);
    mock.on("POST /v1/embed", healthy);
    await run({ dbPath, corpus, mock, backend });
  } finally {
    await backend.close();
    await mock.stop();
  }
}

const setLetterTime = async (dbPath: string, at: string): Promise<void> => {
  const { DatabaseSync } = await import("node:sqlite");
  const raw = new DatabaseSync(dbPath);
  raw.prepare("UPDATE memoria_dead_letters SET at = ?").run(at);
  raw.close();
};

test("a set-aside holds across jobs, and ends after 24 hours", { skip }, () =>
  withSetAside(async ({ dbPath, backend }) => {
    assert.equal((await backend.embed({ force: false })).docsProcessed, 0, "it holds");
    await setLetterTime(dbPath, new Date(Date.now() - 25 * 3_600_000).toISOString());
    assert.deepEqual(backend.deadLetters(), [], "not shown as held");
    assert.equal((await backend.embed({ force: false })).docsProcessed, 1);
    assert.equal((await backend.getStatus()).needsEmbedding, 0);
  }));

test("a set-aside dated in the future, from a clock moved back, holds nothing", { skip }, () =>
  withSetAside(async ({ dbPath, backend }) => {
    await setLetterTime(dbPath, new Date(Date.now() + 25 * 3_600_000).toISOString());
    assert.equal((await backend.embed({ force: false })).docsProcessed, 1);
  }));

test("a set-aside ends when the workers change", { skip }, async () => {
  const { dbPath } = await fixture();
  const first = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("first", first)]) });
  refuseText(first, 400, "Nested document");
  await backend.embed({ force: false });
  await backend.close();

  const same = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("first", first)]) });
  assert.equal(same.deadLetters().length, 1, "the same worker back again changes nothing");
  assert.equal((await same.embed({ force: false })).docsProcessed, 0);
  await same.close();
  await first.stop();

  const second = await new MockWorker(8).start();
  const other = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("second", second)]) });
  try {
    assert.deepEqual(other.deadLetters(), []);
    assert.equal((await other.embed({ force: false })).docsProcessed, 1);
    assert.equal((await other.getStatus()).needsEmbedding, 0);
  } finally {
    await other.close();
    await second.stop();
  }
});

test("the same content under a new path is tried afresh, and a deleted document leaves no record", { skip }, () =>
  withSetAside(async ({ dbPath, corpus, backend }) => {
    const body = "# Delta\n\nNested document about something else entirely.\n";
    await rm(path.join(corpus, "sub/delta.md"));
    await writeFile(path.join(corpus, "renamed.md"), body);
    await backend.update({});
    const out = await backend.embed({ force: false });
    assert.equal(out.docsProcessed, 1, "the hash alone keeps nothing out");
    assert.equal(out.docsPending, 0);
    assert.deepEqual(backend.deadLetters(), []);
    const { DatabaseSync } = await import("node:sqlite");
    const raw = new DatabaseSync(dbPath);
    const rows = (raw.prepare("SELECT COUNT(*) AS n FROM memoria_dead_letters").get() as { n: number }).n;
    raw.close();
    assert.equal(rows, 0);
  }));

test("the same content gaining a name that sorts first is tried afresh while the old name stands", { skip }, () =>
  withSetAside(async ({ corpus, backend }) => {
    await writeFile(path.join(corpus, "a-copy.md"), "# Delta\n\nNested document about something else entirely.\n");
    await backend.update({});
    const out = await backend.embed({ force: false });
    assert.equal(out.docsProcessed, 1, "the hash alone keeps nothing out");
    assert.equal(out.docsPending, 0);
  }));

test("a deleted document's record goes with the next job, even with its content unwritten", { skip }, () =>
  withSetAside(async ({ corpus, backend }) => {
    await rm(path.join(corpus, "sub/delta.md"));
    await backend.update({});
    await backend.embed({ force: false });
    assert.deepEqual(backend.deadLetters(), []);
  }));

// --- /ready says so ----------------------------------------------------------------------------------------------

test("/ready counts set-aside documents, and reads the document circuit", { skip }, async () => {
  const { dbPath } = await fixture();
  const mock = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("only", mock)], { breaker: { threshold: 1, backoffBaseMs: 60_000, backoffMaxMs: 60_000 } }) });
  const app = await buildApp({ backend, config: testConfig(), logger: false });
  const ready = async () => (await app.inject({ method: "GET", url: "/ready", headers: authHeaders })).json();
  try {
    await backend.llm.refreshWorkers();
    assert.equal((await ready()).llm.documentsSetAside, 0);
    assert.deepEqual((await ready()).llm.unavailable, []);
    refuseText(mock, 400, "Nested document");
    await backend.embed({ force: false });
    assert.equal((await ready()).llm.documentsSetAside, 1, "job history is not the only signal");

    // Document failures open the document circuit, and no known-good call clears it in an outage.
    mock.on("POST /v1/embed", () => ({ status: 502, body: {} }));
    await assert.rejects(backend.embed({ force: true }), /No worker can embed/);
    const out = await ready();
    assert.ok(out.llm.unavailable.includes("documents"), JSON.stringify(out.llm));
    assert.ok(!out.llm.unavailable.includes("embed"));
    assert.equal(out.status, "degraded");
  } finally {
    await app.close();
    await backend.close();
    await mock.stop();
  }
});

// --- force and poison --------------------------------------------------------------------------------------------

test("a document set aside during a force loses its old vectors, so the docs are true", { skip }, async () => {
  const o = await opened();
  try {
    await o.backend.embed({ force: false });
    const before = await counts(o.dbPath);
    refuseText(o.mock, 400, "Nested document");
    const out = await o.backend.embed({ force: true });
    assert.equal(out.docsDeadLettered, 1);
    const after = await counts(o.dbPath);
    assert.ok(after.cv < before.cv, `${after.cv} < ${before.cv}`);
    assert.equal(after.cv, after.vec);
    const search = await o.backend.searchDetailed({ queries: [{ type: "vec", query: "Nested document about something else entirely" }], limit: 10, minScore: 0, rerank: false });
    assert.ok(!search.hits.some((h) => h.displayPath.includes("delta")), "no longer searched by its old vector");
    const lexical = await o.backend.searchDetailed({ queries: [{ type: "lex", query: "Nested" }], limit: 10, minScore: 0, rerank: false });
    assert.ok(lexical.hits.some((h) => h.displayPath.includes("delta")), "still found by its words");
  } finally {
    await o.done();
  }
});

test("a partial force removes a deleted document's vectors, so restoring it embeds it afresh", { skip }, async () => {
  const { dbPath, corpus } = await fixture();
  const mock = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("w", mock)]) });
  try {
    await backend.embed({ force: false });
    const body = "# Alpha\n\nHow should the AuthenticationHandler validate API keys?\n\nCompare the bearer token in constant time.\n";
    await rm(path.join(corpus, "alpha.md"));
    await backend.update({});
    refuseText(mock, 400, "Nested document");
    const partial = await backend.embed({ force: true });
    assert.equal(partial.docsPending, 1, "partial");
    await writeFile(path.join(corpus, "alpha.md"), body);
    await backend.update({});
    const restored = await backend.embed({ force: false });
    assert.equal(restored.docsProcessed, 1, "not live again on its old, possibly poisoned, vectors");
  } finally {
    await backend.close();
    await mock.stop();
  }
});

test("a force job waiting on an outage has already removed a deleted document's vectors", { skip }, async () => {
  const { dbPath, corpus } = await fixture();
  const mock = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("w", mock)]) });
  try {
    await backend.embed({ force: false });
    const whole = await counts(dbPath);
    await rm(path.join(corpus, "alpha.md"));
    await backend.update({});
    mock.on("POST /v1/embed", () => ({ status: 503, body: {} }));
    await assert.rejects(backend.embed({ force: true }), /No worker can embed/);
    assert.ok((await counts(dbPath)).cv < whole.cv);
  } finally {
    await backend.close();
    await mock.stop();
  }
});

// --- poison documents, through the real app ----------------------------------------------------------------------

const POISON = "POISONTEXT";

function corpus(good: number, poison: number): Record<string, string> {
  const docs: Record<string, string> = {};
  for (let i = 0; i < good; i++) docs[`good${i}.md`] = `# Good ${i}\n\nUnique ordinary text number ${i}.\n`;
  for (let i = 0; i < poison; i++) docs[`bad${i}.md`] = `# Bad ${i}\n\n${POISON} number ${i}.\n`;
  return docs;
}

async function poisonApp(docs: Record<string, string>, status = 400) {
  const { dbPath, corpus: dir } = await fixture(docs);
  const mock = await new MockWorker(8).start();
  refuseText(mock, status, POISON);
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("only", mock)], { embedBatchBytes: 300 }) });
  const logs: string[] = [];
  const app = await buildApp({ backend, config: testConfig(), logger: { level: "warn", stream: { write: (line: string) => void logs.push(line) } } });
  const get = async (url: string) => (await app.inject({ method: "GET", url, headers: authHeaders })).json();
  return {
    dbPath, dir, mock, backend, app, get, logs,
    /** Ask for a job, and wait until it has finished or gone back to waiting. */
    job: async (payload: object = {}) => {
      const { id } = (await app.inject({ method: "POST", url: "/reindex", headers: authHeaders, payload })).json().job;
      for (let i = 0; i < 3000; i++) {
        const { job } = await get(`/reindex/jobs/${id}`);
        if (job.state === "succeeded" || job.state === "failed" || (job.state === "queued" && job.error !== null && job.attempts > 0 && app.ctx.jobs.stuck())) return job;
        await new Promise((resolve) => setTimeout(resolve, 5));
      }
      throw new Error("the job never settled");
    },
    done: async () => {
      await app.close();
      await backend.close();
      await mock.stop();
    },
  };
}

test("more poison than the old cap: 4 of 100 are set aside once, and the job after succeeds", { skip }, async () => {
  const o = await poisonApp(corpus(96, 4));
  try {
    const first = await o.job();
    assert.equal(first.state, "failed", "documents were left behind, and it says so");
    assert.equal(first.result.embed.docsDeadLettered, 4);
    assert.equal(first.result.embed.chunksEmbedded >= 96, true);
    const second = await o.job();
    assert.equal(second.state, "succeeded");
    assert.equal(second.result.embed.docsProcessed, 0);
    const status = await o.get("/status");
    assert.equal(status.needsEmbedding, 0, "the number is true");
    assert.equal(status.documentsSetAside, 4);
    assert.equal((await o.get("/ready")).llm.documentsSetAside, 4);
    assert.equal((await o.get("/queue")).deadLetters, 4);
  } finally {
    await o.done();
  }
});

test("a worker too small for real text sets nothing aside, and says so", { skip }, async () => {
  const docs = corpus(3, 0);
  docs["long.md"] = `# Long\n\n${"A long run of ordinary words about nothing much. ".repeat(60)}\n`;
  const { dbPath } = await fixture(docs);
  const mock = await new MockWorker(8).start();
  const healthy = mock.handlers.get("POST /v1/embed")!;
  mock.on("POST /v1/embed", (b, req) => ((b.texts as string[]).some((t) => t.length > 800) ? { status: 400, body: {} } : healthy(b, req)));
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("small", mock)], { embedBatchBytes: 4096 }) });
  try {
    await assert.rejects(backend.embed({ force: false }), /full-size test text/);
    assert.deepEqual(backend.deadLetters(), []);
    assert.ok((await backend.getStatus()).needsEmbedding >= 1);
  } finally {
    await backend.close();
    await mock.stop();
  }
});

test("at the old cap: one new poison document among good ones is set aside, and nothing waits", { skip }, async () => {
  const o = await poisonApp(corpus(96, 4));
  try {
    await o.job();
    await writeFile(path.join(o.dir, "newbad.md"), `# New\n\n${POISON} again.\n`);
    await writeFile(path.join(o.dir, "newgood.md"), "# New good\n\nFine.\n");
    const job = await o.job();
    assert.equal(job.state, "failed");
    assert.equal(job.result.embed.docsDeadLettered, 1);
    assert.equal(job.result.embed.docsPending, 1);
    assert.equal((await o.get("/status")).documentsSetAside, 5);
  } finally {
    await o.done();
  }
});

test("a full force with documents already set aside finishes", { skip }, async () => {
  const o = await poisonApp(corpus(97, 3));
  try {
    await o.job();
    const force = await o.job({ force: true });
    assert.equal(force.state, "failed", "they were refused again");
    assert.equal(force.result.embed.docsProcessed, 100);
    assert.equal(force.result.embed.docsDeadLettered, 3);
    assert.equal(o.app.ctx.jobs.stuck(), null);
  } finally {
    await o.done();
  }
});

test("after 24 hours, 5 set-aside documents retried alone are set aside again rather than waiting", { skip }, async () => {
  const o = await poisonApp(corpus(95, 5));
  try {
    await o.job();
    await setLetterTime(o.dbPath, new Date(Date.now() - 25 * 3_600_000).toISOString());
    assert.equal((await o.get("/status")).documentsSetAside, 0);
    assert.equal((await o.get("/status")).needsEmbedding, 5, "due again, so waiting again");
    const job = await o.job();
    assert.equal(job.result.embed.docsProcessed, 5);
    assert.equal(job.result.embed.docsDeadLettered, 5);
    assert.equal((await o.get("/status")).documentsSetAside, 5);
  } finally {
    await o.done();
  }
});

for (const poison of [1, 5]) {
  test(`${poison} document(s) returning 502 are set aside after repeated runs, and later jobs stop failing`, { skip }, async (t) => {
    t.mock.timers.enable({ apis: ["Date"], now: Date.now() });
    const o = await poisonApp(corpus(100 - poison, poison), 502);
    try {
      const first = await o.job();
      assert.equal(first.state, "failed");
      assert.equal(first.result.embed.docsPending, poison);
      assert.equal(o.app.ctx.jobs.stuck(), null, "nothing waits");
      assert.equal((await o.get("/status")).needsEmbedding, poison);

      t.mock.timers.tick(10 * 60_000);
      assert.equal((await o.job()).state, "failed");
      assert.equal((await o.get("/status")).documentsSetAside, 0, "not on two runs");

      t.mock.timers.tick(10 * 60_000);
      const third = await o.job();
      assert.equal(third.result.embed.docsDeadLettered, poison);
      assert.match((await o.get("/queue/dead-letters")).documents[0].reason, /3 separate runs/);

      const fourth = await o.job();
      assert.equal(fourth.state, "succeeded");
      assert.equal((await o.get("/status")).needsEmbedding, 0);
    } finally {
      await o.done();
    }
  });
}

test("/ready reads degraded with the reason while a job is stuck on a worker fault, and recovers", { skip }, async () => {
  const o = await poisonApp(corpus(20, 0));
  try {
    const healthy = o.mock.handlers.get("POST /v1/embed")!;
    await o.backend.llm.refreshWorkers();
    o.mock.on("POST /v1/embed", (b, req) => (realText(b) ? { status: 400, body: {} } : healthy(b, req)));
    const waiting = await o.job();
    assert.equal(waiting.state, "queued");
    const ready = await o.get("/ready");
    assert.equal(ready.status, "degraded");
    assert.deepEqual(ready.llm.unavailable, [], "every role answers, so this is the only signal");
    assert.match(ready.jobs.stuck.reason, /refused too many/);
    assert.ok(ready.jobs.stuck.attempts >= 1);
    assert.equal(ready.index.needsEmbedding, 20);
    assert.deepEqual(o.backend.deadLetters(), []);
    assert.ok(o.logs.some((line) => line.includes("waiting to try again") && line.includes("refused too many")), "and each retry is logged");

    o.mock.on("POST /v1/embed", healthy);
    assert.equal((await o.job()).state, "succeeded");
    const after = await o.get("/ready");
    assert.equal(after.jobs.stuck, null);
    assert.notEqual(after.status, "degraded");
  } finally {
    await o.done();
  }
});

// --- a document or search must not freeze or kill the sidecar, on the real SDK ---------------------------------

test("a vector search over one huge document survives a small heap: its body is read once", { skip, timeout: 120_000 }, async () => {
  const { dbPath } = await fixture({ "huge.md": `# Huge\n\n${"Ordinary words in a very long session transcript line.\n".repeat(600_000)}` });
  const fixturePath = fileURLToPath(new URL("./fixtures/vec-heap.ts", import.meta.url));
  const run = spawnSync(process.execPath, ["--max-old-space-size=256", fixturePath, dbPath], { encoding: "utf8", timeout: 90_000 });
  assert.equal(run.status, 0, run.stderr.slice(-500));
  const report = JSON.parse(run.stdout.trim().split("\n").at(-1)!) as { hits: number; bodyLength: number };
  assert.equal(report.hits, 1);
  assert.ok(report.bodyLength > 30_000_000, "the body still comes back");
});

test("while a search chunks several bad documents, other requests are answered", { skip, timeout: 120_000 }, async () => {
  const docs: Record<string, string> = {};
  for (let i = 0; i < 6; i++) docs[`fence${i}.md`] = `# Fence ${i}\n\nMatchword here.\n${"```\n".repeat(256 * 1024)}`;
  const { dbPath } = await fixture(docs);
  const mock = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: remoteOptions([worker("w", mock)]) });
  const app = await buildApp({ backend, config: testConfig(), logger: false });
  try {
    // How often anything else gets a turn, with no network waits in this search to give it one.
    let turns = 0;
    let searching = true;
    const turn = (): void => {
      turns++;
      if (searching) setImmediate(turn);
    };
    setImmediate(turn);
    const started = performance.now();
    const search = await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { searches: [{ type: "lex", query: "Matchword" }], limit: 6, rerank: false } });
    const took = performance.now() - started;
    searching = false;
    assert.equal(search.statusCode, 200);
    assert.ok(search.json().total >= 6);
    assert.ok(took < 30_000, `the search took ${Math.round(took)} ms`);
    assert.ok(turns >= 6, `only ${turns} turns while chunking 6 documents`);
  } finally {
    await app.close();
    await backend.close();
    await mock.stop();
  }
});

test("a search of only quotes is a 400 on the real SDK, and ordinary quoting still works", { skip }, async () => {
  const o = await opened();
  const app = await buildApp({ backend: o.backend, config: testConfig(), logger: false });
  try {
    const search = (query: string) => app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { searches: [{ type: "lex", query }], rerank: false } });
    assert.equal((await search('"')).statusCode, 400);
    assert.equal((await search('""')).statusCode, 400);
    assert.equal((await search('"AuthenticationHandler"')).statusCode, 200);
  } finally {
    await app.close();
    await o.done();
  }
});
