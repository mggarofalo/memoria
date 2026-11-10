import { refuseAtBoundary } from "./boundary.ts";
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { tokenMatches } from "./auth.ts";
import type { Role } from "./config.ts";
import { InvalidRequest, UpstreamError, type CompleteRequest, type Inference } from "./inference.ts";
import type { Logger } from "./log.ts";
import { busyReasons, type BusyThresholds, type MachineState } from "./probes.ts";
import type { ServerStatus } from "./supervisor.ts";
import { resolveVersion } from "./version.ts";

/**
 * The worker's HTTP surface. Every route requires the bearer token when one is
 * configured. Error bodies are fixed codes and fixed text; a request's content
 * and upstream output never appear in them, or in the logs.
 */

export const WORKER_VERSION = resolveVersion();

/** Hard limits on one request, so a bad caller cannot queue unbounded model work. */
const MAX_EMBED_TEXTS = 256;
const MAX_TOKENIZE_TEXTS = 256;
const MAX_RERANK_DOCUMENTS = 1000;
const MAX_DETOKENIZE_TOKENS = 1_000_000;
/**
 * llama-server turns a recursive grammar away at once, but compiling a half-megabyte
 * one held its only generate slot for over 45 seconds. QMD's is about 100 bytes.
 */
const MAX_GRAMMAR_BYTES = 16 * 1024;

export interface WorkerServerOptions {
  token: string | undefined;
  /** DNS names a request may arrive under. An IP address is always fine. */
  allowedHosts?: ReadonlySet<string>;
  bodyLimitBytes: number;
  /** Bodies held in memory at once, across every request. Beyond it, requests get a 503. */
  maxBufferedBytes?: number;
  maxQueue: number;
  /** Concurrent requests per role; set to llama-server's slot count. */
  concurrency: Partial<Record<Role, number>>;
  inference: Inference;
  servers: () => ServerStatus[];
  machine: () => Promise<MachineState>;
  thresholds: BusyThresholds;
  log: Logger;
}

class HttpFailure extends Error {
  readonly status: number;
  readonly code: string;
  constructor(status: number, code: string, message: string) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

/** Limits concurrent work on one llama-server and counts who is waiting. */
export class Gate {
  readonly #limit: number;
  readonly #maxQueue: number;
  #active = 0;
  readonly #waiting: Array<() => void> = [];

  constructor(limit: number, maxQueue: number) {
    this.#limit = limit;
    this.#maxQueue = maxQueue;
  }

  get active(): number {
    return this.#active;
  }

  get queued(): number {
    return this.#waiting.length;
  }

  /** Refuses before a request's body is read, so a full queue costs no memory. */
  admit(): void {
    if (this.#active >= this.#limit && this.#waiting.length >= this.#maxQueue) throw new HttpFailure(503, "overloaded", "The queue for this operation is full");
  }

  async run<T>(work: () => Promise<T>): Promise<T> {
    if (this.#active >= this.#limit) {
      if (this.#waiting.length >= this.#maxQueue) throw new HttpFailure(503, "overloaded", "The queue for this operation is full");
      await new Promise<void>((resolve) => this.#waiting.push(resolve));
    } else {
      this.#active++;
    }
    try {
      return await work();
    } finally {
      const next = this.#waiting.shift();
      // The freed slot passes straight to the next caller, so `active` is unchanged.
      if (next) next();
      else this.#active--;
    }
  }
}

async function readJson(request: IncomingMessage, limit: number, reserve: (bytes: number) => void): Promise<Record<string, unknown>> {
  // A browser can send text/plain to any address with no preflight. It cannot send this.
  if (!/^application\/json\s*(;|$)/i.test(request.headers["content-type"] ?? "")) throw new HttpFailure(415, "unsupported_media_type", "The request body must be application/json");
  const chunks: Buffer[] = [];
  let total = 0;
  // Stopping early must not destroy the request: that resets the socket under the answer. `finish` decides what happens to the rest.
  for await (const chunk of request.iterator({ destroyOnReturn: false })) {
    total += (chunk as Buffer).length;
    if (total > limit) throw new HttpFailure(413, "payload_too_large", "The request body is too large");
    reserve((chunk as Buffer).length);
    chunks.push(chunk as Buffer);
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(Buffer.concat(chunks).toString("utf8"));
  } catch {
    throw new HttpFailure(400, "invalid_json", "The request body is not JSON");
  }
  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) throw new HttpFailure(400, "invalid_body", "The request body must be an object");
  return parsed as Record<string, unknown>;
}

function stringList(value: unknown, field: string, max: number): string[] {
  if (!Array.isArray(value) || value.length === 0 || value.length > max || !value.every((v) => typeof v === "string")) {
    throw new HttpFailure(400, "invalid_body", `${field} must be a list of 1 to ${max} strings`);
  }
  return value as string[];
}

function optionalNumber(body: Record<string, unknown>, field: string, min: number, max: number): number | undefined {
  const value = body[field];
  if (value === undefined) return undefined;
  if (typeof value !== "number" || !Number.isFinite(value) || value < min || value > max) throw new HttpFailure(400, "invalid_body", `${field} is out of range`);
  return value;
}

function optionalString(body: Record<string, unknown>, field: string): string | undefined {
  const value = body[field];
  if (value !== undefined && typeof value !== "string") throw new HttpFailure(400, "invalid_body", `${field} must be a string`);
  return value;
}

function parseComplete(body: Record<string, unknown>): CompleteRequest {
  if (typeof body.prompt !== "string" || body.prompt === "") throw new HttpFailure(400, "invalid_body", "prompt must be a non-empty string");
  const request: CompleteRequest = { prompt: body.prompt };
  const grammar = optionalString(body, "grammar");
  if (grammar !== undefined) {
    if (Buffer.byteLength(grammar) > MAX_GRAMMAR_BYTES) throw new HttpFailure(400, "grammar_too_large", "grammar is too large");
    request.grammar = grammar;
  }
  const system = optionalString(body, "system");
  if (system !== undefined) request.system = system;
  const numbers: Array<[keyof CompleteRequest & string, number, number]> = [
    ["maxTokens", 1, 8192],
    ["temperature", 0, 5],
    ["topK", 0, 1000],
    ["topP", 0, 1],
    ["minP", 0, 1],
    ["repeatLastN", -1, 8192],
    ["presencePenalty", -2, 2],
  ];
  for (const [field, min, max] of numbers) {
    const value = optionalNumber(body, field, min, max);
    if (value !== undefined) (request as unknown as Record<string, number>)[field] = value;
  }
  return request;
}

const OPERATIONS_BY_ROLE: Record<Role, string[]> = {
  embed: ["embed", "tokenize", "detokenize"],
  rerank: ["rerank"],
  generate: ["complete"],
};

const LOCALHOST_ONLY: ReadonlySet<string> = new Set(["localhost"]);

export function createWorkerServer(options: WorkerServerOptions): { server: Server; gates: Map<Role, Gate> } {
  const { inference, log } = options;
  const gates = new Map<Role, Gate>();
  for (const [role, limit] of Object.entries(options.concurrency) as Array<[Role, number]>) gates.set(role, new Gate(limit, options.maxQueue));

  const served = (role: Role): Gate => {
    const gate = gates.get(role);
    if (!gate) throw new HttpFailure(501, "not_served", `This worker does not serve ${role}`);
    if (!options.servers().find((s) => s.role === role)?.loaded) throw new HttpFailure(503, "model_not_ready", `The ${role} model is not ready`);
    gate.admit();
    return gate;
  };

  const health = async () => {
    const machine = await options.machine();
    const reasons = busyReasons(machine, options.thresholds);
    const statuses = options.servers();
    const models: Record<string, { file: string; state: string; loaded: boolean; restarts: number }> = {};
    for (const s of statuses) models[s.role] = { file: s.file, state: s.state, loaded: s.loaded, restarts: s.restarts };
    const allLoaded = statuses.every((s) => s.loaded);
    let inFlight = 0;
    let queueDepth = 0;
    for (const gate of gates.values()) {
      inFlight += gate.active;
      queueDepth += gate.queued;
    }
    return {
      status: !allLoaded ? ("starting" as const) : reasons.length > 0 ? ("busy" as const) : ("ok" as const),
      busyReasons: reasons,
      version: WORKER_VERSION,
      models,
      inFlight,
      queueDepth,
      vram: machine.vram,
      loadPerCore: machine.loadPerCore,
      cpuSpeedLimit: machine.cpuSpeedLimit,
    };
  };

  const capabilities = () => {
    const statuses = options.servers();
    const file = (role: Role) => statuses.find((s) => s.role === role)?.file ?? null;
    return {
      operations: statuses.flatMap((s) => OPERATIONS_BY_ROLE[s.role]),
      models: { embed: file("embed"), rerank: file("rerank"), generate: file("generate") },
      rerankTransport: inference.rerankBackend,
    };
  };

  let buffered = 0;
  const heldBy = new WeakMap<IncomingMessage, number>();
  const maxBuffered = options.maxBufferedBytes ?? 256 * 1024 * 1024;
  const read = (request: IncomingMessage) =>
    readJson(request, options.bodyLimitBytes, (bytes) => {
      if (buffered + bytes > maxBuffered) throw new HttpFailure(503, "overloaded", "Too much request data is waiting");
      buffered += bytes;
      heldBy.set(request, (heldBy.get(request) ?? 0) + bytes);
    });

  const operations: Record<string, (request: IncomingMessage) => Promise<unknown>> = {
    "/v1/embed": async (request) => {
      const gate = served("embed");
      const texts = stringList((await read(request)).texts, "texts", MAX_EMBED_TEXTS);
      const embeddings = await gate.run(() => inference.embed(texts));
      // Named on every response, so the sidecar can refuse vectors from the wrong model.
      return { embeddings, model: options.servers().find((s) => s.role === "embed")?.file ?? null };
    },
    "/v1/rerank": async (request) => {
      const gate = served("rerank");
      const body = await read(request);
      if (typeof body.query !== "string" || body.query === "") throw new HttpFailure(400, "invalid_body", "query must be a non-empty string");
      const query = body.query;
      const documents = stringList(body.documents, "documents", MAX_RERANK_DOCUMENTS);
      const scores = await gate.run(() => inference.rerank(query, documents));
      return { results: scores.map((score, index) => ({ index, score })), transport: inference.rerankBackend };
    },
    "/v1/complete": async (request) => {
      const gate = served("generate");
      const parsed = parseComplete(await read(request));
      return { text: await gate.run(() => inference.complete(parsed)) };
    },
    "/v1/tokenize": async (request) => {
      const gate = served("embed");
      const { text } = await read(request);
      if (typeof text !== "string") throw new HttpFailure(400, "invalid_body", "text must be a string");
      return { tokens: await gate.run(() => inference.tokenize(text)) };
    },
    "/v1/tokenize-batch": async (request) => {
      const gate = served("embed");
      const texts = stringList((await read(request)).texts, "texts", MAX_TOKENIZE_TEXTS);
      return { tokens: await gate.run(() => inference.tokenizeMany(texts)) };
    },
    "/v1/detokenize": async (request) => {
      const gate = served("embed");
      const { tokens } = await read(request);
      if (!Array.isArray(tokens) || tokens.length > MAX_DETOKENIZE_TOKENS || !tokens.every((t) => Number.isSafeInteger(t) && t >= 0)) {
        throw new HttpFailure(400, "invalid_body", "tokens must be a list of non-negative integers");
      }
      return { text: await gate.run(() => inference.detokenize(tokens as number[])) };
    },
  };

  // A Map, because the path comes from the client: `__proto__` must not find anything on an object.
  const operationsByPath = new Map(Object.entries(operations));

  const route = async (request: IncomingMessage): Promise<unknown> => {
    const path = (request.url ?? "/").split("?")[0];
    const method = request.method ?? "GET";
    if (method === "GET" && path === "/health") return health();
    if (method === "GET" && path === "/capabilities") return capabilities();
    const operation = method === "POST" ? operationsByPath.get(path ?? "") : undefined;
    if (typeof operation !== "function") throw new HttpFailure(404, "no_route", "No such route");
    return operation(request);
  };

  /**
   * A request turned away before its body was read. Closing the connection while the client is
   * still sending resets it, and the reset can reach the client before the answer, which it then
   * never reads. So the rest is read and dropped and the connection stays open, and only a client
   * still sending past the body limit is cut off.
   */
  const discardRest = (request: IncomingMessage): void => {
    let dropped = 0;
    request.on("data", (chunk: Buffer) => {
      dropped += chunk.length;
      if (dropped > options.bodyLimitBytes) request.socket.destroy();
    });
    request.resume();
  };

  const send = (response: ServerResponse, status: number, body: unknown): void => {
    const text = JSON.stringify(body);
    response.writeHead(status, { "content-type": "application/json", "content-length": Buffer.byteLength(text) });
    response.end(text);
  };

  const server = createServer((request, response) => {
    const started = Date.now();
    const path = (request.url ?? "/").split("?")[0];
    // Held until the answer has gone, which covers the whole wait in the queue.
    response.once("close", () => {
      buffered -= heldBy.get(request) ?? 0;
      heldBy.delete(request);
    });
    const finish = (status: number, body: unknown, errorCode?: string): void => {
      if (!request.complete) discardRest(request);
      send(response, status, body);
      log.info({ method: request.method, path, status, durationMs: Date.now() - started, ...(errorCode ? { errorCode } : {}) }, "request");
    };
    const refusal = refuseAtBoundary({ host: request.headers.host, origin: request.headers.origin }, options.allowedHosts ?? LOCALHOST_ONLY);
    if (refusal) {
      finish(refusal.status, { error: refusal.error, message: refusal.message }, refusal.error);
      return;
    }
    if (options.token !== undefined && !tokenMatches(request.headers.authorization, options.token)) {
      response.setHeader("www-authenticate", 'Bearer realm="memoria-worker"');
      finish(401, { error: "unauthorized", message: "Missing or invalid bearer token" }, "unauthorized");
      return;
    }
    route(request).then(
      (body) => finish(200, body),
      (err: unknown) => {
        if (err instanceof HttpFailure) finish(err.status, { error: err.code, message: err.message }, err.code);
        else if (err instanceof InvalidRequest) finish(400, { error: err.code, message: err.message }, err.code);
        else if (err instanceof UpstreamError) finish(err.code === "not_served" ? 501 : 502, { error: err.code, message: err.message }, err.code);
        else {
          log.error({ path, errorType: err instanceof Error ? err.name : "unknown" }, "unhandled error");
          finish(500, { error: "internal", message: "Internal error; see worker logs" }, "internal");
        }
      },
    );
  });
  return { server, gates };
}
