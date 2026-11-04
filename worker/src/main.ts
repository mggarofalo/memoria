import { loadConfig, type Role } from "./config.ts";
import { Inference, LlamaServers } from "./inference.ts";
import { createLogger } from "./log.ts";
import { findModel } from "./models.ts";
import { CachedMachine, systemProbes } from "./probes.ts";
import { createWorkerServer } from "./server.ts";
import { Supervisor } from "./supervisor.ts";

/** Process entry: resolve models, supervise llama-server, listen, warm, stop cleanly. */

/** How long warm start waits for model load before serving `starting` anyway. */
const WARM_START_TIMEOUT_MS = 5 * 60_000;

async function main(): Promise<void> {
  const config = loadConfig();
  const log = createLogger(config.token);

  const modelPaths: Partial<Record<Role, string>> = {};
  for (const role of config.roles) {
    if (role.externalUrl) continue;
    modelPaths[role.role] = role.modelPath ?? findModel(config.modelDir, role.modelFile);
  }

  const supervisor = new Supervisor({ command: config.llamaServer, gpuLayers: config.gpuLayers, roles: config.roles, modelPaths, log });
  const inference = new Inference(new LlamaServers(supervisor, { timeoutMs: config.requestTimeoutMs }));
  const machine = new CachedMachine(systemProbes);
  const concurrency: Partial<Record<Role, number>> = {};
  for (const role of config.roles) concurrency[role.role] = role.parallel;
  const { server } = createWorkerServer({
    token: config.token,
    bodyLimitBytes: config.bodyLimitBytes,
    maxQueue: config.maxQueue,
    concurrency,
    inference,
    servers: () => supervisor.status(),
    machine: () => machine.read(),
    thresholds: config,
    log,
  });

  supervisor.start();
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(config.port, config.host, resolve);
  });
  log.info({ host: config.host, port: config.port, roles: config.roles.map((r) => r.role), rerankBackend: inference.rerankBackend, auth: config.token !== undefined }, "memoria-worker listening");

  let closing = false;
  const shutdown = async (signal: string): Promise<void> => {
    if (closing) return;
    closing = true;
    log.info({ signal }, "shutting down");
    server.close();
    await supervisor.stop();
    process.exit(0);
  };
  process.on("SIGINT", () => void shutdown("SIGINT"));
  process.on("SIGTERM", () => void shutdown("SIGTERM"));

  if (config.warmup) {
    // llama-server loads its model at launch and never unloads it, so this is
    // the keep-warm too. One small call pages in what lazy mapping left cold.
    const ready = await supervisor.waitReady(WARM_START_TIMEOUT_MS);
    if (!ready) {
      log.warn({ timeoutMs: WARM_START_TIMEOUT_MS }, "not every model loaded before the warm start deadline");
      return;
    }
    const started = Date.now();
    const warm: Array<Promise<unknown>> = [];
    for (const role of config.roles) {
      if (role.role === "embed") warm.push(inference.embed(["warm up"]));
      if (role.role === "rerank") warm.push(inference.rerank("warm up", ["warm up"]));
      if (role.role === "generate") warm.push(inference.complete({ prompt: "warm up", maxTokens: 1 }));
    }
    const results = await Promise.allSettled(warm);
    log.info({ durationMs: Date.now() - started, failed: results.filter((r) => r.status === "rejected").length }, "warm start finished");
  }
}

main().catch((err: unknown) => {
  // Config errors are written to name the setting and never hold its value's secret.
  console.error(err instanceof Error ? err.message : "memoria-worker failed to start");
  process.exit(1);
});
