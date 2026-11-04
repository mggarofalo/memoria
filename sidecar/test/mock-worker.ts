import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";

/**
 * A scripted memoria-worker over real HTTP. Each route has a replaceable handler,
 * so tests can make it misbehave in specific ways. Its answers are made up, so
 * tests on it prove the adapter's handling, never scoring.
 */

export const WORKER_TOKEN = "mock-worker-token-0123456789";
export const EMBED_FILE = "embeddinggemma-300M-Q8_0.gguf";

export interface RecordedRequest {
  method: string;
  path: string;
  authorization: string | undefined;
  body: any;
}

export interface MockReply {
  status?: number;
  body: unknown;
}

export type MockHandler = (body: any, request: IncomingMessage) => MockReply | Promise<MockReply>;

/** A deterministic unit-ish vector; different texts get different directions. */
export function vectorFor(text: string, dimensions: number): number[] {
  let h = 7;
  for (const ch of text) h = (h * 31 + (ch.codePointAt(0) ?? 0)) % 9973;
  return Array.from({ length: dimensions }, (_, i) => ((h * (i + 3)) % 101) / 101 + 0.001);
}

export class MockWorker {
  readonly requests: RecordedRequest[] = [];
  readonly handlers: Map<string, MockHandler>;
  url = "";
  #server: Server | undefined;

  constructor(dimensions = 8, embedFile = EMBED_FILE) {
    this.handlers = new Map<string, MockHandler>([
      ["GET /capabilities", () => ({ body: { operations: ["embed", "rerank", "complete", "tokenize", "detokenize"], models: { embed: embedFile, rerank: "r.gguf", generate: "g.gguf" }, rerankTransport: "llama-server-qmd-tokens" } })],
      ["GET /health", () => ({ body: { status: "ok", queueDepth: 0, models: { embed: { loaded: true }, rerank: { loaded: true }, generate: { loaded: true } } } })],
      ["POST /v1/embed", (b) => ({ body: { model: embedFile, embeddings: (b.texts as string[]).map((t) => vectorFor(t, dimensions)) } })],
      ["POST /v1/rerank", (b) => ({ body: { results: (b.documents as string[]).map((_, index) => ({ index, score: 1 / (index + 2) })) } })],
      ["POST /v1/complete", (b) => {
        const query = String(b.prompt).replace("/no_think Expand this search query: ", "");
        return { body: { text: `lex: ${query}\nvec: about ${query}\nhyde: a passage on ${query}\n` } };
      }],
      ["POST /v1/tokenize", (b) => ({ body: { tokens: Array.from(String(b.text), (c) => c.codePointAt(0)) } })],
      ["POST /v1/detokenize", (b) => ({ body: { text: String.fromCodePoint(...(b.tokens as number[])) } })],
    ]);
  }

  async start(): Promise<this> {
    this.#server = createServer((request, response) => void this.#handle(request, response));
    await new Promise<void>((resolve) => this.#server!.listen(0, "127.0.0.1", resolve));
    this.url = `http://127.0.0.1:${(this.#server.address() as AddressInfo).port}`;
    return this;
  }

  async #handle(request: IncomingMessage, response: ServerResponse): Promise<void> {
    const chunks: Buffer[] = [];
    for await (const chunk of request) chunks.push(chunk as Buffer);
    const raw = Buffer.concat(chunks).toString("utf8");
    const body: unknown = raw === "" ? undefined : JSON.parse(raw);
    const path = (request.url ?? "/").split("?")[0]!;
    this.requests.push({ method: request.method ?? "", path, authorization: request.headers.authorization, body });
    const handler = this.handlers.get(`${request.method} ${path}`);
    const reply = handler ? await handler(body, request) : { status: 404, body: { error: "no_route" } };
    const text = typeof reply.body === "string" ? reply.body : JSON.stringify(reply.body);
    response.writeHead(reply.status ?? 200, { "content-type": "application/json" });
    response.end(text);
  }

  on(route: string, handler: MockHandler): void {
    this.handlers.set(route, handler);
  }

  count(path: string): number {
    return this.requests.filter((r) => r.path === path).length;
  }

  async stop(): Promise<void> {
    this.#server?.closeAllConnections();
    await new Promise((resolve) => (this.#server ? this.#server.close(resolve) : resolve(undefined)));
    this.#server = undefined;
  }
}
