import assert from "node:assert/strict";
import { test } from "node:test";
import { Worker } from "node:worker_threads";
import { WriteGate, yieldToMainWrites, type GuardedDb, type GuardedStatement } from "../src/write-gate.ts";

/** A connection that records, for each call, how many writes the gate counted as under way. */
function recordingDb(gate: WriteGate): { db: GuardedDb; seen: string[] } {
  const seen: string[] = [];
  const counted = (): number => Atomics.load(gate.writers, 0);
  const db: GuardedDb = {
    prepare(sql: string): GuardedStatement {
      return {
        reader: sql.startsWith("SELECT"),
        run(...args: unknown[]) {
          seen.push(`run ${sql} ${JSON.stringify(args)} with ${counted()}`);
          return { changes: 1 };
        },
      };
    },
    exec(sql: string) {
      seen.push(`exec ${sql} with ${counted()}`);
      return "done";
    },
  };
  return { db, seen };
}

test("a write counts as under way while it runs, and not after", () => {
  const gate = new WriteGate();
  assert.equal(gate.around(() => Atomics.load(gate.writers, 0)), 1);
  assert.equal(Atomics.load(gate.writers, 0), 0);
  assert.equal(gate.around(() => gate.around(() => Atomics.load(gate.writers, 0))), 2, "writes nest");
});

test("a write that throws stops counting, and its error goes on", () => {
  const gate = new WriteGate();
  assert.throws(() => gate.around(() => {
    throw new Error("disk full");
  }), /disk full/);
  assert.equal(Atomics.load(gate.writers, 0), 0);
});

test("a guarded connection counts each statement that writes, and each exec, while it runs", () => {
  const gate = new WriteGate();
  const { db, seen } = recordingDb(gate);
  gate.guard(db);
  const write = db.prepare("INSERT INTO llm_cache VALUES (?)");
  assert.deepEqual(write.run("k"), { changes: 1 }, "the statement's own result comes back");
  db.prepare("SELECT 1").run();
  assert.equal(db.exec("DELETE FROM llm_cache"), "done");
  assert.deepEqual(seen, ["run INSERT INTO llm_cache VALUES (?) [\"k\"] with 1", "run SELECT 1 [] with 0", "exec DELETE FROM llm_cache with 1"]);
  assert.equal(Atomics.load(gate.writers, 0), 0);
});

test("with no write under way, a scan does not wait", () => {
  const started = performance.now();
  yieldToMainWrites(new Int32Array(new SharedArrayBuffer(4)), 5_000);
  assert.ok(performance.now() - started < 50);
});

test("a write stuck under way holds a scan for the cap at most", () => {
  const writers = new Int32Array(new SharedArrayBuffer(4));
  Atomics.store(writers, 0, 1);
  const started = performance.now();
  yieldToMainWrites(writers, 80);
  const took = performance.now() - started;
  assert.ok(took >= 75 && took < 1_000, `waited ${took} ms`);
});

test("a cap that has run out exactly ends the wait, even on a clock that stands still", () => {
  const writers = new Int32Array(new SharedArrayBuffer(4));
  Atomics.store(writers, 0, 1);
  const started = performance.now();
  yieldToMainWrites(writers, 0, () => 1_000);
  assert.ok(performance.now() - started < 50);
});

test("the cap is counted on the clock given", () => {
  const writers = new Int32Array(new SharedArrayBuffer(4));
  Atomics.store(writers, 0, 1);
  let now = 0;
  // The clock jumps past the cap at once: no wait at all.
  const started = performance.now();
  yieldToMainWrites(writers, 10_000, () => (now += 20_000));
  assert.ok(performance.now() - started < 50);
});

/**
 * A thread that runs `yieldToMainWrites` from the module itself, as the scan does. It sets
 * `ready` once it has seen a write under way, so the test never depends on how long a thread
 * takes to start, and posts the time it went on.
 */
const yielder = `
  const { workerData, parentPort } = require("node:worker_threads");
  const { writers, ready } = workerData;
  while (Atomics.load(writers, 0) === 0) Atomics.wait(writers, 0, 0, 5);
  Atomics.store(ready, 0, 1);
  Atomics.notify(ready, 0);
  import(workerData.module).then(({ yieldToMainWrites }) => {
    yieldToMainWrites(writers, 10000);
    parentPort.postMessage(Date.now());
  });
`;
const gateModule = new URL("../src/write-gate.ts", import.meta.url).href;

test("a scan thread waits while the main thread writes, and goes on once it is done", { timeout: 30_000 }, async () => {
  const gate = new WriteGate();
  const ready = new Int32Array(new SharedArrayBuffer(4));
  Atomics.add(gate.writers, 0, 1);
  const worker = new Worker(yielder, { eval: true, workerData: { writers: gate.writers, ready, module: gateModule } });
  try {
    const wentOn = new Promise<number>((resolve) => worker.once("message", resolve));
    let early: number | null = null;
    void wentOn.then((at) => (early = at));
    for (let waited = 0; Atomics.load(ready, 0) === 0; waited += 10) {
      assert.ok(waited < 20_000, "the thread never saw the write");
      await new Promise((r) => setTimeout(r, 10));
    }
    await new Promise((r) => setTimeout(r, 100));
    assert.equal(early, null, "it does not go on while the write is under way");
    const released = Date.now();
    Atomics.sub(gate.writers, 0, 1);
    Atomics.notify(gate.writers, 0);
    const at = await wentOn;
    assert.ok(at >= released && at - released < 2_000, `it went on ${at - released} ms after the write ended`);
  } finally {
    await worker.terminate();
  }
});

test("around wakes a waiting scan thread when its write ends, not at the thread's timeout", { timeout: 30_000 }, async () => {
  const gate = new WriteGate();
  const ready = new Int32Array(new SharedArrayBuffer(4));
  const worker = new Worker(yielder, { eval: true, workerData: { writers: gate.writers, ready, module: gateModule } });
  try {
    const wentOn = new Promise<number>((resolve) => worker.once("message", resolve));
    let ended = 0;
    gate.around(() => {
      // Held until the thread has seen this write, and a little more so it is waiting on it.
      Atomics.wait(ready, 0, 0, 20_000);
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 200);
      ended = Date.now();
    });
    assert.equal(Atomics.load(ready, 0), 1, "the thread saw the write");
    const at = await wentOn;
    assert.ok(at - ended < 2_000, `woken ${at - ended} ms after the write ended, where it would otherwise wait 10 s`);
  } finally {
    await worker.terminate();
  }
});

