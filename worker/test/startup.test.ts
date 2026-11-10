import assert from "node:assert/strict";
import type { AddressInfo } from "node:net";
import { createServer } from "node:http";
import { test } from "node:test";
import { loadConfig, type RoleConfig, type WorkerConfig } from "../src/config.ts";
import { Inference, LlamaServers } from "../src/inference.ts";
import { createLogger } from "../src/log.ts";
import type { ProbeSource } from "../src/probes.ts";
import { modelPathsFor, startWorker, warmStart } from "../src/startup.ts";
import { fakeSupervisor, freePort, roleConfigs, TOKEN } from "./helpers.ts";

function capturedLog(): { log: ReturnType<typeof createLogger>; lines: string[] } {
  const lines: string[] = [];
  return { log: createLogger(undefined, (line) => lines.push(line)), lines };
}

const noProbes: ProbeSource = { platform: "win32", run: async () => "", loadAverage: () => 0, cores: () => 1, freeMemoryMb: () => 0, totalMemoryMb: () => 0 };

test("a model path comes from the role, then the model directory, and an external server needs none", () => {
  const role = (over: Partial<RoleConfig>): RoleConfig => ({ role: "embed", modelFile: "e.gguf", modelPath: undefined, externalUrl: undefined, port: 1, parallel: 1, ...over });
  const paths = modelPathsFor(
    {
      modelDir: "/models",
      roles: [role({ role: "embed" }), role({ role: "rerank", modelPath: "/explicit/r.gguf" }), role({ role: "generate", externalUrl: "http://127.0.0.1:9" })],
    },
    (dir, file) => `${dir}/${file}`,
  );
  assert.deepEqual(paths, { embed: "/models/e.gguf", rerank: "/explicit/r.gguf" });
});

test("warm start reports a timeout and makes no calls when the models are not ready", async () => {
  const { log, lines } = capturedLog();
  let called = false;
  const inference = new Inference(new LlamaServers({ baseUrlFor: () => { called = true; return undefined; } }, { timeoutMs: 100 }));
  await warmStart({ roles: await roleConfigs(), ready: async () => false, inference, log, timeoutMs: 123 });
  assert.equal(called, false);
  assert.equal(lines.length, 1);
  assert.match(lines[0]!, /warm start deadline/);
  assert.match(lines[0]!, /"timeoutMs":123/);
});

test("warm start calls every role and counts failures without throwing", async () => {
  const { log, lines } = capturedLog();
  const roles: string[] = [];
  const inference = new Inference(new LlamaServers({ baseUrlFor: (role) => { roles.push(role); return undefined; } }, { timeoutMs: 100 }));
  await warmStart({ roles: await roleConfigs(), ready: async () => true, inference, log });
  assert.deepEqual([...new Set(roles)].sort(), ["embed", "generate", "rerank"]);
  assert.match(lines.at(-1)!, /warm start finished/);
  assert.match(lines.at(-1)!, /"failed":3/);
});

async function externalConfig(roles: RoleConfig[], over: NodeJS.ProcessEnv = {}): Promise<WorkerConfig> {
  const env: NodeJS.ProcessEnv = { MEMORIA_WORKER_PORT: String(await freePort()), MEMORIA_WORKER_TOKEN: TOKEN, MEMORIA_WORKER_MODEL_DIR: "/unused", ...over };
  for (const r of roles) env[`MEMORIA_WORKER_${r.role.toUpperCase()}_URL`] = `http://127.0.0.1:${r.port}`;
  return loadConfig(env);
}

test("the started worker serves, warms up over real requests, and only shuts down once", async () => {
  const roles = await roleConfigs();
  const upstream = fakeSupervisor(roles);
  upstream.start();
  const { log, lines } = capturedLog();
  let worker: Awaited<ReturnType<typeof startWorker>>;
  try {
    assert.equal(await upstream.waitReady(10_000), true, `upstream ready: ${JSON.stringify(upstream.status())}`);
    const config = await externalConfig(roles);
    for (const role of config.roles) role.externalApiKey = upstream.apiKeyFor(role.role);
    worker = await startWorker(config, log, { probes: noProbes });
  } catch (err) {
    // Its children would hold this file's process open, and hang the run.
    await upstream.stop();
    throw err;
  }
  try {
    await worker.warm();
    assert.ok(lines.some((l) => /"failed":0/.test(l) && l.includes("warm start finished")), lines.join("\n"));
    assert.ok(lines.some((l) => l.includes("memoria-worker listening")));
    const { port } = worker.server.address() as AddressInfo;
    const refused = await fetch(`http://127.0.0.1:${port}/health`);
    assert.equal(refused.status, 401);
    const health = await fetch(`http://127.0.0.1:${port}/health`, { headers: { authorization: `Bearer ${TOKEN}` } });
    assert.equal(health.status, 200);
  } finally {
    await worker.shutdown("SIGTERM");
    await worker.shutdown("SIGINT");
    await upstream.stop();
  }
  assert.equal(lines.filter((l) => l.includes("shutting down")).length, 1);
  assert.equal(worker.server.listening, false);
});

test("warm is a no-op when the configuration turns it off", async () => {
  const roles = await roleConfigs();
  const { log, lines } = capturedLog();
  const config = await externalConfig(roles, { MEMORIA_WORKER_WARMUP: "off" });
  const worker = await startWorker(config, log, { probes: noProbes });
  try {
    await worker.warm();
    assert.ok(!lines.some((l) => l.includes("warm start")));
  } finally {
    await worker.shutdown("SIGTERM");
  }
});

test("a port already in use is a startup error rather than a silent hang", async () => {
  const blocker = createServer();
  await new Promise<void>((resolve) => blocker.listen(0, "127.0.0.1", resolve));
  const { port } = blocker.address() as AddressInfo;
  const roles = await roleConfigs();
  const config = await externalConfig(roles, { MEMORIA_WORKER_PORT: String(port) });
  try {
    await assert.rejects(startWorker(config, capturedLog().log, { probes: noProbes }), /EADDRINUSE/);
  } finally {
    await new Promise((resolve) => blocker.close(resolve));
  }
});
