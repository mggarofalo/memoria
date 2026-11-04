import { homedir } from "node:os";
import path from "node:path";

/** Everything comes from the environment, so one code path serves NSSM, launchd and a shell. */

export type Role = "embed" | "rerank" | "generate";
export const ROLES: readonly Role[] = ["embed", "rerank", "generate"];

/**
 * QMD's default models, by file name. The sidecar checks that the embed file
 * matches what its index was built with, so changing these needs a re-embed.
 */
export const DEFAULT_MODEL_FILES: Readonly<Record<Role, string>> = {
  embed: "embeddinggemma-300M-Q8_0.gguf",
  rerank: "qwen3-reranker-0.6b-q8_0.gguf",
  generate: "qmd-query-expansion-1.7B-q4_k_m.gguf",
};

/**
 * Per-slot context sizes matching QMD's in-process defaults. llama-server
 * divides `--ctx-size` across its slots, so the argument is this times parallel.
 */
export const CONTEXT_SIZE: Readonly<Record<Role, number>> = { embed: 2048, rerank: 4096, generate: 2048 };

export interface RoleConfig {
  role: Role;
  /** The model file to report; a path when this worker launches the server. */
  modelFile: string;
  /** Explicit model path; otherwise found in `modelDir`. */
  modelPath: string | undefined;
  /** An already-running llama-server to use instead of launching one. */
  externalUrl: string | undefined;
  port: number;
  parallel: number;
}

export interface WorkerConfig {
  host: string;
  port: number;
  token: string | undefined;
  llamaServer: string;
  modelDir: string;
  gpuLayers: number;
  roles: RoleConfig[];
  /** Run one small request per role after start so the first real call is warm. */
  warmup: boolean;
  /** Requests allowed to wait, per role, before new ones get a 503. */
  maxQueue: number;
  bodyLimitBytes: number;
  requestTimeoutMs: number;
  /** Report `busy` above this one-minute load per core. 0 disables. */
  busyLoadPerCore: number;
  /** Report `busy` below this much free VRAM. 0 disables. */
  busyVramFreeMb: number;
  /** Report `busy` when the OS says the CPU is thermally limited. */
  busyOnThermal: boolean;
}

const LOOPBACK = /^(127\.\d{1,3}\.\d{1,3}\.\d{1,3}|::1|localhost)$/i;
const PRIVATE_V4 = /^(10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}|169\.254\.\d{1,3}\.\d{1,3})$/;
const PRIVATE_V6 = /^(f[cd][0-9a-f]{2}|fe[89ab][0-9a-f]):/i;

export function isLoopback(host: string): boolean {
  return LOOPBACK.test(host.trim());
}

/**
 * A literal loopback or private address. Wildcards, public addresses and
 * host names are refused: the worker has no reason to be reachable beyond the
 * LAN, and a name can resolve to anything.
 */
export function isLanAddress(host: string): boolean {
  const h = host.trim();
  return isLoopback(h) || PRIVATE_V4.test(h) || PRIVATE_V6.test(h);
}

function readBool(value: string | undefined, fallback: boolean, name: string): boolean {
  if (value === undefined || value.trim() === "") return fallback;
  const v = value.trim().toLowerCase();
  if (["1", "true", "yes", "on"].includes(v)) return true;
  if (["0", "false", "no", "off"].includes(v)) return false;
  throw new Error(`${name} must be a boolean, got "${value}"`);
}

function readNumber(value: string | undefined, fallback: number, name: string, min: number, max: number, integer = true): number {
  if (value === undefined || value.trim() === "") return fallback;
  const n = Number(value);
  if (!Number.isFinite(n) || (integer && !Number.isInteger(n)) || n < min || n > max) {
    throw new Error(`${name} must be a number between ${min} and ${max}, got "${value}"`);
  }
  return n;
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): WorkerConfig {
  const host = (env.MEMORIA_WORKER_HOST ?? "127.0.0.1").trim();
  if (!isLanAddress(host)) {
    throw new Error(`MEMORIA_WORKER_HOST must be a loopback or private LAN address, got "${host}". Wildcards and public addresses are refused.`);
  }
  const token = env.MEMORIA_WORKER_TOKEN?.trim() || undefined;
  if (token === undefined && !isLoopback(host)) throw new Error("MEMORIA_WORKER_TOKEN is required when binding a LAN address");
  if (token !== undefined && token.length < 16) throw new Error("MEMORIA_WORKER_TOKEN must be at least 16 characters");

  const enabled = (env.MEMORIA_WORKER_ROLES ?? ROLES.join(",")).split(",").map((r) => r.trim().toLowerCase()).filter(Boolean);
  for (const r of enabled) {
    if (!ROLES.includes(r as Role)) throw new Error(`MEMORIA_WORKER_ROLES has an unknown role "${r}"`);
  }
  if (enabled.length === 0) throw new Error("MEMORIA_WORKER_ROLES must name at least one role");

  const basePort = readNumber(env.MEMORIA_WORKER_LLAMA_BASE_PORT, 8384, "MEMORIA_WORKER_LLAMA_BASE_PORT", 1024, 65000);
  const defaultParallel: Record<Role, number> = { embed: 4, rerank: 2, generate: 1 };
  const roles: RoleConfig[] = [];
  ROLES.forEach((role, i) => {
    if (!enabled.includes(role)) return;
    const prefix = `MEMORIA_WORKER_${role.toUpperCase()}`;
    const modelPath = env[`${prefix}_MODEL`]?.trim() || undefined;
    const externalUrl = env[`${prefix}_URL`]?.trim() || undefined;
    roles.push({
      role,
      modelFile: modelPath ? path.basename(modelPath) : DEFAULT_MODEL_FILES[role],
      modelPath,
      externalUrl,
      port: basePort + i,
      parallel: readNumber(env[`${prefix}_PARALLEL`], defaultParallel[role], `${prefix}_PARALLEL`, 1, 64),
    });
  });

  const cacheDir = env.XDG_CACHE_HOME?.trim() || path.join(homedir(), ".cache");
  return {
    host,
    port: readNumber(env.MEMORIA_WORKER_PORT, 8383, "MEMORIA_WORKER_PORT", 1, 65535),
    token,
    llamaServer: env.MEMORIA_WORKER_LLAMA_SERVER?.trim() || "llama-server",
    modelDir: env.MEMORIA_WORKER_MODEL_DIR?.trim() || path.join(cacheDir, "qmd", "models"),
    gpuLayers: readNumber(env.MEMORIA_WORKER_GPU_LAYERS, 999, "MEMORIA_WORKER_GPU_LAYERS", 0, 100000),
    roles,
    warmup: readBool(env.MEMORIA_WORKER_WARMUP, true, "MEMORIA_WORKER_WARMUP"),
    maxQueue: readNumber(env.MEMORIA_WORKER_MAX_QUEUE, 256, "MEMORIA_WORKER_MAX_QUEUE", 0, 100000),
    bodyLimitBytes: readNumber(env.MEMORIA_WORKER_BODY_LIMIT_BYTES, 32 * 1024 * 1024, "MEMORIA_WORKER_BODY_LIMIT_BYTES", 1024, 1024 * 1024 * 1024),
    requestTimeoutMs: readNumber(env.MEMORIA_WORKER_REQUEST_TIMEOUT_MS, 120_000, "MEMORIA_WORKER_REQUEST_TIMEOUT_MS", 100, 3_600_000),
    busyLoadPerCore: readNumber(env.MEMORIA_WORKER_BUSY_LOAD_PER_CORE, 0.9, "MEMORIA_WORKER_BUSY_LOAD_PER_CORE", 0, 1000, false),
    busyVramFreeMb: readNumber(env.MEMORIA_WORKER_BUSY_VRAM_FREE_MB, 0, "MEMORIA_WORKER_BUSY_VRAM_FREE_MB", 0, 10_000_000),
    busyOnThermal: readBool(env.MEMORIA_WORKER_BUSY_ON_THERMAL, true, "MEMORIA_WORKER_BUSY_ON_THERMAL"),
  };
}
