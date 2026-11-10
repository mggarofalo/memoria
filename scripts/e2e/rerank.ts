/**
 * `node scripts/e2e/rerank.ts --assets <dir> [--no-build]`: reranked and filtered search through REST, MCP
 * and the CLI, against a real API, sidecar and worker, with real models running on the CPU. The rest of the
 * suite has no models, so its searches are degraded BM25; this is the one place a reranker scores them.
 *
 * <dir> holds what scripts/e2e/rerank-assets.ts fetches: llama.cpp's CPU build, QMD's embedding model and
 * its 0.6B reranker. The worker is started here, on loopback, and stopped at the end. See README.md.
 */
import { spawn, spawnSync, type ChildProcess } from "node:child_process";
import { randomBytes } from "node:crypto";
import { readdirSync } from "node:fs";
import path from "node:path";
import { bootstrap, noModels } from "./checks.ts";
import { freePort, Harness, pause, repo, type CliResult } from "./harness.ts";
import { mcpSearch } from "./ingest.ts";
import { extracted, loadAssets } from "./rerank-assets.ts";

const COLLECTION = "kb";
/** Searches that went without nothing a worker serving embed and rerank provides. No generate role runs here. */
const ALLOWED_DEGRADED = new Set(["no_expansion"]);

interface Fixture {
  id: string;
  title: string;
  project: string;
  updated: string;
  body: string;
}

/** Topics far enough apart that the right answer is never in doubt, and queries that share few words with them. */
const FIXTURES: Fixture[] = [
  { id: "bread", title: "Overnight loaf", project: "kitchen", updated: "2026-03-10T00:00:00Z", body: "Knead the dough for ten minutes, let it rise overnight in the fridge, then bake the loaf at 230 degrees until the crust is dark." },
  { id: "pasta", title: "Pasta water", project: "kitchen", updated: "2026-03-15T00:00:00Z", body: "Boil the pasta in salted water and keep a cup of the cooking water to loosen the sauce." },
  { id: "cat", title: "Cat on the oven", project: "kitchen", updated: "2026-01-20T00:00:00Z", body: "The cat sleeps on the warm oven door every evening after dinner." },
  { id: "tomatoes", title: "Planting tomatoes", project: "garden", updated: "2026-03-12T00:00:00Z", body: "Tomato seedlings go out after the last frost. Stake them, and water at the base in the morning." },
  { id: "keys", title: "Credential rotation", project: "billing", updated: "2026-01-05T00:00:00Z", body: "Rotate the payment provider credentials every ninety days, and revoke the old ones once the new ones are live." },
  { id: "pauses", title: "Garbage collector pauses", project: "billing", updated: "2026-03-20T00:00:00Z", body: "The service stalled for two seconds whenever the garbage collector ran, so we raised the heap limit and moved to server mode." },
];

interface Hit {
  file?: string;
  score: number;
  metadata?: { project?: string } | null;
}

interface SearchAnswer {
  results: Hit[];
  degraded?: boolean;
  degradedReasons?: string[];
  query?: { rerank?: boolean };
}

function argument(name: string): string | undefined {
  const at = process.argv.indexOf(name);
  return at >= 0 ? process.argv[at + 1] : undefined;
}

function find(dir: string, name: string): string | undefined {
  for (const entry of readdirSync(dir, { withFileTypes: true, recursive: true })) {
    if (entry.isFile() && entry.name === name) return path.join(entry.parentPath, entry.name);
  }
  return undefined;
}

/** The asset folder's three files, located by name from the manifest. */
function assetPaths(dir: string): { llamaServer: string; embed: string; rerank: string } {
  const assets = loadAssets();
  const tarball = assets.find((a) => a.extract);
  const model = (prefix: string) => path.join(dir, assets.find((a) => a.file.startsWith(prefix))!.file);
  // The manifest's build is Linux x64, for CI. Elsewhere, name a local llama-server.
  const llamaServer = argument("--llama-server") ?? (tarball ? find(extracted(dir, tarball), "llama-server") : undefined);
  if (!llamaServer) throw new Error(`No llama-server under ${dir}: run node scripts/e2e/rerank-assets.ts ${dir} first.`);
  return { llamaServer, embed: model("embeddinggemma"), rerank: model("qwen3-reranker") };
}

interface Worker {
  url: string;
  token: string;
  child: ChildProcess;
  output: () => string;
}

/** A worker serving embed and rerank on the CPU, on loopback, as the sidecar would reach one. */
async function startWorker(dir: string): Promise<Worker> {
  const { llamaServer, embed, rerank } = assetPaths(dir);
  const port = await freePort();
  const token = randomBytes(24).toString("base64url");
  const env: NodeJS.ProcessEnv = {
    ...process.env,
    MEMORIA_WORKER_HOST: "127.0.0.1",
    MEMORIA_WORKER_PORT: String(port),
    MEMORIA_WORKER_LLAMA_BASE_PORT: String(await freePort()),
    MEMORIA_WORKER_TOKEN: token,
    MEMORIA_WORKER_LLAMA_SERVER: llamaServer,
    MEMORIA_WORKER_ROLES: "embed,rerank",
    MEMORIA_WORKER_GPU_LAYERS: "0",
    MEMORIA_WORKER_EMBED_MODEL: embed,
    MEMORIA_WORKER_RERANK_MODEL: rerank,
    // Loading two models can push a small runner past the busy threshold, which only sorts workers.
    MEMORIA_WORKER_BUSY_LOAD_PER_CORE: "0",
    // The libraries ship beside the binary.
    LD_LIBRARY_PATH: [path.dirname(llamaServer), process.env["LD_LIBRARY_PATH"] ?? ""].filter(Boolean).join(":"),
  };
  let log = "";
  const child = spawn(process.execPath, ["src/main.ts"], { cwd: path.join(repo, "worker"), env, stdio: ["ignore", "pipe", "pipe"] });
  child.stdout?.setEncoding("utf8").on("data", (text: string) => (log = (log + text).slice(-20_000)));
  child.stderr?.setEncoding("utf8").on("data", (text: string) => (log = (log + text).slice(-20_000)));
  const url = `http://127.0.0.1:${port}`;
  const deadline = Date.now() + 5 * 60_000;
  while (Date.now() < deadline && !exited(child)) {
    try {
      const health = (await (await fetch(`${url}/health`, { headers: { authorization: `Bearer ${token}` } })).json()) as { status?: string };
      if (health.status === "ok" || health.status === "busy") return { url, token, child, output: () => log };
    } catch {
      // Still starting.
    }
    await pause(1_000);
  }
  await stopWorker({ url, token, child, output: () => log });
  throw new Error(`The worker did not become ready on the CPU within five minutes. Its output:\n${log}`);
}

/** A child killed by a signal has no exit code, only a signal. */
function exited(child: ChildProcess): boolean {
  return child.exitCode !== null || child.signalCode !== null;
}

/** Lets the worker stop its llama-server children itself. Windows has no SIGTERM to give it, so there the whole tree goes. */
async function stopWorker(worker: Worker): Promise<void> {
  const { child } = worker;
  if (exited(child) || child.pid === undefined) return;
  const gone = new Promise((resolve) => child.once("exit", resolve));
  if (process.platform === "win32") {
    spawnSync("taskkill", ["/pid", String(child.pid), "/T", "/F"], { stdio: "ignore" });
  } else {
    child.kill("SIGTERM");
    const timer = setTimeout(() => child.kill("SIGKILL"), 15_000);
    await gone;
    clearTimeout(timer);
    return;
  }
  await gone;
}

function json(key: string): Record<string, string> {
  return { authorization: `Bearer ${key}`, "content-type": "application/json" };
}

/** Each fixture's id to the file the server stored it as, which is how search results name it. */
const FILES = new Map<string, string>();

async function seed(h: Harness, writer: string): Promise<void> {
  for (const f of FIXTURES) {
    const answer = await fetch(`${h.apiUrl}/v1/documents`, { method: "POST", headers: json(writer), body: JSON.stringify({ collection: COLLECTION, source: "rerank", sourceId: f.id, updated: f.updated, title: f.title, project: f.project, body: f.body }) });
    const text = await answer.text();
    h.check(`rerank: ${f.id} goes in through the API`, answer.status === 202, text);
    if (answer.status === 202) FILES.set(f.id, (JSON.parse(text) as { file: string }).file);
  }
}

function idOf(file: string | undefined): string | undefined {
  return [...FILES].find(([, stored]) => stored === file)?.[0];
}

/** Every document indexed and embedded by the worker, not by anything on this host. */
async function embedAll(h: Harness): Promise<void> {
  const started = Date.now();
  const queued = JSON.parse((await h.sidecar("POST", "/reindex", { embed: true })).text) as { job: { id: string } };
  let job: { state: string; result?: { embed?: { chunksEmbedded?: number; errors?: number } } } = { state: "queued" };
  while (!["succeeded", "failed", "cancelled"].includes(job.state) && Date.now() - started < 5 * 60_000) {
    await pause(1_000);
    job = (JSON.parse((await h.sidecar("GET", `/reindex/jobs/${queued.job.id}`)).text) as { job: typeof job }).job;
  }
  const embed = job.result?.embed;
  h.check(`rerank: the worker embedded every document (${((Date.now() - started) / 1000).toFixed(1)} s)`, job.state === "succeeded" && (embed?.chunksEmbedded ?? 0) >= FIXTURES.length && embed?.errors === 0, job);
}

/** What a search has to be to count as reranked: not degraded past expansion, and scored by the reranker. */
function reranked(answer: SearchAnswer): boolean {
  const reasons = answer.degradedReasons ?? [];
  // A reranker's probability can round to 0 for a document far off the question, so 0 is a valid score.
  return answer.query?.rerank !== false && reasons.every((r) => ALLOWED_DEGRADED.has(r)) && answer.results.length > 0 && answer.results.every((hit) => hit.score >= 0 && hit.score <= 1);
}

function brief(answer: unknown): string {
  return JSON.stringify(answer).slice(0, 600);
}

const NO_ANSWER: SearchAnswer = { results: [] };

function checkSearch(h: Harness, label: string, given: SearchAnswer | undefined, top: string, project?: string): void {
  const answer = given ?? NO_ANSWER;
  const hits = answer.results;
  h.check(`${label}: reranked, not degraded beyond expansion`, given !== undefined && reranked(answer), brief({ degradedReasons: answer.degradedReasons, rerank: answer.query?.rerank, scores: hits.map((r) => r.score) }));
  h.check(`${label}: the right document is first`, idOf(hits[0]?.file) === top, brief(hits.map((r) => [idOf(r.file), r.score])));
  if (project !== undefined) checkProject(h, label, hits, project);
}

function checkProject(h: Harness, label: string, hits: Hit[], project: string): void {
  h.check(`${label}: only the project asked for`, hits.length > 0 && hits.every((r) => r.metadata?.project === project), brief(hits.map((r) => [idOf(r.file), r.metadata?.project])));
}

async function rest(h: Harness, reader: string, query: string, extra = ""): Promise<SearchAnswer | undefined> {
  const answer = await fetch(`${h.apiUrl}/v1/search?query=${encodeURIComponent(query)}&collection=${COLLECTION}&limit=5${extra}`, { headers: json(reader) });
  return answer.status === 200 ? ((await answer.json()) as SearchAnswer) : undefined;
}

function structured(mcpText: string): SearchAnswer | undefined {
  const line = mcpText.split("\n").find((l) => l.startsWith("data: ")) ?? mcpText;
  try {
    return (JSON.parse(line.replace(/^data: /, "")) as { result?: { structuredContent?: SearchAnswer } }).result?.structuredContent;
  } catch {
    return undefined;
  }
}

function cliJson(result: CliResult): SearchAnswer | undefined {
  try {
    return result.code === 0 ? (JSON.parse(result.out) as SearchAnswer) : undefined;
  } catch {
    return undefined;
  }
}

async function searches(h: Harness, reader: string, worker: Worker): Promise<void> {
  const before = await rerankCalls(h);
  const times: number[] = [];
  const timed = async <T>(run: () => Promise<T> | T): Promise<T> => {
    const started = Date.now();
    try {
      return await run();
    } finally {
      times.push(Date.now() - started);
    }
  };

  checkSearch(h, "rerank REST", await timed(() => rest(h, reader, "how do I make sourdough at home")), "bread");
  checkSearch(h, "rerank REST, filtered by project", await timed(() => rest(h, reader, "why did the app freeze for a moment", "&project=billing")), "pauses", "billing");
  const since = await timed(() => rest(h, reader, "replacing old passwords", "&project=billing&since=2026-02-01"));
  h.check("rerank REST, filtered by date: the older document is left out", since !== undefined && reranked(since) && since.results.every((r) => idOf(r.file) !== "keys"), brief(since));

  const mcp = structured(await timed(() => mcpSearch(h, reader, { query: "when can vegetables go outside in spring", collections: [COLLECTION], rerank: true })));
  checkSearch(h, "rerank MCP", mcp, "tomatoes");
  const mcpFiltered = structured(await timed(() => mcpSearch(h, reader, { query: "cooking noodles", collections: [COLLECTION], rerank: true, project: ["kitchen"] })));
  checkSearch(h, "rerank MCP, filtered by project", mcpFiltered, "pasta", "kitchen");

  const cli = await timed(() => h.memoria(["query", "replacing secrets on a schedule", "--collection", COLLECTION, "--project", "billing", "--format", "json"], { key: reader }));
  checkSearch(h, "rerank CLI, filtered by project", cliJson(cli), "keys", "billing");

  const after = await rerankCalls(h);
  h.check("rerank: the worker was really asked to rerank", after > before, { before, after });
  console.log(`     search times on the CPU, ms: ${times.join(", ")}`);
  h.check("rerank: the worker is still healthy", (await fetch(`${worker.url}/health`, { headers: { authorization: `Bearer ${worker.token}` } })).status === 200);
}

/** Calls the sidecar made to its workers, which only rises when one answers. */
async function rerankCalls(h: Harness): Promise<number> {
  const workers = JSON.parse((await h.sidecar("GET", "/workers")).text) as { workers: Array<{ calls: number }> };
  return workers.workers.reduce((sum, w) => sum + (w.calls ?? 0), 0);
}

async function keyFor(h: Harness, admin: string, label: string, scopes: string[]): Promise<string> {
  const answer = await fetch(`${h.apiUrl}/v1/admin/keys`, { method: "POST", headers: json(admin), body: JSON.stringify({ label, scopes, collections: [COLLECTION] }) });
  return h.remember((JSON.parse(await answer.text()) as { key: string }).key);
}

async function main(): Promise<number> {
  const dir = argument("--assets");
  if (!dir) {
    console.error("usage: node scripts/e2e/rerank.ts --assets <dir> [--no-build]   (fetch them with node scripts/e2e/rerank-assets.ts <dir>)");
    return 2;
  }
  const started = Date.now();
  const worker = await startWorker(path.resolve(dir));
  let h: Harness | undefined;
  let crashed = false;
  try {
    h = new Harness({
      bareIndex: true,
      extraCollections: [COLLECTION],
      apiEnv: { [`Memoria__Ingest__Collections__${COLLECTION}__Enabled`]: "true", Memoria__Ingest__MinReindexInterval: "00:00:00" },
      sidecarEnv: { MEMORIA_QMD_WORKER_URL: worker.url, MEMORIA_QMD_WORKER_TOKEN: worker.token, MEMORIA_QMD_WORKER_POLL_MS: "1000" },
    });
    await runChecks(h, worker);
  } catch (error) {
    crashed = true;
    const text = String(error instanceof Error ? error.stack : error);
    console.error(h ? h.scrub(text) : text);
    console.error(`worker output, last part:\n${worker.output().slice(-4_000)}`);
  } finally {
    await h?.cleanUp();
    await stopWorker(worker);
  }
  return report(h?.results ?? [], started, crashed);
}

async function runChecks(h: Harness, worker: Worker): Promise<void> {
  if (!process.argv.includes("--no-build")) h.buildServers();
  h.buildCli();
  h.seedIndex();
  await h.choosePorts();
  await h.startSidecar();
  await h.startProxy();
  const admin = bootstrap(h);
  await h.startApi();
  await seed(h, await keyFor(h, admin, "rerank-writer", ["ingest"]));
  await embedAll(h);
  await searches(h, await keyFor(h, admin, "rerank-reader", ["read"]), worker);
  noModels(h);
}

function report(results: ReadonlyArray<{ name: string; ok: boolean }>, started: number, crashed: boolean): number {
  const failed = results.filter((r) => !r.ok);
  console.log(`\n${results.length - failed.length} passed, ${failed.length} failed, in ${Math.round((Date.now() - started) / 1000)}s`);
  for (const result of failed) console.log(`  FAILED: ${result.name}`);
  return failed.length > 0 || crashed ? 1 : 0;
}

process.exitCode = await main();