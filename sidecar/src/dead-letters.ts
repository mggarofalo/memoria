import { isBusy, withShortBusyTimeout } from "./sqlite-busy.ts";

/**
 * Documents set aside from embedding, and why. They live in the job queue's file, beside
 * the index and not in it, so rebuilding `index.sqlite` keeps them, as it keeps jobs, and
 * QMD's schema never holds a table of ours. While that file is held by another sidecar,
 * they are kept in memory, and join the file when it comes free, as jobs do.
 *
 * Keyed by content and path, because the path can reach the text a worker sees, through
 * the title. So changed content, or the same content under a new name, is tried afresh.
 * A row names a path and a fixed reason, never document content.
 */

export interface DeadLetterRow {
  hash: string;
  path: string;
  reason: string;
  at: string;
  /** The embedding fingerprint and the worker set it was refused by: a new set is a new chance. */
  workerSet: string;
}

/** Where the rows are kept. */
export interface DeadLetterTable {
  all(): DeadLetterRow[];
  put(row: DeadLetterRow): void;
  /** Every path for this content, which has just been embedded. */
  removeHash(hash: string): void;
  remove(hash: string, path: string): void;
  /** How many rows there were. */
  clear(): number;
  /** Rows were removed: take what they held off the disk, as their paths may be sensitive. */
  scrub(): void;
}

const key = (hash: string, path: string): string => `${hash}\0${path}`;

export class MemoryDeadLetters implements DeadLetterTable {
  readonly #rows = new Map<string, DeadLetterRow>();

  all(): DeadLetterRow[] {
    return [...this.#rows.values()];
  }

  put(row: DeadLetterRow): void {
    this.#rows.set(key(row.hash, row.path), { ...row });
  }

  removeHash(hash: string): void {
    for (const row of this.all()) if (row.hash === hash) this.#rows.delete(key(row.hash, row.path));
  }

  remove(hash: string, path: string): void {
    this.#rows.delete(key(hash, path));
  }

  clear(): number {
    const n = this.#rows.size;
    this.#rows.clear();
    return n;
  }

  scrub(): void {}
}

/** The later of two records for one document wins, so a merge never brings back an older verdict. */
function newer(a: DeadLetterRow | undefined, b: DeadLetterRow): boolean {
  return a === undefined || b.at > a.at;
}

/**
 * The table in use. Starts on the queue file's when there is one, else in memory, and
 * moves to the file's with `adopt`.
 *
 * What it removes, it remembers, with when, until no other copy of the rows is left to
 * merge in: the queue file's while it is not ours, and the index's from an older build. A
 * row there no newer than its removal stays removed, so a clear, an embed, or a deleted
 * document is never undone by a merge.
 */
export class DeadLetterBook {
  #table: DeadLetterTable;
  #persistent: boolean;
  readonly #now: () => string;
  #clearedAt: string | null = null;
  readonly #goneHashes = new Map<string, string>();
  readonly #goneRows = new Map<string, string>();

  constructor(table: DeadLetterTable | null, now: () => string = () => new Date().toISOString()) {
    this.#table = table ?? new MemoryDeadLetters();
    this.#persistent = table !== null;
    this.#now = now;
  }

  get persistent(): boolean {
    return this.#persistent;
  }

  /** Removed here after this row was written, so a copy of it from elsewhere is stale. */
  #removed(row: DeadLetterRow): boolean {
    const at = [this.#clearedAt, this.#goneHashes.get(row.hash), this.#goneRows.get(key(row.hash, row.path))];
    return at.some((when) => when !== undefined && when !== null && row.at <= when);
  }

  /** The queue file came free: what was kept in memory joins what it holds. */
  adopt(table: DeadLetterTable): void {
    if (this.#persistent) return;
    const here = this.#table.all();
    let removed = false;
    for (const row of table.all()) {
      if (!this.#removed(row)) continue;
      table.remove(row.hash, row.path);
      removed = true;
    }
    this.#table = table;
    this.#persistent = true;
    // This process's own rows, so none of them is stale.
    this.#putNewer(here);
    if (removed) table.scrub();
  }

  #putNewer(rows: readonly DeadLetterRow[]): void {
    const held = new Map(this.#table.all().map((row) => [key(row.hash, row.path), row]));
    for (const row of rows) {
      if (newer(held.get(key(row.hash, row.path)), row)) this.#table.put(row);
    }
  }

  /** Rows from elsewhere, keeping whichever record of a document is newer, and none removed since. */
  merge(rows: readonly DeadLetterRow[]): void {
    this.#putNewer(rows.filter((row) => !this.#removed(row)));
  }

  /** No other copy is left to merge in, so what was removed need not be remembered. */
  settle(): void {
    if (!this.#persistent) return;
    this.#clearedAt = null;
    this.#goneHashes.clear();
    this.#goneRows.clear();
  }

  all(): DeadLetterRow[] {
    return this.#table.all();
  }

  put(row: DeadLetterRow): void {
    this.#table.put(row);
  }

  removeHash(hash: string): void {
    this.#goneHashes.set(hash, this.#now());
    this.#table.removeHash(hash);
  }

  /** Drop every row whose document `exists` no longer finds. Returns how many went. */
  retain(exists: (hash: string, path: string) => boolean): number {
    let removed = 0;
    for (const row of this.#table.all()) {
      if (exists(row.hash, row.path)) continue;
      this.#goneRows.set(key(row.hash, row.path), this.#now());
      this.#table.remove(row.hash, row.path);
      removed++;
    }
    if (removed > 0) this.#table.scrub();
    return removed;
  }

  clear(): number {
    this.#clearedAt = this.#now();
    const n = this.#table.clear();
    if (n > 0) this.#table.scrub();
    return n;
  }
}

/** The SQLite calls these need, which both drivers have. */
export interface IndexDb {
  prepare(sql: string): { all(...args: unknown[]): unknown[]; get(...args: unknown[]): unknown };
  exec(sql: string): void;
}

/** Where earlier builds kept set-asides: in the index, beside QMD's tables. */
export const LEGACY_DEAD_LETTERS = "memoria_dead_letters";

function hasLegacyTable(db: IndexDb): boolean {
  return db.prepare(`SELECT 1 FROM sqlite_master WHERE type='table' AND name='${LEGACY_DEAD_LETTERS}'`).get() !== undefined;
}

function legacyRows(db: IndexDb): DeadLetterRow[] {
  const rows = db.prepare(`SELECT hash, path, reason, at, worker_set FROM ${LEGACY_DEAD_LETTERS}`).all() as Array<{ hash: string; path: string; reason: string; at: string; worker_set: string }>;
  return rows.map((r) => ({ hash: r.hash, path: r.path, reason: r.reason, at: r.at, workerSet: r.worker_set }));
}

/** While the table has to stay, the rows of documents no row holds still go, as their paths may be sensitive. */
function pruneLegacyTable(db: IndexDb): void {
  try {
    withShortBusyTimeout(db, () =>
      db.exec(`DELETE FROM ${LEGACY_DEAD_LETTERS} WHERE NOT EXISTS (SELECT 1 FROM documents d WHERE d.hash = ${LEGACY_DEAD_LETTERS}.hash AND d.path = ${LEGACY_DEAD_LETTERS}.path)`),
    );
  } catch (err) {
    if (!isBusy(err)) throw err;
  }
}

/**
 * The move of an older build's set-asides out of the index, run until it has happened.
 *
 * Their rows join the book each time, so they hold even while the move waits. The table is
 * dropped only once the book is the queue file's: a copy in memory alone would be lost with
 * the process, and a tool opening the index holds no queue file at all. The last copy and
 * the drop share the index's write transaction, so no row an older build adds in between is
 * dropped unread, and a failed copy keeps the table. A crash after the copy only copies
 * again. A busy index leaves the drop for next time.
 */
export function moveLegacyLetters(db: IndexDb, book: DeadLetterBook): void {
  if (!hasLegacyTable(db)) {
    book.settle();
    return;
  }
  book.merge(legacyRows(db));
  if (!book.persistent) {
    pruneLegacyTable(db);
    return;
  }
  try {
    withShortBusyTimeout(db, () => {
      db.exec("BEGIN IMMEDIATE");
      try {
        book.merge(legacyRows(db));
        db.exec(`DROP TABLE ${LEGACY_DEAD_LETTERS}`);
        db.exec("COMMIT");
      } catch (err) {
        db.exec("ROLLBACK");
        throw err;
      }
    });
  } catch (err) {
    if (isBusy(err)) return;
    throw err;
  }
  book.settle();
}

/** Does any document row hold this content under this path, or with `active`, an active one? */
export function documentHolds(db: IndexDb, active: boolean): (hash: string, path: string) => boolean {
  const statement = db.prepare(`SELECT 1 FROM documents WHERE hash = ? AND path = ?${active ? " AND active = 1" : ""}`);
  return (hash, path) => statement.get(hash, path) !== undefined;
}
