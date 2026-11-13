/**
 * A scan's writes, grouped into short transactions. The scan runs on a thread of its own
 * (src/scan.ts) with a connection of its own, while searches read the same index on the
 * main thread. QMD's scan writes each statement on its own, so a reader could catch a
 * document between its row and its full-text entry, and a kill there leaves the two out of
 * step for good: the next scan sees the content unchanged and never rebuilds the entry.
 * Grouped, a reader sees each document whole, either as it was or as it is, and a kill
 * loses at most the open group, which the next scan redoes.
 *
 * QMD calls its progress callback after each document's writes, so each group ends there,
 * once it has run for `batchMs`. The last group ends when the scan returns, so it holds the
 * removal of deleted documents too: a reader sees all of them gone or none.
 *
 * The group takes SQLite's write lock as it begins (`BEGIN IMMEDIATE`), so it never has to
 * upgrade a read to a write, which another writer could make fail at once. A writer on the
 * main thread waits for the group to end, so groups stay short: that wait is on its event loop.
 */
export interface BatchDb {
  exec(sql: string): unknown;
}

export interface BatchOptions {
  /** How long a group runs before the next document's end closes it. */
  batchMs: number;
  /** Checked after each document. True ends the scan there, with what it wrote so far kept. */
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
 * Run `scan`, handing it the callback to call after each document, with its writes in
 * transactions of about `batchMs`. Every group it finishes is committed, even when it stops
 * or fails later; a failure rolls back only the open one.
 */
export async function inBatches<T>(db: BatchDb, scan: (afterDocument: () => void) => Promise<T>, options: BatchOptions): Promise<T> {
  const now = options.now ?? (() => performance.now());
  // Stryker disable next-line BooleanLiteral: begin() sets it before anything reads it, and a stop before that throws outside the try.
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
  if (options.shouldStop()) throw new ScanStopped();
  begin();
  try {
    const result = await scan(() => {
      const stop = options.shouldStop();
      if (!stop && now() - since < options.batchMs) return;
      commit();
      if (stop) throw new ScanStopped();
      begin();
    });
    commit();
    return result;
  } catch (err) {
    if (open) rollBack(db);
    throw err;
  }
}

function rollBack(db: BatchDb): void {
  try {
    db.exec("ROLLBACK");
  } catch {
    // SQLite may have rolled back already, as it does on some errors. The first error is the one to report.
  }
}
