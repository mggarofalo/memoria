/**
 * `node scripts/e2e/backup-restore.ts`: deploy/backup.yaml as it ships, on a throwaway compose project. It fills the project, embeds it with the
 * sidecar tests' scripted worker, backs it up, adds a day's worth more and measures that delta, then destroys every volume by name, restores, and
 * proves keys, documents, deletes and search came back. Needs Docker. Everything is named `sc-backup`, and the run removes all of it.
 */
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { randomBytes } from "node:crypto";
import { fileURLToPath } from "node:url";

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const VERSION = "0.0.0-backup-check";
const PROJECT = "sc-backup";
const PORT = 18_094;
const API = `http://127.0.0.1:${PORT}`;
const SIDECAR = "sc-backup-qmd";
const COLLECTION = "claude-sessions";
const VOLUMES = ["memoria-data", "memoria-corpus", "memoria-qmd-index", "memoria-qmd-config"].map((name) => `${PROJECT}_${name}`);
const FIRST_DOCUMENTS = 300;
const DAILY_DOCUMENTS = 40;
const DOCUMENT_BYTES = 20_000;

const dir = mkdtempSync(path.join(os.tmpdir(), "memoria-backup-"));
const results: { name: string; ok: boolean }[] = [];
const vocabulary = Array.from({ length: 3000 }, () => randomBytes(5).toString("hex"));

function check(name: string, ok: unknown, detail?: unknown): void {
  results.push({ name, ok: Boolean(ok) });
  console.log(`${ok ? "PASS" : "FAIL"} ${name}`);
  if (!ok && detail !== undefined) console.log(`     ${String(typeof detail === "string" ? detail : JSON.stringify(detail)).slice(0, 600)}`);
}

function run(command: string, args: string[], allowFailure = false): { status: number | null; out: string } {
  const result = spawnSync(command, args, { cwd: repo, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  if (result.status !== 0 && !allowFailure) throw new Error(`${command} ${args.slice(0, 8).join(" ")} failed: ${result.stderr}`);
  return { status: result.status, out: `${result.stdout}${result.stderr}` };
}

function compose(args: string[], allowFailure = false): { status: number | null; out: string } {
  const files = ["-f", "deploy/compose.yaml", "-f", "deploy/backup.yaml", "-f", path.join(dir, "check.yaml")];
  return run("docker", ["compose", "-p", PROJECT, "--env-file", path.join(dir, "check.env"), ...files, ...args], allowFailure);
}

function sidecar(route: string, body?: unknown): any {
  const init = body === undefined ? "{headers:h}" : `{method:'POST',headers:{...h,'content-type':'application/json'},body:${JSON.stringify(JSON.stringify(body))}}`;
  const script = `const h={authorization:'Bearer '+process.env.MEMORIA_QMD_SECRET};fetch('http://127.0.0.1:8282${route}',${init}).then(r=>r.text()).then(t=>process.stdout.write(t))`;
  return JSON.parse(run("docker", ["exec", SIDECAR, "node", "-e", script]).out);
}

async function api(route: string, init: RequestInit & { key?: string } = {}): Promise<{ status: number; body: any; text: string }> {
  const headers = new Headers(init.headers);
  if (init.key) headers.set("Authorization", `Bearer ${init.key}`);
  const response = await fetch(`${API}${route}`, { ...init, headers });
  const text = await response.text();
  return { status: response.status, body: text.startsWith("{") || text.startsWith("[") ? JSON.parse(text) : text, text };
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
  run("docker", ["build", "--quiet", "-t", `ghcr.io/mggarofalo/memoria:${VERSION}`, "--build-arg", `MEMORIA_VERSION=${VERSION}`, "."]);
  run("docker", ["build", "--quiet", "-t", `ghcr.io/mggarofalo/memoria-qmd:${VERSION}`, "--build-arg", `MEMORIA_VERSION=${VERSION}`, "sidecar"]);
  writeFileSync(path.join(dir, "restic-password"), `${randomBytes(32).toString("base64url")}\n`, { mode: 0o600 });
  const workers = [{ name: "gpu", url: "http://mock-worker:8383", tokenEnv: "MEMORIA_WORKER_TOKEN_GPU", priority: 1 }];
  writeFileSync(path.join(dir, "check.env"), [
    `MEMORIA_VERSION=${VERSION}`,
    `MEMORIA_SIDECAR_SECRET=${randomBytes(32).toString("base64url")}`,
    // Its own, so a deployment on this host keeps its proxy network.
    `MEMORIA_PROXY_NETWORK=${PROJECT}-proxy`,
    `MEMORIA_INGEST_DIGEST_KEY=${randomBytes(32).toString("base64url")}`,
    `MEMORIA_QMD_WORKERS=${JSON.stringify(workers)}`,
    "MEMORIA_WORKER_TOKEN_GPU=mock-worker-token-0123456789",
    `MEMORIA_BACKUP_PASSWORD_FILE=${path.join(dir, "restic-password")}`,
    `MEMORIA_BACKUP_REPOSITORY=${PROJECT}-repository`,
    "",
  ].join("\n"));
  writeFileSync(path.join(dir, "check.yaml"), `services:
  memoria-qmd:
    container_name: ${SIDECAR}
    environment:
      MEMORIA_QMD_WORKER_POLL_MS: "1000"
  memoria-api:
    container_name: sc-backup-api
    environment:
      Memoria__Auth__RateLimit__PermitLimit: "100000"
      Memoria__Ingest__MinReindexInterval: "00:00:00"
    ports: !override
      - "127.0.0.1:${PORT}:8080"
  mock-worker:
    image: node:26-bookworm-slim
    container_name: sc-backup-worker
    command: ["node", "/check/worker-host.mjs"]
    volumes:
      - { type: bind, source: ${JSON.stringify(path.join(repo, "sidecar", "test"))}, target: /mock, read_only: true }
      # mock-worker.ts imports ../../worker/test/ports.ts, which from /mock is this.
      - { type: bind, source: ${JSON.stringify(path.join(repo, "worker", "test"))}, target: /worker/test, read_only: true }
      - { type: bind, source: ${JSON.stringify(dir)}, target: /check, read_only: true }
    networks: [memoria]
volumes:
  ${PROJECT}-repository:
    name: ${PROJECT}-repository
`);
  writeFileSync(path.join(dir, "worker-host.mjs"), `import { createServer, connect } from "node:net";
import { MockWorker } from "/mock/mock-worker.ts";
const worker = await new MockWorker(1024, "Qwen3-Embedding-0.6B-Q8_0.gguf", "Qwen3-Reranker-4B.Q8_0.gguf").start();
const port = Number(new URL(worker.url).port);
createServer((socket) => { const up = connect(port, "127.0.0.1"); socket.pipe(up).pipe(socket); socket.on("error", () => up.destroy()); up.on("error", () => socket.destroy()); }).listen(8383, "0.0.0.0");
`);
}

function body(word: string): string {
  let text = `---\ntitle: "${word}"\n---\n\n## User\n\n${word}\n`;
  while (text.length < DOCUMENT_BYTES) text += Array.from({ length: 40 }, () => vocabulary[Math.floor(Math.random() * vocabulary.length)]).join(" ") + "\n";
  return text;
}

async function write(key: string, from: number, count: number): Promise<number> {
  let failed = 0;
  for (let i = from; i < from + count; i++) {
    const answer = await api(`/v1/ingest/rendered?collection=${COLLECTION}&path=day/d${i}.md&updated=2026-01-15T00:00:00Z`, { method: "POST", key, headers: { "Content-Type": "text/markdown" }, body: body(`marker${i}`) });
    if (answer.status !== 202) failed++;
  }
  return failed;
}

/** Runs an embedding update, as the debounced one would, and says how long the worker took for it. */
async function embed(): Promise<{ state: string; seconds: number; result: unknown }> {
  const started = Date.now();
  const job = sidecar("/reindex", { embed: true }).job as { id: string };
  const done = await until(() => sidecar(`/reindex/jobs/${job.id}`).job as { state: string; result?: { embed?: unknown } }, (j) => j.state === "succeeded" || j.state === "failed", 900);
  return { state: done.state, seconds: Math.round((Date.now() - started) / 100) / 10, result: done.result?.embed };
}

function backup(): { seconds: number; added: string } {
  const started = Date.now();
  compose(["run", "--rm", "snapshot"]);
  const out = compose(["run", "--rm", "backup"]).out;
  return { seconds: Math.round((Date.now() - started) / 100) / 10, added: /Added to the repository: ([^\n]+)/.exec(out)?.[1] ?? "?" };
}

function auditRows(): number {
  const script = "const D=require('better-sqlite3');const d=new D('/data/control.db',{readonly:true});console.log(d.prepare('select count(*) n from audit_log').get().n)";
  // As the API's own user, as a reader writes the -shm index, which must stay the API's.
  return Number(run("docker", ["run", "--rm", "--user", "1654:1654", "-v", `${PROJECT}_memoria-data:/data`, "--entrypoint", "node", `ghcr.io/mggarofalo/memoria-qmd:${VERSION}`, "-e", script]).out.trim());
}

interface Before { admin: string; key: string; doc: string; hits: number; audit: number; labels: string[] }

async function fill(): Promise<Before> {
  compose(["run", "--rm", "--no-deps", "memoria-qmd", "scripts/init-index.mjs", COLLECTION]);
  const admin = /mem_[0-9a-f]+_[0-9a-f]+/.exec(compose(["run", "--rm", "--no-deps", "memoria-api", "--bootstrap-admin-key", "backup-admin"]).out)?.[0] ?? "";
  compose(["up", "-d"]);
  await until(() => api("/health").catch(() => ({ status: 0, body: {}, text: "" })), (r) => r.status === 200, 90);
  await until(() => (sidecar("/workers").workers as { state: string }[])[0]?.state, (state) => state === "up", 60);
  const made = await api("/v1/admin/keys", { method: "POST", key: admin, headers: { "Content-Type": "application/json" }, body: JSON.stringify({ label: "backup-writer", scopes: ["ingest", "read"], allCollections: true }) });
  const key = String(made.body.key ?? "");
  check("backup: the project is up with a writing key", admin !== "" && key !== "", made.status);

  check(`backup: ${FIRST_DOCUMENTS} documents go in`, (await write(key, 0, FIRST_DOCUMENTS)) === 0);
  const gone = await api(`/v1/documents?collection=${COLLECTION}&path=day/d0.md`, { method: "DELETE", key });
  check("backup: and one is deleted", gone.status === 202 && gone.body.deleted === true, gone.body);
  const first = await embed();
  console.log(`     first embed: ${FIRST_DOCUMENTS} documents in ${first.seconds} s, ${JSON.stringify(first.result)}`);
  check("backup: the first embed finishes", first.state === "succeeded", first);

  const doc = (await api(`/v1/docs/${encodeURIComponent(`${COLLECTION}/day/d7.md`)}`, { key })).text;
  const search = await until(() => api(`/v1/search?query=marker7&collection=${COLLECTION}&limit=5`, { key }), (r) => r.body.results?.length > 0, 60);
  const labels = ((await api("/v1/admin/keys", { key: admin })).body as { label: string }[] | { keys: { label: string }[] });
  return { admin, key, doc, hits: search.body.results?.length ?? 0, audit: auditRows(), labels: (Array.isArray(labels) ? labels : labels.keys).map((k) => k.label).sort() };
}

async function backUpAndMeasure(key: string): Promise<void> {
  compose(["run", "--rm", "backup", "init"]);
  const full = backup();
  console.log(`     first backup: ${full.seconds} s, added ${full.added}`);
  check("backup: the first backup is taken", full.added !== "?");

  check(`backup: a day's delta of ${DAILY_DOCUMENTS} documents goes in`, (await write(key, FIRST_DOCUMENTS, DAILY_DOCUMENTS)) === 0);
  const delta = await embed();
  console.log(`     daily delta: ${DAILY_DOCUMENTS} documents of ${DOCUMENT_BYTES} bytes embedded in ${delta.seconds} s on the scripted worker, ${JSON.stringify(delta.result)}`);
  check("backup: the daily embed finishes", delta.state === "succeeded", delta);
  const daily = backup();
  console.log(`     daily backup: ${daily.seconds} s, added ${daily.added}`);
  check("backup: the daily backup is taken", daily.added !== "?");
}

/** With the services stopped, the snapshot may be what creates a database's -wal and -shm files: they must still be the service's afterwards. */
async function snapshotWhileStopped(key: string): Promise<void> {
  compose(["stop", "memoria-api", "memoria-qmd"]);
  const taken = compose(["run", "--rm", "snapshot"], true);
  compose(["start", "memoria-qmd", "memoria-api"]);
  await until(() => api("/health").catch(() => ({ status: 0, body: {}, text: "" })), (r) => r.status === 200, 90);
  check("backup: a snapshot with the services stopped works too", taken.status === 0 && taken.out.includes("/data/control.db -> /staging/control.db"), taken.out.slice(-400));
  const written = await api(`/v1/ingest/rendered?collection=${COLLECTION}&path=day/after-stop.md&updated=2026-01-15T00:00:00Z`, { method: "POST", key, headers: { "Content-Type": "text/markdown" }, body: body("afterstop") });
  check("backup: and the services still write to their databases afterwards", written.status === 202, written.text.slice(0, 300));
}

async function refuseLive(before: Before): Promise<void> {
  const refused = compose(["run", "--rm", "restore"], true);
  check("restore: refuses to write into volumes that hold data", refused.status !== 0 && refused.out.includes("already holds data"), refused.out.slice(-400));
  check("restore: and the running project is untouched", (await api(`/v1/docs/${encodeURIComponent(`${COLLECTION}/day/d7.md`)}`, { key: before.key })).text === before.doc);
}

async function destroyAndRestore(): Promise<void> {
  compose(["down"]);
  run("docker", ["volume", "rm", ...VOLUMES]);
  const left = run("docker", ["volume", "ls", "--format", "{{.Name}}"]).out.split("\n").filter((name) => VOLUMES.includes(name));
  check("restore: every data volume was destroyed, by name", left.length === 0, left);
  const restored = compose(["run", "--rm", "restore"], true);
  check("restore: the restore runs into the empty volumes", restored.status === 0 && restored.out.includes("restored latest"), restored.out.slice(-600));
  compose(["up", "-d"]);
  await until(() => api("/health").catch(() => ({ status: 0, body: {}, text: "" })), (r) => r.status === 200, 90);
  await until(() => (sidecar("/workers").workers as { state: string }[])[0]?.state, (state) => state === "up", 60);
}

async function proveRestored(before: Before): Promise<void> {
  const doc = await api(`/v1/docs/${encodeURIComponent(`${COLLECTION}/day/d7.md`)}`, { key: before.key });
  check("restore: the old writing key still works, and a document reads back byte for byte", doc.status === 200 && doc.text === before.doc, doc.status);
  const daily = await api(`/v1/docs/${encodeURIComponent(`${COLLECTION}/day/d${FIRST_DOCUMENTS + 3}.md`)}`, { key: before.key });
  check("restore: so does one from the day's delta", daily.status === 200, daily.status);
  const keys = (await api("/v1/admin/keys", { key: before.admin })).body as { label: string }[] | { keys: { label: string }[] };
  const labels = (Array.isArray(keys) ? keys : keys.keys).map((k) => k.label).sort();
  check("restore: the admin key works, and lists the same keys", JSON.stringify(labels) === JSON.stringify(before.labels), labels);
  const audit = auditRows();
  check("restore: the audit log came back, with at least what it held before", audit >= before.audit && before.audit > 0, { before: before.audit, after: audit });
}

async function proveSearchAndDeletes(before: Before): Promise<void> {
  const search = await until(() => api(`/v1/search?query=marker7&collection=${COLLECTION}&limit=5`, { key: before.key }), (r) => r.body.results?.length > 0, 60);
  check("restore: search finds it, with vectors and reranking, so the index came back too", search.body.results?.length === before.hits && !search.body.degradedReasons?.includes("no_vectors") && !search.body.degradedReasons?.includes("no_rerank"), search.body);
  const deleted = await api(`/v1/docs/${encodeURIComponent(`${COLLECTION}/day/d0.md`)}`, { key: before.key });
  const again = await api(`/v1/ingest/rendered?collection=${COLLECTION}&path=day/d0.md&updated=2026-01-15T00:00:00Z`, { method: "POST", key: before.key, headers: { "Content-Type": "text/markdown" }, body: body("marker0") });
  check("restore: the deleted document is still gone, and its tombstone still refuses the old copy", deleted.status === 404 && again.status === 409, [deleted.status, again.status, again.body?.code]);
}

const started = Date.now();
let crashed = false;
try {
  prepare();
  const before = await fill();
  await backUpAndMeasure(before.key);
  await snapshotWhileStopped(before.key);
  await refuseLive(before);
  await destroyAndRestore();
  await proveRestored(before);
  await proveSearchAndDeletes(before);
} catch (error) {
  crashed = true;
  console.error(error);
} finally {
  compose(["--profile", "backup", "--profile", "restore", "down", "-v"], true);
  run("docker", ["volume", "rm", ...VOLUMES, `${PROJECT}_memoria-backup-staging`, `${PROJECT}_memoria-restore-staging`, `${PROJECT}-repository`], true);
  run("docker", ["image", "rm", `ghcr.io/mggarofalo/memoria:${VERSION}`, `ghcr.io/mggarofalo/memoria-qmd:${VERSION}`], true);
  rmSync(dir, { recursive: true, force: true });
}

const failed = results.filter((result) => !result.ok);
console.log(`\n${results.length - failed.length} passed, ${failed.length} failed, in ${Math.round((Date.now() - started) / 1000)}s`);
process.exitCode = failed.length > 0 || crashed ? 1 : 0;
