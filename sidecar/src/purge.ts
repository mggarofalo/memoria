import { isBusy, withShortBusyTimeout } from "./sqlite-busy.ts";

/**
 * What a deleted document leaves behind, removed. When a scan finds a document gone,
 * the SDK only marks its row inactive, so its text stays in `content`, its chunks in
 * `content_vectors` and `vectors_vec`, and any set-aside in `memoria_dead_letters`,
 * until a `qmd cleanup` that nothing here runs. Deleted text, which may be sensitive,
 * then stays in the index and in every backup of it.
 *
 * This runs after every scan, in one transaction:
 *
 * 1. Inactive document rows go, except in the collections the caller names to keep:
 *    those the scan found with no document whose root is missing, unreadable, or
 *    not shown to be the real one (see collection-roots.ts). Their rows coming back
 *    would otherwise cost every vector of them a re-embed.
 * 2. Then whatever no remaining row holds: vectors and set-asides by hash, and by
 *    hash and path, and content by hash. Content is shared by hash, so text that
 *    another document also holds stays, with its vectors, for that one.
 *
 * It finds the rows to remove before touching the vector table, so a scan with
 * nothing to remove costs a few indexed lookups.
 */
export interface PurgeDb {
  prepare(sql: string): { get(...args: unknown[]): unknown; all(...args: unknown[]): unknown[]; run(...args: unknown[]): { changes: number | bigint } };
  exec(sql: string): void;
}

export interface PurgeCounts {
  documents: number;
  content: number;
  vectors: number;
  deadLetters: number;
}

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

function changes(db: PurgeDb, sql: string): number {
  return Number(db.prepare(sql).run().changes);
}

/**
 * A chunk's two rows go together or not at all. Without sqlite-vec, `vectors_vec` cannot be
 * written, so neither is removed, as the SDK's own cleanup does: they wait for a host with it.
 */
function removeVectors(db: PurgeDb): number {
  if (!hasTable(db, "content_vectors") || !vectorTableUsable(db)) return 0;
  const orphans = db.prepare(`SELECT hash, seq FROM content_vectors WHERE hash NOT IN (${HELD})`).all() as Array<{ hash: string; seq: number }>;
  if (orphans.length === 0) return 0;
  const remove = db.prepare("DELETE FROM vectors_vec WHERE hash_seq = ?");
  for (const { hash, seq } of orphans) remove.run(`${hash}_${seq}`);
  return changes(db, `DELETE FROM content_vectors WHERE hash NOT IN (${HELD})`);
}

function removeAll(db: PurgeDb, keep: readonly string[]): PurgeCounts {
  const documents = Number(db.prepare("DELETE FROM documents WHERE active = 0 AND collection NOT IN (SELECT value FROM json_each(?))").run(JSON.stringify(keep)).changes);
  const vectors = removeVectors(db);
  const deadLetters = hasTable(db, "memoria_dead_letters")
    ? changes(db, "DELETE FROM memoria_dead_letters WHERE NOT EXISTS (SELECT 1 FROM documents d WHERE d.hash = memoria_dead_letters.hash AND d.path = memoria_dead_letters.path)")
    : 0;
  const content = changes(db, `DELETE FROM content WHERE hash NOT IN (${HELD})`);
  return { documents, content, vectors, deadLetters };
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

/**
 * Remove what deleted documents left. Null if the index was busy, as when an operator's
 * own `qmd` holds it: nothing is changed, and the next scan tries again.
 */
export function purgeDeleted(db: PurgeDb, keep: readonly string[] = []): PurgeCounts | null {
  try {
    return withShortBusyTimeout(db as never, () => inTransaction(db, () => removeAll(db, keep)));
  } catch (err) {
    if (isBusy(err)) return null;
    throw err;
  }
}

/** Anything was removed, so its old versions are worth taking out of the write-ahead log. */
export function removedAny(counts: PurgeCounts | null): boolean {
  return counts !== null && counts.documents + counts.content + counts.vectors + counts.deadLetters > 0;
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
