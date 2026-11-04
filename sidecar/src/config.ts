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
}

export type LlmConfig =
  | { mode: "local" }
  | {
      mode: "remote";
      workerUrl: string;
      workerToken: string | undefined;
      /** Tokenize and embed queries on this host's CPU when no worker answers. */
      fallback: boolean;
      /** Expected embedding length; 0 pins it to the first response. */
      embedDimensions: number;
    };

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

/**
 * Remote settings are read only in remote mode: in local mode a stray worker
 * URL in the environment has no effect and cannot fail startup.
 */
function loadLlmConfig(env: NodeJS.ProcessEnv): LlmConfig {
  const mode = env.MEMORIA_QMD_LLM?.trim().toLowerCase() || "local";
  if (mode === "local") return { mode };
  if (mode !== "remote") throw new Error(`MEMORIA_QMD_LLM must be "local" or "remote", got "${mode}"`);

  const raw = env.MEMORIA_QMD_WORKER_URL?.trim();
  if (!raw) throw new Error("MEMORIA_QMD_WORKER_URL is required when MEMORIA_QMD_LLM=remote");
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    // Not echoed: a malformed value could still hold a credential.
    throw new Error("MEMORIA_QMD_WORKER_URL is not a valid URL");
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") throw new Error("MEMORIA_QMD_WORKER_URL must be http or https");
  if (url.username !== "" || url.password !== "") throw new Error("MEMORIA_QMD_WORKER_URL must not contain credentials; use MEMORIA_QMD_WORKER_TOKEN");
  if (url.search !== "" || url.hash !== "") throw new Error("MEMORIA_QMD_WORKER_URL must not contain a query or fragment");

  const workerToken = resolveSecret(env, "MEMORIA_QMD_WORKER_TOKEN");
  if (workerToken === undefined && !isLoopbackHost(url.hostname.replace(/^\[|\]$/g, ""))) {
    throw new Error("MEMORIA_QMD_WORKER_TOKEN is required when the worker is not on loopback");
  }
  if (workerToken !== undefined && workerToken.length < 16) throw new Error("MEMORIA_QMD_WORKER_TOKEN must be at least 16 characters");
  return {
    mode,
    workerUrl: raw,
    workerToken,
    fallback: readBool(env.MEMORIA_QMD_LLM_FALLBACK, true),
    embedDimensions: readInt(env.MEMORIA_QMD_EMBED_DIMENSIONS, 768, "MEMORIA_QMD_EMBED_DIMENSIONS", 0, 65536),
  };
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): SidecarConfig {
  const host = (env.MEMORIA_QMD_HOST ?? "127.0.0.1").trim();
  if (host === "") throw new Error("MEMORIA_QMD_HOST must not be empty");
  const secret = resolveSecret(env, "MEMORIA_QMD_SECRET");
  if (secret === undefined && !isLoopbackHost(host)) {
    throw new Error(
      `MEMORIA_QMD_SECRET is required when binding to a non-loopback address (${host}). ` +
        "QMD has no auth of its own and the corpus contains credentials.",
    );
  }
  if (secret !== undefined && secret.length < 16) {
    throw new Error("MEMORIA_QMD_SECRET must be at least 16 characters");
  }
  const reindexDebounceMs = readInt(env.MEMORIA_QMD_REINDEX_DEBOUNCE_MS, 60_000, "MEMORIA_QMD_REINDEX_DEBOUNCE_MS", 0, 3_600_000);
  const reindexMaxWaitMs = readInt(env.MEMORIA_QMD_REINDEX_MAX_WAIT_MS, 300_000, "MEMORIA_QMD_REINDEX_MAX_WAIT_MS", 0, 86_400_000);
  if (reindexMaxWaitMs < reindexDebounceMs) {
    throw new Error("MEMORIA_QMD_REINDEX_MAX_WAIT_MS must not be shorter than MEMORIA_QMD_REINDEX_DEBOUNCE_MS");
  }
  return {
    host,
    port: readInt(env.MEMORIA_QMD_PORT, 8282, "MEMORIA_QMD_PORT", 1, 65535),
    dbPath: env.MEMORIA_QMD_DB_PATH?.trim() || undefined,
    secret,
    keepWarm: readBool(env.MEMORIA_QMD_KEEP_WARM, true),
    warmup: readBool(env.MEMORIA_QMD_WARMUP, true),
    logLevel: env.MEMORIA_QMD_LOG_LEVEL?.trim() || "info",
    searchMaxLimit: readInt(env.MEMORIA_QMD_SEARCH_MAX_LIMIT, 100, "MEMORIA_QMD_SEARCH_MAX_LIMIT", 1, 1000),
    filterOverfetch: readInt(env.MEMORIA_QMD_FILTER_OVERFETCH, 4, "MEMORIA_QMD_FILTER_OVERFETCH", 1, 20),
    filterMaxFetch: readInt(env.MEMORIA_QMD_FILTER_MAX_FETCH, 200, "MEMORIA_QMD_FILTER_MAX_FETCH", 1, 1000),
    bodyLimitBytes: readInt(env.MEMORIA_QMD_BODY_LIMIT_BYTES, 16 * 1024 * 1024, "MEMORIA_QMD_BODY_LIMIT_BYTES", 1024, 256 * 1024 * 1024),
    reindexDebounceMs,
    reindexMaxWaitMs,
    shutdownDrainMs: readInt(env.MEMORIA_QMD_SHUTDOWN_DRAIN_MS, 10_000, "MEMORIA_QMD_SHUTDOWN_DRAIN_MS", 0, 600_000),
    llm: loadLlmConfig(env),
  };
}
