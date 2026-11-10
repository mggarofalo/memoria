import assert from "node:assert/strict";
import { spawn, spawnSync, type ChildProcess } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { runSmoke } from "../scripts/smoke.ts";
import { fakeSupervisor, freePort, roleConfigs, TOKEN } from "./helpers.ts";

/** The real entry point as its own process, pointed at already-running fake servers. */

const main = fileURLToPath(new URL("../src/main.ts", import.meta.url));

function waitForExit(child: ChildProcess): Promise<void> {
  return new Promise((resolve) => (child.exitCode !== null || child.signalCode !== null ? resolve() : child.once("exit", () => resolve())));
}

test("the entry point starts, warms up, serves every endpoint, keeps the token out of its output, and stops", async () => {
  const roles = await roleConfigs();
  const upstream = fakeSupervisor(roles);
  upstream.start();
  try {
    assert.equal(await upstream.waitReady(10_000), true, `upstream ready: ${JSON.stringify(upstream.status())}`);
  } catch (err) {
    // Its children would hold this file's process open, and hang the run.
    await upstream.stop();
    throw err;
  }
  const port = await freePort();
  const env: NodeJS.ProcessEnv = {
    ...process.env,
    MEMORIA_WORKER_PORT: String(port),
    MEMORIA_WORKER_TOKEN: TOKEN,
    MEMORIA_WORKER_MODEL_DIR: "/no/models/needed/for/external/servers",
  };
  for (const r of roles) {
    env[`MEMORIA_WORKER_${r.role.toUpperCase()}_URL`] = `http://127.0.0.1:${r.port}`;
    env[`MEMORIA_WORKER_${r.role.toUpperCase()}_API_KEY`] = upstream.apiKeyFor(r.role);
  }
  const child = spawn(process.execPath, [main], { env, stdio: ["ignore", "pipe", "pipe"] });
  let output = "";
  child.stdout.on("data", (c: Buffer) => (output += c.toString()));
  child.stderr.on("data", (c: Buffer) => (output += c.toString()));
  try {
    const deadline = Date.now() + 15_000;
    while (!output.includes("warm start finished") && Date.now() < deadline && child.exitCode === null) {
      await new Promise((r) => setTimeout(r, 25));
    }
    assert.match(output, /memoria-worker listening/, output);
    assert.match(output, /"failed":0/, output);
    const results = await runSmoke(`http://127.0.0.1:${port}`, TOKEN);
    for (const r of results) assert.equal(r.ok, true, `${r.name}: ${r.detail}`);
    assert.ok(!output.includes(TOKEN));
  } finally {
    child.kill("SIGTERM");
    await waitForExit(child);
    await upstream.stop();
  }
});

test("bad configuration exits non-zero, names the setting, and does not repeat a token", () => {
  const run = (over: NodeJS.ProcessEnv) => spawnSync(process.execPath, [main], { env: { ...process.env, ...over }, encoding: "utf8", timeout: 20_000 });
  const wildcard = run({ MEMORIA_WORKER_HOST: "0.0.0.0", MEMORIA_WORKER_TOKEN: TOKEN });
  assert.equal(wildcard.status, 1);
  assert.match(wildcard.stderr, /MEMORIA_WORKER_HOST/);
  assert.ok(!wildcard.stderr.includes(TOKEN));
  const short = run({ MEMORIA_WORKER_TOKEN: "short-secret" });
  assert.equal(short.status, 1);
  assert.match(short.stderr, /at least 16/);
  assert.ok(!short.stderr.includes("short-secret"));
});

test("the entry point reads its token from a file, and a bad file stops it without repeating the content", async (t) => {
  const dir = await mkdtemp(path.join(tmpdir(), "memoria-worker-token-"));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const run = (over: NodeJS.ProcessEnv) => spawnSync(process.execPath, [main], { env: { ...process.env, MEMORIA_WORKER_TOKEN: "", MEMORIA_WORKER_MODEL_DIR: "/definitely/not/here", ...over }, encoding: "utf8", timeout: 20_000 });

  const missing = run({ MEMORIA_WORKER_TOKEN_FILE: path.join(dir, "absent") });
  assert.equal(missing.status, 1);
  assert.match(missing.stderr, /MEMORIA_WORKER_TOKEN_FILE: cannot read/);

  const twoLines = path.join(dir, "two-lines");
  await writeFile(twoLines, `${TOKEN}\nsecond-line\n`, { mode: 0o600 });
  const refused = run({ MEMORIA_WORKER_TOKEN_FILE: twoLines });
  assert.equal(refused.status, 1);
  assert.match(refused.stderr, /must hold one line/);
  assert.ok(!refused.stderr.includes(TOKEN));

  // Past the token: this run gets as far as looking for models, so the file was accepted.
  const good = path.join(dir, "token");
  await writeFile(good, `${TOKEN}\n`, { mode: 0o600 });
  const accepted = run({ MEMORIA_WORKER_TOKEN_FILE: good });
  assert.equal(accepted.status, 1);
  assert.match(accepted.stderr, /cannot be read/);
  assert.ok(!accepted.stderr.includes(TOKEN));
});

test("a missing model is a clear startup failure, not a crash loop of llama-server", () => {
  const run = spawnSync(process.execPath, [main], { env: { ...process.env, MEMORIA_WORKER_MODEL_DIR: "/definitely/not/here" }, encoding: "utf8", timeout: 20_000 });
  assert.equal(run.status, 1);
  assert.match(run.stderr, /cannot be read/);
});
