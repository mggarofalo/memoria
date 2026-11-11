import assert from "node:assert/strict";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";
import { test } from "node:test";
import { DeadLetterBook, MemoryDeadLetters, type DeadLetterRow } from "../src/dead-letters.ts";
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
