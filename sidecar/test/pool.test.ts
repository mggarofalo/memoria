import assert from "node:assert/strict";
import { test } from "node:test";
import { WorkerError } from "../src/llm/client.ts";
import { Breaker, WorkerPool, type DispatchTarget } from "../src/llm/pool.ts";
import { RemoteLlm } from "../src/llm/remote.ts";
import type { Capability, PoolWorkerConfig } from "../src/llm/types.ts";
import { EMBED_FILE, MockWorker } from "./mock-worker.ts";

const TOKENS = { a: "token-for-worker-a-0123456789", b: "token-for-worker-b-0123456789" };
const URI = `hf:ggml-org/embeddinggemma-300M-GGUF/${EMBED_FILE}`;

interface Fleet {
  a: MockWorker;
  b: MockWorker;
  pool: WorkerPool;
  llm: RemoteLlm;
  /** Connection attempts per worker URL, counted before any network happens. */
  attempts: Map<string, number>;
  tick(ms: number): void;
  done(): Promise<void>;
}

async function fleet(over: { a?: Partial<PoolWorkerConfig>; b?: Partial<PoolWorkerConfig>; bFile?: string } = {}): Promise<Fleet> {
  const a = await new MockWorker(8).start();
  const b = await new MockWorker(8, over.bFile).start();
  const attempts = new Map<string, number>();
  let now = 1_000_000;
  const countingFetch = ((input: URL, init: RequestInit) => {
    const origin = new URL(input).origin;
    attempts.set(origin, (attempts.get(origin) ?? 0) + 1);
    return globalThis.fetch(input, init);
  }) as unknown as typeof globalThis.fetch;
  const worker = (name: "a" | "b", mock: MockWorker, extra?: Partial<PoolWorkerConfig>): PoolWorkerConfig => ({ name, url: mock.url, token: TOKENS[name], capabilities: ["embed", "rerank", "generate"], priority: 100, maxConcurrent: 4, ...extra });
  const llm = new RemoteLlm({
    workers: [worker("a", a, { priority: 1, ...over.a }), worker("b", b, { priority: 2, ...over.b })],
    embedDimensions: 8,
    models: { embed: URI, rerank: "hf:x/r.gguf", generate: "hf:x/g.gguf" },
    fallback: null,
    fetch: countingFetch,
    now: () => now,
    breaker: { threshold: 3, backoffBaseMs: 1_000, backoffMaxMs: 8_000 },
  });
  return {
    a,
    b,
    pool: llm.pool,
    llm,
    attempts,
    tick: (ms) => {
      now += ms;
    },
    done: async () => {
      await llm.dispose();
      await a.stop();
      await b.stop();
    },
  };
}

const rerank = (f: Fleet) => f.llm.rerank("q", [{ file: "x.md", text: "x" }]);

// --- the breaker alone ----------------------------------------------------------

test("Breaker opens at the threshold, allows one trial after the backoff, and doubles on failure", () => {
  const b = new Breaker({ threshold: 3, backoffBaseMs: 100, backoffMaxMs: 250 });
  b.failure(0);
  b.failure(0);
  assert.equal(b.state, "closed");
  b.failure(0);
  assert.equal(b.state, "open");
  assert.equal(b.allows(99), false);
  assert.equal(b.allows(100), true);
  b.begin(100);
  assert.equal(b.state, "half_open");
  assert.equal(b.allows(100), false, "only one trial at a time");
  b.failure(100);
  assert.equal(b.allows(299), false, "backoff doubled");
  assert.equal(b.allows(300), true);
  b.begin(300);
  b.failure(300);
  assert.equal(b.allows(549), false, "capped at the maximum");
  assert.equal(b.allows(550), true);
  b.begin(550);
  b.success();
  assert.equal(b.state, "closed");
  b.failure(600);
  b.failure(600);
  assert.equal(b.state, "closed", "a success resets the count");
  b.failure(600);
  assert.equal(b.allows(699), false, "and the backoff");
  assert.equal(b.allows(700), true);
});

test("a worker back from an outage gets its trial without waiting out the backoff", () => {
  const b = new Breaker({ threshold: 1, backoffBaseMs: 1000, backoffMaxMs: 1000 });
  b.failure(0);
  assert.equal(b.allows(1), false);
  b.recovered(1);
  assert.equal(b.allows(1), true);
  assert.equal(b.state, "open", "it still has to prove itself with a real call");
  const closed = new Breaker({ threshold: 1, backoffBaseMs: 1000, backoffMaxMs: 1000 });
  closed.recovered(1);
  assert.equal(closed.state, "closed");
});

test("a stale success cannot close an open circuit", () => {
  const b = new Breaker({ threshold: 1, backoffBaseMs: 1000, backoffMaxMs: 1000 });
  b.failure(0);
  b.success(); // a call that began before the circuit opened
  assert.equal(b.state, "open");
  assert.equal(b.allows(1), false);
  b.failure(1); // nor does a late failure extend the backoff
  assert.equal(b.allows(1000), true);
});

test("a trial that reports nothing cannot wedge the circuit", () => {
  const b = new Breaker({ threshold: 1, backoffBaseMs: 1000, backoffMaxMs: 1000 });
  b.failure(0);
  b.begin(1000);
  assert.equal(b.allows(5000), false);
  b.unstick();
  assert.equal(b.allows(5000), true);
  b.begin(5000);
  b.settle();
  assert.equal(b.state, "closed");
  b.unstick();
  b.settle();
  assert.equal(b.state, "closed", "both do nothing to a closed circuit");
});

// --- routing --------------------------------------------------------------------

test("priority breaks ties between equally loaded workers", async () => {
  const f = await fleet();
  try {
    await f.pool.refresh();
    await rerank(f);
    assert.equal(f.a.count("/v1/rerank"), 1);
    assert.equal(f.b.count("/v1/rerank"), 0);
  } finally {
    await f.done();
  }
});

test("background work asks a down worker again rather than call a missed heartbeat an outage", async () => {
  const f = await fleet();
  const healthy = { body: { status: "ok", queueDepth: 0, models: { embed: { loaded: true }, rerank: { loaded: true }, generate: { loaded: true } } } };
  try {
    for (const mock of [f.a, f.b]) mock.on("GET /health", () => ({ status: 500, body: { error: "stalled" } }));
    await f.pool.refresh();
    assert.equal(f.pool.canServe("embed", "background"), false);
    for (const mock of [f.a, f.b]) mock.on("GET /health", () => healthy);
    await assert.rejects(f.pool.dispatch("rerank", "interactive", "rerank", async () => "x"), /no worker/, "an interactive caller does not wait on it");
    assert.equal(await f.pool.dispatch("embed", "background", "embed", async () => "served"), "served");
  } finally {
    await f.done();
  }
});

test("the lowest queue depth wins over priority", async () => {
  const f = await fleet();
  try {
    f.a.on("GET /health", () => ({ body: { status: "ok", queueDepth: 9, models: { rerank: { loaded: true } } } }));
    await f.pool.refresh();
    await rerank(f);
    assert.equal(f.b.count("/v1/rerank"), 1);
  } finally {
    await f.done();
  }
});

test("a worker cannot attract traffic by reporting a negative depth", async () => {
  const f = await fleet({ a: { priority: 1 }, b: { priority: 2 } });
  try {
    f.b.on("GET /health", () => ({ body: { status: "ok", queueDepth: -1000, models: { rerank: { loaded: true } } } }));
    await f.pool.refresh();
    await rerank(f);
    assert.equal(f.a.count("/v1/rerank"), 1);
  } finally {
    await f.done();
  }
});

test("in-flight calls count toward load, so concurrent calls spread out", async () => {
  const f = await fleet();
  try {
    await f.pool.refresh();
    let open = () => {};
    const gate = new Promise<void>((r) => (open = r));
    const hold = async (target: DispatchTarget) => {
      await gate;
      return target.name;
    };
    const first = f.pool.dispatch("rerank", "interactive", "rerank", hold);
    const second = f.pool.dispatch("rerank", "interactive", "rerank", hold);
    await new Promise((r) => setImmediate(r));
    open();
    assert.deepEqual([await first, await second], ["a", "b"]);
  } finally {
    await f.done();
  }
});

test("busy workers are used last, not refused", async () => {
  const f = await fleet();
  try {
    f.a.on("GET /health", () => ({ body: { status: "busy", queueDepth: 0, models: { rerank: { loaded: true } } } }));
    await f.pool.refresh();
    await rerank(f);
    assert.equal(f.b.count("/v1/rerank"), 1, "the idle worker");
    await f.b.stop();
    await f.pool.refresh();
    await rerank(f);
    assert.equal(f.a.count("/v1/rerank"), 1, "the busy one, as the only choice");
  } finally {
    await f.done();
  }
});

test("calls go only to workers configured and reporting for that capability", async () => {
  const f = await fleet({ a: { capabilities: ["rerank"] }, b: { capabilities: ["generate"] } });
  try {
    await f.pool.refresh();
    await rerank(f);
    await f.llm.expandQuery("q");
    assert.equal(f.a.count("/v1/rerank"), 1);
    assert.equal(f.b.count("/v1/complete"), 1);
    assert.equal(f.pool.canServe("embed"), false);
    await assert.rejects(f.llm.embedDocuments(["x"]), (e) => e instanceof WorkerError && e.code === "no_worker");
  } finally {
    await f.done();
  }
});

test("a worker still loading a model gets no calls for that role", async () => {
  const f = await fleet();
  try {
    f.a.on("GET /health", () => ({ body: { status: "starting", models: { embed: { loaded: true }, rerank: { loaded: false } } } }));
    await f.pool.refresh();
    await rerank(f);
    assert.equal(f.a.count("/v1/rerank"), 0);
    assert.equal(f.b.count("/v1/rerank"), 1);
  } finally {
    await f.done();
  }
});

// --- failover -------------------------------------------------------------------

test("a failure retries once on a different worker", async () => {
  const f = await fleet();
  try {
    f.a.on("POST /v1/rerank", () => ({ status: 500, body: {} }));
    const out = await rerank(f);
    assert.equal(out.results.length, 1);
    assert.equal(f.a.count("/v1/rerank"), 1);
    assert.equal(f.b.count("/v1/rerank"), 1);
  } finally {
    await f.done();
  }
});

test("a bad answer fails over like a dead worker", async () => {
  const f = await fleet();
  try {
    f.a.on("POST /v1/rerank", () => ({ body: { results: [{ index: 0, score: 99 }] } }));
    assert.equal((await rerank(f)).results.length, 1);
    assert.equal(f.b.count("/v1/rerank"), 1);
  } finally {
    await f.done();
  }
});

test("there is only one retry, and the failure is what callers see", async () => {
  const f = await fleet();
  try {
    f.a.on("POST /v1/rerank", () => ({ status: 503, body: { error: "overloaded" } }));
    f.b.on("POST /v1/rerank", () => ({ status: 503, body: { error: "overloaded" } }));
    await assert.rejects(rerank(f), (e) => e instanceof WorkerError && e.code === "unavailable" && /overloaded/.test(e.message));
    assert.equal(f.a.count("/v1/rerank") + f.b.count("/v1/rerank"), 2);
  } finally {
    await f.done();
  }
});

test("a request the worker refuses is not retried and does not count against it", async () => {
  const f = await fleet();
  try {
    f.a.on("POST /v1/rerank", () => ({ status: 400, body: { error: "query_too_long" } }));
    for (let i = 0; i < 4; i++) await assert.rejects(rerank(f), (e) => e instanceof WorkerError && e.code === "rejected");
    assert.equal(f.b.count("/v1/rerank"), 0);
    assert.equal(f.pool.workers()[0]!.circuit, "closed");
  } finally {
    await f.done();
  }
});

// --- the circuit breaker in use ---------------------------------------------------

test("a down worker stops costing connection attempts once its circuit opens", async () => {
  const f = await fleet();
  try {
    const aUrl = f.a.url;
    await f.a.stop();
    await f.b.stop();
    const tries = () => f.attempts.get(aUrl) ?? 0;
    for (let i = 0; i < 3; i++) await assert.rejects(rerank(f));
    const before = tries();
    assert.ok(before >= 3);
    assert.equal(f.pool.workers()[0]!.state, "circuit_open");

    for (let i = 0; i < 50; i++) {
      await assert.rejects(rerank(f), (e) => e instanceof WorkerError && e.code === "no_worker");
    }
    assert.equal(tries(), before, "fifty calls, no connection attempts");
    assert.equal(f.pool.canServe("rerank"), false);

    // After the backoff, one trial. Its failure doubles the wait.
    f.tick(1_000);
    await assert.rejects(rerank(f));
    assert.equal(tries(), before + 1);
    await assert.rejects(rerank(f), (e) => e instanceof WorkerError && e.code === "no_worker");
    assert.equal(tries(), before + 1);
    f.tick(1_000);
    await assert.rejects(rerank(f), (e) => e instanceof WorkerError && e.code === "no_worker");
    assert.equal(tries(), before + 1, "the backoff is now 2 s");
  } finally {
    await f.done();
  }
});

test("while one circuit is open, traffic flows to the other worker at no connection cost", async () => {
  const f = await fleet();
  try {
    f.a.on("POST /v1/rerank", () => ({ status: 500, body: {} }));
    for (let i = 0; i < 3; i++) await rerank(f);
    assert.equal(f.pool.workers()[0]!.circuit, "open");
    const aCalls = f.a.count("/v1/rerank");
    for (let i = 0; i < 10; i++) await rerank(f);
    assert.equal(f.a.count("/v1/rerank"), aCalls);
    assert.equal(f.b.count("/v1/rerank"), 13);
  } finally {
    await f.done();
  }
});

test("a recovered worker comes back through a trial call", async () => {
  const f = await fleet({ b: { capabilities: ["generate"] } });
  try {
    f.a.on("POST /v1/rerank", () => ({ status: 500, body: {} }));
    for (let i = 0; i < 3; i++) await assert.rejects(rerank(f));
    await assert.rejects(rerank(f), (e) => e instanceof WorkerError && e.code === "no_worker");
    f.a.on("POST /v1/rerank", (b) => ({ body: { results: (b.documents as string[]).map((_, index) => ({ index, score: 0.5 })) } }));
    f.tick(1_000);
    assert.equal((await rerank(f)).results.length, 1);
    assert.equal(f.pool.workers()[0]!.circuit, "closed");
  } finally {
    await f.done();
  }
});

test("a worker back from an outage comes back early, but a good heartbeat alone proves nothing", async () => {
  const f = await fleet({ b: { capabilities: ["generate"] } });
  try {
    f.a.on("POST /v1/rerank", () => ({ status: 500, body: {} }));
    for (let i = 0; i < 3; i++) await assert.rejects(rerank(f));
    f.a.on("POST /v1/rerank", (b) => ({ body: { results: (b.documents as string[]).map((_, index) => ({ index, score: 0.5 })) } }));
    await f.pool.refresh();
    await assert.rejects(rerank(f), (e) => e instanceof WorkerError && e.code === "no_worker", "/health was fine all along");

    const healthy = f.a.handlers.get("GET /health")!;
    f.a.on("GET /health", () => ({ status: 500, body: {} }));
    await f.pool.refresh();
    f.a.on("GET /health", healthy);
    await f.pool.refresh();
    assert.equal((await rerank(f)).results.length, 1, "no clock movement needed");
  } finally {
    await f.done();
  }
});

test("a failed heartbeat takes a worker out at once, and a good one restores it", async () => {
  const f = await fleet();
  try {
    await f.pool.refresh();
    f.a.on("GET /health", () => ({ status: 500, body: {} }));
    await f.pool.refresh();
    assert.equal(f.pool.workers()[0]!.state, "down");
    await rerank(f);
    assert.equal(f.a.count("/v1/rerank"), 0, "no call was spent finding out");
    f.a.on("GET /health", () => ({ body: { status: "ok", models: { embed: { loaded: true }, rerank: { loaded: true }, generate: { loaded: true } } } }));
    await f.pool.refresh();
    assert.equal(f.pool.workers()[0]!.state, "up");
    await rerank(f);
    assert.equal(f.a.count("/v1/rerank"), 1);
  } finally {
    await f.done();
  }
});

// --- the embedding model, per worker ----------------------------------------------

test("a worker with a different embedding model never embeds or tokenizes, but may still rerank", async () => {
  const f = await fleet({ a: { priority: 1 }, bFile: "some-other-model.gguf", b: { priority: 0 } });
  try {
    await f.pool.refresh();
    const views = f.pool.workers();
    assert.equal(views[0]!.embedModelCompatible, true);
    assert.equal(views[1]!.embedModelCompatible, false);
    for (let i = 0; i < 5; i++) {
      assert.equal((await f.llm.embedDocuments(["x"])).worker, "a");
      await f.llm.tokenize("x");
      await f.llm.detokenize([120]);
    }
    assert.equal(f.b.count("/v1/embed"), 0);
    assert.equal(f.b.count("/v1/tokenize"), 0);
    assert.equal(f.b.count("/v1/detokenize"), 0);
    await rerank(f);
    assert.equal(f.b.count("/v1/rerank"), 1, "rerank does not touch stored vectors, and b is preferred here");
  } finally {
    await f.done();
  }
});

test("a worker not yet heartbeat-checked is asked before it can embed", async () => {
  const f = await fleet({ a: { capabilities: ["rerank"] }, bFile: "some-other-model.gguf" });
  try {
    await assert.rejects(f.llm.embedDocuments(["x"]), (e) => e instanceof WorkerError && e.code === "model_mismatch");
    assert.equal(f.b.count("/v1/embed"), 0);
  } finally {
    await f.done();
  }
});

test("a worker that swaps model between heartbeats is caught by its response, and the batch moves", async () => {
  const f = await fleet();
  try {
    await f.pool.refresh();
    f.a.on("POST /v1/embed", (b) => ({ body: { model: "swapped-model.gguf", embeddings: (b.texts as string[]).map(() => [1, 2, 3, 4, 5, 6, 7, 8]) } }));
    const out = await f.llm.embedDocuments(["x"]);
    assert.equal(out.worker, "b");
    assert.equal(f.pool.workers()[0]!.embedModelCompatible, false);
    const before = f.a.count("/v1/embed");
    await f.llm.embedDocuments(["y"]);
    assert.equal(f.a.count("/v1/embed"), before, "it stays out until a heartbeat clears it");
    assert.equal(f.pool.workers()[0]!.circuit, "closed", "a wrong model is not an outage");
  } finally {
    await f.done();
  }
});

test("an embed response with no model named is refused", async () => {
  const f = await fleet({ b: { capabilities: ["rerank"] } });
  try {
    await f.pool.refresh();
    f.a.on("POST /v1/embed", () => ({ body: { embeddings: [[1, 2, 3, 4, 5, 6, 7, 8]] } }));
    await assert.rejects(f.llm.embedDocuments(["x"]), (e) => e instanceof WorkerError && e.code === "model_mismatch");
  } finally {
    await f.done();
  }
});

test("workers cannot disagree on dimensions once the length is pinned", async () => {
  const a = await new MockWorker(8).start();
  const b = await new MockWorker(4).start();
  const llm = new RemoteLlm({
    workers: [
      { name: "a", url: a.url, token: undefined, capabilities: ["embed"], priority: 1, maxConcurrent: 1 },
      { name: "b", url: b.url, token: undefined, capabilities: ["embed"], priority: 2, maxConcurrent: 1 },
    ],
    embedDimensions: 0,
    models: { embed: URI, rerank: "r", generate: "g" },
    fallback: null,
  });
  try {
    assert.equal((await llm.embedDocuments(["x"])).vectors[0]!.length, 8);
    a.on("POST /v1/embed", () => ({ status: 500, body: {} }));
    await assert.rejects(llm.embedDocuments(["y"]), (e) => e instanceof WorkerError);
    assert.equal(b.count("/v1/embed"), 1, "tried, and its 4-length vectors refused");
  } finally {
    await llm.dispose();
    await a.stop();
    await b.stop();
  }
});

// --- priority -------------------------------------------------------------------

test("one slot per worker is kept back from background work", async () => {
  const f = await fleet({ a: { maxConcurrent: 2 }, b: { capabilities: ["generate"] } });
  try {
    await f.pool.refresh();
    let open = () => {};
    const gate = new Promise<void>((r) => (open = r));
    const hold = async () => {
      await gate;
      return "done";
    };
    const background = f.pool.dispatch("embed", "background", "embed", hold);
    await new Promise((r) => setImmediate(r));
    assert.deepEqual(f.pool.load().classes.background, { inFlight: 1, waiting: 0 });

    // A second background call waits, though a slot is technically free...
    const waiting = f.pool.dispatch("embed", "background", "embed", hold);
    await new Promise((r) => setImmediate(r));
    assert.deepEqual(f.pool.load().classes.background, { inFlight: 1, waiting: 1 });

    // ...and an interactive call takes it straight away.
    assert.equal(await f.pool.dispatch("rerank", "interactive", "rerank", async (t) => t.name), "a");
    open();
    await Promise.all([background, waiting]);
    assert.deepEqual(f.pool.load().classes, { interactive: { inFlight: 0, waiting: 0 }, background: { inFlight: 0, waiting: 0 } });
    assert.deepEqual(f.pool.load().workers[0], { name: "a", interactive: 0, background: 0, queueDepth: 0 });
  } finally {
    await f.done();
  }
});

test("a freed slot goes to interactive work ahead of older background work", async () => {
  const f = await fleet({ a: { maxConcurrent: 1 }, b: { capabilities: ["generate"] } });
  try {
    await f.pool.refresh();
    let openFirst = () => {};
    const first = new Promise<void>((r) => (openFirst = r));
    const order: string[] = [];
    const running = f.pool.dispatch("embed", "background", "embed", async () => {
      await first;
      order.push("first background");
    });
    await new Promise((r) => setImmediate(r));
    const olderBackground = f.pool.dispatch("embed", "background", "embed", async () => void order.push("older background"));
    await new Promise((r) => setImmediate(r));
    const interactive = f.pool.dispatch("rerank", "interactive", "rerank", async () => void order.push("interactive"));
    await new Promise((r) => setImmediate(r));
    openFirst();
    await Promise.all([running, olderBackground, interactive]);
    assert.deepEqual(order, ["first background", "interactive", "older background"]);
  } finally {
    await f.done();
  }
});

test("waiting too long for a slot is a timeout, not a hang", async () => {
  const a = await new MockWorker(8).start();
  let now = 0;
  const pool = new WorkerPool({
    workers: [{ name: "a", url: a.url, token: undefined, capabilities: ["rerank"], priority: 1, maxConcurrent: 1 }],
    embedFiles: [EMBED_FILE.toLowerCase()],
    now: () => now,
    queueTimeoutMs: { interactive: 100, background: 100 },
  });
  try {
    let open = () => {};
    const gate = new Promise<void>((r) => (open = r));
    const holder = pool.dispatch("rerank", "interactive", "rerank", () => gate);
    const waiter = pool.dispatch("rerank", "interactive", "rerank", async () => "never");
    now = 200;
    await assert.rejects(waiter, (e) => e instanceof WorkerError && e.code === "saturated");
    open();
    await holder;
  } finally {
    await a.stop();
  }
});

// --- reporting -------------------------------------------------------------------

test("the worker view reports state, heartbeat, load and error rate, and no credentials", async () => {
  const f = await fleet();
  try {
    assert.equal(f.pool.workers()[0]!.state, "unknown");
    assert.equal(f.pool.workers()[0]!.lastHeartbeat, null);
    await f.pool.refresh();
    f.a.on("POST /v1/rerank", () => ({ status: 500, body: { message: `leak ${TOKENS.a}` } }));
    await rerank(f);
    await rerank(f);
    const [a, b] = f.pool.workers();
    assert.equal(a!.name, "a");
    assert.equal(a!.host, new URL(f.a.url).host);
    assert.equal(a!.state, "up");
    assert.ok(a!.lastHeartbeat);
    assert.deepEqual(a!.circuits, { embed: "closed", rerank: "closed", generate: "closed", documents: "closed" });
    assert.equal(a!.embedDisagrees, false);
    assert.equal(a!.calls, 2, "still preferred: two failures is under the threshold");
    assert.equal(b!.calls, 2, "and each call failed over");
    assert.equal(a!.errorRate, 1);
    assert.equal(b!.errorRate, 0);
    assert.equal(a!.rerankTransport, "llama-server-qmd-tokens");
    const text = JSON.stringify([f.pool.workers(), f.pool.load()]);
    assert.ok(!text.includes(TOKENS.a));
    assert.ok(!text.includes(TOKENS.b));
    // Each worker gets its own token and no one else's.
    assert.ok(f.a.requests.every((r) => r.authorization === `Bearer ${TOKENS.a}`));
    assert.ok(f.b.requests.every((r) => r.authorization === `Bearer ${TOKENS.b}`));
  } finally {
    await f.done();
  }
});

test("status reports a role only when some worker can serve it now", async () => {
  const f = await fleet({ a: { capabilities: ["rerank"] }, b: { capabilities: ["embed"] } });
  const all = (s: Record<Capability, boolean>) => [s.embed, s.rerank, s.generate];
  try {
    assert.deepEqual(all(f.pool.status()), [false, false, false], "nothing is known before a heartbeat");
    assert.equal(f.pool.canServe("rerank"), true, "but a first call may try");
    await f.pool.refresh();
    assert.deepEqual(all(f.pool.status()), [true, true, false]);
    await f.a.stop();
    await f.pool.refresh();
    assert.deepEqual(all(f.pool.status()), [true, false, false]);
  } finally {
    await f.done();
  }
});

test("heartbeats stay out of the per-call log", async () => {
  const worker = await new MockWorker(8).start();
  const seen: string[] = [];
  const llm = new RemoteLlm({ worker: { url: worker.url, token: undefined, embedDimensions: 8 }, models: { embed: URI, rerank: "r", generate: "g" }, fallback: null, onCall: (e) => seen.push(e.operation) });
  try {
    await llm.refreshStatus();
    await llm.rerank("q", [{ file: "x", text: "x" }]);
    assert.deepEqual(seen, ["rerank"]);
  } finally {
    await worker.stop();
  }
});
