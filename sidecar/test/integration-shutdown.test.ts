/**
 * Shutdown of the real process, on the real SDK, with a slow mock worker: what
 * `docker stop` does to it while an embedding job and a reranked search are in
 * flight. Compose kills the container 15 s after asking, so it must be done, and
 * exit 0, well before that.
 *
 * Opt in with MEMORIA_QMD_INTEGRATION=1 (CI sets it; `npm test` does not).
 */
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { createInterface } from "node:readline";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { queueDbPathFor, SqliteJobStore } from "../src/queue-store.ts";
import { tempDir } from "./helpers.ts";
import { MockWorker } from "./mock-worker.ts";
import { freePort } from "../../worker/test/ports.ts";

// Windows has no SIGTERM to send: killing a process there ends it with no handler run.
const skip = process.env.MEMORIA_QMD_INTEGRATION !== "1" ? "set MEMORIA_QMD_INTEGRATION=1" : process.platform === "win32" ? "needs POSIX signals" : false;
const mainEntry = fileURLToPath(new URL("../src/main.ts", import.meta.url));
const SECRET = "Xk29vQmB7fLp3ZtRw";
const DRAIN_MS = 3_000;
/** Past the drain, what the rest of shutdown takes at most: two final waits and closing up. */
const AFTER_DRAIN_MS = 3_000;
const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

async function index(): Promise<string> {
  const root = await tempDir("memoria-shutdown-it-");
  const corpus = path.join(root, "sessions");
  await mkdir(corpus, { recursive: true });
  // Enough text for many batches at a small batch size, so the job is still embedding when the signal comes.
  for (let i = 0; i < 12; i++) await writeFile(path.join(corpus, `doc-${i}.md`), `# Doc ${i}\n\n${"The AuthenticationHandler validates keys. ".repeat(80)}\n`, "utf8");
  const dbPath = path.join(root, "index.sqlite");
  const qmd = await import("@tobilu/qmd");
  const builder = await qmd.createStore({ dbPath, config: { collections: { sessions: { path: corpus, pattern: "**/*.md" } } } });
  await builder.update();
  await builder.close();
  return dbPath;
}

/** Every model call takes `ms`, and each is stamped with when it arrived. */
async function slowWorker(ms: number): Promise<{ mock: MockWorker; arrivals: number[] }> {
  const mock = await new MockWorker(8).start();
  const arrivals: number[] = [];
  for (const route of ["POST /v1/embed", "POST /v1/rerank", "POST /v1/complete", "POST /v1/tokenize", "POST /v1/tokenize-batch"]) {
    const fast = mock.handlers.get(route)!;
    mock.on(route, async (body, request) => {
      arrivals.push(Date.now());
      await sleep(ms);
      return fast(body, request);
    });
  }
  return { mock, arrivals };
}

test("stopped mid-embed with a reranked search in flight, it calls no worker after, keeps the job, and exits 0 in time", { skip }, async () => {
  const dbPath = await index();
  const { mock, arrivals } = await slowWorker(1_500);
  const port = await freePort();
  const child = spawn(process.execPath, [mainEntry], {
    env: {
      ...process.env,
      MEMORIA_QMD_DB_PATH: dbPath,
      MEMORIA_QMD_PORT: String(port),
      MEMORIA_QMD_WARMUP: "0",
      MEMORIA_QMD_SECRET: SECRET,
      MEMORIA_QMD_LLM: "remote",
      MEMORIA_QMD_WORKER_URL: mock.url,
      MEMORIA_QMD_EMBED_DIMENSIONS: "8",
      MEMORIA_QMD_LLM_FALLBACK: "0",
      MEMORIA_QMD_EMBED_BATCH_BYTES: "4096",
      MEMORIA_QMD_SHUTDOWN_DRAIN_MS: String(DRAIN_MS),
    },
    stdio: ["ignore", "pipe", "inherit"],
  });
  const exited = new Promise<number | null>((resolve) => child.once("exit", (code) => resolve(code)));
  let output = "";
  const listening = new Promise<void>((resolve) => {
    createInterface({ input: child.stdout }).on("line", (line) => {
      output += `${line}\n`;
      if (line.includes("memoria-qmd listening")) resolve();
    });
  });
  const base = `http://127.0.0.1:${port}`;
  const headers = { authorization: `Bearer ${SECRET}`, "content-type": "application/json" };
  try {
    await Promise.race([listening, sleep(30_000)]);
    assert.match(output, /memoria-qmd listening/, output);
    const queued = await fetch(`${base}/reindex`, { method: "POST", headers, body: JSON.stringify({ embed: true }) });
    assert.equal(queued.status, 202);
    for (let waited = 0; arrivals.length === 0 && waited < 20_000; waited += 100) await sleep(100);
    assert.ok(arrivals.length > 0, "the job reached the worker");
    const search = fetch(`${base}/search`, { method: "POST", headers, body: JSON.stringify({ query: "AuthenticationHandler", rerank: true, limit: 3 }) }).then((r) => r.status, () => 0);
    await sleep(300);

    const signalled = Date.now();
    child.kill("SIGTERM");
    const code = await Promise.race([exited, sleep(DRAIN_MS + AFTER_DRAIN_MS + 5_000).then(() => "still running" as const)]);
    const took = Date.now() - signalled;
    assert.equal(code, 0, output);
    assert.ok(took < DRAIN_MS + AFTER_DRAIN_MS, `exited after ${took} ms`);
    // A call can start in the moment between the signal and its handler, but none after.
    const late = arrivals.filter((at) => at > signalled + 250);
    assert.deepEqual(late, [], "no worker call started once shutdown had begun");
    assert.match(output, /shutting down/);
    await search;

    const store = new SqliteJobStore(queueDbPathFor(dbPath));
    try {
      const jobs = store.load().map((entry) => entry.job);
      assert.ok(jobs.some((job) => job.state === "queued"), `the embedding job waits for the next start: ${JSON.stringify(jobs.map((j) => j.state))}`);
      assert.ok(!jobs.some((job) => job.state === "failed"), "shutdown failed no job");
    } finally {
      store.close();
    }
  } finally {
    child.kill("SIGKILL");
    await exited;
    await mock.stop();
  }
});
