import assert from "node:assert/strict";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";
import { test } from "node:test";
import { inBatches, ScanStopped, type BatchDb } from "../src/scan-batches.ts";
import { tempDir } from "./helpers.ts";

/** Records what the scan sent, and can fail one statement. */
class RecordingDb implements BatchDb {
  readonly sql: string[] = [];
  failOn: string | null = null;
  exec(sql: string): void {
    this.sql.push(sql);
    if (sql === this.failOn) throw new Error(`${sql} failed`);
  }
}

/** A scan of `documents` documents, each taking `stepMs` on the clock given. */
function scanOf(documents: number, clock: { now: number }, stepMs: number, onDocument: (i: number) => void = () => {}) {
  return async (afterDocument: () => void): Promise<string> => {
    for (let i = 0; i < documents; i++) {
      clock.now += stepMs;
      onDocument(i);
      afterDocument();
    }
    return "scanned";
  };
}

test("documents are grouped into transactions that close at a document's end once the batch time has run", async () => {
  const db = new RecordingDb();
  const clock = { now: 0 };
  const marks: string[] = [];
  const result = await inBatches(db, scanOf(7, clock, 10, (i) => db.sql.push(`doc ${i}`)), { batchMs: 25, shouldStop: () => false, now: () => clock.now });
  assert.equal(result, "scanned");
  marks.push(...db.sql);
  // 10 ms a document: the third ends at 30 ms, past 25, and closes the group; so does the sixth.
  assert.deepEqual(marks, [
    "BEGIN IMMEDIATE",
    "doc 0", "doc 1", "doc 2",
    "COMMIT", "BEGIN IMMEDIATE",
    "doc 3", "doc 4", "doc 5",
    "COMMIT", "BEGIN IMMEDIATE",
    "doc 6",
    "COMMIT",
  ]);
});

test("before each group takes the lock, another writer gets its turn", async () => {
  const db = new RecordingDb();
  const clock = { now: 0 };
  await inBatches(db, scanOf(2, clock, 30), { batchMs: 25, shouldStop: () => false, now: () => clock.now, beforeGroup: () => db.sql.push("main thread's turn") });
  assert.deepEqual(db.sql, ["main thread's turn", "BEGIN IMMEDIATE", "COMMIT", "main thread's turn", "BEGIN IMMEDIATE", "COMMIT", "main thread's turn", "BEGIN IMMEDIATE", "COMMIT"]);
});

test("a group that has run exactly the batch time closes", async () => {
  const db = new RecordingDb();
  const clock = { now: 0 };
  await inBatches(db, scanOf(2, clock, 25), { batchMs: 25, shouldStop: () => false, now: () => clock.now });
  assert.deepEqual(db.sql, ["BEGIN IMMEDIATE", "COMMIT", "BEGIN IMMEDIATE", "COMMIT", "BEGIN IMMEDIATE", "COMMIT"]);
});

test("the batch time counts from each group's start, not the scan's", async () => {
  const db = new RecordingDb();
  const clock = { now: 1_000 };
  // 1,000 ms in before the scan begins: counting from 0 would close a group after every document.
  await inBatches(db, scanOf(2, clock, 10), { batchMs: 25, shouldStop: () => false, now: () => clock.now });
  assert.deepEqual(db.sql, ["BEGIN IMMEDIATE", "COMMIT"]);
});

test("without a clock it uses its own, and a long batch time keeps the scan in one group", async () => {
  const db = new RecordingDb();
  await inBatches(db, scanOf(50, { now: 0 }, 0), { batchMs: 60_000, shouldStop: () => false });
  assert.deepEqual(db.sql, ["BEGIN IMMEDIATE", "COMMIT"]);
});

test("asked to stop, it commits the group it is in at the document's end and stops there", async () => {
  const db = new RecordingDb();
  const clock = { now: 0 };
  let stop = false;
  await assert.rejects(
    inBatches(db, scanOf(5, clock, 1, (i) => {
      db.sql.push(`doc ${i}`);
      if (i === 1) stop = true;
    }), { batchMs: 25, shouldStop: () => stop, now: () => clock.now }),
    ScanStopped,
  );
  assert.deepEqual(db.sql, ["BEGIN IMMEDIATE", "doc 0", "doc 1", "COMMIT"], "nothing written is rolled back, and nothing more begins");
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
      clock.now += 30;
      afterDocument();
      throw boom;
    }, { batchMs: 25, shouldStop: () => false, now: () => clock.now }),
    (err: unknown) => err === boom,
  );
  assert.deepEqual(db.sql, ["BEGIN IMMEDIATE", "COMMIT", "BEGIN IMMEDIATE", "ROLLBACK"]);
});

test("when the rollback fails too, the first error is the one reported", async () => {
  const db = new RecordingDb();
  db.failOn = "ROLLBACK";
  await assert.rejects(
    inBatches(db, async () => {
      throw new Error("the scan's own");
    }, { batchMs: 25, shouldStop: () => false }),
    /the scan's own/,
  );
});

test("a commit that fails ends the scan with that error, and rolls back", async () => {
  const db = new RecordingDb();
  db.failOn = "COMMIT";
  const clock = { now: 0 };
  await assert.rejects(inBatches(db, scanOf(3, clock, 30), { batchMs: 25, shouldStop: () => false, now: () => clock.now }), /COMMIT failed/);
  assert.deepEqual(db.sql, ["BEGIN IMMEDIATE", "COMMIT", "ROLLBACK"]);
});

test("a final commit that fails rolls back the last group", async () => {
  const db = new RecordingDb();
  db.failOn = "COMMIT";
  await assert.rejects(inBatches(db, async () => "done", { batchMs: 25, shouldStop: () => false }), /COMMIT failed/);
  assert.deepEqual(db.sql, ["BEGIN IMMEDIATE", "COMMIT", "ROLLBACK"]);
});

test("a commit that succeeded is not rolled back when the scan fails later", async () => {
  const db = new RecordingDb();
  await assert.rejects(
    inBatches(db, async (afterDocument) => {
      afterDocument();
      return "never";
    }, { batchMs: 25, shouldStop: () => true }).then(() => {}),
    ScanStopped,
  );
  assert.equal(db.sql.includes("ROLLBACK"), false);
});

// --- on SQLite itself ---------------------------------------------------------------------------------

async function twoConnections(): Promise<{ scanner: DatabaseSync; other: DatabaseSync }> {
  const file = path.join(await tempDir("memoria-scan-batches-"), "index.sqlite");
  const scanner = new DatabaseSync(file, { timeout: 0 });
  scanner.exec("PRAGMA journal_mode = WAL");
  scanner.exec("CREATE TABLE documents (path TEXT PRIMARY KEY, hash TEXT); CREATE TABLE fts (path TEXT PRIMARY KEY, body TEXT); CREATE TABLE cache (k TEXT)");
  scanner.exec("INSERT INTO documents VALUES ('a.md', 'old'); INSERT INTO fts VALUES ('a.md', 'old text')");
  const other = new DatabaseSync(file, { timeout: 0 });
  return { scanner, other };
}

test("another connection sees each document whole, never its row without its text", async () => {
  const { scanner, other } = await twoConnections();
  const seen: string[] = [];
  const look = (): void => {
    const row = other.prepare("SELECT d.hash AS hash, f.body AS body FROM documents d LEFT JOIN fts f ON f.path = d.path WHERE d.path = 'a.md'").get() as { hash: string; body: string | null };
    seen.push(`${row.hash}:${row.body}`);
  };
  try {
    await inBatches(scanner, async (afterDocument) => {
      // As QMD rewrites a document: its row, then its full-text entry, deleted and put back.
      scanner.exec("UPDATE documents SET hash = 'new' WHERE path = 'a.md'");
      look();
      scanner.exec("DELETE FROM fts WHERE path = 'a.md'");
      look();
      scanner.exec("INSERT INTO fts VALUES ('a.md', 'new text')");
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

test("a writer on another connection is held off while a group is open, and gets in between groups", async () => {
  const { scanner, other } = await twoConnections();
  const attempts: string[] = [];
  const tryWrite = (): void => {
    try {
      other.exec("INSERT INTO cache VALUES ('rerank')");
      attempts.push("written");
    } catch (err) {
      attempts.push((err as { errstr?: string }).errstr ?? String(err));
    }
  };
  try {
    const clock = { now: 0 };
    await inBatches(scanner, async (afterDocument) => {
      scanner.exec("UPDATE documents SET hash = 'b' WHERE path = 'a.md'");
      tryWrite();
      clock.now += 30;
      afterDocument();
      // Between groups is only inside afterDocument; the next one is open again here.
      tryWrite();
    }, { batchMs: 25, shouldStop: () => false, now: () => clock.now });
    tryWrite();
    assert.deepEqual(attempts, ["database is locked", "database is locked", "written"]);
  } finally {
    other.close();
    scanner.close();
  }
});
