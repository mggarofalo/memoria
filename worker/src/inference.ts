import { CONTEXT_SIZE, type Role } from "./config.ts";

/**
 * The worker's operations, implemented against llama-server's HTTP API. The
 * truncation rules copy QMD's in-process path, because llama-server rejects
 * input past its context size where QMD would have shortened it.
 *
 * `RerankBackend` is what scores documents. Swapping it means implementing that
 * interface and changing nothing else here or in the server.
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
  /** System message. Without one, llama-server's template renders none. */
  system?: string;
  grammar?: string;
  maxTokens?: number;
  temperature?: number;
  topK?: number;
  topP?: number;
  minP?: number;
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

  /** `parseSpecial` stays false for user text, as in QMD: what looks like a control token stays text. */
  async tokenize(role: Role, text: string, parseSpecial = false, addSpecial = false): Promise<number[]> {
    const raw = (await this.post(role, "tokenize", { content: text, add_special: addSpecial, parse_special: parseSpecial })) as { tokens?: unknown };
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
 * The rerank template in `qwen3-reranker-0.6b-q8_0.gguf`'s
 * `tokenizer.chat_template.rerank` metadata. This backend is specific to that
 * model, which is the one QMD ships.
 */
export const QWEN3_RERANK_TEMPLATE = {
  head: '<|im_start|>system\nJudge whether the Document meets the requirements based on the Query and the Instruct provided. Note that the answer can only be "yes" or "no".<|im_end|>\n<|im_start|>user\n<Instruct>: Given a web search query, retrieve relevant passages that answer the query\n<Query>: ',
  middle: "\n<Document>: ",
  tail: "<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n",
} as const;

/** Enough candidates that "yes" and "no" nearly always both appear. */
const RERANK_N_PROBS = 200;

/**
 * Scores the tokens in-process QMD scores. node-llama-cpp tokenizes the template's
 * pieces, the query and the document separately and joins the tokens; llama-server's
 * `/rerank` tokenizes one formatted string, which on real data moved scores by up
 * to 0.19, and it parses control tokens in document text. Neither happens here.
 *
 * `/rerank` takes no token arrays, and `/embeddings` on a rank-pooled server does
 * but crashed the server on real data (docs/workers.md). So this asks `/completion`
 * on an ordinary server for the probabilities of "yes" and "no" after the prompt.
 * Their ratio is the softmax rank pooling computes.
 */
export class QmdTokensRerank implements RerankBackend {
  readonly name = "llama-server-qmd-tokens";
  readonly #servers: LlamaServers;
  #vocabulary: Promise<{ head: number[]; middle: number[]; tail: number[]; yes: number; no: number }> | undefined;

  constructor(servers: LlamaServers) {
    this.#servers = servers;
  }

  #fixedTokens() {
    const tokenize = (text: string, special: boolean) => this.#servers.tokenize("rerank", text, special);
    this.#vocabulary ??= Promise.all([tokenize(QWEN3_RERANK_TEMPLATE.head, true), tokenize(QWEN3_RERANK_TEMPLATE.middle, true), tokenize(QWEN3_RERANK_TEMPLATE.tail, true), tokenize("yes", false), tokenize("no", false)])
      .then(([head, middle, tail, yes, no]) => {
        if (yes[0] === undefined || no[0] === undefined) throw new UpstreamError("upstream_invalid", "llama-server returned malformed tokens");
        return { head, middle, tail, yes: yes[0], no: no[0] };
      })
      .catch((err: unknown) => {
        this.#vocabulary = undefined; // a server still loading must not poison later calls
        throw err;
      });
    return this.#vocabulary;
  }

  async score(query: string, documents: string[]): Promise<number[]> {
    const { head, middle, tail, yes, no } = await this.#fixedTokens();
    const queryTokens = await this.#servers.tokenize("rerank", query);
    const documentTokens = await mapLimited(documents, TOKENIZE_CONCURRENCY, (text) => this.#servers.tokenize("rerank", text));
    const prompt = documentTokens.map((tokens) => [...head, ...queryTokens, ...middle, ...tokens, ...tail]);
    const raw = await this.#servers.post("rerank", "completion", {
      prompt,
      n_predict: 1,
      n_probs: RERANK_N_PROBS,
      post_sampling_probs: true,
      grammar: 'root ::= "yes" | "no"',
      temperature: 1,
      top_k: 0,
      top_p: 1,
      min_p: 0,
      repeat_last_n: 0,
      cache_prompt: false,
      stream: false,
    });
    const items = Array.isArray(raw) ? raw : [raw];
    if (items.length !== documents.length) throw new UpstreamError("upstream_invalid", "llama-server returned the wrong number of rerank results");
    const scores = new Array<number | undefined>(documents.length).fill(undefined);
    items.forEach((item: { index?: unknown; completion_probabilities?: Array<{ top_probs?: Array<{ id?: unknown; prob?: unknown }> }> }, position) => {
      const index = item.index ?? position;
      if (typeof index !== "number" || !Number.isInteger(index) || index < 0 || index >= documents.length || scores[index] !== undefined) {
        throw new UpstreamError("upstream_invalid", "llama-server returned a bad rerank index");
      }
      const top = item.completion_probabilities?.[0]?.top_probs;
      if (!Array.isArray(top) || top.length === 0) throw new UpstreamError("upstream_invalid", "llama-server returned no probabilities");
      scores[index] = yesShare(top, yes, no);
    });
    return scores as number[];
  }
}

/**
 * P(yes) over P(yes) + P(no). A token outside the list is below the lowest listed
 * probability, which is used as its bound, so such a document scores near an end
 * of the range; only the order among documents all that far down is lost.
 */
function yesShare(top: Array<{ id?: unknown; prob?: unknown }>, yes: number, no: number): number {
  const probs = top.map((t) => t.prob);
  if (!probs.every((p): p is number => typeof p === "number" && Number.isFinite(p) && p >= 0 && p <= 1)) {
    throw new UpstreamError("rerank_score_out_of_range", "llama-server returned a probability outside 0 to 1");
  }
  const find = (id: number): number | undefined => {
    const at = top.findIndex((t) => t.id === id);
    return at < 0 ? undefined : probs[at];
  };
  const y = find(yes);
  const n = find(no);
  if (y === undefined && n === undefined) throw new UpstreamError("upstream_invalid", "llama-server listed neither yes nor no");
  const floor = Math.min(...probs);
  const a = y ?? floor;
  const b = n ?? floor;
  return a + b === 0 ? 0 : a / (a + b);
}

export class Inference {
  readonly #servers: LlamaServers;
  readonly #rerank: RerankBackend;

  constructor(servers: LlamaServers, rerank: RerankBackend = new QmdTokensRerank(servers)) {
    this.#servers = servers;
    this.#rerank = rerank;
  }

  get rerankBackend(): string {
    return this.#rerank.name;
  }

  async embed(texts: string[]): Promise<number[][]> {
    const input = await mapLimited(texts, TOKENIZE_CONCURRENCY, (text) => this.#embedTokens(text));
    const raw = (await this.#servers.post("embed", "v1/embeddings", { input })) as { data?: unknown };
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

  /**
   * Tokens rather than a string, because the route parses control tokens found
   * in a string, so a document could change its own vector. In-process QMD keeps
   * them as text, and adds the model's BOS and EOS, as this does.
   */
  async #embedTokens(text: string): Promise<number[]> {
    const limit = CONTEXT_SIZE.embed;
    const tokens = await this.#servers.tokenize("embed", text);
    const fitted = tokens.length <= limit ? text : await this.#servers.detokenize("embed", tokens.slice(0, Math.max(1, limit - EMBED_TOKEN_MARGIN)));
    return this.#servers.tokenize("embed", fitted, false, true);
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
      messages: [...(request.system !== undefined ? [{ role: "system", content: request.system }] : []), { role: "user", content: request.prompt }],
      stream: false,
      ...(request.grammar !== undefined ? { grammar: request.grammar } : {}),
      ...(request.maxTokens !== undefined ? { max_tokens: request.maxTokens } : {}),
      ...(request.temperature !== undefined ? { temperature: request.temperature } : {}),
      ...(request.topK !== undefined ? { top_k: request.topK } : {}),
      ...(request.topP !== undefined ? { top_p: request.topP } : {}),
      ...(request.minP !== undefined ? { min_p: request.minP } : {}),
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
