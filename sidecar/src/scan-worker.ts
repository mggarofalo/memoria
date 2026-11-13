import { parentPort, workerData } from "node:worker_threads";
import { answerScan, type ThreadData } from "./scan.ts";
import { scanIndex, type ScanRequest } from "./store.ts";

/** The scan thread's entry (src/scan.ts): one scan on a connection of its own, and one reply. */
const { stop, ...request } = workerData as ThreadData & ScanRequest;
parentPort!.postMessage(await answerScan((shouldStop) => scanIndex(request, shouldStop), stop));
