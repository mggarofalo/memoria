import assert from "node:assert/strict";
import { test } from "node:test";
import { isLoopbackHost, loadConfig } from "../src/config.ts";

test("defaults bind loopback on 8282 with warm models", () => {
  const c = loadConfig({});
  assert.equal(c.host, "127.0.0.1");
  assert.equal(c.port, 8282);
  assert.equal(c.secretGenerated, true);
  assert.equal(c.keepWarm, true);
  assert.equal(c.warmup, true);
  assert.equal(c.dbPath, undefined);
});

test("a routable bind address requires a secret", () => {
  assert.throws(() => loadConfig({ MEMORIA_QMD_HOST: "192.168.1.20" }), /MEMORIA_QMD_SECRET is required/);
  const c = loadConfig({ MEMORIA_QMD_HOST: "192.168.1.20", MEMORIA_QMD_SECRET: "Xk29vQmB7fLp3ZtRw", MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK: "1" });
  assert.equal(c.host, "192.168.1.20");
  assert.equal(c.secret, "Xk29vQmB7fLp3ZtRw");
});

test("short secrets are rejected", () => {
  assert.throws(() => loadConfig({ MEMORIA_QMD_SECRET: "short" }), /at least 16/);
});

test("reindex debounce defaults to a minute, capped at five, and is configurable", () => {
  const d = loadConfig({});
  assert.equal(d.reindexDebounceMs, 60_000);
  assert.equal(d.reindexMaxWaitMs, 300_000);
  assert.equal(d.shutdownDrainMs, 10_000);
  const c = loadConfig({ MEMORIA_QMD_REINDEX_DEBOUNCE_MS: "0", MEMORIA_QMD_REINDEX_MAX_WAIT_MS: "5000", MEMORIA_QMD_SHUTDOWN_DRAIN_MS: "250" });
  assert.equal(c.reindexDebounceMs, 0);
  assert.equal(c.reindexMaxWaitMs, 5000);
  assert.equal(c.shutdownDrainMs, 250);
  assert.throws(() => loadConfig({ MEMORIA_QMD_REINDEX_DEBOUNCE_MS: "-1" }), /MEMORIA_QMD_REINDEX_DEBOUNCE_MS/);
  assert.throws(() => loadConfig({ MEMORIA_QMD_REINDEX_DEBOUNCE_MS: "60000", MEMORIA_QMD_REINDEX_MAX_WAIT_MS: "1000" }), /must not be shorter/);
});

test("LLM mode is local by default, and worker settings are ignored unless remote", () => {
  assert.deepEqual(loadConfig({}).llm, { mode: "local" });
  assert.deepEqual(loadConfig({ MEMORIA_QMD_LLM: "LOCAL" }).llm, { mode: "local" });
  assert.deepEqual(loadConfig({ MEMORIA_QMD_WORKER_URL: "not a url", MEMORIA_QMD_WORKER_TOKEN: "x" }).llm, { mode: "local" });
  assert.throws(() => loadConfig({ MEMORIA_QMD_LLM: "cloud" }), /MEMORIA_QMD_LLM/);
});

test("remote mode needs a clean worker URL and, off loopback, a token", () => {
  const TOKEN = "Xk29vQmB7fLp3ZtRw";
  const remote = (over: NodeJS.ProcessEnv) => loadConfig({ MEMORIA_QMD_LLM: "remote", MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK: "1", ...over });
  assert.throws(() => remote({}), /MEMORIA_QMD_WORKER_URL is required/);
  const one = (url: string, token: string | undefined) => [{ name: new URL(url).host, url, token, capabilities: ["embed", "rerank", "generate"], priority: 100, maxConcurrent: 4 }];
  assert.throws(() => remote({ MEMORIA_QMD_WORKER_URL: "http://192.168.1.20:8383" }), /MEMORIA_QMD_WORKER_TOKEN is required/);
  assert.throws(() => remote({ MEMORIA_QMD_WORKER_URL: "http://192.168.1.20:8383", MEMORIA_QMD_WORKER_TOKEN: "short" }), /at least 16/);
  assert.throws(() => remote({ MEMORIA_QMD_WORKER_URL: "ftp://127.0.0.1:1" }), /http or https/);
  assert.throws(() => remote({ MEMORIA_QMD_WORKER_URL: "http://127.0.0.1:1/?x=1" }), /query or fragment/);
  // Errors about the URL must not repeat it: it could hold a credential.
  for (const url of [`http://me:${TOKEN}@127.0.0.1:1`, `not a url ${TOKEN}`]) {
    assert.throws(() => remote({ MEMORIA_QMD_WORKER_URL: url }), (err: Error) => !err.message.includes(TOKEN));
  }
  assert.deepEqual(remote({ MEMORIA_QMD_WORKER_URL: "http://127.0.0.1:8383" }).llm, { mode: "remote", workers: one("http://127.0.0.1:8383", undefined), fallback: true, embedDimensions: 768, pollMs: 10_000, embedBatchBytes: 262_144, rerankTimeoutMs: 20_000, searchDeadlineMs: 60_000 });
  assert.deepEqual(remote({ MEMORIA_QMD_WORKER_URL: "http://[::1]:8383" }).llm.mode, "remote");
  assert.deepEqual(remote({ MEMORIA_QMD_WORKER_URL: "http://192.168.1.20:8383", MEMORIA_QMD_WORKER_TOKEN: TOKEN, MEMORIA_QMD_LLM_FALLBACK: "0", MEMORIA_QMD_EMBED_DIMENSIONS: "0", MEMORIA_QMD_WORKER_POLL_MS: "500", MEMORIA_QMD_EMBED_BATCH_BYTES: "2048" }).llm, { mode: "remote", workers: one("http://192.168.1.20:8383", TOKEN), fallback: false, embedDimensions: 0, pollMs: 500, embedBatchBytes: 2048, rerankTimeoutMs: 20_000, searchDeadlineMs: 60_000 });
});

test("a worker list names each token's environment variable and holds no secrets itself", () => {
  const TOKEN = "Xk29vQmB7fLp3ZtRw";
  const list = (workers: unknown, env: NodeJS.ProcessEnv = {}) => loadConfig({ MEMORIA_QMD_LLM: "remote", MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK: "1", MEMORIA_QMD_WORKERS: JSON.stringify(workers), ...env });
  const c = list(
    [
      { name: "desktop", url: "http://192.168.1.20:8383", tokenEnv: "DESKTOP_TOKEN", priority: 1, maxConcurrent: 8 },
      { name: "macbook.local", url: "http://127.0.0.1:8383", capabilities: ["rerank"] },
    ],
    { DESKTOP_TOKEN: TOKEN },
  );
  assert.ok(c.llm.mode === "remote");
  assert.deepEqual(c.llm.workers, [
    { name: "desktop", url: "http://192.168.1.20:8383", token: TOKEN, capabilities: ["embed", "rerank", "generate"], priority: 1, maxConcurrent: 8 },
    { name: "macbook.local", url: "http://127.0.0.1:8383", token: undefined, capabilities: ["rerank"], priority: 100, maxConcurrent: 4 },
  ]);

  const good = { name: "a", url: "http://127.0.0.1:1" };
  assert.throws(() => list([]), /non-empty/);
  assert.throws(() => loadConfig({ MEMORIA_QMD_LLM: "remote", MEMORIA_QMD_WORKERS: `not json ${TOKEN}` }), (e: Error) => /valid JSON/.test(e.message) && !e.message.includes(TOKEN));
  assert.throws(() => loadConfig({ MEMORIA_QMD_LLM: "remote", MEMORIA_QMD_WORKERS: JSON.stringify([good]), MEMORIA_QMD_WORKER_URL: "http://127.0.0.1:1" }), /not both/);
  assert.throws(() => list([{ ...good, token: TOKEN }]), (e: Error) => /must not hold a token/.test(e.message) && !e.message.includes(TOKEN));
  assert.throws(() => list([good, good]), /repeats/);
  assert.throws(() => list([{ ...good, name: "bad name" }]), /name must be/);
  assert.throws(() => list([{ name: "a" }]), /url is required/);
  assert.throws(() => list([{ name: "a", url: `http://x:${TOKEN}@127.0.0.1:1` }]), (e: Error) => /credentials/.test(e.message) && !e.message.includes(TOKEN));
  assert.throws(() => list([{ name: "a", url: "http://192.168.1.20:1" }]), /required when the worker is not on loopback/);
  assert.throws(() => list([{ ...good, tokenEnv: "UNSET_TOKEN" }]), /UNSET_TOKEN, which is not set/);
  assert.throws(() => list([{ ...good, tokenEnv: "SHORT" }], { SHORT: "abc" }), /at least 16/);
  assert.throws(() => list([{ ...good, capabilities: ["transcribe"] }]), /capabilities/);
  assert.throws(() => list([{ ...good, maxConcurrent: 0 }]), /maxConcurrent/);
  assert.equal(loadConfig({}).queueDbPath, undefined);
  assert.equal(loadConfig({ MEMORIA_QMD_QUEUE_DB_PATH: " /q/jobs.sqlite " }).queueDbPath, "/q/jobs.sqlite");
});

test("a pool token can come from a file, like every other secret", async () => {
  const { mkdtemp, writeFile, chmod } = await import("node:fs/promises");
  const { tmpdir } = await import("node:os");
  const { join } = await import("node:path");
  const file = join(await mkdtemp(join(tmpdir(), "memoria-config-")), "token");
  await writeFile(file, "Xk29vQmB7fLp3ZtRw\n");
  await chmod(file, 0o600);
  const workers = JSON.stringify([{ name: "a", url: "http://192.168.1.20:1", tokenEnv: "A_TOKEN" }]);
  const c = loadConfig({ MEMORIA_QMD_LLM: "remote", MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK: "1", MEMORIA_QMD_WORKERS: workers, A_TOKEN_FILE: file });
  assert.ok(c.llm.mode === "remote");
  assert.equal(c.llm.workers[0]!.token, "Xk29vQmB7fLp3ZtRw");
  assert.throws(() => loadConfig({ MEMORIA_QMD_LLM: "remote", MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK: "1", MEMORIA_QMD_WORKERS: workers, A_TOKEN_FILE: file, A_TOKEN: "Xk29vQmB7fLp3ZtRw" }), /only one/);
});

test("loopback detection covers the usual spellings", () => {
  for (const h of ["127.0.0.1", "127.1.2.3", "localhost", "LOCALHOST", "::1"]) assert.equal(isLoopbackHost(h), true, h);
  for (const h of ["0.0.0.0", "::", "192.168.0.1", "10.0.0.5", "example.com"]) assert.equal(isLoopbackHost(h), false, h);
});

test("numeric and boolean settings are validated", () => {
  assert.throws(() => loadConfig({ MEMORIA_QMD_PORT: "99999" }), /MEMORIA_QMD_PORT/);
  assert.throws(() => loadConfig({ MEMORIA_QMD_PORT: "abc" }), /MEMORIA_QMD_PORT/);
  assert.throws(() => loadConfig({ MEMORIA_QMD_KEEP_WARM: "maybe" }), /boolean/);
  const c = loadConfig({ MEMORIA_QMD_PORT: "9000", MEMORIA_QMD_KEEP_WARM: "off", MEMORIA_QMD_WARMUP: "false", MEMORIA_QMD_DB_PATH: " C:/x/index.sqlite " });
  assert.equal(c.port, 9000);
  assert.equal(c.keepWarm, false);
  assert.equal(c.warmup, false);
  assert.equal(c.dbPath, "C:/x/index.sqlite");
});
