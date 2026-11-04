/**
 * The model surface the QMD SDK calls on `store.internal.llm`, restated as our
 * own types so this directory never imports `@tobilu/qmd`. test/llm-surface.test.ts
 * pins this list to the installed SDK and fails when a bump changes it.
 */

export interface EmbedOptions {
  model?: string;
  isQuery?: boolean;
  title?: string;
}

export interface EmbeddingResult {
  embedding: number[];
  model: string;
}

export interface RerankDocument {
  file: string;
  text: string;
  title?: string;
}

export interface RerankDocumentResult {
  file: string;
  score: number;
  index: number;
}

export interface RerankResult {
  results: RerankDocumentResult[];
  model: string;
}

export type QueryType = "lex" | "vec" | "hyde";

export interface Queryable {
  type: QueryType;
  text: string;
}

export interface ExpandOptions {
  context?: string;
  includeLexical?: boolean;
}

/** Everything store.js, the session wrapper and the chunker reach for. */
export interface LlmSurface {
  readonly embedModelName: string;
  readonly rerankModelName: string;
  readonly generateModelName: string;
  embed(text: string, options?: EmbedOptions): Promise<EmbeddingResult | null>;
  embedBatch(texts: string[], options?: EmbedOptions): Promise<(EmbeddingResult | null)[]>;
  expandQuery(query: string, options?: ExpandOptions): Promise<Queryable[]>;
  rerank(query: string, documents: RerankDocument[], options?: { model?: string }): Promise<RerankResult>;
  tokenize(text: string): Promise<readonly number[]>;
  detokenize(tokens: readonly number[]): Promise<string>;
  dispose(): Promise<void>;
}

/**
 * What degraded mode may do with no worker: tokenize and embed on this host's
 * CPU. In production this is the SDK's own LlamaCpp, which loads lazily, so a
 * healthy worker means the model never loads here.
 */
export type LocalFallback = Pick<LlmSurface, "embedBatch" | "tokenize" | "detokenize">;

/** One structured line per worker call. Holds no text, vectors or credentials. */
export interface LlmCallLog {
  worker: string;
  operation: string;
  batchSize: number;
  durationMs: number;
  ok: boolean;
  status?: number;
  errorCode?: string;
}

export type LlmCallSink = (entry: LlmCallLog) => void;

/** What the sidecar sends to reach one worker. */
export interface WorkerOptions {
  url: string;
  token: string | undefined;
  /** Expected length of every embedding; 0 pins it to the first response. */
  embedDimensions: number;
}
