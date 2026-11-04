import { readdirSync } from "node:fs";
import type { EmbedSummary } from "../jobs.ts";
import type { BackendEmbedOptions, BackendSearchOptions, LlmOptions, OpenBackendOptions, SearchHit, SearchOutcome } from "../store.ts";
import { searchDegradable, type QueryValidators } from "./degraded-search.ts";
import { runEmbedFarm, type FarmChunk, type FarmDeps, type PendingDoc } from "./embed-farm.ts";
import { acceptedEmbedFiles, RemoteLlm } from "./remote.ts";
import type { LlmSurface, LocalFallback, ProbeReference, ProvenanceStore } from "./types.ts";

/**
 * Connects `RemoteLlm` and the embedding farm to the SDK's store. Everything here
 * is loaded only in remote mode. The SDK itself is reached only through the handles
 * `src/store.ts` passes in, which keeps that the one file importing it.
 */

interface Statement {
  all(...args: unknown[]): unknown[];
  get(...args: unknown[]): unknown;
  run(...args: unknown[]): { changes: number | bigint };
}

export interface SdkDb {
  prepare(sql: string): Statement;
  exec(sql: string): void;
}

export interface SdkHandles {
  db: SdkDb;
  local: LlmSurface;
  validators: QueryValidators;
  insertEmbedding(hash: string, seq: number, pos: number, embedding: Float32Array, model: string, at: string, total: number, fingerprint: string): void;
  ensureVecTable(dimensions: number): void;
  /** The SDK migrates content_vectors lazily; this brings the columns the farm's query needs. */
  migrateVectors(model: string): void;
  fingerprint(model: string): string;
  chunk(body: string, path: string, signal: AbortSignal): Promise<Array<{ text: string; pos: number }>>;
  format(text: string, title: string, model: string): string;
  title(body: string, path: string): string;
  clearVectors(): void;
  modelCacheDir: string;
  /** How much a fallback loading on this host matters is decided in `store.ts`. */
  setDefaultLlm(llm: unknown): void;
}

export type RemoteLlmConfig = Extract<LlmOptions, { mode: "remote" }>;

/** A cached rerank score or expansion outlives its worker by at most this. */
const WORKER_CACHE_TTL_MS = 7 * 24 * 60 * 60 * 1000;
const CACHE_SWEEP_MS = 60 * 60 * 1000;
const DEFAULT_EMBED_BATCH_BYTES = 256 * 1024;
const EMBED_BATCH_CHUNKS = 32;

/** Mirrors the SDK's private getPendingEmbeddingDocs, with a way to include stale documents; the surface test pins it. */
const PENDING_DOCS = `
  SELECT d.hash, MIN(d.path) as path, length(CAST(c.doc AS BLOB)) as bytes
  FROM documents d
  JOIN content c ON d.hash = c.hash
  LEFT JOIN (
    SELECT hash, model, COUNT(*) AS chunk_count, MAX(total_chunks) AS expected_chunks, MAX(embedded_at) AS last_embedded
    FROM content_vectors
    WHERE model = ? AND embed_fingerprint = ?
    GROUP BY hash, model, embed_fingerprint
  ) v ON d.hash = v.hash
  WHERE d.active = 1
    AND (v.hash IS NULL OR v.chunk_count < v.expected_chunks OR (? IS NOT NULL AND v.last_embedded < ?))
    AND (? IS NULL OR d.collection = ?)
  GROUP BY d.hash
  ORDER BY MIN(d.path)
`;

/** Length of the vectors the index holds, or null if it has no vector table. */
export function indexDimensions(db: SdkDb): number | null {
  const row = db.prepare("SELECT sql FROM sqlite_master WHERE type='table' AND name='vectors_vec'").get() as { sql?: string } | undefined;
  const match = /float\[(\d+)\]/.exec(row?.sql ?? "");
  return match ? Number(match[1]) : null;
}

function inTransaction(db: SdkDb, work: () => void): void {
  db.exec("BEGIN IMMEDIATE");
  try {
    work();
    db.exec("COMMIT");
  } catch (err) {
    db.exec("ROLLBACK");
    throw err;
  }
}

function deleteVectors(db: SdkDb, hash: string): void {
  const hasTable = indexDimensions(db) !== null;
  if (hasTable) {
    const remove = db.prepare("DELETE FROM vectors_vec WHERE hash_seq = ?");
    for (const row of db.prepare("SELECT seq FROM content_vectors WHERE hash = ?").all(hash) as Array<{ seq: number }>) remove.run(`${hash}_${row.seq}`);
  }
  db.prepare("DELETE FROM content_vectors WHERE hash = ?").run(hash);
}

/**
 * The SDK writes two tables with no transaction, so a kill between them would
 * leave a row claiming a vector that does not exist. Here a document's vectors
 * replace its old ones in one transaction.
 */
export function replaceDocumentVectors(handles: SdkHandles, model: string, fingerprint: string, hash: string, chunks: readonly FarmChunk[], total: number): void {
  inTransaction(handles.db, () => {
    if (indexDimensions(handles.db) === null) handles.ensureVecTable(chunks[0]!.vector.length);
    deleteVectors(handles.db, hash);
    const at = new Date().toISOString();
    for (const chunk of chunks) handles.insertEmbedding(hash, chunk.seq, chunk.pos, new Float32Array(chunk.vector), model, at, total, fingerprint);
  });
}

/**
 * The SDK's model loader downloads anything it cannot find. A sidecar host with no
 * model must fail fast and wait for a worker, not fetch gigabytes from the internet
 * in the middle of an outage. So the fallback only works if the file is already here.
 */
export function guardedFallback(local: LocalFallback, cacheDir: string, modelUri: string, list: (dir: string) => string[] = readdirSync): LocalFallback {
  const accepted = new Set(acceptedEmbedFiles(modelUri));
  const present = (): boolean => {
    try {
      return list(cacheDir).some((name) => accepted.has(name.toLowerCase()));
    } catch {
      return false;
    }
  };
  const guard = <T>(run: () => Promise<T>): Promise<T> => (present() ? run() : Promise.reject(new Error("The embedding model is not on this host")));
  return {
    embedBatch: (texts) => guard(() => local.embedBatch(texts)),
    tokenize: (text) => guard(() => local.tokenize(text)),
    detokenize: (tokens) => guard(() => local.detokenize(tokens)),
  };
}

/** A failing provenance write must not lose a vector that is already safely written. */
function safely(work: () => void, onError: (message: string) => void): void {
  try {
    work();
  } catch (err) {
    onError(err instanceof Error ? err.message : "provenance store failure");
  }
}

function probeReferenceFrom(store: ProvenanceStore | undefined): { get(): ProbeReference | null; set(reference: ProbeReference): void } {
  let held: ProbeReference | null = null;
  return {
    get: () => {
      if (held) return held;
      try {
        held = store?.probeReference() ?? null;
      } catch {
        held = null;
      }
      return held;
    },
    set: (reference) => {
      held = reference;
      safely(() => store?.setProbeReference(reference), () => {});
    },
  };
}

export interface WiredRemote {
  llm: RemoteLlm;
  embed(options: BackendEmbedOptions): Promise<EmbedSummary>;
  search(run: (options: BackendSearchOptions) => Promise<SearchHit[]>, options: BackendSearchOptions): Promise<SearchOutcome>;
  /** Remove every vector one worker wrote, so those documents embed again. */
  purgeWorkerVectors(worker: string): number;
  close(): void;
}

function farmDeps(handles: SdkHandles, llm: RemoteLlm, provenance: ProvenanceStore | undefined, model: string, fingerprint: string, onError: (message: string) => void): FarmDeps {
  const { db } = handles;
  const pending = (collection: string | undefined, redoBefore: string | undefined): PendingDoc[] => {
    handles.migrateVectors(model);
    const c = collection ?? null;
    const before = redoBefore ?? null;
    // Prepared here, after the migration: on an older index these columns do not exist before it.
    return db.prepare(PENDING_DOCS).all(model, fingerprint, before, before, c, c) as PendingDoc[];
  };
  return {
    canEmbed: () => llm.canServe("embed"),
    capacity: () => llm.pool.backgroundCapacity("embed"),
    pendingDocs: pending,
    readBody: (hash) => (db.prepare("SELECT doc FROM content WHERE hash = ?").get(hash) as { doc?: string } | undefined)?.doc ?? "",
    chunk: handles.chunk,
    format: (text, body, path) => handles.format(text, handles.title(body, path), model),
    vectorDimensions: () => indexDimensions(db),
    resetVectors: handles.clearVectors,
    replaceDocument: (hash, chunks, total, workers) => {
      replaceDocumentVectors(handles, model, fingerprint, hash, chunks, total);
      safely(() => provenance?.recordDocument(hash, workers), onError);
    },
    embed: (texts) => llm.embedDocuments(texts),
    sleep: (ms) =>
      new Promise((resolve) => {
        setTimeout(resolve, ms).unref();
      }),
  };
}

function sweepExpiredCache(db: SdkDb): void {
  db.prepare("DELETE FROM llm_cache WHERE created_at < ?").run(new Date(Date.now() - WORKER_CACHE_TTL_MS).toISOString());
}

export function wireRemote(handles: SdkHandles, config: RemoteLlmConfig, options: OpenBackendOptions): WiredRemote {
  const { db, local } = handles;
  const model = local.embedModelName;
  const fingerprint = handles.fingerprint(model);
  const provenance = options.provenance;
  const onError = options.onProvenanceError ?? (() => {});
  const llm = new RemoteLlm({
    workers: config.workers,
    embedDimensions: config.embedDimensions,
    models: { embed: model, rerank: local.rerankModelName, generate: local.generateModelName },
    // The SDK's own instance. It loads nothing until a fallback first uses it.
    fallback: config.fallback ? guardedFallback(local, handles.modelCacheDir, model) : null,
    statusPollMs: config.statusPollMs ?? 10_000,
    indexDimensions: () => indexDimensions(db),
    probeReference: probeReferenceFrom(provenance),
    ...(config.probeEveryMs !== undefined ? { probeEveryMs: config.probeEveryMs } : {}),
    ...(config.rerankTimeoutMs !== undefined ? { timeouts: { rerank: config.rerankTimeoutMs } } : {}),
    ...(config.breaker ? { breaker: config.breaker } : {}),
    ...(options.onLlmCall ? { onCall: options.onLlmCall } : {}),
    ...(options.onLlmDegraded ? { onDegraded: options.onLlmDegraded } : {}),
  });
  // The chunker ignores store.llm and asks for this process-wide singleton, so
  // embedding documents would otherwise load a model on this host just to count tokens.
  handles.setDefaultLlm(llm);
  sweepExpiredCache(db);
  const sweep = setInterval(() => sweepExpiredCache(db), CACHE_SWEEP_MS);
  sweep.unref();
  const deps = farmDeps(handles, llm, provenance, model, fingerprint, onError);
  return {
    llm,
    embed: (o) =>
      runEmbedFarm(deps, {
        force: o.force,
        collection: o.collection,
        forceSince: o.forceSince,
        signal: o.signal ?? new AbortController().signal,
        onWorker: o.onWorker ?? (() => {}),
        ...(o.onProgress ? { onProgress: o.onProgress } : {}),
        maxBatchBytes: config.embedBatchBytes ?? DEFAULT_EMBED_BATCH_BYTES,
        maxBatchChunks: EMBED_BATCH_CHUNKS,
      }),
    search: (run, o) => searchDegradable(llm, handles.validators, run, o),
    purgeWorkerVectors: (worker) => purgeWorker(handles, provenance, worker),
    close: () => {
      clearInterval(sweep);
      llm.pool.stop();
      handles.setDefaultLlm(null);
    },
  };
}

function purgeWorker(handles: SdkHandles, provenance: ProvenanceStore | undefined, worker: string): number {
  if (!provenance) return 0;
  const hashes = provenance.documentsBy(worker);
  inTransaction(handles.db, () => {
    for (const hash of hashes) deleteVectors(handles.db, hash);
  });
  provenance.forgetDocuments(hashes);
  if (provenance.probeReference()?.worker === worker) provenance.setProbeReference(null);
  return hashes.length;
}
