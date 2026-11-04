import assert from "node:assert/strict";
import { test } from "node:test";
import { isLoopbackHost, loadConfig } from "../src/config.ts";

test("defaults bind loopback on 8282 with warm models", () => {
  const c = loadConfig({});
  assert.equal(c.host, "127.0.0.1");
  assert.equal(c.port, 8282);
  assert.equal(c.secret, undefined);
  assert.equal(c.keepWarm, true);
  assert.equal(c.warmup, true);
  assert.equal(c.dbPath, undefined);
});

test("a routable bind address requires a secret", () => {
  assert.throws(() => loadConfig({ MEMORIA_QMD_HOST: "192.168.1.20" }), /MEMORIA_QMD_SECRET is required/);
  const c = loadConfig({ MEMORIA_QMD_HOST: "192.168.1.20", MEMORIA_QMD_SECRET: "0123456789abcdefXYZ" });
  assert.equal(c.host, "192.168.1.20");
  assert.equal(c.secret, "0123456789abcdefXYZ");
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
  const TOKEN = "0123456789abcdefXYZ";
  const remote = (over: NodeJS.ProcessEnv) => loadConfig({ MEMORIA_QMD_LLM: "remote", ...over });
  assert.throws(() => remote({}), /MEMORIA_QMD_WORKER_URL is required/);
  assert.throws(() => remote({ MEMORIA_QMD_WORKER_URL: "http://192.168.1.20:8383" }), /MEMORIA_QMD_WORKER_TOKEN is required/);
  assert.throws(() => remote({ MEMORIA_QMD_WORKER_URL: "http://192.168.1.20:8383", MEMORIA_QMD_WORKER_TOKEN: "short" }), /at least 16/);
  assert.throws(() => remote({ MEMORIA_QMD_WORKER_URL: "ftp://127.0.0.1:1" }), /http or https/);
  assert.throws(() => remote({ MEMORIA_QMD_WORKER_URL: "http://127.0.0.1:1/?x=1" }), /query or fragment/);
  // Errors about the URL must not repeat it: it could hold a credential.
  for (const url of [`http://me:${TOKEN}@127.0.0.1:1`, `not a url ${TOKEN}`]) {
    assert.throws(() => remote({ MEMORIA_QMD_WORKER_URL: url }), (err: Error) => !err.message.includes(TOKEN));
  }
  assert.deepEqual(remote({ MEMORIA_QMD_WORKER_URL: "http://127.0.0.1:8383" }).llm, { mode: "remote", workerUrl: "http://127.0.0.1:8383", workerToken: undefined, fallback: true, embedDimensions: 768 });
  assert.deepEqual(remote({ MEMORIA_QMD_WORKER_URL: "http://[::1]:8383" }).llm.mode, "remote");
  assert.deepEqual(remote({ MEMORIA_QMD_WORKER_URL: "http://192.168.1.20:8383", MEMORIA_QMD_WORKER_TOKEN: TOKEN, MEMORIA_QMD_LLM_FALLBACK: "0", MEMORIA_QMD_EMBED_DIMENSIONS: "0" }).llm, { mode: "remote", workerUrl: "http://192.168.1.20:8383", workerToken: TOKEN, fallback: false, embedDimensions: 0 });
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
