import { existsSync, renameSync } from "node:fs";
import { DatabaseSync } from "node:sqlite";
import type { Job, JobStore, PersistedJob } from "./jobs.ts";
import type { ProbeReference, ProvenanceStore } from "./llm/types.ts";

/**
 * Jobs in their own SQLite file beside the index, never inside QMD's schema, so
 * deleting and rebuilding `index.sqlite` leaves the queue alone and QMD upgrades
 * cannot collide with it. Uses Node's built-in SQLite, which adds no dependency.
 *
 * A row holds a job's request, timestamps and result counts. It never holds
 * document content or credentials: the reason names a file path at most, and
 * errors are built from fixed text (src/llm/client.ts).
 *
 * The same file records which worker embedded which document. That sits outside
 * QMD's schema for the same reason, and lets one worker's vectors be removed.
 *
 * This file only holds job records, so it must never stop search: a bad row is
 * set aside, and `openJobStore` moves a corrupt file out of the way.
 */

const SCHEMA_VERSION = 1;
const JOB_STATES: ReadonlySet<string> = new Set(["queued", "running", "succeeded", "failed"]);

/** The default location: beside the index, under a name of its own. */
export function queueDbPathFor(indexPath: string): string {
  return `${indexPath}.queue.sqlite`;
}

interface Row {
  id: string;
  seq: number;
  state: string;
  priority: string;
  attempts: number;
  worker: string | null;
  collections: string | null;
  embed: number;
  force: number;
  reason: string;
  base_reason: string;
  merged_requests: number;
  created_at: string;
  started_at: string | null;
  finished_at: string | null;
  due_at: number | null;
  first_request_at: number | null;
  deferred: number;
  deferrals: number;
  crashes: number;
  result: string | null;
  error: string | null;
}

function jobFromRow(row: Row): PersistedJob {
  if (!JOB_STATES.has(row.state)) throw new Error(`unknown state ${row.state}`);
  const job: Job = {
    id: row.id,
    state: row.state as Job["state"],
    request: {
      collections: row.collections === null ? null : (JSON.parse(row.collections) as string[]),
      embed: row.embed === 1,
      force: row.force === 1,
      reason: row.reason,
    },
    createdAt: row.created_at,
    startedAt: row.started_at,
    finishedAt: row.finished_at,
    result: row.result === null ? null : (JSON.parse(row.result) as Job["result"]),
    error: row.error,
    priority: "background",
    attempts: row.attempts,
    worker: row.worker,
  };
  return {
    job,
    seq: row.seq,
    dueAt: row.due_at,
    firstRequestAt: row.first_request_at,
    deferred: row.deferred === 1,
    deferrals: row.deferrals,
    crashes: row.crashes,
    baseReason: row.base_reason,
    mergedRequests: row.merged_requests,
  };
}

export class SqliteJobStore implements JobStore, ProvenanceStore {
  readonly #db: DatabaseSync;
  readonly #upsert: ReturnType<DatabaseSync["prepare"]>;
  readonly #delete: ReturnType<DatabaseSync["prepare"]>;
  /** Rows set aside by the last `load`. */
  quarantined = 0;

  constructor(path: string) {
    this.#db = new DatabaseSync(path);
    try {
      this.#prepareFile(path);
    } catch (err) {
      // Left open, Windows would refuse to move the damaged file out of the way.
      this.#db.close();
      throw err;
    }
    this.#upsert = this.#db.prepare(`
      INSERT OR REPLACE INTO jobs (id, seq, state, priority, attempts, worker, collections, embed, force, reason, base_reason, merged_requests, created_at, started_at, finished_at, due_at, first_request_at, deferred, deferrals, crashes, result, error)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    `);
    this.#delete = this.#db.prepare("DELETE FROM jobs WHERE id = ?");
  }

  #prepareFile(path: string): void {
    this.#db.exec("PRAGMA journal_mode = WAL");
    // A job acknowledged with a 202 has to survive the machine going down.
    this.#db.exec("PRAGMA synchronous = FULL");
    // Synchronous calls run on the event loop, so waiting on a lock would freeze
    // the sidecar. Nothing else should hold this file; if something does, the
    // write fails at once and the queue carries on in memory.
    this.#db.exec("PRAGMA busy_timeout = 0");
    const { user_version: version } = this.#db.prepare("PRAGMA user_version").get() as { user_version: number };
    if (version > SCHEMA_VERSION) throw new Error(`The job queue at ${path} is from a newer version of memoria-qmd.`);
    this.#createTables();
    this.#db.exec(`PRAGMA user_version = ${SCHEMA_VERSION}`);
  }

  #createTables(): void {
    this.#db.exec(`
      CREATE TABLE IF NOT EXISTS jobs (
        id TEXT PRIMARY KEY,
        seq INTEGER NOT NULL,
        state TEXT NOT NULL,
        priority TEXT NOT NULL,
        attempts INTEGER NOT NULL,
        worker TEXT,
        collections TEXT,
        embed INTEGER NOT NULL,
        force INTEGER NOT NULL,
        reason TEXT NOT NULL,
        base_reason TEXT NOT NULL,
        merged_requests INTEGER NOT NULL,
        created_at TEXT NOT NULL,
        started_at TEXT,
        finished_at TEXT,
        due_at INTEGER,
        first_request_at INTEGER,
        deferred INTEGER NOT NULL,
        deferrals INTEGER NOT NULL,
        crashes INTEGER NOT NULL,
        result TEXT,
        error TEXT
      );
      CREATE TABLE IF NOT EXISTS jobs_quarantine (id TEXT, data TEXT NOT NULL, reason TEXT NOT NULL, at TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS vector_provenance (hash TEXT NOT NULL, worker TEXT NOT NULL, embedded_at TEXT NOT NULL, PRIMARY KEY (hash, worker));
      CREATE TABLE IF NOT EXISTS probe_reference (id INTEGER PRIMARY KEY CHECK (id = 1), worker TEXT NOT NULL, vector TEXT NOT NULL);
    `);
  }

  /** One unreadable row must not cost the rest: it is set aside, with what it held. */
  load(): PersistedJob[] {
    const rows = this.#db.prepare("SELECT * FROM jobs ORDER BY seq").all() as unknown as Row[];
    const out: PersistedJob[] = [];
    for (const row of rows) {
      try {
        out.push(jobFromRow(row));
      } catch (err) {
        this.#quarantine(row, err);
      }
    }
    return out;
  }

  #quarantine(row: Row, err: unknown): void {
    this.quarantined++;
    const reason = err instanceof Error ? err.message : "unreadable";
    this.#db.prepare("INSERT INTO jobs_quarantine (id, data, reason, at) VALUES (?, ?, ?, ?)").run(row.id, JSON.stringify(row), reason, new Date().toISOString());
    this.#delete.run(row.id);
  }

  save(entry: PersistedJob): void {
    const { job } = entry;
    this.#upsert.run(
      job.id,
      entry.seq,
      job.state,
      job.priority,
      job.attempts,
      job.worker,
      job.request.collections === null ? null : JSON.stringify(job.request.collections),
      job.request.embed ? 1 : 0,
      job.request.force ? 1 : 0,
      job.request.reason,
      entry.baseReason,
      entry.mergedRequests,
      job.createdAt,
      job.startedAt,
      job.finishedAt,
      entry.dueAt,
      entry.firstRequestAt,
      entry.deferred ? 1 : 0,
      entry.deferrals,
      entry.crashes,
      job.result === null ? null : JSON.stringify(job.result),
      job.error,
    );
  }

  remove(id: string): void {
    this.#delete.run(id);
  }

  // --- provenance ------------------------------------------------------------

  /** A document's vectors are replaced whole, so its earlier record goes with them. */
  recordDocument(hash: string, workers: readonly string[]): void {
    const at = new Date().toISOString();
    this.#db.prepare("DELETE FROM vector_provenance WHERE hash = ?").run(hash);
    const insert = this.#db.prepare("INSERT INTO vector_provenance (hash, worker, embedded_at) VALUES (?, ?, ?)");
    for (const worker of workers) insert.run(hash, worker, at);
  }

  documentsBy(worker: string): string[] {
    const rows = this.#db.prepare("SELECT hash FROM vector_provenance WHERE worker = ?").all(worker) as unknown as Array<{ hash: string }>;
    return rows.map((row) => row.hash);
  }

  forgetDocuments(hashes: readonly string[]): void {
    const remove = this.#db.prepare("DELETE FROM vector_provenance WHERE hash = ?");
    for (const hash of hashes) remove.run(hash);
  }

  probeReference(): ProbeReference | null {
    const row = this.#db.prepare("SELECT worker, vector FROM probe_reference WHERE id = 1").get() as { worker: string; vector: string } | undefined;
    return row ? { worker: row.worker, vector: JSON.parse(row.vector) as number[] } : null;
  }

  setProbeReference(reference: ProbeReference | null): void {
    this.#db.prepare("DELETE FROM probe_reference").run();
    if (reference) this.#db.prepare("INSERT INTO probe_reference (id, worker, vector) VALUES (1, ?, ?)").run(reference.worker, JSON.stringify(reference.vector));
  }

  close(): void {
    this.#db.close();
  }
}

export interface OpenedJobStore {
  /** Null when no file could be used, so jobs live in memory this run. */
  store: SqliteJobStore | null;
  /** Where a corrupt file was moved, if one was. */
  movedTo: string | null;
}

function tryOpen(path: string): SqliteJobStore {
  const store = new SqliteJobStore(path);
  try {
    store.load();
    return store;
  } catch (err) {
    store.close();
    throw err;
  }
}

function moveAside(path: string, stamp: number): string {
  const target = `${path}.corrupt-${stamp}`;
  renameSync(path, target);
  for (const suffix of ["-wal", "-shm"]) {
    if (existsSync(path + suffix)) renameSync(path + suffix, target + suffix);
  }
  return target;
}

/**
 * Open the queue, or get out of the way. A file that cannot be read is renamed,
 * not deleted, and a new one started. If even that fails, the sidecar runs with
 * jobs in memory: search matters more than this file.
 */
export function openJobStore(path: string, warn: (message: string) => void, now: () => number = Date.now): OpenedJobStore {
  try {
    return { store: tryOpen(path), movedTo: null };
  } catch (err) {
    warn(`The job queue at ${path} could not be opened: ${err instanceof Error ? err.message : "unknown error"}`);
  }
  try {
    const movedTo = moveAside(path, now());
    warn(`Moved the unreadable job queue to ${movedTo}. Jobs it held will not run.`);
    return { store: tryOpen(path), movedTo };
  } catch (err) {
    warn(`Running with jobs in memory only: ${err instanceof Error ? err.message : "unknown error"}`);
    return { store: null, movedTo: null };
  }
}
