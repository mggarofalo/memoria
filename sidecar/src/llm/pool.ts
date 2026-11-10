import { createHash } from "node:crypto";
import { WorkerClient, WorkerError, type OperationTimeouts } from "./client.ts";
import { cosineOf } from "./probe.ts";
import type { Capability, LlmCallSink, PoolLoad, PoolWorkerConfig, PriorityClass, WorkerView } from "./types.ts";
import { parseCapabilities, parseHealth, ResponseViolation, type WorkerCapabilities, type WorkerHealth } from "./validate.ts";

/**
 * The sidecar's view of its workers, and the one place that decides who does
 * what. Every model call goes through `dispatch`:
 *
 * - **Routing:** among workers that can serve the capability, not busy first,
 *   then lowest queue depth, then best priority (lower number wins), then name.
 *   A batch is one call to one worker, and a retry moves the whole batch.
 * - **Failure:** one retry on a different worker. Repeated failures open that
 *   worker's circuit for that role, so a worker whose reranker is down costs no
 *   further reranker calls until a backoff passes. Then a heartbeat makes one trial
 *   call, so a role recovers without waiting for a search to try it. Embedding has
 *   two circuits: one bad document chunk must not cost search its query vectors.
 * - **Priority:** interactive calls take free slots ahead of waiting background
 *   ones, and one slot per worker is kept back from background work. Calls in
 *   flight are not interrupted, so this is priority at the next free slot.
 *
 * Tokens stay inside each `WorkerClient`. Nothing here stores or returns them.
 */

export interface BreakerOptions {
  /** Consecutive failures that open the circuit. */
  threshold: number;
  backoffBaseMs: number;
  backoffMaxMs: number;
}

export const DEFAULT_BREAKER: BreakerOptions = { threshold: 3, backoffBaseMs: 5_000, backoffMaxMs: 60_000 };

/** Outcomes kept for a worker's reported error rate. */
const OUTCOME_WINDOW = 100;
/** Refusals in a row before "you sent a bad request" starts to look like "this worker is broken". */
const REFUSAL_STREAK_LIMIT = 5;

const ROLES: readonly Capability[] = ["embed", "rerank", "generate"];

export type BreakerState = "closed" | "open" | "half_open";

/** Document embedding fails for reasons query embedding does not, so it has its own circuit. */
type CircuitKey = Capability | "documents";
const CIRCUITS: readonly CircuitKey[] = [...ROLES, "documents"];

export class Breaker {
  readonly #options: BreakerOptions;
  #state: BreakerState = "closed";
  #failures = 0;
  #openUntil = 0;
  #backoff: number;

  constructor(options: BreakerOptions) {
    this.#options = options;
    this.#backoff = options.backoffBaseMs;
  }

  get state(): BreakerState {
    return this.#state;
  }

  /** Could a call go now? An elapsed open circuit allows exactly one, via `begin`. */
  allows(now: number): boolean {
    if (this.#state === "closed") return true;
    if (this.#state === "half_open") return false;
    return now >= this.#openUntil;
  }

  begin(now: number): void {
    if (this.#state === "open" && now >= this.#openUntil) this.#state = "half_open";
  }

  /** A call that began before the circuit opened proves nothing, so it cannot close it. */
  success(): void {
    if (this.#state === "open") return;
    this.#state = "closed";
    this.#failures = 0;
    this.#backoff = this.#options.backoffBaseMs;
  }

  failure(now: number): void {
    if (this.#state === "open") return;
    if (this.#state === "half_open") {
      this.#backoff = Math.min(this.#backoff * 2, this.#options.backoffMaxMs);
      this.#open(now);
      return;
    }
    this.#failures++;
    if (this.#failures >= this.#options.threshold) this.#open(now);
  }

  /** Known good, by a test of the role itself rather than the call that failed. */
  reset(): void {
    this.#state = "closed";
    this.#failures = 0;
    this.#backoff = this.#options.backoffBaseMs;
  }

  /** The worker answered but the call says nothing about the role, such as a wrong-model refusal. */
  settle(): void {
    if (this.#state === "half_open") this.#state = "closed";
  }

  /** A heartbeat got through after an outage: try a real call without waiting out the backoff. */
  recovered(now: number): void {
    if (this.#state === "closed") return;
    this.#state = "open";
    this.#openUntil = now;
  }

  /** A trial that never reported back must not wedge the circuit until a restart. */
  unstick(): void {
    if (this.#state === "half_open") this.#state = "open";
  }

  #open(now: number): void {
    this.#state = "open";
    this.#openUntil = now + this.#backoff;
  }
}

interface Entry {
  config: PoolWorkerConfig;
  client: WorkerClient;
  breakers: Record<CircuitKey, Breaker>;
  /** Null until the first heartbeat. */
  reachable: boolean | null;
  health: WorkerHealth | null;
  lastHeartbeat: number | null;
  capabilities: WorkerCapabilities | null;
  /** Null until checked. False keeps this worker off embedding and tokens. */
  modelOk: boolean | null;
  refusals: Record<CircuitKey, number>;
  inFlight: Record<PriorityClass, number>;
  outcomes: boolean[];
  calls: number;
  /** Out of rotation by an operator's request. Held in memory only: a restart puts it back. */
  drained: boolean;
}

/** What a dispatched call may use. Deliberately not the whole entry. */
export interface DispatchTarget {
  name: string;
  client: WorkerClient;
}

/** A small real call for a role, proving it works. Throws `WorkerError` if not. */
export type RoleTrial = (capability: Capability, target: DispatchTarget) => Promise<void>;

/** A worker's embedding of a fixed text, to compare with its neighbours'. */
export type EmbedProbe = (target: DispatchTarget) => Promise<number[]>;

export interface WorkerPoolOptions {
  workers: PoolWorkerConfig[];
  /** The exact file names, in lower case, that count as the index's embedding model. */
  embedFiles: readonly string[];
  breaker?: Partial<BreakerOptions>;
  timeouts?: Partial<OperationTimeouts>;
  fetch?: typeof globalThis.fetch;
  onCall?: LlmCallSink;
  now?: () => number;
  /** Longest a call waits for a free slot. */
  queueTimeoutMs?: Record<PriorityClass, number>;
  /** Time left for the search in progress, which a wait for a slot must not outlast. */
  interactiveBudgetMs?: () => number | null;
  trial?: RoleTrial;
  probe?: EmbedProbe;
  probeEveryMs?: number;
  /** Told when workers start or stop disagreeing about the same text. */
  onDisagreement?: (workers: readonly string[]) => void;
  /** Slots per worker that background work may not use, when the worker has more than that. */
  interactiveReserve?: number;
}

type Attempt<T> = { ok: true; value: T } | { ok: false; error: WorkerError; retry: boolean };

/** Below this, two workers are not running one model. Above it is rounding between CPU and GPU. */
const PROBE_MIN_COSINE = 0.995;

function circuitFor(capability: Capability, klass: PriorityClass): CircuitKey {
  return capability === "embed" && klass === "background" ? "documents" : capability;
}

/** Operations that need the worker to hold a particular role. */
const OPERATIONS: Record<Capability, string> = { embed: "embed", rerank: "rerank", generate: "complete" };

function isBodySpecific(err: WorkerError): boolean {
  return err.code === "rejected" && (err.status === 400 || err.status === 413);
}

/** The pool's breaker, with whatever this worker's own configuration says instead. */
function breakerFor(config: PoolWorkerConfig, pool: BreakerOptions): BreakerOptions {
  const own = config.availability ?? {};
  return {
    threshold: own.failureThreshold ?? pool.threshold,
    backoffBaseMs: own.backoffMs ?? pool.backoffBaseMs,
    backoffMaxMs: own.maxBackoffMs ?? pool.backoffMaxMs,
  };
}

function newEntry(config: PoolWorkerConfig, pool: BreakerOptions, options: WorkerPoolOptions, onCall: LlmCallSink | undefined): Entry {
  const breaker = breakerFor(config, pool);
  return {
    config,
    client: new WorkerClient({
      url: config.url,
      token: config.token,
      ...(options.timeouts ? { timeouts: options.timeouts } : {}),
      ...(options.fetch ? { fetch: options.fetch } : {}),
      ...(onCall ? { onCall } : {}),
    }),
    breakers: { embed: new Breaker(breaker), rerank: new Breaker(breaker), generate: new Breaker(breaker), documents: new Breaker(breaker) },
    reachable: null,
    health: null,
    lastHeartbeat: null,
    capabilities: null,
    modelOk: null,
    refusals: { embed: 0, rerank: 0, generate: 0, documents: 0 },
    inFlight: { interactive: 0, background: 0 },
    outcomes: [],
    calls: 0,
    drained: false,
  };
}

function worstCircuit(entry: Entry): BreakerState {
  const states = entry.config.capabilities.map((role) => entry.breakers[role].state);
  if (entry.config.capabilities.includes("embed")) states.push(entry.breakers.documents.state);
  if (states.includes("open")) return "open";
  return states.includes("half_open") ? "half_open" : "closed";
}

export class WorkerPool {
  readonly #entries: Entry[];
  readonly #embedFiles: ReadonlySet<string>;
  readonly #now: () => number;
  readonly #queueTimeoutMs: Record<PriorityClass, number>;
  readonly #interactiveBudgetMs: () => number | null;
  readonly #trial: RoleTrial | undefined;
  readonly #probe: EmbedProbe | undefined;
  readonly #probeEveryMs: number;
  readonly #onDisagreement: ((workers: readonly string[]) => void) | undefined;
  readonly #interactiveReserve: number;
  #lastProbe: number | null = null;
  #disagreeing: readonly string[] = [];
  readonly #waiters: Array<() => void> = [];
  readonly #waiting: Record<PriorityClass, number> = { interactive: 0, background: 0 };
  #poll: NodeJS.Timeout | undefined;

  constructor(options: WorkerPoolOptions) {
    if (options.workers.length === 0) throw new Error("A worker pool needs at least one worker");
    this.#embedFiles = new Set(options.embedFiles);
    this.#now = options.now ?? (() => Date.now());
    this.#queueTimeoutMs = options.queueTimeoutMs ?? { interactive: 5_000, background: 600_000 };
    this.#interactiveBudgetMs = options.interactiveBudgetMs ?? (() => null);
    this.#trial = options.trial;
    this.#probe = options.probe;
    this.#probeEveryMs = options.probeEveryMs ?? 60_000;
    this.#onDisagreement = options.onDisagreement;
    this.#interactiveReserve = options.interactiveReserve ?? 1;
    const breaker = { ...DEFAULT_BREAKER, ...options.breaker };
    // Heartbeats would drown the per-call log, and they say nothing about model work.
    const sink = options.onCall;
    const onCall: LlmCallSink | undefined = sink && ((entry) => {
      if (entry.operation !== "health" && entry.operation !== "capabilities") sink(entry);
    });
    this.#entries = options.workers.map((config) => newEntry(config, breaker, options, onCall));
  }

  // --- heartbeat -------------------------------------------------------------

  start(pollMs: number): void {
    void this.refresh();
    this.#poll = setInterval(() => void this.refresh(), pollMs);
    this.#poll.unref();
  }

  stop(): void {
    if (this.#poll) clearInterval(this.#poll);
    this.#poll = undefined;
  }

  async refresh(): Promise<void> {
    await Promise.all(this.#entries.map((entry) => this.#heartbeat(entry)));
    await this.#compareEmbeddings();
    this.#wake();
  }

  async #heartbeat(entry: Entry): Promise<void> {
    const wasDown = entry.reachable === false;
    try {
      entry.health = parseHealth(await entry.client.get("health", "/health"));
      entry.capabilities = parseCapabilities(await entry.client.get("capabilities", "/capabilities"));
    } catch (err) {
      if (!(err instanceof WorkerError) && !(err instanceof ResponseViolation)) throw err;
      entry.reachable = false;
      entry.health = null;
      return;
    }
    entry.reachable = true;
    entry.lastHeartbeat = this.#now();
    this.#settleCircuits(entry, wasDown);
    entry.modelOk = this.servesIndexModel(entry.capabilities?.models.embed);
    await this.#runTrials(entry);
  }

  /**
   * A worker answering `/health` proves little about a role, since a reranker can
   * fail while the process is fine. So a good beat frees a stuck trial, and only a
   * worker returning from an outage gets its trials early.
   */
  #settleCircuits(entry: Entry, wasDown: boolean): void {
    for (const key of CIRCUITS) {
      entry.breakers[key].unstick();
      if (wasDown) entry.breakers[key].recovered(this.#now());
    }
  }

  /**
   * An open circuit whose backoff has passed gets a real call from here. Otherwise
   * `/ready` would stay degraded until a search cleared it, and a router going by
   * `/ready` would never send that search.
   */
  async #runTrials(entry: Entry): Promise<void> {
    if (!this.#trial) return;
    const now = this.#now();
    for (const key of CIRCUITS) {
      const capability: Capability = key === "documents" ? "embed" : key;
      const breaker = entry.breakers[key];
      if (breaker.state !== "open" || !breaker.allows(now) || !this.#servesRole(entry, capability)) continue;
      breaker.begin(now);
      if (await this.#tryRole(capability, entry)) {
        breaker.success();
        entry.refusals[key] = 0;
      } else {
        breaker.failure(this.#now());
      }
    }
  }

  #tryRole(capability: Capability, entry: Entry): Promise<boolean> {
    return this.#passes(entry, (target) => this.#trial!(capability, target));
  }

  /** Did the worker do it? Anything it did wrong is an answer, and anything else is a bug here. */
  async #passes(entry: Entry, check: (target: DispatchTarget) => Promise<unknown>): Promise<boolean> {
    try {
      await check({ name: entry.config.name, client: entry.client });
      return true;
    } catch (err) {
      if (err instanceof WorkerError) return false;
      throw err;
    }
  }

  /**
   * Can any worker embed at all? Called when a batch fails, because that alone cannot
   * tell a bad batch from a dead worker. A known-good text can. It ignores the document
   * circuit, which the bad batch may have opened, and clears it for a worker that passes.
   */
  async confirmDocuments(): Promise<boolean> {
    if (!this.#trial) return false;
    let alive = false;
    for (const entry of this.#entries) {
      if (entry.reachable === false || !this.#servesRole(entry, "embed")) continue;
      if (!(await this.#tryRole("embed", entry))) continue;
      entry.breakers.documents.reset();
      entry.refusals.documents = 0;
      alive = true;
    }
    return alive;
  }

  /** Every worker that could take embedding passes, and there is at least one. */
  async everyEmbedderPasses(check: (target: DispatchTarget) => Promise<unknown>): Promise<boolean> {
    const able = this.#entries.filter((e) => e.reachable !== false && this.#servesRole(e, "embed"));
    const passed = await Promise.all(able.map((e) => this.#passes(e, check)));
    return able.length > 0 && passed.every(Boolean);
  }

  /**
   * Catches honest mistakes, such as two workers on different builds of a model. It
   * only reports. A token holder is already trusted, a careful liar would answer this
   * public text honestly, and acting on it could take embedding down over a legitimate
   * model update. With one worker there is nothing to compare, so it does not run.
   */
  async #compareEmbeddings(): Promise<void> {
    if (!this.#probe || !this.#probeDue()) return;
    this.#lastProbe = this.#now();
    const answers: Array<{ name: string; vector: number[] }> = [];
    for (const entry of this.#entries) {
      if (!this.#probeable(entry)) continue;
      const vector = await this.#probeWorker(entry);
      if (vector) answers.push({ name: entry.config.name, vector });
    }
    // Fewer than two answers says nothing, so the last verdict stands.
    if (answers.length < 2) return;
    const first = answers[0]!.vector;
    const agreed = answers.every((a) => cosineOf(first, a.vector) >= PROBE_MIN_COSINE);
    this.#setDisagreement(agreed ? [] : answers.map((a) => a.name));
  }

  /** Up, in rotation, and serving the index's model, so its vector says something. */
  #probeable(entry: Entry): boolean {
    return entry.reachable === true && !entry.drained && entry.config.capabilities.includes("embed") && entry.modelOk === true;
  }

  #probeDue(): boolean {
    const embedders = this.#entries.filter((e) => !e.drained && e.config.capabilities.includes("embed")).length;
    return embedders > 1 && (this.#lastProbe === null || this.#now() - this.#lastProbe >= this.#probeEveryMs);
  }

  async #probeWorker(entry: Entry): Promise<number[] | null> {
    try {
      return await this.#probe!({ name: entry.config.name, client: entry.client });
    } catch (err) {
      if (err instanceof WorkerError) return null;
      throw err;
    }
  }

  #setDisagreement(workers: readonly string[]): void {
    if (workers.join() === this.#disagreeing.join()) return;
    this.#disagreeing = workers;
    this.#onDisagreement?.(workers);
  }

  /** Workers that answered the same text differently, at the last comparison. */
  get disagreeing(): readonly string[] {
    return this.#disagreeing;
  }

  /** Exact, so `evil-embeddinggemma...` does not pass for `embeddinggemma...`. */
  servesIndexModel(file: unknown): boolean {
    return typeof file === "string" && this.#embedFiles.has(file.toLowerCase());
  }

  // --- routing ---------------------------------------------------------------

  #servesRole(entry: Entry, capability: Capability): boolean {
    if (entry.drained || !entry.config.capabilities.includes(capability)) return false;
    if (capability === "embed" && entry.modelOk === false) return false;
    if (entry.capabilities && !entry.capabilities.operations.includes(OPERATIONS[capability])) return false;
    return !entry.health || entry.health.models[capability];
  }

  #eligible(entry: Entry, capability: Capability, key: CircuitKey, now: number): boolean {
    return entry.reachable !== false && entry.breakers[key].allows(now) && this.#servesRole(entry, capability);
  }

  #candidates(capability: Capability, key: CircuitKey, exclude: ReadonlySet<Entry>): Entry[] {
    const now = this.#now();
    const depth = (e: Entry) => e.inFlight.interactive + e.inFlight.background + (e.health?.queueDepth ?? 0);
    const busy = (e: Entry) => (e.health?.status === "busy" ? 1 : 0);
    return this.#entries
      .filter((e) => !exclude.has(e) && this.#eligible(e, capability, key, now))
      .sort((a, b) => busy(a) - busy(b) || depth(a) - depth(b) || a.config.priority - b.config.priority || a.config.name.localeCompare(b.config.name));
  }

  /** Is anyone able to take this now? Optimistic before the first heartbeat. */
  canServe(capability: Capability, klass: PriorityClass = "interactive"): boolean {
    return this.#candidates(capability, circuitFor(capability, klass), new Set()).length > 0;
  }

  /** Slots background work could use at once, to size embedding concurrency. */
  backgroundCapacity(capability: Capability): number {
    return this.#candidates(capability, circuitFor(capability, "background"), new Set()).reduce((sum, e) => sum + this.#limit(e, "background"), 0);
  }

  /** Slots per worker are kept back from background work, but it always keeps one. */
  #limit(entry: Entry, klass: PriorityClass): number {
    const max = entry.config.maxConcurrent;
    return klass === "background" ? Math.max(1, max - this.#interactiveReserve) : max;
  }

  // --- operator control ------------------------------------------------------

  /**
   * Take a worker out of rotation: calls in flight finish, and it gets no new ones.
   * Heartbeats go on, so its state stays visible. Null for a name not in the pool.
   */
  drain(name: string): WorkerView | null {
    return this.#setDrained(name, true);
  }

  /** Put a drained worker back. Null for a name not in the pool. */
  enable(name: string): WorkerView | null {
    return this.#setDrained(name, false);
  }

  #setDrained(name: string, drained: boolean): WorkerView | null {
    const index = this.#entries.findIndex((e) => e.config.name === name);
    if (index < 0) return null;
    this.#entries[index]!.drained = drained;
    this.#wake();
    return this.workers()[index]!;
  }

  #wake(): void {
    for (const resolve of this.#waiters.splice(0)) resolve();
  }

  #takeFreeSlot(candidates: Entry[], key: CircuitKey, klass: PriorityClass): Entry | null {
    if (klass === "background" && this.#waiting.interactive > 0) return null;
    const free = candidates.find((e) => e.inFlight.interactive + e.inFlight.background < this.#limit(e, klass));
    if (!free) return null;
    free.breakers[key].begin(this.#now());
    free.inFlight[klass]++;
    return free;
  }

  #waitForChange(): Promise<void> {
    return new Promise<void>((resolve) => {
      // The timer covers changes nothing signals, such as a backoff elapsing.
      const timer = setTimeout(resolve, 250);
      timer.unref();
      this.#waiters.push(() => {
        clearTimeout(timer);
        resolve();
      });
    });
  }

  /** Take a slot on the best worker, waiting if all are full. Null when nobody can serve this. */
  async #acquire(capability: Capability, klass: PriorityClass, exclude: ReadonlySet<Entry>, operation: string): Promise<Entry | null> {
    const key = circuitFor(capability, klass);
    const budget = klass === "interactive" ? this.#interactiveBudgetMs() : null;
    const deadline = this.#now() + Math.min(this.#queueTimeoutMs[klass], budget ?? Infinity);
    this.#waiting[klass]++;
    let rechecked = klass !== "background";
    try {
      for (;;) {
        const candidates = this.#candidates(capability, key, exclude);
        if (candidates.length === 0 && !rechecked) {
          // A long synchronous index write stalls this thread past a heartbeat's
          // timeout, which marks a healthy worker down. Background work can afford
          // to ask again rather than call that an outage.
          rechecked = true;
          await Promise.all(this.#entries.filter((e) => e.reachable === false).map((e) => this.#heartbeat(e)));
          continue;
        }
        if (candidates.length === 0) return null;
        const taken = this.#takeFreeSlot(candidates, key, klass);
        if (taken) return taken;
        if (this.#now() >= deadline) throw new WorkerError("saturated", operation, "pool", "no worker had a free slot before the deadline");
        await this.#waitForChange();
      }
    } finally {
      this.#waiting[klass]--;
    }
  }

  // --- dispatch --------------------------------------------------------------

  /**
   * Run `work` on the best worker, retrying once on a different one. Throws the
   * failure if no attempt succeeds, or `no_worker` if nobody could be tried.
   */
  async dispatch<T>(capability: Capability, klass: PriorityClass, operation: string, work: (target: DispatchTarget) => Promise<T>): Promise<T> {
    const tried = new Set<Entry>();
    let lastError: WorkerError | undefined;
    for (let attempt = 0; attempt < 2; attempt++) {
      const entry = await this.#acquire(capability, klass, tried, operation);
      if (!entry) break;
      tried.add(entry);
      const outcome = await this.#attempt(entry, capability, klass, operation, work);
      if (outcome.ok) return outcome.value;
      lastError = outcome.error;
      if (!outcome.retry) throw lastError;
    }
    if (lastError) throw lastError;
    throw new WorkerError("no_worker", operation, "pool", `no worker can serve ${capability} right now`);
  }

  async #attempt<T>(entry: Entry, capability: Capability, klass: PriorityClass, operation: string, work: (target: DispatchTarget) => Promise<T>): Promise<Attempt<T>> {
    try {
      if (capability === "embed") await this.#verifyEmbedModel(entry, operation);
      const value = await work({ name: entry.config.name, client: entry.client });
      const key = circuitFor(capability, klass);
      entry.breakers[key].success();
      entry.refusals[key] = 0;
      this.#record(entry, true);
      return { ok: true, value };
    } catch (err) {
      return this.#failed(entry, circuitFor(capability, klass), err);
    } finally {
      entry.inFlight[klass]--;
      this.#wake();
    }
  }

  #failed(entry: Entry, key: CircuitKey, err: unknown): Attempt<never> {
    const breaker = entry.breakers[key];
    if (!(err instanceof WorkerError)) {
      this.#record(entry, false);
      breaker.failure(this.#now());
      throw err;
    }
    if (err.code === "deadline") {
      // The search ran out of time before asking. The worker has done nothing wrong.
      breaker.settle();
      return { ok: false, error: err, retry: false };
    }
    if (isBodySpecific(err) && ++entry.refusals[key] < REFUSAL_STREAK_LIMIT) {
      // The worker answered and refused this request; another would refuse it too.
      breaker.settle();
      this.#record(entry, true);
      return { ok: false, error: err, retry: false };
    }
    this.#record(entry, false);
    if (err.code === "model_mismatch") {
      entry.modelOk = false;
      breaker.settle();
    } else {
      breaker.failure(this.#now());
    }
    return { ok: false, error: err, retry: true };
  }

  /** A worker that has not been heartbeat-checked yet is asked before it embeds anything. */
  async #verifyEmbedModel(entry: Entry, operation: string): Promise<void> {
    if (entry.modelOk === true) return;
    let capabilities;
    try {
      // Named apart from the heartbeat's call: this one is on a request's path, so it is logged.
      capabilities = parseCapabilities(await entry.client.get("verify-model", "/capabilities"));
    } catch (err) {
      if (err instanceof ResponseViolation) throw new WorkerError("invalid_response", operation, entry.client.name, err.message);
      throw err;
    }
    entry.modelOk = this.servesIndexModel(capabilities.models.embed);
    if (!entry.modelOk) throw new WorkerError("model_mismatch", operation, entry.client.name, "the worker does not serve the index's embedding model");
  }

  #record(entry: Entry, ok: boolean): void {
    entry.calls++;
    entry.outcomes.push(ok);
    if (entry.outcomes.length > OUTCOME_WINDOW) entry.outcomes.shift();
  }

  // --- reporting -------------------------------------------------------------

  /** Who is scoring and expanding, for cache keys: a change of worker must not replay old answers. */
  cacheIdentity(capability: Capability): string {
    const parts = this.#entries
      .filter((e) => e.config.capabilities.includes(capability))
      .map((e) => `${e.config.name}|${e.client.name}|${e.capabilities?.models[capability] ?? ""}`)
      .sort();
    return createHash("sha256").update(parts.join("\n")).digest("hex").slice(0, 12);
  }

  /**
   * Roles at least one worker can serve by recent evidence: loaded by its last
   * heartbeat, with a closed circuit. So a role failing every call reads as down
   * even while `/health` looks fine. False before any heartbeat.
   */
  status(): Record<Capability, boolean> {
    const up = (role: Capability) => this.#entries.some((e) => e.reachable === true && e.breakers[role].state === "closed" && this.#servesRole(e, role) && e.health?.models[role] === true);
    return { embed: up("embed"), rerank: up("rerank"), generate: up("generate") };
  }

  /** Can any worker take document embedding? Query vectors have their own circuit, so this can differ. */
  documentsUp(): boolean {
    return this.#entries.some((e) => e.reachable === true && e.breakers.documents.state === "closed" && this.#servesRole(e, "embed") && e.health?.models.embed === true);
  }

  #state(entry: Entry): WorkerView["state"] {
    if (entry.reachable === false) return "down";
    if (entry.config.capabilities.some((role) => entry.breakers[role].state !== "closed")) return "circuit_open";
    if (entry.reachable === null) return "unknown";
    if (entry.health?.status === "starting") return "starting";
    return entry.health?.status === "busy" ? "busy" : "up";
  }

  /** Built field by field, so nothing about a worker's credentials can come along. */
  workers(): WorkerView[] {
    return this.#entries.map((e) => {
      const failures = e.outcomes.filter((ok) => !ok).length;
      return {
        name: e.config.name,
        host: e.client.name,
        state: this.#state(e),
        circuit: worstCircuit(e),
        circuits: { embed: e.breakers.embed.state, rerank: e.breakers.rerank.state, generate: e.breakers.generate.state, documents: e.breakers.documents.state },
        embedDisagrees: this.#disagreeing.includes(e.config.name),
        capabilities: [...e.config.capabilities],
        priority: e.config.priority,
        maxConcurrent: e.config.maxConcurrent,
        embedModelCompatible: e.modelOk,
        rerankTransport: e.capabilities?.rerankTransport ?? null,
        lastHeartbeat: e.lastHeartbeat === null ? null : new Date(e.lastHeartbeat).toISOString(),
        inFlight: e.inFlight.interactive + e.inFlight.background,
        queueDepth: e.health?.queueDepth ?? 0,
        calls: e.calls,
        errorRate: e.outcomes.length === 0 ? 0 : failures / e.outcomes.length,
        drained: e.drained,
      };
    });
  }

  load(): PoolLoad {
    const sum = (klass: PriorityClass) => this.#entries.reduce((total, e) => total + e.inFlight[klass], 0);
    return {
      classes: {
        interactive: { inFlight: sum("interactive"), waiting: this.#waiting.interactive },
        background: { inFlight: sum("background"), waiting: this.#waiting.background },
      },
      workers: this.#entries.map((e) => ({ name: e.config.name, interactive: e.inFlight.interactive, background: e.inFlight.background, queueDepth: e.health?.queueDepth ?? 0 })),
    };
  }
}
