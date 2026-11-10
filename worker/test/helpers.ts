import { spawn } from "node:child_process";
import { createServer, type Server } from "node:net";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type { Server as HttpServer } from "node:http";
import type { Role, RoleConfig } from "../src/config.ts";
import { Inference, LlamaServers } from "../src/inference.ts";
import { createLogger, type Logger } from "../src/log.ts";
import type { MachineState } from "../src/probes.ts";
import { createWorkerServer, type Gate } from "../src/server.ts";
import { Supervisor, type SpawnFn, type SupervisorTimings } from "../src/supervisor.ts";
import { freePort, listenFetchable } from "./ports.ts";

export const TOKEN = "worker-test-token-0123456789";

export const fixturePath = path.join(path.dirname(fileURLToPath(import.meta.url)), "fixtures", "fake-llama-server.ts");

export { freePort };

export const FAST_TIMINGS: Partial<SupervisorTimings> = { pollMs: 20, healthyPollMs: 50, healthTimeoutMs: 1_000, hungAfter: 2, backoffBaseMs: 20, backoffMaxMs: 100, stopTimeoutMs: 2_000 };

export async function roleConfigs(roles: Role[] = ["embed", "rerank", "generate"]): Promise<RoleConfig[]> {
  const files: Record<Role, string> = { embed: "embeddinggemma-300M-Q8_0.gguf", rerank: "qwen3-reranker-0.6b-q8_0.gguf", generate: "qmd-query-expansion-1.7B-q4_k_m.gguf" };
  const out: RoleConfig[] = [];
  for (const role of roles) out.push({ role, modelFile: files[role], modelPath: undefined, externalUrl: undefined, port: await freePort(), parallel: 2 });
  return out;
}

/**
 * Launches the fake llama-server with its port already bound. A child that binds the port itself does so only once it has started, a second
 * or more on a loaded machine, and anything that takes the port meanwhile leaves it crash-looping on `port_in_use` past the readiness
 * deadline. So the port is bound here, at launch, and the listening socket handed over. If it cannot be bound, because something else holds
 * it, the child is told to bind it itself and fails as llama-server would, which is what the impostor test checks.
 */
export const handOverPort: SpawnFn = (command, args, options) => {
  const child = spawn(command, args, { env: options.env, stdio: ["ignore", "ignore", "pipe", "ipc"], windowsHide: options.windowsHide });
  child.channel?.unref();
  const port = Number(args[args.indexOf("--port") + 1]);
  // A check that arrives before the handover is turned away at once, rather than left waiting on a socket no one serves.
  const held = createServer((socket) => socket.destroy());
  const tell = (message: { listen: "handle" | "port" }, handle?: Server) => {
    if (!child.connected) return void held.close();
    child.send(message, handle, () => held.close());
  };
  // A failed bind means something else holds the port. Once bound, any error on the held socket is moot, as it closes at the handover.
  held.once("error", () => tell({ listen: "port" }));
  held.listen(port, "127.0.0.1", () => {
    held.on("error", () => {});
    tell({ listen: "handle" }, held);
  });
  return child;
};

/** A supervisor that launches the fake llama-server as a real child process. */
export function fakeSupervisor(roles: RoleConfig[], options: { log?: Logger; env?: NodeJS.ProcessEnv; timings?: Partial<SupervisorTimings> } = {}): Supervisor {
  const modelPaths: Partial<Record<Role, string>> = {};
  for (const r of roles) modelPaths[r.role] = path.join("models", r.modelFile);
  return new Supervisor({
    command: process.execPath,
    prefixArgs: [fixturePath],
    gpuLayers: 0,
    roles,
    modelPaths,
    log: options.log ?? createLogger(undefined, () => {}),
    timings: { ...FAST_TIMINGS, ...options.timings },
    env: options.env ?? process.env,
    spawn: handOverPort,
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
export async function startWorker(options: { token?: string | undefined; env?: NodeJS.ProcessEnv; machine?: () => Promise<MachineState>; maxQueue?: number; bodyLimitBytes?: number; maxBufferedBytes?: number; roles?: Role[] } = {}): Promise<RunningWorker> {
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
    ...(options.maxBufferedBytes !== undefined ? { maxBufferedBytes: options.maxBufferedBytes } : {}),
    maxQueue: options.maxQueue ?? 16,
    concurrency,
    inference: new Inference(new LlamaServers(supervisor, { timeoutMs: 5_000 })),
    servers: () => supervisor.status(),
    machine: options.machine ?? (async () => quietMachine),
    thresholds: { busyLoadPerCore: 0.9, busyVramFreeMb: 500, busyOnThermal: true },
    log,
  });
  const port = await listenFetchable(server);
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
