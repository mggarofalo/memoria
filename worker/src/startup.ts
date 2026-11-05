import type { Server } from "node:http";
import type { RoleConfig, Role, WorkerConfig } from "./config.ts";
import { Inference, LlamaServers } from "./inference.ts";
import type { Logger } from "./log.ts";
import { findModel } from "./models.ts";
import { CachedMachine, systemProbes, type ProbeSource } from "./probes.ts";
import { createWorkerServer } from "./server.ts";
import { Supervisor } from "./supervisor.ts";

/** Wires the worker together, separate from the process, so tests can run all of it. */

/** How long warm start waits for model load before serving `starting` anyway. */
const WARM_START_TIMEOUT_MS = 5 * 60_000;

/** Where each role's model is. A role served by an external server needs none. */
export function modelPathsFor(config: Pick<WorkerConfig, "roles" | "modelDir">, find: typeof findModel = findModel): Partial<Record<Role, string>> {
  const paths: Partial<Record<Role, string>> = {};
  for (const role of config.roles) {
    if (role.externalUrl) continue;
    paths[role.role] = role.modelPath ?? find(config.modelDir, role.modelFile);
  }
  return paths;
}

/** One small call per role, enough to page in what lazy mapping left cold. */
function warmCall(role: Role, inference: Inference): Promise<unknown> {
  if (role === "embed") return inference.embed(["warm up"]);
  if (role === "rerank") return inference.rerank("warm up", ["warm up"]);
  return inference.complete({ prompt: "warm up", maxTokens: 1 });
}

export interface WarmStartOptions {
  roles: readonly RoleConfig[];
  ready: (timeoutMs: number) => Promise<boolean>;
  inference: Inference;
  log: Logger;
  timeoutMs?: number;
}

/**
 * llama-server loads its model at launch and never unloads it, so this is the
 * keep-warm too. A failed warm call is reported and does not stop the worker.
 */
export async function warmStart(options: WarmStartOptions): Promise<void> {
  const { roles, ready, inference, log, timeoutMs = WARM_START_TIMEOUT_MS } = options;
  if (!(await ready(timeoutMs))) {
    log.warn({ timeoutMs }, "not every model loaded before the warm start deadline");
    return;
  }
  const started = Date.now();
  const results = await Promise.allSettled(roles.map((role) => warmCall(role.role, inference)));
  log.info({ durationMs: Date.now() - started, failed: results.filter((r) => r.status === "rejected").length }, "warm start finished");
}

export interface RunningWorker {
  server: Server;
  /** Runs the warm start when the configuration asks for one. */
  warm(): Promise<void>;
  /** Stops listening and stops the models. Only the first call does anything. */
  shutdown(signal: string): Promise<void>;
}

export async function startWorker(config: WorkerConfig, log: Logger, options: { probes?: ProbeSource; find?: typeof findModel } = {}): Promise<RunningWorker> {
  const modelPaths = modelPathsFor(config, options.find);
  const supervisor = new Supervisor({ command: config.llamaServer, gpuLayers: config.gpuLayers, roles: config.roles, modelPaths, log });
  const inference = new Inference(new LlamaServers(supervisor, { timeoutMs: config.requestTimeoutMs }));
  const machine = new CachedMachine(options.probes ?? systemProbes);
  const concurrency: Partial<Record<Role, number>> = {};
  for (const role of config.roles) concurrency[role.role] = role.parallel;
  const { server } = createWorkerServer({
    token: config.token,
    allowedHosts: config.allowedHosts,
    bodyLimitBytes: config.bodyLimitBytes,
    maxBufferedBytes: config.maxBufferedBytes,
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
  return {
    server,
    warm: async () => {
      if (config.warmup) await warmStart({ roles: config.roles, ready: (ms) => supervisor.waitReady(ms), inference, log });
    },
    shutdown: async (signal) => {
      if (closing) return;
      closing = true;
      log.info({ signal }, "shutting down");
      server.close();
      await supervisor.stop();
    },
  };
}
