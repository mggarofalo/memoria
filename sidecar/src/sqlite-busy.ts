/**
 * The SDK sets a 120-second busy timeout, and SQLite waits synchronously, on the
 * event loop. So a write of ours that meets another process's lock would freeze the
 * whole sidecar. Writes that can wait for later use this instead, and fail fast.
 */
const SHORT_BUSY_TIMEOUT_MS = 250;

interface BusyDb {
  prepare(sql: string): { get(): unknown };
  exec(sql: string): unknown;
}

export function withShortBusyTimeout<T>(db: BusyDb, work: () => T): T {
  const previous = (db.prepare("PRAGMA busy_timeout").get() as { timeout: number }).timeout;
  db.exec(`PRAGMA busy_timeout = ${SHORT_BUSY_TIMEOUT_MS}`);
  try {
    return work();
  } finally {
    db.exec(`PRAGMA busy_timeout = ${previous}`);
  }
}

const SQLITE_BUSY = 5;

/** Is this SQLite telling us someone else holds the database? The two drivers say it differently. */
export function isBusy(err: unknown): boolean {
  const { code, errcode } = err as { code?: unknown; errcode?: unknown };
  if (typeof code === "string" && code.startsWith("SQLITE_BUSY")) return true;
  return typeof errcode === "number" && errcode % 256 === SQLITE_BUSY;
}
