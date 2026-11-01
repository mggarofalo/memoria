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
