import assert from "node:assert/strict";
import { existsSync, readdirSync } from "node:fs";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { JobDeferred, JobPartial, JobQueue, type JobResult, type JobStore, type PersistedJob, type ReindexRequest } from "../src/jobs.ts";
import { WorkerClient, WorkerError } from "../src/llm/client.ts";
import { MAX_EXPANSION_QUERIES, parseExpansion } from "../src/llm/expansion.ts";
import { cosineOf } from "../src/llm/probe.ts";
import { acceptedEmbedFiles, PROBE_MIN_COSINE, RemoteLlm, type RemoteLlmOptions } from "../src/llm/remote.ts";
import type { LocalFallback, PoolWorkerConfig, ProbeReference } from "../src/llm/types.ts";
import { parseEmbeddings, ResponseViolation } from "../src/llm/validate.ts";
import { guardedFallback } from "../src/llm/wire.ts";
import { openJobStore, SqliteJobStore } from "../src/queue-store.ts";
import { loadConfig } from "../src/config.ts";
import { ManualScheduler } from "./helpers.ts";
import { EMBED_FILE, MockWorker, vectorFor } from "./mock-worker.ts";

/** Each test here reproduces a failure that once left the system looking healthy. */

const URI = `hf:ggml-org/embeddinggemma-300M-GGUF/${EMBED_FILE}`;
const MODELS = { embed: URI, rerank: "hf:x/r.gguf", generate: "hf:x/g.gguf" };
const result: JobResult = { update: { collections: 1, indexed: 0, updated: 0, unchanged: 0, removed: 0, skipped: 0, needsEmbedding: 0 }, embed: null, metadata: { documents: 0, withMetadata: 0 } };
const req = (over: Partial<ReindexRequest> = {}): ReindexRequest => ({ collections: ["c"], embed: true, force: false, reason: "test", ...over });
const never = new Promise<JobResult>(() => {});
const workerConfig = (name: string, url: string, extra: Partial<PoolWorkerConfig> = {}): PoolWorkerConfig => ({ name, url, token: undefined, capabilities: ["embed", "rerank", "generate"], priority: 1, maxConcurrent: 4, ...extra });
const tempFile = async (name: string) => path.join(await mkdtemp(path.join(tmpdir(), "memoria-failure-")), name);

interface Pair {
  a: MockWorker;
  b: MockWorker;
  llm: RemoteLlm;
  tick(ms: number): void;
  done(): Promise<void>;
}

async function pair(over: Partial<RemoteLlmOptions> = {}, bFile?: string): Promise<Pair> {
  const a = await new MockWorker(8).start();
  const b = await new MockWorker(8, bFile).start();
  let now = 1_000_000;
  const llm = new RemoteLlm({
    workers: [workerConfig("a", a.url, { priority: 1 }), workerConfig("b", b.url, { priority: 2 })],
    embedDimensions: 8,
    models: MODELS,
    fallback: null,
    now: () => now,
    breaker: { threshold: 3, backoffBaseMs: 1_000, backoffMaxMs: 8_000 },
    ...over,
  });
  return {
    a,
    b,
    llm,
    tick: (ms) => (now += ms),
    done: async () => {
      await llm.dispose();
      await a.stop();
      await b.stop();
    },
  };
}

const rerank = (llm: RemoteLlm) => llm.rerank("q", [{ file: "x.md", text: "x" }]);

// --- a worker answering 4xx must not starve its neighbour ---------------------------

for (const status of [404, 410, 501, 405, 422]) {
  test(`a worker answering ${status} fails over and is counted against`, async () => {
    const p = await pair();
    try {
      p.a.on("POST /v1/rerank", () => ({ status, body: {} }));
      for (let i = 0; i < 10; i++) await rerank(p.llm);
      assert.equal(p.b.count("/v1/rerank"), 10, "the good worker did every one");
      assert.equal(p.a.count("/v1/rerank"), 3, "and its circuit stopped the calls");
      assert.equal(p.llm.pool.workers()[0]!.circuits.rerank, "open");
    } finally {
      await p.done();
    }
  });
}

test("a worker answering 400 to everything is eventually treated as broken", async () => {
  const p = await pair();
  try {
    p.a.on("POST /v1/rerank", () => ({ status: 400, body: {} }));
    for (let i = 0; i < 4; i++) await assert.rejects(rerank(p.llm), (e) => e instanceof WorkerError && e.code === "rejected");
    assert.equal(p.b.count("/v1/rerank"), 0, "at first a refusal is taken at its word");
    await rerank(p.llm);
    assert.equal(p.b.count("/v1/rerank"), 1, "then the neighbour gets a chance");
  } finally {
    await p.done();
  }
});

test("a success resets the refusal count, so a few bad requests never add up", async () => {
  const p = await pair();
  try {
    let refuse = true;
    p.a.on("POST /v1/rerank", (b) => (refuse ? { status: 413, body: {} } : { body: { results: (b.documents as string[]).map((_, index) => ({ index, score: 0.5 })) } }));
    for (let round = 0; round < 3; round++) {
      refuse = true;
      for (let i = 0; i < 4; i++) await assert.rejects(rerank(p.llm), (e) => e instanceof WorkerError && e.code === "rejected");
      refuse = false;
      await rerank(p.llm);
    }
    assert.equal(p.b.count("/v1/rerank"), 0);
  } finally {
    await p.done();
  }
});

// --- circuits are per role, and readiness follows them ---------------------------------

test("a failing reranker opens only its own circuit, and other roles cannot reset it", async () => {
  const a = await new MockWorker(8).start();
  const llm = new RemoteLlm({ workers: [workerConfig("a", a.url)], embedDimensions: 8, models: MODELS, fallback: null, breaker: { threshold: 3, backoffBaseMs: 60_000 } });
  try {
    await llm.refreshStatus();
    a.on("POST /v1/rerank", () => ({ status: 500, body: {} }));
    for (let i = 0; i < 3; i++) {
      await assert.rejects(rerank(llm));
      await llm.embedDocuments(["fine"]); // a success on another role, between each failure
    }
    const [view] = llm.pool.workers();
    assert.equal(view!.circuits.rerank, "open");
    assert.equal(view!.circuits.embed, "closed");
    assert.equal(view!.state, "circuit_open");
    assert.equal(llm.canServe("rerank"), false);
    assert.equal(llm.canServe("embed"), true);
  } finally {
    await a.stop();
  }
});

test("availability follows recent outcomes: /health can be fine while every rerank fails", async () => {
  const a = await new MockWorker(8).start();
  const llm = new RemoteLlm({ workers: [workerConfig("a", a.url)], embedDimensions: 8, models: MODELS, fallback: null, breaker: { threshold: 3, backoffBaseMs: 1 } });
  try {
    await llm.refreshStatus();
    assert.equal(llm.status().rerank, true);
    a.on("POST /v1/rerank", () => ({ status: 500, body: {} }));
    for (let i = 0; i < 3; i++) await assert.rejects(rerank(llm));
    await llm.refreshStatus();
    assert.equal(llm.status().rerank, false, "/health still says loaded");
    assert.equal(llm.status().embed, true);
    await new Promise((r) => setTimeout(r, 5));
    assert.equal(llm.status().rerank, false, "an elapsed backoff is not evidence of recovery");
  } finally {
    await a.stop();
  }
});

test("search-time deadlines stay well under the API's two minutes", async () => {
  const { DEFAULT_TIMEOUTS: t } = await import("../src/llm/client.ts");
  const worstCase = 2 * (t.embedQuery + t.rerank + t.complete);
  assert.ok(worstCase <= 90_000, `${worstCase} ms`);
});

test("a hung reranker costs its deadline, not two minutes", async () => {
  const a = await new MockWorker(8).start();
  const llm = new RemoteLlm({ workers: [workerConfig("a", a.url)], embedDimensions: 8, models: MODELS, fallback: null, timeouts: { rerank: 20 } });
  try {
    a.on("POST /v1/rerank", () => new Promise((resolve) => setTimeout(() => resolve({ body: {} }), 500)));
    const started = Date.now();
    await assert.rejects(rerank(llm), (e) => e instanceof WorkerError && e.code === "timeout");
    assert.ok(Date.now() - started < 400);
  } finally {
    await a.stop();
  }
});

// --- half-open circuits ------------------------------------------------------------------

test("a wrong-model trial settles the circuit rather than sticking it half-open", async () => {
  const a = await new MockWorker(8).start();
  let now = 1_000_000;
  const llm = new RemoteLlm({ workers: [workerConfig("a", a.url)], embedDimensions: 8, models: MODELS, fallback: null, now: () => now, breaker: { threshold: 1, backoffBaseMs: 1_000 } });
  try {
    await llm.refreshStatus();
    const healthy = a.handlers.get("POST /v1/embed")!;
    a.on("POST /v1/embed", () => ({ status: 500, body: {} }));
    await assert.rejects(llm.embedDocuments(["x"]));
    assert.equal(llm.pool.workers()[0]!.circuits.embed, "open");
    now += 1_000;
    a.on("POST /v1/embed", (b) => ({ body: { model: "other.gguf", embeddings: (b.texts as string[]).map(() => [1, 2, 3, 4, 5, 6, 7, 8]) } }));
    await assert.rejects(llm.embedDocuments(["x"]), (e) => e instanceof WorkerError && e.code === "model_mismatch");
    assert.equal(llm.pool.workers()[0]!.circuits.embed, "closed");
    a.on("POST /v1/embed", healthy);
    await llm.refreshStatus();
    assert.equal((await llm.embedDocuments(["x"])).worker, "a");
  } finally {
    await a.stop();
  }
});

test("a good heartbeat frees a trial that never came back", async () => {
  const a = await new MockWorker(8).start();
  let now = 1_000_000;
  const llm = new RemoteLlm({ workers: [workerConfig("a", a.url)], embedDimensions: 8, models: MODELS, fallback: null, now: () => now, breaker: { threshold: 1, backoffBaseMs: 1_000 } });
  try {
    await llm.refreshStatus();
    a.on("POST /v1/rerank", () => ({ status: 500, body: {} }));
    await assert.rejects(rerank(llm));
    now += 1_000;
    let release = (_: { body: unknown }) => {};
    a.on("POST /v1/rerank", () => new Promise((resolve) => (release = resolve)));
    const trial = rerank(llm).catch(() => "ended");
    await new Promise((r) => setTimeout(r, 20));
    assert.equal(llm.pool.workers()[0]!.circuits.rerank, "half_open");
    assert.equal(llm.canServe("rerank"), false);
    await llm.refreshStatus();
    assert.equal(llm.pool.workers()[0]!.circuits.rerank, "open");
    assert.equal(llm.canServe("rerank"), true);
    release({ body: {} });
    await trial;
  } finally {
    await a.stop();
  }
});

// --- a lying worker -------------------------------------------------------------------------

test("the model name must match exactly: a suffix is not enough", async () => {
  assert.deepEqual(acceptedEmbedFiles(URI), [EMBED_FILE.toLowerCase(), `hf_ggml-org_${EMBED_FILE.toLowerCase()}`]);
  assert.deepEqual(acceptedEmbedFiles("/models/plain.gguf"), ["plain.gguf"]);
  for (const liar of [`evil-${EMBED_FILE}`, `hf_someone-else_${EMBED_FILE}`, `x${EMBED_FILE}`]) {
    const a = await new MockWorker(8, liar).start();
    const llm = new RemoteLlm({ workers: [workerConfig("a", a.url)], embedDimensions: 8, models: MODELS, fallback: null });
    try {
      await llm.refreshStatus();
      assert.equal(llm.pool.workers()[0]!.embedModelCompatible, false, liar);
      await assert.rejects(llm.embedDocuments(["x"]), (e) => e instanceof WorkerError && e.code === "no_worker");
      assert.equal(a.count("/v1/embed"), 0);
    } finally {
      await a.stop();
    }
  }
});

function memoryReference(start: ProbeReference | null = null) {
  let held = start;
  return { get: () => held, set: (r: ProbeReference) => void (held = r), peek: () => held };
}

test("a worker whose embeddings disagree with the reference is taken off embedding, and back on if it recovers", async () => {
  const reference = memoryReference();
  const p = await pair({ probeReference: reference, probeEveryMs: 0 });
  try {
    await p.llm.refreshStatus();
    assert.ok(["a", "b"].includes(reference.peek()!.worker), "the first answer became the reference");
    assert.equal(p.llm.pool.workers()[0]!.embedModelCompatible, true);
    assert.equal(p.llm.pool.workers()[1]!.embedModelCompatible, true);

    // Claims the right model, returns something else.
    const honest = p.b.handlers.get("POST /v1/embed")!;
    p.b.on("POST /v1/embed", (b) => ({ body: { model: EMBED_FILE, embeddings: (b.texts as string[]).map(() => [-1, 2, -3, 4, -5, 6, -7, 8]) } }));
    await p.llm.refreshStatus();
    assert.equal(p.llm.pool.workers()[1]!.embedModelCompatible, false);
    assert.equal((await p.llm.embedDocuments(["x"])).worker, "a");

    p.b.on("POST /v1/embed", honest);
    await p.llm.refreshStatus();
    assert.equal(p.llm.pool.workers()[1]!.embedModelCompatible, true);
  } finally {
    await p.done();
  }
});

test("a probe nobody answered changes no verdict, and rounding between devices passes", async () => {
  const text = vectorFor("x", 8);
  assert.ok(cosineOf(text, text.map((v) => v * (1 + 1e-4))) >= PROBE_MIN_COSINE);
  assert.equal(cosineOf([1, 0], [1, 0, 0]), 0);
  assert.equal(cosineOf([0, 0], [1, 1]), 0);
  const reference = memoryReference();
  const p = await pair({ probeReference: reference, probeEveryMs: 0 });
  try {
    await p.llm.refreshStatus();
    p.a.on("POST /v1/embed", () => ({ status: 503, body: {} }));
    await p.llm.refreshStatus();
    assert.equal(p.llm.pool.workers()[0]!.embedModelCompatible, true);
  } finally {
    await p.done();
  }
});

test("probes run on their own schedule", async () => {
  const p = await pair({ probeReference: memoryReference(), probeEveryMs: 60_000 });
  try {
    await p.llm.refreshStatus();
    const first = p.a.count("/v1/embed");
    assert.equal(first, 1);
    await p.llm.refreshStatus();
    assert.equal(p.a.count("/v1/embed"), first, "not on every beat");
    p.tick(60_000);
    await p.llm.refreshStatus();
    assert.equal(p.a.count("/v1/embed"), first + 1);
  } finally {
    await p.done();
  }
});

// --- values that break after the float32 cast -----------------------------------------------

test("embeddings are judged by what survives the float32 cast", () => {
  const rejects = (vector: number[], why: RegExp) => assert.throws(() => parseEmbeddings({ embeddings: [vector] }, 1, vector.length), (e) => e instanceof ResponseViolation && why.test(e.message), String(vector[0]));
  rejects([1e308, 1], /float32/); // becomes infinity
  rejects([1e-320, 1], /float32/); // becomes zero while claiming not to be
  rejects([1e20, 1], /too long/); // storable, but its length overflows
  rejects([0, 0], /all zeros/);
  rejects([1e-30, 1e-30], /all zeros/); // squares underflow, so its length is zero
  assert.deepEqual(parseEmbeddings({ embeddings: [[0, 0.5, -0.5]] }, 1, 3), [[0, 0.5, -0.5]]);
});

// --- expansion -------------------------------------------------------------------------------

test("an endless expansion is capped, in lines and in line length", () => {
  const many = Array.from({ length: 5300 }, (_, i) => `lex: api ${i}`).join("\n");
  const out = parseExpansion("api", many, true);
  assert.equal(out.length, MAX_EXPANSION_QUERIES);
  assert.deepEqual(parseExpansion("api", `lex: api ${"x".repeat(5000)}\nvec: api`, true), [{ type: "vec", text: "api" }]);
});

test("useless expansion output is a failure: noted, and nothing placeholder to cache", async () => {
  const a = await new MockWorker(8).start();
  const llm = new RemoteLlm({ workers: [workerConfig("a", a.url)], embedDimensions: 8, models: MODELS, fallback: null });
  try {
    a.on("POST /v1/complete", () => ({ body: { text: "lex: bananas\nvec: oranges\n" } }));
    const { out, reasons } = await llm.track(async (r) => ({ out: await llm.expandQuery("api keys"), reasons: r }));
    assert.deepEqual(out, [{ type: "lex", text: "api keys" }, { type: "vec", text: "api keys" }], "only the query, which QMD never caches");
    assert.deepEqual([...reasons], ["no_expansion"]);
  } finally {
    await a.stop();
  }
});

// --- the fallback ------------------------------------------------------------------------------

function trackingFallback(over: Partial<LocalFallback> = {}): LocalFallback & { used: string[] } {
  const used: string[] = [];
  return {
    used,
    embedBatch: async (texts) => {
      used.push("embedBatch");
      return texts.map(() => ({ embedding: [1, 0, 0, 0, 0, 0, 0, 0], model: "local" }));
    },
    tokenize: async () => {
      used.push("tokenize");
      return [1];
    },
    detokenize: async () => {
      used.push("detokenize");
      return "x";
    },
    ...over,
  };
}

test("a host with no model reports an outage, so a job waits rather than fails", async () => {
  const a = await new MockWorker(8).start();
  const broken = trackingFallback({ tokenize: async () => { throw new Error("no model here"); }, detokenize: async () => { throw new Error("no model here"); } });
  const llm = new RemoteLlm({ workers: [workerConfig("a", a.url)], embedDimensions: 8, models: MODELS, fallback: broken });
  try {
    await a.stop();
    await assert.rejects(llm.tokenize("x"), (e) => e instanceof WorkerError && e.code === "no_worker");
    await assert.rejects(llm.detokenize([1]), (e) => e instanceof WorkerError && e.code === "no_worker");
  } finally {
    await a.stop();
  }
});

test("the CPU tokenizer is for outages: not refusals or full queues", async () => {
  const a = await new MockWorker(8).start();
  const fallback = trackingFallback();
  const llm = new RemoteLlm({ workers: [workerConfig("a", a.url)], embedDimensions: 8, models: MODELS, fallback });
  try {
    a.on("POST /v1/tokenize", () => ({ status: 400, body: {} }));
    await assert.rejects(llm.tokenize("x"), (e) => e instanceof WorkerError && e.code === "rejected");
    assert.deepEqual(fallback.used, []);
    a.on("POST /v1/tokenize", () => ({ status: 503, body: {} }));
    await llm.tokenize("x");
    assert.deepEqual(fallback.used, ["tokenize"]);
  } finally {
    await a.stop();
  }
  // A full queue: one slot, held, and no waiting allowed past the deadline.
  const b = await new MockWorker(8).start();
  let now = 0;
  const busy = new RemoteLlm({ workers: [workerConfig("b", b.url, { maxConcurrent: 1 })], embedDimensions: 8, models: MODELS, fallback, now: () => now });
  try {
    let open = () => {};
    const gate = new Promise<void>((r) => (open = r));
    const holder = busy.pool.dispatch("embed", "background", "embed", () => gate);
    await new Promise((r) => setImmediate(r));
    const before = fallback.used.length;
    const waiting = busy.tokenize("x");
    now = 700_000;
    await assert.rejects(waiting, (e) => e instanceof WorkerError && e.code === "saturated");
    assert.equal(fallback.used.length, before);
    open();
    await holder;
  } finally {
    await b.stop();
  }
});

test("the fallback will not load a model that is not already on this host", async () => {
  const fallback = trackingFallback();
  const guarded = guardedFallback(fallback, "/cache", URI, () => ["something-else.gguf"]);
  await assert.rejects(guarded.tokenize("x"), /not on this host/);
  await assert.rejects(guarded.detokenize([1]), /not on this host/);
  await assert.rejects(guarded.embedBatch(["x"]), /not on this host/);
  assert.deepEqual(fallback.used, [], "the SDK's loader, which would download it, was never reached");

  const unreadable = guardedFallback(fallback, "/cache", URI, () => { throw new Error("ENOENT"); });
  await assert.rejects(unreadable.tokenize("x"), /not on this host/);

  for (const cached of [EMBED_FILE, `hf_ggml-org_${EMBED_FILE}`]) {
    const present = guardedFallback(fallback, "/cache", URI, () => [cached]);
    assert.deepEqual(await present.tokenize("x"), [1], cached);
    await present.detokenize([1]);
    await present.embedBatch(["x"]);
  }
  const partial = guardedFallback(trackingFallback(), "/cache", URI, () => [`${EMBED_FILE}.ipull`]);
  await assert.rejects(partial.tokenize("x"), /not on this host/, "a partial download does not count");
});

test("a query vector the index cannot use degrades, on the worker's path and the fallback's", async () => {
  const a = await new MockWorker(4).start();
  const fallback = trackingFallback({ embedBatch: async (t) => t.map(() => ({ embedding: [1, 2, 3], model: "local" })) });
  const llm = new RemoteLlm({ workers: [workerConfig("a", a.url)], embedDimensions: 0, models: MODELS, fallback, indexDimensions: () => 8 });
  try {
    const { out, reasons } = await llm.track(async (r) => ({ out: await llm.embedBatch(["q"]), reasons: r }));
    assert.deepEqual(out, [null], "not a 500 from sqlite-vec");
    assert.deepEqual([...reasons], ["no_vectors"]);
  } finally {
    await a.stop();
  }
});

test("plain queries survive QMD's syntax rules however the text looks", async () => {
  const { plainQueries } = await import("../src/llm/degraded-search.ts");
  const strict = { validateLexQuery: (q: string) => (q.includes('"') ? "unbalanced" : null), validateSemanticQuery: (q: string) => (q.includes('"') ? "negation" : null) };
  assert.deepEqual(plainQueries(strict, "  api   keys "), [{ type: "lex", query: "api keys" }, { type: "vec", query: "api keys" }]);
  assert.deepEqual(plainQueries(strict, 'api "keys'), [{ type: "lex", query: "api keys" }]);
  assert.deepEqual(plainQueries({ validateLexQuery: () => "no", validateSemanticQuery: () => "no" }, '"!!"'), []);
});

test("the vector repair covers a missing table, a healthy one, and a host that cannot read it", async () => {
  const { repairVectorRows } = await import("../src/store.ts");
  const seen: string[] = [];
  const db = (hasTable: boolean, run: () => { changes: number }) => ({
    prepare: (sql: string) => ({
      get: () => (hasTable ? {} : undefined),
      run: () => {
        seen.push(sql);
        return run();
      },
    }),
  });
  assert.equal(repairVectorRows(db(false, () => ({ changes: 4 }))), 4);
  assert.equal(seen.at(-1), "DELETE FROM content_vectors");
  assert.equal(repairVectorRows(db(true, () => ({ changes: 1 }))), 1);
  assert.match(seen.at(-1)!, /NOT IN \(SELECT hash_seq FROM vectors_vec\)/);
  const noExtension = db(true, () => {
    throw new Error("no such module: vec0");
  });
  assert.equal(repairVectorRows(noExtension), 0, "it cannot judge what it cannot read, so it deletes nothing");
});

// --- configuration ---------------------------------------------------------------------------------

test("a worker's token cannot be the sidecar's own secret", () => {
  assert.throws(
    () => loadConfig({ MEMORIA_QMD_LLM: "remote", MEMORIA_QMD_SECRET: "0123456789abcdefXYZ", MEMORIA_QMD_WORKERS: JSON.stringify([{ name: "a", url: "http://127.0.0.1:1", tokenEnv: "MEMORIA_QMD_SECRET" }]) }),
    /must not be the sidecar's own secret/,
  );
});

test("the rerank deadline is configurable but cannot reach the API's timeout", () => {
  const remote = (value: string) => loadConfig({ MEMORIA_QMD_LLM: "remote", MEMORIA_QMD_WORKER_URL: "http://127.0.0.1:1", MEMORIA_QMD_RERANK_TIMEOUT_MS: value }).llm;
  const set = remote("5000");
  assert.ok(set.mode === "remote" && set.rerankTimeoutMs === 5000);
  assert.throws(() => remote("120000"), /MEMORIA_QMD_RERANK_TIMEOUT_MS/);
});

// --- the crash budget ------------------------------------------------------------------------------

test("deferrals do not use up the crash budget: a restart after a long outage does not kill the job", async () => {
  const file = await tempFile("q.sqlite");
  const scheduler = new ManualScheduler();
  const store = new SqliteJobStore(file);
  let tries = 0;
  const during = new JobQueue(() => {
    // A long outage, then the workers return and the machine goes down mid-run.
    if (++tries > 15) return never;
    return Promise.reject(new JobDeferred("no worker"));
  }, { store, scheduler, deferBaseMs: 1_000, deferMaxMs: 1_000, maxCrashes: 2 });
  const job = during.enqueue(req());
  for (let i = 0; i < 20; i++) await scheduler.advance(1_000);
  assert.equal(job.state, "running");
  assert.ok(job.attempts >= 15, `${job.attempts} attempts`);
  store.close();

  const reopened = new SqliteJobStore(file);
  const queue = new JobQueue(async () => result, { store: reopened, scheduler: new ManualScheduler(), maxCrashes: 2 });
  queue.flush();
  await queue.idle();
  assert.equal(queue.get(job.id)?.state, "succeeded");
  reopened.close();
});

test("only a restart that finds the job running counts as a crash, and progress clears the count", async () => {
  const file = await tempFile("q.sqlite");
  const crash = async (run: () => Promise<JobResult>): Promise<{ state: string | undefined; crashes: number }> => {
    const store = new SqliteJobStore(file);
    const queue = new JobQueue(run, { store, maxCrashes: 9 });
    await new Promise((r) => setTimeout(r, 5));
    const [row] = store.load();
    store.close();
    void queue;
    return { state: row?.job.state, crashes: row!.crashes };
  };
  const first = new SqliteJobStore(file);
  const start = new JobQueue(() => never, { store: first, maxCrashes: 9 });
  start.enqueue(req());
  await new Promise((r) => setImmediate(r));
  first.close();

  assert.equal((await crash(() => never)).crashes, 1);
  assert.equal((await crash(() => never)).crashes, 2);

  // This run gets real work done before the machine goes down again.
  const store = new SqliteJobStore(file);
  const progressing = new JobQueue((_r, ctx) => {
    ctx.noteProgress();
    return never;
  }, { store, maxCrashes: 9 });
  await new Promise((r) => setImmediate(r));
  assert.equal(store.load()[0]!.crashes, 0);
  void progressing;
  store.close();

  assert.equal((await crash(() => never)).crashes, 1, "the count started over");
});

test(":retry gives a job a clean crash budget", async () => {
  const file = await tempFile("q.sqlite");
  let id = "";
  for (let i = 0; i < 3; i++) {
    const store = new SqliteJobStore(file);
    const queue = new JobQueue(() => never, { store, maxCrashes: 3 });
    if (i === 0) id = queue.enqueue(req()).id;
    await new Promise((r) => setImmediate(r));
    store.close();
  }
  const store = new SqliteJobStore(file);
  const queue = new JobQueue(async () => result, { store, maxCrashes: 3 });
  assert.equal(queue.get(id)?.state, "failed");
  assert.ok(store.load()[0]!.crashes >= 3);
  queue.retry(id);
  assert.equal(store.load()[0]!.crashes, 0);
  await queue.idle();
  assert.equal(queue.get(id)?.state, "succeeded");
  store.close();
});

test("a clean shutdown keeps a running job waiting: not failed, not a crash", async () => {
  const file = await tempFile("q.sqlite");
  const store = new SqliteJobStore(file);
  let closeIndex = (_err: Error) => {};
  const queue = new JobQueue(() => new Promise<JobResult>((_resolve, reject) => (closeIndex = reject)), { store });
  const job = queue.enqueue(req());
  await new Promise((r) => setImmediate(r));
  assert.equal(job.state, "running");

  queue.suspend();
  closeIndex(new Error("database connection is not open")); // what closing the index does to it
  await new Promise((r) => setImmediate(r));
  store.close();

  const reopened = new SqliteJobStore(file);
  const [row] = reopened.load();
  assert.equal(row!.job.state, "queued", "never recorded as failed");
  assert.equal(row!.crashes, 0);
  const after = new JobQueue(async () => result, { store: reopened });
  await after.idle();
  assert.equal(after.get(job.id)?.state, "succeeded");
  reopened.close();
});

test("suspend stops new work and timers", async () => {
  const scheduler = new ManualScheduler();
  let runs = 0;
  const queue = new JobQueue(async () => {
    runs++;
    return result;
  }, { scheduler, debounceMs: 1_000 });
  queue.enqueue(req(), { debounce: true });
  queue.suspend();
  assert.equal(scheduler.pendingTimers, 0);
  queue.enqueue(req({ collections: ["other"] }));
  await new Promise((r) => setImmediate(r));
  assert.equal(runs, 0);
});

// --- a failing queue store ---------------------------------------------------------------------------

class FlakyStore implements JobStore {
  failing = true;
  saved = new Map<string, string>();
  removed: string[] = [];
  load(): PersistedJob[] {
    return [];
  }
  save(entry: PersistedJob): void {
    if (this.failing) throw new Error("database is locked");
    this.saved.set(entry.job.id, entry.job.state);
  }
  remove(id: string): void {
    if (this.failing) throw new Error("database is locked");
    this.removed.push(id);
  }
}

test("a store failure on a debounce timer neither throws nor stops the job", async () => {
  const scheduler = new ManualScheduler();
  const store = new FlakyStore();
  const errors: string[] = [];
  let runs = 0;
  const queue = new JobQueue(async () => {
    runs++;
    return result;
  }, { store, scheduler, debounceMs: 1_000, onStoreError: (m) => errors.push(m) });
  const job = queue.enqueue(req(), { debounce: true });
  assert.deepEqual(queue.persistence(), { enabled: true, healthy: false });
  await scheduler.advance(1_000); // an uncaught throw here would have killed the process
  await queue.idle();
  assert.equal(runs, 1);
  assert.equal(job.state, "succeeded");
  assert.ok(errors.every((m) => m === "database is locked"));
  assert.ok(errors.length > 0);
});

test("when the store recovers, what it missed is written and health returns", async () => {
  const store = new FlakyStore();
  const queue = new JobQueue(async () => result, { store, keep: 1 });
  const first = queue.enqueue(req());
  await queue.idle();
  assert.equal(store.saved.size, 0);
  store.failing = false;
  const second = queue.enqueue(req({ collections: ["other"] }));
  await queue.idle();
  assert.deepEqual(queue.persistence(), { enabled: true, healthy: true });
  assert.equal(store.saved.get(second.id), "succeeded");
  assert.ok(store.removed.includes(first.id), "the trimmed job is gone from the store too");
});

test("a store that cannot load starts an empty queue that says so", () => {
  const store = new FlakyStore();
  store.load = () => {
    throw new Error("disk I/O error");
  };
  const errors: string[] = [];
  const queue = new JobQueue(async () => result, { store, onStoreError: (m) => errors.push(m) });
  assert.equal(queue.list().length, 0);
  assert.equal(queue.persistence().healthy, false);
  assert.deepEqual(errors, ["disk I/O error"]);
});

test("with no store, persistence says so", () => {
  assert.deepEqual(new JobQueue(async () => result).persistence(), { enabled: false, healthy: false });
});

test("a locked queue file fails fast rather than freezing the event loop", async () => {
  const file = await tempFile("q.sqlite");
  const store = new SqliteJobStore(file);
  const { DatabaseSync } = await import("node:sqlite");
  const other = new DatabaseSync(file);
  other.exec("BEGIN EXCLUSIVE");
  try {
    const errors: string[] = [];
    const queue = new JobQueue(() => never, { store, onStoreError: (m) => errors.push(m) });
    const started = Date.now();
    queue.enqueue(req());
    assert.ok(Date.now() - started < 1_000, "a 5 s busy timeout would freeze everything");
    assert.equal(queue.persistence().healthy, false);
    assert.ok(errors.length > 0);
  } finally {
    other.exec("ROLLBACK");
    other.close();
    store.close();
  }
});

// --- a damaged queue file must not stop startup ----------------------------------------------------------

test("one bad row is set aside, and the rest load", async () => {
  const file = await tempFile("q.sqlite");
  const store = new SqliteJobStore(file);
  const queue = new JobQueue(async () => result, { store });
  queue.enqueue(req({ reason: "good" }));
  await queue.idle();
  const bad = queue.enqueue(req({ reason: "bad", collections: ["other"] }));
  await queue.idle();
  store.close();
  const { DatabaseSync } = await import("node:sqlite");
  const db = new DatabaseSync(file);
  db.prepare("UPDATE jobs SET result = '{not json' WHERE id = ?").run(bad.id);
  db.close();

  const warnings: string[] = [];
  const { store: reopened } = openJobStore(file, (m) => warnings.push(m));
  assert.ok(reopened);
  assert.equal(reopened.quarantined, 1);
  const rows = reopened.load();
  assert.deepEqual(rows.map((r) => r.job.request.reason), ["good"]);
  assert.equal(reopened.quarantined, 1, "it left the table, so it is not found twice");
  reopened.close();
  assert.deepEqual(warnings, []);

  const raw = new DatabaseSync(file);
  const kept = raw.prepare("SELECT id, reason FROM jobs_quarantine").all() as Array<{ id: string; reason: string }>;
  raw.close();
  assert.equal(kept[0]!.id, bad.id, "kept for inspection");
});

test("a row with an unknown state is set aside", async () => {
  const file = await tempFile("q.sqlite");
  const store = new SqliteJobStore(file);
  const queue = new JobQueue(async () => result, { store });
  const job = queue.enqueue(req());
  await queue.idle();
  store.close();
  const { DatabaseSync } = await import("node:sqlite");
  const db = new DatabaseSync(file);
  db.prepare("UPDATE jobs SET state = 'exploded' WHERE id = ?").run(job.id);
  db.close();
  const reopened = new SqliteJobStore(file);
  assert.deepEqual(reopened.load(), []);
  reopened.close();
});

test("a corrupt queue file is moved aside and a new one started", async () => {
  const file = await tempFile("q.sqlite");
  await writeFile(file, "this is not a database, and never was. ".repeat(400));
  const warnings: string[] = [];
  const opened = openJobStore(file, (m) => warnings.push(m), () => 12345);
  assert.ok(opened.store, "search must still be able to start");
  assert.equal(opened.movedTo, `${file}.corrupt-12345`);
  assert.match(await readFile(`${file}.corrupt-12345`, "utf8"), /not a database/, "moved, not deleted");
  assert.deepEqual(opened.store.load(), []);
  assert.equal(warnings.length, 2);
  opened.store.close();
});

test("a queue file from a newer version is moved aside too", async () => {
  const file = await tempFile("q.sqlite");
  new SqliteJobStore(file).close();
  const { DatabaseSync } = await import("node:sqlite");
  const db = new DatabaseSync(file);
  db.exec("PRAGMA user_version = 999");
  db.close();
  const opened = openJobStore(file, () => {}, () => 1);
  assert.ok(opened.store);
  assert.ok(existsSync(`${file}.corrupt-1`));
  opened.store.close();
});

test("if no file can be used at all, the sidecar runs with jobs in memory", async () => {
  const directory = path.dirname(await tempFile("x"));
  const warnings: string[] = [];
  // A directory cannot be opened as a database, and this path cannot be recreated once moved aside.
  const inMissing = path.join(directory, "no", "such", "dir", "q.sqlite");
  const opened = openJobStore(inMissing, (m) => warnings.push(m));
  assert.equal(opened.store, null);
  assert.equal(opened.movedTo, null);
  assert.match(warnings.at(-1)!, /in memory only/);
  assert.equal(readdirSync(directory).length, 0);
});

// --- scheduling edges ----------------------------------------------------------------------------------------

test("a write joining a job waiting on a retry brings its scan forward to the debounce", async () => {
  const scheduler = new ManualScheduler();
  let runs = 0;
  const queue = new JobQueue(async () => {
    runs++;
    if (runs === 1) throw new JobDeferred("no worker");
    return result;
  }, { scheduler, debounceMs: 60_000, deferBaseMs: 300_000, deferMaxMs: 300_000 });
  queue.enqueue(req());
  await scheduler.advance(0);
  assert.equal(runs, 1);
  queue.enqueue(req({ reason: "a delete" }), { debounce: true });
  await scheduler.advance(59_000);
  assert.equal(runs, 1);
  await scheduler.advance(1_000);
  await queue.idle();
  assert.equal(runs, 2, "at 60 s, not 300 s");
});

test("a write does not push a retry that is already sooner than the debounce", async () => {
  const scheduler = new ManualScheduler();
  let runs = 0;
  const queue = new JobQueue(async () => {
    runs++;
    if (runs === 1) throw new JobDeferred("no worker");
    return result;
  }, { scheduler, debounceMs: 60_000, deferBaseMs: 1_000 });
  queue.enqueue(req());
  await scheduler.advance(0);
  queue.enqueue(req(), { debounce: true });
  await scheduler.advance(1_000);
  await queue.idle();
  assert.equal(runs, 2);
});

test("max-wait holds when the clock moves back", async () => {
  let now = 1_000_000;
  const timers: Array<{ at: number; run: () => void }> = [];
  const scheduler = {
    now: () => now,
    setTimeout: (run: () => void, delay: number) => {
      const timer = { at: now + delay, run };
      timers.push(timer);
      return timer;
    },
    clearTimeout: (handle: unknown) => {
      const i = timers.indexOf(handle as never);
      if (i >= 0) timers.splice(i, 1);
    },
  };
  const queue = new JobQueue(async () => result, { scheduler, debounceMs: 60_000, maxWaitMs: 300_000 });
  queue.enqueue(req({ reason: "first" }), { debounce: true });
  now -= 86_400_000; // the clock jumps back a day
  queue.enqueue(req({ reason: "second" }), { debounce: true });
  // Keep writing, past the allowance measured from the new clock.
  let released = false;
  for (let elapsed = 0; elapsed < 400_000 && !released; elapsed += 30_000) {
    now += 30_000;
    const due = timers.filter((t) => t.at <= now);
    for (const t of due) t.run();
    released = queue.counts().held === 0;
    if (!released) queue.enqueue(req({ reason: "more" }), { debounce: true });
  }
  assert.equal(released, true, "steady writes still could not hold it past max-wait");
  await queue.idle();
});

test("a job restored with a far-future due time, from a clock set back, still runs within max-wait", async () => {
  const file = await tempFile("q.sqlite");
  const before = new ManualScheduler();
  await before.advance(86_400_000);
  const store = new SqliteJobStore(file);
  const first = new JobQueue(async () => result, { store, scheduler: before, debounceMs: 60_000, maxWaitMs: 300_000 });
  const job = first.enqueue(req(), { debounce: true });
  store.close();

  const reopened = new SqliteJobStore(file);
  const after = new ManualScheduler(); // the clock now reads a day earlier
  const queue = new JobQueue(async () => result, { store: reopened, scheduler: after, debounceMs: 60_000, maxWaitMs: 300_000 });
  await after.advance(300_000);
  await queue.idle();
  assert.equal(queue.get(job.id)?.state, "succeeded");
  reopened.close();
});

test(":retry does not queue the same work twice", async () => {
  let fail = true;
  let runs = 0;
  const scheduler = new ManualScheduler();
  const queue = new JobQueue(async () => {
    runs++;
    if (fail) throw new Error("kaboom");
    return result;
  }, { scheduler, debounceMs: 1_000 });
  const failed = queue.enqueue(req());
  await scheduler.advance(0);
  await queue.idle();
  assert.equal(failed.state, "failed");
  fail = false;
  const waiting = queue.enqueue(req({ reason: "another" }), { debounce: true });
  assert.notEqual(waiting, failed);
  const retried = queue.retry(failed.id);
  assert.equal(retried, waiting, "the waiting job is returned, and started");
  await queue.idle();
  assert.equal(runs, 2);
  assert.equal(failed.state, "failed", "the failed record is left as it was");
});

test("a partial job fails with its result, so documents left behind cannot read as success", async () => {
  const queue = new JobQueue(async () => {
    throw new JobPartial("1 of 12 documents could not be embedded", { ...result, embed: { docsProcessed: 12, chunksEmbedded: 11, errors: 1, durationMs: 1, docsPending: 1 } });
  });
  const job = queue.enqueue(req());
  await queue.idle();
  assert.equal(job.state, "failed");
  assert.match(job.error!, /1 of 12/);
  assert.equal(job.result?.embed?.docsPending, 1);
  assert.ok(queue.retry(job.id));
  assert.equal(job.result, null);
});

// --- the client's status mapping ---------------------------------------------------------------------------------

test("only 400 and 413 say something about the request", async () => {
  const a = await new MockWorker(8).start();
  const client = new WorkerClient({ url: a.url, token: undefined });
  try {
    for (const [status, code] of [[400, "rejected"], [413, "rejected"], [404, "unavailable"], [410, "unavailable"], [422, "unavailable"], [500, "unavailable"], [401, "unauthorized"]] as const) {
      a.on("GET /x", () => ({ status, body: {} }));
      await assert.rejects(client.get("x", "/x"), (e) => e instanceof WorkerError && e.code === code, String(status));
    }
  } finally {
    await a.stop();
  }
});
