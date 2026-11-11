/**
 * What a delete leaves in index.sqlite, on the real SDK and sqlite-vec with a mock
 * worker: nothing of the deleted document, and nothing taken from one sharing its text.
 *
 * Opt in with MEMORIA_QMD_INTEGRATION=1 (CI sets it; `npm test` does not).
 */
import assert from "node:assert/strict";
import { mkdir, readFile, rename, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import { existsSync } from "node:fs";
import { createRequire } from "node:module";
import { test } from "node:test";
import type { PoolWorkerConfig } from "../src/llm/types.ts";
import { ROOT_MARKER } from "../src/collection-roots.ts";
import { DeadLetterBook } from "../src/dead-letters.ts";
import { purgeDeleted } from "../src/purge.ts";
import { openBackend } from "../src/store.ts";
import { tempDir } from "./helpers.ts";
import { MockWorker } from "./mock-worker.ts";

const Database = createRequire(import.meta.url)("better-sqlite3") as new (file: string) => { exec(sql: string): void; close(): void };
const skip = process.env.MEMORIA_QMD_INTEGRATION === "1" ? false : "set MEMORIA_QMD_INTEGRATION=1";

const PHRASE = "holding the sentence only this document says";
const SECRET = `Deleted text, ${PHRASE}. `.repeat(40);
/** Short enough to sit in a page beside other rows, where a delete without secure_delete leaves its bytes. */
const TINY = "a short note the tiny document alone holds";
const SHARED = "Text two documents hold word for word, so they share one content row. ".repeat(40);

interface Index {
  corpus: string;
  dbPath: string;
}

async function index(): Promise<Index> {
  const root = await tempDir("memoria-purge-it-");
  const corpus = path.join(root, "notes");
  await mkdir(path.join(corpus, "copies"), { recursive: true });
  await writeFile(path.join(corpus, "secret.md"), `# Secret\n\n${SECRET}\n`, "utf8");
  await writeFile(path.join(corpus, "shared.md"), `# Shared\n\n${SHARED}\n`, "utf8");
  await writeFile(path.join(corpus, "copies", "shared.md"), `# Shared\n\n${SHARED}\n`, "utf8");
  await writeFile(path.join(corpus, "kept.md"), "# Kept\n\nNothing happens to this one.\n", "utf8");
  await writeFile(path.join(corpus, "tiny.md"), `# Tiny\n\n${TINY}.\n`, "utf8");
  const dbPath = path.join(root, "index.sqlite");
  const qmd = await import("@tobilu/qmd");
  const builder = await qmd.createStore({ dbPath, config: { collections: { notes: { path: corpus, pattern: "**/*.md" } } } });
  // As the sidecar and init-index write, so the index starts with nothing left in free space: what is found later, the delete left.
  (builder.internal.db as unknown as { exec(sql: string): void }).exec("PRAGMA secure_delete = ON");
  await builder.update();
  await builder.close();
  return { corpus, dbPath };
}

const worker = (mock: MockWorker): PoolWorkerConfig => ({ name: "only", url: mock.url, token: undefined, capabilities: ["embed", "rerank", "generate"], priority: 1, maxConcurrent: 4 });

type Db = { prepare(sql: string): { get(...a: unknown[]): unknown; all(...a: unknown[]): unknown[] } };

/** Every row anywhere in the index that holds this hash, or full text with this snippet in it. */
interface Traces {
  content: number;
  documents: number;
  contentVectors: number;
  vectors: number;
  fullText: number;
}

function traces(db: Db, hash: string, snippet: string): Traces {
  const count = (sql: string, ...args: unknown[]): number => (db.prepare(sql).get(...args) as { n: number }).n;
  return {
    content: count("SELECT COUNT(*) AS n FROM content WHERE hash = ?", hash),
    documents: count("SELECT COUNT(*) AS n FROM documents WHERE hash = ?", hash),
    contentVectors: count("SELECT COUNT(*) AS n FROM content_vectors WHERE hash = ?", hash),
    vectors: count("SELECT COUNT(*) AS n FROM vectors_vec WHERE hash_seq LIKE ? || '_%'", hash),
    fullText: count("SELECT COUNT(*) AS n FROM documents_fts WHERE body LIKE '%' || ? || '%'", snippet),
  };
}

const hashOf = (db: Db, file: string): string => (db.prepare("SELECT hash FROM documents WHERE path = ?").get(file) as { hash: string }).hash;

test("a deleted document leaves no content, vectors or full text in the index, and no set-aside beside it", { skip }, async () => {
  const { corpus, dbPath } = await index();
  const mock = await new MockWorker(8).start();
  const letters = new DeadLetterBook(null);
  const backend = await openBackend({ dbPath, keepWarm: false, deadLetters: letters, llm: { mode: "remote", workers: [worker(mock)], embedDimensions: 8, fallback: false, statusPollMs: 0, embedBatchBytes: 2048 } });
  const qmd = await import("@tobilu/qmd");
  try {
    await backend.embed({ force: false });
    const reader = await qmd.createStore({ dbPath });
    let hash = "";
    try {
      const db = reader.internal.db as unknown as Db;
      hash = hashOf(db, "secret.md");
      // A set-aside for it, as a worker refusing one of its chunks leaves. It names the deleted path.
      letters.put({ hash, path: "secret.md", reason: "refused", at: "2026-01-01T00:00:00Z", workerSet: "w" });
      letters.put({ hash: hashOf(db, "kept.md"), path: "kept.md", reason: "refused", at: "2026-01-01T00:00:00Z", workerSet: "w" });
      const before = traces(db, hash, "only this document says");
      assert.ok(before.content === 1 && before.contentVectors > 0 && before.vectors > 0 && before.fullText === 1, JSON.stringify(before));
    } finally {
      await reader.close();
    }

    // Into the file itself, so a delete that left its bytes in free pages would show.
    const early = new Database(dbPath);
    early.exec("PRAGMA wal_checkpoint(PASSIVE)");
    early.close();
    assert.notEqual((await readFile(dbPath)).indexOf(Buffer.from(PHRASE)), -1, "the text was in the file before the delete");

    await rm(path.join(corpus, "secret.md"));
    await rm(path.join(corpus, "tiny.md"));
    await backend.update({});

    const after = await qmd.createStore({ dbPath });
    try {
      const db = after.internal.db as unknown as Db;
      assert.deepEqual(traces(db, hash, "only this document says"), { content: 0, documents: 0, contentVectors: 0, vectors: 0, fullText: 0 });
      assert.deepEqual(letters.all().map((l) => l.path), ["kept.md"], "the deleted path's set-aside goes with it, and a live one's stays");
      assert.equal(db.prepare("SELECT 1 FROM sqlite_master WHERE name = 'memoria_dead_letters'").get(), undefined, "the index holds no table of ours");
      assert.equal((db.prepare("SELECT COUNT(*) AS n FROM documents WHERE active = 0").get() as { n: number }).n, 0, "no inactive row is left");
      assert.equal((db.prepare("SELECT COUNT(*) AS n FROM documents WHERE path = 'kept.md' AND active = 1").get() as { n: number }).n, 1);
    } finally {
      await after.close();
    }
  } finally {
    await backend.close();
    await mock.stop();
  }
  // Nor is its text anywhere in the file, free pages included, once the log is folded in. Its words alone can stay in the
  // full-text index's word lists until FTS5 merges them: docs/deployment.md says so.
  const checkpoint = new Database(dbPath);
  checkpoint.exec("PRAGMA wal_checkpoint(TRUNCATE)");
  checkpoint.close();
  const bytes = await readFile(dbPath);
  assert.equal(bytes.indexOf(Buffer.from(PHRASE)), -1, "the deleted text is still in index.sqlite");
  assert.equal(bytes.indexOf(Buffer.from(TINY)), -1, "a short deleted text is still in index.sqlite");
});

/** Rows left in the index of every kind, whatever their collection. */
async function rowCounts(dbPath: string): Promise<{ documents: number; inactive: number; content: number; contentVectors: number }> {
  const qmd = await import("@tobilu/qmd");
  const reader = await qmd.createStore({ dbPath });
  try {
    const db = reader.internal.db as unknown as Db;
    const count = (sql: string): number => (db.prepare(sql).get() as { n: number }).n;
    return {
      documents: count("SELECT COUNT(*) AS n FROM documents"),
      inactive: count("SELECT COUNT(*) AS n FROM documents WHERE active = 0"),
      content: count("SELECT COUNT(*) AS n FROM content"),
      contentVectors: count("SELECT COUNT(*) AS n FROM content_vectors"),
    };
  } finally {
    await reader.close();
  }
}

async function remoteBackend(dbPath: string, mock: MockWorker, warnings: string[]): Promise<Awaited<ReturnType<typeof openBackend>>> {
  return openBackend({ dbPath, keepWarm: false, llm: { mode: "remote", workers: [worker(mock)], embedDimensions: 8, fallback: false, statusPollMs: 0, embedBatchBytes: 2048 }, onLlmWarning: (m) => warnings.push(m) });
}

const ALL = ["secret.md", "shared.md", "copies/shared.md", "kept.md", "tiny.md"];

test("a collection's folder gone for a scan keeps everything, says so, and its return re-embeds nothing", { skip }, async () => {
  const { corpus, dbPath } = await index();
  const mock = await new MockWorker(8).start();
  const warnings: string[] = [];
  const backend = await remoteBackend(dbPath, mock, warnings);
  try {
    await backend.embed({ force: false });
    const embeds = mock.count("/v1/embed");
    const away = corpus + "-away";
    await rename(corpus, away);
    assert.equal((await backend.update({})).removed, 5, "the scan found every document gone");
    assert.ok(warnings.some((w) => w.startsWith("Collection notes:") && /missing or cannot be read/.test(w)), warnings.join("\n"));
    await rename(away, corpus);
    const back = await backend.update({});
    assert.equal(back.needsEmbedding, 0, "every vector was still there");
    assert.equal((await backend.embed({ force: false })).docsProcessed, 0);
    assert.equal(mock.count("/v1/embed"), embeds, "no worker was asked to embed again");
  } finally {
    await backend.close();
    await mock.stop();
  }
});

test("a folder that is there but whose last document was deleted keeps no content or vectors", { skip }, async () => {
  const { corpus, dbPath } = await index();
  const mock = await new MockWorker(8).start();
  const warnings: string[] = [];
  const backend = await remoteBackend(dbPath, mock, warnings);
  try {
    await backend.embed({ force: false });
    // A scan that sees its documents marks the root as the real one.
    await backend.update({});
    assert.ok(existsSync(path.join(corpus, ROOT_MARKER)));
    for (const file of ALL) await rm(path.join(corpus, file));
    assert.equal((await backend.update({})).removed, 5);
    assert.deepEqual(await rowCounts(dbPath), { documents: 0, inactive: 0, content: 0, contentVectors: 0 });
    assert.deepEqual(warnings, []);
  } finally {
    await backend.close();
    await mock.stop();
  }
});

test("an empty folder with no marker, as under a mount that failed, keeps everything and says so", { skip }, async () => {
  const { corpus, dbPath } = await index();
  const mock = await new MockWorker(8).start();
  const warnings: string[] = [];
  const backend = await remoteBackend(dbPath, mock, warnings);
  try {
    await backend.embed({ force: false });
    await backend.update({});
    const before = await rowCounts(dbPath);
    // The mount fails: what was on it is elsewhere, and an empty folder stands in its place.
    const mounted = corpus + "-mounted";
    await rename(corpus, mounted);
    await mkdir(corpus);
    assert.equal((await backend.update({})).removed, 5);
    assert.deepEqual(await rowCounts(dbPath), { ...before, inactive: before.documents }, "only marked inactive");
    assert.ok(warnings.some((w) => w.startsWith("Collection notes:") && w.includes(ROOT_MARKER)), warnings.join("\n"));
    await rm(corpus, { recursive: true });
    await rename(mounted, corpus);
    assert.equal((await backend.update({})).needsEmbedding, 0, "the mount back, nothing needs embedding");
  } finally {
    await backend.close();
    await mock.stop();
  }
});

test("a deleted document whose text another still holds leaves that text and its vectors to it", { skip }, async () => {
  const { corpus, dbPath } = await index();
  const mock = await new MockWorker(8).start();
  const backend = await openBackend({ dbPath, keepWarm: false, llm: { mode: "remote", workers: [worker(mock)], embedDimensions: 8, fallback: false, statusPollMs: 0, embedBatchBytes: 2048 } });
  const qmd = await import("@tobilu/qmd");
  try {
    await backend.embed({ force: false });
    const reader = await qmd.createStore({ dbPath });
    let before: Traces;
    let hash = "";
    try {
      hash = hashOf(reader.internal.db as unknown as Db, "shared.md");
      before = traces(reader.internal.db as unknown as Db, hash, "share one content row");
      assert.ok(before.content === 1 && before.documents === 2 && before.vectors > 0, JSON.stringify(before));
    } finally {
      await reader.close();
    }

    await rm(path.join(corpus, "shared.md"));
    await backend.update({});
    const embedCalls = mock.count("/v1/embed");

    const after = await qmd.createStore({ dbPath });
    try {
      const left = traces(after.internal.db as unknown as Db, hash, "share one content row");
      assert.deepEqual(left, { ...before, documents: 1, fullText: 1 }, "the copy keeps its content row and every vector");
    } finally {
      await after.close();
    }
    const found = await backend.searchDetailed({ queries: [{ type: "vec", query: "two documents word for word" }], limit: 5, minScore: 0, rerank: false });
    assert.ok(found.hits.some((hit) => hit.file.endsWith("copies/shared.md")), "the copy is still found by its vectors");
    assert.equal((await backend.embed({ force: false })).docsProcessed, 0, "and needs no new embedding");
    assert.equal(mock.count("/v1/embed"), embedCalls + 1, "only the query was embedded");
  } finally {
    await backend.close();
    await mock.stop();
  }
});

test("a busy index changes nothing, and the next purge removes it all", { skip }, async () => {
  const { corpus, dbPath } = await index();
  const qmd = await import("@tobilu/qmd");
  await rm(path.join(corpus, "secret.md"));
  const scanner = await qmd.createStore({ dbPath });
  await scanner.update();
  const holder = await qmd.createStore({ dbPath });
  try {
    const db = scanner.internal.db as unknown as Db & { exec(sql: string): void };
    const hash = (db.prepare("SELECT hash FROM documents WHERE path = 'secret.md'").get() as { hash: string }).hash;
    const blocker = holder.internal.db as unknown as { exec(sql: string): void };
    blocker.exec("BEGIN IMMEDIATE");
    assert.equal(purgeDeleted(db as never), null, "busy: nothing changed");
    const contentRows = (): number => (db.prepare("SELECT COUNT(*) AS n FROM content WHERE hash = ?").get(hash) as { n: number }).n;
    assert.equal(contentRows(), 1);
    blocker.exec("ROLLBACK");
    assert.deepEqual(purgeDeleted(db as never), { documents: 1, content: 1, vectors: 0 });
    assert.equal(contentRows(), 0);
  } finally {
    await holder.close();
    await scanner.close();
  }
});

