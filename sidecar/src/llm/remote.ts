import { WorkerClient, WorkerError, type OperationTimeouts } from "./client.ts";
import { EXPANSION_GRAMMAR, EXPANSION_SAMPLING, expansionFailure, expansionPrompt, parseExpansion } from "./expansion.ts";
import { WorkerRerankTransport, type RerankTransport } from "./rerank-transport.ts";
import type {
  EmbedOptions,
  EmbeddingResult,
  ExpandOptions,
  LlmCallSink,
  LlmSurface,
  LocalFallback,
  Queryable,
  RerankDocument,
  RerankResult,
  WorkerOptions,
} from "./types.ts";
import { checkedScore, parseCapabilities, parseEmbeddings, parseHealthModels, parseText, parseTokens, ResponseViolation, type WorkerHealthModels } from "./validate.ts";

/** Longest expansion the grammar's 600-token budget could plausibly produce. */
const MAX_EXPANSION_CHARS = 64 * 1024;
const MAX_DETOKENIZED_CHARS = 8 * 1024 * 1024;

/**
 * Marks results that came through a worker. QMD keys `llm_cache` by model name,
 * so sharing the plain name would replay cached in-process scores as if a worker
 * had produced them, and later serve worker scores to in-process mode. That
 * would also make any parity comparison look perfect. The embedding model keeps
 * its plain name on purpose: vectors are stored under it, and renaming it would
 * force a full re-embed.
 */
export const WORKER_CACHE_SUFFIX = "#worker";

export interface RemoteLlmOptions {
  worker: WorkerOptions;
  models: { embed: string; rerank: string; generate: string };
  /** Null when this host has no local models and degraded mode is off. */
  fallback: LocalFallback | null;
  onCall?: LlmCallSink;
  /** Called on a failure the SDK's contract requires swallowing (it expects null, not a throw). */
  onDegraded?: (entry: { operation: string; error: string; fallback: boolean }) => void;
  timeouts?: Partial<OperationTimeouts>;
  fetch?: typeof globalThis.fetch;
  rerankTransport?: RerankTransport;
  /** How often `status()` is refreshed. 0 disables the poll. */
  statusPollMs?: number;
}

function basename(uri: string): string {
  return (uri.split("/").pop() ?? uri).toLowerCase();
}

/**
 * QMD's `llm` surface over HTTP. Assigned to `store.internal.llm`, so retrieval
 * itself is untouched: the store still does FTS, sqlite-vec, fusion and caching.
 */
export class RemoteLlm implements LlmSurface {
  readonly embedModelName: string;
  readonly rerankModelName: string;
  readonly generateModelName: string;
  readonly transportName: string;
  readonly #client: WorkerClient;
  readonly #transport: RerankTransport;
  readonly #fallback: LocalFallback | null;
  readonly #onDegraded: RemoteLlmOptions["onDegraded"];
  readonly #embedFile: string;
  #dimensions: number;
  #embedModelChecked = false;
  #health: WorkerHealthModels = { embed: false, rerank: false, generate: false };
  #poll: NodeJS.Timeout | undefined;

  constructor(options: RemoteLlmOptions) {
    this.embedModelName = options.models.embed;
    this.rerankModelName = options.models.rerank + WORKER_CACHE_SUFFIX;
    this.generateModelName = options.models.generate + WORKER_CACHE_SUFFIX;
    this.#embedFile = basename(options.models.embed);
    this.#dimensions = options.worker.embedDimensions;
    this.#fallback = options.fallback;
    this.#onDegraded = options.onDegraded;
    this.#client = new WorkerClient({
      url: options.worker.url,
      token: options.worker.token,
      ...(options.timeouts ? { timeouts: options.timeouts } : {}),
      ...(options.fetch ? { fetch: options.fetch } : {}),
      ...(options.onCall ? { onCall: options.onCall } : {}),
    });
    this.#transport = options.rerankTransport ?? new WorkerRerankTransport(this.#client);
    this.transportName = this.#transport.name;
    if (options.statusPollMs && options.statusPollMs > 0) {
      void this.refreshStatus();
      this.#poll = setInterval(() => void this.refreshStatus(), options.statusPollMs);
      this.#poll.unref();
    }
  }

  get workerName(): string {
    return this.#client.name;
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

  async #embedMany(texts: string[], isQuery: boolean): Promise<(EmbeddingResult | null)[]> {
    if (texts.length === 0) return [];
    try {
      await this.#checkEmbedModel();
      const raw = await this.#client.post("embed", "embed", "/v1/embed", { texts }, texts.length);
      const vectors = this.#validated("embed", () => parseEmbeddings(raw, texts.length, this.#dimensions));
      if (this.#dimensions === 0) this.#dimensions = vectors[0]!.length;
      return vectors.map((embedding) => ({ embedding, model: this.embedModelName }));
    } catch (err) {
      const fallback = isQuery ? this.#fallback : null;
      this.#onDegraded?.({ operation: "embed", error: messageOf(err), fallback: fallback !== null });
      if (!fallback) return texts.map(() => null);
      try {
        const local = await fallback.embedBatch(texts);
        // A local model of another size would poison a pinned index just the same.
        return local.map((r) => (r && (this.#dimensions === 0 || r.embedding.length === this.#dimensions) ? { embedding: r.embedding, model: this.embedModelName } : null));
      } catch {
        return texts.map(() => null);
      }
    }
  }

  /**
   * Vectors are stored under the plain embed model name, so a worker serving a
   * different model would silently mix two vector spaces in one index. Checked
   * once per process; a failure is retried on the next call.
   */
  async #checkEmbedModel(): Promise<void> {
    if (this.#embedModelChecked) return;
    const raw = await this.#client.get("capabilities", "/capabilities");
    const capabilities = this.#validated("capabilities", () => parseCapabilities(raw));
    // QMD's cache prefixes file names (`hf_<owner>_<file>`), so compare the tail.
    const served = capabilities.models.embed?.toLowerCase() ?? "";
    if (!served.endsWith(this.#embedFile)) {
      throw new WorkerError("model_mismatch", "embed", this.#client.name, "the worker does not serve the index's embedding model");
    }
    this.#embedModelChecked = true;
  }

  // --- rerank ----------------------------------------------------------------

  /**
   * Failures throw. The SDK caches whatever this returns in `llm_cache`, so a
   * made-up score (its own local path uses 0.5 when it has no reranker) would be
   * replayed long after the worker recovered. A failed reranked search is an
   * error the caller can see and retry, or avoid with `rerank: false`.
   */
  async rerank(query: string, documents: RerankDocument[]): Promise<RerankResult> {
    if (documents.length === 0) return { results: [], model: this.rerankModelName };
    // Identical chunks are scored once, as the SDK does.
    const texts = [...new Set(documents.map((d) => d.text))];
    const scores = await this.#transport.scores(query, texts);
    const byText = new Map<string, number>();
    this.#validated("rerank", () => {
      if (!Array.isArray(scores) || scores.length !== texts.length) throw new ResponseViolation("result count does not match the request");
      texts.forEach((text, i) => byText.set(text, checkedScore(scores[i])));
    });
    const results = documents
      .map((doc, index) => ({ file: doc.file, score: byText.get(doc.text)!, index }))
      .sort((a, b) => b.score - a.score);
    return { results, model: this.rerankModelName };
  }

  // --- expansion -------------------------------------------------------------

  /** Failures fall back to the plain query, exactly as the in-process path does. */
  async expandQuery(query: string, options: ExpandOptions = {}): Promise<Queryable[]> {
    const includeLexical = options.includeLexical ?? true;
    try {
      const raw = await this.#client.post("complete", "complete", "/v1/complete", { prompt: expansionPrompt(query), grammar: EXPANSION_GRAMMAR, ...EXPANSION_SAMPLING }, 1);
      const output = this.#validated("complete", () => parseText(raw, MAX_EXPANSION_CHARS));
      return parseExpansion(query, output, includeLexical);
    } catch (err) {
      this.#onDegraded?.({ operation: "complete", error: messageOf(err), fallback: true });
      return expansionFailure(query, includeLexical);
    }
  }

  // --- tokens ----------------------------------------------------------------

  async tokenize(text: string): Promise<readonly number[]> {
    try {
      const raw = await this.#client.post("tokenize", "tokenize", "/v1/tokenize", { text }, 1);
      return this.#validated("tokenize", () => parseTokens(raw, text));
    } catch (err) {
      return this.#tokenFallback("tokenize", err, (f) => f.tokenize(text));
    }
  }

  async detokenize(tokens: readonly number[]): Promise<string> {
    try {
      const raw = await this.#client.post("detokenize", "tokenize", "/v1/detokenize", { tokens }, tokens.length);
      return this.#validated("detokenize", () => parseText(raw, MAX_DETOKENIZED_CHARS));
    } catch (err) {
      return this.#tokenFallback("detokenize", err, (f) => f.detokenize(tokens));
    }
  }

  #tokenFallback<T>(operation: string, err: unknown, run: (fallback: LocalFallback) => Promise<T>): Promise<T> {
    this.#onDegraded?.({ operation, error: messageOf(err), fallback: this.#fallback !== null });
    if (!this.#fallback) throw err;
    return run(this.#fallback);
  }

  // --- status ----------------------------------------------------------------

  /** Last known per-role state, for `/ready`, which is synchronous. */
  status(): WorkerHealthModels {
    return this.#health;
  }

  async refreshStatus(): Promise<WorkerHealthModels> {
    try {
      const raw = await this.#client.get("health", "/health");
      this.#health = this.#validated("health", () => parseHealthModels(raw));
    } catch {
      this.#health = { embed: false, rerank: false, generate: false };
    }
    return this.#health;
  }

  async dispose(): Promise<void> {
    if (this.#poll) clearInterval(this.#poll);
    this.#poll = undefined;
  }

  #validated<T>(operation: string, parse: () => T): T {
    try {
      return parse();
    } catch (err) {
      if (err instanceof ResponseViolation) throw new WorkerError("invalid_response", operation, this.#client.name, err.message);
      throw err;
    }
  }
}

/** Only our own errors carry a message worth repeating; they are built to be safe. */
function messageOf(err: unknown): string {
  return err instanceof WorkerError ? err.message : "unexpected local error";
}
