import { WorkerError, type WorkerClient } from "./client.ts";
import { parseRerankResults, ResponseViolation } from "./validate.ts";

/**
 * How a query and documents become relevance scores. This is the seam for the
 * main open risk (docs/workers.md): if llama-server's `/rerank` turns out not
 * to match in-process scoring, a replacement implements this interface and
 * nothing else changes. `RemoteLlm` and the store do not know which is in use.
 */
export interface RerankTransport {
  /** Reported in logs and benchmark output. */
  readonly name: string;
  /** One score per text, in the order given. `RemoteLlm` validates the range. */
  scores(query: string, texts: string[]): Promise<number[]>;
}

/**
 * Scores through the worker's `POST /v1/rerank`, which forwards to
 * llama-server's `/rerank`. The server applies the model's own rerank template.
 */
export class WorkerRerankTransport implements RerankTransport {
  readonly name = "llama-server-rerank";
  readonly #client: WorkerClient;

  constructor(client: WorkerClient) {
    this.#client = client;
  }

  async scores(query: string, texts: string[]): Promise<number[]> {
    const raw = await this.#client.post("rerank", "rerank", "/v1/rerank", { query, documents: texts }, texts.length);
    try {
      return parseRerankResults(raw, texts.length);
    } catch (err) {
      if (err instanceof ResponseViolation) throw new WorkerError("invalid_response", "rerank", this.#client.name, err.message);
      throw err;
    }
  }
}
