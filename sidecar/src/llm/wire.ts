import { existsSync, readdirSync } from "node:fs";
import { documentHolds, moveLegacyLetters, type DeadLetterBook, type DeadLetterRow } from "../dead-letters.ts";
import type { EmbedSummary } from "../jobs.ts";
import { isBusy, withShortBusyTimeout } from "../sqlite-busy.ts";
import type { BackendEmbedOptions, BackendSearchOptions, LlmOptions, OpenBackendOptions, SearchHit, SearchOutcome } from "../store.ts";
import { searchDegradable, type QueryValidators } from "./degraded-search.ts";
import { IndexBusy, runEmbedFarm, type FarmChunk, type FarmDeps, type PendingDoc } from "./embed-farm.ts";
import { RemoteLlm } from "./remote.ts";
import type { DeadLetter, LlmSurface, LocalFallback } from "./types.ts";

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
  exec(sql: string): unknown;
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
  /** The texts `chunk` will ask to tokenize first, before any re-splitting. */
  firstPassTexts(body: string, path: string): Promise<string[]>;
  format(text: string, title: string, model: string): string;
  title(body: string, path: string): string;
  clearVectors(): void;
  modelCacheDir: string;
  setDefaultLlm(llm: unknown): void;
}

export type RemoteLlmConfig = Extract<LlmOptions, { mode: "remote" }>;

/** A cached rerank score or expansion outlives its worker by at most this. */
const WORKER_CACHE_TTL_MS = 7 * 24 * 60 * 60 * 1000;
const CACHE_SWEEP_MS = 60 * 60 * 1000;
const DEFAULT_EMBED_BATCH_BYTES = 256 * 1024;
const EMBED_BATCH_CHUNKS = 32;

/** A refusal can be the worker's doing. So it holds only for this set of workers, and not for long. */
const DEAD_LETTER_RETRY_MS = 24 * 60 * 60 * 1000;

/**
 * Mirrors the SDK's private getPendingEmbeddingDocs, plus a force job's stale documents
 * and minus what has been dead-lettered, which a force job retries. A time ahead of now
 * means the clock moved back since it was written, so it says nothing about order and is
 * redone. The surface test pins this.
 */
const PENDING_DOCS = `
  SELECT g.hash, g.path, g.bytes FROM (
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
    AND (v.hash IS NULL OR v.chunk_count < v.expected_chunks OR (? IS NOT NULL AND (v.last_embedded <= ? OR v.last_embedded > ?)))
    AND (? IS NULL OR d.collection = ?)
  GROUP BY d.hash
  ) g
  WHERE ? IS NOT NULL OR NOT EXISTS (SELECT 1 FROM json_each(?) l WHERE json_extract(l.value, '$[0]') = g.hash AND json_extract(l.value, '$[1]') = g.path)
  ORDER BY g.path
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

/** A write the farm can put off: it does not wait on a lock, and tells the farm so with `IndexBusy`. */
function politeWrite<T>(db: SdkDb, work: () => T): T {
  try {
    return withShortBusyTimeout(db, work);
  } catch (err) {
    if (isBusy(err)) throw new IndexBusy();
    throw err;
  }
}

/** A document's vectors, or with `fingerprint` only those carrying it. */
function deleteVectors(db: SdkDb, hash: string, fingerprint?: string): void {
  const only = fingerprint === undefined ? "" : " AND embed_fingerprint = ?";
  const args = fingerprint === undefined ? [hash] : [hash, fingerprint];
  if (indexDimensions(db) !== null) {
    const remove = db.prepare("DELETE FROM vectors_vec WHERE hash_seq = ?");
    for (const row of db.prepare(`SELECT seq FROM content_vectors WHERE hash = ?${only}`).all(...args) as Array<{ seq: number }>) remove.run(`${hash}_${row.seq}`);
  }
  db.prepare(`DELETE FROM content_vectors WHERE hash = ?${only}`).run(...args);
}

/**
 * The SDK writes two tables with no transaction, so a kill between them would
 * leave a row claiming a vector that does not exist. Here a document's vectors
 * replace its old ones in one transaction. Another process holding the index is not
 * a failure, and waiting for it would freeze the event loop, so that defers the job.
 */
export function replaceDocumentVectors(handles: SdkHandles, model: string, fingerprint: string, hash: string, chunks: readonly FarmChunk[], total: number): void {
  politeWrite(handles.db, () =>
    inTransaction(handles.db, () => {
      if (indexDimensions(handles.db) === null) handles.ensureVecTable(chunks[0]!.vector.length);
      deleteVectors(handles.db, hash);
      const at = new Date().toISOString();
      for (const chunk of chunks) handles.insertEmbedding(hash, chunk.seq, chunk.pos, new Float32Array(chunk.vector), model, at, total, fingerprint);
    }),
  );
}

/**
 * The name the SDK's model loader looks for in its cache. Anything else, including a
 * file with the plain model name, sends it to Hugging Face. Mirrors node-llama-cpp's
 * `buildHuggingFaceFilePrefix`; a test holds it to the real function.
 */
export function sdkCacheFileName(uri: string): string | null {
  const parts = /^hf:([^/]+)\/([^/]+)\/([^/]+)$/.exec(uri);
  if (!parts) return null;
  const [, user, repo, file] = parts as unknown as [string, string, string, string];
  const stem = /-gguf$/i.test(repo) ? repo.slice(0, -"-gguf".length).toLowerCase() : null;
  const repoInName = stem === null || !file.toLowerCase().startsWith(stem);
  return repoInName ? `hf_${user}_${repo}_${file}` : `hf_${user}_${file}`;
}

/**
 * The SDK's model loader downloads anything it cannot find. A sidecar host with no
 * model must fail fast and wait for a worker, not fetch gigabytes from the internet
 * in the middle of an outage. So the fallback only works if the file is already here.
 */
export function guardedFallback(local: LocalFallback, cacheDir: string, modelUri: string, list: (dir: string) => string[] = readdirSync): LocalFallback {
  const cached = sdkCacheFileName(modelUri);
  const present = (): boolean => {
    // Two letters or more, because a Windows path starts with a drive letter and a colon.
    if (cached === null) return !/^[a-z][a-z0-9+.-]+:/i.test(modelUri) && existsSync(modelUri);
    try {
      return list(cacheDir).includes(cached);
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

export interface WiredRemote {
  llm: RemoteLlm;
  embed(options: BackendEmbedOptions): Promise<EmbedSummary>;
  search(run: (options: BackendSearchOptions) => Promise<SearchHit[]>, options: BackendSearchOptions): Promise<SearchOutcome>;
  deadLetters(): DeadLetter[];
  clearDeadLetters(): number;
  close(): void;
}

/** Vectors of documents gone from the index. Only a full force job can see all of them as stale. */
function removeOrphanVectors(db: SdkDb): void {
  politeWrite(db, () =>
    inTransaction(db, () => {
      const orphans = db.prepare("SELECT DISTINCT hash FROM content_vectors WHERE hash NOT IN (SELECT hash FROM documents WHERE active = 1)").all() as Array<{ hash: string }>;
      for (const { hash } of orphans) deleteVectors(db, hash);
    }),
  );
}

/** What a set-aside holds for: a change of worker, address or model is a new chance. */
function workerSetOf(llm: RemoteLlm, fingerprint: string): string {
  return `${fingerprint}:${llm.pool.cacheIdentity("embed")}`;
}

/** Holds for this worker set, this model, and under a day. A time ahead of now is from a clock moved back, so it says nothing. */
function heldLetters(letters: DeadLetterBook, llm: RemoteLlm, fingerprint: string): DeadLetterRow[] {
  const now = Date.now();
  const set = workerSetOf(llm, fingerprint);
  const since = new Date(now - DEAD_LETTER_RETRY_MS).toISOString();
  const until = new Date(now).toISOString();
  return letters.all().filter((l) => l.workerSet === set && l.at > since && l.at <= until);
}

/** Every free background slot, or fewer when the operator caps it. */
export function embedCapacity(slots: number, cap: number | undefined): number {
  return cap ? Math.min(slots, cap) : slots;
}

function farmDeps(handles: SdkHandles, llm: RemoteLlm, model: string, fingerprint: string, cap: number | undefined, letters: DeadLetterBook): FarmDeps {
  const { db } = handles;
  const pending = (collection: string | undefined, redoBefore: string | undefined): PendingDoc[] => {
    politeWrite(db, () => handles.migrateVectors(model));
    moveLegacyLetters(db, letters);
    letters.retain(documentHolds(db, true));
    const c = collection ?? null;
    const before = redoBefore ?? null;
    const held = JSON.stringify(heldLetters(letters, llm, fingerprint).map((l) => [l.hash, l.path]));
    // Prepared here, after the migration: on an older index these columns do not exist before it.
    return db.prepare(PENDING_DOCS).all(model, fingerprint, before, before, new Date().toISOString(), c, c, before, held) as PendingDoc[];
  };
  return {
    canEmbed: () => llm.canServe("embed", "background"),
    confirmWorkers: () => llm.pool.confirmDocuments(),
    acceptsFullChunk: () => llm.acceptsFullChunk(),
    capacity: () => embedCapacity(llm.pool.backgroundCapacity("embed"), cap),
    pendingDocs: pending,
    readBody: (hash) => (db.prepare("SELECT doc FROM content WHERE hash = ?").get(hash) as { doc?: string } | undefined)?.doc ?? "",
    chunk: async (body, path, signal) => {
      await llm.primeTokens(await handles.firstPassTexts(body, path));
      try {
        return await handles.chunk(body, path, signal);
      } finally {
        llm.forgetPrimedTokens();
      }
    },
    format: (text, body, path) => handles.format(text, handles.title(body, path), model),
    vectorDimensions: () => indexDimensions(db),
    resetVectors: () => politeWrite(db, handles.clearVectors),
    replaceDocument: (hash, chunks, total) => {
      // First, as the two are in different files: should the write then fail, the document is
      // only pending again, where the other order could leave it set aside with fresh vectors.
      letters.removeHash(hash);
      replaceDocumentVectors(handles, model, fingerprint, hash, chunks, total);
    },
    deadLetter: (hash, path, reason) => {
      // During a force job this document may hold vectors from the worker being replaced.
      // Those carry this fingerprint. Vectors under another, such as stock QMD's, no
      // worker of this build made, so they keep serving search until it embeds. They go
      // first: a busy index then sets nothing aside, and the job tries again later.
      politeWrite(db, () => inTransaction(db, () => deleteVectors(db, hash, fingerprint)));
      letters.put({ hash, path, reason, at: new Date().toISOString(), workerSet: workerSetOf(llm, fingerprint) });
    },
    setAsideCount: () => heldLetters(letters, llm, fingerprint).length,
    documentCount: () => (db.prepare("SELECT COUNT(DISTINCT hash) AS n FROM documents WHERE active = 1").get() as { n: number }).n,
    recentFailures: new Map(),
    removeOrphans: () => removeOrphanVectors(db),
    embed: (texts) => llm.embedDocuments(texts),
    sleep: (ms) =>
      new Promise((resolve) => {
        setTimeout(resolve, ms).unref();
      }),
  };
}

/** Housekeeping: it can run next hour, so it must never crash the process or wait on a lock. */
export function sweepExpiredCache(db: SdkDb, onWarning: (message: string) => void): void {
  try {
    withShortBusyTimeout(db, () => db.prepare("DELETE FROM llm_cache WHERE created_at < ?").run(new Date(Date.now() - WORKER_CACHE_TTL_MS).toISOString()));
  } catch (err) {
    onWarning(`Could not sweep expired cache entries, and will try again: ${err instanceof Error ? err.message : "unknown error"}`);
  }
}

export function wireRemote(handles: SdkHandles, config: RemoteLlmConfig, options: OpenBackendOptions, letters: DeadLetterBook): WiredRemote {
  const { db, local } = handles;
  const model = local.embedModelName;
  const fingerprint = handles.fingerprint(model);
  const warn = options.onLlmWarning ?? (() => {});
  const timeouts = config.rerankTimeoutMs !== undefined ? { timeouts: { rerank: config.rerankTimeoutMs } } : {};
  const llm = new RemoteLlm({
    workers: config.workers,
    embedDimensions: config.embedDimensions,
    models: { embed: model, rerank: local.rerankModelName, generate: local.generateModelName },
    // The SDK's own instance. It loads nothing until a fallback first uses it.
    fallback: config.fallback ? guardedFallback(local, handles.modelCacheDir, model) : null,
    statusPollMs: config.statusPollMs ?? 10_000,
    indexDimensions: () => indexDimensions(db),
    onDisagreement: (workers) => warn(workers.length > 0 ? `Workers ${workers.join(", ")} embedded the same text differently, so their vectors may not be comparable. Check they run the same model build.` : "Workers embed alike again."),
    onModelMismatch: (worker, role, reported) => warn(`Worker ${worker} serves ${reported ?? "no model"} for ${role}, not the configured ${role} model, so it gets no ${role} work until it does.`),
    ...timeouts,
    ...optionalRemote(config, options),
  });
  // The chunker ignores store.llm and asks for this process-wide singleton, so
  // embedding documents would otherwise load a model on this host just to count tokens.
  handles.setDefaultLlm(llm);
  sweepExpiredCache(db, warn);
  const sweep = setInterval(() => sweepExpiredCache(db, warn), CACHE_SWEEP_MS);
  sweep.unref();
  const deps = farmDeps(handles, llm, model, fingerprint, config.embedConcurrency, letters);
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
    deadLetters: () =>
      heldLetters(letters, llm, fingerprint)
        .sort((a, b) => (a.at < b.at ? -1 : a.at > b.at ? 1 : 0))
        .map((l): DeadLetter => ({ hash: l.hash, path: l.path, reason: l.reason, at: l.at })),
    clearDeadLetters: () => letters.clear(),
    close: () => {
      clearInterval(sweep);
      llm.pool.stop();
      handles.setDefaultLlm(null);
    },
  };
}

function optionalRemote(config: RemoteLlmConfig, options: OpenBackendOptions) {
  return Object.fromEntries(
    Object.entries({ probeEveryMs: config.probeEveryMs, breaker: config.breaker, interactiveReserve: config.interactiveReserve, searchDeadlineMs: config.searchDeadlineMs, onCall: options.onLlmCall, onDegraded: options.onLlmDegraded }).filter(([, value]) => value !== undefined),
  );
}
