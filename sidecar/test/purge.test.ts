import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import { collectionsWithDocuments, collectionsWithoutDocuments, purgeDeleted, removedAny, truncateLog, type PurgeDb } from "../src/purge.ts";

interface Statement {
  run(...args: unknown[]): { changes: number };
  get(...args: unknown[]): unknown;
  all(...args: unknown[]): unknown[];
  pluck(): Statement;
}

interface Db {
  prepare(sql: string): Statement;
  exec(sql: string): void;
}

const Database = createRequire(import.meta.url)("better-sqlite3") as new (file: string) => Db;

/** The SDK's tables as far as the purge reads them. vectors_vec is a plain table here; the SQL is the same. */
function index(withVectors = true): Db {
  const db = new Database(":memory:");
  db.exec(`
    CREATE TABLE content (hash TEXT PRIMARY KEY, doc TEXT NOT NULL);
    CREATE TABLE documents (id INTEGER PRIMARY KEY, collection TEXT, path TEXT, hash TEXT NOT NULL, active INTEGER NOT NULL);
    CREATE TABLE content_vectors (hash TEXT NOT NULL, seq INTEGER NOT NULL, PRIMARY KEY (hash, seq));
  `);
  if (withVectors) db.exec("CREATE TABLE vectors_vec (hash_seq TEXT PRIMARY KEY)");
  for (const hash of ["gone", "shared", "kept"]) {
    db.prepare("INSERT INTO content VALUES (?, ?)").run(hash, `text of ${hash}`);
    for (const seq of [0, 1]) {
      db.prepare("INSERT INTO content_vectors VALUES (?, ?)").run(hash, seq);
      if (withVectors) db.prepare("INSERT INTO vectors_vec VALUES (?)").run(`${hash}_${seq}`);
    }
  }
  doc(db, 1, "c", "gone.md", "gone", 0);
  doc(db, 2, "c", "shared.md", "shared", 0);
  doc(db, 3, "c", "copy/shared.md", "shared", 1);
  doc(db, 4, "c", "kept.md", "kept", 1);
  return db;
}

function doc(db: Db, id: number, collection: string, path: string, hash: string, active: number): void {
  db.prepare("INSERT INTO documents VALUES (?, ?, ?, ?, ?)").run(id, collection, path, hash, active);
}

const rows = (db: Db, sql: string): unknown[] => db.prepare(sql).pluck().all();

test("a deleted document's row, content and vectors go, and nothing a live document holds", () => {
  const db = index();
  assert.deepEqual(purgeDeleted(db as unknown as PurgeDb), { documents: 2, content: 1, vectors: 2 });
  assert.deepEqual(rows(db, "SELECT path FROM documents ORDER BY id"), ["copy/shared.md", "kept.md"]);
  assert.deepEqual(rows(db, "SELECT hash FROM content ORDER BY hash"), ["kept", "shared"], "shared text stays with its copy");
  assert.deepEqual(rows(db, "SELECT DISTINCT hash FROM content_vectors ORDER BY hash"), ["kept", "shared"]);
  assert.deepEqual(rows(db, "SELECT hash_seq FROM vectors_vec ORDER BY hash_seq"), ["kept_0", "kept_1", "shared_0", "shared_1"]);
  const again = purgeDeleted(db as unknown as PurgeDb);
  assert.deepEqual(again, { documents: 0, content: 0, vectors: 0 }, "running it again finds nothing");
  assert.equal(removedAny(again), false);
});

test("a collection named to keep keeps every row, and what only it holds; any other is purged", () => {
  const db = index();
  db.prepare("INSERT INTO content VALUES ('away', 'text of away')").run();
  db.prepare("INSERT INTO content_vectors VALUES ('away', 0)").run();
  db.prepare("INSERT INTO vectors_vec VALUES ('away_0')").run();
  doc(db, 5, "unmounted", "a.md", "away", 0);
  doc(db, 6, "unmounted", "b.md", "kept", 0);
  doc(db, 7, "emptied", "c.md", "kept", 0);
  assert.deepEqual(collectionsWithoutDocuments(db as unknown as PurgeDb).sort(), ["emptied", "unmounted"]);
  assert.deepEqual(collectionsWithDocuments(db as unknown as PurgeDb), ["c"]);
  assert.deepEqual(purgeDeleted(db as unknown as PurgeDb, () => ["unmounted"]), { documents: 3, content: 1, vectors: 2 }, "c's and emptied's deletes go");
  assert.deepEqual(rows(db, "SELECT collection || '/' || path FROM documents WHERE active = 0 ORDER BY id"), ["unmounted/a.md", "unmounted/b.md"]);
  assert.deepEqual(rows(db, "SELECT hash_seq FROM vectors_vec WHERE hash_seq = 'away_0'"), ["away_0"]);
  assert.deepEqual(purgeDeleted(db as unknown as PurgeDb), { documents: 2, content: 1, vectors: 1 }, "kept no longer, its deletes go too");
});

test("an index with nothing embedded is purged all the same", () => {
  const db = index(false);
  assert.deepEqual(purgeDeleted(db as unknown as PurgeDb), { documents: 2, content: 1, vectors: 0 });
  db.exec("DROP TABLE content_vectors");
  doc(db, 9, "c", "kept2.md", "kept", 0);
  assert.deepEqual(purgeDeleted(db as unknown as PurgeDb), { documents: 1, content: 0, vectors: 0 });
});

test("without sqlite-vec, a chunk's two rows are both left, never one of them", () => {
  const db = index(false);
  // A vec0 table seen without the extension loaded: listed, but any read of it fails.
  db.exec("CREATE VIEW vectors_vec AS SELECT hash_seq FROM missing_vec0_module");
  assert.deepEqual(purgeDeleted(db as unknown as PurgeDb), { documents: 2, content: 1, vectors: 0 });
  assert.equal(rows(db, "SELECT COUNT(*) FROM content_vectors")[0], 6, "content_vectors kept with them");
});

test("a failure part way changes nothing, and is the error reported", () => {
  const db = index();
  db.exec("CREATE TRIGGER refuse BEFORE DELETE ON content BEGIN SELECT RAISE(ABORT, 'refused'); END");
  assert.throws(() => purgeDeleted(db as unknown as PurgeDb), /refused/);
  assert.equal(rows(db, "SELECT COUNT(*) FROM documents")[0], 4);
  assert.equal(rows(db, "SELECT COUNT(*) FROM content_vectors")[0], 6);
  const rolledBack: PurgeDb = {
    prepare: (sql) => db.prepare(sql) as never,
    exec: (sql) => {
      if (sql === "ROLLBACK") throw new Error("cannot rollback - no transaction is active");
      db.exec(sql);
    },
  };
  assert.throws(() => purgeDeleted(rolledBack), /refused/, "a failed rollback does not hide the first error");
});

test("a busy index is left as it is, for the next scan", () => {
  const db = index();
  const busy = Object.assign(new Error("database is locked"), { code: "SQLITE_BUSY" });
  const locked: PurgeDb = {
    prepare: (sql) => db.prepare(sql) as never,
    exec: (sql) => {
      if (sql === "BEGIN IMMEDIATE") throw busy;
      db.exec(sql);
    },
  };
  assert.equal(purgeDeleted(locked), null);
  assert.equal(removedAny(null), false);
  assert.equal(rows(db, "SELECT COUNT(*) FROM documents")[0], 4);
});

test("truncating the log never throws, busy or not in WAL mode", () => {
  const db = index();
  truncateLog(db as unknown as PurgeDb);
  truncateLog({ prepare: () => ({ get: () => { throw new Error("busy"); } }) as never, exec: () => {} });
});
