import type { JobQueue } from "./jobs.ts";
import type { QmdBackend } from "./store.ts";

/**
 * Shutdown, inside one budget. Compose gives the container `stop_grace_period`
 * (15 s) before it kills it, so all of this must end well before that:
 *
 * 1. No new worker call starts, an embedding batch included, and no new request
 *    is accepted. Requests and calls already in flight go on.
 * 2. Jobs run for what is left of the budget, so what was accepted is indexed. One
 *    that needs a worker stops at its next call and waits for the next start.
 * 3. At the budget's end, the running job is kept as waiting, worker calls still
 *    in flight are cut off, and requests have a moment to answer without them.
 * 4. The index and the job store close.
 *
 * Steps 3 and 4 take at most `FINAL_MS` each beyond the budget, and closing the
 * index is quick, so the whole is the budget plus a few seconds.
 */
export interface ShutdownParts {
  /** Stop accepting requests. Resolves once those in flight have answered. */
  closeServer(): Promise<void>;
  jobs: Pick<JobQueue, "stopping" | "drain" | "suspend" | "settled" | "pending">;
  backend: Pick<QmdBackend, "stopWorkerCalls" | "abortWorkerCalls" | "close">;
  closeStore(): void;
  log: { warn(o: object, m: string): void };
  now?: () => number;
}

/** How long requests, then the running job, get once worker calls are cut off. */
export const FINAL_MS = 1_000;

export async function shutDown(parts: ShutdownParts, budgetMs: number): Promise<void> {
  const now = parts.now ?? Date.now;
  const deadline = now() + budgetMs;
  const left = (): number => Math.max(0, deadline - now());
  parts.backend.stopWorkerCalls();
  parts.jobs.stopping();
  const server = parts.closeServer();
  // Index what was accepted in the debounce window. A timeout loses nothing: the
  // jobs are persisted and resume at the next start.
  if (!(await parts.jobs.drain(left()))) {
    parts.log.warn({ pendingJobs: parts.jobs.pending, timeoutMs: budgetMs }, "reindex jobs still pending at shutdown");
  }
  // A job still running is about to lose the index under it. That is not its
  // failure, so it stays queued in the store and resumes at the next start.
  parts.jobs.suspend();
  parts.backend.abortWorkerCalls();
  if (!(await within(server, Math.max(left(), FINAL_MS)))) parts.log.warn({}, "requests still open at shutdown");
  if (!(await parts.jobs.settled(FINAL_MS))) parts.log.warn({}, "a reindex job was still stopping at shutdown");
  await parts.backend.close();
  parts.closeStore();
}

/** Whether `promise` settled within `ms`. A rejection counts as settled: shutdown goes on. */
function within(promise: Promise<void>, ms: number): Promise<boolean> {
  return new Promise((resolve) => {
    const timer = setTimeout(() => resolve(false), ms);
    promise.then(
      () => {
        clearTimeout(timer);
        resolve(true);
      },
      () => {
        clearTimeout(timer);
        resolve(true);
      },
    );
  });
}
