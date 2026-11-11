import { randomUUID } from "node:crypto";
import type { QueueSnapshot } from "./queue-store.ts";

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
 *
 * With a `JobStore`, every change is written as it happens, so a job accepted
 * before a crash runs after the restart. A job that was running then runs again,
 * which is safe: scans and embedding both resume from the index. A failing store
 * never stops the queue: it carries on in memory, says so through `persistence()`,
 * and writes what it missed when the store recovers.
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
  /** Documents this run could not finish, including any it set aside. */
  docsPending?: number;
  /** Of those, how many a worker refused and were set aside. Later jobs skip them until a retry is due. */
  docsDeadLettered?: number;
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
  /** Reindex jobs are background work; interactive calls never become jobs. */
  priority: "background";
  /** How many times this job has started. */
  attempts: number;
  /** Workers that embedded for the latest attempt, comma-separated. */
  worker: string | null;
}

export interface JobRunContext {
  /** Aborted when the job is cancelled. Long-running work should stop at the next safe point. */
  signal: AbortSignal;
  noteWorker(name: string): void;
  /** Durable work was done. A job making progress is not stuck in a crash loop. */
  noteProgress(): void;
  /** When this job was first accepted. A resumed force job skips what it already redid. */
  jobCreatedAt: string;
  /** Nothing has run for this job before, so there is nothing to resume. */
  firstAttempt: boolean;
}

export type JobRunner = (request: ReindexRequest, context: JobRunContext) => Promise<JobResult>;

/** Thrown by a runner that cannot make progress yet. The job waits and tries again; it has not failed. */
export class JobDeferred extends Error {
  constructor(reason: string) {
    super(reason);
    this.name = "JobDeferred";
  }
}

export class JobCancelled extends Error {
  constructor() {
    super("Cancelled");
    this.name = "JobCancelled";
  }
}

/**
 * The run finished, but work remains that retrying soon would not fix. The job
 * reports `failed` with its result attached, so a healthy-looking `succeeded`
 * can never hide documents left pending. `:retry` or the next job resumes them.
 */
export class JobPartial extends Error {
  readonly result: JobResult;
  constructor(message: string, result: JobResult) {
    super(message);
    this.name = "JobPartial";
    this.result = result;
  }
}

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

/** A job and the scheduling state that has to survive a restart with it. */
export interface PersistedJob {
  job: Job;
  /** Submission order. */
  seq: number;
  /** When a held job becomes eligible. Null when it is not held. */
  dueAt: number | null;
  firstRequestAt: number | null;
  /** Held because it could not make progress, rather than by the debounce. */
  deferred: boolean;
  deferrals: number;
  /** Restarts that found this job running with no progress since the last. */
  crashes: number;
  /** The first reason, and how many requests were folded in after it. */
  baseReason: string;
  mergedRequests: number;
}

export interface JobStore {
  load(): PersistedJob[];
  save(entry: PersistedJob): void;
  remove(id: string): void;
  /** A consistent copy of the store's file, for a backup. Only a store with a file has one. */
  snapshot?(): QueueSnapshot;
}

export interface JobQueueOptions {
  /** Finished jobs to keep for `GET /reindex/jobs`. */
  keep?: number;
  /** Quiet period before a debounced job runs. 0 makes it eligible at once. */
  debounceMs?: number;
  /** Longest a job can be held from its first request, so steady ingest cannot starve the index. */
  maxWaitMs?: number;
  scheduler?: JobScheduler;
  /** Where jobs persist. Without one they live in memory, for tests and tools. */
  store?: JobStore;
  /** Crashes in a row, with no progress between, before a job is given up on. */
  maxCrashes?: number;
  /** First wait after a deferral; it doubles each time. */
  deferBaseMs?: number;
  deferMaxMs?: number;
  /** Told about a store failure. The queue carries on regardless. */
  onStoreError?: (message: string) => void;
  /** Told each time a job goes back to waiting, so that a job never waits in silence. */
  onDeferred?: (job: Job, reason: string) => void;
}

export interface EnqueueOptions {
  debounce?: boolean;
}

export interface QueueCounts {
  queued: number;
  /** The queued jobs waiting on the debounce or a retry delay. */
  held: number;
  running: number;
  succeeded: number;
  failed: number;
}

export interface QueuePersistence {
  /** False means jobs are in memory only and will not survive a restart. */
  enabled: boolean;
  healthy: boolean;
}

/** How one run of a job ended for the queue: done with, waiting on a retry, or stopped by shutdown. */
type RunEnding = "finished" | "deferred" | "parked";

export class JobQueue {
  readonly #runner: JobRunner;
  readonly #keep: number;
  readonly #debounceMs: number;
  readonly #maxWaitMs: number;
  readonly #scheduler: JobScheduler;
  #store: JobStore | undefined;
  readonly #maxCrashes: number;
  readonly #deferBaseMs: number;
  readonly #deferMaxMs: number;
  readonly #onStoreError: ((message: string) => void) | undefined;
  readonly #onDeferred: ((job: Job, reason: string) => void) | undefined;
  readonly #entries = new Map<string, PersistedJob>();
  readonly #jobs: Job[] = [];
  /** Waiting jobs the worker may start, in release order. */
  readonly #ready: Job[] = [];
  /** Timers of waiting jobs that are held. */
  readonly #held = new Map<string, unknown>();
  /** Jobs whose latest state has not reached the store. */
  readonly #dirty = new Set<string>();
  /** Finished jobs trimmed from memory but not yet from the store. */
  readonly #unremoved = new Set<string>();
  #active: Job | null = null;
  #abort: AbortController | null = null;
  #pending = 0;
  #seq = 0;
  #idleWaiters: Array<() => void> = [];
  #storeHealthy = true;
  #suspended = false;
  /** Shutdown has begun: a job that does not succeed now was stopped by it, and waits for the next start. */
  #stopping = false;
  /** Jobs stopped by shutdown and kept for the next start. No longer pending here. */
  readonly #parked = new Set<string>();
  /** The running job was cancelled by an operator, which shutdown must not turn into a park. */
  #cancelRequested = false;
  /** No job starts while set. In memory only, so a restart always resumes. */
  #paused = false;
  // Replaced at most once: see `adoptStore`.

  constructor(runner: JobRunner, options: JobQueueOptions = {}) {
    this.#runner = runner;
    this.#keep = options.keep ?? 100;
    this.#debounceMs = options.debounceMs ?? 0;
    this.#maxWaitMs = options.maxWaitMs ?? 5 * 60_000;
    this.#scheduler = options.scheduler ?? realScheduler;
    this.#store = options.store;
    this.#maxCrashes = options.maxCrashes ?? 5;
    this.#deferBaseMs = options.deferBaseMs ?? 30_000;
    this.#deferMaxMs = options.deferMaxMs ?? 5 * 60_000;
    this.#onStoreError = options.onStoreError;
    this.#onDeferred = options.onDeferred;
    this.#restore();
  }

  // --- persistence -----------------------------------------------------------

  #storeFailed(err: unknown): void {
    this.#storeHealthy = false;
    this.#onStoreError?.(err instanceof Error ? err.message : "job store failure");
  }

  /** Every write goes through here, so no store failure can reach a timer or a request. */
  #persist(job: Job): void {
    if (!this.#store || this.#suspended) return;
    this.#dirty.add(job.id);
    this.#flush(this.#store);
  }

  #flush(store: JobStore): void {
    try {
      for (const id of [...this.#unremoved]) {
        store.remove(id);
        this.#unremoved.delete(id);
      }
      for (const id of [...this.#dirty]) {
        const entry = this.#entries.get(id);
        if (entry) store.save(entry);
        this.#dirty.delete(id);
      }
      this.#storeHealthy = true;
    } catch (err) {
      this.#storeFailed(err);
    }
  }

  /** A job waiting to try again, and why. Null when none is. */
  stuck(): { reason: string; attempts: number } | null {
    const job = this.#jobs.find((j) => this.#entries.get(j.id)?.deferred && j.error !== null);
    return job ? { reason: job.error!, attempts: job.attempts } : null;
  }

  persistence(): QueuePersistence {
    return { enabled: this.#store !== undefined, healthy: this.#store !== undefined && this.#storeHealthy };
  }

  /**
   * A copy of the store's file, for a backup. Null when no file holds these jobs: they
   * are in memory only, the file is behind them after a failed write, which also leaves
   * the store unhealthy, or shutdown has begun. A copy then would read as a good backup
   * of jobs it does not have.
   */
  snapshot(): QueueSnapshot | null {
    if (!this.#store?.snapshot || this.#suspended || !this.#storeHealthy) return null;
    return this.#store.snapshot();
  }

  /**
   * A store became available after starting without one, because another sidecar held
   * the file and has now gone. Its jobs join these, and these reach its file.
   */
  adoptStore(store: JobStore): void {
    if (this.#store || this.#suspended) return;
    this.#store = store;
    const here = [...this.#entries.values()];
    this.#restore();
    // Filed after the file's own, which are older, and given numbers it has not used.
    for (const entry of here) {
      entry.seq = ++this.#seq;
      this.#dirty.add(entry.job.id);
    }
    const seqOf = (job: Job): number => this.#entries.get(job.id)?.seq ?? 0;
    this.#jobs.sort((a, b) => seqOf(a) - seqOf(b));
    this.#flush(store);
    this.#trim();
    this.#schedulePump();
  }

  // --- restart ---------------------------------------------------------------

  #restore(): void {
    if (!this.#store) return;
    let saved: PersistedJob[] = [];
    try {
      saved = this.#store.load();
    } catch (err) {
      this.#storeFailed(err);
    }
    for (const entry of saved.sort((a, b) => a.seq - b.seq)) {
      this.#seq = Math.max(this.#seq, entry.seq);
      this.#entries.set(entry.job.id, entry);
      this.#jobs.push(entry.job);
      if (entry.job.state === "running") this.#restoreInterrupted(entry);
      if (entry.job.state === "queued") this.#restoreWaiting(entry);
    }
    this.#schedulePump();
  }

  /** It was running when the process died. Only this counts toward giving up. */
  #restoreInterrupted(entry: PersistedJob): void {
    const { job } = entry;
    entry.crashes++;
    if (entry.crashes >= this.#maxCrashes) {
      job.state = "failed";
      job.error = `Gave up after ${entry.crashes} restarts found it running with no progress`;
      job.finishedAt = new Date().toISOString();
    } else {
      job.state = "queued";
      job.startedAt = null;
      entry.dueAt = null;
      entry.deferred = false;
    }
    this.#persist(job);
  }

  #restoreWaiting(entry: PersistedJob): void {
    const { job } = entry;
    this.#pending++;
    if (entry.dueAt === null) {
      this.#ready.push(job);
      return;
    }
    // A clock set back must not stretch the wait past what the job was promised.
    const longest = entry.deferred ? this.#deferMaxMs : this.#maxWaitMs;
    const delay = Math.min(Math.max(0, entry.dueAt - this.#scheduler.now()), longest);
    this.#held.set(job.id, this.#scheduler.setTimeout(() => this.#release(job), delay));
  }

  /**
   * Shutdown has begun, and worker calls are refused. Jobs still run, so what was
   * accepted is indexed, but one that does not succeed from here was stopped by
   * shutdown, not failed: it is kept as waiting, with no retry delay, and runs again
   * at the next start. It no longer counts as pending, so `drain` need not wait it out.
   */
  stopping(): void {
    this.#stopping = true;
  }

  /**
   * Stop for shutdown. Whatever is running is signalled to stop, and the store keeps
   * it as waiting, so it resumes at the next start without counting as a crash. Its
   * failure as it stops, or as the index closes under it, is not recorded, because
   * it did not fail.
   */
  suspend(): void {
    if (this.#active) {
      const entry = this.#entries.get(this.#active.id);
      if (entry && this.#store) this.#saveAsWaiting(this.#store, entry);
    }
    this.#suspended = true;
    this.#stopping = true;
    this.#abort?.abort();
    for (const timer of this.#held.values()) this.#scheduler.clearTimeout(timer);
    this.#held.clear();
  }

  /** Resolves true once no job is running, or false after `timeoutMs`. */
  settled(timeoutMs: number): Promise<boolean> {
    if (this.#active === null) return Promise.resolve(true);
    return new Promise((resolve) => {
      const timer = this.#scheduler.setTimeout(() => resolve(false), timeoutMs);
      void this.#until(() => this.#active === null).then(() => {
        this.#scheduler.clearTimeout(timer);
        resolve(true);
      });
    });
  }

  #saveAsWaiting(store: JobStore, entry: PersistedJob): void {
    try {
      store.save({ ...entry, job: { ...entry.job, state: "queued", startedAt: null } });
    } catch (err) {
      this.#storeFailed(err);
    }
  }

  // --- submitting ------------------------------------------------------------

  enqueue(request: ReindexRequest, options: EnqueueOptions = {}): Job {
    // Once shutdown has begun, a write's job is not held for the window: the drain is its only chance to run before exit.
    return options.debounce && !this.#stopping ? this.#enqueueDebounced(request) : this.#enqueueImmediate(request);
  }

  #enqueueImmediate(request: ReindexRequest): Job {
    const duplicate = this.#waitingMatch((j) => sameTarget(j.request, request));
    if (duplicate) {
      // A force job skips what it redid since it was accepted. Someone asking again now
      // means everything since now, which may well be what a bad worker wrote.
      if (request.force) duplicate.createdAt = new Date().toISOString();
      this.#persist(duplicate);
      this.#release(duplicate);
      return duplicate;
    }
    const job = this.#create(request);
    this.#persist(job);
    this.#ready.push(job);
    this.#schedulePump();
    return job;
  }

  #waitingMatch(matches: (job: Job) => boolean): Job | undefined {
    return this.#jobs.find((j) => j.state === "queued" && matches(j));
  }

  #enqueueDebounced(request: ReindexRequest): Job {
    const waiting = this.#waitingMatch((j) => sameScope(j.request, request));
    if (waiting) {
      this.#merge(waiting, request);
      if (this.#held.has(waiting.id)) this.#rearm(waiting);
      this.#persist(waiting);
      return waiting;
    }
    const job = this.#create(request);
    if (this.#debounceMs <= 0) {
      this.#persist(job);
      this.#ready.push(job);
      this.#schedulePump();
    } else {
      this.#entries.get(job.id)!.firstRequestAt = this.#scheduler.now();
      this.#arm(job);
      this.#persist(job);
    }
    return job;
  }

  /**
   * A job waiting out a retry can be hours from its next try, but a new write
   * still needs its scan: a deleted document stays searchable until then. So the
   * retry may come sooner, on the debounce, and never later.
   */
  #rearm(job: Job): void {
    const entry = this.#entries.get(job.id)!;
    if (!entry.deferred) {
      this.#arm(job);
      return;
    }
    const soonest = this.#scheduler.now() + Math.max(0, this.#debounceMs);
    if (entry.dueAt !== null && soonest < entry.dueAt) this.#schedule(job, entry, soonest);
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
      priority: "background",
      attempts: 0,
      worker: null,
    };
    this.#entries.set(job.id, { job, seq: ++this.#seq, dueAt: null, firstRequestAt: null, deferred: false, deferrals: 0, crashes: 0, baseReason: request.reason, mergedRequests: 0 });
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
    const entry = this.#entries.get(job.id)!;
    entry.mergedRequests++;
    job.request.reason = `${entry.baseReason} (+${entry.mergedRequests} more)`;
  }

  /** (Re)start a held job's quiet period, never past its max-wait deadline. */
  #arm(job: Job): void {
    const entry = this.#entries.get(job.id)!;
    const now = this.#scheduler.now();
    // The clock moved back: restart the allowance rather than let it stretch.
    const first = Math.min(entry.firstRequestAt ?? now, now);
    entry.firstRequestAt = first;
    this.#schedule(job, entry, Math.min(now + this.#debounceMs, first + this.#maxWaitMs));
  }

  #schedule(job: Job, entry: PersistedJob, due: number): void {
    const timer = this.#held.get(job.id);
    if (timer !== undefined) this.#scheduler.clearTimeout(timer);
    entry.dueAt = due;
    this.#held.set(job.id, this.#scheduler.setTimeout(() => this.#release(job), Math.max(0, due - this.#scheduler.now())));
  }

  /** Move a held job to the ready list. A no-op for a job that is not held. */
  #release(job: Job): void {
    const timer = this.#held.get(job.id);
    if (timer === undefined) return;
    this.#scheduler.clearTimeout(timer);
    this.#held.delete(job.id);
    const entry = this.#entries.get(job.id)!;
    entry.dueAt = null;
    entry.deferred = false;
    this.#persist(job);
    this.#ready.push(job);
    this.#schedulePump();
  }

  // --- running ---------------------------------------------------------------

  #schedulePump(): void {
    // Deferred so the caller sees the job as queued and can serialize it first.
    queueMicrotask(() => this.#pump());
  }

  #pump(): void {
    if (this.#active || this.#suspended || this.#paused) return;
    const job = this.#ready.shift();
    if (!job) return;
    this.#active = job;
    void this.#run(job);
  }

  async #run(job: Job): Promise<void> {
    const entry = this.#entries.get(job.id)!;
    job.state = "running";
    job.startedAt = new Date().toISOString();
    job.attempts++;
    job.error = null;
    job.worker = null;
    this.#persist(job);
    const abort = new AbortController();
    this.#abort = abort;
    let ending: RunEnding = "finished";
    try {
      job.result = await this.#runner(job.request, this.#context(job, entry, abort.signal));
      job.state = "succeeded";
      entry.deferrals = 0;
      entry.crashes = 0;
    } catch (err) {
      ending = this.#ended(job, entry, err, abort.signal);
    } finally {
      this.#finishRun(job, ending);
    }
  }

  #ended(job: Job, entry: PersistedJob, err: unknown, signal: AbortSignal): RunEnding {
    if (this.#stopping && !this.#cancelRequested) {
      this.#park(job, entry);
      return "parked";
    }
    if (err instanceof JobDeferred && !signal.aborted) {
      this.#defer(job, err.message);
      return "deferred";
    }
    this.#fail(job, err);
    return "finished";
  }

  /** Stopped by shutdown: waiting again, due at once, for the next start. */
  #park(job: Job, entry: PersistedJob): void {
    job.state = "queued";
    job.startedAt = null;
    job.error = null;
    entry.dueAt = null;
    entry.deferred = false;
    this.#parked.add(job.id);
  }

  #context(job: Job, entry: PersistedJob, signal: AbortSignal): JobRunContext {
    const workers = new Set<string>();
    return {
      signal,
      jobCreatedAt: job.createdAt,
      firstAttempt: job.attempts === 1,
      noteWorker: (name) => {
        workers.add(name);
        job.worker = [...workers].join(",");
      },
      noteProgress: () => {
        if (entry.crashes === 0) return;
        entry.crashes = 0;
        this.#persist(job);
      },
    };
  }

  #fail(job: Job, err: unknown): void {
    job.state = "failed";
    if (err instanceof JobPartial) job.result = err.result;
    job.error = errorText(err);
  }

  #finishRun(job: Job, ending: RunEnding): void {
    this.#abort = null;
    this.#cancelRequested = false;
    this.#active = null;
    if (ending === "finished") job.finishedAt = new Date().toISOString();
    if (ending !== "deferred") this.#pending--;
    this.#persist(job);
    this.#settle();
    this.#pump();
  }

  #defer(job: Job, reason: string): void {
    const entry = this.#entries.get(job.id)!;
    const delay = Math.min(this.#deferBaseMs * 2 ** entry.deferrals, this.#deferMaxMs);
    entry.deferrals++;
    entry.deferred = true;
    job.state = "queued";
    job.startedAt = null;
    job.error = reason;
    this.#onDeferred?.(job, reason);
    this.#schedule(job, entry, this.#scheduler.now() + delay);
  }

  #settle(): void {
    const waiters = this.#idleWaiters;
    this.#idleWaiters = [];
    for (const check of waiters) check();
  }

  #trim(): void {
    // Drop the oldest finished jobs once the history exceeds the cap.
    while (this.#jobs.length > this.#keep) {
      const idx = this.#jobs.findIndex((j) => j.state === "succeeded" || j.state === "failed");
      if (idx < 0) break;
      const [removed] = this.#jobs.splice(idx, 1);
      this.#entries.delete(removed!.id);
      this.#dirty.delete(removed!.id);
      this.#unremoved.add(removed!.id);
    }
    if (this.#store && this.#unremoved.size > 0 && !this.#suspended) this.#flush(this.#store);
  }

  // --- operators -------------------------------------------------------------

  /**
   * Run a failed job again under the same id, with a clean crash count. If an
   * identical job is already waiting, that one is returned and started instead,
   * so a retry cannot queue the same work twice. Null unless it had failed.
   */
  retry(id: string): Job | null {
    const job = this.get(id);
    if (!job || job.state !== "failed") return null;
    const waiting = this.#waitingMatch((j) => sameTarget(j.request, job.request));
    if (waiting) {
      this.#release(waiting);
      return waiting;
    }
    job.state = "queued";
    job.startedAt = null;
    job.finishedAt = null;
    job.result = null;
    job.error = null;
    const entry = this.#entries.get(id)!;
    entry.dueAt = null;
    entry.deferred = false;
    entry.deferrals = 0;
    entry.crashes = 0;
    this.#pending++;
    this.#persist(job);
    this.#ready.push(job);
    this.#schedulePump();
    return job;
  }

  /**
   * Stop a job that has not finished. A waiting job fails at once. A running one
   * is signalled and fails at its next safe point, so it may still be `running`
   * in the response; what it wrote so far stands and a later job resumes from it.
   * Returns null if it has already finished. Cancelled jobs report `failed`.
   */
  cancel(id: string): Job | null {
    const job = this.get(id);
    if (!job) return null;
    if (job === this.#active) {
      this.#cancelRequested = true;
      this.#abort?.abort();
      return job;
    }
    if (job.state !== "queued") return null;
    this.#forgetWaiting(job);
    job.state = "failed";
    job.error = "Cancelled";
    job.finishedAt = new Date().toISOString();
    // A parked job already left the count when shutdown stopped it.
    if (!this.#parked.delete(job.id)) this.#pending--;
    this.#persist(job);
    this.#settle();
    return job;
  }

  #forgetWaiting(job: Job): void {
    const timer = this.#held.get(job.id);
    if (timer !== undefined) {
      this.#scheduler.clearTimeout(timer);
      this.#held.delete(job.id);
    }
    const ready = this.#ready.indexOf(job);
    if (ready >= 0) this.#ready.splice(ready, 1);
    const entry = this.#entries.get(job.id)!;
    entry.dueAt = null;
    entry.deferred = false;
  }

  /**
   * Start no more jobs. A running job runs to its end, and writes still queue, so
   * nothing accepted is lost: it waits. Held in memory: a restart resumes.
   */
  pause(): void {
    this.#paused = true;
  }

  resume(): void {
    this.#paused = false;
    this.#schedulePump();
  }

  get paused(): boolean {
    return this.#paused;
  }

  // --- reading ---------------------------------------------------------------

  get(id: string): Job | undefined {
    return this.#jobs.find((j) => j.id === id);
  }

  list(): Job[] {
    return [...this.#jobs].reverse();
  }

  get pending(): number {
    return this.#pending;
  }

  counts(): QueueCounts {
    const count = (state: JobState) => this.#jobs.filter((j) => j.state === state).length;
    return { queued: count("queued"), held: this.#held.size, running: count("running"), succeeded: count("succeeded"), failed: count("failed") };
  }

  /** Stop waiting: every held job becomes eligible to run now, retries included. */
  flush(): void {
    for (const job of [...this.#jobs]) this.#release(job);
  }

  /**
   * Resolves once nothing is queued or running, including held jobs, so it
   * waits out the debounce. Use `flush()` first to skip the wait.
   */
  idle(): Promise<void> {
    return this.#until(() => this.#pending === 0);
  }

  #until(done: () => boolean): Promise<void> {
    if (done()) return Promise.resolve();
    return new Promise((resolve) => {
      const check = (): void => {
        if (done()) resolve();
        else this.#idleWaiters.push(check);
      };
      this.#idleWaiters.push(check);
    });
  }

  /**
   * Release debounced jobs and wait for the queue to finish them, for at most
   * `timeoutMs`. Returns false on timeout. Jobs waiting on a retry are left alone:
   * they cannot finish now, and with a store they survive the restart anyway.
   */
  drain(timeoutMs: number): Promise<boolean> {
    for (const job of [...this.#jobs]) {
      if (!this.#entries.get(job.id)?.deferred) this.#release(job);
    }
    const waitingOnRetry = (): number => [...this.#held.keys()].filter((id) => this.#entries.get(id)?.deferred).length;
    // Paused, nothing new starts, so only the job already running is waited for.
    const finished = this.#paused ? () => this.#active === null : () => this.#pending === waitingOnRetry();
    const done = (): boolean => this.#pending === waitingOnRetry();
    if (finished()) return Promise.resolve(done());
    return new Promise((resolve) => {
      const timer = this.#scheduler.setTimeout(() => resolve(false), timeoutMs);
      void this.#until(finished).then(() => {
        this.#scheduler.clearTimeout(timer);
        resolve(done());
      });
    });
  }
}

function errorText(err: unknown): string {
  if (err instanceof JobDeferred) return "Cancelled";
  return err instanceof Error ? err.message : String(err);
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
