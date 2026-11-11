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
}

/** The later of two records for one document wins, so a merge never brings back an older verdict. */
function newer(a: DeadLetterRow | undefined, b: DeadLetterRow): boolean {
  return a === undefined || b.at > a.at;
}

/**
 * The table in use. Starts on the queue file's when there is one, else in memory, and
 * moves to the file's with `adopt`.
 */
export class DeadLetterBook {
  #table: DeadLetterTable;
  #persistent: boolean;

  constructor(table: DeadLetterTable | null) {
    this.#table = table ?? new MemoryDeadLetters();
    this.#persistent = table !== null;
  }

  get persistent(): boolean {
    return this.#persistent;
  }

  /** The queue file came free: what was kept in memory joins what it holds. */
  adopt(table: DeadLetterTable): void {
    if (this.#persistent) return;
    const here = this.#table.all();
    this.#table = table;
    this.#persistent = true;
    this.merge(here);
  }

  /** Rows from elsewhere, keeping whichever record of a document is newer. */
  merge(rows: readonly DeadLetterRow[]): void {
    const held = new Map(this.#table.all().map((row) => [key(row.hash, row.path), row]));
    for (const row of rows) {
      if (newer(held.get(key(row.hash, row.path)), row)) this.#table.put(row);
    }
  }

  all(): DeadLetterRow[] {
    return this.#table.all();
  }

  put(row: DeadLetterRow): void {
    this.#table.put(row);
  }

  removeHash(hash: string): void {
    this.#table.removeHash(hash);
  }

  /** Drop every row whose document `exists` no longer finds. Returns how many went. */
  retain(exists: (hash: string, path: string) => boolean): number {
    let removed = 0;
    for (const row of this.#table.all()) {
      if (exists(row.hash, row.path)) continue;
      this.#table.remove(row.hash, row.path);
      removed++;
    }
    return removed;
  }

  clear(): number {
    return this.#table.clear();
  }
}
