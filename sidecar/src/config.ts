import type { Capability, PoolWorkerConfig } from "./llm/types.ts";

/**
 * Process configuration. Everything comes from environment variables so the
 * same build runs under NSSM, in a shell, or in CI. Secrets may instead come
 * from a file named by `<NAME>_FILE`, which is what the installed service uses.
 */
import { resolveSecret } from "./secret-file.ts";

export interface SidecarConfig {
  /** Interface to bind. Loopback by default; set a LAN address to serve Memoria.Api. */
  host: string;
  port: number;
  /** Path to QMD's index.sqlite. Undefined means the SDK default (~/.cache/qmd/index.sqlite). */
  dbPath: string | undefined;
  /** Shared secret for `Authorization: Bearer`. Required unless bound to loopback. */
  secret: string | undefined;
  /** Disable the SDK's five-minute idle unload so models stay resident. */
  keepWarm: boolean;
  /** Run one reranked query at startup so the first real request hits the warm path. */
  warmup: boolean;
  logLevel: string;
  /** Hard cap on `limit` for /search. */
  searchMaxLimit: number;
  /** When filters are present, fetch `limit * filterOverfetch` candidates before filtering. */
  filterOverfetch: number;
  /** Largest `/search` result window fetched from QMD even with over-fetch. */
  filterMaxFetch: number;
  /** Request body limit in bytes. `/documents` carries whole transcripts. */
  bodyLimitBytes: number;
  /** Quiet period after a document write before its index update runs. 0 disables the debounce. */
  reindexDebounceMs: number;
  /** Longest a debounced update can wait from its first write, so steady ingest cannot starve it. */
  reindexMaxWaitMs: number;
  /** How long shutdown waits for queued reindex jobs to finish. */
  shutdownDrainMs: number;
  /** Where model work runs: in this process, or on a worker. */
  llm: LlmConfig;
  /** The job queue's database. Undefined means beside the index. */
  queueDbPath: string | undefined;
}

export type LlmConfig =
  | { mode: "local" }
  | {
      mode: "remote";
      workers: PoolWorkerConfig[];
      /** Tokenize and embed queries on this host's CPU when no worker answers. */
      fallback: boolean;
      /** Expected embedding length; 0 pins it to the first response. */
      embedDimensions: number;
      /** How often workers are polled. */
      pollMs: number;
      /** Most text one embedding batch carries. */
      embedBatchBytes: number;
      /** Deadline for one search-time rerank call. */
      rerankTimeoutMs: number;
    };

const CAPABILITIES: readonly Capability[] = ["embed", "rerank", "generate"];

/** Checks one worker's address. Messages never quote the URL: it could hold a credential. */
function checkWorkerUrl(raw: string, setting: string): URL {
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    throw new Error(`${setting} is not a valid URL`);
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") throw new Error(`${setting} must be http or https`);
  if (url.username !== "" || url.password !== "") throw new Error(`${setting} must not contain credentials; use a token setting`);
  if (url.search !== "" || url.hash !== "") throw new Error(`${setting} must not contain a query or fragment`);
  return url;
}

function checkWorkerToken(token: string | undefined, url: URL, setting: string): void {
  if (token === undefined && !isLoopbackHost(url.hostname.replace(/^\[|\]$/g, ""))) {
    throw new Error(`${setting} is required when the worker is not on loopback`);
  }
  if (token !== undefined && token.length < 16) throw new Error(`${setting} must be at least 16 characters`);
}

/**
 * `MEMORIA_QMD_WORKERS` is a JSON list. It holds the name of an environment
 * variable for each token, never the token, so the list can be logged or committed.
 */
function loadWorkerList(env: NodeJS.ProcessEnv, raw: string): PoolWorkerConfig[] {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    throw new Error("MEMORIA_QMD_WORKERS is not valid JSON");
  }
  if (!Array.isArray(parsed) || parsed.length === 0) throw new Error("MEMORIA_QMD_WORKERS must be a non-empty list");
  const names = new Set<string>();
  return parsed.map((item: unknown, index) => workerFromEntry(env, item, `MEMORIA_QMD_WORKERS[${index}]`, names));
}

function workerFromEntry(env: NodeJS.ProcessEnv, item: unknown, at: string, names: Set<string>): PoolWorkerConfig {
  if (typeof item !== "object" || item === null) throw new Error(`${at} must be an object`);
  const w = item as Record<string, unknown>;
  if ("token" in w) throw new Error(`${at} must not hold a token; name an environment variable with tokenEnv`);
  const name = readWorkerName(w.name, at, names);
  if (typeof w.url !== "string") throw new Error(`${at}.url is required`);
  const url = checkWorkerUrl(w.url, `${at}.url`);
  const token = readTokenEnv(env, w.tokenEnv, at);
  checkWorkerToken(token, url, `${at}.tokenEnv`);
  return {
    name,
    url: w.url,
    token,
    capabilities: readCapabilities(w.capabilities, at),
    priority: readCount(w.priority, 100, `${at}.priority`, 0, 100000),
    maxConcurrent: readCount(w.maxConcurrent, 4, `${at}.maxConcurrent`, 1, 256),
  };
}

function readWorkerName(name: unknown, at: string, names: Set<string>): string {
  if (typeof name !== "string" || !/^[\w.-]{1,64}$/.test(name)) throw new Error(`${at}.name must be letters, digits, dots, dashes or underscores`);
  if (names.has(name)) throw new Error(`${at}.name repeats "${name}"`);
  names.add(name);
  return name;
}

function readTokenEnv(env: NodeJS.ProcessEnv, tokenEnv: unknown, at: string): string | undefined {
  if (tokenEnv === undefined) return undefined;
  if (typeof tokenEnv !== "string" || tokenEnv === "") throw new Error(`${at}.tokenEnv must name an environment variable`);
  // It would hand every caller's key to a worker: the sidecar's secret guards the whole corpus.
  if (tokenEnv === "MEMORIA_QMD_SECRET") throw new Error(`${at}.tokenEnv must not be the sidecar's own secret`);
  // The same two ways in as every other secret: `<name>_FILE` or `<name>`.
  const token = resolveSecret(env, tokenEnv);
  if (!token) throw new Error(`${at}.tokenEnv names ${tokenEnv}, which is not set`);
  return token;
}

function readCapabilities(value: unknown, at: string): Capability[] {
  const capabilities = value ?? CAPABILITIES;
  if (!Array.isArray(capabilities) || capabilities.length === 0 || !capabilities.every((c) => CAPABILITIES.includes(c as Capability))) {
    throw new Error(`${at}.capabilities must list some of ${CAPABILITIES.join(", ")}`);
  }
  return capabilities as Capability[];
}

function readCount(value: unknown, fallback: number, name: string, min: number, max: number): number {
  if (value === undefined) return fallback;
  if (typeof value !== "number" || !Number.isInteger(value) || value < min || value > max) throw new Error(`${name} must be an integer between ${min} and ${max}`);
  return value;
}

const LOOPBACK_HOSTS = new Set(["localhost", "::1", "0:0:0:0:0:0:0:1"]);

export function isLoopbackHost(host: string): boolean {
  const h = host.trim().toLowerCase();
  if (LOOPBACK_HOSTS.has(h)) return true;
  return /^127\.\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(h);
}

function readBool(value: string | undefined, fallback: boolean): boolean {
  if (value === undefined || value === "") return fallback;
  const v = value.trim().toLowerCase();
  if (["1", "true", "yes", "on"].includes(v)) return true;
  if (["0", "false", "no", "off"].includes(v)) return false;
  throw new Error(`Expected a boolean, got "${value}"`);
}

function readInt(value: string | undefined, fallback: number, name: string, min: number, max: number): number {
  if (value === undefined || value === "") return fallback;
  const n = Number(value);
  if (!Number.isInteger(n) || n < min || n > max) {
    throw new Error(`${name} must be an integer between ${min} and ${max}, got "${value}"`);
  }
  return n;
}

function loadWorkers(env: NodeJS.ProcessEnv): PoolWorkerConfig[] {
  const list = trimmed(env.MEMORIA_QMD_WORKERS);
  const raw = trimmed(env.MEMORIA_QMD_WORKER_URL);
  if (list && raw) throw new Error("Set MEMORIA_QMD_WORKERS or MEMORIA_QMD_WORKER_URL, not both");
  if (list) return loadWorkerList(env, list);
  if (!raw) throw new Error("MEMORIA_QMD_WORKERS or MEMORIA_QMD_WORKER_URL is required when MEMORIA_QMD_LLM=remote");
  const url = checkWorkerUrl(raw, "MEMORIA_QMD_WORKER_URL");
  const token = resolveSecret(env, "MEMORIA_QMD_WORKER_TOKEN");
  checkWorkerToken(token, url, "MEMORIA_QMD_WORKER_TOKEN");
  return [{ name: url.host, url: raw, token, capabilities: [...CAPABILITIES], priority: 100, maxConcurrent: 4 }];
}

/** Unset and blank mean the same. */
function trimmed(value: string | undefined): string | undefined {
  return value?.trim() || undefined;
}

/**
 * Remote settings are read only in remote mode: in local mode a stray worker
 * URL in the environment has no effect and cannot fail startup.
 */
function loadLlmConfig(env: NodeJS.ProcessEnv): LlmConfig {
  const mode = env.MEMORIA_QMD_LLM?.trim().toLowerCase() || "local";
  if (mode === "local") return { mode };
  if (mode !== "remote") throw new Error(`MEMORIA_QMD_LLM must be "local" or "remote", got "${mode}"`);

  return {
    mode,
    workers: loadWorkers(env),
    fallback: readBool(env.MEMORIA_QMD_LLM_FALLBACK, true),
    embedDimensions: readInt(env.MEMORIA_QMD_EMBED_DIMENSIONS, 768, "MEMORIA_QMD_EMBED_DIMENSIONS", 0, 65536),
    pollMs: readInt(env.MEMORIA_QMD_WORKER_POLL_MS, 10_000, "MEMORIA_QMD_WORKER_POLL_MS", 100, 3_600_000),
    embedBatchBytes: readInt(env.MEMORIA_QMD_EMBED_BATCH_BYTES, 256 * 1024, "MEMORIA_QMD_EMBED_BATCH_BYTES", 1024, 16 * 1024 * 1024),
    rerankTimeoutMs: readInt(env.MEMORIA_QMD_RERANK_TIMEOUT_MS, 20_000, "MEMORIA_QMD_RERANK_TIMEOUT_MS", 100, 110_000),
  };
}

function checkSecret(secret: string | undefined, host: string): void {
  if (secret === undefined && !isLoopbackHost(host)) {
    throw new Error(
      `MEMORIA_QMD_SECRET is required when binding to a non-loopback address (${host}). ` +
        "QMD has no auth of its own and the corpus contains credentials.",
    );
  }
  if (secret !== undefined && secret.length < 16) {
    throw new Error("MEMORIA_QMD_SECRET must be at least 16 characters");
  }
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): SidecarConfig {
  const host = (env.MEMORIA_QMD_HOST ?? "127.0.0.1").trim();
  if (host === "") throw new Error("MEMORIA_QMD_HOST must not be empty");
  const secret = resolveSecret(env, "MEMORIA_QMD_SECRET");
  checkSecret(secret, host);
  const reindexDebounceMs = readInt(env.MEMORIA_QMD_REINDEX_DEBOUNCE_MS, 60_000, "MEMORIA_QMD_REINDEX_DEBOUNCE_MS", 0, 3_600_000);
  const reindexMaxWaitMs = readInt(env.MEMORIA_QMD_REINDEX_MAX_WAIT_MS, 300_000, "MEMORIA_QMD_REINDEX_MAX_WAIT_MS", 0, 86_400_000);
  if (reindexMaxWaitMs < reindexDebounceMs) {
    throw new Error("MEMORIA_QMD_REINDEX_MAX_WAIT_MS must not be shorter than MEMORIA_QMD_REINDEX_DEBOUNCE_MS");
  }
  return {
    host,
    port: readInt(env.MEMORIA_QMD_PORT, 8282, "MEMORIA_QMD_PORT", 1, 65535),
    dbPath: trimmed(env.MEMORIA_QMD_DB_PATH),
    secret,
    keepWarm: readBool(env.MEMORIA_QMD_KEEP_WARM, true),
    warmup: readBool(env.MEMORIA_QMD_WARMUP, true),
    logLevel: trimmed(env.MEMORIA_QMD_LOG_LEVEL) ?? "info",
    searchMaxLimit: readInt(env.MEMORIA_QMD_SEARCH_MAX_LIMIT, 100, "MEMORIA_QMD_SEARCH_MAX_LIMIT", 1, 1000),
    filterOverfetch: readInt(env.MEMORIA_QMD_FILTER_OVERFETCH, 4, "MEMORIA_QMD_FILTER_OVERFETCH", 1, 20),
    filterMaxFetch: readInt(env.MEMORIA_QMD_FILTER_MAX_FETCH, 200, "MEMORIA_QMD_FILTER_MAX_FETCH", 1, 1000),
    bodyLimitBytes: readInt(env.MEMORIA_QMD_BODY_LIMIT_BYTES, 16 * 1024 * 1024, "MEMORIA_QMD_BODY_LIMIT_BYTES", 1024, 256 * 1024 * 1024),
    reindexDebounceMs,
    reindexMaxWaitMs,
    shutdownDrainMs: readInt(env.MEMORIA_QMD_SHUTDOWN_DRAIN_MS, 10_000, "MEMORIA_QMD_SHUTDOWN_DRAIN_MS", 0, 600_000),
    llm: loadLlmConfig(env),
    queueDbPath: trimmed(env.MEMORIA_QMD_QUEUE_DB_PATH),
  };
}
