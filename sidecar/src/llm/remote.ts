import { AsyncLocalStorage } from "node:async_hooks";
import { WorkerError, type OperationTimeouts, workerName } from "./client.ts";
import { EXPANSION_GRAMMAR, EXPANSION_SAMPLING, expansionFailure, expansionPrompt, parseExpansion } from "./expansion.ts";
import { WorkerPool, type BreakerOptions, type DispatchTarget } from "./pool.ts";
import { cosineOf } from "./probe.ts";
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
  ProbeReference,
  Queryable,
  RerankDocument,
  RerankResult,
  WorkerOptions,
} from "./types.ts";
import { checkedScore, parseEmbedResponse, parseText, parseTokens, ResponseViolation } from "./validate.ts";

/** Longer than any honest expansion: QMD asks for 600 tokens at most. */
const MAX_EXPANSION_CHARS = 16 * 1024;
const MAX_DETOKENIZED_CHARS = 8 * 1024 * 1024;

/** A fixed text every worker should embed alike, to catch one serving something else. */
export const PROBE_TEXT = "task: search result | query: memoria worker identity probe";
/** Below this, it is a different model and not rounding between CPU and GPU. */
export const PROBE_MIN_COSINE = 0.995;

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
  /** Where the probe's reference answer lives. Enables probing. */
  probeReference?: { get(): ProbeReference | null; set(reference: ProbeReference): void };
  probeEveryMs?: number;
}

function basename(uri: string): string {
  return (uri.split("/").pop() ?? uri).toLowerCase();
}

/** The model's file name, and QMD's cache name for it (`hf_<owner>_<file>`). Exact names only. */
export function acceptedEmbedFiles(uri: string): string[] {
  const file = basename(uri);
  const owner = /^hf:([^/]+)\//.exec(uri)?.[1]?.toLowerCase();
  return owner ? [file, `hf_${owner}_${file}`] : [file];
}

/** Leave out what was not given, since optional properties here may not be set to undefined. */
function definedOnly<T extends object>(value: T): { [K in keyof T]?: Exclude<T[K], undefined> } {
  return Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined)) as { [K in keyof T]?: Exclude<T[K], undefined> };
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
  readonly #probeReference: RemoteLlmOptions["probeReference"];
  readonly #degradation = new AsyncLocalStorage<Set<DegradedReason>>();
  #dimensions: number;

  constructor(options: RemoteLlmOptions) {
    this.embedModelName = options.models.embed;
    this.#rerankUri = options.models.rerank;
    this.#generateUri = options.models.generate;
    this.#dimensions = options.worker?.embedDimensions ?? options.embedDimensions ?? 0;
    this.#fallback = options.fallback;
    this.#onDegraded = options.onDegraded;
    this.#indexDimensions = options.indexDimensions;
    this.#probeReference = options.probeReference;
    const probe = options.probeReference && ((target: DispatchTarget) => this.#checkProbe(target));
    this.pool = new WorkerPool({
      workers: workersFrom(options),
      embedFiles: acceptedEmbedFiles(options.models.embed),
      ...definedOnly({ breaker: options.breaker, timeouts: options.timeouts, fetch: options.fetch, onCall: options.onCall, now: options.now, probe, probeEveryMs: options.probeEveryMs }),
    });
    this.#transport = options.rerankTransport ?? new WorkerRerankTransport(this.pool);
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
    const reasons = new Set<DegradedReason>();
    return this.#degradation.run(reasons, () => run(reasons));
  }

  canServe(capability: Capability): boolean {
    return this.pool.canServe(capability);
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
    const raw = await client.post("embed", isQuery ? "embedQuery" : "embed", "/v1/embed", { texts }, texts.length);
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
        const out = this.#keepSearchable(await fallback.embedBatch(texts));
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

  /**
   * Trust the first answer as the reference, and hold every later one to it. A
   * worker that lies from the very start can only be found by comparing it with
   * others, which is why `GET /workers` shows the verdict and purging that worker
   * clears the reference.
   */
  async #checkProbe(target: DispatchTarget): Promise<boolean> {
    const raw = await target.client.post("probe", "embedQuery", "/v1/embed", { texts: [PROBE_TEXT] }, 1);
    const parsed = this.#validated(target.client.name, "probe", () => parseEmbedResponse(raw, 1, this.#dimensions));
    const vector = parsed.vectors[0]!;
    const reference = this.#probeReference!.get();
    if (!reference) {
      this.#probeReference!.set({ worker: target.name, vector });
      return true;
    }
    return cosineOf(reference.vector, vector) >= PROBE_MIN_COSINE;
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
    try {
      const output = await this.pool.dispatch("generate", "interactive", "complete", async ({ client }) => {
        const raw = await client.post("complete", "complete", "/v1/complete", { prompt: expansionPrompt(query), grammar: EXPANSION_GRAMMAR, ...EXPANSION_SAMPLING }, 1);
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

  /** Tokens are the embedding model's, so these route like embedding, including the model check. */
  async tokenize(text: string): Promise<readonly number[]> {
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
      return await run(fallback);
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
    this.#degradation.getStore()?.add(reason);
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
