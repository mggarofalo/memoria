/**
 * Runs the worst matches the grammar allows in a worker thread, so a regression
 * that blocks can be terminated by the test instead of hanging CI.
 */
import { parentPort, workerData } from "node:worker_threads";
import { globViolation, matchGlob } from "../src/multi-get.ts";

const { globs, paths } = workerData as { globs: string[]; paths: string[] };
for (const glob of globs) {
  if (globViolation(glob)) throw new Error(`worst case is no longer accepted: ${glob}`);
  for (const path of paths) matchGlob(glob, path);
}
parentPort!.postMessage("done");
