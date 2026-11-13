import assert from "node:assert/strict";
import { getEventListeners } from "node:events";
import { test } from "node:test";
import { answerScan, errorFrom, ScanThreads } from "../src/scan.ts";
import { ScanStopped } from "../src/scan-batches.ts";

const entry = new URL("./fixtures/scan-thread.ts", import.meta.url);
const threads = (maxHeapMb = 256): ScanThreads => new ScanThreads({ entry, maxHeapMb });
const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

/** Resolves once nothing is running, or fails after `ms`. */
async function idle(scans: ScanThreads, ms = 5_000): Promise<void> {
  for (let waited = 0; scans.running > 0; waited += 10) {
    assert.ok(waited < ms, "the thread is still running");
    await sleep(10);
  }
}

test("a scan answers with what its thread found, and the thread is gone after", async () => {
  const scans = threads();
  const result = await scans.run<{ indexed: number }>({ mode: "ok", value: { indexed: 52_000 } });
  assert.deepEqual(result, { indexed: 52_000 });
  await idle(scans);
});

test("the thread is counted while it runs", async () => {
  const scans = threads();
  const controller = new AbortController();
  const run = scans.run({ mode: "until-stopped" }, controller.signal);
  assert.equal(scans.running, 1);
  controller.abort();
  await assert.rejects(run, ScanStopped);
  await idle(scans);
});

test("a scan that failed on its thread fails with the thread's message", async () => {
  await assert.rejects(threads().run({ mode: "fail" }), (err: unknown) => err instanceof Error && !(err instanceof ScanStopped) && err.message === "the index is not there");
});

test("an abort asks the thread to stop after its document, and the scan rejects as stopped", async () => {
  const scans = threads();
  const controller = new AbortController();
  const run = scans.run({ mode: "until-stopped" }, controller.signal);
  await sleep(50);
  controller.abort();
  await assert.rejects(run, ScanStopped);
  await idle(scans);
  assert.equal(getEventListeners(controller.signal, "abort").length, 0, "the scan leaves nothing listening on the signal");
});

test("a scan that finishes as it is asked to stop keeps its result", async () => {
  const controller = new AbortController();
  const run = threads().run({ mode: "finishes-anyway" }, controller.signal);
  await sleep(50);
  controller.abort();
  assert.equal(await run, "finished");
});

test("a finished scan stops listening on its signal", async () => {
  const controller = new AbortController();
  await threads().run({ mode: "ok", value: 1 }, controller.signal);
  assert.equal(getEventListeners(controller.signal, "abort").length, 0);
});

test("a signal already aborted starts no thread", async () => {
  const scans = threads();
  const controller = new AbortController();
  controller.abort();
  const run = scans.run({ mode: "ok", value: 1 }, controller.signal);
  assert.equal(scans.running, 0);
  await assert.rejects(run, ScanStopped);
});

test("a thread that throws fails the scan with its error, and leaves nothing listening on the signal", async () => {
  const controller = new AbortController();
  await assert.rejects(threads().run({ mode: "throws" }, controller.signal), /the thread broke/);
  assert.equal(getEventListeners(controller.signal, "abort").length, 0);
});

test("the thread runs under the heap ceiling it was given", async () => {
  assert.equal(await threads(96).run({ mode: "limits" }), 96);
});

test("a thread that exits without answering fails the scan, saying so", async () => {
  await assert.rejects(threads().run({ mode: "exits" }), (err: unknown) => err instanceof Error && err.message === "The scan thread exited with code 3 before it answered");
});

test("a scan cannot take more heap than its ceiling: only its thread fails", { timeout: 60_000 }, async () => {
  await assert.rejects(threads(64).run({ mode: "heap" }), (err: unknown) => (err as { code?: string }).code === "ERR_WORKER_OUT_OF_MEMORY");
});

test("close asks a running scan to stop, and waits for it", async () => {
  const scans = threads();
  const run = scans.run({ mode: "until-stopped" });
  run.catch(() => {});
  await sleep(50);
  const started = Date.now();
  await scans.close(2_000);
  assert.ok(Date.now() - started < 1_000, "it stopped at once, well inside the grace");
  assert.equal(scans.running, 0);
  await assert.rejects(run, ScanStopped);
});

test("close terminates a scan that does not stop within the grace, and is done within twice it", async () => {
  const scans = threads();
  const run = scans.run({ mode: "deaf" });
  run.catch(() => {});
  await sleep(50);
  const started = Date.now();
  await scans.close(300);
  const took = Date.now() - started;
  assert.ok(took >= 290 && took < 1_500, `close took ${took} ms`);
  await assert.rejects(run, ScanStopped);
  await idle(scans);
});

test("close with nothing running returns at once", async () => {
  const started = Date.now();
  await threads().close(5_000);
  assert.ok(Date.now() - started < 100);
});

// --- the thread's side ---------------------------------------------------------------------------------

test("the thread answers with the scan's result, and lets it read the stop flag", async () => {
  const stop = new Int32Array(new SharedArrayBuffer(4));
  const seen: boolean[] = [];
  const reply = await answerScan(async (shouldStop) => {
    seen.push(shouldStop());
    Atomics.store(stop, 0, 1);
    seen.push(shouldStop());
    return { indexed: 3 };
  }, stop);
  assert.deepEqual(reply, { ok: true, result: { indexed: 3 } });
  assert.deepEqual(seen, [false, true]);
});

test("the thread answers a stopped scan as stopped, and any other failure with its name, code and stack", async () => {
  const stop = new Int32Array(new SharedArrayBuffer(4));
  const stopped = await answerScan(async () => {
    throw new ScanStopped();
  }, stop);
  assert.equal(stopped.ok, false);
  assert.equal(!stopped.ok && stopped.failure.stopped, true);
  const sqlite = Object.assign(new Error("database disk image is malformed"), { name: "SqliteError", code: "SQLITE_CORRUPT" });
  const failed = await answerScan(async () => {
    throw sqlite;
  }, stop);
  assert.deepEqual(failed, { ok: false, failure: { message: "database disk image is malformed", stopped: false, name: "SqliteError", code: "SQLITE_CORRUPT", stack: sqlite.stack } });
  assert.deepEqual(await answerScan(async () => {
    throw "a bare string";
  }, stop), { ok: false, failure: { message: "a bare string", stopped: false } });
  const numeric = Object.assign(new Error("odd"), { code: 5 });
  numeric.stack = undefined as unknown as string;
  const noStack = await answerScan(async () => {
    throw numeric;
  }, stop);
  assert.deepEqual(noStack, { ok: false, failure: { message: "odd", stopped: false, name: "Error" } }, "a code that is not a string, and a missing stack, are left out");
});

test("a failure crosses back as an error with the thread's name, code and stack, and a stop as a stop", () => {
  const err = errorFrom({ message: "disk I/O error", stopped: false, name: "SqliteError", code: "SQLITE_IOERR", stack: "SqliteError: disk I/O error\n    at thread" }) as Error & { code?: string };
  assert.ok(!(err instanceof ScanStopped));
  assert.deepEqual([err.message, err.name, err.code, err.stack], ["disk I/O error", "SqliteError", "SQLITE_IOERR", "SqliteError: disk I/O error\n    at thread"]);
  const bare = errorFrom({ message: "plain", stopped: false }) as Error & { code?: string };
  assert.deepEqual([bare.message, bare.name, bare.code], ["plain", "Error", undefined]);
  assert.equal(Object.prototype.hasOwnProperty.call(bare, "code"), false, "no code is not a code of undefined");
  assert.match(bare.stack ?? "", /plain/);
  assert.ok(errorFrom({ message: "whatever", stopped: true, code: "X" }) instanceof ScanStopped);
});

test("a thread's failure keeps its code and stack across", async () => {
  await assert.rejects(threads().run({ mode: "fail" }), (err: unknown) => {
    const e = err as Error & { code?: string };
    return e.name === "SqliteError" && e.code === "SQLITE_CANTOPEN" && /at the scan thread/.test(e.stack ?? "");
  });
});

test("a thread that dies after it was asked to stop has failed, not stopped", async () => {
  const controller = new AbortController();
  const run = threads().run({ mode: "dies-when-stopped" }, controller.signal);
  run.catch(() => {});
  await sleep(50);
  controller.abort();
  await assert.rejects(run, (err: unknown) => !(err instanceof ScanStopped) && err instanceof Error && /Array buffer allocation failed/.test(err.message));
});

test("a thread that dies as close asks it to stop has failed too, and close still returns", async () => {
  const scans = threads();
  const run = scans.run({ mode: "dies-when-stopped" });
  run.catch(() => {});
  await sleep(50);
  await scans.close(2_000);
  await assert.rejects(run, (err: unknown) => !(err instanceof ScanStopped));
});
