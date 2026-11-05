import { AsyncLocalStorage } from "node:async_hooks";
import { WorkerError, type OperationTimeouts, workerName } from "./client.ts";
import { EXPANSION_GRAMMAR, EXPANSION_SAMPLING, EXPANSION_SYSTEM_PROMPT, expansionFailure, expansionPrompt, parseExpansion } from "./expansion.ts";
import { WorkerPool, type BreakerOptions, type DispatchTarget } from "./pool.ts";
import { WorkerRerankTransport, type RerankTransport } from "./rerank-transport.ts";
import type {
  Capability,
  DegradedReason,
  EmbedOptions,
  EmbeddingResult,
  ExpandOptions,
  LlmCallSink,
  LlmSurface,
  LocalFallback,
  PoolWorkerConfig,
  PriorityClass,
  Queryable,
  RerankDocument,
  RerankResult,
  WorkerOptions,
} from "./types.ts";
import { checkedScore, parseEmbedResponse, parseRerankResults, parseText, parseTokenLists, parseTokens, ResponseViolation } from "./validate.ts";

/** Longer than any honest expansion: QMD asks for 600 tokens at most. */
const MAX_EXPANSION_CHARS = 16 * 1024;
const MAX_DETOKENIZED_CHARS = 8 * 1024 * 1024;

/** A fixed text for checking a role works, and that workers embed alike. */
export const PROBE_TEXT = "task: search result | query: memoria worker identity probe";

/**
 * As long as the longest chunk the SDK makes (900 tokens, which a test pins), and more,
 * as these are short common words. A worker that refuses this is too small for real text.
 */
export const FULL_CHUNK_TEXT = "the quick brown fox ".repeat(230);

/**
 * One deadline for all of a search's model work, well under `Memoria.Api`'s 2-minute
 * client timeout, so a hung worker gives a degraded answer rather than a timeout.
 */
export const DEFAULT_SEARCH_DEADLINE_MS = 60_000;

/** A trial holds up the heartbeat it runs in, and a slow answer is no proof of health anyway. */
const TRIAL_TIMEOUT_MS = 10_000;

/** A CPU fallback may have to load a model first, but cannot be left to wait forever. */
const FALLBACK_DEADLINE_MS = 30_000;

/** What one search has spent and learned, so its rerun without reranking repeats no work. */
interface SearchContext {
  reasons: Set<DegradedReason>;
  deadline: number;
  expansions: Map<string, Queryable[]>;
  vectors: Map<string, (EmbeddingResult | null)[]>;
}

/**
 * Marks results that came through a worker, and which workers. QMD keys
 * `llm_cache` by model name, so sharing the plain name would replay cached
 * in-process scores as if a worker had produced them, and later serve worker
 * scores to in-process mode. That would also make any parity comparison look
 * perfect. Naming the workers means replacing one cannot replay what it scored.
 * The embedding model keeps its plain name on purpose: vectors are stored under
 * it, and renaming it would force a full re-embed.
 */
export const WORKER_CACHE_MARK = "#worker:";

export interface RemoteLlmOptions {
  /** One worker, named after its host. For tools and tests; the sidecar passes `workers`. */
  worker?: WorkerOptions;
  workers?: PoolWorkerConfig[];
  /** Expected embedding length with `workers`; `worker` carries its own. 0 pins to the first response. */
  embedDimensions?: number;
  models: { embed: string; rerank: string; generate: string };
  /** Null when this host has no local models and degraded mode is off. */
  fallback: LocalFallback | null;
  onCall?: LlmCallSink;
  /** Called on a failure the SDK's contract requires swallowing (it expects null, not a throw). */
  onDegraded?: (entry: { operation: string; error: string; fallback: boolean }) => void;
  timeouts?: Partial<OperationTimeouts>;
  fetch?: typeof globalThis.fetch;
  rerankTransport?: RerankTransport;
  breaker?: Partial<BreakerOptions>;
  now?: () => number;
  /** How often workers are polled. 0 disables the poll. */
  statusPollMs?: number;
  /** Length of vectors already in the index. Query vectors must match it to be searchable. */
  indexDimensions?: () => number | null;
  probeEveryMs?: number;
  onDisagreement?: (workers: readonly string[]) => void;
  searchDeadlineMs?: number;
}

function basename(uri: string): string {
  return (uri.split("/").pop() ?? uri).toLowerCase();
}

/** What a worker may report for the model: its file name, or QMD's cache name for it. Exact names only. */
export function acceptedEmbedFiles(uri: string): string[] {
  const file = basename(uri);
  const owner = /^hf:([^/]+)\//.exec(uri)?.[1]?.toLowerCase();
  return owner ? [file, `hf_${owner}_${file}`] : [file];
}

/** Leave out what was not given, since optional properties here may not be set to undefined. */
function definedOnly<T extends object>(value: T): { [K in keyof T]?: Exclude<T[K], undefined> } {
  return Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined)) as { [K in keyof T]?: Exclude<T[K], undefined> };
}

/** Sizes a worker accepts, kept well under its body limit. */
const TOKEN_BATCH_TEXTS = 256;
const TOKEN_BATCH_BYTES = 8 * 1024 * 1024;

function tokenBatches(texts: readonly string[]): string[][] {
  const batches: string[][] = [];
  let current: string[] = [];
  let bytes = 0;
  for (const text of texts) {
    const size = Buffer.byteLength(text, "utf8");
    if (current.length > 0 && (current.length >= TOKEN_BATCH_TEXTS || bytes + size > TOKEN_BATCH_BYTES)) {
      batches.push(current);
      current = [];
      bytes = 0;
    }
    current.push(text);
    bytes += size;
  }
  if (current.length > 0) batches.push(current);
  return batches;
}

function workersFrom(options: RemoteLlmOptions): PoolWorkerConfig[] {
  if (options.workers) return options.workers;
  if (!options.worker) throw new Error("RemoteLlm needs `worker` or `workers`");
  const { url, token } = options.worker;
  return [{ name: workerName(url), url, token, capabilities: ["embed", "rerank", "generate"], priority: 100, maxConcurrent: 4 }];
}

/** Only an outage justifies the CPU tokenizer. A refusal or a full queue would refuse or wait there too. */
function allowsTokenFallback(err: unknown): boolean {
  return err instanceof WorkerError && err.code !== "rejected" && err.code !== "saturated";
}

/**
 * QMD's `llm` surface over HTTP. Assigned to `store.internal.llm`, so retrieval
 * itself is untouched: the store still does FTS, sqlite-vec, fusion and caching.
 */
export class RemoteLlm implements LlmSurface {
  readonly embedModelName: string;
  readonly transportName: string;
  readonly pool: WorkerPool;
  readonly #rerankUri: string;
  readonly #generateUri: string;
  readonly #transport: RerankTransport;
  readonly #fallback: LocalFallback | null;
  readonly #onDegraded: RemoteLlmOptions["onDegraded"];
  readonly #indexDimensions: (() => number | null) | undefined;
  readonly #now: () => number;
  readonly #searchDeadlineMs: number;
  readonly #search = new AsyncLocalStorage<SearchContext>();
  readonly #primed = new Map<string, readonly number[]>();
  #noTokenBatches = false;
  #dimensions: number;

  constructor(options: RemoteLlmOptions) {
    this.embedModelName = options.models.embed;
    this.#rerankUri = options.models.rerank;
    this.#generateUri = options.models.generate;
    this.#dimensions = options.worker?.embedDimensions ?? options.embedDimensions ?? 0;
    this.#fallback = options.fallback;
    this.#onDegraded = options.onDegraded;
    this.#indexDimensions = options.indexDimensions;
    this.#now = options.now ?? Date.now;
    this.#searchDeadlineMs = options.searchDeadlineMs ?? DEFAULT_SEARCH_DEADLINE_MS;
    this.pool = new WorkerPool({
      workers: workersFrom(options),
      embedFiles: acceptedEmbedFiles(options.models.embed),
      ...definedOnly({ breaker: options.breaker, timeouts: options.timeouts, fetch: options.fetch, onCall: options.onCall, now: options.now, probeEveryMs: options.probeEveryMs, onDisagreement: options.onDisagreement }),
      interactiveBudgetMs: () => this.#remainingMs(),
      trial: (capability, target) => this.#trial(capability, target),
      probe: (target) => this.#probeVector(target),
    });
    this.#transport = options.rerankTransport ?? new WorkerRerankTransport(this.pool, () => this.#remainingMs());
    this.transportName = this.#transport.name;
    if (options.statusPollMs && options.statusPollMs > 0) this.pool.start(options.statusPollMs);
  }

  get rerankModelName(): string {
    return `${this.#rerankUri}${WORKER_CACHE_MARK}${this.pool.cacheIdentity("rerank")}`;
  }

  get generateModelName(): string {
    return `${this.#generateUri}${WORKER_CACHE_MARK}${this.pool.cacheIdentity("generate")}`;
  }

  /**
   * Run a search, collecting what went without along the way. The SDK's contract
   * has these failures swallowed, so this is how a caller still hears about them.
   */
  track<T>(run: (reasons: Set<DegradedReason>) => Promise<T>): Promise<T> {
    const context: SearchContext = { reasons: new Set(), deadline: this.#now() + this.#searchDeadlineMs, expansions: new Map(), vectors: new Map() };
    return this.#search.run(context, () => run(context.reasons));
  }

  /** Time left for the current search. Null outside one. */
  #remainingMs(): number | null {
    const context = this.#search.getStore();
    return context ? context.deadline - this.#now() : null;
  }

  canServe(capability: Capability, klass: PriorityClass = "interactive"): boolean {
    return this.pool.canServe(capability, klass);
  }

  // --- embeddings ------------------------------------------------------------

  async embed(text: string, options: EmbedOptions = {}): Promise<EmbeddingResult | null> {
    const [result] = await this.#embedMany([text], options.isQuery === true);
    return result ?? null;
  }

  /**
   * The SDK calls this bare for query vectors and with `{ model }` for document
   * chunks. Only queries may fall back to CPU: a corpus re-embed on CPU would
   * run for hours while looking healthy, so document chunks fail instead and
   * the SDK's own failure accounting retries them.
   */
  embedBatch(texts: string[], options: EmbedOptions = {}): Promise<(EmbeddingResult | null)[]> {
    return this.#embedMany(texts, options.isQuery === true || options.model === undefined);
  }

  /** One batch on one worker, for the embedding farm. Throws, so the farm can tell why. */
  embedDocuments(texts: string[]): Promise<{ vectors: number[][]; worker: string }> {
    return this.pool.dispatch("embed", "background", "embed", async (target) => ({ vectors: await this.#embedOn(target, texts, false), worker: target.name }));
  }

  async #embedOn(target: DispatchTarget, texts: string[], isQuery: boolean): Promise<number[][]> {
    const { client } = target;
    const raw = await client.post("embed", isQuery ? "embedQuery" : "embed", "/v1/embed", { texts }, texts.length, isQuery ? this.#remainingMs() : null);
    // Checked on every response, not just at startup: a worker restarted with
    // another model between heartbeats must not get vectors into the index.
    const parsed = this.#validated(client.name, "embed", () => parseEmbedResponse(raw, texts.length, this.#dimensions));
    if (!this.pool.servesIndexModel(parsed.model)) throw new WorkerError("model_mismatch", "embed", client.name, "the worker does not serve the index's embedding model");
    if (isQuery) this.#checkSearchable(client.name, parsed.vectors[0]!.length);
    // Pinned here, after the await and with no await before it, so two first
    // responses in flight cannot both set a different length.
    if (this.#dimensions === 0) this.#dimensions = parsed.vectors[0]!.length;
    return parsed.vectors;
  }

  /** sqlite-vec fails the whole search on a vector of the wrong length, so stop it here. */
  #checkSearchable(worker: string, length: number): void {
    const indexed = this.#indexDimensions?.() ?? null;
    if (indexed !== null && indexed !== length) throw new WorkerError("invalid_response", "embed", worker, "an embedding does not match the index's length");
  }

  async #embedMany(texts: string[], isQuery: boolean): Promise<(EmbeddingResult | null)[]> {
    if (texts.length === 0) return [];
    const context = isQuery ? this.#search.getStore() : undefined;
    const key = texts.join("\0");
    const known = context?.vectors.get(key);
    if (known) return known;
    const out = await this.#embedAnyhow(texts, isQuery);
    context?.vectors.set(key, out);
    return out;
  }

  async #embedAnyhow(texts: string[], isQuery: boolean): Promise<(EmbeddingResult | null)[]> {
    const klass: PriorityClass = isQuery ? "interactive" : "background";
    try {
      const vectors = await this.pool.dispatch("embed", klass, "embed", (target) => this.#embedOn(target, texts, isQuery));
      return vectors.map((embedding) => ({ embedding, model: this.embedModelName }));
    } catch (err) {
      return this.#embedWithoutWorker(texts, isQuery, err);
    }
  }

  async #embedWithoutWorker(texts: string[], isQuery: boolean, err: unknown): Promise<(EmbeddingResult | null)[]> {
    const fallback = isQuery ? this.#fallback : null;
    this.#onDegraded?.({ operation: "embed", error: messageOf(err), fallback: fallback !== null });
    if (fallback) {
      try {
        const out = this.#keepSearchable(await this.#withinDeadline(fallback.embedBatch(texts)));
        this.#note(out.every((r) => r !== null) ? "cpu_embedding" : "no_vectors");
        return out;
      } catch {
        // Falls through: a host with no model has no vectors to offer.
      }
    }
    this.#note("no_vectors");
    return texts.map(() => null);
  }

  /** A local model of another size would break the search just the same. */
  #keepSearchable(local: readonly (EmbeddingResult | null)[]): (EmbeddingResult | null)[] {
    const expected = this.#indexDimensions?.() ?? this.#dimensions;
    return local.map((r) => (r && (!expected || r.embedding.length === expected) ? { embedding: r.embedding, model: this.embedModelName } : null));
  }

  /** A fallback that outlasts its deadline keeps running, but the caller moves on. */
  #withinDeadline<T>(work: Promise<T>): Promise<T> {
    const ms = Math.min(FALLBACK_DEADLINE_MS, this.#remainingMs() ?? Infinity);
    let timer: NodeJS.Timeout;
    const expired = new Promise<never>((_, reject) => {
      timer = setTimeout(() => reject(new Error("the local fallback took too long")), Math.max(0, ms));
    });
    return Promise.race([work, expired]).finally(() => clearTimeout(timer));
  }

  /** A small real call for one role, from a heartbeat or a failed batch. Bypasses every circuit. */
  async #trial(capability: Capability, target: DispatchTarget): Promise<void> {
    if (capability === "embed") {
      await this.#probeVector(target);
      return;
    }
    const { client } = target;
    if (capability === "rerank") {
      const raw = await client.post("trial", "rerank", "/v1/rerank", { query: "memoria trial", documents: ["memoria trial"] }, 1, TRIAL_TIMEOUT_MS);
      this.#validated(client.name, "trial", () => parseRerankResults(raw, 1));
      return;
    }
    const raw = await client.post("trial", "complete", "/v1/complete", { prompt: "memoria trial", maxTokens: 1 }, 1, TRIAL_TIMEOUT_MS);
    this.#validated(client.name, "trial", () => parseText(raw, MAX_EXPANSION_CHARS));
  }

  acceptsFullChunk(): Promise<boolean> {
    return this.pool.everyEmbedderPasses((target) => this.#probeVector(target, FULL_CHUNK_TEXT));
  }

  async #probeVector(target: DispatchTarget, text = PROBE_TEXT): Promise<number[]> {
    const raw = await target.client.post("trial", "embedQuery", "/v1/embed", { texts: [text] }, 1, TRIAL_TIMEOUT_MS);
    const parsed = this.#validated(target.client.name, "trial", () => parseEmbedResponse(raw, 1, this.#dimensions));
    if (!this.pool.servesIndexModel(parsed.model)) throw new WorkerError("model_mismatch", "trial", target.client.name, "the worker does not serve the index's embedding model");
    return parsed.vectors[0]!;
  }

  // --- rerank ----------------------------------------------------------------

  /**
   * Failures throw. The SDK caches whatever this returns in `llm_cache`, so a
   * made-up score (its own local path uses 0.5 when it has no reranker) would be
   * replayed long after the worker recovered. The store catches this one level
   * up and serves unreranked results flagged as degraded.
   */
  async rerank(query: string, documents: RerankDocument[]): Promise<RerankResult> {
    if (documents.length === 0) return { results: [], model: this.rerankModelName };
    // Identical chunks are scored once, as the SDK does.
    const texts = [...new Set(documents.map((d) => d.text))];
    const scores = await this.#transport.scores(query, texts);
    const byText = new Map<string, number>();
    this.#validated(this.transportName, "rerank", () => {
      if (!Array.isArray(scores) || scores.length !== texts.length) throw new ResponseViolation("result count does not match the request");
      texts.forEach((text, i) => byText.set(text, checkedScore(scores[i])));
    });
    const results = documents
      .map((doc, index) => ({ file: doc.file, score: byText.get(doc.text)!, index }))
      .sort((a, b) => b.score - a.score);
    return { results, model: this.rerankModelName };
  }

  // --- expansion -------------------------------------------------------------

  /**
   * Useless output counts as a failure. Whatever this returns beyond the query
   * itself gets cached, so a placeholder would replay at full strength as if the
   * model had produced it. A failure returns just the query, which is never cached.
   */
  async expandQuery(query: string, options: ExpandOptions = {}): Promise<Queryable[]> {
    const includeLexical = options.includeLexical ?? true;
    const context = this.#search.getStore();
    const key = `${includeLexical}:${query}`;
    const known = context?.expansions.get(key);
    if (known) return known;
    const out = await this.#expand(query, includeLexical);
    context?.expansions.set(key, out);
    return out;
  }

  async #expand(query: string, includeLexical: boolean): Promise<Queryable[]> {
    try {
      const output = await this.pool.dispatch("generate", "interactive", "complete", async ({ client }) => {
        const raw = await client.post("complete", "complete", "/v1/complete", { prompt: expansionPrompt(query), system: EXPANSION_SYSTEM_PROMPT, grammar: EXPANSION_GRAMMAR, ...EXPANSION_SAMPLING }, 1, this.#remainingMs());
        return this.#validated(client.name, "complete", () => parseText(raw, MAX_EXPANSION_CHARS));
      });
      const expanded = parseExpansion(query, output, includeLexical);
      if (expanded.length > 0) return expanded;
      return this.#expansionFailed(query, includeLexical, "the worker's expansion had nothing usable");
    } catch (err) {
      return this.#expansionFailed(query, includeLexical, messageOf(err));
    }
  }

  #expansionFailed(query: string, includeLexical: boolean, error: string): Queryable[] {
    this.#onDegraded?.({ operation: "complete", error, fallback: true });
    this.#note("no_expansion");
    return expansionFailure(query, includeLexical);
  }

  // --- tokens ----------------------------------------------------------------

  /**
   * The SDK's chunker asks for tokens one text at a time, and on a real corpus those
   * round trips took most of the time a re-embed was not embedding. It asks about
   * known texts first, so these are fetched together and its calls answered from
   * here. This only saves time: any failure leaves the texts to be asked for singly.
   */
  async primeTokens(texts: readonly string[]): Promise<void> {
    if (this.#noTokenBatches) return;
    for (const batch of tokenBatches([...new Set(texts)])) {
      try {
        await this.pool.dispatch("embed", "background", "tokenize", async ({ client }) => {
          const raw = await client.post("tokenize", "tokenize", "/v1/tokenize-batch", { texts: batch }, batch.length);
          const lists = this.#validated(client.name, "tokenize", () => parseTokenLists(raw, batch));
          batch.forEach((text, i) => this.#primed.set(text, lists[i]!));
        });
      } catch (err) {
        // A worker from before this route: asked once, so its circuit is not at risk.
        if (err instanceof WorkerError && err.status === 404) this.#noTokenBatches = true;
        return;
      }
    }
  }

  forgetPrimedTokens(): void {
    this.#primed.clear();
  }

  /** Tokens are the embedding model's, so these route like embedding, including the model check. */
  async tokenize(text: string): Promise<readonly number[]> {
    const primed = this.#primed.get(text);
    if (primed) return primed;
    try {
      return await this.pool.dispatch("embed", "background", "tokenize", async ({ client }) => {
        const raw = await client.post("tokenize", "tokenize", "/v1/tokenize", { text }, 1);
        return this.#validated(client.name, "tokenize", () => parseTokens(raw, text));
      });
    } catch (err) {
      return this.#tokenFallback("tokenize", err, (f) => f.tokenize(text));
    }
  }

  async detokenize(tokens: readonly number[]): Promise<string> {
    try {
      return await this.pool.dispatch("embed", "background", "detokenize", async ({ client }) => {
        const raw = await client.post("detokenize", "tokenize", "/v1/detokenize", { tokens }, tokens.length);
        return this.#validated(client.name, "detokenize", () => parseText(raw, MAX_DETOKENIZED_CHARS));
      });
    } catch (err) {
      return this.#tokenFallback("detokenize", err, (f) => f.detokenize(tokens));
    }
  }

  /** A host with no model fails as an outage, so a job waits for a worker instead of failing. */
  async #tokenFallback<T>(operation: string, err: unknown, run: (fallback: LocalFallback) => Promise<T>): Promise<T> {
    const fallback = allowsTokenFallback(err) ? this.#fallback : null;
    this.#onDegraded?.({ operation, error: messageOf(err), fallback: fallback !== null });
    if (!fallback) throw err;
    try {
      return await this.#withinDeadline(run(fallback));
    } catch {
      throw new WorkerError("no_worker", operation, "local", "no worker answered and the local fallback is unavailable");
    }
  }

  // --- status ----------------------------------------------------------------

  /** Roles some worker can serve now, by the last heartbeat, for `/ready`, which is synchronous. */
  status(): Record<Capability, boolean> {
    return this.pool.status();
  }

  async refreshStatus(): Promise<Record<Capability, boolean>> {
    await this.pool.refresh();
    return this.pool.status();
  }

  async dispose(): Promise<void> {
    this.pool.stop();
  }

  #note(reason: DegradedReason): void {
    this.#search.getStore()?.reasons.add(reason);
  }

  #validated<T>(worker: string, operation: string, parse: () => T): T {
    try {
      return parse();
    } catch (err) {
      if (err instanceof ResponseViolation) throw new WorkerError("invalid_response", operation, worker, err.message);
      throw err;
    }
  }
}

/** Only our own errors carry a message worth repeating; they are built to be safe. */
function messageOf(err: unknown): string {
  return err instanceof WorkerError ? err.message : "unexpected local error";
}
