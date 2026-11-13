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

test("a scan thread waits while the main thread writes, and goes on the moment it is done", async () => {
  const gate = new WriteGate();
  const source = `
    const { workerData, parentPort } = require("node:worker_threads");
    const writers = workerData.writers;
    parentPort.postMessage("waiting");
    const started = Date.now();
    for (;;) {
      const count = Atomics.load(writers, 0);
      if (count === 0) break;
      Atomics.wait(writers, 0, count, 5000);
    }
    parentPort.postMessage(Date.now() - started);
  `;
  let release = (): void => {};
  const held = new Promise<void>((resolve) => (release = resolve));
  Atomics.add(gate.writers, 0, 1);
  const worker = new Worker(source, { eval: true, workerData: { writers: gate.writers } });
  const messages: unknown[] = [];
  const waited = new Promise<number>((resolve) => worker.on("message", (m) => (m === "waiting" ? messages.push(m) : resolve(m as number))));
  await new Promise((r) => setTimeout(r, 150));
  assert.deepEqual(messages, ["waiting"]);
  // As `around` ends a write: the count drops and the scan is woken.
  setTimeout(() => {
    Atomics.sub(gate.writers, 0, 1);
    Atomics.notify(gate.writers, 0);
    release();
  }, 0);
  await held;
  const ms = await waited;
  assert.ok(ms >= 100 && ms < 2_000, `the thread waited ${ms} ms`);
  await worker.terminate();
});

test("around wakes a waiting thread when the write ends", async () => {
  const gate = new WriteGate();
  const source = `
    const { workerData, parentPort } = require("node:worker_threads");
    const writers = workerData.writers;
    while (Atomics.load(writers, 0) === 0) Atomics.wait(writers, 0, 0, 5);
    parentPort.postMessage("saw the write");
    const started = Date.now();
    // Not polling: only a notify wakes this before its 10 s timeout.
    Atomics.wait(writers, 0, 1, 10000);
    parentPort.postMessage(Date.now() - started);
  `;
  const worker = new Worker(source, { eval: true, workerData: { writers: gate.writers } });
  const messages: unknown[] = [];
  const woken = new Promise<number>((resolve) => worker.on("message", (m) => (m === "saw the write" ? messages.push(m) : resolve(m as number))));
  gate.around(() => {
    const until = Date.now() + 200;
    while (Date.now() < until && messages.length === 0) Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 5);
  });
  const ms = await woken;
  assert.ok(ms < 2_000, `woken after ${ms} ms, not at the timeout`);
  await worker.terminate();
});
