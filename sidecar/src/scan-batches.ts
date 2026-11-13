/**
 * A scan's writes, grouped into short transactions. The scan runs on a thread of its own
 * (src/scan.ts) with a connection of its own, while searches read the same index on the
 * main thread. QMD's scan writes each statement on its own, so a reader could catch a
 * document between its row and its full-text entry, and a kill there leaves the two out of
 * step for good: the next scan sees the content unchanged and never rebuilds the entry.
 * Grouped, a reader sees each document whole, either as it was or as it is, and a kill
 * loses at most the open group, which the next scan redoes.
 *
 * A group opens at the scan's first write, not before: a scan that only reads and hashes
 * unchanged files takes no write lock at all, as QMD's own scan does not, so no other writer,
 * in this process or another, ever waits for it. It takes the lock as it opens
 * (`BEGIN IMMEDIATE`), so it never has to upgrade a read to a write, which another writer
 * could make fail at once.
 *
 * A group may close only where no document is half-written:
 * - after a document, where QMD calls its progress callback;
 * - before one of the statements QMD runs after the documents, each of which stands alone: the
 *   deactivation of one missing document, and the removal of orphaned content. So removing
 *   10,000 deleted documents is many short groups, not one long one. A reader sees them go a
 *   group at a time, each document whole.
 * At either place it closes once it has run for `batchMs`, and the scan stops there when asked.
 * A writer on the main thread waits for the group in progress, so groups stay short: that wait
 * is on its event loop. One statement still holds the lock for as long as it runs: see
 * sidecar/README.md for what that costs.
 */
export interface BatchStatement {
  readonly reader: boolean;
  run(...args: unknown[]): unknown;
}

/** A better-sqlite3 connection, as far as grouping needs it. */
export interface BatchDb {
  exec(sql: string): unknown;
  prepare(sql: string): BatchStatement;
  readonly inTransaction: boolean;
}

export interface BatchOptions {
  /** How long a group runs before the next place it may close closes it. */
  batchMs: number;
  /** Checked wherever a group may close. True ends the scan there, with what it wrote so far kept. */
  shouldStop: () => boolean;
  now?: () => number;
  /** Runs before each group takes the lock, so another writer can go first: see write-gate.ts. */
  beforeGroup?: () => void;
}

/** The scan was asked to stop, and did, between documents. What it wrote stands. */
export class ScanStopped extends Error {
  constructor() {
    super("The scan was stopped");
    this.name = "ScanStopped";
  }
}

/**
 * Statements QMD's scan runs after its documents, each complete in itself, so a group may close
 * before any of them. Matched on QMD's own SQL: test/integration-scan-thread.test.ts measures
 * how long a mass deletion holds the lock, so a change in that SQL shows there.
 */
const STANDS_ALONE = [/^\s*UPDATE documents SET active = 0 WHERE collection = \? AND path = \?/, /^\s*DELETE FROM content\s+WHERE hash NOT IN/];

export function standsAlone(sql: string): boolean {
  return STANDS_ALONE.some((pattern) => pattern.test(sql));
}

/**
 * Run `scan`, handing it the callback to call after each document, with its writes in
 * transactions of about `batchMs`. Every group it finishes is committed, even when it stops
 * or fails later; a failure rolls back only the open one. While it runs, the connection's
 * `prepare` hands out statements that open a group before they write.
 */
export async function inBatches<T>(db: BatchDb, scan: (afterDocument: () => void) => Promise<T>, options: BatchOptions): Promise<T> {
  const now = options.now ?? (() => performance.now());
  let open = false;
  let since = 0;
  const begin = (): void => {
    options.beforeGroup?.();
    db.exec("BEGIN IMMEDIATE");
    open = true;
    since = now();
  };
  const commit = (): void => {
    db.exec("COMMIT");
    open = false;
  };
  /** A place where no document is half-written. */
  const mayClose = (): void => {
    const stop = options.shouldStop();
    if (open && (stop || now() - since >= options.batchMs)) commit();
    if (stop) throw new ScanStopped();
  };
  if (options.shouldStop()) throw new ScanStopped();
  const restore = writesOpenGroups(db, (sql) => {
    if (standsAlone(sql)) mayClose();
    // A transaction QMD opened itself (a legacy path's rename) carries its own writes.
    if (!open && !db.inTransaction) begin();
  });
  try {
    const result = await scan(mayClose);
    if (open) commit();
    return result;
  } catch (err) {
    if (open) rollBack(db);
    throw err;
  } finally {
    restore();
  }
}

/** Until the returned function is called, `beforeWrite` runs before each statement of `db` that writes. */
function writesOpenGroups(db: BatchDb, beforeWrite: (sql: string) => void): () => void {
  const hadOwn = Object.prototype.hasOwnProperty.call(db, "prepare");
  const prepare = db.prepare;
  db.prepare = (sql: string): BatchStatement => {
    const statement = prepare.call(db, sql);
    if (statement.reader) return statement;
    const run = statement.run.bind(statement);
    statement.run = (...args: unknown[]) => {
      beforeWrite(sql);
      return run(...args);
    };
    return statement;
  };
  return () => {
    if (hadOwn) db.prepare = prepare;
    else delete (db as { prepare?: unknown }).prepare;
  };
}

function rollBack(db: BatchDb): void {
  try {
    db.exec("ROLLBACK");
  } catch {
    // SQLite may have rolled back already, as it does on some errors. The first error is the one to report.
  }
}
