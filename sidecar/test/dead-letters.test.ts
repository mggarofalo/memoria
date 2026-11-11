import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";
import { test } from "node:test";
import { DeadLetterBook, documentHolds, MemoryDeadLetters, moveLegacyLetters, type DeadLetterRow } from "../src/dead-letters.ts";
import { SqliteJobStore } from "../src/queue-store.ts";
import { tempDir } from "./helpers.ts";

const row = (hash: string, file: string, at = "2026-11-01T00:00:00.000Z", reason = "refused"): DeadLetterRow => ({ hash, path: file, reason, at, workerSet: "fp:workers" });

const paths = (book: DeadLetterBook): string[] => book.all().map((r) => r.path).sort();

test("a set-aside is keyed by content and path: the same text under another name is another record", () => {
  const book = new DeadLetterBook(null);
  book.put(row("h", "a.md"));
  book.put(row("h", "b.md"));
  book.put(row("h", "a.md", "2026-11-02T00:00:00.000Z"));
  assert.deepEqual(paths(book), ["a.md", "b.md"]);
  assert.equal(book.all().find((r) => r.path === "a.md")!.at, "2026-11-02T00:00:00.000Z", "putting again replaces");
  book.removeHash("h");
  assert.deepEqual(book.all(), [], "embedding the content clears every name it had");
});

test("retain drops what the index no longer holds, and says how many", () => {
  const book = new DeadLetterBook(null);
  for (const file of ["kept.md", "gone.md", "moved.md"]) book.put(row(file, file));
  assert.equal(book.retain((hash) => hash === "kept.md"), 2);
  assert.deepEqual(paths(book), ["kept.md"]);
  assert.equal(book.clear(), 1);
  assert.equal(book.clear(), 0);
});

test("without the queue's file they are kept in memory, and join the file's when it comes free, the newer record winning", async () => {
  const store = new SqliteJobStore(path.join(await tempDir("memoria-letters-"), "q.sqlite"));
  try {
    const file = store.deadLetters();
    file.put(row("both", "both.md", "2026-11-05T00:00:00.000Z", "the file's, newer"));
    file.put(row("older-here", "older-here.md", "2026-11-05T00:00:00.000Z", "the file's, newer"));
    const book = new DeadLetterBook(null);
    assert.equal(book.persistent, false);
    book.put(row("both", "both.md", "2026-11-01T00:00:00.000Z", "memory's, older"));
    book.put(row("older-here", "older-here.md", "2026-11-09T00:00:00.000Z", "memory's, newer"));
    book.put(row("only-here", "only-here.md"));
    book.adopt(file);
    assert.equal(book.persistent, true);
    const reasons = Object.fromEntries(file.all().map((r) => [r.path, r.reason]));
    assert.deepEqual(reasons, { "both.md": "the file's, newer", "older-here.md": "memory's, newer", "only-here.md": "refused" });
    book.adopt(new MemoryDeadLetters());
    assert.equal(file.all().length, 3, "a second adopt changes nothing");
  } finally {
    store.close();
  }
});

test("in the queue's file they outlive the process, beside the jobs and not in place of them", async () => {
  const file = path.join(await tempDir("memoria-letters-"), "q.sqlite");
  const first = new SqliteJobStore(file);
  new DeadLetterBook(first.deadLetters()).put(row("h", "a.md"));
  first.close();
  const second = new SqliteJobStore(file);
  try {
    assert.deepEqual(new DeadLetterBook(second.deadLetters()).all(), [row("h", "a.md")]);
    assert.deepEqual(second.load(), []);
  } finally {
    second.close();
  }
});

/** A clock that reads `at` until moved. */
function clock(at: string): { now: () => string; set: (next: string) => void } {
  let current = at;
  return { now: () => current, set: (next) => void (current = next) };
}

const T1 = "2026-11-01T00:00:00.000Z";
const T2 = "2026-11-02T00:00:00.000Z";
const T3 = "2026-11-03T00:00:00.000Z";

async function queueFile(): Promise<{ file: string; store: SqliteJobStore }> {
  const file = path.join(await tempDir("memoria-letters-"), "q.sqlite");
  return { file, store: new SqliteJobStore(file) };
}

test("what was cleared, embedded or deleted while in memory leaves the file when it comes free, and what came later stays", async () => {
  const { store } = await queueFile();
  try {
    const file = store.deadLetters();
    for (const name of ["cleared", "embedded", "deleted", "later"]) file.put(row(name, `${name}.md`, T1));
    const time = clock(T2);
    const book = new DeadLetterBook(null, time.now);
    book.put(row("embedded", "embedded.md", T1));
    book.put(row("deleted", "deleted.md", T1));
    book.removeHash("embedded");
    assert.equal(book.retain((hash) => hash !== "deleted"), 1);
    book.clear();
    // Written by the file's holder after this one's clear, so not covered by it.
    file.remove("later", "later.md");
    file.put(row("later", "later.md", T3));
    book.put(row("mine", "mine.md", T2));
    book.adopt(file);
    assert.deepEqual(file.all().map((r) => r.path).sort(), ["later.md", "mine.md"]);
  } finally {
    store.close();
  }
});

test("each kind of removal covers only its own rows, and only those no newer than it", async () => {
  const { store } = await queueFile();
  try {
    const file = store.deadLetters();
    file.put(row("h", "a.md", T1));
    file.put(row("h", "b.md", T3));
    file.put(row("g", "c.md", T1));
    file.put(row("k", "d.md", T1));
    file.put(row("k", "e.md", T1));
    const book = new DeadLetterBook(null, clock(T2).now);
    book.put(row("k", "d.md", T1));
    book.removeHash("h");
    book.retain((_, p) => p !== "d.md");
    book.adopt(file);
    assert.deepEqual(file.all().map((r) => r.path).sort(), ["b.md", "c.md", "e.md"]);
  } finally {
    store.close();
  }
});

test("a merge brings back nothing removed since, and does bring a newer record", () => {
  const book = new DeadLetterBook(new MemoryDeadLetters(), clock(T2).now);
  book.put(row("h", "a.md", T1));
  book.clear();
  book.merge([row("h", "a.md", T1), row("n", "new.md", T3)]);
  assert.deepEqual(paths(book), ["new.md"]);
  book.settle();
  book.merge([row("h", "a.md", T1)]);
  assert.deepEqual(paths(book), ["a.md", "new.md"], "once settled, nothing is remembered");
});

test("a book in memory keeps what it removed in mind even when told to settle, as the file is still to come", async () => {
  const { store } = await queueFile();
  try {
    const file = store.deadLetters();
    file.put(row("h", "a.md", T1));
    const book = new DeadLetterBook(null, clock(T2).now);
    book.clear();
    book.settle();
    book.adopt(file);
    assert.deepEqual(file.all(), []);
  } finally {
    store.close();
  }
});

test("a removed set-aside's path is gone from the queue's file and its log, not left in free pages", async () => {
  const { file, store } = await queueFile();
  const book = new DeadLetterBook(store.deadLetters());
  for (let i = 0; i < 50; i++) book.put(row(`h${i}`, `secret/path-${i}.md`));
  book.put(row("kept", "kept/visible.md"));
  assert.equal(book.retain((hash) => hash === "kept"), 50);
  const onDisk = (): string => [file, `${file}-wal`].filter((f) => existsSync(f)).map((f) => readFileSync(f).toString("latin1")).join("");
  assert.ok(onDisk().includes("kept/visible.md"), "the check can see a path that is there");
  assert.ok(!onDisk().includes("secret/path-"), "while the sidecar still runs");
  book.put(row("again", "secret/cleared.md"));
  book.clear();
  store.close();
  assert.ok(!onDisk().includes("secret/"), "after a clear too");
});

test("an older build's table in the index is copied into a book in memory and left in place, for when the file is ours", () => {
  const index = new DatabaseSync(":memory:");
  index.exec("CREATE TABLE memoria_dead_letters (hash TEXT NOT NULL, path TEXT NOT NULL, reason TEXT NOT NULL, at TEXT NOT NULL, worker_set TEXT NOT NULL, PRIMARY KEY (hash, path))");
  index.prepare("INSERT INTO memoria_dead_letters VALUES ('h', 'a.md', 'refused', ?, 'fp:workers')").run(T1);
  index.prepare("INSERT INTO memoria_dead_letters VALUES ('g', 'deleted.md', 'refused', ?, 'fp:workers')").run(T1);
  index.exec("CREATE TABLE documents (hash TEXT, path TEXT, active INTEGER)");
  index.exec("INSERT INTO documents VALUES ('h', 'a.md', 1)");
  const book = new DeadLetterBook(null, clock(T2).now);
  moveLegacyLetters(index, book);
  book.retain(documentHolds(index, false));
  assert.deepEqual(book.all(), [row("h", "a.md", T1)]);
  assert.ok(legacyTable(index), "a copy in memory alone would be lost with the process");
  assert.deepEqual(index.prepare("SELECT path FROM memoria_dead_letters").all().map((r) => r.path), ["a.md"], "but a deleted document's row leaves it");
  book.clear();
  moveLegacyLetters(index, book);
  assert.deepEqual(book.all(), [], "a clear is not undone by copying again");
});

test("a busy index leaves a deleted document's row in an older build's table for next time, and the copy still counts", async () => {
  const indexFile = path.join(await tempDir("memoria-letters-"), "index.sqlite");
  const index = new DatabaseSync(indexFile);
  const other = new DatabaseSync(indexFile);
  try {
    index.exec("CREATE TABLE memoria_dead_letters (hash TEXT NOT NULL, path TEXT NOT NULL, reason TEXT NOT NULL, at TEXT NOT NULL, worker_set TEXT NOT NULL, PRIMARY KEY (hash, path))");
    index.prepare("INSERT INTO memoria_dead_letters VALUES ('g', 'deleted.md', 'refused', ?, 'fp:workers')").run(T1);
    index.exec("CREATE TABLE documents (hash TEXT, path TEXT, active INTEGER)");
    other.exec("BEGIN IMMEDIATE");
    const book = new DeadLetterBook(null, clock(T2).now);
    moveLegacyLetters(index, book);
    assert.deepEqual(paths(book), ["deleted.md"]);
    other.exec("ROLLBACK");
    moveLegacyLetters(index, book);
    assert.deepEqual(index.prepare("SELECT path FROM memoria_dead_letters").all(), []);
  } finally {
    other.close();
    index.close();
  }
});

const legacyTable = (db: DatabaseSync): boolean => db.prepare("SELECT 1 FROM sqlite_master WHERE name = 'memoria_dead_letters'").get() !== undefined;

test("an older build's table moves into the queue's file, rows added since the first copy included, and is dropped", async () => {
  const dir = await tempDir("memoria-letters-");
  const indexFile = path.join(dir, "index.sqlite");
  const index = new DatabaseSync(indexFile);
  const { store } = await queueFile();
  try {
    index.exec("CREATE TABLE memoria_dead_letters (hash TEXT NOT NULL, path TEXT NOT NULL, reason TEXT NOT NULL, at TEXT NOT NULL, worker_set TEXT NOT NULL, PRIMARY KEY (hash, path))");
    index.prepare("INSERT INTO memoria_dead_letters VALUES ('h', 'a.md', 'refused', ?, 'fp:workers')").run(T1);
    const book = new DeadLetterBook(store.deadLetters(), clock(T2).now);
    // Another writer holds the index: the rows still count, and the drop waits.
    const other = new DatabaseSync(indexFile);
    other.exec("BEGIN IMMEDIATE");
    other.prepare("INSERT INTO memoria_dead_letters VALUES ('g', 'b.md', 'refused', ?, 'fp:workers')").run(T1);
    moveLegacyLetters(index, book);
    assert.deepEqual(paths(book), ["a.md"]);
    assert.ok(legacyTable(index));
    other.exec("COMMIT");
    other.close();
    book.removeHash("h");
    moveLegacyLetters(index, book);
    assert.deepEqual(paths(book), ["b.md"], "the row added meanwhile moves, and the one embedded since does not come back");
    assert.equal(legacyTable(index), false);
    moveLegacyLetters(index, book);
    assert.deepEqual(paths(book), ["b.md"], "running again changes nothing");
  } finally {
    index.close();
    store.close();
  }
});

test("a copy that fails keeps the older build's table, so nothing is lost", () => {
  const index = new DatabaseSync(":memory:");
  index.exec("CREATE TABLE memoria_dead_letters (hash TEXT NOT NULL, path TEXT NOT NULL, reason TEXT NOT NULL, at TEXT NOT NULL, worker_set TEXT NOT NULL, PRIMARY KEY (hash, path))");
  index.prepare("INSERT INTO memoria_dead_letters VALUES ('h', 'a.md', 'refused', ?, 'fp:workers')").run(T1);
  // The queue file fails on the copy inside the index's transaction, the second read of it.
  class FailsSecondRead extends MemoryDeadLetters {
    reads = 0;
    override all(): DeadLetterRow[] {
      if (++this.reads === 2) throw new Error("disk I/O error");
      return super.all();
    }
  }
  const book = new DeadLetterBook(new FailsSecondRead(), clock(T2).now);
  assert.throws(() => moveLegacyLetters(index, book), /disk I\/O error/);
  assert.ok(legacyTable(index), "the drop rolled back with the failed copy");
  assert.equal((index.prepare("SELECT COUNT(*) AS n FROM memoria_dead_letters").get() as { n: number }).n, 1);
  assert.equal(index.isTransaction, false, "and no transaction is left open");
  moveLegacyLetters(index, book);
  assert.equal(legacyTable(index), false, "the next try moves it");
  assert.deepEqual(paths(book), ["a.md"]);
});

test("whether a document row holds a set-aside: any row, or only an active one", () => {
  const index = new DatabaseSync(":memory:");
  index.exec("CREATE TABLE documents (hash TEXT, path TEXT, active INTEGER)");
  index.exec("INSERT INTO documents VALUES ('h', 'live.md', 1), ('h', 'gone.md', 0)");
  const any = documentHolds(index, false);
  const active = documentHolds(index, true);
  assert.deepEqual([any("h", "live.md"), any("h", "gone.md"), any("h", "never.md"), any("x", "live.md")], [true, true, false, false]);
  assert.deepEqual([active("h", "live.md"), active("h", "gone.md")], [true, false]);
});

/** A table in memory that counts its scrubs. */
class CountingTable extends MemoryDeadLetters {
  scrubs = 0;
  override scrub(): void {
    this.scrubs++;
  }
}

test("a merge keeps the record it holds when the other is no newer, and a removal covers a row written in its own millisecond", () => {
  const book = new DeadLetterBook(new MemoryDeadLetters(), clock(T2).now);
  book.put(row("h", "a.md", T1, "held"));
  book.merge([row("h", "a.md", T1, "same time, from elsewhere")]);
  assert.equal(book.all()[0]!.reason, "held");
  book.removeHash("h");
  book.merge([row("h", "a.md", T2)]);
  assert.deepEqual(book.all(), [], "written as it was removed, so not after it");
});

test("by default a removal is timed by the clock, so a copy written before it stays removed", () => {
  const book = new DeadLetterBook(new MemoryDeadLetters());
  book.clear();
  book.merge([row("h", "a.md", "2000-01-01T00:00:00.000Z")]);
  assert.deepEqual(book.all(), []);
});

test("settling forgets every kind of removal, once the book is the file's", () => {
  const book = new DeadLetterBook(new MemoryDeadLetters(), clock(T2).now);
  book.put(row("h", "a.md", T1));
  book.put(row("g", "b.md", T1));
  book.removeHash("h");
  book.retain((hash) => hash !== "g");
  book.settle();
  book.merge([row("h", "a.md", T1), row("g", "b.md", T1)]);
  assert.deepEqual(paths(book), ["a.md", "b.md"]);
});

test("a second adopt keeps the book on the first file", async () => {
  const { store } = await queueFile();
  try {
    const book = new DeadLetterBook(null);
    book.adopt(store.deadLetters());
    book.adopt(new MemoryDeadLetters());
    book.put(row("h", "a.md"));
    assert.equal(store.deadLetters().all().length, 1);
  } finally {
    store.close();
  }
});

test("the table is scrubbed after rows are removed, and only then", () => {
  const table = new CountingTable();
  const book = new DeadLetterBook(table, clock(T2).now);
  assert.equal(book.retain(() => false), 0);
  assert.equal(book.clear(), 0);
  assert.equal(table.scrubs, 0, "nothing removed, nothing to scrub");
  book.put(row("h", "a.md", T1));
  book.put(row("g", "b.md", T1));
  assert.equal(book.retain((hash) => hash === "g"), 1);
  assert.equal(table.scrubs, 1);
  assert.equal(book.clear(), 1);
  assert.equal(table.scrubs, 2);

  const file = new CountingTable();
  file.put(row("f", "f.md", T1));
  const inMemory = new DeadLetterBook(null, clock(T2).now);
  inMemory.adopt(file);
  assert.equal(file.scrubs, 0, "an adopt that removes nothing");
  const cleared = new DeadLetterBook(null, clock(T2).now);
  cleared.clear();
  const other = new CountingTable();
  other.put(row("f", "f.md", T1));
  cleared.adopt(other);
  assert.deepEqual([other.all(), other.scrubs], [[], 1], "and one that does");
});

test("an older build's table goes only once it is moved, and a fault other than a busy index is not hidden", () => {
  const index = new DatabaseSync(":memory:");
  index.exec("CREATE TABLE memoria_dead_letters (hash TEXT NOT NULL, path TEXT NOT NULL, reason TEXT NOT NULL, at TEXT NOT NULL, worker_set TEXT NOT NULL, PRIMARY KEY (hash, path))");
  assert.throws(() => moveLegacyLetters(index, new DeadLetterBook(null)), /no such table: documents/, "pruning in memory needs the documents table");
  const book = new DeadLetterBook(new MemoryDeadLetters(), clock(T2).now);
  book.clear();
  moveLegacyLetters(index, book);
  assert.equal(index.isTransaction, false, "the move committed");
  assert.equal(legacyTable(index), false);
  book.merge([row("h", "a.md", T1)]);
  assert.deepEqual(paths(book), ["a.md"], "and with nothing left to merge, the book settled");
  const again = new DeadLetterBook(new MemoryDeadLetters(), clock(T2).now);
  again.clear();
  moveLegacyLetters(index, again);
  again.merge([row("h", "a.md", T1)]);
  assert.deepEqual(paths(again), ["a.md"], "it settles when there was no table to move, too");
});

test("a queue file an earlier build made gains the table and keeps its schema version, so that build can still open it", async () => {
  const file = path.join(await tempDir("memoria-letters-"), "q.sqlite");
  // As an earlier build left it: this build's file, less the table it adds.
  new SqliteJobStore(file).close();
  const earlier = new DatabaseSync(file);
  earlier.exec("DROP TABLE dead_letters");
  earlier.close();
  const store = new SqliteJobStore(file);
  store.deadLetters().put(row("h", "a.md"));
  store.close();
  const after = new DatabaseSync(file, { readOnly: true });
  try {
    assert.equal((after.prepare("PRAGMA user_version").get() as { user_version: number }).user_version, 1);
    assert.equal((after.prepare("SELECT COUNT(*) AS n FROM dead_letters").get() as { n: number }).n, 1);
  } finally {
    after.close();
  }
});
