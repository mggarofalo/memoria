import assert from "node:assert/strict";
import { getEventListeners } from "node:events";
import { test } from "node:test";
import { answerScan, ScanThreads } from "../src/scan.ts";
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

test("the thread answers a stopped scan as stopped, and any other failure with its message", async () => {
  const stop = new Int32Array(new SharedArrayBuffer(4));
  assert.deepEqual(await answerScan(async () => {
    throw new ScanStopped();
  }, stop), { ok: false, message: "The scan was stopped", stopped: true });
  assert.deepEqual(await answerScan(async () => {
    throw new Error("database disk image is malformed");
  }, stop), { ok: false, message: "database disk image is malformed", stopped: false });
  assert.deepEqual(await answerScan(async () => {
    throw "a bare string";
  }, stop), { ok: false, message: "a bare string", stopped: false });
});
