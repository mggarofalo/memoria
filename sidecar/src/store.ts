import { AsyncLocalStorage } from "node:async_hooks";
import { existsSync } from "node:fs";
import { DatabaseSync } from "node:sqlite";
import { metadataEntries, type DocumentHead, type MetadataSource, type ParsedMetadata } from "./metadata.ts";
import type { EmbedSummary, UpdateSummary } from "./jobs.ts";
import type { BreakerOptions } from "./llm/pool.ts";
import type { RemoteLlm } from "./llm/remote.ts";
import { requireSdkFixes, type SdkStoreFunctions } from "./sdk-check.ts";
import { collectionsToKeep, type CollectionRoot } from "./collection-roots.ts";
import { DeadLetterBook, documentHolds, moveLegacyLetters } from "./dead-letters.ts";
import { purgeDeleted, removedAny, truncateLog, type PurgeDb } from "./purge.ts";
import { ScanThreads } from "./scan.ts";
import { inBatches, ScanStopped } from "./scan-batches.ts";
import { withShortBusyTimeout } from "./sqlite-busy.ts";
import { WriteGate, yieldToMainWrites } from "./write-gate.ts";
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
  /**
   * `false` searches a plain query without expanding it: QMD's hybrid search runs as it does
   * when its BM25 probe is strong enough to skip expansion, on the query itself, lexically and
   * by vector, with no call to the expansion model. Meaningless with `queries`.
   */
  expand?: boolean;
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
  /** Take a worker out of rotation. Null when no worker has that name, as always in local mode. */
  drainWorker(name: string): WorkerView | null;
  /** Put a drained worker back. Null when no worker has that name. */
  enableWorker(name: string): WorkerView | null;
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
  /**
   * Scan collections into the index, on a thread of its own (src/scan.ts). An abort stops it
   * after the document it is on, and it rejects with `ScanStopped`; what it wrote stands.
   */
  update(options: { collections?: string[]; signal?: AbortSignal; headBytes?: number }): Promise<ScanSummary>;
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
  /** Start no more worker calls, for shutdown. Those in flight go on. Nothing in local mode. */
  stopWorkerCalls(): void;
  /** End the worker calls still in flight, for shutdown. Nothing in local mode. */
  abortWorkerCalls(): void;
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
      /** Most embedding batches one job runs at once. 0 means as many as workers have background slots. */
      embedConcurrency?: number;
      /** Slots per worker that background work may not use. */
      interactiveReserve?: number;
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
  /**
   * Where documents set aside from embedding are kept: the queue file's, from the process that
   * holds it (src/main.ts). Without one they are kept in memory, as for a tool.
   */
  deadLetters?: DeadLetterBook;
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
  CHUNK_SIZE_TOKENS: number;
  CHUNK_OVERLAP_TOKENS: number;
  CHUNK_WINDOW_TOKENS: number;
  chunkDocumentAsync(content: string, maxChars: number, overlapChars: number, windowChars: number, filepath: string, strategy: string): Promise<Array<{ text: string; pos: number }>>;
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
    // The same first pass as chunkDocumentByTokens, which estimates three characters a token.
    firstPassTexts: async (body, path) =>
      (await sdkStore.chunkDocumentAsync(body, sdkStore.CHUNK_SIZE_TOKENS * 3, sdkStore.CHUNK_OVERLAP_TOKENS * 3, sdkStore.CHUNK_WINDOW_TOKENS * 3, path, "regex")).map((c) => c.text),
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

/** The SDK's internal store, as `hybridQuery` reaches its expansion. Pinned by test/llm-surface.test.ts. */
export interface ExpandingStore {
  expandQuery(query: string, model?: string): Promise<unknown[]>;
}

/**
 * Lets one search go unexpanded. `hybridQuery` asks the store it is given for
 * `expandQuery(query)`, and its strong-signal path already runs on an empty answer, so
 * answering that inside the returned scope is QMD's own search without expansion: the
 * query's own BM25 and vector lists, at their usual weights, and no model call. The scope
 * is per async context, so searches running together each keep their own.
 */
export function withholdExpansion(store: ExpandingStore): AsyncLocalStorage<boolean> {
  const scope = new AsyncLocalStorage<boolean>();
  const expand = store.expandQuery.bind(store);
  store.expandQuery = (query, model) => (scope.getStore() === true ? Promise.resolve([]) : expand(query, model));
  return scope;
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
    workersUp: workers.filter((w) => !w.drained && (w.state === "up" || w.state === "busy")).length,
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
    throw new Error(`QMD index not found at ${dbPath}. Make it with the first-run command in docs/deployment.md, or the \`qmd\` CLI the sidecar installs, or set MEMORIA_QMD_DB_PATH.`);
  }
}

/** The index schema the pinned SDK writes, its `STORE_SCHEMA_VERSION`. test/integration-schema.test.ts holds the two together. */
export const SUPPORTED_INDEX_SCHEMA = 1;

/** How long the schema check waits for another process's write, as after a restore an operator's `qmd` may be scanning. */
const SCHEMA_CHECK_BUSY_MS = 10_000;

interface IndexSchema {
  version: number;
  vectorRows: boolean;
  vectorsVec: boolean;
}

function readSchema(dbPath: string): IndexSchema {
  let db: DatabaseSync | undefined;
  try {
    db = new DatabaseSync(dbPath, { readOnly: true, timeout: SCHEMA_CHECK_BUSY_MS });
    const has = (name: string): boolean => db!.prepare("SELECT 1 FROM sqlite_master WHERE name = ?").get(name) !== undefined;
    return { version: Number((db.prepare("PRAGMA user_version").get() as { user_version: number }).user_version), vectorRows: has("vector_rows"), vectorsVec: has("vectors_vec") };
  } catch (err) {
    throw new Error(`The index at ${dbPath} could not be read to check its schema: ${err instanceof Error ? err.message : String(err)}`);
  } finally {
    db?.close();
  }
}

/**
 * Refuse an index a newer QMD has touched: a `user_version` above the SDK's, or the `vector_rows`
 * table that replaces `vectors_vec` upstream. On one migrated, the startup repair would read every
 * vector row as having no vector and delete it, and the purge would stop removing vectors, both
 * without a word. Upstream moves vectors over in steps at the old version and flips it only at the
 * end, so an index can also be caught partway, with both tables: refused too, as this SDK would
 * leave the copy in `vector_rows` to fall behind, deleted documents' vectors included. Rolling the
 * image back across a QMD upgrade, or running a stock `qmd` newer than the pinned one, gets here.
 * It reads the file before the SDK opens it, as the SDK's own setup writes to the index.
 */
export function requireKnownSchema(dbPath: string): void {
  const { version, vectorRows, vectorsVec } = readSchema(dbPath);
  if (version <= SUPPORTED_INDEX_SCHEMA && !vectorRows) return;
  const at = `The index at ${dbPath}`;
  const sdk = `this sidecar's SDK writes schema version ${SUPPORTED_INDEX_SCHEMA}`;
  if (version <= SUPPORTED_INDEX_SCHEMA && vectorsVec) {
    throw new Error(
      `${at} is partway through a newer QMD's upgrade: it has schema version ${version} with both vector_rows and vectors_vec, and ${sdk}. ` +
        "Serving it would let the newer copy of its vectors fall behind, deleted documents' vectors included, so the sidecar stops. " +
        "Finish the upgrade with that QMD and run the sidecar release that matches it, or restore the index from a backup taken before.",
    );
  }
  const found = vectorRows ? `schema version ${version} and a vector_rows table` : `schema version ${version}`;
  throw new Error(
    `${at} was migrated by a newer QMD: it has ${found}, and ${sdk}. ` +
      "Serving it would delete its vector rows at start and leave deleted documents' vectors behind, so the sidecar stops. " +
      "Run the sidecar release that matches the index, or restore the index from a backup taken before the migration.",
  );
}

/**
 * Live documents in a collection the index has no record of. Stock QMD's config sync deletes
 * the record of every collection a config does not name, and a missing config names none,
 * while the documents stay (upstream tobi/qmd#1048). This SDK is patched against that, but a
 * `qmd` installed elsewhere on the host is not. Serving such an index would refuse every
 * search and write naming those collections, and its next scan would not see them, so the
 * sidecar stops instead. `qmd collection remove` takes the documents with the record, so it
 * leaves nothing for this to find.
 */
export function requireCollectionRecords(db: { prepare(sql: string): { all(): unknown[] } }): void {
  const orphans = db
    .prepare("SELECT DISTINCT collection FROM documents WHERE active = 1 AND collection NOT IN (SELECT name FROM store_collections) ORDER BY collection")
    .all() as Array<{ collection: string }>;
  if (orphans.length === 0) return;
  const names = orphans.map((o) => o.collection).join(", ");
  throw new Error(
    `The index holds documents for collections it has no record of: ${names}. Their records were deleted, most likely by a qmd command run while its config was missing. ` +
      "Nothing has been changed. Restore the records, then start again: see \"Lost collection records\" in docs/deployment.md.",
  );
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

async function wire(storeInternal: unknown, holder: { llm?: LlamaLike }, config: Extract<LlmOptions, { mode: "remote" }>, options: OpenBackendOptions, letters: DeadLetterBook): Promise<WiredRemote> {
  const { wireRemote } = await import("./llm/wire.ts");
  const wired = wireRemote(await sdkHandles(storeInternal, holder.llm as unknown as LlmSurface), config, options, letters);
  holder.llm = wired.llm as unknown as LlamaLike;
  return wired;
}

/**
 * Set-asides live in the queue's file, which must never stop search, status or a scan. So
 * trouble with it there is a warning, and the work goes on as if none were set aside.
 */
function tolerated<T>(work: () => T, fallback: T, options: OpenBackendOptions): T {
  try {
    return work();
  } catch (err) {
    options.onLlmWarning?.(`The documents set aside from embedding could not be read or tidied: ${err instanceof Error ? err.message : String(err)}`);
    return fallback;
  }
}

/**
 * The book of set-asides, and its tidying: an older build's table moved out of the index,
 * and the records of documents no row holds any more removed, as their paths may be
 * sensitive. Both run in either LLM mode.
 */
function setAsides(db: unknown, options: OpenBackendOptions): { letters: DeadLetterBook; tidyLetters: () => void } {
  const letters = options.deadLetters ?? new DeadLetterBook(null);
  const tidyLetters = (): void =>
    tolerated(() => {
      moveLegacyLetters(db as never, letters);
      letters.retain(documentHolds(db as never, false));
    }, undefined, options);
  return { letters, tidyLetters };
}

/** What one scan is asked to do, on its thread. */
export interface ScanRequest {
  dbPath: string;
  collections?: string[];
  batchMs: number;
  /** The main thread's count of writes under way, which the scan waits out between groups: see write-gate.ts. */
  mainWrites: Int32Array;
  /** Read each document's head this far and parse its frontmatter after the scan. Unset reads none. */
  headBytes?: number;
}

/** What a scan did, as it crosses back from its thread. */
export interface ScanResult extends UpdateSummary {
  /** For the operator, from the removal of what deleted documents left: see purge.ts. */
  warnings: string[];
  metadata?: ParsedMetadata;
}

/** What `update` reports: the scan, and the documents' metadata when it was asked for. */
export interface ScanSummary extends UpdateSummary {
  metadata?: ParsedMetadata;
}

const HEADS_SQL = `SELECT d.collection AS collection, d.path AS path, substr(c.doc, 1, ?) AS head
   FROM documents d JOIN content c ON c.hash = d.hash
   WHERE d.active = 1`;

/** store.listCollections() drops this column. The integration test pins it to the SDK. */
const ROOTS_SQL = "SELECT name, path FROM store_collections";

const BUSY_PURGE = "The index was busy, so what deleted documents left in it waits for the next scan to remove it.";

function readHeads(db: { prepare(sql: string): { all(...a: unknown[]): unknown[] } }, maxBytes: number): DocumentHead[] {
  return (db.prepare(HEADS_SQL).all(maxBytes) as HeadRow[]).map((r) => ({ collection: r.collection, path: r.path, head: r.head ?? "" }));
}

/**
 * After a scan, on its thread: remove what deleted documents left (purge.ts), then read the
 * metadata, so that it reflects the scan. Each took over 100 ms and 3 s respectively at 52,000
 * documents, which on the main thread would be stalls of their own.
 */
function afterScan(db: PurgeDb & { prepare(sql: string): { all(...a: unknown[]): unknown[] } }, request: ScanRequest, shouldStop: () => boolean): Pick<ScanResult, "warnings" | "metadata"> {
  // A set: the keep list is decided again in each of the purge's transactions.
  const warnings = new Set<string>();
  // Before each of the purge's short transactions, a place to stop, and a write on the main
  // thread goes first: see write-gate.ts. What is left waits for the next scan's purge.
  const between = (): void => {
    if (shouldStop()) throw new ScanStopped();
    yieldToMainWrites(request.mainWrites, MAIN_WRITE_YIELD_MS);
  };
  between();
  const keep = (): string[] => collectionsToKeep(db, db.prepare(ROOTS_SQL).all() as CollectionRoot[], (message) => warnings.add(message));
  // Once whatever there is to purge: it also marks each root that holds a document (collection-roots.ts).
  keep();
  const purged = purgeDeleted(db, keep, { between });
  if (purged === null || !purged.complete) warnings.add(BUSY_PURGE);
  if (removedAny(purged)) truncateLog(db);
  if (request.headBytes === undefined) return { warnings: [...warnings] };
  if (shouldStop()) throw new ScanStopped();
  return { warnings: [...warnings], metadata: metadataEntries(readHeads(db, request.headBytes)) };
}

/**
 * How long one group of a scan's writes holds the write lock. A write on the main thread,
 * such as a search caching a rerank score, waits up to this on its event loop.
 */
export const SCAN_BATCH_MS = 25;

/** Longest a scan waits between groups for the main thread's writes: see yieldToMainWrites. */
export const MAIN_WRITE_YIELD_MS = 1_000;

/** The scan thread's heap ceiling. A scan holds one document at a time, so this is room for the largest. */
export const SCAN_HEAP_MB = 2048;

/** How long shutdown gives a scan to finish its document and commit before terminating its thread. */
export const SCAN_CLOSE_GRACE_MS = 200;

/** The scan thread's entry: the source in a checkout, what tsc built in the image. */
const SCAN_ENTRY = new URL(import.meta.url.endsWith(".ts") ? "./scan-worker.ts" : "./scan-worker.js", import.meta.url);

/**
 * One scan, run where it is called: on the scan thread (src/scan-worker.ts). It opens a
 * connection of its own, as better-sqlite3's cannot cross threads, and closes it when done.
 */
export async function scanIndex(request: ScanRequest, shouldStop: () => boolean): Promise<ScanResult> {
  const qmd = await import("@tobilu/qmd");
  const store = await qmd.createStore({ dbPath: request.dbPath });
  try {
    // A scan overwrites and removes text, which must not linger in free pages: see openBackend.
    store.internal.db.exec("PRAGMA secure_delete = ON");
    const r = await inBatches(store.internal.db as never,(afterDocument) => store.update({ ...(request.collections ? { collections: request.collections } : {}), onProgress: afterDocument }), { batchMs: request.batchMs, shouldStop, beforeGroup: () => yieldToMainWrites(request.mainWrites, MAIN_WRITE_YIELD_MS) });
    // What the scan found deleted leaves nothing behind it: see purge.ts. Any scan can find one.
    const after = afterScan(store.internal.db as never, request, shouldStop);
    return { collections: r.collections, indexed: r.indexed, updated: r.updated, unchanged: r.unchanged, removed: r.removed, skipped: r.skipped, needsEmbedding: r.needsEmbedding, ...after };
  } finally {
    await store.close();
  }
}

/**
 * Open the real QMD store. The index must already exist — the sidecar serves
 * an index the host maintains with `qmd`, it does not create one.
 */
export async function openBackend(options: OpenBackendOptions): Promise<OpenedBackend> {
  const llmOptions = options.llm ?? { mode: "local" as const };
  keepFallbackOffGpu(llmOptions);
  const qmd = await import("@tobilu/qmd");
  const entry = import.meta.resolve("@tobilu/qmd");
  const sdkStore = (await import(new URL("./store.js", entry).href)) as Omit<SdkStoreFunctions, "ensureEmbedContexts">;
  const { LlamaCpp } = (await import(new URL("./llm.js", entry).href)) as { LlamaCpp: { prototype: Pick<SdkStoreFunctions, "ensureEmbedContexts"> } };
  requireSdkFixes({ ...sdkStore, ensureEmbedContexts: LlamaCpp.prototype.ensureEmbedContexts });
  const dbPath = options.dbPath ?? qmd.getDefaultDbPath();
  requireIndex(dbPath);
  requireKnownSchema(dbPath);
  // DB-only mode: collections and contexts come from store_collections inside
  // index.sqlite, and no config file is read or written.
  const store = await qmd.createStore({ dbPath });
  try {
    requireCollectionRecords(store.internal.db);
  } catch (err) {
    await store.close();
    throw err;
  }
  // LlamaCpp keeps its model handles private; we only read them for status.
  const internal = store.internal as unknown as { llm?: LlamaLike };
  const llm = internal.llm;
  if (llmOptions.mode === "local" && options.keepWarm) disableIdleUnload(llm);
  const db = store.internal.db;
  // Before anything else writes through it: a scan on its thread gives way to these writes.
  const writes = new WriteGate();
  writes.guard(db as never);
  // A row deleted, a deleted document's text included, is overwritten on disk, not left in free pages. Set before
  // anything here deletes: the repair below, and the move of an older build's set-asides, which name paths.
  db.exec("PRAGMA secure_delete = ON");
  reportRepair(repairVectorRows(db), options);
  const { letters, tidyLetters } = setAsides(db, options);
  tidyLetters();
  const wired = llmOptions.mode === "remote" ? await wire(store.internal, internal, llmOptions, options, letters) : undefined;
  const remote = wired?.llm;
  const setAside = (): number => tolerated(() => wired?.deadLetters().length ?? 0, 0, options);
  const active = (remote ?? llm) as unknown as LlmSurface;
  const unexpanded = withholdExpansion(store.internal as unknown as ExpandingStore);
  const ignoreStatement = db.prepare(`SELECT ignore_patterns AS patterns FROM store_collections WHERE name = ?`);

  const runSearch = async (o: BackendSearchOptions): Promise<SearchHit[]> => {
    const search = () => store.search({
      ...(o.query !== undefined ? { query: o.query } : {}),
      ...(o.queries !== undefined ? { queries: o.queries } : {}),
      ...(o.intent !== undefined ? { intent: o.intent } : {}),
      ...(o.collections !== undefined ? { collections: o.collections } : {}),
      ...(o.candidateLimit !== undefined ? { candidateLimit: o.candidateLimit } : {}),
      limit: o.limit,
      minScore: o.minScore,
      rerank: o.rerank,
    });
    const results = o.expand === false && o.query !== undefined ? await unexpanded.run(true, search) : await search();
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
  const scans = new ScanThreads({ entry: SCAN_ENTRY, maxHeapMb: SCAN_HEAP_MB });
  const refsInCollection = db.prepare(`SELECT collection, path, hash FROM documents WHERE collection = ? AND active = 1 ORDER BY path`);
  const allRefs = db.prepare(`SELECT collection, path, hash FROM documents WHERE active = 1 ORDER BY collection, path`);

  return {
  dbPath: store.dbPath,
  search: async (o) => (await searchDetailed(o)).hits,
  searchDetailed,
  llmHealth,
  workers: () => remote?.pool.workers() ?? [],
  poolLoad: () => remote?.pool.load() ?? null,
  drainWorker: (name) => remote?.pool.drain(name) ?? null,
  enableWorker: (name) => remote?.pool.enable(name) ?? null,
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
    const scan: ScanRequest = { dbPath: store.dbPath, ...(o.collections ? { collections: o.collections } : {}), batchMs: SCAN_BATCH_MS, mainWrites: writes.writers, ...(o.headBytes === undefined ? {} : { headBytes: o.headBytes }) };
    const r = await scans.run<ScanResult>(scan, o.signal);
    for (const message of r.warnings) options.onLlmWarning?.(message);
    // Set-asides of documents no row holds any more go too, as their paths may be sensitive. They
    // live in the queue's file, which only this thread may hold, so this stays here: a lookup each.
    tidyLetters();
    return {
      collections: r.collections,
      indexed: r.indexed,
      updated: r.updated,
      unchanged: r.unchanged,
      removed: r.removed,
      skipped: r.skipped,
      needsEmbedding: Math.max(0, r.needsEmbedding - setAside()),
      ...(r.metadata ? { metadata: r.metadata } : {}),
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
  readDocumentHeads: (maxBytes: number): DocumentHead[] => readHeads(db, maxBytes),
  snippet: (body, query, maxLen, chunkPos, chunkLen, intent) => {
    const s = qmd.extractSnippet(body, query, maxLen, chunkPos, chunkLen, intent);
    return { line: s.line, snippet: s.snippet };
  },
  addLineNumbers: (text, startLine) => qmd.addLineNumbers(text, startLine),
  models: () => (remote ? remoteModelStatus(remote, llm as unknown as LlmSurface) : modelStatus(llm)),
  stopWorkerCalls: () => remote?.lifecycle.refuseNew(),
  abortWorkerCalls: () => remote?.lifecycle.abortInFlight(),
  close: async () => {
    // A scan still running stops at its next document, or is cut off: see ScanThreads.close.
    await scans.close(SCAN_CLOSE_GRACE_MS);
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
