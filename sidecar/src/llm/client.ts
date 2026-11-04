import type { LlmCallSink } from "./types.ts";

/**
 * HTTP to one worker. Two rules shape everything here:
 *
 * - The bearer token goes in one header and nowhere else. Error messages are
 *   built from fixed text and validated codes, never from a response body or a
 *   caught error's message, because they reach job records and `/ready`.
 * - A worker's response is untrusted, so its size is capped while reading.
 */

export type WorkerErrorCode =
  | "unavailable"
  | "timeout"
  | "unauthorized"
  | "rejected"
  | "invalid_response"
  | "model_mismatch";

export class WorkerError extends Error {
  readonly code: WorkerErrorCode;
  readonly operation: string;
  readonly worker: string;
  readonly status: number | undefined;

  constructor(code: WorkerErrorCode, operation: string, worker: string, detail: string, status?: number) {
    super(`LLM worker ${worker}: ${operation} failed (${code}): ${detail}`);
    this.name = "WorkerError";
    this.code = code;
    this.operation = operation;
    this.worker = worker;
    this.status = status;
  }
}

export interface OperationTimeouts {
  embed: number;
  rerank: number;
  complete: number;
  tokenize: number;
  status: number;
}

export const DEFAULT_TIMEOUTS: OperationTimeouts = { embed: 120_000, rerank: 120_000, complete: 60_000, tokenize: 15_000, status: 5_000 };

/** Larger than any legitimate response: 256 embeddings of 4096 numbers, as JSON. */
export const DEFAULT_MAX_RESPONSE_BYTES = 64 * 1024 * 1024;

export interface WorkerClientOptions {
  url: string;
  token: string | undefined;
  timeouts?: Partial<OperationTimeouts>;
  maxResponseBytes?: number;
  fetch?: typeof globalThis.fetch;
  onCall?: LlmCallSink;
}

const SAFE_CODE = /^[a-z][a-z0-9_]{0,63}$/;
const SAFE_SYSTEM_CODE = /^[A-Z][A-Z0-9_]{0,63}$/;

/** The stable identity used in logs and errors: host and port only. */
export function workerName(url: string): string {
  return new URL(url).host;
}

export class WorkerClient {
  readonly name: string;
  readonly #base: URL;
  readonly #token: string | undefined;
  readonly #timeouts: OperationTimeouts;
  readonly #maxBytes: number;
  readonly #fetch: typeof globalThis.fetch;
  readonly #onCall: LlmCallSink | undefined;

  constructor(options: WorkerClientOptions) {
    const base = new URL(options.url);
    if (base.protocol !== "http:" && base.protocol !== "https:") throw new Error("Worker URL must be http or https");
    if (base.username !== "" || base.password !== "") throw new Error("Worker URL must not contain credentials; use the token setting");
    if (base.search !== "" || base.hash !== "") throw new Error("Worker URL must not contain a query or fragment");
    this.#base = base;
    this.name = base.host;
    this.#token = options.token;
    this.#timeouts = { ...DEFAULT_TIMEOUTS, ...options.timeouts };
    this.#maxBytes = options.maxResponseBytes ?? DEFAULT_MAX_RESPONSE_BYTES;
    this.#fetch = options.fetch ?? globalThis.fetch;
    this.#onCall = options.onCall;
  }

  post(operation: string, timeout: keyof OperationTimeouts, path: string, body: unknown, batchSize: number): Promise<unknown> {
    return this.#request(operation, timeout, path, batchSize, { method: "POST", body: JSON.stringify(body) });
  }

  get(operation: string, path: string): Promise<unknown> {
    return this.#request(operation, "status", path, 0, { method: "GET" });
  }

  async #request(operation: string, timeout: keyof OperationTimeouts, path: string, batchSize: number, init: { method: string; body?: string }): Promise<unknown> {
    const started = Date.now();
    let status: number | undefined;
    try {
      const headers: Record<string, string> = { accept: "application/json" };
      if (init.body !== undefined) headers["content-type"] = "application/json";
      if (this.#token !== undefined) headers.authorization = `Bearer ${this.#token}`;
      let response: Response;
      try {
        response = await this.#fetch(new URL(path.replace(/^\//, ""), this.#withTrailingSlash()), {
          ...init,
          headers,
          // A redirect would resend the bearer token to wherever it points.
          redirect: "error",
          signal: AbortSignal.timeout(this.#timeouts[timeout]),
        });
      } catch (err) {
        throw this.#networkError(operation, err);
      }
      status = response.status;
      const text = await this.#readCapped(operation, response);
      if (!response.ok) throw this.#statusError(operation, response.status, text);
      try {
        const parsed: unknown = JSON.parse(text);
        this.#onCall?.({ worker: this.name, operation, batchSize, durationMs: Date.now() - started, ok: true, status });
        return parsed;
      } catch {
        throw new WorkerError("invalid_response", operation, this.name, "response is not JSON", status);
      }
    } catch (err) {
      const code = err instanceof WorkerError ? err.code : "unavailable";
      this.#onCall?.({ worker: this.name, operation, batchSize, durationMs: Date.now() - started, ok: false, ...(status !== undefined ? { status } : {}), errorCode: code });
      throw err;
    }
  }

  #withTrailingSlash(): URL {
    const base = new URL(this.#base);
    if (!base.pathname.endsWith("/")) base.pathname += "/";
    return base;
  }

  #networkError(operation: string, err: unknown): WorkerError {
    const name = (err as { name?: unknown }).name;
    if (name === "TimeoutError" || name === "AbortError") {
      return new WorkerError("timeout", operation, this.name, "no response before the deadline");
    }
    // undici puts the socket code on `cause`. Keep only that, and only if it
    // looks like a code.
    const cause = (err as { cause?: { code?: unknown } }).cause?.code;
    const detail = typeof cause === "string" && SAFE_SYSTEM_CODE.test(cause) ? cause : "connection failed";
    return new WorkerError("unavailable", operation, this.name, detail);
  }

  #statusError(operation: string, status: number, text: string): WorkerError {
    let remote = "";
    try {
      const code = (JSON.parse(text) as { error?: unknown }).error;
      if (typeof code === "string" && SAFE_CODE.test(code)) remote = `: ${code}`;
    } catch {
      // The body is not ours to repeat.
    }
    const detail = `HTTP ${status}${remote}`;
    if (status === 401 || status === 403) return new WorkerError("unauthorized", operation, this.name, detail, status);
    if (status >= 500 || status === 408 || status === 429) return new WorkerError("unavailable", operation, this.name, detail, status);
    return new WorkerError("rejected", operation, this.name, detail, status);
  }

  async #readCapped(operation: string, response: Response): Promise<string> {
    const tooLarge = () => new WorkerError("invalid_response", operation, this.name, "response exceeds the size limit", response.status);
    const declared = Number(response.headers.get("content-length") ?? "0");
    if (declared > this.#maxBytes) {
      await response.body?.cancel().catch(() => {});
      throw tooLarge();
    }
    if (!response.body) return "";
    const reader = response.body.getReader();
    const chunks: Uint8Array[] = [];
    let total = 0;
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        total += value.byteLength;
        if (total > this.#maxBytes) {
          await reader.cancel().catch(() => {});
          throw tooLarge();
        }
        chunks.push(value);
      }
    } catch (err) {
      if (err instanceof WorkerError) throw err;
      throw this.#networkError(operation, err);
    }
    return Buffer.concat(chunks).toString("utf8");
  }
}
