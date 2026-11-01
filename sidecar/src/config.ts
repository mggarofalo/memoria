/**
 * Process configuration. Everything comes from environment variables so the
 * same build runs under NSSM, in a shell, or in CI.
 */

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

export function loadConfig(env: NodeJS.ProcessEnv = process.env): SidecarConfig {
  const host = (env.MEMORIA_QMD_HOST ?? "127.0.0.1").trim();
  if (host === "") throw new Error("MEMORIA_QMD_HOST must not be empty");
  const secret = env.MEMORIA_QMD_SECRET?.trim() || undefined;
  if (secret === undefined && !isLoopbackHost(host)) {
    throw new Error(
      `MEMORIA_QMD_SECRET is required when binding to a non-loopback address (${host}). ` +
        "QMD has no auth of its own and the corpus contains credentials.",
    );
  }
  if (secret !== undefined && secret.length < 16) {
    throw new Error("MEMORIA_QMD_SECRET must be at least 16 characters");
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
  };
}
