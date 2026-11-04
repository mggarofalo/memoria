import { existsSync } from "node:fs";
import type { DocumentHead, MetadataSource } from "./metadata.ts";
import type { EmbedSummary, UpdateSummary } from "./jobs.ts";
import type { BreakerOptions } from "./llm/pool.ts";
import type { RemoteLlm } from "./llm/remote.ts";
import { withShortBusyTimeout } from "./sqlite-busy.ts";
import type { DeadLetter, DegradedReason, EmbeddingResult, LlmCallSink, LlmHealth, LlmSurface, PoolLoad, PoolWorkerConfig, Queryable, RerankDocument, RerankResult, WorkerView } from "./llm/types.ts";
import type { SdkDb, SdkHandles, WiredRemote } from "./llm/wire.ts";

/**
 * The slice of the QMD SDK the sidecar depends on, expressed as our own
 * types so tests can run against an in-memory fake without loading
 * better-sqlite3 or node-llama-cpp. `openBackend()` is the only place that
 * imports `@tobilu/qmd`.
 */

export type SearchType = "lex" | "vec" | "hyde";

export interface SearchQuery {
  type: SearchType;
  query: string;
}

export interface BackendSearchOptions {
  query?: string;
  queries?: SearchQuery[];
  intent?: string;
  collections?: string[];
  limit: number;
  candidateLimit?: number;
  minScore: number;
  rerank: boolean;
}

export interface SearchHit {
  /** QMD virtual path, e.g. `qmd://claude-sessions/2026-01-01_abc.md`. */
  file: string;
  /** `collection/path`. */
  displayPath: string;
  title: string;
  body: string;
  bestChunk: string;
  bestChunkPos: number;
  score: number;
  context: string | null;
  docid: string;
}

export interface DocumentInfo {
  filepath: string;
  displayPath: string;
  title: string;
  context: string | null;
  hash: string;
  docid: string;
  collectionName: string;
  modifiedAt: string;
  bodyLength: number;
  body?: string;
}

export type DocumentLookupError =
  | { error: "not_found"; query: string; similarFiles: string[] }
  | { error: "excluded_by_ignore"; query: string; collection: string; path: string; rule: string };

export type MultiGetEntry =
  | { doc: DocumentInfo; skipped: false }
  | { doc: Pick<DocumentInfo, "filepath" | "displayPath">; skipped: true; skipReason: string };

/** An active document's identity, with nothing read from its content. */
export interface DocumentRef {
  collection: string;
  path: string;
  hash: string;
}

export interface CollectionRow {
  name: string;
  pwd: string;
  glob_pattern: string;
  doc_count: number;
  active_count: number;
  last_modified: string | null;
  includeByDefault: boolean;
}

export interface ContextRow {
  collection: string;
  path: string;
  context: string;
}

export interface IndexStatusInfo {
  totalDocuments: number;
  needsEmbedding: number;
  /** Documents out of embedding until a retry is due. Not counted in `needsEmbedding`. */
  documentsSetAside: number;
  hasVectorIndex: boolean;
  collections: { name: string; path: string | null; pattern: string | null; documents: number; lastUpdated: string }[];
}

export interface ModelStatus {
  embed: string;
  rerank: string;
  generate: string;
  loaded: { embed: boolean; rerank: boolean; generate: boolean };
  /** True when every model is loaded in memory. */
  resident: boolean;
}

export interface Snippet {
  line: number;
  snippet: string;
}

export interface SearchOutcome {
  hits: SearchHit[];
  /** What this search went without. Empty at full strength. */
  degradedReasons: DegradedReason[];
}

export interface BackendEmbedOptions {
  force: boolean;
  collection?: string;
  /** Stop at the next safe point. Honoured in remote mode. */
  signal?: AbortSignal;
  onWorker?: (name: string) => void;
  /** A document reached the index. */
  onProgress?: () => void;
  /** With `force`: when the job was accepted, so a resumed job skips what it already redid. */
  forceSince?: string;
}

export interface QmdBackend extends MetadataSource {
  readonly dbPath: string;
  search(options: BackendSearchOptions): Promise<SearchHit[]>;
  searchDetailed(options: BackendSearchOptions): Promise<SearchOutcome>;
  llmHealth(): LlmHealth;
  /** Empty in local mode. */
  workers(): WorkerView[];
  /** Null in local mode. */
  poolLoad(): PoolLoad | null;
  /** Resolve a path, `qmd://` URI, or docid to document metadata (no body). */
  get(reference: string): Promise<DocumentInfo | DocumentLookupError>;
  getDocumentBody(filepath: string, options: { fromLine?: number; maxLines?: number }): Promise<string | null>;
  multiGet(pattern: string, options: { includeBody: boolean; maxBytes: number }): Promise<{ docs: MultiGetEntry[]; errors: string[] }>;
  /** Active documents, of these collections only when given. Touches no other collection's rows. */
  listDocumentRefs(collections?: readonly string[]): Promise<DocumentRef[]>;
  listCollections(): Promise<CollectionRow[]>;
  /** The collection's operator-configured ignore patterns, which QMD's scan applies. */
  ignorePatterns(collection: string): string[];
  listContexts(): Promise<ContextRow[]>;
  getDefaultCollectionNames(): Promise<string[]>;
  update(options: { collections?: string[] }): Promise<UpdateSummary>;
  embed(options: BackendEmbedOptions): Promise<EmbedSummary>;
  getStatus(): Promise<IndexStatusInfo>;
  /** Drop every cached rerank score and expansion. Returns how many. */
  purgeCache(): number;
  /** Documents set aside because no worker could embed them. Empty in local mode. */
  deadLetters(): DeadLetter[];
  /** Let those documents embed again. Returns how many. */
  clearDeadLetters(): number;
  snippet(body: string, query: string, maxLen: number, chunkPos: number, chunkLen: number, intent?: string): Snippet;
  addLineNumbers(text: string, startLine: number): string;
  models(): ModelStatus;
  close(): Promise<void>;
}

/**
 * Where model work runs. `local` is the SDK's in-process path, untouched.
 * `remote` routes it to a worker; `fallback` lets tokenizing and query embedding
 * run on this host's CPU when no worker answers.
 */
export type LlmOptions =
  | { mode: "local" }
  | {
      mode: "remote";
      workers: PoolWorkerConfig[];
      embedDimensions: number;
      fallback: boolean;
      statusPollMs?: number;
      /** Tuning for the embedding farm's batches. */
      embedBatchBytes?: number;
      /** Tuning for the circuit breaker. */
      breaker?: Partial<BreakerOptions>;
      /** Deadline for one search-time rerank call. */
      rerankTimeoutMs?: number;
      /** How often workers are compared on a fixed text. */
      probeEveryMs?: number;
      /** Deadline for all of one search's model work. */
      searchDeadlineMs?: number;
    };

export interface OpenBackendOptions {
  dbPath: string | undefined;
  keepWarm: boolean;
  llm?: LlmOptions;
  /** Receives one structured entry per worker call. */
  onLlmCall?: LlmCallSink;
  onLlmDegraded?: (entry: { operation: string; error: string; fallback: boolean }) => void;
  /** Something an operator should know about, that needs no action from the caller. */
  onLlmWarning?: (message: string) => void;
  /** Told how many vector rows the startup repair removed, when it removes any. */
  onVectorRepair?: (rows: number) => void;
}

/** Direct access to the model path, bypassing `llm_cache`. For the parity benchmark. */
export interface LlmProbe {
  readonly mode: "local" | "remote";
  readonly rerankTransport: string;
  embedBatch(texts: string[]): Promise<(EmbeddingResult | null)[]>;
  rerank(query: string, documents: RerankDocument[]): Promise<RerankResult>;
  expandQuery(query: string): Promise<Queryable[]>;
  /** Empty the cached expansions and rerank scores in this index. */
  clearCache(): void;
  /** What is cached, so a test can show a failure wrote nothing. */
  cacheEntries(): number;
  /** Refresh worker state now rather than at the next heartbeat. */
  refreshWorkers(): Promise<void>;
}

export interface OpenedBackend extends QmdBackend {
  readonly llm: LlmProbe;
}

/** `dist/llm.js` exports the SDK does not re-export from its entry point. */
interface SdkLlmModule {
  setDefaultLlamaCpp(llm: unknown): void;
  formatDocForEmbedding(text: string, title?: string, modelUri?: string): string;
  DEFAULT_MODEL_CACHE_DIR: string;
}

/** `dist/store.js` exports likewise. Pinned by test/llm-surface.test.ts. */
interface SdkStoreModule {
  getEmbeddingFingerprint(model: string): string;
  chunkDocumentByTokens(content: string, maxTokens: undefined, overlap: undefined, window: undefined, filepath: string, strategy: undefined, signal: AbortSignal): Promise<Array<{ text: string; pos: number }>>;
  extractTitle(content: string, filename: string): string;
  clearAllEmbeddings(db: unknown, collection?: string): void;
  validateLexQuery(query: string): string | null;
  validateSemanticQuery(query: string): string | null;
}

/**
 * What remote mode needs from the SDK. The exports map hides these modules; a file
 * URL reaches the same instances. The surface test pins this for the exact version.
 */
async function sdkHandles(storeInternal: unknown, local: LlmSurface): Promise<SdkHandles> {
  const entry = import.meta.resolve("@tobilu/qmd");
  const sdkStore = (await import(new URL("./store.js", entry).href)) as SdkStoreModule;
  const sdkLlm = (await import(new URL("./llm.js", entry).href)) as SdkLlmModule;
  if (typeof sdkLlm.setDefaultLlamaCpp !== "function") {
    throw new Error("This QMD version has no setDefaultLlamaCpp; remote LLM mode needs updating for it.");
  }
  const internal = storeInternal as Pick<SdkHandles, "insertEmbedding" | "ensureVecTable"> & { db: SdkDb; getHashesNeedingEmbedding(model: string): unknown };
  return {
    db: internal.db,
    local,
    validators: sdkStore,
    insertEmbedding: (...args) => internal.insertEmbedding(...args),
    ensureVecTable: (dimensions) => internal.ensureVecTable(dimensions),
    migrateVectors: (model) => void internal.getHashesNeedingEmbedding(model),
    fingerprint: (model) => sdkStore.getEmbeddingFingerprint(model),
    chunk: (body, path, signal) => sdkStore.chunkDocumentByTokens(body, undefined, undefined, undefined, path, undefined, signal),
    format: (text, title, model) => sdkLlm.formatDocForEmbedding(text, title, model),
    title: (body, path) => sdkStore.extractTitle(body, path),
    clearVectors: () => sdkStore.clearAllEmbeddings(internal.db),
    modelCacheDir: sdkLlm.DEFAULT_MODEL_CACHE_DIR,
    setDefaultLlm: (llm) => sdkLlm.setDefaultLlamaCpp(llm),
  };
}

/**
 * The SDK writes `content_vectors` and then `vectors_vec` with no transaction, so a
 * kill between them leaves a row claiming a vector that does not exist. The document
 * then reads as fully embedded, for good. This runs at every start, in both LLM
 * modes, and removes such rows so the document becomes pending again.
 *
 * Another process caught between those two inserts looks the same, so its row can go
 * too. That is harmless: the document embeds again on the next job. Nothing cheap can
 * tell the two apart, because the SDK's write holds no lock between them.
 */
export function repairVectorRows(db: { prepare(sql: string): { get(...a: unknown[]): unknown; run(...a: unknown[]): { changes: number | bigint } }; exec(sql: string): unknown }): number {
  const table = db.prepare("SELECT 1 FROM sqlite_master WHERE type='table' AND name='vectors_vec'").get();
  try {
    const statement = table
      ? "DELETE FROM content_vectors WHERE hash || '_' || seq NOT IN (SELECT hash_seq FROM vectors_vec)"
      : "DELETE FROM content_vectors";
    return withShortBusyTimeout(db, () => Number(db.prepare(statement).run().changes));
  } catch {
    // A host without sqlite-vec cannot read that table, and so cannot judge it.
    return 0;
  }
}

function wantsVectors(o: BackendSearchOptions): boolean {
  return o.query !== undefined || (o.queries ?? []).some((q) => q.type !== "lex");
}

function holdsVectors(db: { prepare(sql: string): { get(...a: unknown[]): unknown } }): boolean {
  if (!db.prepare("SELECT 1 FROM sqlite_master WHERE type='table' AND name='vectors_vec'").get()) return false;
  return db.prepare("SELECT 1 FROM content_vectors LIMIT 1").get() !== undefined;
}

function remoteHealth(remote: RemoteLlm, documentsSetAside: number): LlmHealth {
  const status = remote.status();
  const unavailable: LlmHealth["unavailable"] = (["embed", "rerank", "generate"] as const).filter((role) => !status[role]);
  if (status.embed && !remote.pool.documentsUp()) unavailable.push("documents");
  const workers = remote.pool.workers();
  return {
    mode: "remote",
    degraded: unavailable.length > 0,
    unavailable,
    workersUp: workers.filter((w) => w.state === "up" || w.state === "busy").length,
    workersTotal: workers.length,
    embedDisagreement: [...remote.pool.disagreeing],
    documentsSetAside,
  };
}

interface LlamaLike {
  embedModel?: unknown;
  rerankModel?: unknown;
  generateModel?: unknown;
  embedModelName?: string;
  rerankModelName?: string;
  generateModelName?: string;
  inactivityTimeoutMs?: number;
}

interface HeadRow {
  collection: string;
  path: string;
  head: string;
}

function requireIndex(dbPath: string): void {
  if (!existsSync(dbPath)) {
    throw new Error(`QMD index not found at ${dbPath}. Run \`qmd update\` on this host first, or set MEMORIA_QMD_DB_PATH.`);
  }
}

function reportRepair(rows: number, options: OpenBackendOptions): void {
  if (rows > 0) options.onVectorRepair?.(rows);
}

/**
 * Degraded mode must not compete with a worker for this host's GPU. The SDK reads
 * this when it first loads a model, which may be never. Process-wide: only set when
 * a fallback can load, so the parity benchmark, which runs both modes in one process
 * with the fallback off, keeps its local side's GPU.
 */
function keepFallbackOffGpu(llm: LlmOptions): void {
  if (llm.mode === "remote" && llm.fallback) process.env.QMD_FORCE_CPU ??= "1";
}

/**
 * The SDK hardcodes a five-minute idle unload. Zero disables the timer so the ~11s
 * cold start is paid once per process, not once per lull.
 */
function disableIdleUnload(llm: LlamaLike | undefined): void {
  if (llm) llm.inactivityTimeoutMs = 0;
}

async function wire(storeInternal: unknown, holder: { llm?: LlamaLike }, config: Extract<LlmOptions, { mode: "remote" }>, options: OpenBackendOptions): Promise<WiredRemote> {
  const { wireRemote } = await import("./llm/wire.ts");
  const wired = wireRemote(await sdkHandles(storeInternal, holder.llm as unknown as LlmSurface), config, options);
  holder.llm = wired.llm as unknown as LlamaLike;
  return wired;
}

/**
 * Open the real QMD store. The index must already exist — the sidecar serves
 * an index the host maintains with `qmd`, it does not create one.
 */
export async function openBackend(options: OpenBackendOptions): Promise<OpenedBackend> {
  const llmOptions = options.llm ?? { mode: "local" as const };
  keepFallbackOffGpu(llmOptions);
  const qmd = await import("@tobilu/qmd");
  const dbPath = options.dbPath ?? qmd.getDefaultDbPath();
  requireIndex(dbPath);
  // DB-only mode: collections and contexts come from store_collections inside
  // index.sqlite, which `qmd` keeps in sync with ~/.config/qmd/index.yml.
  const store = await qmd.createStore({ dbPath });
  // LlamaCpp keeps its model handles private; we only read them for status.
  const internal = store.internal as unknown as { llm?: LlamaLike };
  const llm = internal.llm;
  if (llmOptions.mode === "local" && options.keepWarm) disableIdleUnload(llm);
  reportRepair(repairVectorRows(store.internal.db), options);
  const wired = llmOptions.mode === "remote" ? await wire(store.internal, internal, llmOptions, options) : undefined;
  const remote = wired?.llm;
  const setAside = (): number => wired?.deadLetters().length ?? 0;
  const active = (remote ?? llm) as unknown as LlmSurface;
  const db = store.internal.db;
  const headsStatement = db.prepare(
    `SELECT d.collection AS collection, d.path AS path, substr(c.doc, 1, ?) AS head
     FROM documents d JOIN content c ON c.hash = d.hash
     WHERE d.active = 1`,
  );
  // store.listCollections() drops this column. The integration test pins it to the SDK.
  const ignoreStatement = db.prepare(`SELECT ignore_patterns AS patterns FROM store_collections WHERE name = ?`);

  const runSearch = async (o: BackendSearchOptions): Promise<SearchHit[]> => {
    const results = await store.search({
      ...(o.query !== undefined ? { query: o.query } : {}),
      ...(o.queries !== undefined ? { queries: o.queries } : {}),
      ...(o.intent !== undefined ? { intent: o.intent } : {}),
      ...(o.collections !== undefined ? { collections: o.collections } : {}),
      ...(o.candidateLimit !== undefined ? { candidateLimit: o.candidateLimit } : {}),
      limit: o.limit,
      minScore: o.minScore,
      rerank: o.rerank,
    });
    return results.map((r) => ({
      file: r.file,
      displayPath: r.displayPath,
      title: r.title,
      body: r.body,
      bestChunk: r.bestChunk,
      bestChunkPos: r.bestChunkPos,
      score: r.score,
      context: r.context,
      docid: r.docid,
    }));
  };

  /** Whatever else happened, an index holding no vectors means vector search gave nothing. */
  const searchDetailed = async (o: BackendSearchOptions): Promise<SearchOutcome> => {
    const outcome = wired ? await wired.search(runSearch, o) : { hits: await runSearch(o), degradedReasons: [] };
    if (!wantsVectors(o) || outcome.degradedReasons.includes("no_vectors") || holdsVectors(db)) return outcome;
    return { hits: outcome.hits, degradedReasons: [...outcome.degradedReasons, "no_vectors"] };
  };

  const llmHealth = (): LlmHealth => (remote ? remoteHealth(remote, setAside()) : { mode: "local", degraded: false, unavailable: [], workersUp: 0, workersTotal: 0, embedDisagreement: [], documentsSetAside: 0 });
  const refsInCollection = db.prepare(`SELECT collection, path, hash FROM documents WHERE collection = ? AND active = 1 ORDER BY path`);
  const allRefs = db.prepare(`SELECT collection, path, hash FROM documents WHERE active = 1 ORDER BY collection, path`);

  return {
  dbPath: store.dbPath,
  search: async (o) => (await searchDetailed(o)).hits,
  searchDetailed,
  llmHealth,
  workers: () => remote?.pool.workers() ?? [],
  poolLoad: () => remote?.pool.load() ?? null,
  get: (reference) => store.get(reference, { includeBody: false }),
  getDocumentBody: (filepath, o) => store.getDocumentBody(filepath, o),
  multiGet: (pattern, o) => store.multiGet(pattern, o),
  listDocumentRefs: async (collections) =>
    (collections ? collections.flatMap((c) => refsInCollection.all(c)) : allRefs.all()) as DocumentRef[],
  listCollections: () => store.listCollections(),
  ignorePatterns: (collection) => {
    const row = ignoreStatement.get(collection) as { patterns: string | null } | undefined;
    return row?.patterns ? (JSON.parse(row.patterns) as string[]) : [];
  },
  listContexts: () => store.listContexts(),
  getDefaultCollectionNames: () => store.getDefaultCollectionNames(),
  update: async (o) => {
    const r = await store.update(o.collections ? { collections: o.collections } : {});
    return {
      collections: r.collections,
      indexed: r.indexed,
      updated: r.updated,
      unchanged: r.unchanged,
      removed: r.removed,
      skipped: r.skipped,
      needsEmbedding: Math.max(0, r.needsEmbedding - setAside()),
    };
  },
  purgeCache: () => withShortBusyTimeout(db as never, () => Number(db.prepare("DELETE FROM llm_cache").run().changes)),
  deadLetters: () => wired?.deadLetters() ?? [],
  clearDeadLetters: () => wired?.clearDeadLetters() ?? 0,
  embed: async (o) => {
    if (wired) return wired.embed(o);
    const r = await store.embed({ force: o.force, ...(o.collection !== undefined ? { collection: o.collection } : {}) });
    return { docsProcessed: r.docsProcessed, chunksEmbedded: r.chunksEmbedded, errors: r.errors, durationMs: r.durationMs };
  },
  getStatus: async () => {
    const status = await store.getStatus();
    const documentsSetAside = setAside();
    return { ...status, needsEmbedding: Math.max(0, status.needsEmbedding - documentsSetAside), documentsSetAside };
  },
  readDocumentHeads: (maxBytes: number): DocumentHead[] => {
    const rows = headsStatement.all(maxBytes) as HeadRow[];
    return rows.map((r) => ({ collection: r.collection, path: r.path, head: r.head ?? "" }));
  },
  snippet: (body, query, maxLen, chunkPos, chunkLen, intent) => {
    const s = qmd.extractSnippet(body, query, maxLen, chunkPos, chunkLen, intent);
    return { line: s.line, snippet: s.snippet };
  },
  addLineNumbers: (text, startLine) => qmd.addLineNumbers(text, startLine),
  models: () => (remote ? remoteModelStatus(remote, llm as unknown as LlmSurface) : modelStatus(llm)),
  close: async () => {
    wired?.close();
    // store.close() disposes the SDK's own instance, which is also the fallback.
    await store.close();
  },
  llm: {
    mode: llmOptions.mode,
    rerankTransport: remote ? remote.transportName : "in-process",
    embedBatch: (texts) => active.embedBatch(texts),
    rerank: (query, documents) => active.rerank(query, documents),
    expandQuery: (query) => active.expandQuery(query),
    clearCache: () => {
      db.exec("DELETE FROM llm_cache");
    },
    cacheEntries: () => (db.prepare("SELECT COUNT(*) AS n FROM llm_cache").get() as { n: number }).n,
    refreshWorkers: async () => {
      await remote?.refreshStatus();
    },
  },
};
}


function remoteModelStatus(remote: RemoteLlm, local: LlmSurface): ModelStatus {
  const loaded = remote.status();
  return {
    embed: local.embedModelName,
    rerank: local.rerankModelName,
    generate: local.generateModelName,
    loaded,
    resident: loaded.embed && loaded.rerank && loaded.generate,
  };
}

function modelStatus(held: LlamaLike | undefined): ModelStatus {
  const llm = held ?? {};
  const loaded = { embed: !!llm.embedModel, rerank: !!llm.rerankModel, generate: !!llm.generateModel };
  return {
    embed: llm.embedModelName ?? "",
    rerank: llm.rerankModelName ?? "",
    generate: llm.generateModelName ?? "",
    loaded,
    resident: Object.values(loaded).every(Boolean),
  };
}
