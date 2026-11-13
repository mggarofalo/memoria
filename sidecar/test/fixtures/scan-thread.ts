import { parentPort, resourceLimits, workerData } from "node:worker_threads";

/** A stand-in scan thread for test/scan.test.ts. `mode` picks how it behaves. */
const data = workerData as { stop: Int32Array; mode: string; value?: unknown };
const stopped = (): boolean => Atomics.load(data.stop, 0) === 1;
const nap = (ms: number): void => {
  Atomics.wait(data.stop, 0, 0, ms);
};

switch (data.mode) {
  case "ok":
    parentPort!.postMessage({ ok: true, result: data.value });
    break;
  case "limits":
    parentPort!.postMessage({ ok: true, result: resourceLimits.maxOldGenerationSizeMb });
    break;
  case "fail":
    parentPort!.postMessage({ ok: false, message: "the index is not there", stopped: false });
    break;
  case "until-stopped":
    // As a scan does: work, and look at the flag after each document.
    while (!stopped()) nap(5);
    parentPort!.postMessage({ ok: false, message: "The scan was stopped", stopped: true });
    break;
  case "finishes-anyway":
    // The flag came after its last document: it answers with its result.
    while (!stopped()) nap(5);
    parentPort!.postMessage({ ok: true, result: "finished" });
    break;
  case "deaf":
    // Inside one long statement: it never looks at the flag.
    for (;;) Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 50);
  case "throws":
    throw new Error("the thread broke");
  case "exits":
    process.exit(3);
    break;
  case "heap": {
    const hoard: number[][] = [];
    for (;;) hoard.push(new Array<number>(1_000_000).fill(hoard.length));
  }
}
