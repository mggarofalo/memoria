import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";
import { test } from "node:test";
import { inBatches, ScanStopped, standsAlone, type BatchDb, type BatchStatement } from "../src/scan-batches.ts";
import { tempDir } from "./helpers.ts";

const DEACTIVATE = "UPDATE documents SET active = 0 WHERE collection = ? AND path = ? AND active = 1";
const CLEANUP = "\n    DELETE FROM content\n    WHERE hash NOT IN (SELECT DISTINCT hash FROM documents)\n  ";

/** Records what the scan sent, statement by statement, and can fail one. */
class RecordingDb implements BatchDb {
  readonly sql: string[] = [];
  failOn: string | null = null;
  inTransaction = false;
  exec(sql: string): void {
    this.sql.push(sql);
    if (sql === this.failOn) throw new Error(`${sql} failed`);
  }
  prepare(sql: string): BatchStatement {
    return {
      reader: sql.startsWith("SELECT"),
      run: () => {
        this.sql.push(sql.trim().split(/\s+/).slice(0, 3).join(" "));
        return { changes: 1 };
      },
    };
  }
}

interface Doc {
  writes: boolean;
}

/** A scan over `docs`, as QMD's: a read for each, its writes if it changed, then the callback; `stepMs` each on `clock`. */
function scanOf(db: BatchDb, docs: Doc[], clock: { now: number }, stepMs: number, tail: string[] = []) {
  return async (afterDocument: () => void): Promise<string> => {
    for (const doc of docs) {
      clock.now += stepMs;
      db.prepare("SELECT id, hash FROM documents WHERE path = ?").run();
      if (doc.writes) {
        db.prepare("INSERT OR IGNORE INTO content VALUES (?)").run();
        db.prepare("UPDATE documents SET hash = ? WHERE id = ?").run();
      }
      afterDocument();
    }
    for (const sql of tail) {
      clock.now += stepMs;
      db.prepare(sql).run();
    }
    return "scanned";
  };
}

const changed = (n: number): Doc[] => Array.from({ length: n }, () => ({ writes: true }));
const unchanged = (n: number): Doc[] => Array.from({ length: n }, () => ({ writes: false }));
const READ = "SELECT id, hash";
const WRITE = ["INSERT OR IGNORE", "UPDATE documents SET"];

test("a scan that only reads takes no write lock at all", async () => {
  const db = new RecordingDb();
  const clock = { now: 0 };
  assert.equal(await inBatches(db, scanOf(db, unchanged(5), clock, 10), { batchMs: 25, shouldStop: () => false, now: () => clock.now }), "scanned");
  assert.deepEqual(db.sql, [READ, READ, READ, READ, READ]);
});

test("a group opens at the first write, and closes at a document's end once the batch time has run", async () => {
  const db = new RecordingDb();
  const clock = { now: 0 };
  await inBatches(db, scanOf(db, changed(5), clock, 10), { batchMs: 25, shouldStop: () => false, now: () => clock.now });
  // 10 ms a document, the group from the first write at 10 ms: the third ends at 30, which is 20 in, the fourth at 40, past 25.
  assert.deepEqual(db.sql, [
    READ, "BEGIN IMMEDIATE", ...WRITE,
    READ, ...WRITE,
    READ, ...WRITE,
    READ, ...WRITE,
    "COMMIT",
    READ, "BEGIN IMMEDIATE", ...WRITE,
    "COMMIT",
  ]);
});

test("after a group closes, unchanged documents take no lock until the next write", async () => {
  const db = new RecordingDb();
  const clock = { now: 0 };
  await inBatches(db, scanOf(db, [{ writes: true }, { writes: false }, { writes: false }, { writes: true }], clock, 30), { batchMs: 25, shouldStop: () => false, now: () => clock.now });
  assert.deepEqual(db.sql, [READ, "BEGIN IMMEDIATE", ...WRITE, READ, "COMMIT", READ, READ, "BEGIN IMMEDIATE", ...WRITE, "COMMIT"]);
});

test("a group that has run exactly the batch time closes", async () => {
  const db = new RecordingDb();
  const clock = { now: 0 };
  await inBatches(db, scanOf(db, changed(3), clock, 25), { batchMs: 25, shouldStop: () => false, now: () => clock.now });
  // The group opens at 25 ms; the second document ends at 50, exactly 25 in.
  assert.deepEqual(db.sql, [READ, "BEGIN IMMEDIATE", ...WRITE, READ, ...WRITE, "COMMIT", READ, "BEGIN IMMEDIATE", ...WRITE, "COMMIT"]);
});

test("the batch time counts from the group's start, not the scan's", async () => {
  const db = new RecordingDb();
  const clock = { now: 1_000 };
  await inBatches(db, scanOf(db, changed(2), clock, 10), { batchMs: 25, shouldStop: () => false, now: () => clock.now });
  assert.equal(db.sql.filter((s) => s === "COMMIT").length, 1);
});

test("without a clock it uses its own, and a long batch time keeps the writes in one group", async () => {
  const db = new RecordingDb();
  await inBatches(db, scanOf(db, changed(50), { now: 0 }, 0), { batchMs: 60_000, shouldStop: () => false });
  assert.equal(db.sql.filter((s) => s === "BEGIN IMMEDIATE").length, 1);
  assert.equal(db.sql.filter((s) => s === "COMMIT").length, 1);
});

test("without a clock it uses its own, and a batch time of 0 closes the group at every document", async () => {
  const db = new RecordingDb();
  await inBatches(db, scanOf(db, changed(3), { now: 0 }, 0), { batchMs: 0, shouldStop: () => false });
  assert.equal(db.sql.filter((s) => s === "BEGIN IMMEDIATE").length, 3);
});

test("before each group takes the lock, another writer gets its turn", async () => {
  const db = new RecordingDb();
  const clock = { now: 0 };
  await inBatches(db, scanOf(db, changed(3), clock, 30), { batchMs: 25, shouldStop: () => false, now: () => clock.now, beforeGroup: () => db.sql.push("main thread's turn") });
  assert.deepEqual(db.sql.filter((s) => s !== READ && !WRITE.includes(s)), ["main thread's turn", "BEGIN IMMEDIATE", "COMMIT", "main thread's turn", "BEGIN IMMEDIATE", "COMMIT"]);
});

test("a statement that stands alone after the documents is a place to close the group", async () => {
  const db = new RecordingDb();
  const clock = { now: 0 };
  const tail = [DEACTIVATE, DEACTIVATE, DEACTIVATE, DEACTIVATE, CLEANUP];
  await inBatches(db, scanOf(db, [], clock, 10, tail), { batchMs: 25, shouldStop: () => false, now: () => clock.now });
  // From the first at 10 ms: the fourth comes at 40, 30 in, so the group closes before it.
  assert.deepEqual(db.sql, ["BEGIN IMMEDIATE", "UPDATE documents SET", "UPDATE documents SET", "UPDATE documents SET", "COMMIT", "BEGIN IMMEDIATE", "UPDATE documents SET", "DELETE FROM content", "COMMIT"]);
});

test("a write that does not stand alone is never a place to close, however long the group has run", async () => {
  const db = new RecordingDb();
  const clock = { now: 0 };
  await inBatches(db, scanOf(db, [], clock, 100, ["UPDATE documents SET hash = ? WHERE id = ?", "INSERT INTO documents_fts VALUES (?)"]), { batchMs: 25, shouldStop: () => false, now: () => clock.now });
  assert.deepEqual(db.sql, ["BEGIN IMMEDIATE", "UPDATE documents SET", "INSERT INTO documents_fts", "COMMIT"]);
});

test("asked to stop among the statements after the documents, it commits and stops there", async () => {
  const db = new RecordingDb();
  const clock = { now: 0 };
  let calls = 0;
  await assert.rejects(inBatches(db, scanOf(db, [], clock, 1, [DEACTIVATE, DEACTIVATE, DEACTIVATE]), { batchMs: 25, shouldStop: () => ++calls > 2, now: () => clock.now }), ScanStopped);
  // The first check is at the start; the second before the first deactivation; the third stops before the second.
  assert.deepEqual(db.sql, ["BEGIN IMMEDIATE", "UPDATE documents SET", "COMMIT"]);
});

test("QMD's own statements after the documents are the ones that stand alone", () => {
  const sdk = readFileSync(new URL("./store.js", import.meta.resolve("@tobilu/qmd")), "utf8");
  const deactivate = /function deactivateDocument[\s\S]*?db\.prepare\(`([^`]*)`\)/.exec(sdk)?.[1];
  const cleanup = /function cleanupOrphanedContent[\s\S]*?db\.prepare\(`([^`]*)`\)/.exec(sdk)?.[1];
  assert.ok(deactivate && cleanup, "both are still in the SDK");
  assert.equal(standsAlone(deactivate), true);
  assert.equal(standsAlone(cleanup), true);
  assert.equal(standsAlone(DEACTIVATE), true);
  assert.equal(standsAlone(CLEANUP), true);
  assert.equal(standsAlone("\n    UPDATE documents SET active = 0 WHERE collection = ? AND path = ? AND active = 1"), true, "leading whitespace, as a template literal has");
  for (const other of ["SELECT 1; UPDATE documents SET active = 0 WHERE collection = ? AND path = ?", "SELECT 1; DELETE FROM content WHERE hash NOT IN (SELECT hash FROM documents)", "UPDATE documents SET title = ?, hash = ? WHERE id = ?", "DELETE FROM documents_fts WHERE rowid = ?", "INSERT OR IGNORE INTO content (hash, doc, created_at) VALUES (?, ?, ?)", "DELETE FROM llm_cache", "UPDATE documents SET active = 0"]) {
    assert.equal(standsAlone(other), false, other);
  }
});

test("inside a transaction QMD opened itself, a write opens no group", async () => {
  const db = new RecordingDb();
  await inBatches(db, async () => {
    db.inTransaction = true;
    db.prepare("UPDATE OR IGNORE documents SET path = ?").run();
    db.inTransaction = false;
  }, { batchMs: 25, shouldStop: () => false });
  assert.deepEqual(db.sql, ["UPDATE OR IGNORE"]);
});

test("asked to stop, it commits the group it is in at the document's end and stops there", async () => {
  const db = new RecordingDb();
  const clock = { now: 0 };
  let stop = false;
  const docs = changed(5);
  await assert.rejects(
    inBatches(db, async (afterDocument) => {
      for (let i = 0; i < docs.length; i++) {
        db.prepare("INSERT OR IGNORE INTO content VALUES (?)").run();
        if (i === 1) stop = true;
        afterDocument();
      }
    }, { batchMs: 25, shouldStop: () => stop, now: () => clock.now }),
    ScanStopped,
  );
  assert.deepEqual(db.sql, ["BEGIN IMMEDIATE", "INSERT OR IGNORE", "INSERT OR IGNORE", "COMMIT"], "nothing written is rolled back, and nothing more begins");
});

test("asked to stop with no group open, it stops without touching the lock", async () => {
  const db = new RecordingDb();
  const clock = { now: 0 };
  let calls = 0;
  await assert.rejects(inBatches(db, scanOf(db, unchanged(3), clock, 1), { batchMs: 25, shouldStop: () => ++calls > 2, now: () => clock.now }), ScanStopped);
  assert.deepEqual(db.sql, [READ, READ]);
});

test("asked to stop before it starts, it opens nothing", async () => {
  const db = new RecordingDb();
  let ran = false;
  await assert.rejects(
    inBatches(db, async () => {
      ran = true;
    }, { batchMs: 25, shouldStop: () => true }),
    (err: unknown) => err instanceof ScanStopped && err.message === "The scan was stopped" && err.name === "ScanStopped",
  );
  assert.equal(ran, false);
  assert.deepEqual(db.sql, []);
});

test("a scan that fails rolls back only the open group, and the error is its own", async () => {
  const db = new RecordingDb();
  const clock = { now: 0 };
  const boom = new Error("disk gone");
  await assert.rejects(
    inBatches(db, async (afterDocument) => {
      db.prepare("INSERT INTO content VALUES (?)").run();
      clock.now += 30;
      afterDocument();
      db.prepare("INSERT INTO content VALUES (?)").run();
      throw boom;
    }, { batchMs: 25, shouldStop: () => false, now: () => clock.now }),
    (err: unknown) => err === boom,
  );
  assert.deepEqual(db.sql, ["BEGIN IMMEDIATE", "INSERT INTO content", "COMMIT", "BEGIN IMMEDIATE", "INSERT INTO content", "ROLLBACK"]);
});

test("a scan that fails with no group open rolls nothing back", async () => {
  const db = new RecordingDb();
  await assert.rejects(inBatches(db, async () => {
    throw new Error("glob failed");
  }, { batchMs: 25, shouldStop: () => false }), /glob failed/);
  assert.deepEqual(db.sql, []);
});

test("when the rollback fails too, the first error is the one reported", async () => {
  const db = new RecordingDb();
  db.failOn = "ROLLBACK";
  await assert.rejects(
    inBatches(db, async () => {
      db.prepare("INSERT INTO content VALUES (?)").run();
      throw new Error("the scan's own");
    }, { batchMs: 25, shouldStop: () => false }),
    /the scan's own/,
  );
});

test("a commit that fails ends the scan with that error, and rolls back", async () => {
  const db = new RecordingDb();
  db.failOn = "COMMIT";
  const clock = { now: 0 };
  await assert.rejects(inBatches(db, scanOf(db, changed(3), clock, 30), { batchMs: 25, shouldStop: () => false, now: () => clock.now }), /COMMIT failed/);
  assert.deepEqual(db.sql.slice(-2), ["COMMIT", "ROLLBACK"]);
});

test("a final commit that fails rolls back the last group", async () => {
  const db = new RecordingDb();
  db.failOn = "COMMIT";
  await assert.rejects(inBatches(db, async () => {
    db.prepare("INSERT INTO content VALUES (?)").run();
  }, { batchMs: 25, shouldStop: () => false }), /COMMIT failed/);
  assert.deepEqual(db.sql, ["BEGIN IMMEDIATE", "INSERT INTO content", "COMMIT", "ROLLBACK"]);
});

test("the connection's own prepare is back once the scan is done, however it ended", async () => {
  const db = new RecordingDb();
  const own = db.prepare;
  await inBatches(db, async () => "done", { batchMs: 25, shouldStop: () => false });
  assert.equal(Object.prototype.hasOwnProperty.call(db, "prepare"), false);
  assert.equal(db.prepare, own);
  const withOwn = new RecordingDb();
  const mine = (sql: string): BatchStatement => ({ reader: false, run: () => withOwn.sql.push(`mine ${sql}`) });
  withOwn.prepare = mine;
  await assert.rejects(inBatches(withOwn, async () => {
    throw new Error("x");
  }, { batchMs: 25, shouldStop: () => false }));
  assert.equal(withOwn.prepare, mine);
  withOwn.prepare("INSERT INTO content VALUES (?)").run();
  assert.deepEqual(withOwn.sql, ["mine INSERT INTO content VALUES (?)"], "and statements from it no longer open groups");
});

// --- on SQLite itself ---------------------------------------------------------------------------------

/** node:sqlite, shaped as the better-sqlite3 connection the scan has. */
function adapt(db: DatabaseSync): BatchDb {
  return {
    exec: (sql) => db.exec(sql),
    prepare: (sql) => {
      const statement = db.prepare(sql);
      return { reader: /^\s*SELECT/i.test(sql), run: (...args: unknown[]) => statement.run(...(args as [])) };
    },
    get inTransaction() {
      return db.isTransaction;
    },
  };
}

async function twoConnections(): Promise<{ scanner: DatabaseSync; other: DatabaseSync }> {
  const file = path.join(await tempDir("memoria-scan-batches-"), "index.sqlite");
  const scanner = new DatabaseSync(file, { timeout: 0 });
  scanner.exec("PRAGMA journal_mode = WAL");
  scanner.exec("CREATE TABLE documents (path TEXT PRIMARY KEY, hash TEXT); CREATE TABLE fts (path TEXT PRIMARY KEY, body TEXT); CREATE TABLE cache (k TEXT)");
  scanner.exec("INSERT INTO documents VALUES ('a.md', 'old'); INSERT INTO fts VALUES ('a.md', 'old text')");
  const other = new DatabaseSync(file, { timeout: 0 });
  return { scanner, other };
}

function tryWrite(other: DatabaseSync, attempts: string[]): void {
  try {
    other.exec("INSERT INTO cache VALUES ('rerank')");
    attempts.push("written");
  } catch (err) {
    attempts.push((err as { errstr?: string }).errstr ?? String(err));
  }
}

test("another connection sees each document whole, never its row without its text", async () => {
  const { scanner, other } = await twoConnections();
  const db = adapt(scanner);
  const seen: string[] = [];
  const look = (): void => {
    const row = other.prepare("SELECT d.hash AS hash, f.body AS body FROM documents d LEFT JOIN fts f ON f.path = d.path WHERE d.path = 'a.md'").get() as { hash: string; body: string | null };
    seen.push(`${row.hash}:${row.body}`);
  };
  try {
    await inBatches(db, async (afterDocument) => {
      // As QMD rewrites a document: its row, then its full-text entry, deleted and put back.
      db.prepare("UPDATE documents SET hash = 'new' WHERE path = 'a.md'").run();
      look();
      db.prepare("DELETE FROM fts WHERE path = 'a.md'").run();
      look();
      db.prepare("INSERT INTO fts VALUES ('a.md', 'new text')").run();
      look();
      afterDocument();
    }, { batchMs: 0, shouldStop: () => false });
    look();
    assert.deepEqual(seen, ["old:old text", "old:old text", "old:old text", "new:new text"]);
  } finally {
    other.close();
    scanner.close();
  }
});

test("another connection writes freely while the scan only reads, is held off while a group is open, and gets in between groups", async () => {
  const { scanner, other } = await twoConnections();
  const db = adapt(scanner);
  const attempts: string[] = [];
  try {
    const clock = { now: 0 };
    await inBatches(db, async (afterDocument) => {
      db.prepare("SELECT hash FROM documents WHERE path = 'a.md'").run();
      tryWrite(other, attempts);
      afterDocument();
      db.prepare("UPDATE documents SET hash = 'b' WHERE path = 'a.md'").run();
      tryWrite(other, attempts);
      clock.now += 30;
      afterDocument();
      tryWrite(other, attempts);
    }, { batchMs: 25, shouldStop: () => false, now: () => clock.now });
    assert.deepEqual(attempts, ["written", "database is locked", "written"]);
  } finally {
    other.close();
    scanner.close();
  }
});
