import { spawn as nodeSpawn, type ChildProcess } from "node:child_process";
import { CONTEXT_SIZE, type Role, type RoleConfig } from "./config.ts";
import type { Logger } from "./log.ts";

/**
 * Keeps one llama-server per role running. Children bind loopback only; the
 * worker's own server is the sole way in, and the child environment never holds
 * the worker's token. A crashed or hung child is restarted with backoff.
 *
 * Process trees: children die with the worker on a clean stop. If the worker
 * itself is killed, NSSM (`AppKillProcessTree`) and launchd (process group
 * teardown) clean up. A stray child holding the port fails the next bind, which
 * shows up as restarts and `loaded: false` in `/health`.
 */

export type ServerState = "starting" | "ready" | "backoff" | "stopped";

export interface ServerStatus {
  role: Role;
  file: string;
  state: ServerState;
  /** True when the server reports its model loaded. */
  loaded: boolean;
  restarts: number;
}

export interface SupervisorTimings {
  pollMs: number;
  /** How often to check a server that is up. */
  healthyPollMs: number;
  healthTimeoutMs: number;
  /** Failed checks in a row before a running server counts as hung. */
  hungAfter: number;
  backoffBaseMs: number;
  backoffMaxMs: number;
  /** Uptime after which the backoff resets. */
  stableMs: number;
  stopTimeoutMs: number;
}

/**
 * A restart throws away in-flight work and costs a model load, so "hung" means
 * about 90 seconds without an answer. On the real desktop, a 3 s timeout and 3
 * failures killed healthy servers while the machine was busy, and the reload
 * then took minutes (docs/workers.md).
 */
export const DEFAULT_TIMINGS: SupervisorTimings = {
  pollMs: 500,
  healthyPollMs: 5_000,
  healthTimeoutMs: 10_000,
  hungAfter: 6,
  backoffBaseMs: 1_000,
  backoffMaxMs: 30_000,
  stableMs: 60_000,
  stopTimeoutMs: 5_000,
};

export type SpawnFn = (command: string, args: string[], options: { env: NodeJS.ProcessEnv; stdio: ["ignore", "ignore", "pipe"]; windowsHide: boolean }) => ChildProcess;

export interface SupervisorOptions {
  command: string;
  /** Arguments before llama-server's own; tests use it to run a fixture under node. */
  prefixArgs?: string[];
  gpuLayers: number;
  roles: RoleConfig[];
  /** Resolved model path by role, for roles this worker launches. */
  modelPaths: Partial<Record<Role, string>>;
  log: Logger;
  env?: NodeJS.ProcessEnv;
  spawn?: SpawnFn;
  fetch?: typeof globalThis.fetch;
  timings?: Partial<SupervisorTimings>;
}

/** llama-server's flags for one role. Sizes mirror QMD's in-process contexts. */
export function llamaServerArgs(role: RoleConfig, modelPath: string, gpuLayers: number): string[] {
  const perSlot = CONTEXT_SIZE[role.role];
  const args = [
    "--model", modelPath,
    "--host", "127.0.0.1",
    "--port", String(role.port),
    "--n-gpu-layers", String(gpuLayers),
    "--parallel", String(role.parallel),
    "--ctx-size", String(perSlot * role.parallel),
    // The prompt cache defaults to 8 GiB of system memory and kept real documents
    // there. Requests here do not repeat, so it only costs.
    "--cache-ram", "0",
  ];
  if (role.role === "embed") args.push("--embeddings", "--batch-size", String(perSlot), "--ubatch-size", String(perSlot));
  // An ordinary server, not `--reranking`: see QmdTokensRerank.
  if (role.role === "rerank") args.push("--batch-size", String(perSlot), "--ubatch-size", String(perSlot));
  if (role.role === "generate") args.push("--jinja");
  return args;
}

/** Copy of the environment without the worker's own settings, the token above all. */
export function childEnvironment(env: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
  const out: NodeJS.ProcessEnv = {};
  for (const [key, value] of Object.entries(env)) {
    if (!key.startsWith("MEMORIA_")) out[key] = value;
  }
  return out;
}

const STDERR_TAIL_CHARS = 2000;

class ManagedServer {
  readonly config: RoleConfig;
  readonly #owner: Supervisor;
  child: ChildProcess | undefined;
  state: ServerState = "stopped";
  loaded = false;
  restarts = 0;
  #attempt = 0;
  #startedAt = 0;
  #failedChecks = 0;
  /** Bumped on every exit, so a health probe still in flight can tell it is out of date. */
  #generation = 0;
  #timer: NodeJS.Timeout | undefined;
  #stderrTail = "";

  constructor(config: RoleConfig, owner: Supervisor) {
    this.config = config;
    this.#owner = owner;
  }

  start(): void {
    this.state = "starting";
    this.loaded = false;
    this.#failedChecks = 0;
    this.#startedAt = Date.now();
    if (this.config.externalUrl) {
      this.#schedule(0);
      return;
    }
    this.#stderrTail = "";
    const { command, args, env, spawn } = this.#owner.launch(this.config);
    const child = spawn(command, args, { env, stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    this.child = child;
    child.stderr?.on("data", (chunk: Buffer) => {
      this.#stderrTail = (this.#stderrTail + chunk.toString("utf8")).slice(-STDERR_TAIL_CHARS);
    });
    child.once("error", (err: NodeJS.ErrnoException) => {
      this.#owner.log.error({ role: this.config.role, code: err.code ?? "unknown" }, "llama-server failed to start");
      this.#exited(null);
    });
    child.once("exit", (code) => this.#exited(code));
    this.#schedule(this.#owner.timings.pollMs);
  }

  #exited(code: number | null): void {
    if (this.child === undefined) return;
    this.child = undefined;
    this.loaded = false;
    this.#generation++;
    this.#clearTimer();
    if (this.#owner.stopping) {
      this.state = "stopped";
      return;
    }
    const t = this.#owner.timings;
    if (Date.now() - this.#startedAt >= t.stableMs) this.#attempt = 0;
    const delay = Math.min(t.backoffBaseMs * 2 ** this.#attempt, t.backoffMaxMs);
    this.#attempt++;
    this.restarts++;
    this.state = "backoff";
    this.#owner.log.warn({ role: this.config.role, exitCode: code, restartInMs: delay, restarts: this.restarts, stderrTail: this.#stderrTail }, "llama-server exited");
    this.#timer = setTimeout(() => this.start(), delay);
    this.#timer.unref();
  }

  #schedule(delayMs: number): void {
    this.#clearTimer();
    this.#timer = setTimeout(() => void this.#check(), delayMs);
    this.#timer.unref();
  }

  #clearTimer(): void {
    if (this.#timer) clearTimeout(this.#timer);
    this.#timer = undefined;
  }

  async #check(): Promise<void> {
    const t = this.#owner.timings;
    const generation = this.#generation;
    let healthy = false;
    try {
      const response = await this.#owner.fetchFn(new URL("health", this.#owner.baseUrl(this.config)), { signal: AbortSignal.timeout(t.healthTimeoutMs) });
      await response.body?.cancel().catch(() => {});
      healthy = response.ok;
    } catch {
      healthy = false;
    }
    // The server may have exited while this check was in flight. Its answer describes
    // a dead process: scheduling another check now would overwrite the timer that
    // restarts it, and a late "healthy" would mark the next process ready too soon.
    if (this.#owner.stopping || generation !== this.#generation) return;
    if (healthy) {
      if (this.state !== "ready") this.#owner.log.info({ role: this.config.role, startMs: Date.now() - this.#startedAt }, "llama-server ready");
      this.state = "ready";
      this.loaded = true;
      this.#failedChecks = 0;
      this.#schedule(t.healthyPollMs);
      return;
    }
    if (this.state === "ready") {
      this.#failedChecks++;
      if (this.#failedChecks >= t.hungAfter) {
        this.loaded = false;
        if (this.child) {
          this.#owner.log.warn({ role: this.config.role }, "llama-server stopped answering; restarting it");
          this.child.kill("SIGKILL");
          return; // the exit handler schedules the restart
        }
        // An external server is not ours to restart. Keep watching.
        this.state = "starting";
      }
      this.#schedule(t.healthyPollMs);
      return;
    }
    this.#schedule(t.pollMs);
  }

  async stop(): Promise<void> {
    this.#clearTimer();
    this.loaded = false;
    const child = this.child;
    if (!child) {
      this.state = "stopped";
      return;
    }
    await new Promise<void>((resolve) => {
      const forced = setTimeout(() => child.kill("SIGKILL"), this.#owner.timings.stopTimeoutMs);
      child.once("exit", () => {
        clearTimeout(forced);
        resolve();
      });
      child.kill();
    });
    this.state = "stopped";
  }
}

export class Supervisor {
  readonly log: Logger;
  readonly timings: SupervisorTimings;
  readonly fetchFn: typeof globalThis.fetch;
  stopping = false;
  readonly #options: SupervisorOptions;
  readonly #servers: ManagedServer[];

  constructor(options: SupervisorOptions) {
    this.#options = options;
    this.log = options.log;
    this.timings = { ...DEFAULT_TIMINGS, ...options.timings };
    this.fetchFn = options.fetch ?? globalThis.fetch;
    this.#servers = options.roles.map((role) => new ManagedServer(role, this));
  }

  launch(role: RoleConfig): { command: string; args: string[]; env: NodeJS.ProcessEnv; spawn: SpawnFn } {
    const modelPath = this.#options.modelPaths[role.role];
    if (modelPath === undefined) throw new Error(`No model path resolved for ${role.role}`);
    return {
      command: this.#options.command,
      args: [...(this.#options.prefixArgs ?? []), ...llamaServerArgs(role, modelPath, this.#options.gpuLayers)],
      env: childEnvironment(this.#options.env ?? process.env),
      spawn: this.#options.spawn ?? (nodeSpawn as unknown as SpawnFn),
    };
  }

  /** Base URL with a trailing slash, so relative paths resolve under it. */
  baseUrl(role: RoleConfig): string {
    const base = role.externalUrl ?? `http://127.0.0.1:${role.port}`;
    return base.endsWith("/") ? base : `${base}/`;
  }

  baseUrlFor(role: Role): string | undefined {
    const server = this.#servers.find((s) => s.config.role === role);
    return server ? this.baseUrl(server.config) : undefined;
  }

  start(): void {
    for (const server of this.#servers) server.start();
  }

  async stop(): Promise<void> {
    this.stopping = true;
    await Promise.all(this.#servers.map((s) => s.stop()));
  }

  status(): ServerStatus[] {
    return this.#servers.map((s) => ({ role: s.config.role, file: s.config.modelFile, state: s.state, loaded: s.loaded, restarts: s.restarts }));
  }

  ready(role: Role): boolean {
    return this.#servers.find((s) => s.config.role === role)?.loaded === true;
  }

  /** Resolves once every server is up, or false at the deadline. */
  async waitReady(timeoutMs: number): Promise<boolean> {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      if (this.#servers.every((s) => s.loaded)) return true;
      await new Promise((resolve) => setTimeout(resolve, Math.min(this.timings.pollMs, 100)));
    }
    return this.#servers.every((s) => s.loaded);
  }
}
