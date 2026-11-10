import { randomBytes } from "node:crypto";
import { readFileSync } from "node:fs";
import type { Capability, PoolWorkerConfig, WorkerAvailability } from "./llm/types.ts";

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
  /** Shared secret for `Authorization: Bearer`. There always is one: see `secretGenerated`. */
  secret: string;
  /** None was set, on loopback, so this run made its own. */
  secretGenerated: boolean;
  /** DNS names a request may arrive under. An IP address is always fine. */
  allowedHosts: ReadonlySet<string>;
  /** Largest document `POST /documents` takes. */
  maxDocumentBytes: number;
  /** Time a client has to finish sending its request headers. */
  headersTimeoutMs: number;
  /** Time a client has to finish sending a whole request. */
  requestTimeoutMs: number;
  /** Connections held at once. */
  maxConnections: number;
  /** Connections any one address may hold, so one peer cannot use up the total. */
  maxConnectionsPerSource: number;
  /** Memoria.Api's address, which gets more. */
  apiAddresses: ReadonlySet<string>;
  maxConnectionsPerApiAddress: number;
  /** Searches running at once. Each holds whole document bodies. */
  searchConcurrency: number;
  /** Searches that may wait for a turn. Past that, or `searchMaxWaitMs`, a 503. */
  searchQueue: number;
  searchMaxWaitMs: number;
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
      /** Most embedding batches one job runs at once. 0 means every free background slot. */
      embedConcurrency: number;
      /** Slots per worker that background work may not use. */
      interactiveReserve: number;
      /** Deadline for one search-time rerank call. */
      rerankTimeoutMs: number;
      /** Deadline for all of one search's model work. */
      searchDeadlineMs: number;
    };

const CAPABILITIES: readonly Capability[] = ["embed", "rerank", "generate"];

/** Checks one worker's address. Messages never quote the URL: it could hold a credential. */
function checkWorkerUrl(raw: string, setting: string, env: NodeJS.ProcessEnv): URL {
  let url: URL;
  try {
    url = new URL(raw);
  } catch {
    throw new Error(`${setting} is not a valid URL`);
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") throw new Error(`${setting} must be http or https`);
  if (url.username !== "" || url.password !== "") throw new Error(`${setting} must not contain credentials; use a token setting`);
  if (url.search !== "" || url.hash !== "") throw new Error(`${setting} must not contain a query or fragment`);
  if (url.protocol === "http:" && !isLoopbackHost(url.hostname.replace(/^\[|\]$/g, ""))) requirePlaintextOptIn(env, `${setting} is plain http to another machine`);
  return url;
}

function checkWorkerToken(token: string | undefined, url: URL, setting: string): void {
  if (token === undefined && !isLoopbackHost(url.hostname.replace(/^\[|\]$/g, ""))) {
    throw new Error(`${setting} is required when the worker is not on loopback`);
  }
  if (token !== undefined) checkSecretStrength(token, setting);
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
  return workerEntries(env, parsed, "MEMORIA_QMD_WORKERS");
}

function workerEntries(env: NodeJS.ProcessEnv, parsed: unknown, at: string): PoolWorkerConfig[] {
  if (!Array.isArray(parsed) || parsed.length === 0) throw new Error(`${at} must be a non-empty list`);
  const names = new Set<string>();
  return parsed.map((item: unknown, index) => workerFromEntry(env, item, `${at}[${index}]`, names));
}

/**
 * `MEMORIA_QMD_CONFIG_FILE`: the worker pool and the queue's tuning in one JSON file, in
 * place of hand-edited variables. Read once, at startup. Tokens never go in it: each
 * worker names the variable that holds its token, as in `MEMORIA_QMD_WORKERS`.
 */
interface ConfigFile {
  workers?: unknown;
  queue: Record<string, unknown>;
}

const CONFIG_KEYS = ["workers", "queue"] as const;
const QUEUE_KEYS = ["embedBatchBytes", "embedConcurrency", "interactiveReserve"] as const;

function readConfigFile(env: NodeJS.ProcessEnv): ConfigFile {
  const file = trimmed(env.MEMORIA_QMD_CONFIG_FILE);
  if (!file) return { queue: {} };
  let text: string;
  try {
    text = readFileSync(file, "utf8");
  } catch {
    throw new Error(`MEMORIA_QMD_CONFIG_FILE names ${file}, which cannot be read`);
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new Error("MEMORIA_QMD_CONFIG_FILE is not valid JSON");
  }
  const root = readObject(parsed, "The config file", CONFIG_KEYS);
  const queue = root.queue === undefined ? {} : readObject(root.queue, "queue", QUEUE_KEYS);
  return { ...(root.workers !== undefined ? { workers: root.workers } : {}), queue };
}

/** The variable, or the file's setting, but not both: two places for one number is how they drift. */
function batchBytes(env: NodeJS.ProcessEnv, queue: Record<string, unknown>): number {
  const name = "MEMORIA_QMD_EMBED_BATCH_BYTES";
  if (trimmed(env[name]) && queue.embedBatchBytes !== undefined) throw new Error(`Set ${name} or the config file's queue.embedBatchBytes, not both`);
  if (queue.embedBatchBytes !== undefined) return readCount(queue.embedBatchBytes, 0, "queue.embedBatchBytes", 1024, 16 * 1024 * 1024);
  return readInt(env[name], 256 * 1024, name, 1024, 16 * 1024 * 1024);
}

function workerFromEntry(env: NodeJS.ProcessEnv, item: unknown, at: string, names: Set<string>): PoolWorkerConfig {
  if (typeof item !== "object" || item === null) throw new Error(`${at} must be an object`);
  const w = item as Record<string, unknown>;
  if ("token" in w) throw new Error(`${at} must not hold a token; name an environment variable with tokenEnv`);
  const name = readWorkerName(w.name, at, names);
  if (typeof w.url !== "string") throw new Error(`${at}.url is required`);
  const url = checkWorkerUrl(w.url, `${at}.url`, env);
  const token = readTokenEnv(env, w.tokenEnv, at);
  checkWorkerToken(token, url, `${at}.tokenEnv`);
  const availability = readAvailability(w.availability, `${at}.availability`);
  return {
    name,
    url: w.url,
    token,
    capabilities: readCapabilities(w.capabilities, at),
    priority: readCount(w.priority, 100, `${at}.priority`, 0, 100000),
    maxConcurrent: readCount(w.maxConcurrent, 4, `${at}.maxConcurrent`, 1, 256),
    ...(availability ? { availability } : {}),
  };
}

/** An object holding only these keys, so a misspelt setting is an error rather than a default. */
function readObject(value: unknown, at: string, keys: readonly string[]): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new Error(`${at} must be an object`);
  const unknown = Object.keys(value).filter((k) => !keys.includes(k));
  if (unknown.length > 0) throw new Error(`${at} has unknown settings: ${unknown.join(", ")}. It takes ${keys.join(", ")}`);
  return value as Record<string, unknown>;
}

const AVAILABILITY_KEYS = ["failureThreshold", "backoffMs", "maxBackoffMs"] as const;

function readAvailability(value: unknown, at: string): Partial<WorkerAvailability> | undefined {
  if (value === undefined) return undefined;
  const a = readObject(value, at, AVAILABILITY_KEYS);
  const read: Partial<WorkerAvailability> = {};
  if (a.failureThreshold !== undefined) read.failureThreshold = readCount(a.failureThreshold, 3, `${at}.failureThreshold`, 1, 100);
  if (a.backoffMs !== undefined) read.backoffMs = readCount(a.backoffMs, 5_000, `${at}.backoffMs`, 100, 3_600_000);
  if (a.maxBackoffMs !== undefined) read.maxBackoffMs = readCount(a.maxBackoffMs, 60_000, `${at}.maxBackoffMs`, 100, 86_400_000);
  if ((read.maxBackoffMs ?? 60_000) < (read.backoffMs ?? 5_000)) throw new Error(`${at}.maxBackoffMs must not be shorter than backoffMs`);
  return read;
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
  // Naming the file variable would send its path as the token.
  if (tokenEnv.toUpperCase().endsWith("_FILE")) throw new Error(`${at}.tokenEnv must name the secret, not its file: leave off _FILE`);
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

/** Text from examples and templates, which someone may well have pasted in as it stood. */
const PLACEHOLDERS = ["a-long-random", "at-least-16", "changeme", "change-me", "replace-me", "replaceme", "placeholder", "your-secret", "yoursecret", "example", "password"];
const MIN_SECRET_LENGTH = 16;
// Of the secret as written. Folding case first, with 8, refused about 1 random 16-character secret in 40,000.
const MIN_DISTINCT_CHARACTERS = 6;
const KEYBOARD_ROWS = ["1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm"];
const MIN_RUN = 4;
const MAX_SHARE_IN_RUNS = 0.75;

function nextTo(a: string, b: string): boolean {
  if (Math.abs(a.charCodeAt(0) - b.charCodeAt(0)) === 1) return true;
  return KEYBOARD_ROWS.some((row) => row.includes(a + b) || row.includes(b + a));
}

/** How much of it is abcd, 4321 or qwer. A random secret has next to none of this. */
function shareInRuns(lower: string): number {
  let inRuns = 0;
  let run = 1;
  for (let i = 1; i <= lower.length; i++) {
    if (i < lower.length && nextTo(lower[i - 1]!, lower[i]!)) {
      run++;
      continue;
    }
    if (run >= MIN_RUN) inRuns += run;
    run = 1;
  }
  return inRuns / lower.length;
}

/** Only says what is wrong with a secret, never what the secret is. */
function checkSecretStrength(secret: string, setting: string): void {
  if (secret.length < MIN_SECRET_LENGTH) throw new Error(`${setting} must be at least ${MIN_SECRET_LENGTH} characters`);
  const lower = secret.toLowerCase();
  const weak = new Set(secret).size < MIN_DISTINCT_CHARACTERS || PLACEHOLDERS.some((p) => lower.includes(p)) || shareInRuns(lower) >= MAX_SHARE_IN_RUNS;
  if (weak) throw new Error(`${setting} is too weak: it looks like a placeholder or repeats itself. Generate one: node -e "console.log(require('crypto').randomBytes(32).toString('base64url'))"`);
}

/**
 * The services speak plain http, which is the intended setup on a WireGuard tunnel: the
 * tunnel does the encrypting. Nothing here can tell a tunnel from an open LAN, so whoever
 * deploys it has to say which this is. See docs/deployment.md.
 */
function requirePlaintextOptIn(env: NodeJS.ProcessEnv, what: string): void {
  if (readBool(env.MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK, false)) return;
  throw new Error(`${what}, so the secret and transcripts are in clear unless the network itself is encrypted. On a WireGuard tunnel, which is the intended setup, set MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK=1. See docs/deployment.md.`);
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

/**
 * The sidecar's secret guards the whole corpus, and a worker must never hold it. This
 * compares values, not names: an alias, a lower-case variable on Windows, and a file
 * all reach the same place.
 */
function rejectSecretReuse(workers: readonly PoolWorkerConfig[], secret: string | undefined): void {
  const reused = secret === undefined ? undefined : workers.find((w) => w.token === secret);
  if (reused) throw new Error(`The token for worker "${reused.name}" is the sidecar's own secret. Give each worker its own.`);
}

function loadWorkers(env: NodeJS.ProcessEnv, file: ConfigFile): PoolWorkerConfig[] {
  const list = trimmed(env.MEMORIA_QMD_WORKERS);
  const raw = trimmed(env.MEMORIA_QMD_WORKER_URL);
  const sources = [file.workers !== undefined, list !== undefined, raw !== undefined].filter(Boolean).length;
  if (sources > 1) throw new Error("Set the workers in one place: the config file's workers, MEMORIA_QMD_WORKERS or MEMORIA_QMD_WORKER_URL");
  if (file.workers !== undefined) return workerEntries(env, file.workers, "workers");
  if (list) return loadWorkerList(env, list);
  if (!raw) throw new Error("The config file's workers, MEMORIA_QMD_WORKERS or MEMORIA_QMD_WORKER_URL is required when MEMORIA_QMD_LLM=remote");
  const url = checkWorkerUrl(raw, "MEMORIA_QMD_WORKER_URL", env);
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
function loadLlmConfig(env: NodeJS.ProcessEnv, secret: string | undefined): LlmConfig {
  const mode = env.MEMORIA_QMD_LLM?.trim().toLowerCase() || "local";
  if (mode === "local") return { mode };
  if (mode !== "remote") throw new Error(`MEMORIA_QMD_LLM must be "local" or "remote", got "${mode}"`);

  const file = readConfigFile(env);
  const workers = loadWorkers(env, file);
  rejectSecretReuse(workers, secret);
  return {
    mode,
    workers,
    fallback: readBool(env.MEMORIA_QMD_LLM_FALLBACK, true),
    embedDimensions: readInt(env.MEMORIA_QMD_EMBED_DIMENSIONS, 768, "MEMORIA_QMD_EMBED_DIMENSIONS", 0, 65536),
    pollMs: readInt(env.MEMORIA_QMD_WORKER_POLL_MS, 10_000, "MEMORIA_QMD_WORKER_POLL_MS", 100, 3_600_000),
    embedBatchBytes: batchBytes(env, file.queue),
    embedConcurrency: readCount(file.queue.embedConcurrency, 0, "queue.embedConcurrency", 0, 1024),
    interactiveReserve: readCount(file.queue.interactiveReserve, 1, "queue.interactiveReserve", 0, 255),
    rerankTimeoutMs: readInt(env.MEMORIA_QMD_RERANK_TIMEOUT_MS, 20_000, "MEMORIA_QMD_RERANK_TIMEOUT_MS", 100, 110_000),
    searchDeadlineMs: readInt(env.MEMORIA_QMD_SEARCH_DEADLINE_MS, 60_000, "MEMORIA_QMD_SEARCH_DEADLINE_MS", 100, 110_000),
  };
}

function checkSecret(secret: string | undefined, host: string): void {
  if (secret === undefined && !isLoopbackHost(host)) {
    throw new Error(
      `MEMORIA_QMD_SECRET is required when binding to a non-loopback address (${host}). ` +
        "QMD has no auth of its own and the corpus contains credentials.",
    );
  }
  if (secret !== undefined) checkSecretStrength(secret, "MEMORIA_QMD_SECRET");
}

function checkBindAddress(host: string, env: NodeJS.ProcessEnv): void {
  if (!isLoopbackHost(host)) requirePlaintextOptIn(env, `The sidecar serves plain http, and MEMORIA_QMD_HOST (${host}) is not loopback`);
}

function readList(value: string | undefined): ReadonlySet<string> {
  return new Set((value ?? "").split(",").map((h) => h.trim().toLowerCase()).filter((h) => h !== ""));
}

function readAllowedHosts(env: NodeJS.ProcessEnv): ReadonlySet<string> {
  return new Set(["localhost", ...readList(env.MEMORIA_QMD_ALLOWED_HOSTS)]);
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): SidecarConfig {
  const host = (env.MEMORIA_QMD_HOST ?? "127.0.0.1").trim();
  if (host === "") throw new Error("MEMORIA_QMD_HOST must not be empty");
  const secret = resolveSecret(env, "MEMORIA_QMD_SECRET");
  checkSecret(secret, host);
  checkBindAddress(host, env);
  const reindexDebounceMs = readInt(env.MEMORIA_QMD_REINDEX_DEBOUNCE_MS, 60_000, "MEMORIA_QMD_REINDEX_DEBOUNCE_MS", 0, 3_600_000);
  const reindexMaxWaitMs = readInt(env.MEMORIA_QMD_REINDEX_MAX_WAIT_MS, 300_000, "MEMORIA_QMD_REINDEX_MAX_WAIT_MS", 0, 86_400_000);
  if (reindexMaxWaitMs < reindexDebounceMs) {
    throw new Error("MEMORIA_QMD_REINDEX_MAX_WAIT_MS must not be shorter than MEMORIA_QMD_REINDEX_DEBOUNCE_MS");
  }
  return {
    host,
    port: readInt(env.MEMORIA_QMD_PORT, 8282, "MEMORIA_QMD_PORT", 1, 65535),
    dbPath: trimmed(env.MEMORIA_QMD_DB_PATH),
    secret: secret ?? randomBytes(32).toString("base64url"),
    secretGenerated: secret === undefined,
    allowedHosts: readAllowedHosts(env),
    maxDocumentBytes: readInt(env.MEMORIA_QMD_MAX_DOCUMENT_BYTES, 2 * 1024 * 1024, "MEMORIA_QMD_MAX_DOCUMENT_BYTES", 1024, 256 * 1024 * 1024),
    headersTimeoutMs: readInt(env.MEMORIA_QMD_HEADERS_TIMEOUT_MS, 10_000, "MEMORIA_QMD_HEADERS_TIMEOUT_MS", 100, 120_000),
    requestTimeoutMs: readInt(env.MEMORIA_QMD_REQUEST_TIMEOUT_MS, 300_000, "MEMORIA_QMD_REQUEST_TIMEOUT_MS", 1_000, 3_600_000),
    maxConnections: readInt(env.MEMORIA_QMD_MAX_CONNECTIONS, 1024, "MEMORIA_QMD_MAX_CONNECTIONS", 1, 65_536),
    maxConnectionsPerSource: readInt(env.MEMORIA_QMD_MAX_CONNECTIONS_PER_SOURCE, 64, "MEMORIA_QMD_MAX_CONNECTIONS_PER_SOURCE", 1, 65_536),
    apiAddresses: readList(env.MEMORIA_QMD_API_ADDRESSES),
    maxConnectionsPerApiAddress: readInt(env.MEMORIA_QMD_MAX_CONNECTIONS_PER_API_ADDRESS, 512, "MEMORIA_QMD_MAX_CONNECTIONS_PER_API_ADDRESS", 1, 65_536),
    searchConcurrency: readInt(env.MEMORIA_QMD_SEARCH_CONCURRENCY, 4, "MEMORIA_QMD_SEARCH_CONCURRENCY", 1, 256),
    searchQueue: readInt(env.MEMORIA_QMD_SEARCH_QUEUE, 16, "MEMORIA_QMD_SEARCH_QUEUE", 0, 4096),
    searchMaxWaitMs: readInt(env.MEMORIA_QMD_SEARCH_MAX_WAIT_MS, 10_000, "MEMORIA_QMD_SEARCH_MAX_WAIT_MS", 1, 110_000),
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
    llm: loadLlmConfig(env, secret),
    queueDbPath: trimmed(env.MEMORIA_QMD_QUEUE_DB_PATH),
  };
}
