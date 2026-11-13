/**
 * Index scans on their own thread, on the real SDK: the main thread keeps answering while
 * one runs, readers never see a document half-written, a writer on the main thread is not
 * locked out, and a scan stops, or is cut off, without leaving the index out of step.
 *
 * Opt in with MEMORIA_QMD_INTEGRATION=1 (CI sets it; `npm test` does not).
 */
import assert from "node:assert/strict";
import { spawn, type ChildProcess } from "node:child_process";
import { createRequire } from "node:module";
import { appendFile, mkdir, readdir, readFile, rename, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import { monitorEventLoopDelay } from "node:perf_hooks";
import { createInterface } from "node:readline";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { buildApp } from "../src/app.ts";
import { queueDbPathFor, SqliteJobStore } from "../src/queue-store.ts";
import { ScanStopped } from "../src/scan-batches.ts";
import { freePort } from "../../worker/test/ports.ts";
import { MAIN_WRITE_YIELD_MS, openBackend, scanIndex, type OpenedBackend } from "../src/store.ts";
import { authHeaders, tempDir, testConfig } from "./helpers.ts";

const skip = process.env.MEMORIA_QMD_INTEGRATION === "1" ? false : "set MEMORIA_QMD_INTEGRATION=1";

/** Enough documents that the scan takes seconds: on the main thread, that long without an answer. */
const DOCUMENTS = 3_000;

function transcript(i: number, extraTurns = 0): string {
  const turns: string[] = [];
  for (let t = 0; t < 12 + extraTurns; t++) turns.push(`## ${t % 2 ? "Assistant" : "User"}\n\nTurn ${t} of session ${i} talks about the sidecar index, the worker pool and document ${i * 7}.\n`);
  return `---\nsession_id: s${i}\ndate: 2026-0${1 + (i % 9)}-1${i % 10}\nproject: P${i % 5}\ngit_branch: main\ntags:\n  - project/P${i % 5}\n---\n\n# Session ${i}\n\n${turns.join("\n")}`;
}

interface Index {
  corpus: string;
  dbPath: string;
}

async function index(documents = DOCUMENTS, extraTurns = 0): Promise<Index> {
  const root = await tempDir("memoria-scan-thread-it-");
  const corpus = path.join(root, "sessions");
  await mkdir(corpus, { recursive: true });
  for (let i = 0; i < documents; i++) await writeFile(path.join(corpus, `s-${String(i).padStart(5, "0")}.md`), transcript(i, extraTurns), "utf8");
  const dbPath = path.join(root, "index.sqlite");
  const qmd = await import("@tobilu/qmd");
  const builder = await qmd.createStore({ dbPath, config: { collections: { sessions: { path: corpus, pattern: "**/*.md" } } } });
  await builder.update();
  await builder.close();
  return { corpus, dbPath };
}

/** Change every document, so the scan rewrites each one: its row, its content and its full-text entry. */
async function changeAll(corpus: string, note: string): Promise<void> {
  for (const name of await readdir(corpus)) if (name.endsWith(".md")) await appendFile(path.join(corpus, name), `\n${note}\n`, "utf8");
}

async function open(dbPath: string): Promise<OpenedBackend> {
  return openBackend({ dbPath, keepWarm: false });
}


/** Active documents whose full-text entry is missing or holds other text: a document seen half-written. */
const TORN = `SELECT COUNT(*) AS n FROM documents d JOIN content c ON c.hash = d.hash
  WHERE d.active = 1 AND NOT EXISTS (SELECT 1 FROM documents_fts f WHERE f.rowid = d.id AND f.body = c.doc)`;

test("while a scan runs, /health and a search answer in tens of milliseconds", { skip, timeout: 600_000 }, async () => {
  const { dbPath, corpus } = await index();
  await changeAll(corpus, "A change, so the scan writes every document.");
  const backend = await open(dbPath);
  const app = await buildApp({ backend, config: testConfig({ dbPath }) });
  const delay = monitorEventLoopDelay({ resolution: 5 });
  try {
    const health: number[] = [];
    const searches: number[] = [];
    let done = false;
    const scan = app.ctx.jobs.enqueue({ collections: ["sessions"], embed: false, force: false, reason: "test" });
    delay.enable();
    const started = Date.now();
    const probe = (async () => {
      while (!done) {
        let t = performance.now();
        assert.equal((await app.inject({ method: "GET", url: "/health" })).statusCode, 200);
        health.push(performance.now() - t);
        t = performance.now();
        const search = await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { searches: [{ type: "lex", query: "sidecar index" }], limit: 3, rerank: false } });
        assert.equal(search.statusCode, 200);
        searches.push(performance.now() - t);
        await new Promise((r) => setTimeout(r, 10));
      }
    })();
    await app.ctx.jobs.idle();
    done = true;
    await probe;
    delay.disable();
    const took = Date.now() - started;
    const job = app.ctx.jobs.get(scan.id)!;
    assert.equal(job.state, "succeeded", job.error ?? "");
    assert.equal(job.result?.update.updated, DOCUMENTS, "the scan rewrote every document");
    assert.equal(job.result?.metadata.withMetadata, DOCUMENTS, "and read their metadata on its thread");
    assert.ok(took > 1_000, `a scan long enough to matter: ${took} ms`);
    // On the main thread, the scan held the event loop for all of it: /health waited the whole scan.
    // The usual answer is what shows the scan is elsewhere. The slowest only has to stay well
    // short of the scan, as the test runner runs other files beside this one, and on a small CI
    // runner they take the CPU now and then: 434 ms once, during a 4 s scan.
    const at = (xs: number[], q: number): number => [...xs].sort((a, b) => a - b)[Math.floor(q * (xs.length - 1))]!;
    const longestStall = delay.max / 1e6;
    const bound = Math.min(1_000, took / 4);
    assert.ok(health.length > 20, `health was asked ${health.length} times`);
    assert.ok(at(health, 0.9) < 50, `/health took ${at(health, 0.9).toFixed(0)} ms at the 90th percentile during a ${took} ms scan`);
    assert.ok(at(health, 1) < bound, `slowest /health ${at(health, 1).toFixed(0)} ms during a ${took} ms scan`);
    assert.ok(longestStall < bound, `the event loop stalled for ${longestStall.toFixed(0)} ms during a ${took} ms scan`);
    assert.ok(at(searches, 0.9) < 500, `a search took ${at(searches, 0.9).toFixed(0)} ms at the 90th percentile`);
  } finally {
    await app.close();
    await backend.close();
  }
});

test("a reader on the main thread never sees a document half-written by the scan", { skip, timeout: 600_000 }, async () => {
  const { dbPath, corpus } = await index(1_500);
  await changeAll(corpus, "Each document gets new text, so each gets a new full-text entry.");
  const qmd = await import("@tobilu/qmd");
  const reader = await qmd.createStore({ dbPath });
  const backend = await open(dbPath);
  try {
    const torn = reader.internal.db.prepare(TORN);
    let reads = 0;
    let seen = 0;
    let scanning = true;
    const scan = backend.update({}).finally(() => (scanning = false));
    // A failure below must be reported as itself, not as this scan, stopped by the close.
    scan.catch(() => {});
    while (scanning) {
      seen = Math.max(seen, (torn.get() as { n: number }).n);
      reads++;
      await new Promise((r) => setImmediate(r));
    }
    const result = await scan;
    assert.equal(result.updated, 1_500);
    assert.ok(reads > 10, `read ${reads} times during the scan`);
    assert.equal(seen, 0, "no read caught a document between its row and its text");
    assert.equal((torn.get() as { n: number }).n, 0);
  } finally {
    await backend.close();
    await reader.close();
  }
});

/** Another process's connection, as an operator's `qmd` has: it waits up to `busyMs` for the lock. */
const Database = createRequire(import.meta.url)("better-sqlite3") as new (file: string, options?: { timeout?: number }) => { prepare(sql: string): { run(...a: unknown[]): unknown }; close(): void };

test("a scan with nothing to change takes no write lock: another connection writes as if it were not there", { skip, timeout: 600_000 }, async () => {
  const { dbPath } = await index();
  const backend = await open(dbPath);
  const outsider = new Database(dbPath, { timeout: 2_000 });
  try {
    const insert = outsider.prepare("INSERT OR REPLACE INTO llm_cache (hash, result, created_at) VALUES (?, ?, ?)");
    let scanning = true;
    const scan = backend.update({}).finally(() => (scanning = false));
    scan.catch(() => {});
    const waits: number[] = [];
    let n = 0;
    while (scanning) {
      const t = performance.now();
      insert.run(`outsider-${n++}`, "{}", new Date().toISOString());
      waits.push(performance.now() - t);
      await new Promise((r) => setTimeout(r, 5));
    }
    assert.equal((await scan).unchanged, DOCUMENTS);
    assert.ok(waits.length > 20, `wrote ${waits.length} times during the scan`);
    assert.ok(Math.max(...waits) < 100, `the longest write waited ${Math.max(...waits).toFixed(0)} ms`);
  } finally {
    outsider.close();
    await backend.close();
  }
});

test("removing thousands of deleted documents never holds a write on the main thread for long", { skip, timeout: 600_000 }, async () => {
  // Transcripts of about 20 KB, as real ones run: removing their text with secure_delete is what takes the time.
  const { dbPath, corpus } = await index(DOCUMENTS, 160);
  const deleted = DOCUMENTS - 500;
  for (let i = 0; i < deleted; i++) await rm(path.join(corpus, `s-${String(i).padStart(5, "0")}.md`));
  const backend = await open(dbPath);
  try {
    let scanning = true;
    const scan = backend.update({}).finally(() => (scanning = false));
    scan.catch(() => {});
    const waits: number[] = [];
    while (scanning) {
      const t = performance.now();
      // A short busy timeout of its own (250 ms): it throws if a group holds the lock longer.
      backend.purgeCache();
      waits.push(performance.now() - t);
      await new Promise((r) => setTimeout(r, 5));
    }
    const result = await scan;
    assert.equal(result.removed, deleted);
    assert.ok(waits.length > 5, `wrote ${waits.length} times during the scan`);
    assert.ok(Math.max(...waits) < 250, `the longest write waited ${Math.max(...waits).toFixed(0)} ms`);
  } finally {
    await backend.close();
  }
});

test("a write on the main thread during a scan waits for a group, not the scan, and neither fails", { skip, timeout: 600_000 }, async () => {
  const { dbPath, corpus } = await index(1_500);
  await changeAll(corpus, "Changed, so the scan holds the write lock group after group.");
  const backend = await open(dbPath);
  try {
    let scanning = true;
    const scan = backend.update({}).finally(() => (scanning = false));
    // A failure below must be reported as itself, not as this scan, stopped by the close.
    scan.catch(() => {});
    const waits: number[] = [];
    while (scanning) {
      const t = performance.now();
      // A search caching a rerank score writes this table, on the main thread.
      backend.purgeCache();
      waits.push(performance.now() - t);
      await new Promise((r) => setTimeout(r, 5));
    }
    assert.equal((await scan).updated, 1_500);
    assert.ok(waits.length > 5, `wrote ${waits.length} times during the scan`);
    assert.ok(Math.max(...waits) < 250, `the longest write waited ${Math.max(...waits).toFixed(0)} ms`);
  } finally {
    await backend.close();
  }
});

test("a scan asked to stop stops between documents, keeps what it wrote, and the next scan finishes the job", { skip, timeout: 600_000 }, async () => {
  const { dbPath, corpus } = await index(2_000);
  await changeAll(corpus, "Changed once.");
  const backend = await open(dbPath);
  try {
    const controller = new AbortController();
    const scan = backend.update({ signal: controller.signal });
    await new Promise((r) => setTimeout(r, 400));
    const asked = Date.now();
    controller.abort();
    await assert.rejects(scan, ScanStopped);
    assert.ok(Date.now() - asked < 1_000, `stopped ${Date.now() - asked} ms after it was asked`);
    const qmd = await import("@tobilu/qmd");
    const reader = await qmd.createStore({ dbPath });
    try {
      assert.equal((reader.internal.db.prepare(TORN).get() as { n: number }).n, 0, "nothing half-written");
    } finally {
      await reader.close();
    }
    const again = await backend.update({});
    assert.ok(again.updated > 0 && again.updated < 2_000, `the first scan got partway: ${again.updated} left to rewrite`);
    assert.equal(again.updated + again.unchanged, 2_000);
    assert.equal((await backend.update({})).unchanged, 2_000, "and the next one finds nothing to do");
  } finally {
    await backend.close();
  }
});

test("closing the backend mid-scan stops the scan, and the index is whole for the next start", { skip, timeout: 600_000 }, async () => {
  const { dbPath, corpus } = await index(2_000);
  await changeAll(corpus, "Changed again.");
  const backend = await open(dbPath);
  const scan = backend.update({});
  scan.catch(() => {});
  await new Promise((r) => setTimeout(r, 400));
  const started = Date.now();
  await backend.close();
  assert.ok(Date.now() - started < 2_000, `closed in ${Date.now() - started} ms`);
  await assert.rejects(scan, ScanStopped);
  const next = await open(dbPath);
  try {
    const qmd = await import("@tobilu/qmd");
    const reader = await qmd.createStore({ dbPath });
    try {
      assert.equal((reader.internal.db.prepare(TORN).get() as { n: number }).n, 0);
    } finally {
      await reader.close();
    }
    const resumed = await next.update({});
    assert.equal(resumed.updated + resumed.unchanged, 2_000);
  } finally {
    await next.close();
  }
});

// --- the scan itself, on this thread ---------------------------------------------------------------
// What the thread runs, called directly: the same code, where a test can see every line it runs.

const noWrites = (): Int32Array => new Int32Array(new SharedArrayBuffer(4));

test("scanIndex reports the scan, reads the metadata it was asked for, and removes what a deleted document left", { skip, timeout: 120_000 }, async () => {
  const { dbPath, corpus } = await index(30);
  const phrase = "a sentence only the deleted document ever held, zebra quartz";
  await writeFile(path.join(corpus, "gone.md"), `# Gone\n\n${`${phrase}. `.repeat(30)}\n`, "utf8");
  await scanIndex({ dbPath, batchMs: 25, mainWrites: noWrites() }, () => false);
  await rm(path.join(corpus, "gone.md"));
  await appendFile(path.join(corpus, "s-00001.md"), "\nOne change.\n", "utf8");
  const scanned = await scanIndex({ dbPath, collections: ["sessions"], batchMs: 25, mainWrites: noWrites(), headBytes: 8192 }, () => false);
  assert.deepEqual({ ...scanned, metadata: undefined }, { collections: 1, indexed: 0, updated: 1, unchanged: 29, removed: 1, skipped: 0, needsEmbedding: 30, warnings: [], metadata: undefined });
  assert.equal(scanned.metadata?.documents, 30);
  assert.equal(scanned.metadata?.entries.find(([key]) => key === "sessions/s-00004.md")?.[1].project, "P4");
  // Removed in the file itself, with secure_delete: no bytes of it left in free pages.
  const bytes = Buffer.concat([await readFile(dbPath), await readFile(`${dbPath}-wal`).catch(() => Buffer.alloc(0))]);
  assert.equal(bytes.indexOf(Buffer.from(phrase)), -1, "the deleted text is gone from the file");
  const none = await scanIndex({ dbPath, batchMs: 25, mainWrites: noWrites() }, () => false);
  assert.equal(none.metadata, undefined, "no metadata unless asked");
  assert.equal(none.unchanged, 30);
});

test("scanIndex passes on what the purge has to say, as when a collection's folder is gone", { skip, timeout: 120_000 }, async () => {
  const { dbPath, corpus } = await index(5);
  await rename(corpus, `${corpus}-away`);
  const scanned = await scanIndex({ dbPath, batchMs: 25, mainWrites: noWrites() }, () => false);
  assert.equal(scanned.removed, 5);
  assert.equal(scanned.warnings.length, 1);
  assert.match(scanned.warnings[0]!, /Collection sessions: its folder is missing/);
});

test("scanIndex asked to stop from the start writes nothing, and one stopped partway keeps whole documents", { skip, timeout: 120_000 }, async () => {
  const { dbPath, corpus } = await index(300);
  await changeAll(corpus, "Changed for the stop.");
  await assert.rejects(scanIndex({ dbPath, batchMs: 0, mainWrites: noWrites() }, () => true), ScanStopped);
  let documents = 0;
  // Each document ends a group at batchMs 0, and the stop comes after the 100th.
  await assert.rejects(scanIndex({ dbPath, batchMs: 0, mainWrites: noWrites() }, () => ++documents > 100), ScanStopped);
  const qmd = await import("@tobilu/qmd");
  const reader = await qmd.createStore({ dbPath });
  try {
    assert.equal((reader.internal.db.prepare(TORN).get() as { n: number }).n, 0);
  } finally {
    await reader.close();
  }
  const rest = await scanIndex({ dbPath, batchMs: 25, mainWrites: noWrites() }, () => false);
  assert.equal(rest.updated, 200, "the first 100 were kept, the rest left for this scan");
});

test("scanIndex waits out a write under way on the main thread before each group", { skip, timeout: 120_000 }, async () => {
  const { dbPath, corpus } = await index(20);
  await changeAll(corpus, "Changed for the wait.");
  const writers = noWrites();
  Atomics.store(writers, 0, 1);
  const started = performance.now();
  // The write never ends here, so the scan waits the cap before its first group, then goes on.
  const scanned = await scanIndex({ dbPath, batchMs: 60_000, mainWrites: writers }, () => false);
  assert.ok(performance.now() - started >= MAIN_WRITE_YIELD_MS - 5, "it waited for the write first");
  assert.equal(scanned.updated, 20);
});

// --- the real process ------------------------------------------------------------------------------

// Windows has no SIGTERM to send: killing a process there ends it with no handler run.
const posixOnly = skip || (process.platform === "win32" ? "needs POSIX signals" : false);
const mainEntry = fileURLToPath(new URL("../src/main.ts", import.meta.url));
const SECRET = "Xk29vQmB7fLp3ZtRw";
const DRAIN_MS = 1_000;
const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

async function sidecar(dbPath: string): Promise<{ base: string; child: ChildProcess; exited: Promise<number | null>; output: () => string }> {
  const port = await freePort();
  const child = spawn(process.execPath, [mainEntry], {
    env: { ...process.env, MEMORIA_QMD_DB_PATH: dbPath, MEMORIA_QMD_PORT: String(port), MEMORIA_QMD_WARMUP: "0", MEMORIA_QMD_SECRET: SECRET, MEMORIA_QMD_SHUTDOWN_DRAIN_MS: String(DRAIN_MS), MEMORIA_QMD_REINDEX_DEBOUNCE_MS: "0" },
    stdio: ["ignore", "pipe", "inherit"],
  });
  const exited = new Promise<number | null>((resolve) => child.once("exit", (code) => resolve(code)));
  let output = "";
  const listening = new Promise<void>((resolve) => {
    createInterface({ input: child.stdout! }).on("line", (line) => {
      output += `${line}\n`;
      if (line.includes("memoria-qmd listening")) resolve();
    });
  });
  await Promise.race([listening, sleep(60_000)]);
  assert.match(output, /memoria-qmd listening/, output);
  return { base: `http://127.0.0.1:${port}`, child, exited, output: () => output };
}

const auth = { authorization: `Bearer ${SECRET}`, "content-type": "application/json" };

async function jobState(base: string, id: string): Promise<{ state: string; result: { update: { updated: number; unchanged: number } } | null }> {
  return ((await (await fetch(`${base}/reindex/jobs/${id}`, { headers: auth })).json()) as { job: { state: string; result: { update: { updated: number; unchanged: number } } | null } }).job;
}

test("SIGTERM mid-scan stops the scan within the drain, keeps the job queued, and the next start finishes it", { skip: posixOnly, timeout: 600_000 }, async () => {
  const { dbPath, corpus } = await index();
  await changeAll(corpus, "Changed before the stop.");
  const first = await sidecar(dbPath);
  let id: string;
  try {
    const queued = await fetch(`${first.base}/reindex`, { method: "POST", headers: auth, body: JSON.stringify({ embed: false }) });
    id = ((await queued.json()) as { job: { id: string } }).job.id;
    for (let waited = 0; (await jobState(first.base, id)).state !== "running" && waited < 10_000; waited += 20) await sleep(20);
    await sleep(300);
    // Health answers while the scan runs, the signal included.
    const t = performance.now();
    assert.equal((await fetch(`${first.base}/health`)).status, 200);
    assert.ok(performance.now() - t < 1_000, "health answered during the scan");
    const signalled = Date.now();
    first.child.kill("SIGTERM");
    const code = await Promise.race([first.exited, sleep(DRAIN_MS + 8_000).then(() => "still running" as const)]);
    const took = Date.now() - signalled;
    assert.equal(code, 0, first.output());
    assert.ok(took < DRAIN_MS + 3_000, `exited ${took} ms after the signal`);
    assert.doesNotMatch(first.output(), /shutdown did not finish in time|a reindex job was still stopping/);
  } finally {
    first.child.kill("SIGKILL");
    await first.exited;
  }
  const store = new SqliteJobStore(queueDbPathFor(dbPath));
  try {
    const job = store.load().map((entry) => entry.job).find((j) => j.id === id);
    assert.equal(job?.state, "queued", "the scan's job waits for the next start");
  } finally {
    store.close();
  }
  const second = await sidecar(dbPath);
  try {
    let job = await jobState(second.base, id);
    for (let waited = 0; job.state !== "succeeded" && job.state !== "failed" && waited < 300_000; waited += 100) {
      await sleep(100);
      job = await jobState(second.base, id);
    }
    assert.equal(job.state, "succeeded");
    assert.equal(job.result!.update.updated + job.result!.update.unchanged, DOCUMENTS);
    assert.ok(job.result!.update.updated < DOCUMENTS, "the first start's scan kept what it wrote");
  } finally {
    second.child.kill("SIGTERM");
    await second.exited;
  }
});

test("a scan's metadata comes back from its thread, and what it removes is purged there", { skip, timeout: 120_000 }, async () => {
  const { dbPath, corpus } = await index(20);
  const warnings: string[] = [];
  const backend = await openBackend({ dbPath, keepWarm: false, onLlmWarning: (message) => warnings.push(message) });
  try {
    await rm(path.join(corpus, "s-00007.md"));
    const scanned = await backend.update({ headBytes: 8192 });
    assert.equal(scanned.removed, 1);
    assert.equal(scanned.metadata?.documents, 19);
    assert.equal(scanned.metadata?.entries.length, 19);
    assert.equal(scanned.metadata?.entries.find(([key]) => key === "sessions/s-00003.md")?.[1].project, "P3");
    assert.equal(scanned.metadata?.entries.some(([key]) => key === "sessions/s-00007.md"), false);
    const qmd = await import("@tobilu/qmd");
    const reader = await qmd.createStore({ dbPath });
    try {
      assert.equal((reader.internal.db.prepare("SELECT COUNT(*) AS n FROM documents WHERE active = 0").get() as { n: number }).n, 0, "the deleted document's row was purged");
    } finally {
      await reader.close();
    }
    assert.equal((await backend.update({})).metadata, undefined, "none was asked for");
    assert.deepEqual(warnings, []);
  } finally {
    await backend.close();
  }
});
