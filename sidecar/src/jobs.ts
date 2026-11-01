import { randomUUID } from "node:crypto";

/**
 * A single-worker queue for index maintenance. `qmd update` and `qmd embed`
 * contend over the model and the database, so everything that touches the
 * index runs here, one job at a time, in submission order.
 */
export type JobState = "queued" | "running" | "succeeded" | "failed";

export interface ReindexRequest {
  /** Collections to rescan; null means every collection. */
  collections: string[] | null;
  /** Generate embeddings after the scan. */
  embed: boolean;
  /** Re-embed everything, not just documents whose content changed. */
  force: boolean;
  /** Free-text origin for the audit trail (e.g. "POST /documents claude-sessions/x.md"). */
  reason: string;
}

export interface UpdateSummary {
  collections: number;
  indexed: number;
  updated: number;
  unchanged: number;
  removed: number;
  skipped: number;
  needsEmbedding: number;
}

export interface EmbedSummary {
  docsProcessed: number;
  chunksEmbedded: number;
  errors: number;
  durationMs: number;
}

export interface JobResult {
  update: UpdateSummary;
  embed: EmbedSummary | null;
  metadata: { documents: number; withMetadata: number };
}

export interface Job {
  id: string;
  state: JobState;
  request: ReindexRequest;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  result: JobResult | null;
  error: string | null;
}

export type JobRunner = (request: ReindexRequest) => Promise<JobResult>;

export class JobQueue {
  readonly #runner: JobRunner;
  readonly #keep: number;
  readonly #jobs: Job[] = [];
  #chain: Promise<void> = Promise.resolve();
  #pending = 0;

  constructor(runner: JobRunner, keep: number = 100) {
    this.#runner = runner;
    this.#keep = keep;
  }

  /**
   * Enqueue a request. An identical request already waiting in the queue is
   * returned instead of being duplicated, so a burst of ingests collapses
   * into one scan.
   */
  enqueue(request: ReindexRequest): Job {
    const duplicate = this.#jobs.find((j) => j.state === "queued" && sameTarget(j.request, request));
    if (duplicate) return duplicate;

    const job: Job = {
      id: randomUUID(),
      state: "queued",
      request,
      createdAt: new Date().toISOString(),
      startedAt: null,
      finishedAt: null,
      result: null,
      error: null,
    };
    this.#jobs.push(job);
    this.#pending++;
    this.#chain = this.#chain.then(() => this.#run(job));
    this.#trim();
    return job;
  }

  async #run(job: Job): Promise<void> {
    job.state = "running";
    job.startedAt = new Date().toISOString();
    try {
      job.result = await this.#runner(job.request);
      job.state = "succeeded";
    } catch (err) {
      job.state = "failed";
      job.error = err instanceof Error ? err.message : String(err);
    } finally {
      job.finishedAt = new Date().toISOString();
      this.#pending--;
    }
  }

  #trim(): void {
    // Drop the oldest finished jobs once the history exceeds the cap.
    while (this.#jobs.length > this.#keep) {
      const idx = this.#jobs.findIndex((j) => j.state === "succeeded" || j.state === "failed");
      if (idx < 0) break;
      this.#jobs.splice(idx, 1);
    }
  }

  get(id: string): Job | undefined {
    return this.#jobs.find((j) => j.id === id);
  }

  list(): Job[] {
    return [...this.#jobs].reverse();
  }

  get pending(): number {
    return this.#pending;
  }

  /** Resolves once every job enqueued so far has finished. */
  idle(): Promise<void> {
    return this.#chain;
  }
}

function sameTarget(a: ReindexRequest, b: ReindexRequest): boolean {
  const ac = a.collections ? [...a.collections].sort().join(",") : "*";
  const bc = b.collections ? [...b.collections].sort().join(",") : "*";
  return ac === bc && a.embed === b.embed && a.force === b.force;
}
