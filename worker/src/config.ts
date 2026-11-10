import { isIPv4, isIPv6 } from "node:net";
import { homedir } from "node:os";
import path from "node:path";
import { resolveSecret } from "./secret-file.ts";

/**
 * Everything comes from the environment, so one code path serves NSSM, launchd
 * and a shell. The token may instead come from a file named by
 * `MEMORIA_WORKER_TOKEN_FILE`, which is what the installed services use.
 */

export type Role = "embed" | "rerank" | "generate";
export const ROLES: readonly Role[] = ["embed", "rerank", "generate"];

/**
 * Memoria's default models, by file name: Qwen3-Embedding-0.6B and the 4B
 * reranker, chosen over QMD's own defaults on measured retrieval
 * (docs/workers.md#the-models). The sidecar checks that the embed file matches
 * what its index was built with, so changing these needs a re-embed.
 */
export const DEFAULT_MODEL_FILES: Readonly<Record<Role, string>> = {
  embed: "Qwen3-Embedding-0.6B-Q8_0.gguf",
  rerank: "Qwen3-Reranker-4B.Q8_0.gguf",
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
  /** The key for that server, if it has one. */
  externalApiKey?: string | undefined;
  port: number;
  parallel: number;
}

export interface WorkerConfig {
  host: string;
  /** DNS names a request may arrive under. An IP address is always fine. */
  allowedHosts: ReadonlySet<string>;
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
  /** Request bodies held in memory at once, across every request. */
  maxBufferedBytes: number;
  requestTimeoutMs: number;
  /** Report `busy` above this one-minute load per core. 0 disables. */
  busyLoadPerCore: number;
  /** Report `busy` below this much free VRAM. 0 disables. */
  busyVramFreeMb: number;
  /** Report `busy` when the OS says the CPU is thermally limited. */
  busyOnThermal: boolean;
}

const PRIVATE_V6 = /^(f[cd][0-9a-f]{2}|fe[89ab][0-9a-f]):/i;

function octets(host: string): number[] | null {
  return isIPv4(host) ? host.split(".").map(Number) : null;
}

export function isLoopback(host: string): boolean {
  const h = host.trim().toLowerCase();
  return h === "localhost" || h === "::1" || octets(h)?.[0] === 127;
}

function isPrivateV4(host: string): boolean {
  const o = octets(host);
  if (!o) return false;
  const [a, b] = o as [number, number];
  return a === 10 || (a === 192 && b === 168) || (a === 172 && b >= 16 && b <= 31) || (a === 169 && b === 254);
}

/**
 * A literal loopback or private address. Wildcards, public addresses and
 * host names are refused: the worker has no reason to be reachable beyond the
 * LAN, and a name can resolve to anything.
 */
export function isLanAddress(host: string): boolean {
  const h = host.trim();
  return isLoopback(h) || isPrivateV4(h) || (isIPv6(h) && PRIVATE_V6.test(h));
}

/**
 * Transcript text goes to this address, so the rules match the worker's own bind:
 * a literal loopback or private address, with no credentials in the URL.
 */
function checkedExternalUrl(value: string, name: string): string {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Error(`${name} is not a URL`);
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") throw new Error(`${name} must be http or https`);
  if (url.username !== "" || url.password !== "") throw new Error(`${name} must not contain credentials`);
  if (!isLanAddress(url.hostname.replace(/^\[|\]$/g, ""))) throw new Error(`${name} must point at a loopback or private LAN address`);
  return value;
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

const PLACEHOLDERS = ["a-long-random", "at-least-16", "changeme", "change-me", "replace-me", "replaceme", "placeholder", "your-token", "yourtoken", "example", "password"];
const KEYBOARD_ROWS = ["1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm"];

function shareInRuns(lower: string): number {
  const nextTo = (a: string, b: string): boolean => Math.abs(a.charCodeAt(0) - b.charCodeAt(0)) === 1 || KEYBOARD_ROWS.some((row) => row.includes(a + b) || row.includes(b + a));
  let inRuns = 0;
  let run = 1;
  for (let i = 1; i <= lower.length; i++) {
    if (i < lower.length && nextTo(lower[i - 1]!, lower[i]!)) {
      run++;
      continue;
    }
    if (run >= 4) inRuns += run;
    run = 1;
  }
  return inRuns / lower.length;
}

/** The same rules as the sidecar's, which cannot be shared between these two packages. */
function checkTokenStrength(token: string): void {
  if (token.length < 16) throw new Error("MEMORIA_WORKER_TOKEN must be at least 16 characters");
  const lower = token.toLowerCase();
  if (new Set(token).size < 6 || PLACEHOLDERS.some((p) => lower.includes(p)) || shareInRuns(lower) >= 0.75) {
    throw new Error("MEMORIA_WORKER_TOKEN is too weak: it looks like a placeholder or repeats itself. Generate one with a random source.");
  }
}

function checkPlaintextOptIn(host: string, env: NodeJS.ProcessEnv): void {
  if (isLoopback(host) || ["1", "true", "yes", "on"].includes((env.MEMORIA_WORKER_ALLOW_HTTP_ON_PRIVATE_NETWORK ?? "").trim().toLowerCase())) return;
  throw new Error(`The worker serves plain http, and MEMORIA_WORKER_HOST (${host}) is not loopback, so its token and every text it embeds are in clear unless the network itself is encrypted. On a WireGuard tunnel, which is the intended setup, set MEMORIA_WORKER_ALLOW_HTTP_ON_PRIVATE_NETWORK=1. See docs/deployment.md.`);
}

function readAllowedHosts(env: NodeJS.ProcessEnv): ReadonlySet<string> {
  return new Set(["localhost", ...(env.MEMORIA_WORKER_ALLOWED_HOSTS ?? "").split(",").map((h) => h.trim().toLowerCase()).filter((h) => h !== "")]);
}

function readHost(env: NodeJS.ProcessEnv): string {
  const host = (env.MEMORIA_WORKER_HOST ?? "127.0.0.1").trim();
  if (!isLanAddress(host)) {
    throw new Error(`MEMORIA_WORKER_HOST must be a loopback or private LAN address, got "${host}". Wildcards and public addresses are refused.`);
  }
  return host;
}

function readToken(env: NodeJS.ProcessEnv, host: string): string | undefined {
  const token = resolveSecret(env, "MEMORIA_WORKER_TOKEN");
  if (token === undefined && !isLoopback(host)) throw new Error("MEMORIA_WORKER_TOKEN is required when binding a LAN address");
  if (token !== undefined) checkTokenStrength(token);
  return token;
}

function readEnabledRoles(env: NodeJS.ProcessEnv): string[] {
  const enabled = (env.MEMORIA_WORKER_ROLES ?? ROLES.join(",")).split(",").map((r) => r.trim().toLowerCase()).filter(Boolean);
  for (const r of enabled) {
    if (!ROLES.includes(r as Role)) throw new Error(`MEMORIA_WORKER_ROLES has an unknown role "${r}"`);
  }
  if (enabled.length === 0) throw new Error("MEMORIA_WORKER_ROLES must name at least one role");
  return enabled;
}

const DEFAULT_PARALLEL: Readonly<Record<Role, number>> = { embed: 4, rerank: 2, generate: 1 };

function readRole(env: NodeJS.ProcessEnv, role: Role, port: number): RoleConfig {
  const prefix = `MEMORIA_WORKER_${role.toUpperCase()}`;
  const modelPath = env[`${prefix}_MODEL`]?.trim() || undefined;
  const rawUrl = env[`${prefix}_URL`]?.trim();
  const externalUrl = rawUrl ? checkedExternalUrl(rawUrl, `${prefix}_URL`) : undefined;
  return {
    role,
    modelFile: modelPath ? path.basename(modelPath) : DEFAULT_MODEL_FILES[role],
    modelPath,
    externalUrl,
    externalApiKey: env[`${prefix}_API_KEY`]?.trim() || undefined,
    port,
    parallel: readNumber(env[`${prefix}_PARALLEL`], DEFAULT_PARALLEL[role], `${prefix}_PARALLEL`, 1, 64),
  };
}

function readRoles(env: NodeJS.ProcessEnv): RoleConfig[] {
  const enabled = readEnabledRoles(env);
  const basePort = readNumber(env.MEMORIA_WORKER_LLAMA_BASE_PORT, 8384, "MEMORIA_WORKER_LLAMA_BASE_PORT", 1024, 65000);
  // A role keeps its port whichever others are enabled.
  return ROLES.flatMap((role, i) => (enabled.includes(role) ? [readRole(env, role, basePort + i)] : []));
}

function defaultModelDir(env: NodeJS.ProcessEnv): string {
  const cacheDir = env.XDG_CACHE_HOME?.trim() || path.join(homedir(), ".cache");
  return path.join(cacheDir, "qmd", "models");
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): WorkerConfig {
  const host = readHost(env);
  const token = readToken(env, host);
  checkPlaintextOptIn(host, env);
  const roles = readRoles(env);
  return {
    host,
    allowedHosts: readAllowedHosts(env),
    port: readNumber(env.MEMORIA_WORKER_PORT, 8383, "MEMORIA_WORKER_PORT", 1, 65535),
    token,
    llamaServer: env.MEMORIA_WORKER_LLAMA_SERVER?.trim() || "llama-server",
    modelDir: env.MEMORIA_WORKER_MODEL_DIR?.trim() || defaultModelDir(env),
    gpuLayers: readNumber(env.MEMORIA_WORKER_GPU_LAYERS, 999, "MEMORIA_WORKER_GPU_LAYERS", 0, 100000),
    roles,
    warmup: readBool(env.MEMORIA_WORKER_WARMUP, true, "MEMORIA_WORKER_WARMUP"),
    maxQueue: readNumber(env.MEMORIA_WORKER_MAX_QUEUE, 256, "MEMORIA_WORKER_MAX_QUEUE", 0, 100000),
    bodyLimitBytes: readNumber(env.MEMORIA_WORKER_BODY_LIMIT_BYTES, 32 * 1024 * 1024, "MEMORIA_WORKER_BODY_LIMIT_BYTES", 1024, 1024 * 1024 * 1024),
    maxBufferedBytes: readNumber(env.MEMORIA_WORKER_MAX_BUFFERED_BYTES, 256 * 1024 * 1024, "MEMORIA_WORKER_MAX_BUFFERED_BYTES", 1024, 64 * 1024 * 1024 * 1024),
    requestTimeoutMs: readNumber(env.MEMORIA_WORKER_REQUEST_TIMEOUT_MS, 120_000, "MEMORIA_WORKER_REQUEST_TIMEOUT_MS", 100, 3_600_000),
    busyLoadPerCore: readNumber(env.MEMORIA_WORKER_BUSY_LOAD_PER_CORE, 0.9, "MEMORIA_WORKER_BUSY_LOAD_PER_CORE", 0, 1000, false),
    busyVramFreeMb: readNumber(env.MEMORIA_WORKER_BUSY_VRAM_FREE_MB, 0, "MEMORIA_WORKER_BUSY_VRAM_FREE_MB", 0, 10_000_000),
    busyOnThermal: readBool(env.MEMORIA_WORKER_BUSY_ON_THERMAL, true, "MEMORIA_WORKER_BUSY_ON_THERMAL"),
  };
}
