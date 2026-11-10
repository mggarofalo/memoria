import { isBusy, withShortBusyTimeout } from "./sqlite-busy.ts";

/**
 * What a deleted document leaves behind, removed. When a scan finds a document gone,
 * the SDK only marks its row inactive, so its text stays in `content`, its chunks in
 * `content_vectors` and `vectors_vec`, and any set-aside in `memoria_dead_letters`,
 * until a `qmd cleanup` that nothing here runs. Deleted text, which may be sensitive,
 * then stays in the index and in every backup of it.
 *
 * This runs after every scan. Content is shared by hash, so a row goes only once no
 * active document holds that hash: a document whose text another one also has leaves
 * that text, and its vectors, to the other. Inactive document rows go too, as they are
 * what kept their content referenced. It is one transaction, so a reader sees all of
 * it or none of it.
 */
export interface PurgeDb {
  prepare(sql: string): { get(...args: unknown[]): unknown; run(...args: unknown[]): { changes: number | bigint } };
  exec(sql: string): void;
}

export interface PurgeCounts {
  documents: number;
  content: number;
  vectors: number;
  deadLetters: number;
}

const ACTIVE_HASHES = "SELECT hash FROM documents WHERE active = 1";

function hasTable(db: PurgeDb, name: string): boolean {
  return db.prepare("SELECT 1 FROM sqlite_master WHERE type IN ('table', 'view') AND name = ?").get(name) !== undefined;
}

function changes(db: PurgeDb, sql: string): number {
  return Number(db.prepare(sql).run().changes);
}

function removeAll(db: PurgeDb): PurgeCounts {
  let vectors = 0;
  if (hasTable(db, "content_vectors")) {
    // vectors_vec first: its rows are found through content_vectors. It exists only once something has been embedded.
    if (hasTable(db, "vectors_vec")) {
      db.prepare(`DELETE FROM vectors_vec WHERE hash_seq IN (SELECT hash || '_' || seq FROM content_vectors WHERE hash NOT IN (${ACTIVE_HASHES}))`).run();
    }
    vectors = changes(db, `DELETE FROM content_vectors WHERE hash NOT IN (${ACTIVE_HASHES})`);
  }
  const deadLetters = hasTable(db, "memoria_dead_letters") ? changes(db, `DELETE FROM memoria_dead_letters WHERE hash NOT IN (${ACTIVE_HASHES})`) : 0;
  const documents = changes(db, "DELETE FROM documents WHERE active = 0");
  const content = changes(db, "DELETE FROM content WHERE hash NOT IN (SELECT hash FROM documents)");
  return { documents, content, vectors, deadLetters };
}

/**
 * Remove what deleted documents left. Null if the index was busy, as when an operator's
 * own `qmd` holds it: nothing is changed, and the next scan tries again.
 */
export function purgeDeleted(db: PurgeDb): PurgeCounts | null {
  try {
    return withShortBusyTimeout(db as never, () => {
      db.exec("BEGIN IMMEDIATE");
      try {
        const counts = removeAll(db);
        db.exec("COMMIT");
        return counts;
      } catch (err) {
        db.exec("ROLLBACK");
        throw err;
      }
    });
  } catch (err) {
    if (isBusy(err)) return null;
    throw err;
  }
}
