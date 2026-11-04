import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { readdir, readFile } from "node:fs/promises";
import path from "node:path";
import { createInterface } from "node:readline";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { buildApp } from "../src/app.ts";
import { JobCancelled, JobDeferred, JobQueue, type JobResult, type ReindexRequest } from "../src/jobs.ts";
import { SqliteJobStore } from "../src/queue-store.ts";
import { FakeBackend } from "./fake-backend.ts";
import { authHeaders, makeApp, ManualScheduler, tempDir, testConfig } from "./helpers.ts";

const result: JobResult = { update: { collections: 1, indexed: 1, updated: 0, unchanged: 0, removed: 0, skipped: 0, needsEmbedding: 0 }, embed: null, metadata: { documents: 1, withMetadata: 0 } };
const req = (over: Partial<ReindexRequest> = {}): ReindexRequest => ({ collections: ["c"], embed: true, force: false, reason: "test", ...over });
const dbFile = async () => path.join(await tempDir("memoria-queue-"), "index.sqlite.queue.sqlite");
const never = new Promise<JobResult>(() => {});

// --- the store ------------------------------------------------------------------

test("a job round-trips through the store exactly", async () => {
  const file = await dbFile();
  const store = new SqliteJobStore(file);
  const queue = new JobQueue(async (_r, ctx) => {
    ctx.noteWorker("desktop");
    ctx.noteWorker("macbook");
    ctx.noteWorker("desktop");
    return result;
  }, { store });
  const job = queue.enqueue(req({ collections: ["a", "b"], force: true, reason: "POST /reindex" }));
  await queue.idle();
  const expected = JSON.parse(JSON.stringify(job));
  store.close();

  const reopened = new SqliteJobStore(file);
  const [loaded] = reopened.load();
  assert.deepEqual(JSON.parse(JSON.stringify(loaded!.job)), expected);
  assert.equal(loaded!.job.state, "succeeded");
  assert.equal(loaded!.job.worker, "desktop,macbook");
  assert.equal(loaded!.job.attempts, 1);
  assert.equal(loaded!.job.priority, "background");
  reopened.close();
});

test("a null collection list stays null", async () => {
  const file = await dbFile();
  const store = new SqliteJobStore(file);
  const queue = new JobQueue(async () => result, { store });
  queue.enqueue(req({ collections: null }));
  await queue.idle();
  assert.equal(store.load()[0]!.job.request.collections, null);
  store.close();
});

test("a queue database from a newer version is not opened by this class", async () => {
  const file = await dbFile();
  const store = new SqliteJobStore(file);
  store.close();
  const { DatabaseSync } = await import("node:sqlite");
  const db = new DatabaseSync(file);
  db.exec("PRAGMA user_version = 999");
  db.close();
  assert.throws(() => new SqliteJobStore(file), /newer version/);
});

// --- restart ---------------------------------------------------------------------

test("a queued job runs after a restart, under the same id", async () => {
  const file = await dbFile();
  const store = new SqliteJobStore(file);
  const blocker = new JobQueue(() => never, { store });
  blocker.enqueue(req({ collections: ["running"] }));
  const queued = blocker.enqueue(req({ collections: ["waiting"] }));
  await new Promise((r) => setImmediate(r));
  store.close();

  const reopened = new SqliteJobStore(file);
  const runs: Array<string[] | null> = [];
  const queue = new JobQueue(async (r) => {
    runs.push(r.collections);
    return result;
  }, { store: reopened });
  assert.equal(queue.pending, 2);
  await queue.idle();
  assert.deepEqual(runs, [["running"], ["waiting"]], "in submission order");
  assert.equal(queue.get(queued.id)?.state, "succeeded");
  reopened.close();
});

test("a job interrupted while running runs again and counts the attempt", async () => {
  const file = await dbFile();
  const store = new SqliteJobStore(file);
  const before = new JobQueue(() => never, { store });
  const job = before.enqueue(req());
  await new Promise((r) => setImmediate(r));
  assert.equal(job.state, "running");
  store.close();

  const reopened = new SqliteJobStore(file);
  const queue = new JobQueue(async () => result, { store: reopened });
  assert.equal(queue.get(job.id)?.state, "queued", "back to waiting, not reported as running");
  assert.equal(queue.get(job.id)?.startedAt, null);
  await queue.idle();
  const after = queue.get(job.id)!;
  assert.equal(after.state, "succeeded");
  assert.equal(after.attempts, 2);
  reopened.close();
});

test("a job that keeps taking the process down is eventually given up on", async () => {
  const file = await dbFile();
  let id = "";
  for (let i = 0; i < 3; i++) {
    const store = new SqliteJobStore(file);
    const queue = new JobQueue(() => never, { store, maxCrashes: 3 });
    if (i === 0) id = queue.enqueue(req()).id;
    await new Promise((r) => setImmediate(r));
    assert.equal(queue.get(id)?.state, "running", `start ${i + 1}`);
    store.close();
  }
  const store = new SqliteJobStore(file);
  const queue = new JobQueue(() => never, { store, maxCrashes: 3 });
  const job = queue.get(id)!;
  assert.equal(job.state, "failed");
  assert.match(job.error!, /Gave up after 3 restarts/);
  assert.equal(queue.pending, 0);
  store.close();
});

test("a debounced job keeps its schedule and its merged reason across a restart", async () => {
  const file = await dbFile();
  const scheduler = new ManualScheduler();
  const store = new SqliteJobStore(file);
  const before = new JobQueue(async () => result, { store, scheduler, debounceMs: 60_000 });
  const job = before.enqueue(req({ reason: "d0" }), { debounce: true });
  before.enqueue(req({ reason: "d1" }), { debounce: true });
  await scheduler.advance(10_000);
  store.close();

  const reopened = new SqliteJobStore(file);
  const runs: string[] = [];
  const later = new ManualScheduler();
  await later.advance(10_000); // same clock position as before the crash
  const queue = new JobQueue(async (r) => {
    runs.push(r.reason);
    return result;
  }, { store: reopened, scheduler: later, debounceMs: 60_000 });
  assert.equal(queue.counts().held, 1);
  assert.equal(queue.get(job.id)?.state, "queued");

  // A write after the restart still joins it, and the count carries on.
  queue.enqueue(req({ reason: "d2" }), { debounce: true });
  assert.equal(queue.get(job.id)?.request.reason, "d0 (+2 more)");
  await later.advance(59_000);
  assert.equal(runs.length, 0);
  await later.advance(1_000);
  await queue.idle();
  assert.deepEqual(runs, ["d0 (+2 more)"]);
  reopened.close();
});

test("a job past due at restart runs promptly", async () => {
  const file = await dbFile();
  const scheduler = new ManualScheduler();
  const store = new SqliteJobStore(file);
  const before = new JobQueue(async () => result, { store, scheduler, debounceMs: 1_000 });
  const job = before.enqueue(req(), { debounce: true });
  store.close();

  const reopened = new SqliteJobStore(file);
  const later = new ManualScheduler();
  await later.advance(5_000);
  const queue = new JobQueue(async () => result, { store: reopened, scheduler: later, debounceMs: 1_000 });
  await later.advance(0);
  await queue.idle();
  assert.equal(queue.get(job.id)?.state, "succeeded");
  reopened.close();
});

test("finished history survives a restart, and the cap still applies", async () => {
  const file = await dbFile();
  const store = new SqliteJobStore(file);
  const queue = new JobQueue(async () => result, { store, keep: 3 });
  for (let i = 0; i < 6; i++) {
    queue.enqueue(req({ reason: `j${i}`, collections: [`c${i}`] }));
    await queue.idle();
  }
  store.close();
  const reopened = new SqliteJobStore(file);
  assert.equal(reopened.load().length, 3, "trimmed rows are deleted, not just hidden");
  const after = new JobQueue(async () => result, { store: reopened, keep: 3 });
  assert.deepEqual(after.list().map((j) => j.request.reason), ["j5", "j4", "j3"]);
  assert.equal(after.pending, 0);
  reopened.close();
});

// --- a real crash -----------------------------------------------------------------

const crashFixture = fileURLToPath(new URL("./fixtures/queue-crash.ts", import.meta.url));

/** Start the fixture, wait for its report, and kill it with no chance to clean up. */
async function crashAfter(queueDb: string, root: string, mode: string): Promise<{ status: number; id: string; state: string }> {
  const child = spawn(process.execPath, [crashFixture, queueDb, root, mode], { stdio: ["ignore", "pipe", "ignore"] });
  const exited = new Promise<void>((resolve) => child.once("exit", () => resolve()));
  const lines = createInterface({ input: child.stdout });
  const report = await Promise.race([
    new Promise<string>((resolve) => lines.once("line", resolve)),
    exited.then(() => {
      throw new Error("the fixture exited before reporting");
    }),
  ]);
  child.kill("SIGKILL");
  await exited;
  return JSON.parse(report);
}

for (const [mode, expectedState] of [["held", "queued"], ["running", "running"]] as const) {
  test(`a job accepted before a real crash (${mode}) completes after the restart`, async () => {
    const queueDb = await dbFile();
    const root = await tempDir("memoria-crash-");
    const before = await crashAfter(queueDb, root, mode);
    assert.equal(before.status, 202);
    assert.equal(before.state, expectedState);
    assert.ok((await readdir(root)).includes("survivor.md"), "the document was on disk");

    const backend = new FakeBackend({ collections: [{ name: "c", root }], docs: [] });
    const store = new SqliteJobStore(queueDb);
    const app = await buildApp({ backend, config: testConfig({ reindexDebounceMs: 60_000 }), jobStore: store, logger: false });
    try {
      app.ctx.jobs.flush();
      await app.ctx.jobs.idle();
      const job = (await app.inject({ method: "GET", url: `/reindex/jobs/${before.id}`, headers: authHeaders })).json().job;
      assert.equal(job.state, "succeeded");
      assert.equal(job.result.update.indexed, 1);
      const found = await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { query: "PRIVATE-BODY-TEXT" } });
      assert.equal(found.json().total, 1);
    } finally {
      await app.close();
      store.close();
    }
  });
}

test("the queue database holds no document content or credentials", async () => {
  const queueDb = await dbFile();
  const root = await tempDir("memoria-crash-");
  const TOKEN = "worker-token-0123456789abcdef";
  const backend = new FakeBackend({ collections: [{ name: "c", root }], docs: [] });
  const store = new SqliteJobStore(queueDb);
  const app = await buildApp({ backend, config: testConfig({ reindexDebounceMs: 0 }), jobStore: store, logger: false });
  backend.workerViews = [];
  backend.embedImpl = async (options) => {
    options.onWorker?.("desktop");
    return { docsProcessed: 1, chunksEmbedded: 1, errors: 0, durationMs: 1 };
  };
  try {
    await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "c", path: "secret.md", content: `# Doc\n\nPRIVATE-BODY-TEXT ${TOKEN}\n` } });
    await app.ctx.jobs.idle();
  } finally {
    await app.close();
    store.close();
  }
  // Checkpoint-free: read the main file and the write-ahead log, as raw bytes.
  const files = (await readdir(path.dirname(queueDb))).filter((f) => f.startsWith(path.basename(queueDb)));
  assert.ok(files.length > 0);
  for (const file of files) {
    const bytes = (await readFile(path.join(path.dirname(queueDb), file))).toString("latin1");
    assert.ok(!bytes.includes("PRIVATE-BODY-TEXT"), file);
    assert.ok(!bytes.includes(TOKEN), file);
  }
  const reopened = new SqliteJobStore(queueDb);
  const [row] = reopened.load();
  assert.equal(row!.job.request.reason, "POST /documents c/secret.md", "the path is what it names");
  assert.equal(row!.job.worker, "desktop");
  reopened.close();
});

// --- deferral --------------------------------------------------------------------

test("a job with no worker to use waits, backing off, and finishes once one returns", async () => {
  const scheduler = new ManualScheduler();
  let up = false;
  let runs = 0;
  const queue = new JobQueue(async () => {
    runs++;
    if (!up) throw new JobDeferred("no worker");
    return result;
  }, { scheduler, deferBaseMs: 1_000, deferMaxMs: 3_000 });
  const job = queue.enqueue(req());
  await scheduler.advance(0);
  assert.equal(runs, 1);
  assert.equal(job.state, "queued", "waiting, not failed");
  assert.equal(job.error, "no worker", "and it says why");
  assert.equal(job.startedAt, null);
  assert.equal(queue.pending, 1);
  assert.equal(queue.counts().held, 1);

  await scheduler.advance(999);
  assert.equal(runs, 1);
  await scheduler.advance(1);
  assert.equal(runs, 2);
  await scheduler.advance(1_999);
  assert.equal(runs, 2, "the wait doubled");
  await scheduler.advance(1);
  assert.equal(runs, 3);
  await scheduler.advance(3_000);
  assert.equal(runs, 4, "capped");

  up = true;
  await scheduler.advance(3_000);
  await queue.idle();
  assert.equal(job.state, "succeeded");
  assert.equal(job.error, null);
  assert.equal(job.attempts, 5);
});

test("a deferred job survives a restart still waiting", async () => {
  const file = await dbFile();
  const scheduler = new ManualScheduler();
  const store = new SqliteJobStore(file);
  const before = new JobQueue(async () => { throw new JobDeferred("no worker"); }, { store, scheduler, deferBaseMs: 1_000 });
  const job = before.enqueue(req());
  await scheduler.advance(0);
  assert.equal(job.state, "queued");
  store.close();

  const reopened = new SqliteJobStore(file);
  const later = new ManualScheduler();
  const queue = new JobQueue(async () => result, { store: reopened, scheduler: later });
  assert.equal(queue.get(job.id)?.error, "no worker");
  await later.advance(1_000);
  await queue.idle();
  assert.equal(queue.get(job.id)?.state, "succeeded");
  reopened.close();
});

test("writes during a deferral join the job without resetting its retry", async () => {
  const scheduler = new ManualScheduler();
  let runs = 0;
  const queue = new JobQueue(async () => {
    runs++;
    if (runs === 1) throw new JobDeferred("no worker");
    return result;
  }, { scheduler, debounceMs: 10_000, deferBaseMs: 1_000 });
  const job = queue.enqueue(req({ reason: "first" }));
  await scheduler.advance(0);
  assert.equal(queue.enqueue(req({ reason: "second" }), { debounce: true }), job);
  await scheduler.advance(1_000);
  await queue.idle();
  assert.equal(runs, 2);
});

test("POST /reindex releases a job waiting on a retry", async () => {
  const scheduler = new ManualScheduler();
  let runs = 0;
  const queue = new JobQueue(async () => {
    runs++;
    if (runs === 1) throw new JobDeferred("no worker");
    return result;
  }, { scheduler, deferBaseMs: 3_600_000 });
  const job = queue.enqueue(req());
  await scheduler.advance(0);
  assert.equal(queue.enqueue(req()), job);
  await queue.idle();
  assert.equal(runs, 2);
});

test("drain does not wait on a job that cannot finish yet", async () => {
  const scheduler = new ManualScheduler();
  const queue = new JobQueue(async () => { throw new JobDeferred("no worker"); }, { scheduler, deferBaseMs: 3_600_000 });
  queue.enqueue(req());
  await scheduler.advance(0);
  assert.equal(await queue.drain(1_000), true);
  assert.equal(queue.pending, 1);
});

// --- retry and cancel --------------------------------------------------------------

test("retry runs a failed job again under the same id, and refuses anything else", async () => {
  let fail = true;
  const queue = new JobQueue(async () => {
    if (fail) throw new Error("kaboom");
    return result;
  });
  const job = queue.enqueue(req());
  await queue.idle();
  assert.equal(job.state, "failed");
  assert.equal(job.error, "kaboom");

  fail = false;
  assert.equal(queue.retry(job.id), job);
  assert.equal(job.state, "queued");
  assert.equal(job.error, null);
  assert.equal(job.finishedAt, null);
  await queue.idle();
  assert.equal(job.state, "succeeded");
  assert.equal(job.attempts, 2);

  assert.equal(queue.retry(job.id), null, "it succeeded");
  assert.equal(queue.retry("nope"), null);
});

test("cancel ends a waiting job at once, with its timer", async () => {
  const scheduler = new ManualScheduler();
  const runs: string[] = [];
  const queue = new JobQueue(async (r) => {
    runs.push(r.reason);
    return result;
  }, { scheduler, debounceMs: 1_000 });
  const held = queue.enqueue(req({ reason: "held" }), { debounce: true });
  assert.equal(queue.cancel(held.id), held);
  assert.equal(held.state, "failed");
  assert.equal(held.error, "Cancelled");
  assert.ok(held.finishedAt);
  assert.equal(queue.pending, 0);
  assert.equal(scheduler.pendingTimers, 0);
  await scheduler.advance(5_000);
  assert.deepEqual(runs, []);

  const ready = queue.enqueue(req({ reason: "ready", collections: ["x"] }));
  assert.equal(queue.cancel(ready.id), ready);
  await new Promise((r) => setImmediate(r));
  assert.deepEqual(runs, [], "it never started");
  assert.equal(queue.cancel(ready.id), null, "already finished");
  assert.equal(queue.cancel("nope"), null);
});

test("cancel signals a running job, which fails when it stops", async () => {
  let aborted = false;
  const queue = new JobQueue(async (_r, ctx) => {
    await new Promise<void>((resolve) => ctx.signal.addEventListener("abort", () => resolve()));
    aborted = true;
    throw new JobCancelled();
  });
  const job = queue.enqueue(req());
  await new Promise((r) => setImmediate(r));
  assert.equal(job.state, "running");
  assert.equal(queue.cancel(job.id), job);
  await queue.idle();
  assert.equal(aborted, true);
  assert.equal(job.state, "failed");
  assert.equal(job.error, "Cancelled");
});

test("a cancelled job that defers is cancelled, not rescheduled", async () => {
  const queue = new JobQueue(async (_r, ctx) => {
    await new Promise<void>((resolve) => ctx.signal.addEventListener("abort", () => resolve()));
    throw new JobDeferred("no worker");
  });
  const job = queue.enqueue(req());
  await new Promise((r) => setImmediate(r));
  queue.cancel(job.id);
  await queue.idle();
  assert.equal(job.state, "failed");
  assert.equal(job.error, "Cancelled");
});

test("cancelling a job with a retry delay clears it", async () => {
  const scheduler = new ManualScheduler();
  const queue = new JobQueue(async () => { throw new JobDeferred("no worker"); }, { scheduler, deferBaseMs: 1_000 });
  const job = queue.enqueue(req());
  await scheduler.advance(0);
  queue.cancel(job.id);
  assert.equal(job.state, "failed");
  assert.equal(queue.pending, 0);
  assert.equal(scheduler.pendingTimers, 0);
});

test("retry and cancel persist", async () => {
  const file = await dbFile();
  const store = new SqliteJobStore(file);
  const queue = new JobQueue(() => never, { store, debounceMs: 3_600_000, scheduler: new ManualScheduler() });
  const job = queue.enqueue(req(), { debounce: true });
  queue.cancel(job.id);
  assert.equal(store.load()[0]!.job.state, "failed");
  assert.equal(store.load()[0]!.dueAt, null);
  queue.retry(job.id);
  assert.equal(store.load()[0]!.job.state, "queued");
  // Let the worker pick it up before the store closes under it.
  await new Promise((r) => setImmediate(r));
  store.close();
});

test("job states stay the four the API knows", async () => {
  const { jobSchema } = await import("../src/schemas.ts");
  assert.deepEqual(jobSchema.properties.state.enum, ["queued", "running", "succeeded", "failed"]);
});

// --- the routes -------------------------------------------------------------------

test("GET /queue reports jobs by state, and model calls when there is a pool", async () => {
  const scheduler = new ManualScheduler();
  const { app, backend } = await makeApp({ scheduler, config: { reindexDebounceMs: 60_000 } });
  try {
    await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "x.md", content: "# x\n" } });
    const body = (await app.inject({ method: "GET", url: "/queue", headers: authHeaders })).json();
    assert.deepEqual(body, {
      persistence: { enabled: false, healthy: false },
      jobs: { queued: 1, held: 1, running: 0, succeeded: 0, failed: 0 },
      classes: { interactive: { inFlight: 0, waiting: 0 }, background: { inFlight: 0, waiting: 0 } },
      workers: [],
      deadLetters: 0,
    });
    backend.load = {
      classes: { interactive: { inFlight: 1, waiting: 2 }, background: { inFlight: 3, waiting: 4 } },
      workers: [{ name: "desktop", interactive: 1, background: 3, queueDepth: 5 }],
    };
    const withPool = (await app.inject({ method: "GET", url: "/queue", headers: authHeaders })).json();
    assert.deepEqual(withPool.classes, backend.load.classes);
    assert.deepEqual(withPool.workers, backend.load.workers);
  } finally {
    await app.close();
  }
});

test("POST /queue/jobs/{id}:retry and :cancel work and refuse what they should", async () => {
  const scheduler = new ManualScheduler();
  const { app, backend } = await makeApp({ scheduler, config: { reindexDebounceMs: 60_000 } });
  const action = (id: string, verb: string) => app.inject({ method: "POST", url: `/queue/jobs/${id}:${verb}`, headers: authHeaders });
  try {
    const id = (await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "x.md", content: "# x\n" } })).json().job.id;

    const conflict = await action(id, "retry");
    assert.equal(conflict.statusCode, 409);
    assert.equal(conflict.json().error, "not_retryable");

    const cancelled = await action(id, "cancel");
    assert.equal(cancelled.statusCode, 202, cancelled.body);
    assert.equal(cancelled.json().job.state, "failed");
    assert.equal(cancelled.json().job.error, "Cancelled");
    assert.equal((await action(id, "cancel")).statusCode, 409);

    const retried = await action(id, "retry");
    assert.equal(retried.statusCode, 202);
    assert.equal(retried.json().job.id, id);
    await app.ctx.jobs.idle();
    assert.equal(backend.calls.filter((c) => c.method === "update").length, 1);
    assert.equal((await app.inject({ method: "GET", url: `/reindex/jobs/${id}`, headers: authHeaders })).json().job.state, "succeeded");

    for (const verb of ["retry", "cancel"]) {
      const missing = await action("nope", verb);
      assert.equal(missing.statusCode, 404);
      assert.equal(missing.json().error, "not_found");
    }
    assert.equal((await app.inject({ method: "POST", url: `/queue/jobs/${id}:retry` })).statusCode, 401);
    assert.equal((await app.inject({ method: "GET", url: "/queue" })).statusCode, 401);
    assert.equal((await app.inject({ method: "GET", url: "/workers" })).statusCode, 401);
  } finally {
    await app.close();
  }
});

test("POST /documents and POST /reindex keep their shape, with additions only", async () => {
  const { app } = await makeApp();
  try {
    const doc = (await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "x.md", content: "# x\n" } })).json();
    assert.deepEqual(Object.keys(doc).sort(), ["bytes", "collection", "created", "file", "job", "metadata", "path"]);
    assert.deepEqual(Object.keys(doc.job.request).sort(), ["collections", "embed", "force", "reason"]);
    for (const key of ["id", "state", "request", "createdAt", "startedAt", "finishedAt", "result", "error"]) assert.ok(key in doc.job, key);
    await app.ctx.jobs.idle();
    const job = (await app.inject({ method: "POST", url: "/reindex", headers: authHeaders, payload: {} })).json().job;
    assert.equal(job.priority, "background");
    assert.equal(job.attempts, 0);
    assert.equal(job.worker, null);
  } finally {
    await app.close();
  }
});

test("GET /workers is empty in local mode and passes through only declared fields", async () => {
  const { app, backend } = await makeApp();
  try {
    assert.deepEqual((await app.inject({ method: "GET", url: "/workers", headers: authHeaders })).json(), { mode: "local", workers: [] });
    backend.health = { mode: "remote", degraded: false, unavailable: [], workersUp: 1, workersTotal: 1, embedDisagreement: [], documentsSetAside: 0 };
    const view = { name: "d", host: "h:1", state: "up" as const, circuit: "closed" as const, circuits: { embed: "closed" as const, rerank: "closed" as const, generate: "closed" as const, documents: "closed" as const }, embedDisagrees: false, capabilities: ["embed" as const], priority: 1, maxConcurrent: 2, embedModelCompatible: true, rerankTransport: null, lastHeartbeat: null, inFlight: 0, queueDepth: 0, calls: 0, errorRate: 0 };
    // Even if a token somehow reached the view, the response schema would drop it.
    backend.workerViews = [{ ...view, token: "should-not-appear" } as never];
    const body = (await app.inject({ method: "GET", url: "/workers", headers: authHeaders })).body;
    assert.ok(!body.includes("should-not-appear"));
    assert.deepEqual(JSON.parse(body).workers, [view]);
  } finally {
    await app.close();
  }
});

test("/ready and search say so when degraded, and warmup does not read as done", async () => {
  const { app, backend } = await makeApp({ modelsResident: false });
  try {
    let ready = (await app.inject({ method: "GET", url: "/ready", headers: authHeaders })).json();
    assert.equal(ready.status, "warming");
    assert.equal(ready.llm.mode, "local");
    assert.equal(ready.llm.degraded, false);

    backend.health = { mode: "remote", degraded: true, unavailable: ["rerank", "generate"], workersUp: 0, workersTotal: 2, embedDisagreement: ["x", "y"], documentsSetAside: 4 };
    app.ctx.warmup.done = true;
    const response = await app.inject({ method: "GET", url: "/ready", headers: authHeaders });
    assert.equal(response.statusCode, 200);
    ready = response.json();
    assert.equal(ready.status, "degraded");
    assert.deepEqual(ready.llm, { mode: "remote", degraded: true, unavailable: ["rerank", "generate"], workersUp: 0, workersTotal: 2, embedDisagreement: ["x", "y"], documentsSetAside: 4 });
    assert.equal(ready.warmup.done, false, "a stored flag must not read as healthy");
    assert.match(ready.warmup.error, /rerank, generate/);
    assert.equal(ready.models.resident, false);
    assert.equal((await app.inject({ method: "GET", url: "/ready?requireModels=true", headers: authHeaders })).statusCode, 503);

    backend.degradedReasons = ["no_rerank", "no_expansion", "cpu_embedding"];
    const search = (await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { query: "api keys" } })).json();
    assert.equal(search.degraded, true);
    assert.deepEqual(search.degradedReasons, ["no_rerank", "no_expansion", "cpu_embedding"]);
    assert.ok(search.total > 0);

    backend.degradedReasons = [];
    const full = (await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { query: "api keys" } })).json();
    assert.equal(full.degraded, false);
    assert.deepEqual(full.degradedReasons, []);
  } finally {
    await app.close();
  }
});

test("a job's update runs and metadata refreshes even when embedding has to wait", async () => {
  const scheduler = new ManualScheduler();
  const { app, backend } = await makeApp({ scheduler, config: { reindexDebounceMs: 0 } });
  try {
    backend.embedImpl = async () => {
      throw new JobDeferred("No worker can embed right now");
    };
    const job = (await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "wait.md", content: "# Wait\n\nmarkerwaiting\n" } })).json().job;
    await scheduler.advance(0);
    // The scan is real file I/O, so wait for the job to come back from running.
    const deadline = Date.now() + 5_000;
    while (app.ctx.jobs.get(job.id)?.attempts === 0 || app.ctx.jobs.get(job.id)?.state === "running") {
      assert.ok(Date.now() < deadline, "the job never deferred");
      await new Promise((r) => setTimeout(r, 5));
    }
    const record = (await app.inject({ method: "GET", url: `/reindex/jobs/${job.id}`, headers: authHeaders })).json().job;
    assert.equal(record.state, "queued");
    assert.match(record.error, /No worker/);
    // The scan happened, so the document can already be found lexically.
    const found = await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { query: "markerwaiting" } });
    assert.equal(found.json().total, 1);

    backend.embedImpl = null;
    app.ctx.jobs.flush();
    await app.ctx.jobs.idle();
    assert.equal((await app.inject({ method: "GET", url: `/reindex/jobs/${job.id}`, headers: authHeaders })).json().job.state, "succeeded");
  } finally {
    await app.close();
  }
});

test("dead letters are listed and cleared through the routes, and clearing queues a job", async () => {
  const { app, backend } = await makeApp();
  try {
    backend.deadLetterList = [{ hash: "h", path: "c/a.md", reason: "HTTP 400", at: "2026-01-01T00:00:00.000Z" }];
    assert.equal((await app.inject({ method: "GET", url: "/queue/dead-letters" })).statusCode, 401);
    assert.equal((await app.inject({ method: "POST", url: "/queue/dead-letters:clear" })).statusCode, 401);
    assert.equal((await app.inject({ method: "GET", url: "/queue", headers: authHeaders })).json().deadLetters, 1);
    assert.deepEqual((await app.inject({ method: "GET", url: "/queue/dead-letters", headers: authHeaders })).json().documents, backend.deadLetterList);
    const cleared = await app.inject({ method: "POST", url: "/queue/dead-letters:clear", headers: authHeaders });
    assert.equal(cleared.statusCode, 202);
    assert.equal(cleared.json().documents, 1);
    assert.equal(cleared.json().job.request.embed, true);
    assert.deepEqual(backend.deadLetterList, []);
  } finally {
    await app.close();
  }
});

test("the worker-purge route is gone", async () => {
  const { app } = await makeApp();
  try {
    assert.equal((await app.inject({ method: "POST", url: "/workers/a:purge-vectors", headers: authHeaders })).statusCode, 404);
  } finally {
    await app.close();
  }
});

test("purging the cache while another process holds the index says busy, with a 503", async () => {
  const { app, backend } = await makeApp();
  try {
    backend.purgeCache = () => {
      throw Object.assign(new Error("database is locked"), { code: "SQLITE_BUSY" });
    };
    const busy = await app.inject({ method: "POST", url: "/cache:purge", headers: authHeaders });
    assert.equal(busy.statusCode, 503);
    assert.match(busy.body, /index is busy/);
    backend.purgeCache = () => {
      throw new Error("disk full");
    };
    assert.equal((await app.inject({ method: "POST", url: "/cache:purge", headers: authHeaders })).statusCode, 500);
  } finally {
    await app.close();
  }
});
