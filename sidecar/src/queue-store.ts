import { existsSync, readdirSync, renameSync, rmSync } from "node:fs";
import { isBusy } from "./sqlite-busy.ts";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";
import type { DeadLetterRow, DeadLetterTable } from "./dead-letters.ts";
import type { Job, JobStore, PersistedJob } from "./jobs.ts";

/**
 * Jobs in their own SQLite file beside the index, never inside QMD's schema, so
 * deleting and rebuilding `index.sqlite` leaves the queue alone and QMD upgrades
 * cannot collide with it. Uses Node's built-in SQLite, which adds no dependency.
 *
 * A row holds a job's request, timestamps and result counts. It never holds
 * document content or credentials: the reason names a file path at most, and
 * errors are built from fixed text (src/llm/client.ts). The file also holds the
 * documents set aside from embedding (src/dead-letters.ts), which name a path
 * and a fixed reason, so they too outlive a rebuilt index.
 *
 * This file only holds those records, so it must never stop search: a bad row is
 * set aside, `openJobStore` moves a truly corrupt file out of the way, and any other
 * trouble leaves the sidecar running with jobs in memory.
 *
 * One sidecar owns the file at a time. SQLite's exclusive locking mode does that,
 * and the operating system releases the lock if the process dies.
 */

const SCHEMA_VERSION = 1;
const JOB_STATES: ReadonlySet<string> = new Set(["queued", "running", "succeeded", "failed"]);

/** The default location: beside the index, under a name of its own. */
export function queueDbPathFor(indexPath: string): string {
  return `${indexPath}.queue.sqlite`;
}

/** How many moved-aside corrupt files to keep. */
const KEEP_CORRUPT_FILES = 3;

interface Row {
  row_id: number;
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
  if (typeof row.id !== "string") throw new Error("a job row with no id");
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

export class SqliteJobStore implements JobStore {
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
    this.#db.exec("PRAGMA busy_timeout = 0");
    this.#db.exec("PRAGMA locking_mode = EXCLUSIVE");
    this.#db.exec("PRAGMA journal_mode = WAL");
    // A job acknowledged with a 202 has to survive the machine going down.
    this.#db.exec("PRAGMA synchronous = FULL");
    // A removed row, such as a deleted document's set-aside, is overwritten, not left in free pages.
    this.#db.exec("PRAGMA secure_delete = ON");
    const { user_version: version } = this.#db.prepare("PRAGMA user_version").get() as { user_version: number };
    if (version > SCHEMA_VERSION) throw new Error(`The job queue at ${path} is from a newer version of memoria-qmd.`);
    this.#createTables();
    // A write, so under exclusive locking this is what takes the lock: a second sidecar finds out now.
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
      CREATE TABLE IF NOT EXISTS dead_letters (hash TEXT NOT NULL, path TEXT NOT NULL, reason TEXT NOT NULL, at TEXT NOT NULL, worker_set TEXT NOT NULL, PRIMARY KEY (hash, path));
    `);
  }

  /**
   * The set-aside documents, in this file. A table added beside the jobs, so the schema
   * version stays: an older sidecar opening the file ignores it.
   */
  deadLetters(): DeadLetterTable {
    const db = this.#db;
    return {
      all: () =>
        (db.prepare("SELECT hash, path, reason, at, worker_set FROM dead_letters").all() as Array<{ hash: string; path: string; reason: string; at: string; worker_set: string }>).map(
          (r): DeadLetterRow => ({ hash: r.hash, path: r.path, reason: r.reason, at: r.at, workerSet: r.worker_set }),
        ),
      put: (row) => {
        db.prepare("INSERT OR REPLACE INTO dead_letters (hash, path, reason, at, worker_set) VALUES (?, ?, ?, ?, ?)").run(row.hash, row.path, row.reason, row.at, row.workerSet);
      },
      removeHash: (hash) => {
        db.prepare("DELETE FROM dead_letters WHERE hash = ?").run(hash);
      },
      remove: (hash, path) => {
        db.prepare("DELETE FROM dead_letters WHERE hash = ? AND path = ?").run(hash, path);
      },
      clear: () => Number(db.prepare("DELETE FROM dead_letters").run().changes),
      // Until a checkpoint, the removed rows' old pages are still in the write-ahead log.
      scrub: () => {
        db.prepare("PRAGMA wal_checkpoint(TRUNCATE)").get();
      },
    };
  }

  /** One unreadable row must not cost the rest: it is set aside, with what it held. */
  load(): PersistedJob[] {
    const rows = this.#db.prepare("SELECT rowid AS row_id, * FROM jobs ORDER BY seq").all() as unknown as Row[];
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
    // By rowid: a bad row's id can be NULL, which no `id = ?` would ever match.
    this.#db.prepare("DELETE FROM jobs WHERE rowid = ?").run(row.row_id);
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

  close(): void {
    this.#db.close();
  }
}

export interface OpenedJobStore {
  /** Null when no file could be used, so jobs live in memory for now. */
  store: SqliteJobStore | null;
  /** Where a corrupt file was moved, if one was. */
  movedTo: string | null;
  /** Another process holds the file, which may change. See `retryJobStore`. */
  locked: boolean;
}

const SQLITE_CORRUPT = 11;
const SQLITE_NOTADB = 26;

/** Only damage justifies moving a file. Locked, newer or unwritable files are someone's data. */
function isCorruption(err: unknown): boolean {
  const code = (err as { errcode?: unknown }).errcode;
  if (typeof code !== "number") return false;
  const primary = code % 256;
  return primary === SQLITE_CORRUPT || primary === SQLITE_NOTADB;
}

function tryOpen(file: string): SqliteJobStore {
  const store = new SqliteJobStore(file);
  try {
    store.load();
    return store;
  } catch (err) {
    store.close();
    throw err;
  }
}

function moveAside(file: string, stamp: number): string {
  const target = `${file}.corrupt-${stamp}`;
  renameSync(file, target);
  for (const suffix of ["-wal", "-shm"]) {
    if (existsSync(file + suffix)) renameSync(file + suffix, target + suffix);
  }
  return target;
}

/** They hold job records nobody is reading. A few are enough to look into what happened. */
function pruneCorruptFiles(file: string): void {
  const directory = path.dirname(file);
  const pattern = new RegExp(`^${path.basename(file).replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\.corrupt-(\\d+)`);
  const stamps = new Set<number>();
  for (const name of readdirSync(directory)) {
    const match = pattern.exec(name);
    if (match) stamps.add(Number(match[1]));
  }
  for (const stamp of [...stamps].sort((a, b) => b - a).slice(KEEP_CORRUPT_FILES)) {
    for (const suffix of ["", "-wal", "-shm"]) rmSync(`${file}.corrupt-${stamp}${suffix}`, { force: true });
  }
}

function inMemory(warn: (message: string) => void, err: unknown): OpenedJobStore {
  const locked = isBusy(err);
  warn(`Running with jobs in memory only, so they will not survive a restart${locked ? " unless the file comes free" : ""}: ${err instanceof Error ? err.message : "unknown error"}`);
  return { store: null, movedTo: null, locked };
}

/** How often a sidecar shut out of its queue file tries again. */
const LOCK_RETRY_MS = 30_000;

export interface LockRetryOptions {
  everyMs?: number;
  setTimer?: (run: () => void, ms: number) => { unref?: () => unknown };
  clearTimer?: (timer: unknown) => void;
}

/**
 * A new sidecar can start while the old one still holds the file, as in a rolling
 * restart. Keep trying, so it does not run in memory for its whole life. Calls
 * `onOpened` once, and stops by itself if the file turns out unusable for another reason.
 */
export function retryJobStore(file: string, onOpened: (store: SqliteJobStore) => void, warn: (message: string) => void, options: LockRetryOptions = {}): () => void {
  const everyMs = options.everyMs ?? LOCK_RETRY_MS;
  const setTimer = options.setTimer ?? setTimeout;
  const quiet = (message: string): void => {
    if (!message.includes("unless the file comes free")) warn(message);
  };
  let timer: { unref?: () => unknown } | undefined;
  const attempt = (): void => {
    const opened = openJobStore(file, quiet);
    if (opened.store) onOpened(opened.store);
    else if (opened.locked) arm();
  };
  const arm = (): void => {
    timer = setTimer(attempt, everyMs);
    timer.unref?.();
  };
  arm();
  return () => (options.clearTimer ?? ((t) => clearTimeout(t as NodeJS.Timeout)))(timer);
}

/**
 * Open the queue, or get out of the way. Only real corruption moves the file, which
 * is renamed, not deleted. Anything else, such as another sidecar holding it or a
 * file from a newer version, leaves it untouched and runs with jobs in memory: search
 * matters more than this file.
 */
export function openJobStore(file: string, warn: (message: string) => void, now: () => number = Date.now): OpenedJobStore {
  try {
    return { store: tryOpen(file), movedTo: null, locked: false };
  } catch (err) {
    if (!isCorruption(err)) return inMemory(warn, err);
    warn(`The job queue at ${file} is corrupt: ${err instanceof Error ? err.message : "unknown error"}`);
  }
  try {
    const movedTo = moveAside(file, now());
    warn(`Moved the unreadable job queue to ${movedTo}. Jobs it held will not run.`);
    pruneCorruptFiles(file);
    return { store: tryOpen(file), movedTo, locked: false };
  } catch (err) {
    return inMemory(warn, err);
  }
}
