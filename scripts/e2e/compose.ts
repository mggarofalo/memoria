/**
 * `node scripts/e2e/compose.ts`: deploy/compose.yaml as it ships, on images built from this checkout, with a scripted worker in a
 * container standing in for a remote host. The main suite runs the services as processes, with a recording proxy between them for its
 * checks; this runs the layout itself, where nothing sits between them. Needs Docker. Leaves nothing behind.
 */
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { randomBytes } from "node:crypto";
import { fileURLToPath } from "node:url";

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const VERSION = "0.0.0-compose-check";
const PROJECT = "memoria-e2e-compose";
const PORT = 18_093;
const API = `http://127.0.0.1:${PORT}`;
const SIDECAR = "memoria-e2e-qmd";
const COLLECTION = "claude-sessions";
const WORKER_TOKEN = "mock-worker-token-0123456789";

const dir = mkdtempSync(path.join(os.tmpdir(), "memoria-compose-"));
const results: { name: string; ok: boolean }[] = [];

function check(name: string, ok: unknown, detail?: unknown): void {
  results.push({ name, ok: Boolean(ok) });
  console.log(`${ok ? "PASS" : "FAIL"} ${name}`);
  if (!ok && detail !== undefined) console.log(`     ${String(typeof detail === "string" ? detail : JSON.stringify(detail)).slice(0, 600)}`);
}

function docker(args: string[], allowFailure = false): string {
  const result = spawnSync("docker", args, { cwd: repo, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  if (result.status !== 0 && !allowFailure) throw new Error(`docker ${args.slice(0, 6).join(" ")} failed: ${result.stderr}`);
  return `${result.stdout}${allowFailure ? result.stderr : ""}`;
}

function compose(...args: string[]): string {
  return docker(["compose", "-p", PROJECT, "--env-file", path.join(dir, "check.env"), "-f", "deploy/compose.yaml", "-f", path.join(dir, "check.yaml"), ...args]);
}

/** A call on the sidecar from inside its own container, as nothing outside the bridge can reach it. */
function sidecar(route: string, body?: unknown): unknown {
  const init = body === undefined ? "{headers:h}" : `{method:'POST',headers:{...h,'content-type':'application/json'},body:${JSON.stringify(JSON.stringify(body))}}`;
  const script = `const h={authorization:'Bearer '+process.env.MEMORIA_QMD_SECRET};fetch('http://127.0.0.1:8282${route}',${init}).then(r=>r.text()).then(t=>process.stdout.write(t))`;
  return JSON.parse(docker(["exec", SIDECAR, "node", "-e", script]));
}

async function api(route: string, init: RequestInit & { key?: string } = {}): Promise<{ status: number; body: any }> {
  const headers = new Headers(init.headers);
  if (init.key) headers.set("Authorization", `Bearer ${init.key}`);
  const response = await fetch(`${API}${route}`, { ...init, headers });
  const text = await response.text();
  return { status: response.status, body: text.startsWith("{") ? JSON.parse(text) : text };
}

async function until<T>(what: () => Promise<T> | T, done: (value: T) => boolean, seconds: number): Promise<T> {
  let value = await what();
  for (let waited = 0; !done(value) && waited < seconds; waited++) {
    await new Promise((resolve) => setTimeout(resolve, 1000));
    value = await what();
  }
  return value;
}

function prepare(): void {
  docker(["build", "--quiet", "-t", `ghcr.io/mggarofalo/memoria:${VERSION}`, "--build-arg", `MEMORIA_VERSION=${VERSION}`, "."]);
  docker(["build", "--quiet", "-t", `ghcr.io/mggarofalo/memoria-qmd:${VERSION}`, "--build-arg", `MEMORIA_VERSION=${VERSION}`, "sidecar"]);
  const workers = [{ name: "gpu", url: "http://mock-worker:8383", tokenEnv: "MEMORIA_WORKER_TOKEN_GPU", priority: 1 }];
  writeFileSync(path.join(dir, "check.env"), [
    `MEMORIA_VERSION=${VERSION}`,
    `MEMORIA_SIDECAR_SECRET=${randomBytes(32).toString("base64url")}`,
    `MEMORIA_INGEST_DIGEST_KEY=${randomBytes(32).toString("base64url")}`,
    `MEMORIA_QMD_WORKERS=${JSON.stringify(workers)}`,
    `MEMORIA_WORKER_TOKEN_GPU=${WORKER_TOKEN}`,
    "",
  ].join("\n"));
  // Only what a check needs: its own names and port, a fast heartbeat, and the worker host, which starts stopped.
  writeFileSync(path.join(dir, "check.yaml"), `services:
  memoria-qmd:
    container_name: ${SIDECAR}
    environment:
      MEMORIA_QMD_WORKER_POLL_MS: "1000"
  memoria-api:
    container_name: memoria-e2e-api
    ports: !override
      - "127.0.0.1:${PORT}:8080"
  mock-worker:
    image: node:22-bookworm-slim
    container_name: memoria-e2e-worker
    command: ["node", "/check/worker-host.mjs"]
    volumes:
      - { type: bind, source: ${JSON.stringify(path.join(repo, "sidecar", "test"))}, target: /mock, read_only: true }
      # mock-worker.ts imports ../../worker/test/ports.ts, which from /mock is this.
      - { type: bind, source: ${JSON.stringify(path.join(repo, "worker", "test"))}, target: /worker/test, read_only: true }
      - { type: bind, source: ${JSON.stringify(dir)}, target: /check, read_only: true }
    networks: [memoria]
    profiles: [worker]
`);
  writeFileSync(path.join(dir, "worker-host.mjs"), `import { createServer, connect } from "node:net";
import { MockWorker } from "/mock/mock-worker.ts";
const worker = await new MockWorker(768).start();
const port = Number(new URL(worker.url).port);
createServer((socket) => { const up = connect(port, "127.0.0.1"); socket.pipe(up).pipe(socket); socket.on("error", () => up.destroy()); up.on("error", () => socket.destroy()); }).listen(8383, "0.0.0.0");
`);
}

async function firstRun(): Promise<string> {
  const made = compose("run", "--rm", "--no-deps", "memoria-qmd", "scripts/init-index.mjs", COLLECTION, "codex-sessions");
  const again = compose("run", "--rm", "--no-deps", "memoria-qmd", "scripts/init-index.mjs", COLLECTION, "codex-sessions");
  check("compose: the first-run command makes the index, and running it again changes nothing", made.includes("holds: claude-sessions, codex-sessions") && again.includes("holds: claude-sessions, codex-sessions"), made + again);
  const admin = /mem_[0-9a-f]+_[0-9a-f]+/.exec(compose("run", "--rm", "--no-deps", "memoria-api", "--bootstrap-admin-key", "compose-admin"))?.[0] ?? "";
  check("compose: the first admin key is made", admin !== "");
  compose("up", "-d");
  const health = await until(() => api("/health").catch(() => ({ status: 0, body: {} })), (r) => r.status === 200, 90);
  check("compose: the API answers, at the version it was built with", health.status === 200 && health.body.version === VERSION, health.body);
  check("compose: so does the sidecar, at the same version", (sidecar("/health") as { version?: string }).version === VERSION);
  return admin;
}

function boundary(): void {
  check("compose: the sidecar publishes no port", docker(["port", SIDECAR], true).trim() === "");
  const networks = docker(["network", "inspect", `${PROJECT}_memoria`, "--format", "{{range .Containers}}{{.Name}} {{end}}"]).trim().split(" ").sort();
  check("compose: the API and the sidecar are the only containers on their network", JSON.stringify(networks) === JSON.stringify(["memoria-e2e-api", SIDECAR]), networks);
  check("compose: the sidecar runs as a user, not root", docker(["exec", SIDECAR, "id", "-u"]).trim() === "1000");
  const llama = docker(["exec", SIDECAR, "sh", "-c", "ls node_modules/@node-llama-cpp 2>/dev/null | wc -l; grep -c -i -E 'llama|cuda|vulkan' /proc/1/maps; find / -xdev -name '*.gguf' 2>/dev/null | wc -l"]).trim().split(/\s+/);
  check("compose: the sidecar holds no llama.cpp binaries, has none loaded, and has no model file", JSON.stringify(llama) === JSON.stringify(["0", "0", "0"]), llama);
}

async function workersDown(key: string): Promise<void> {
  const write = await api(`/v1/ingest/rendered?collection=${COLLECTION}&path=2026-01-15_compose1.md&updated=2026-01-15T00:00:00Z`, { method: "POST", key, headers: { "Content-Type": "text/markdown" }, body: "---\nsession_id: \"compose1\"\nmessage_end: 0\n---\n\n## User\n\nthe zebrafish protocol\n" });
  check("compose: a document goes in through the API with no worker up", write.status === 202, write.body);
  sidecar("/reindex", { embed: false });
  const search = await until(() => api(`/v1/search?query=zebrafish&collection=${COLLECTION}&limit=5`, { key }), (r) => r.status === 200 && r.body.results?.length === 1, 60);
  check("compose: with every worker down, search still finds it, and says it is degraded", search.body.results?.length === 1 && search.body.degraded === true, search.body);
  check("compose: it says why: no reranking, no expansion, no vectors", ["no_expansion", "no_rerank", "no_vectors"].every((reason) => search.body.degradedReasons?.includes(reason)), search.body.degradedReasons);
  const ready = await api("/ready");
  const strict = await api("/ready?strict=true");
  check("compose: the API stays ready, reading degraded, and strict readiness refuses", ready.status === 200 && ready.body.status === "degraded" && strict.status === 503, [ready.body, strict.status]);
  const vectors = docker(["exec", SIDECAR, "sh", "-c", "grep -c -i -E 'llama|cuda|vulkan' /proc/1/maps || true"]).trim();
  check("compose: and searching loaded nothing that could run a model", vectors === "0", vectors);
}

async function workerArrives(key: string): Promise<void> {
  const job = sidecar("/reindex", { embed: true }) as { job: { id: string } };
  const waiting = await until(() => sidecar(`/reindex/jobs/${job.job.id}`) as { job: { state: string; error?: string } }, (r) => r.job.error !== undefined && r.job.error !== null, 60);
  check("compose: with no worker, embedding waits and says why", waiting.job.state === "queued" && String(waiting.job.error).includes("No worker"), waiting.job);
  compose("--profile", "worker", "up", "-d", "mock-worker");
  // The waiting job retries on a backoff; asking again releases it at once.
  await until(() => (sidecar("/workers") as { workers: { state: string }[] }).workers[0]?.state, (state) => state === "up", 60);
  sidecar("/reindex", { embed: true });
  const done = await until(() => sidecar(`/reindex/jobs/${job.job.id}`) as { job: { state: string } }, (r) => r.job.state === "succeeded" || r.job.state === "failed", 180);
  check("compose: once a worker on another host answers, the job finishes", done.job.state === "succeeded", done.job);
  const search = await api(`/v1/search?query=zebrafish&collection=${COLLECTION}&limit=5`, { key });
  check("compose: and search uses it: reranked, with vectors", search.body.results?.length === 1 && !search.body.degradedReasons?.includes("no_vectors") && !search.body.degradedReasons?.includes("no_rerank"), search.body);
}

async function survivesRestart(key: string): Promise<void> {
  const session = `{"type":"user","timestamp":"2026-01-16T10:00:00.000Z","cwd":"/srv/demo","message":{"role":"user","content":"the axolotl ledger"}}\n`;
  const upload = await api(`/v1/ingest/session?collection=${COLLECTION}&format=claude&sessionId=11111111-2222-3333-4444-555555555555&updated=2026-01-16T11:00:00Z&projectDirectory=-srv-demo`, { method: "POST", key, headers: { "Content-Type": "application/x-ndjson" }, body: session });
  check("compose: a session goes in", upload.status === 202, upload.body);
  const gone = await api(`/v1/documents?collection=${COLLECTION}&path=2026-01-15_compose1.md`, { method: "DELETE", key });
  check("compose: a delete is confirmed", gone.status === 202 && gone.body.deleted === true, gone.body);
  const search = (word: string) => () => api(`/v1/search?query=${word}&collection=${COLLECTION}&limit=5&rerank=false`, { key });
  const paths = (r: { body: any }): string[] => (r.body.results ?? []).map((hit: { path: string }) => hit.path);
  // Vector search returns the nearest documents however far, so once the session is embedded
  // it can answer "zebrafish" too. What must not come back is the deleted document.
  const deletedPath = "2026-01-15_compose1.md";
  const left = await until(search("zebrafish"), (r) => r.status === 200 && !paths(r).includes(deletedPath), 90);
  check("compose: the deleted document leaves search", left.status === 200 && !paths(left).includes(deletedPath), { paths: paths(left), degraded: left.body.degradedReasons });
  compose("restart");
  await until(() => api("/health").catch(() => ({ status: 0, body: {} })), (r) => r.status === 200, 90);
  const kept = await until(search("axolotl"), (r) => paths(r).length === 1, 60);
  check("compose: after a restart the session is still there", paths(kept).length === 1, paths(kept));
  const deleted = await search("zebrafish")();
  check("compose: and the deleted document is still gone", deleted.status === 200 && !paths(deleted).includes(deletedPath), { paths: paths(deleted), degraded: deleted.body.degradedReasons });
}

const started = Date.now();
let crashed = false;
try {
  prepare();
  const admin = await firstRun();
  const made = await api("/v1/admin/keys", { method: "POST", key: admin, headers: { "Content-Type": "application/json" }, body: JSON.stringify({ label: "compose", scopes: ["ingest", "read"], allCollections: true }) });
  const key = String(made.body.key ?? "");
  check("compose: a writing key is made", key !== "", made.status);
  boundary();
  await workersDown(key);
  await workerArrives(key);
  await survivesRestart(key);
} catch (error) {
  crashed = true;
  console.error(error);
} finally {
  docker(["compose", "-p", PROJECT, "--env-file", path.join(dir, "check.env"), "-f", "deploy/compose.yaml", "-f", path.join(dir, "check.yaml"), "--profile", "worker", "down", "-v"], true);
  docker(["image", "rm", `ghcr.io/mggarofalo/memoria:${VERSION}`, `ghcr.io/mggarofalo/memoria-qmd:${VERSION}`], true);
  rmSync(dir, { recursive: true, force: true });
}

const failed = results.filter((result) => !result.ok);
console.log(`\n${results.length - failed.length} passed, ${failed.length} failed, in ${Math.round((Date.now() - started) / 1000)}s`);
process.exitCode = failed.length > 0 || crashed ? 1 : 0;
