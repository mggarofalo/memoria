import { CONTEXT_SIZE, type Role } from "./config.ts";

/**
 * The worker's operations, implemented against llama-server's HTTP API. The
 * truncation rules copy QMD's in-process path, because llama-server rejects
 * input past its context size where QMD would have shortened it.
 *
 * `RERANK_BACKEND` names what scores documents. Swapping it means implementing
 * `RerankBackend` and changing nothing else here or in the server.
 */

/** QMD's budget for the reranker's prompt template. */
const RERANK_TEMPLATE_OVERHEAD = 512;
/** QMD's margin for BOS/EOS when shortening text to fit the embed context. */
const EMBED_TOKEN_MARGIN = 4;
const TOKENIZE_CONCURRENCY = 8;

const REJECTED_INPUT: ReadonlySet<number> = new Set([400, 413, 422]);

export class UpstreamError extends Error {
  readonly code: string;
  constructor(code: string, message: string) {
    super(message);
    this.name = "UpstreamError";
    this.code = code;
  }
}

export class InvalidRequest extends Error {
  readonly code: string;
  constructor(code: string, message: string) {
    super(message);
    this.name = "InvalidRequest";
    this.code = code;
  }
}

export interface CompleteRequest {
  prompt: string;
  grammar?: string;
  maxTokens?: number;
  temperature?: number;
  topK?: number;
  topP?: number;
  repeatLastN?: number;
  presencePenalty?: number;
}

export interface RerankBackend {
  readonly name: string;
  score(query: string, documents: string[]): Promise<number[]>;
}

export interface LlamaRoutes {
  baseUrlFor(role: Role): string | undefined;
}

export class LlamaServers {
  readonly #routes: LlamaRoutes;
  readonly #fetch: typeof globalThis.fetch;
  readonly #timeoutMs: number;

  constructor(routes: LlamaRoutes, options: { fetch?: typeof globalThis.fetch; timeoutMs: number }) {
    this.#routes = routes;
    this.#fetch = options.fetch ?? globalThis.fetch;
    this.#timeoutMs = options.timeoutMs;
  }

  async post(role: Role, path: string, body: unknown): Promise<unknown> {
    const base = this.#routes.baseUrlFor(role);
    if (base === undefined) throw new UpstreamError("not_served", `This worker does not serve ${role}`);
    let response: Response;
    try {
      response = await this.#fetch(new URL(path, base), {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body),
        signal: AbortSignal.timeout(this.#timeoutMs),
      });
    } catch {
      throw new UpstreamError("upstream_unavailable", `llama-server for ${role} did not answer`);
    }
    const text = await response.text();
    // llama-server saying the input is bad is a refusal of this text, not a broken server.
    if (REJECTED_INPUT.has(response.status)) throw new InvalidRequest("upstream_rejected", `llama-server for ${role} rejected the input with HTTP ${response.status}`);
    if (!response.ok) throw new UpstreamError("upstream_error", `llama-server for ${role} returned HTTP ${response.status}`);
    try {
      return JSON.parse(text);
    } catch {
      throw new UpstreamError("upstream_invalid", `llama-server for ${role} returned something other than JSON`);
    }
  }

  /** `parse_special: false` matches QMD: text that looks like a control token stays text. */
  async tokenize(role: Role, text: string): Promise<number[]> {
    const raw = (await this.post(role, "tokenize", { content: text, add_special: false, parse_special: false })) as { tokens?: unknown };
    if (!Array.isArray(raw.tokens) || !raw.tokens.every((t) => Number.isSafeInteger(t))) {
      throw new UpstreamError("upstream_invalid", "llama-server returned malformed tokens");
    }
    return raw.tokens as number[];
  }

  async detokenize(role: Role, tokens: number[]): Promise<string> {
    const raw = (await this.post(role, "detokenize", { tokens })) as { content?: unknown };
    if (typeof raw.content !== "string") throw new UpstreamError("upstream_invalid", "llama-server returned malformed text");
    return raw.content;
  }
}

async function mapLimited<T, R>(items: readonly T[], limit: number, run: (item: T) => Promise<R>): Promise<R[]> {
  const out = new Array<R>(items.length);
  let next = 0;
  const lane = async (): Promise<void> => {
    while (next < items.length) {
      const i = next++;
      out[i] = await run(items[i]!);
    }
  };
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, lane));
  return out;
}

/**
 * Scores with llama-server's `/rerank`. The server formats each pair with the
 * model's rerank template and returns the pooled rank output. Qwen3's is a
 * softmax, so it should already be a probability; nothing is rescaled here, and
 * anything outside 0 to 1 is an error rather than a number to guess about.
 */
export class LlamaServerRerank implements RerankBackend {
  readonly name = "llama-server-rerank";
  readonly #servers: LlamaServers;

  constructor(servers: LlamaServers) {
    this.#servers = servers;
  }

  async score(query: string, documents: string[]): Promise<number[]> {
    const raw = (await this.#servers.post("rerank", "rerank", { query, documents })) as { results?: unknown };
    if (!Array.isArray(raw.results) || raw.results.length !== documents.length) {
      throw new UpstreamError("upstream_invalid", "llama-server returned the wrong number of rerank results");
    }
    const scores = new Array<number | undefined>(documents.length).fill(undefined);
    for (const item of raw.results as Array<{ index?: unknown; relevance_score?: unknown }>) {
      const { index, relevance_score: score } = item;
      if (typeof index !== "number" || !Number.isInteger(index) || index < 0 || index >= documents.length || scores[index] !== undefined) {
        throw new UpstreamError("upstream_invalid", "llama-server returned a bad rerank index");
      }
      if (typeof score !== "number" || !Number.isFinite(score) || score < 0 || score > 1) {
        throw new UpstreamError("rerank_score_out_of_range", "llama-server returned a rerank score outside 0 to 1");
      }
      scores[index] = score;
    }
    return scores as number[];
  }
}

export class Inference {
  readonly #servers: LlamaServers;
  readonly #rerank: RerankBackend;

  constructor(servers: LlamaServers, rerank: RerankBackend = new LlamaServerRerank(servers)) {
    this.#servers = servers;
    this.#rerank = rerank;
  }

  get rerankBackend(): string {
    return this.#rerank.name;
  }

  async embed(texts: string[]): Promise<number[][]> {
    const fitted = await mapLimited(texts, TOKENIZE_CONCURRENCY, (text) => this.#fitEmbedText(text));
    const raw = (await this.#servers.post("embed", "v1/embeddings", { input: fitted })) as { data?: unknown };
    if (!Array.isArray(raw.data) || raw.data.length !== texts.length) {
      throw new UpstreamError("upstream_invalid", "llama-server returned the wrong number of embeddings");
    }
    const vectors = new Array<number[] | undefined>(texts.length).fill(undefined);
    for (const item of raw.data as Array<{ index?: unknown; embedding?: unknown }>) {
      const { index, embedding } = item;
      if (typeof index !== "number" || !Number.isInteger(index) || index < 0 || index >= texts.length || !Array.isArray(embedding)) {
        throw new UpstreamError("upstream_invalid", "llama-server returned a malformed embedding");
      }
      vectors[index] = embedding as number[];
    }
    if (vectors.some((v) => v === undefined)) throw new UpstreamError("upstream_invalid", "llama-server skipped an embedding");
    return vectors as number[][];
  }

  async #fitEmbedText(text: string): Promise<string> {
    const limit = CONTEXT_SIZE.embed;
    const tokens = await this.#servers.tokenize("embed", text);
    if (tokens.length <= limit) return text;
    return this.#servers.detokenize("embed", tokens.slice(0, Math.max(1, limit - EMBED_TOKEN_MARGIN)));
  }

  /** QMD's budget: context size, less template overhead, less the query. */
  async rerank(query: string, documents: string[]): Promise<number[]> {
    const queryTokens = await this.#servers.tokenize("rerank", query);
    const budget = CONTEXT_SIZE.rerank - RERANK_TEMPLATE_OVERHEAD - queryTokens.length;
    if (budget <= 0) throw new InvalidRequest("query_too_long", "The query leaves no room for documents in the reranker's context");
    const fitted = await mapLimited(documents, TOKENIZE_CONCURRENCY, async (text) => {
      const tokens = await this.#servers.tokenize("rerank", text);
      return tokens.length <= budget ? text : this.#servers.detokenize("rerank", tokens.slice(0, budget));
    });
    return this.#rerank.score(query, fitted);
  }

  /**
   * Generation goes through the chat route so the server applies the model's
   * chat template, as QMD's `LlamaChatSession` does with its own.
   */
  async complete(request: CompleteRequest): Promise<string> {
    const raw = (await this.#servers.post("generate", "v1/chat/completions", {
      messages: [{ role: "user", content: request.prompt }],
      stream: false,
      ...(request.grammar !== undefined ? { grammar: request.grammar } : {}),
      ...(request.maxTokens !== undefined ? { max_tokens: request.maxTokens } : {}),
      ...(request.temperature !== undefined ? { temperature: request.temperature } : {}),
      ...(request.topK !== undefined ? { top_k: request.topK } : {}),
      ...(request.topP !== undefined ? { top_p: request.topP } : {}),
      ...(request.repeatLastN !== undefined ? { repeat_last_n: request.repeatLastN } : {}),
      ...(request.presencePenalty !== undefined ? { presence_penalty: request.presencePenalty } : {}),
    })) as { choices?: Array<{ message?: { content?: unknown } }> };
    const content = raw.choices?.[0]?.message?.content;
    if (typeof content !== "string") throw new UpstreamError("upstream_invalid", "llama-server returned no completion text");
    return content;
  }

  /** The tokenizer that matters for chunking is the embedding model's, as in QMD. */
  tokenize(text: string): Promise<number[]> {
    return this.#servers.tokenize("embed", text);
  }

  detokenize(tokens: number[]): Promise<string> {
    return this.#servers.detokenize("embed", tokens);
  }
}
