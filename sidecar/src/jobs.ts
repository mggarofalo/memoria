import { randomUUID } from "node:crypto";

/**
 * A single-worker queue for index maintenance. `qmd update` and `qmd embed`
 * contend over the model and the database, so everything that touches the
 * index runs here, one job at a time.
 *
 * Two ways in:
 *
 * - **Immediate** (`POST /reindex`): eligible to run as soon as the worker is
 *   free. An identical job still waiting is returned instead of duplicated,
 *   and starts now if it was being held by the debounce.
 * - **Debounced** (`POST /documents`, `DELETE /documents`): held until writes
 *   for that scope go quiet, so a burst of N documents costs one scan. A job
 *   that is already running never absorbs a request, because its scan may have
 *   passed the new file; the request becomes one follow-up job instead.
 *
 * Held jobs report `queued`: the debounce is a scheduling detail, not a state
 * callers need to handle.
 */
export type JobState = "queued" | "running" | "succeeded" | "failed";

export interface ReindexRequest {
  /** Collections to rescan; null means every collection. */
  collections: string[] | null;
  /** Generate embeddings after the scan. */
  embed: boolean;
  /** Re-embed everything, not just documents whose content changed. */
  force: boolean;
  /** Free-text origin for the audit trail (e.g. "POST /documents claude-sessions/x.md"). */
  reason: string;
}

export interface UpdateSummary {
  collections: number;
  indexed: number;
  updated: number;
  unchanged: number;
  removed: number;
  skipped: number;
  needsEmbedding: number;
}

export interface EmbedSummary {
  docsProcessed: number;
  chunksEmbedded: number;
  errors: number;
  durationMs: number;
}

export interface JobResult {
  update: UpdateSummary;
  embed: EmbedSummary | null;
  metadata: { documents: number; withMetadata: number };
}

export interface Job {
  id: string;
  state: JobState;
  request: ReindexRequest;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  result: JobResult | null;
  error: string | null;
}

export type JobRunner = (request: ReindexRequest) => Promise<JobResult>;

/** The clock and timers the debounce runs on. Tests inject a manual one. */
export interface JobScheduler {
  now(): number;
  setTimeout(callback: () => void, delayMs: number): unknown;
  clearTimeout(handle: unknown): void;
}

/** Real timers, unref'd so a held job never keeps the process alive by itself. */
export const realScheduler: JobScheduler = {
  now: () => Date.now(),
  setTimeout: (callback, delayMs) => {
    const timer = setTimeout(callback, delayMs);
    timer.unref();
    return timer;
  },
  clearTimeout: (handle) => clearTimeout(handle as NodeJS.Timeout),
};

export interface JobQueueOptions {
  /** Finished jobs to keep for `GET /reindex/jobs`. */
  keep?: number;
  /** Quiet period before a debounced job runs. 0 makes it eligible at once. */
  debounceMs?: number;
  /** Longest a job can be held from its first request, so steady ingest cannot starve the index. */
  maxWaitMs?: number;
  scheduler?: JobScheduler;
}

export interface EnqueueOptions {
  debounce?: boolean;
}

interface Hold {
  firstRequestAt: number;
  timer: unknown;
}

export class JobQueue {
  readonly #runner: JobRunner;
  readonly #keep: number;
  readonly #debounceMs: number;
  readonly #maxWaitMs: number;
  readonly #scheduler: JobScheduler;
  readonly #jobs: Job[] = [];
  /** Waiting jobs the worker may start, in release order. */
  readonly #ready: Job[] = [];
  /** Waiting jobs still inside their debounce window. */
  readonly #held = new Map<string, Hold>();
  /** Waiting jobs that absorbed later requests: their original reason and how many. */
  readonly #merged = new Map<string, { reason: string; extra: number }>();
  #active: Job | null = null;
  #pending = 0;
  #idleWaiters: Array<() => void> = [];

  constructor(runner: JobRunner, options: JobQueueOptions = {}) {
    this.#runner = runner;
    this.#keep = options.keep ?? 100;
    this.#debounceMs = options.debounceMs ?? 0;
    this.#maxWaitMs = options.maxWaitMs ?? 5 * 60_000;
    this.#scheduler = options.scheduler ?? realScheduler;
  }

  enqueue(request: ReindexRequest, options: EnqueueOptions = {}): Job {
    return options.debounce ? this.#enqueueDebounced(request) : this.#enqueueImmediate(request);
  }

  #enqueueImmediate(request: ReindexRequest): Job {
    const duplicate = this.#jobs.find((j) => j.state === "queued" && sameTarget(j.request, request));
    if (duplicate) {
      this.#release(duplicate);
      return duplicate;
    }
    const job = this.#create(request);
    this.#ready.push(job);
    this.#schedulePump();
    return job;
  }

  #enqueueDebounced(request: ReindexRequest): Job {
    const waiting = this.#jobs.find((j) => j.state === "queued" && sameScope(j.request, request));
    if (waiting) {
      this.#merge(waiting, request);
      const hold = this.#held.get(waiting.id);
      if (hold) this.#arm(waiting, hold);
      return waiting;
    }
    const job = this.#create(request);
    if (this.#debounceMs <= 0) {
      this.#ready.push(job);
      this.#schedulePump();
    } else {
      const hold: Hold = { firstRequestAt: this.#scheduler.now(), timer: undefined };
      this.#held.set(job.id, hold);
      this.#arm(job, hold);
    }
    return job;
  }

  #create(request: ReindexRequest): Job {
    const job: Job = {
      id: randomUUID(),
      state: "queued",
      request: { ...request },
      createdAt: new Date().toISOString(),
      startedAt: null,
      finishedAt: null,
      result: null,
      error: null,
    };
    this.#jobs.push(job);
    this.#pending++;
    this.#trim();
    return job;
  }

  /**
   * Fold a later request into a waiting job. `embed` is an OR rather than a
   * reason to split: an embed run sweeps every unembedded document in the
   * collection, so two jobs would embed the same documents anyway.
   */
  #merge(job: Job, request: ReindexRequest): void {
    job.request.embed = job.request.embed || request.embed;
    const merged = this.#merged.get(job.id) ?? { reason: job.request.reason, extra: 0 };
    merged.extra++;
    this.#merged.set(job.id, merged);
    job.request.reason = `${merged.reason} (+${merged.extra} more)`;
  }

  /** (Re)start a held job's quiet period, never past its max-wait deadline. */
  #arm(job: Job, hold: Hold): void {
    if (hold.timer !== undefined) this.#scheduler.clearTimeout(hold.timer);
    const now = this.#scheduler.now();
    const due = Math.min(now + this.#debounceMs, hold.firstRequestAt + this.#maxWaitMs);
    hold.timer = this.#scheduler.setTimeout(() => this.#release(job), Math.max(0, due - now));
  }

  /** Move a held job to the ready list. A no-op for a job that is not held. */
  #release(job: Job): void {
    const hold = this.#held.get(job.id);
    if (!hold) return;
    this.#scheduler.clearTimeout(hold.timer);
    this.#held.delete(job.id);
    this.#ready.push(job);
    this.#schedulePump();
  }

  #schedulePump(): void {
    // Deferred so the caller sees the job as queued and can serialize it first.
    queueMicrotask(() => this.#pump());
  }

  #pump(): void {
    if (this.#active) return;
    const job = this.#ready.shift();
    if (!job) return;
    this.#active = job;
    void this.#run(job);
  }

  async #run(job: Job): Promise<void> {
    this.#merged.delete(job.id);
    job.state = "running";
    job.startedAt = new Date().toISOString();
    try {
      job.result = await this.#runner(job.request);
      job.state = "succeeded";
    } catch (err) {
      job.state = "failed";
      job.error = err instanceof Error ? err.message : String(err);
    } finally {
      job.finishedAt = new Date().toISOString();
      this.#pending--;
      this.#active = null;
      if (this.#pending === 0) {
        const waiters = this.#idleWaiters;
        this.#idleWaiters = [];
        for (const resolve of waiters) resolve();
      }
      this.#pump();
    }
  }

  #trim(): void {
    // Drop the oldest finished jobs once the history exceeds the cap.
    while (this.#jobs.length > this.#keep) {
      const idx = this.#jobs.findIndex((j) => j.state === "succeeded" || j.state === "failed");
      if (idx < 0) break;
      this.#jobs.splice(idx, 1);
    }
  }

  get(id: string): Job | undefined {
    return this.#jobs.find((j) => j.id === id);
  }

  list(): Job[] {
    return [...this.#jobs].reverse();
  }

  get pending(): number {
    return this.#pending;
  }

  /** Stop waiting: every held job becomes eligible to run now. */
  flush(): void {
    for (const job of this.#jobs) {
      if (this.#held.has(job.id)) this.#release(job);
    }
  }

  /**
   * Resolves once nothing is queued or running, including held jobs, so it
   * waits out the debounce. Use `flush()` first to skip the wait.
   */
  idle(): Promise<void> {
    if (this.#pending === 0) return Promise.resolve();
    return new Promise((resolve) => this.#idleWaiters.push(resolve));
  }

  /**
   * Flush and wait for the queue to empty, for at most `timeoutMs`. Returns
   * false on timeout. Shutdown uses this so a document accepted moments ago
   * is indexed rather than left waiting on disk for the next scan.
   */
  drain(timeoutMs: number): Promise<boolean> {
    this.flush();
    if (this.#pending === 0) return Promise.resolve(true);
    return new Promise((resolve) => {
      const timer = this.#scheduler.setTimeout(() => resolve(false), timeoutMs);
      void this.idle().then(() => {
        this.#scheduler.clearTimeout(timer);
        resolve(true);
      });
    });
  }
}

function collectionsKey(request: ReindexRequest): string {
  return request.collections ? [...request.collections].sort().join(",") : "*";
}

function sameScope(a: ReindexRequest, b: ReindexRequest): boolean {
  return collectionsKey(a) === collectionsKey(b) && a.force === b.force;
}

function sameTarget(a: ReindexRequest, b: ReindexRequest): boolean {
  return sameScope(a, b) && a.embed === b.embed;
}
