import { createServer, type Server } from "node:net";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type { AddressInfo } from "node:net";
import type { Server as HttpServer } from "node:http";
import type { Role, RoleConfig } from "../src/config.ts";
import { Inference, LlamaServers } from "../src/inference.ts";
import { createLogger, type Logger } from "../src/log.ts";
import type { MachineState } from "../src/probes.ts";
import { createWorkerServer, type Gate } from "../src/server.ts";
import { Supervisor, type SupervisorTimings } from "../src/supervisor.ts";

export const TOKEN = "worker-test-token-0123456789";

const fixture = path.join(path.dirname(fileURLToPath(import.meta.url)), "fixtures", "fake-llama-server.ts");

export async function freePort(): Promise<number> {
  const probe: Server = createServer();
  await new Promise<void>((resolve) => probe.listen(0, "127.0.0.1", resolve));
  const { port } = probe.address() as AddressInfo;
  await new Promise((resolve) => probe.close(resolve));
  return port;
}

export const FAST_TIMINGS: Partial<SupervisorTimings> = { pollMs: 20, healthyPollMs: 50, healthTimeoutMs: 200, hungAfter: 2, backoffBaseMs: 20, backoffMaxMs: 100, stopTimeoutMs: 2_000 };

export async function roleConfigs(roles: Role[] = ["embed", "rerank", "generate"]): Promise<RoleConfig[]> {
  const files: Record<Role, string> = { embed: "embeddinggemma-300M-Q8_0.gguf", rerank: "qwen3-reranker-0.6b-q8_0.gguf", generate: "qmd-query-expansion-1.7B-q4_k_m.gguf" };
  const out: RoleConfig[] = [];
  for (const role of roles) out.push({ role, modelFile: files[role], modelPath: undefined, externalUrl: undefined, port: await freePort(), parallel: 2 });
  return out;
}

/** A supervisor that launches the fake llama-server as a real child process. */
export function fakeSupervisor(roles: RoleConfig[], options: { log?: Logger; env?: NodeJS.ProcessEnv; timings?: Partial<SupervisorTimings> } = {}): Supervisor {
  const modelPaths: Partial<Record<Role, string>> = {};
  for (const r of roles) modelPaths[r.role] = path.join("models", r.modelFile);
  return new Supervisor({
    command: process.execPath,
    prefixArgs: [fixture],
    gpuLayers: 0,
    roles,
    modelPaths,
    log: options.log ?? createLogger(undefined, () => {}),
    timings: { ...FAST_TIMINGS, ...options.timings },
    env: options.env ?? process.env,
  });
}

export interface RunningWorker {
  url: string;
  roles: RoleConfig[];
  supervisor: Supervisor;
  server: HttpServer;
  gates: Map<Role, Gate>;
  logs: string[];
  stop(): Promise<void>;
}

export const quietMachine: MachineState = { loadPerCore: 0.1, cpuSpeedLimit: null, vram: { freeMb: 8000, totalMb: 12000, source: "nvidia-smi" } };

/** The whole worker in this process, over fake llama-servers. */
export async function startWorker(options: { token?: string | undefined; env?: NodeJS.ProcessEnv; machine?: () => Promise<MachineState>; maxQueue?: number; bodyLimitBytes?: number; roles?: Role[] } = {}): Promise<RunningWorker> {
  const token = "token" in options ? options.token : TOKEN;
  const logs: string[] = [];
  const log = createLogger(undefined, (line) => logs.push(line));
  const roles = await roleConfigs(options.roles);
  const supervisor = fakeSupervisor(roles, { log, ...(options.env ? { env: options.env } : {}) });
  supervisor.start();
  const concurrency: Partial<Record<Role, number>> = {};
  for (const r of roles) concurrency[r.role] = r.parallel;
  const { server, gates } = createWorkerServer({
    token,
    bodyLimitBytes: options.bodyLimitBytes ?? 1024 * 1024,
    maxQueue: options.maxQueue ?? 16,
    concurrency,
    inference: new Inference(new LlamaServers(supervisor, { timeoutMs: 5_000 })),
    servers: () => supervisor.status(),
    machine: options.machine ?? (async () => quietMachine),
    thresholds: { busyLoadPerCore: 0.9, busyVramFreeMb: 500, busyOnThermal: true },
    log,
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address() as AddressInfo;
  return {
    url: `http://127.0.0.1:${port}`,
    roles,
    supervisor,
    server,
    gates,
    logs,
    stop: async () => {
      await new Promise((resolve) => server.close(resolve));
      await supervisor.stop();
    },
  };
}
