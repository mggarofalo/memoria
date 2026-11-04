import { WorkerError } from "./client.ts";
import type { WorkerPool } from "./pool.ts";
import { parseRerankResults, ResponseViolation } from "./validate.ts";

/**
 * How a query and documents become relevance scores. This is the seam for the
 * main risk (docs/workers.md): a replacement implements this interface and
 * nothing else changes. `RemoteLlm` and the store do not know which is in use.
 */
export interface RerankTransport {
  /** Reported in logs and benchmark output. */
  readonly name: string;
  /** One score per text, in the order given. `RemoteLlm` validates the range. */
  scores(query: string, texts: string[]): Promise<number[]>;
}

/**
 * Scores through a worker's `POST /v1/rerank`. Which backend the worker uses is
 * its own business; `GET /capabilities` names it. A bad answer is an error inside
 * the dispatch, so it fails over to another worker.
 */
export class WorkerRerankTransport implements RerankTransport {
  readonly name = "worker-rerank";
  readonly #pool: WorkerPool;
  readonly #remainingMs: () => number | null;

  constructor(pool: WorkerPool, remainingMs: () => number | null = () => null) {
    this.#pool = pool;
    this.#remainingMs = remainingMs;
  }

  scores(query: string, texts: string[]): Promise<number[]> {
    return this.#pool.dispatch("rerank", "interactive", "rerank", async ({ client }) => {
      const raw = await client.post("rerank", "rerank", "/v1/rerank", { query, documents: texts }, texts.length, this.#remainingMs());
      try {
        return parseRerankResults(raw, texts.length);
      } catch (err) {
        if (err instanceof ResponseViolation) throw new WorkerError("invalid_response", "rerank", client.name, err.message);
        throw err;
      }
    });
  }
}
