import assert from "node:assert/strict";
import { test } from "node:test";
import { createRequire } from "node:module";
import { purgeDeleted, type PurgeDb } from "../src/purge.ts";

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
    CREATE TABLE memoria_dead_letters (hash TEXT NOT NULL, path TEXT NOT NULL, PRIMARY KEY (hash, path));
  `);
  if (withVectors) db.exec("CREATE TABLE vectors_vec (hash_seq TEXT PRIMARY KEY)");
  const doc = (id: number, path: string, hash: string, active: number) => db.prepare("INSERT INTO documents VALUES (?, 'c', ?, ?, ?)").run(id, path, hash, active);
  for (const hash of ["gone", "shared", "kept"]) {
    db.prepare("INSERT INTO content VALUES (?, ?)").run(hash, `text of ${hash}`);
    for (const seq of [0, 1]) {
      db.prepare("INSERT INTO content_vectors VALUES (?, ?)").run(hash, seq);
      if (withVectors) db.prepare("INSERT INTO vectors_vec VALUES (?)").run(`${hash}_${seq}`);
    }
  }
  doc(1, "gone.md", "gone", 0);
  doc(2, "shared.md", "shared", 0);
  doc(3, "copy/shared.md", "shared", 1);
  doc(4, "kept.md", "kept", 1);
  db.prepare("INSERT INTO memoria_dead_letters VALUES ('gone', 'gone.md')").run();
  db.prepare("INSERT INTO memoria_dead_letters VALUES ('kept', 'kept.md')").run();
  return db;
}

const rows = (db: Db, sql: string): unknown[] => db.prepare(sql).pluck().all();

test("a deleted document's row, content, vectors and set-aside go, and nothing a live document holds", () => {
  const db = index();
  assert.deepEqual(purgeDeleted(db as unknown as PurgeDb), { documents: 2, content: 1, vectors: 2, deadLetters: 1 });
  assert.deepEqual(rows(db, "SELECT path FROM documents ORDER BY id"), ["copy/shared.md", "kept.md"]);
  assert.deepEqual(rows(db, "SELECT hash FROM content ORDER BY hash"), ["kept", "shared"], "shared text stays with its copy");
  assert.deepEqual(rows(db, "SELECT DISTINCT hash FROM content_vectors ORDER BY hash"), ["kept", "shared"]);
  assert.deepEqual(rows(db, "SELECT hash_seq FROM vectors_vec ORDER BY hash_seq"), ["kept_0", "kept_1", "shared_0", "shared_1"]);
  assert.deepEqual(rows(db, "SELECT hash FROM memoria_dead_letters"), ["kept"]);
  assert.deepEqual(purgeDeleted(db as unknown as PurgeDb), { documents: 0, content: 0, vectors: 0, deadLetters: 0 }, "running it again finds nothing");
});

test("an index with nothing embedded, or no set-aside table, is purged all the same", () => {
  const db = index(false);
  db.exec("DROP TABLE memoria_dead_letters");
  assert.deepEqual(purgeDeleted(db as unknown as PurgeDb), { documents: 2, content: 1, vectors: 2, deadLetters: 0 });
  db.exec("DROP TABLE content_vectors");
  db.prepare("INSERT INTO documents VALUES (9, 'c', 'kept2.md', 'kept', 0)").run();
  assert.deepEqual(purgeDeleted(db as unknown as PurgeDb), { documents: 1, content: 0, vectors: 0, deadLetters: 0 });
});

test("a failure part way changes nothing", () => {
  const db = index();
  db.exec("CREATE TRIGGER refuse BEFORE DELETE ON content BEGIN SELECT RAISE(ABORT, 'refused'); END");
  assert.throws(() => purgeDeleted(db as unknown as PurgeDb), /refused/);
  assert.equal(rows(db, "SELECT COUNT(*) FROM documents")[0], 4);
  assert.equal(rows(db, "SELECT COUNT(*) FROM content_vectors")[0], 6);
  assert.equal(rows(db, "SELECT COUNT(*) FROM memoria_dead_letters")[0], 2);
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
  assert.equal(rows(db, "SELECT COUNT(*) FROM documents")[0], 4);
});
