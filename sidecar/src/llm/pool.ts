import { createHash } from "node:crypto";
import { WorkerClient, WorkerError, type OperationTimeouts } from "./client.ts";
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
 *   further reranker calls until a backoff passes, and then only one trial call.
 *   Its embedding, if healthy, carries on.
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
  breakers: Record<Capability, Breaker>;
  /** Null until the first heartbeat. */
  reachable: boolean | null;
  health: WorkerHealth | null;
  lastHeartbeat: number | null;
  capabilities: WorkerCapabilities | null;
  /** Null until checked. False keeps this worker off embedding and tokens. */
  modelOk: boolean | null;
  /** The worker's answer to the probe text disagreed with the reference. */
  probeFailed: boolean;
  lastProbe: number | null;
  refusals: Record<Capability, number>;
  inFlight: Record<PriorityClass, number>;
  outcomes: boolean[];
  calls: number;
}

/** What a dispatched call may use. Deliberately not the whole entry. */
export interface DispatchTarget {
  name: string;
  client: WorkerClient;
}

/** Compare a worker's embedding of a fixed text against the reference. False means it disagrees. */
export type EmbedProbe = (target: DispatchTarget) => Promise<boolean>;

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
  probe?: EmbedProbe;
  probeEveryMs?: number;
}

type Attempt<T> = { ok: true; value: T } | { ok: false; error: WorkerError; retry: boolean };

/** Operations that need the worker to hold a particular role. */
const OPERATIONS: Record<Capability, string> = { embed: "embed", rerank: "rerank", generate: "complete" };

function isBodySpecific(err: WorkerError): boolean {
  return err.code === "rejected" && (err.status === 400 || err.status === 413);
}

function newEntry(config: PoolWorkerConfig, breaker: BreakerOptions, options: WorkerPoolOptions, onCall: LlmCallSink | undefined): Entry {
  return {
    config,
    client: new WorkerClient({
      url: config.url,
      token: config.token,
      ...(options.timeouts ? { timeouts: options.timeouts } : {}),
      ...(options.fetch ? { fetch: options.fetch } : {}),
      ...(onCall ? { onCall } : {}),
    }),
    breakers: { embed: new Breaker(breaker), rerank: new Breaker(breaker), generate: new Breaker(breaker) },
    reachable: null,
    health: null,
    lastHeartbeat: null,
    capabilities: null,
    modelOk: null,
    probeFailed: false,
    lastProbe: null,
    refusals: { embed: 0, rerank: 0, generate: 0 },
    inFlight: { interactive: 0, background: 0 },
    outcomes: [],
    calls: 0,
  };
}

function worstCircuit(entry: Entry): BreakerState {
  const states = entry.config.capabilities.map((role) => entry.breakers[role].state);
  if (states.includes("open")) return "open";
  return states.includes("half_open") ? "half_open" : "closed";
}

export class WorkerPool {
  readonly #entries: Entry[];
  readonly #embedFiles: ReadonlySet<string>;
  readonly #now: () => number;
  readonly #queueTimeoutMs: Record<PriorityClass, number>;
  readonly #probe: EmbedProbe | undefined;
  readonly #probeEveryMs: number;
  readonly #waiters: Array<() => void> = [];
  readonly #waiting: Record<PriorityClass, number> = { interactive: 0, background: 0 };
  #poll: NodeJS.Timeout | undefined;

  constructor(options: WorkerPoolOptions) {
    if (options.workers.length === 0) throw new Error("A worker pool needs at least one worker");
    this.#embedFiles = new Set(options.embedFiles);
    this.#now = options.now ?? (() => Date.now());
    this.#queueTimeoutMs = options.queueTimeoutMs ?? { interactive: 5_000, background: 600_000 };
    this.#probe = options.probe;
    this.#probeEveryMs = options.probeEveryMs ?? 60_000;
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
    await this.#checkModel(entry);
  }

  /**
   * A worker answering `/health` proves little about a role, since a reranker can
   * fail while the process is fine. So a good beat frees a stuck trial, and only a
   * worker returning from an outage gets its trials early.
   */
  #settleCircuits(entry: Entry, wasDown: boolean): void {
    for (const role of ROLES) {
      entry.breakers[role].unstick();
      if (wasDown) entry.breakers[role].recovered(this.#now());
    }
  }

  /** Re-checked every beat: a worker can restart with a different model. */
  async #checkModel(entry: Entry): Promise<void> {
    const named = this.servesIndexModel(entry.capabilities?.models.embed);
    if (named && this.#probeDue(entry)) await this.#runProbe(entry);
    entry.modelOk = named && !entry.probeFailed;
  }

  #probeDue(entry: Entry): boolean {
    if (!this.#probe || !entry.config.capabilities.includes("embed")) return false;
    return entry.lastProbe === null || this.#now() - entry.lastProbe >= this.#probeEveryMs;
  }

  /** An unanswered probe says nothing about the model, so only a real answer changes the verdict. */
  async #runProbe(entry: Entry): Promise<void> {
    entry.lastProbe = this.#now();
    try {
      entry.probeFailed = !(await this.#probe!({ name: entry.config.name, client: entry.client }));
    } catch (err) {
      if (!(err instanceof WorkerError)) throw err;
    }
  }

  /** Exact, so `evil-embeddinggemma...` does not pass for `embeddinggemma...`. */
  servesIndexModel(file: unknown): boolean {
    return typeof file === "string" && this.#embedFiles.has(file.toLowerCase());
  }

  // --- routing ---------------------------------------------------------------

  #servesRole(entry: Entry, capability: Capability): boolean {
    if (!entry.config.capabilities.includes(capability)) return false;
    if (capability === "embed" && entry.modelOk === false) return false;
    if (entry.capabilities && !entry.capabilities.operations.includes(OPERATIONS[capability])) return false;
    return !entry.health || entry.health.models[capability];
  }

  #eligible(entry: Entry, capability: Capability, now: number): boolean {
    return entry.reachable !== false && entry.breakers[capability].allows(now) && this.#servesRole(entry, capability);
  }

  #candidates(capability: Capability, exclude: ReadonlySet<Entry>): Entry[] {
    const now = this.#now();
    const depth = (e: Entry) => e.inFlight.interactive + e.inFlight.background + (e.health?.queueDepth ?? 0);
    const busy = (e: Entry) => (e.health?.status === "busy" ? 1 : 0);
    return this.#entries
      .filter((e) => !exclude.has(e) && this.#eligible(e, capability, now))
      .sort((a, b) => busy(a) - busy(b) || depth(a) - depth(b) || a.config.priority - b.config.priority || a.config.name.localeCompare(b.config.name));
  }

  /** Is anyone able to take this now? Optimistic before the first heartbeat. */
  canServe(capability: Capability): boolean {
    return this.#candidates(capability, new Set()).length > 0;
  }

  /** Slots background work could use at once, to size embedding concurrency. */
  backgroundCapacity(capability: Capability): number {
    return this.#candidates(capability, new Set()).reduce((sum, e) => sum + this.#limit(e, "background"), 0);
  }

  /** One slot per worker is kept back from background work, when there is more than one. */
  #limit(entry: Entry, klass: PriorityClass): number {
    const max = entry.config.maxConcurrent;
    return klass === "background" && max > 1 ? max - 1 : max;
  }

  #wake(): void {
    for (const resolve of this.#waiters.splice(0)) resolve();
  }

  #takeFreeSlot(candidates: Entry[], capability: Capability, klass: PriorityClass): Entry | null {
    if (klass === "background" && this.#waiting.interactive > 0) return null;
    const free = candidates.find((e) => e.inFlight.interactive + e.inFlight.background < this.#limit(e, klass));
    if (!free) return null;
    free.breakers[capability].begin(this.#now());
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
    const deadline = this.#now() + this.#queueTimeoutMs[klass];
    this.#waiting[klass]++;
    try {
      for (;;) {
        const candidates = this.#candidates(capability, exclude);
        if (candidates.length === 0) return null;
        const taken = this.#takeFreeSlot(candidates, capability, klass);
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
      entry.breakers[capability].success();
      entry.refusals[capability] = 0;
      this.#record(entry, true);
      return { ok: true, value };
    } catch (err) {
      return this.#failed(entry, capability, err);
    } finally {
      entry.inFlight[klass]--;
      this.#wake();
    }
  }

  #failed(entry: Entry, capability: Capability, err: unknown): Attempt<never> {
    const breaker = entry.breakers[capability];
    if (!(err instanceof WorkerError)) {
      this.#record(entry, false);
      breaker.failure(this.#now());
      throw err;
    }
    if (isBodySpecific(err) && ++entry.refusals[capability] < REFUSAL_STREAK_LIMIT) {
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

  #state(entry: Entry): WorkerView["state"] {
    if (entry.reachable === false) return "down";
    if (worstCircuit(entry) !== "closed") return "circuit_open";
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
        circuits: { embed: e.breakers.embed.state, rerank: e.breakers.rerank.state, generate: e.breakers.generate.state },
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
