import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, symlinkSync, writeFileSync } from "node:fs";
import { createServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { buildApp, type App } from "../src/app.ts";
import { JobQueue, type JobResult, type JobStore, type ReindexRequest } from "../src/jobs.ts";
import { queueSnapshotPathFor, SqliteJobStore } from "../src/queue-store.ts";
import { FakeBackend } from "./fake-backend.ts";
import { authHeaders, dieWithParent, dieWithParentEnv, makeApp, TEST_SECRET, tempDir, testConfig } from "./helpers.ts";

const result: JobResult = { update: { collections: 1, indexed: 1, updated: 0, unchanged: 0, removed: 0, skipped: 0, needsEmbedding: 0 }, embed: null, metadata: { documents: 1, withMetadata: 0 } };
const req = (reason: string): ReindexRequest => ({ collections: ["c"], embed: true, force: false, reason });
const queueFile = async (): Promise<string> => path.join(await tempDir("memoria-snapshot-"), "index.sqlite.queue.sqlite");
const letter = { hash: "h1", path: "c/set-aside.md", reason: "worker refused it", at: "2026-01-15T00:00:00.000Z", workerSet: "gpu" };
const SCRIPT = fileURLToPath(new URL("../scripts/snapshot-db.mjs", import.meta.url));

/** What a copy holds: its job reasons, set-aside paths and journal mode. */
function contents(file: string): { reasons: string[]; setAside: string[]; journal: string } {
  const db = new DatabaseSync(file, { readOnly: true });
  try {
    return {
      reasons: (db.prepare("SELECT reason FROM jobs ORDER BY seq").all() as Array<{ reason: string }>).map((r) => r.reason),
      setAside: (db.prepare("SELECT path FROM dead_letters").all() as Array<{ path: string }>).map((r) => r.path),
      journal: (db.prepare("PRAGMA journal_mode").get() as { journal_mode: string }).journal_mode,
    };
  } finally {
    db.close();
  }
}

const sha256 = (file: string): string => createHash("sha256").update(readFileSync(file)).digest("hex");

/** A store holding one finished job and one set-aside, as a running sidecar would. */
async function heldStore(file: string): Promise<{ store: SqliteJobStore; queue: JobQueue }> {
  const store = new SqliteJobStore(file);
  const queue = new JobQueue(async () => result, { store });
  queue.enqueue(req("first"));
  await queue.idle();
  store.deadLetters().put(letter);
  return { store, queue };
}

// --- the store ------------------------------------------------------------------

test("a held queue file is copied whole by the store that holds it, beside it under a fixed name", async () => {
  const file = await queueFile();
  const { store, queue } = await heldStore(file);
  const other = new DatabaseSync(file, { readOnly: true });
  assert.throws(() => other.prepare("SELECT count(*) FROM jobs").get(), /locked/, "nothing else can read it while it is held");
  other.close();

  const written = store.snapshot();
  const copy = queueSnapshotPathFor(file);
  assert.equal(copy, `${file}.snapshot`);
  assert.deepEqual(contents(copy), { reasons: ["first"], setAside: ["c/set-aside.md"], journal: "delete" });
  assert.deepEqual(written, { bytes: readFileSync(copy).length, sha256: sha256(copy) });
  assert.match(written.sha256, /^[0-9a-f]{64}$/);
  assert.equal(existsSync(`${copy}.partial`), false);
  assert.equal(existsSync(`${copy}-wal`), false, "one file, with nothing beside it");

  queue.enqueue(req("second"));
  await queue.idle();
  store.snapshot();
  assert.deepEqual(contents(copy).reasons, ["first", "second"], "the next copy replaces the last");
  store.close();
});

test("a copy left half-written is replaced, and a failed copy leaves the last one and nothing else", async () => {
  const file = await queueFile();
  const { store } = await heldStore(file);
  const copy = queueSnapshotPathFor(file);
  writeFileSync(`${copy}.partial`, "left by a copy that died");
  store.snapshot();
  assert.deepEqual(contents(copy).reasons, ["first"]);
  const last = sha256(copy);

  store.close();
  assert.throws(() => store.snapshot());
  assert.equal(sha256(copy), last);
  assert.equal(existsSync(`${copy}.partial`), false);
});

test("links where the copy goes are replaced, never written through", { skip: process.platform === "win32" && "symlinks need privileges on Windows" }, async () => {
  const file = await queueFile();
  const { store } = await heldStore(file);
  const elsewhere = path.join(await tempDir("memoria-elsewhere-"), "victim");
  writeFileSync(elsewhere, "");
  const copy = queueSnapshotPathFor(file);
  symlinkSync(elsewhere, copy);
  symlinkSync(elsewhere, `${copy}.partial`);
  store.snapshot();
  assert.equal(readFileSync(elsewhere, "utf8"), "");
  assert.deepEqual(contents(copy).reasons, ["first"]);
  store.close();
});

// --- the queue ------------------------------------------------------------------

test("the queue gives a copy only while a file holds its jobs", async () => {
  assert.equal(new JobQueue(async () => result).snapshot(), null, "jobs in memory only");

  const plain: JobStore = { load: () => [], save: () => {}, remove: () => {} };
  assert.equal(new JobQueue(async () => result, { store: plain }).snapshot(), null, "a store with no file");

  const taken = { bytes: 7, sha256: "ab" };
  let failing = false;
  const store: JobStore = { load: () => [], save: () => { if (failing) throw new Error("disk full"); }, remove: () => {}, snapshot: () => taken };
  const queue = new JobQueue(async () => result, { store, onStoreError: () => {} });
  assert.equal(queue.snapshot(), taken);
  failing = true;
  queue.enqueue(req("lost"));
  await queue.idle();
  assert.equal(queue.persistence().healthy, false);
  assert.equal(queue.snapshot(), null, "the file is behind the jobs");
  failing = false;
  queue.enqueue(req("recovers"));
  await queue.idle();
  assert.equal(queue.snapshot(), taken, "a write that lands brings the file level again");
  queue.suspend();
  assert.equal(queue.snapshot(), null, "shutting down");
});

// --- the route ------------------------------------------------------------------

async function appWithStore(file: string): Promise<{ app: App; store: SqliteJobStore }> {
  const store = new SqliteJobStore(file);
  const backend = new FakeBackend({ collections: [{ name: "c", root: await tempDir("memoria-root-") }], docs: [] });
  const app = await buildApp({ backend, config: testConfig(), jobStore: store });
  await app.ready();
  return { app, store };
}

test("POST /queue:snapshot needs the secret, and writes only its fixed copy whatever the request says", async () => {
  const file = await queueFile();
  const { app, store } = await appWithStore(file);
  try {
    app.ctx.jobs.enqueue(req("routed"));
    await app.ctx.jobs.idle();
    assert.equal((await app.inject({ method: "POST", url: "/queue:snapshot" })).statusCode, 401);
    assert.equal((await app.inject({ method: "POST", url: "/queue:snapshot", headers: { authorization: "Bearer wrong-secret-0123456789" } })).statusCode, 401);
    assert.equal(existsSync(queueSnapshotPathFor(file)), false, "a refused caller writes nothing");

    const elsewhere = path.join(await tempDir("memoria-elsewhere-"), "chosen.sqlite");
    const answer = await app.inject({ method: "POST", url: `/queue:snapshot?path=${encodeURIComponent(elsewhere)}`, headers: authHeaders, payload: { path: elsewhere, target: elsewhere } });
    assert.equal(answer.statusCode, 200, answer.body);
    const copy = queueSnapshotPathFor(file);
    assert.deepEqual(answer.json(), { bytes: readFileSync(copy).length, sha256: sha256(copy) });
    assert.deepEqual(contents(copy).reasons, ["routed"]);
    assert.equal(existsSync(elsewhere), false);
  } finally {
    await app.close();
    store.close();
  }
});

test("POST /queue:snapshot is 409 when no file holds the jobs, and 500 when the copy fails", async () => {
  const { app } = await makeApp();
  try {
    const answer = await app.inject({ method: "POST", url: "/queue:snapshot", headers: authHeaders });
    assert.equal(answer.statusCode, 409);
    assert.equal(answer.json().error, "queue_not_persistent");
  } finally {
    await app.close();
  }

  const file = await queueFile();
  const { app: broken, store } = await appWithStore(file);
  try {
    mkdirSync(`${queueSnapshotPathFor(file)}.partial`);
    const answer = await broken.inject({ method: "POST", url: "/queue:snapshot", headers: authHeaders });
    assert.equal(answer.statusCode, 500);
    assert.equal(answer.json().error, "internal");
  } finally {
    await broken.close();
    store.close();
  }
});

// --- the backup's script ----------------------------------------------------------

function snapshotDb(args: string[], env: Record<string, string> = {}): Promise<{ status: number | null; out: string }> {
  return new Promise((resolve) => {
    const child = spawn(process.execPath, [...dieWithParent, SCRIPT, ...args], { env: { ...dieWithParentEnv(), MEMORIA_SIDECAR_URL: "", MEMORIA_SIDECAR_SECRET: "", ...env }, stdio: ["ignore", "pipe", "pipe"] });
    let out = "";
    child.stdout.on("data", (d) => (out += d));
    child.stderr.on("data", (d) => (out += d));
    child.on("close", (status) => resolve({ status, out }));
  });
}

async function listening(server: Server): Promise<string> {
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  return `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
}

/** An address nothing listens on. */
async function nobody(): Promise<string> {
  const server = createServer();
  const url = await listening(server);
  await new Promise((resolve) => server.close(resolve));
  return url;
}

test("the backup asks a running sidecar for the queue's copy, and takes it", async () => {
  const file = await queueFile();
  const { app, store } = await appWithStore(file);
  try {
    app.ctx.jobs.enqueue(req("while running"));
    await app.ctx.jobs.idle();
    store.deadLetters().put(letter);
    const url = await app.listen({ host: "127.0.0.1", port: 0 });
    const target = path.join(await tempDir("memoria-staging-"), "queue.sqlite");

    const taken = await snapshotDb(["--queue", file, target], { MEMORIA_SIDECAR_URL: url, MEMORIA_SIDECAR_SECRET: TEST_SECRET });
    assert.equal(taken.status, 0, taken.out);
    assert.match(taken.out, /copied by the sidecar/);
    assert.deepEqual(contents(target), { reasons: ["while running"], setAside: ["c/set-aside.md"], journal: "delete" });
    assert.equal(existsSync(queueSnapshotPathFor(file)), false, "the sidecar's copy goes once read");
    assert.doesNotMatch(taken.out, new RegExp(TEST_SECRET));

    const before = sha256(target);
    const refused = await snapshotDb(["--queue", file, target], { MEMORIA_SIDECAR_URL: url, MEMORIA_SIDECAR_SECRET: "wrong-secret-0123456789" });
    assert.equal(refused.status, 1, refused.out);
    assert.match(refused.out, /refused to copy it \(401 unauthorized\), so its last copy stays/);
    assert.doesNotMatch(refused.out, /wrong-secret/);
    assert.equal(sha256(target), before);
  } finally {
    await app.close();
    store.close();
  }
});

test("with no answer, a held queue file fails the backup, and one nobody holds is read directly", async () => {
  const file = await queueFile();
  const { store } = await heldStore(file);
  const target = path.join(await tempDir("memoria-staging-"), "queue.sqlite");
  const env = { MEMORIA_SIDECAR_URL: await nobody(), MEMORIA_SIDECAR_SECRET: TEST_SECRET };

  const held = await snapshotDb(["--queue", file, target], env);
  assert.equal(held.status, 1, held.out);
  assert.match(held.out, /the sidecar did not answer \(ECONNREFUSED\), and a process holds the file, so it was not copied and there is no copy of it/);
  assert.equal(existsSync(target), false);

  store.close();
  const stopped = await snapshotDb(["--queue", file, target], env);
  assert.equal(stopped.status, 0, stopped.out);
  assert.match(stopped.out, /read directly: the sidecar did not answer \(ECONNREFUSED\), and nothing holds the file/);
  assert.deepEqual(contents(target), { reasons: ["first"], setAside: ["c/set-aside.md"], journal: "delete" });

  const none = await snapshotDb(["--queue", path.join(path.dirname(file), "missing.sqlite"), target], env);
  assert.equal(none.status, 0, none.out);
  assert.match(none.out, /skipped .*missing\.sqlite: the sidecar did not answer \(ECONNREFUSED\), and there is no such database/);
});

test("the backup refuses a copy that is not the one the sidecar answered with", async () => {
  const file = await queueFile();
  const target = path.join(await tempDir("memoria-staging-"), "queue.sqlite");
  let reply = { status: 200, body: "{}" };
  const server = createServer((_req, res) => res.writeHead(reply.status, { "content-type": "application/json" }).end(reply.body));
  const url = await listening(server);
  const run = () => snapshotDb(["--queue", file, target], { MEMORIA_SIDECAR_URL: url, MEMORIA_SIDECAR_SECRET: TEST_SECRET });
  try {
    const empty = await run();
    assert.equal(empty.status, 1);
    assert.match(empty.out, /refused to copy it \(200 with no bytes and sha256\)/);

    reply = { status: 409, body: "not json" };
    assert.match((await run()).out, /refused to copy it \(409 with no error code\)/);

    reply = { status: 200, body: JSON.stringify({ bytes: 3, sha256: "00" }) };
    const missing = await run();
    assert.equal(missing.status, 1);
    assert.match(missing.out, /there is no copy where the sidecar writes it/);

    writeFileSync(queueSnapshotPathFor(file), "abc");
    const raced = await run();
    assert.equal(raced.status, 1);
    assert.match(raced.out, /the copy changed after the sidecar wrote it/);
    assert.equal(existsSync(queueSnapshotPathFor(file)), true, "a copy that is not the one answered for is left alone");

    reply = { status: 200, body: JSON.stringify({ bytes: 3, sha256: createHash("sha256").update("abc").digest("hex") }) };
    const torn = await run();
    assert.equal(torn.status, 1, torn.out);
    assert.match(torn.out, /not a database|integrity|malformed/i);
    assert.equal(existsSync(`${target}.partial`), false);
  } finally {
    await new Promise((resolve) => server.close(resolve));
  }
});

test("the backup's other databases copy as before, and a held one fails it", async () => {
  const dir = await tempDir("memoria-staging-");
  const plain = path.join(dir, "control.db");
  const db = new DatabaseSync(plain);
  db.exec("PRAGMA journal_mode = WAL; CREATE TABLE t (x); INSERT INTO t VALUES ('kept')");
  db.close();
  const copied = await snapshotDb([plain, path.join(dir, "control.copy"), path.join(dir, "absent.db"), path.join(dir, "absent.copy")]);
  assert.equal(copied.status, 0, copied.out);
  assert.match(copied.out, /skipped .*absent\.db: there is no such database/);
  const copy = new DatabaseSync(path.join(dir, "control.copy"), { readOnly: true });
  assert.deepEqual(copy.prepare("SELECT x FROM t").all().map((r) => ({ ...r })), [{ x: "kept" }]);
  copy.close();

  const file = await queueFile();
  const { store } = await heldStore(file);
  try {
    const held = await snapshotDb([file, path.join(dir, "queue.copy")]);
    assert.equal(held.status, 1, held.out);
    assert.match(held.out, /another process holds it, so it was not copied and there is no copy of it/);
  } finally {
    store.close();
  }
});

test("the backup's script refuses arguments it cannot pair, and --queue without the sidecar's address", async () => {
  for (const args of [[], ["a"], ["a", "b", "c"], ["--queue", "a"], ["--queue", "a", "b", "--queue", "c", "d"]]) {
    const refused = await snapshotDb(args);
    assert.equal(refused.status, 2, `${args.join(" ")}: ${refused.out}`);
    assert.match(refused.out, /usage/);
  }
  const unaddressed = await snapshotDb(["--queue", "a", "b"], { MEMORIA_SIDECAR_SECRET: TEST_SECRET });
  assert.equal(unaddressed.status, 2);
  assert.match(unaddressed.out, /--queue needs MEMORIA_SIDECAR_URL and MEMORIA_SIDECAR_SECRET/);
  assert.equal((await snapshotDb(["--queue", "a", "b"], { MEMORIA_SIDECAR_URL: "http://127.0.0.1:1" })).status, 2);
});
