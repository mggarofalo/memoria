import { isBusy, withShortBusyTimeout } from "./sqlite-busy.ts";

/**
 * What a deleted document leaves behind, removed. When a scan finds a document gone,
 * the SDK only marks its row inactive, so its text stays in `content` and its chunks in
 * `content_vectors` and `vectors_vec`, until a `qmd cleanup` that nothing here runs. Any
 * record of it being set aside is in the queue's file, and goes after this (src/llm/wire.ts). Deleted text, which may be sensitive,
 * then stays in the index and in every backup of it.
 *
 * This runs after every scan, in three steps, in this order:
 *
 * 1. Inactive document rows go, except in the collections the caller names to keep:
 *    those the scan found with no document whose root is missing, unreadable, or
 *    not shown to be the real one (see collection-roots.ts). Their rows coming back
 *    would otherwise cost every vector of them a re-embed.
 * 2. Then the vectors no remaining row holds, by hash.
 * 3. Then the content no remaining row holds, by hash. Content is shared by hash, so text
 *    that another document also holds stays, with its vectors, for that one.
 *
 * Each step goes in transactions of at most `chunk` rows, because removing text with
 * `secure_delete` writes every page it held: 10,000 deleted transcripts took 2 s, all of it
 * holding the write lock, which a search caching a score on the main thread had to wait
 * out. Before each transaction `between` runs, so such a write can go first (write-gate.ts).
 * Each removal is decided inside its own transaction, the keep list and whether a row still
 * holds a hash included, so no other writer can change the answer between the decision and
 * the delete. A purge that stops partway leaves only rows nothing reads, which the next
 * scan's purge removes.
 *
 * It finds the rows to remove before taking any lock, so a scan with nothing to remove
 * costs a few indexed lookups.
 */
export interface PurgeDb {
  prepare(sql: string): { get(...args: unknown[]): unknown; all(...args: unknown[]): unknown[]; run(...args: unknown[]): { changes: number | bigint } };
  exec(sql: string): void;
}

export interface PurgeCounts {
  documents: number;
  content: number;
  vectors: number;
}

/** What one purge removed, and whether it finished: false when the index was busy partway. */
export interface PurgeResult extends PurgeCounts {
  complete: boolean;
}

export interface PurgeOptions {
  /** Most rows one transaction removes. */
  chunk?: number;
  /** Runs before each transaction. */
  between?: () => void;
}

/** Rows one transaction removes, unless the caller says otherwise. About 20 ms of deleting text. */
export const PURGE_CHUNK = 100;

/** Collections with document rows and not one of them active: the scan found no document in them. */
export function collectionsWithoutDocuments(db: PurgeDb): string[] {
  return (db.prepare("SELECT collection FROM documents GROUP BY collection HAVING MAX(active) = 0").all() as Array<{ collection: string }>).map((r) => r.collection);
}

/** Collections holding an active document. */
export function collectionsWithDocuments(db: PurgeDb): string[] {
  return (db.prepare("SELECT DISTINCT collection FROM documents WHERE active = 1").all() as Array<{ collection: string }>).map((r) => r.collection);
}

/** Hashes some remaining row still holds, once step 1 is done. */
const HELD = "SELECT hash FROM documents";
const UNHELD = "NOT EXISTS (SELECT 1 FROM documents WHERE documents.hash = ?)";

function hasTable(db: PurgeDb, name: string): boolean {
  return db.prepare("SELECT 1 FROM sqlite_master WHERE type IN ('table', 'view') AND name = ?").get(name) !== undefined;
}

/** Whether vectors_vec can be written here. It needs sqlite-vec loaded, which a host without it lacks. */
function vectorTableUsable(db: PurgeDb): boolean {
  if (!hasTable(db, "vectors_vec")) return false;
  try {
    db.prepare("SELECT 1 FROM vectors_vec LIMIT 0").get();
    return true;
  } catch {
    return false;
  }
}

function inTransaction<T>(db: PurgeDb, work: () => T): T {
  db.exec("BEGIN IMMEDIATE");
  let result: T;
  try {
    result = work();
  } catch (err) {
    try {
      db.exec("ROLLBACK");
    } catch {
      // SQLite may have rolled back already, as it does on some errors. The first error is the one to report.
    }
    throw err;
  }
  db.exec("COMMIT");
  return result;
}

/** Run `remove` on `candidates` in transactions of `chunk`; the total it removed. */
function inChunks<T>(db: PurgeDb, candidates: readonly T[], options: Required<PurgeOptions>, remove: (batch: readonly T[]) => number): number {
  let removed = 0;
  for (let at = 0; at < candidates.length; at += options.chunk) {
    options.between();
    removed += inTransaction(db, () => remove(candidates.slice(at, at + options.chunk)));
  }
  return removed;
}

function removeDocuments(db: PurgeDb, keep: () => readonly string[], options: Required<PurgeOptions>): number {
  const ids = (db.prepare("SELECT rowid AS id FROM documents WHERE active = 0").all() as Array<{ id: number }>).map((r) => r.id);
  const remove = db.prepare("DELETE FROM documents WHERE rowid = ? AND active = 0 AND collection NOT IN (SELECT value FROM json_each(?))");
  return inChunks(db, ids, options, (batch) => {
    // Decided inside the transaction, so no other writer can empty a collection between the decision and the delete.
    const kept = JSON.stringify(keep());
    return batch.reduce((n, id) => n + Number(remove.run(id, kept).changes), 0);
  });
}

/**
 * A chunk's two rows go together or not at all. Without sqlite-vec, `vectors_vec` cannot be
 * written, so neither is removed, as the SDK's own cleanup does: they wait for a host with it.
 */
function removeVectors(db: PurgeDb, options: Required<PurgeOptions>): number {
  if (!hasTable(db, "content_vectors") || !vectorTableUsable(db)) return 0;
  const orphans = db.prepare(`SELECT hash, seq FROM content_vectors WHERE hash NOT IN (${HELD})`).all() as Array<{ hash: string; seq: number }>;
  // Asked of documents first, rather than in the delete: vectors_vec is a virtual table, looked up only by its key.
  const held = db.prepare("SELECT 1 FROM documents WHERE hash = ? LIMIT 1");
  const vector = db.prepare("DELETE FROM vectors_vec WHERE hash_seq = ?");
  const row = db.prepare("DELETE FROM content_vectors WHERE hash = ? AND seq = ?");
  return inChunks(db, orphans, options, (batch) =>
    batch.reduce((n, { hash, seq }) => {
      if (held.get(hash) !== undefined) return n;
      vector.run(`${hash}_${seq}`);
      return n + Number(row.run(hash, seq).changes);
    }, 0),
  );
}

function removeContent(db: PurgeDb, options: Required<PurgeOptions>): number {
  const orphans = (db.prepare(`SELECT hash FROM content WHERE hash NOT IN (${HELD})`).all() as Array<{ hash: string }>).map((r) => r.hash);
  const remove = db.prepare(`DELETE FROM content WHERE hash = ? AND ${UNHELD}`);
  return inChunks(db, orphans, options, (batch) => batch.reduce((n, hash) => n + Number(remove.run(hash, hash).changes), 0));
}

/**
 * Remove what deleted documents left. Null if the index was busy before anything was removed,
 * as when an operator's own `qmd` holds it: nothing is changed, and the next scan tries again.
 * Busy partway, it reports what it removed, with `complete` false, and the next scan finishes.
 */
export function purgeDeleted(db: PurgeDb, keep: () => readonly string[] = () => [], options: PurgeOptions = {}): PurgeResult | null {
  const steps: Required<PurgeOptions> = { chunk: options.chunk ?? PURGE_CHUNK, between: options.between ?? (() => {}) };
  const counts: PurgeCounts = { documents: 0, content: 0, vectors: 0 };
  try {
    withShortBusyTimeout(db as never, () => {
      counts.documents = removeDocuments(db, keep, steps);
      counts.vectors = removeVectors(db, steps);
      counts.content = removeContent(db, steps);
    });
  } catch (err) {
    if (!isBusy(err)) throw err;
    return removedAny(counts) ? { ...counts, complete: false } : null;
  }
  return { ...counts, complete: true };
}

/** Anything was removed, so its old versions are worth taking out of the write-ahead log. */
export function removedAny(counts: PurgeCounts | null): boolean {
  return counts !== null && counts.documents + counts.content + counts.vectors > 0;
}

/**
 * Fold the write-ahead log into the file and empty it, so the frames that held deleted rows
 * are gone too. A reader in the middle of a long read stops it short; nothing is lost by
 * that, the next one goes further.
 */
export function truncateLog(db: PurgeDb): void {
  try {
    withShortBusyTimeout(db as never, () => db.prepare("PRAGMA wal_checkpoint(TRUNCATE)").get());
  } catch {
    // Busy or not in WAL mode: the log is left for SQLite's own checkpoints.
  }
}
