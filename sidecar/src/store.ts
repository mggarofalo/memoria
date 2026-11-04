import { existsSync } from "node:fs";
import type { DocumentHead, MetadataSource } from "./metadata.ts";
import type { EmbedSummary, UpdateSummary } from "./jobs.ts";
import type { EmbeddingResult, LlmCallSink, LlmSurface, Queryable, RerankDocument, RerankResult, WorkerOptions } from "./llm/types.ts";

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

export interface QmdBackend extends MetadataSource {
  readonly dbPath: string;
  search(options: BackendSearchOptions): Promise<SearchHit[]>;
  /** Resolve a path, `qmd://` URI, or docid to document metadata (no body). */
  get(reference: string): Promise<DocumentInfo | DocumentLookupError>;
  getDocumentBody(filepath: string, options: { fromLine?: number; maxLines?: number }): Promise<string | null>;
  multiGet(pattern: string, options: { includeBody: boolean; maxBytes: number }): Promise<{ docs: MultiGetEntry[]; errors: string[] }>;
  listCollections(): Promise<CollectionRow[]>;
  listContexts(): Promise<ContextRow[]>;
  getDefaultCollectionNames(): Promise<string[]>;
  update(options: { collections?: string[] }): Promise<UpdateSummary>;
  embed(options: { force: boolean; collection?: string }): Promise<EmbedSummary>;
  getStatus(): Promise<IndexStatusInfo>;
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
export type LlmOptions = { mode: "local" } | { mode: "remote"; worker: WorkerOptions; fallback: boolean; statusPollMs?: number };

export interface OpenBackendOptions {
  dbPath: string | undefined;
  keepWarm: boolean;
  llm?: LlmOptions;
  /** Receives one structured entry per worker call. */
  onLlmCall?: LlmCallSink;
  onLlmDegraded?: (entry: { operation: string; error: string; fallback: boolean }) => void;
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
}

export interface OpenedBackend extends QmdBackend {
  readonly llm: LlmProbe;
}

/** `dist/llm.js` exports the SDK does not re-export from its entry point. */
interface SdkLlmModule {
  setDefaultLlamaCpp(llm: unknown): void;
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

/**
 * Open the real QMD store. The index must already exist — the sidecar serves
 * an index the host maintains with `qmd`, it does not create one.
 */
export async function openBackend(options: OpenBackendOptions): Promise<OpenedBackend> {
  const llmOptions = options.llm ?? { mode: "local" as const };
  if (llmOptions.mode === "remote" && llmOptions.fallback) {
    // Degraded mode must not compete with a worker for this host's GPU. The SDK
    // reads this when it first loads a model, which may be never. Process-wide:
    // only set when a fallback can load, so the parity benchmark, which runs
    // both modes in one process with the fallback off, keeps its local side's GPU.
    process.env.QMD_FORCE_CPU ??= "1";
  }
  const qmd = await import("@tobilu/qmd");
  const dbPath = options.dbPath ?? qmd.getDefaultDbPath();
  if (!existsSync(dbPath)) {
    throw new Error(`QMD index not found at ${dbPath}. Run \`qmd update\` on this host first, or set MEMORIA_QMD_DB_PATH.`);
  }
  // DB-only mode: collections and contexts come from store_collections inside
  // index.sqlite, which `qmd` keeps in sync with ~/.config/qmd/index.yml.
  const store = await qmd.createStore({ dbPath });
  // LlamaCpp keeps its model handles private; we only read them for status.
  const internal = store.internal as unknown as { llm?: LlamaLike };
  const llm = internal.llm;
  if (options.keepWarm && llm && llmOptions.mode === "local") {
    // The SDK hardcodes a five-minute idle unload. Zero disables the timer so
    // the ~11s cold start is paid once per process, not once per lull.
    llm.inactivityTimeoutMs = 0;
  }
  let remote: import("./llm/remote.ts").RemoteLlm | undefined;
  let sdkLlmModule: SdkLlmModule | undefined;
  if (llmOptions.mode === "remote") {
    const { RemoteLlm } = await import("./llm/remote.ts");
    const local = llm as unknown as LlmSurface;
    remote = new RemoteLlm({
      worker: llmOptions.worker,
      models: { embed: local.embedModelName, rerank: local.rerankModelName, generate: local.generateModelName },
      // The SDK's own instance. It loads nothing until a fallback first uses it.
      fallback: llmOptions.fallback ? local : null,
      statusPollMs: llmOptions.statusPollMs ?? 30_000,
      ...(options.onLlmCall ? { onCall: options.onLlmCall } : {}),
      ...(options.onLlmDegraded ? { onDegraded: options.onLlmDegraded } : {}),
    });
    internal.llm = remote as unknown as LlamaLike;
    // The chunker ignores store.llm and asks for this process-wide singleton, so
    // embedding documents would otherwise load a model on this host just to
    // count tokens. The SDK's exports map hides the setter; a file URL reaches
    // the same module instance. The surface test pins this for the exact version.
    const sdkEntry = import.meta.resolve("@tobilu/qmd");
    sdkLlmModule = (await import(new URL("./llm.js", sdkEntry).href)) as SdkLlmModule;
    if (typeof sdkLlmModule.setDefaultLlamaCpp !== "function") {
      throw new Error("This QMD version has no setDefaultLlamaCpp; remote LLM mode needs updating for it.");
    }
    sdkLlmModule.setDefaultLlamaCpp(remote);
  }
  const active = (remote ?? llm) as unknown as LlmSurface;
  const db = store.internal.db;
  const headsStatement = db.prepare(
    `SELECT d.collection AS collection, d.path AS path, substr(c.doc, 1, ?) AS head
     FROM documents d JOIN content c ON c.hash = d.hash
     WHERE d.active = 1`,
  );

  return {
    dbPath: store.dbPath,
    search: async (o) => {
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
    },
    get: (reference) => store.get(reference, { includeBody: false }),
    getDocumentBody: (filepath, o) => store.getDocumentBody(filepath, o),
    multiGet: (pattern, o) => store.multiGet(pattern, o),
    listCollections: () => store.listCollections(),
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
        needsEmbedding: r.needsEmbedding,
      };
    },
    embed: async (o) => {
      const r = await store.embed({ force: o.force, ...(o.collection !== undefined ? { collection: o.collection } : {}) });
      return { docsProcessed: r.docsProcessed, chunksEmbedded: r.chunksEmbedded, errors: r.errors, durationMs: r.durationMs };
    },
    getStatus: () => store.getStatus(),
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
      if (remote) {
        await remote.dispose();
        sdkLlmModule?.setDefaultLlamaCpp(null);
      }
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
    },
  };
}

function remoteModelStatus(remote: import("./llm/remote.ts").RemoteLlm, local: LlmSurface): ModelStatus {
  const loaded = remote.status();
  return {
    embed: local.embedModelName,
    rerank: local.rerankModelName,
    generate: local.generateModelName,
    loaded,
    resident: loaded.embed && loaded.rerank && loaded.generate,
  };
}

function modelStatus(llm: LlamaLike | undefined): ModelStatus {
  const loaded = {
    embed: !!llm?.embedModel,
    rerank: !!llm?.rerankModel,
    generate: !!llm?.generateModel,
  };
  return {
    embed: llm?.embedModelName ?? "",
    rerank: llm?.rerankModelName ?? "",
    generate: llm?.generateModelName ?? "",
    loaded,
    resident: loaded.embed && loaded.rerank && loaded.generate,
  };
}
