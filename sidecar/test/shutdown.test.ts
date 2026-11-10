import assert from "node:assert/strict";
import path from "node:path";
import { test } from "node:test";
import { WorkerClient, WorkerError, WorkerLifecycle } from "../src/llm/client.ts";
import { RemoteLlm } from "../src/llm/remote.ts";
import { JobDeferred, JobQueue, type JobResult, type ReindexRequest } from "../src/jobs.ts";
import { SqliteJobStore } from "../src/queue-store.ts";
import { FINAL_MS, shutDown } from "../src/shutdown.ts";
import { FakeBackend } from "./fake-backend.ts";
import { tempDir } from "./helpers.ts";
import { EMBED_FILE, MockWorker } from "./mock-worker.ts";

const result: JobResult = { update: { collections: 1, indexed: 1, updated: 0, unchanged: 0, removed: 0, skipped: 0, needsEmbedding: 0 }, embed: null, metadata: { documents: 1, withMetadata: 0 } };
const req = (over: Partial<ReindexRequest> = {}): ReindexRequest => ({ collections: ["c"], embed: true, force: false, reason: "test", ...over });
const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

/** A worker that takes `ms` over every embedding batch, as a busy GPU does. */
async function slowWorker(ms: number): Promise<MockWorker> {
  const mock = await new MockWorker(8).start();
  const fast = mock.handlers.get("POST /v1/embed")!;
  mock.on("POST /v1/embed", async (body, request) => {
    await sleep(ms);
    return fast(body, request);
  });
  return mock;
}

// --- worker calls --------------------------------------------------------------------------------------------------

test("once shutdown refuses new calls, none reaches a worker, and those in flight go on", async () => {
  const mock = await slowWorker(300);
  const lifecycle = new WorkerLifecycle();
  const client = new WorkerClient({ url: mock.url, token: undefined, lifecycle });
  try {
    const inFlight = client.post("embed", "embed", "/v1/embed", { texts: ["a"] }, 1);
    await sleep(50);
    lifecycle.refuseNew();
    await assert.rejects(client.post("embed", "embed", "/v1/embed", { texts: ["b"] }, 1), (err: unknown) => err instanceof WorkerError && err.code === "closing");
    await assert.rejects(client.get("health", "/health"), (err: unknown) => err instanceof WorkerError && err.code === "closing");
    assert.ok(await inFlight, "the call already in flight finished");
    assert.equal(mock.requests.length, 1, "nothing new reached the worker");
  } finally {
    await mock.stop();
  }
});

test("aborting cuts off a call still waiting on a slow worker, at once", async () => {
  const mock = await slowWorker(5_000);
  const lifecycle = new WorkerLifecycle();
  const client = new WorkerClient({ url: mock.url, token: undefined, lifecycle });
  try {
    const inFlight = client.post("embed", "embed", "/v1/embed", { texts: ["a"] }, 1);
    await sleep(50);
    const started = Date.now();
    lifecycle.abortInFlight();
    await assert.rejects(inFlight, (err: unknown) => err instanceof WorkerError && err.code === "closing" && /shutting down/.test(err.message));
    assert.ok(Date.now() - started < 1_000, "the call ended when it was cut off, not when the worker answered");
  } finally {
    await mock.stop();
  }
});

test("a refused call leaves the worker's circuit alone and loads no model here", async () => {
  const mock = await new MockWorker(8).start();
  let fellBack = 0;
  const fallback = { embedBatch: async () => (fellBack++, []), tokenize: async () => [], detokenize: async () => "" };
  const llm = new RemoteLlm({ models: { embed: `hf:x/${EMBED_FILE}`, rerank: "r", generate: "g" }, workers: [{ name: "only", url: mock.url, token: undefined, capabilities: ["embed", "rerank", "generate"], priority: 1, maxConcurrent: 2 }], embedDimensions: 8, fallback });
  try {
    await llm.refreshStatus();
    llm.lifecycle.refuseNew();
    const before = mock.requests.length;
    assert.deepEqual(await llm.embedBatch(["query"]), [null]);
    await assert.rejects(llm.embedDocuments(["chunk"]), (err: unknown) => err instanceof WorkerError && err.code === "closing");
    assert.equal(mock.requests.length, before, "no call reached the worker");
    assert.equal(fellBack, 0, "shutdown did not fall back to this host's CPU");
    assert.equal(llm.pool.workers()[0]!.circuit, "closed");
  } finally {
    await llm.dispose();
    await mock.stop();
  }
});

// --- the queue -----------------------------------------------------------------------------------------------------

test("a job stopped by shutdown is kept as waiting, not failed, and runs at the next start", async () => {
  const file = path.join(await tempDir("memoria-shutdown-"), "q.sqlite");
  const store = new SqliteJobStore(file);
  const queue = new JobQueue(async () => {
    throw new JobDeferred("No worker can embed right now");
  }, { store });
  queue.stopping();
  const job = queue.enqueue(req());
  assert.equal(await queue.drain(1_000), true, "a parked job does not hold up the drain");
  assert.equal(job.state, "queued");
  assert.equal(job.error, null);
  assert.equal(queue.pending, 0);
  store.close();

  const reopened = new SqliteJobStore(file);
  let ran = 0;
  const next = new JobQueue(async () => (ran++, result), { store: reopened });
  await next.idle();
  assert.equal(ran, 1, "it ran again at the next start, with no retry delay");
  reopened.close();
});

test("suspending stops the running job, keeps it waiting, and settles once it has stopped", async () => {
  const file = path.join(await tempDir("memoria-shutdown-"), "q.sqlite");
  const store = new SqliteJobStore(file);
  let signalled = false;
  const queue = new JobQueue((_r, ctx) => new Promise<JobResult>((_resolve, reject) => {
    ctx.signal.addEventListener("abort", () => {
      signalled = true;
      reject(new Error("stopped"));
    });
  }), { store });
  queue.enqueue(req());
  await sleep(10);
  assert.equal(await queue.settled(20), false, "still running");
  queue.suspend();
  assert.equal(await queue.settled(1_000), true);
  assert.ok(signalled, "the running job was told to stop");
  store.close();
  const [saved] = new SqliteJobStore(file).load();
  assert.equal(saved!.job.state, "queued", "kept as waiting, not failed");
});

// --- the sequence --------------------------------------------------------------------------------------------------

test("shutdown refuses worker calls first, and ends inside its budget whatever hangs", async () => {
  const backend = new FakeBackend({ collections: [], docs: [] });
  const order: string[] = [];
  const queue = new JobQueue(() => new Promise<JobResult>(() => {}));
  queue.enqueue(req());
  await sleep(10);
  const started = Date.now();
  let warned = 0;
  await shutDown({
    // A request that never answers, and a job that never stops.
    closeServer: () => (order.push(`server, worker calls ${backend.workerCalls}`), new Promise(() => {})),
    jobs: queue,
    backend: {
      stopWorkerCalls: () => (order.push("refuse"), backend.stopWorkerCalls()),
      abortWorkerCalls: () => (order.push("abort"), backend.abortWorkerCalls()),
      close: async () => void order.push("close"),
    },
    closeStore: () => void order.push("store"),
    log: { warn: () => void warned++ },
  }, 300);
  const took = Date.now() - started;
  assert.deepEqual(order, ["refuse", "server, worker calls refused", "abort", "close", "store"]);
  assert.ok(took >= 300 && took < 300 + 2 * FINAL_MS + 500, `took ${took} ms`);
  assert.equal(warned, 3, "the drain, the open request and the job that would not stop were each reported");
});

test("with nothing in flight, shutdown does not wait out its budget", async () => {
  const queue = new JobQueue(async () => result);
  const started = Date.now();
  await shutDown({ closeServer: async () => {}, jobs: queue, backend: new FakeBackend({ collections: [], docs: [] }), closeStore: () => {}, log: { warn: () => assert.fail("nothing to warn about") } }, 10_000);
  assert.ok(Date.now() - started < 500);
});
