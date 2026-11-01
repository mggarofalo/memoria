import { existsSync } from "node:fs";
import type { DocumentHead, MetadataSource } from "./metadata.ts";
import type { EmbedSummary, UpdateSummary } from "./jobs.ts";

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

export interface OpenBackendOptions {
  dbPath: string | undefined;
  keepWarm: boolean;
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
export async function openBackend(options: OpenBackendOptions): Promise<QmdBackend> {
  const qmd = await import("@tobilu/qmd");
  const dbPath = options.dbPath ?? qmd.getDefaultDbPath();
  if (!existsSync(dbPath)) {
    throw new Error(`QMD index not found at ${dbPath}. Run \`qmd update\` on this host first, or set MEMORIA_QMD_DB_PATH.`);
  }
  // DB-only mode: collections and contexts come from store_collections inside
  // index.sqlite, which `qmd` keeps in sync with ~/.config/qmd/index.yml.
  const store = await qmd.createStore({ dbPath });
  // LlamaCpp keeps its model handles private; we only read them for status.
  const llm = (store.internal as unknown as { llm?: LlamaLike }).llm;
  if (options.keepWarm && llm) {
    // The SDK hardcodes a five-minute idle unload. Zero disables the timer so
    // the ~11s cold start is paid once per process, not once per lull.
    llm.inactivityTimeoutMs = 0;
  }
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
    models: () => modelStatus(llm),
    close: () => store.close(),
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
