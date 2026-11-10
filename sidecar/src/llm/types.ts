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

export type Capability = "embed" | "rerank" | "generate";

/** Interactive is search-time work. Background is embedding, which can wait. */
export type PriorityClass = "interactive" | "background";

/** One entry of the worker pool's configuration. */
export interface PoolWorkerConfig {
  name: string;
  url: string;
  token: string | undefined;
  capabilities: Capability[];
  /** Tiebreak between workers of equal load. Lower is preferred. */
  priority: number;
  /** Calls this sidecar sends this worker at once. */
  maxConcurrent: number;
  /** When this worker's circuits open and how long they stay open. Unset fields take the pool's. */
  availability?: Partial<WorkerAvailability>;
}

/** A worker's circuit breaker, as configured. */
export interface WorkerAvailability {
  /** Consecutive failures that open a role's circuit. */
  failureThreshold: number;
  /** First wait before a trial call; it doubles after each failed trial. */
  backoffMs: number;
  /** Longest that wait grows to. */
  maxBackoffMs: number;
}

/** What a search went without. Reported to callers with `degraded: true`. */
export type DegradedReason = "no_rerank" | "no_expansion" | "cpu_embedding" | "no_vectors";

/** What `GET /workers` returns per worker. Holds no URL path or token. */
export interface WorkerView {
  name: string;
  host: string;
  state: "unknown" | "up" | "busy" | "starting" | "down" | "circuit_open";
  /** The worst of `circuits`, over the roles this worker is configured for. */
  circuit: "closed" | "open" | "half_open";
  /** `documents` is document embedding, kept apart from `embed`, which is query vectors. */
  circuits: Record<Capability | "documents", "closed" | "open" | "half_open">;
  /** This worker embedded the same text differently from another. Reported only: nothing acts on it. */
  embedDisagrees: boolean;
  capabilities: Capability[];
  priority: number;
  maxConcurrent: number;
  /** Null until checked. False keeps the worker off embedding and tokens. */
  embedModelCompatible: boolean | null;
  rerankTransport: string | null;
  lastHeartbeat: string | null;
  inFlight: number;
  queueDepth: number;
  calls: number;
  /** Failed share of the last 100 calls. */
  errorRate: number;
  /** An operator took this worker out of rotation. It finishes what it has and gets no new calls. */
  drained: boolean;
}

export interface PoolLoad {
  classes: Record<PriorityClass, { inFlight: number; waiting: number }>;
  workers: Array<{ name: string; interactive: number; background: number; queueDepth: number }>;
}

/** A document a worker could not embed, set aside so it stops failing every later job. */
export interface DeadLetter {
  hash: string;
  path: string;
  reason: string;
  at: string;
}

/** Whether model work is at full strength. Drives `/ready`. */
export interface LlmHealth {
  mode: "local" | "remote";
  degraded: boolean;
  /** Roles no worker can serve right now. `documents` is listed when only document embedding is out. */
  unavailable: Array<Capability | "documents">;
  workersUp: number;
  workersTotal: number;
  /** Workers that embedded the same text differently. Reported only: nothing acts on it. */
  embedDisagreement: string[];
  /** Documents out of vector search because a worker refused them. Retried by themselves within a day. */
  documentsSetAside: number;
}
