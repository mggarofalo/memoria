import { Worker } from "node:worker_threads";
import { ScanStopped } from "./scan-batches.ts";

/**
 * Index scans run on a thread of their own. QMD's scan reads, hashes and writes every
 * document synchronously, so on the main thread a scan of 52,000 documents held the event
 * loop for seconds at a stretch, and `/health` timed out. On its own thread it holds only
 * that thread, with its own SQLite connection (a connection cannot cross threads), and the
 * main thread goes on answering. src/scan-batches.ts keeps what readers see consistent.
 *
 * One thread per scan, gone when it ends, so nothing idles between scans. Stopping is
 * cooperative: a flag in shared memory, which the thread reads after each document, as its
 * own event loop is as busy as the main one was. Only `close` terminates one outright.
 */

/** What the thread is given. `stop` is set to 1 to ask it to stop. */
export interface ThreadData {
  stop: Int32Array;
}

/** What the thread posts back, once. */
export type ThreadReply<T> = { ok: true; result: T } | { ok: false; failure: ThreadFailure };

/** How a scan ended on its thread, as much of the error as crosses: SQLite's code and the thread's stack included. */
export interface ThreadFailure {
  message: string;
  stopped: boolean;
  name?: string;
  code?: string;
  stack?: string;
}

/** The thread's failure as an error here, carrying its code and its own stack. */
export function errorFrom(failure: ThreadFailure): Error {
  if (failure.stopped) return new ScanStopped();
  const err = new Error(failure.message) as Error & { code?: string };
  if (failure.name !== undefined) err.name = failure.name;
  if (failure.code !== undefined) err.code = failure.code;
  if (failure.stack !== undefined) err.stack = failure.stack;
  return err;
}

/** An error thrown on the thread, as it can cross to this one. */
export function failureOf(err: unknown): ThreadFailure {
  if (!(err instanceof Error)) return { message: String(err), stopped: false };
  const { code } = err as { code?: unknown };
  return { message: err.message, stopped: err instanceof ScanStopped, name: err.name, ...(typeof code === "string" ? { code } : {}), ...(err.stack !== undefined ? { stack: err.stack } : {}) };
}

export interface ScanThreadOptions {
  /** The thread's entry module. */
  entry: URL;
  /** Ceiling on the thread's heap, so a scan cannot take the process's own. */
  maxHeapMb: number;
}

export class ScanThreads {
  readonly #options: ScanThreadOptions;
  readonly #running = new Map<Worker, { stop: Int32Array; exited: Promise<void> }>();
  /** Threads `close` has asked to stop: cut off, they stopped rather than failed. */
  readonly #closed = new WeakSet<Worker>();

  constructor(options: ScanThreadOptions) {
    this.#options = options;
  }

  /** Threads running now. */
  get running(): number {
    return this.#running.size;
  }

  /**
   * Run one scan on a new thread. Resolves with its result. An abort asks the thread to stop
   * after the document it is on, and it then rejects with `ScanStopped`. So does a thread
   * stopped by `close`.
   */
  run<T>(data: object, signal?: AbortSignal): Promise<T> {
    if (signal?.aborted) return Promise.reject(new ScanStopped());
    const stop = new Int32Array(new SharedArrayBuffer(4));
    const worker = new Worker(this.#options.entry, {
      workerData: { ...data, stop } satisfies ThreadData,
      resourceLimits: { maxOldGenerationSizeMb: this.#options.maxHeapMb },
    });
    const exited = new Promise<void>((resolve) => worker.once("exit", () => resolve()));
    this.#running.set(worker, { stop, exited });
    const askToStop = (): void => {
      Atomics.store(stop, 0, 1);
    };
    signal?.addEventListener("abort", askToStop);
    return new Promise<T>((resolve, reject) => {
      let failure: Error | undefined;
      // The reply settles it. An exit with none settles it otherwise; after a reply, settling
      // again changes nothing. Only `close` cutting a thread off makes that a stop: a thread
      // that ran out of heap or crashed failed, asked to stop or not, and says why.
      worker.once("message", (reply: ThreadReply<T>) => {
        signal?.removeEventListener("abort", askToStop);
        if (reply.ok) resolve(reply.result);
        else reject(errorFrom(reply.failure));
      });
      worker.once("error", (err: Error) => {
        failure = err;
      });
      worker.once("exit", (code) => {
        this.#running.delete(worker);
        signal?.removeEventListener("abort", askToStop);
        if (failure) reject(failure);
        else if (this.#closed.has(worker)) reject(new ScanStopped());
        else reject(new Error(`The scan thread exited with code ${code} before it answered`));
      });
    });
  }

  /**
   * Ask every running scan to stop, give them `graceMs` to finish the document they are on
   * and commit, then terminate whatever is left. A thread inside one long SQLite statement
   * cannot be interrupted, so this waits at most `graceMs` twice and then lets it go: the
   * process is about to exit, and SQLite drops the uncommitted group.
   */
  async close(graceMs: number): Promise<void> {
    const threads = [...this.#running];
    for (const [worker, { stop }] of threads) {
      Atomics.store(stop, 0, 1);
      this.#closed.add(worker);
    }
    await Promise.all(
      threads.map(async ([worker, { exited }]) => {
        await within(exited, graceMs);
        // Ends one still running. One already gone has nothing to end, and this returns at once.
        await within(worker.terminate(), graceMs);
      }),
    );
  }
}

/**
 * The thread's side: run `scan`, letting it read the stop flag, and turn how it ended into the
 * one reply the thread posts. src/scan-worker.ts calls this with the real scan.
 */
export async function answerScan<T>(scan: (shouldStop: () => boolean) => Promise<T>, stop: Int32Array): Promise<ThreadReply<T>> {
  try {
    return { ok: true, result: await scan(() => Atomics.load(stop, 0) === 1) };
  } catch (err) {
    return { ok: false, failure: failureOf(err) };
  }
}

/** Waits for `promise`, or `ms`, whichever comes first. The timer does not keep the process alive. */
function within(promise: Promise<unknown>, ms: number): Promise<unknown> {
  return Promise.race([promise, new Promise<void>((resolve) => setTimeout(resolve, ms).unref())]);
}
