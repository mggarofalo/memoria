import { JobCancelled, JobDeferred, type EmbedSummary } from "../jobs.ts";
import { WorkerError } from "./client.ts";

/**
 * Embeds pending documents across the worker pool: chunks are grouped into
 * batches by byte budget, and as many batches run at once as workers have
 * background slots.
 *
 * **A document is written whole or not at all.** Its vectors build up in memory
 * and go to the index in one transaction, replacing whatever it had. So a kill
 * or an outage mid-job loses nothing, and that includes a `force` job: old vectors
 * stand until a new set is ready for that document. The index stays the record of
 * progress, so a re-run asks what is still pending and carries on.
 *
 * **One bad chunk costs one document.** A failed batch is retried with a short
 * backoff, in case the failure was brief, then split to isolate what the worker
 * refuses. Documents left over are reported in `docsPending`, and the job does not
 * end as a success.
 */

export interface PendingDoc {
  hash: string;
  path: string;
  bytes: number;
}

export interface FarmChunk {
  seq: number;
  pos: number;
  vector: number[];
}

export interface FarmDeps {
  /** Can any worker take embedding now? */
  canEmbed(): boolean;
  /** Batches to run at once. */
  capacity(): number;
  /** With `redoBefore`, also documents last embedded before that time: a force job's list. */
  pendingDocs(collection: string | undefined, redoBefore: string | undefined): PendingDoc[];
  readBody(hash: string): string;
  chunk(body: string, path: string, signal: AbortSignal): Promise<Array<{ text: string; pos: number }>>;
  /** QMD's document text, title included. */
  format(chunkText: string, body: string, path: string): string;
  /** Length of vectors in the index, or null if it has none. */
  vectorDimensions(): number | null;
  /** Drop every vector. Only ever called holding a real embedding of a new length. */
  resetVectors(): void;
  /** Replace all of one document's vectors, atomically. */
  replaceDocument(hash: string, chunks: FarmChunk[], totalChunks: number, workers: readonly string[]): void;
  embed(texts: string[]): Promise<{ vectors: number[][]; worker: string }>;
  sleep(ms: number): Promise<void>;
}

export interface FarmOptions {
  force: boolean;
  collection: string | undefined;
  /** When the job was first accepted. A resumed force job skips what it already redid. */
  forceSince?: string | undefined;
  signal: AbortSignal;
  onWorker: (name: string) => void;
  /** A document reached the index. */
  onProgress?: () => void;
  maxBatchBytes: number;
  maxBatchChunks: number;
  /** Tries of a failed batch before it is split. */
  retries?: number;
  retryBaseMs?: number;
  now?: () => number;
}

interface Item {
  doc: DocState;
  seq: number;
  pos: number;
  text: string;
}

interface DocState {
  hash: string;
  total: number;
  chunks: FarmChunk[];
  workers: Set<string>;
  failed: boolean;
}

const NO_WORKER = "No worker can embed right now; the documents stay pending and this job will retry.";
const DEFAULT_RETRIES = 3;
const DEFAULT_RETRY_BASE_MS = 500;

class Farm {
  readonly #deps: FarmDeps;
  readonly #options: FarmOptions;
  readonly #retries: number;
  readonly #retryBaseMs: number;
  #chunksEmbedded = 0;
  #errors = 0;
  #docsWritten = 0;
  #docsSkipped = 0;
  #outage = false;
  #reset = false;

  constructor(deps: FarmDeps, options: FarmOptions) {
    this.#deps = deps;
    this.#options = options;
    this.#retries = options.retries ?? DEFAULT_RETRIES;
    this.#retryBaseMs = options.retryBaseMs ?? DEFAULT_RETRY_BASE_MS;
  }

  get #stopping(): boolean {
    return this.#options.signal.aborted || this.#outage;
  }

  async run(docs: PendingDoc[]): Promise<EmbedSummary> {
    const now = this.#options.now ?? Date.now;
    const started = now();
    const source = this.#batches(docs);
    const lanes = Array.from({ length: Math.max(1, this.#deps.capacity()) }, () => this.#lane(source));
    // Every lane stops before this returns, so nothing writes after the job has
    // ended: reindexing stays serialized even when a lane fails.
    const outcomes = await Promise.allSettled(lanes);
    await source.return(undefined);
    const failure = outcomes.find((o) => o.status === "rejected");
    if (failure) throw failure.reason;
    if (this.#options.signal.aborted) throw new JobCancelled();
    if (this.#outage) throw new JobDeferred(NO_WORKER);
    return {
      docsProcessed: docs.length,
      chunksEmbedded: this.#chunksEmbedded,
      errors: this.#errors,
      durationMs: now() - started,
      docsPending: docs.length - this.#docsWritten - this.#docsSkipped,
    };
  }

  /** Chunking runs one document at a time and pauses whenever no lane wants a batch. */
  async *#batches(docs: PendingDoc[]): AsyncGenerator<Item[]> {
    let current: Item[] = [];
    let bytes = 0;
    for (const doc of docs) {
      if (this.#stopping) return;
      for (const item of await this.#itemsFor(doc)) {
        const size = Buffer.byteLength(item.text, "utf8");
        const full = bytes + size > this.#options.maxBatchBytes || current.length >= this.#options.maxBatchChunks;
        if (current.length > 0 && full) {
          yield current;
          current = [];
          bytes = 0;
        }
        current.push(item);
        bytes += size;
      }
    }
    if (current.length > 0) yield current;
  }

  async #itemsFor(doc: PendingDoc): Promise<Item[]> {
    const body = this.#deps.readBody(doc.hash);
    // As QMD does: an empty document has nothing to embed and is not a failure.
    if (!body.trim()) {
      this.#docsSkipped++;
      return [];
    }
    let chunks: Array<{ text: string; pos: number }>;
    try {
      chunks = await this.#deps.chunk(body, doc.path, this.#options.signal);
    } catch (err) {
      this.#noteFailure(err);
      this.#errors++;
      return [];
    }
    const state: DocState = { hash: doc.hash, total: chunks.length, chunks: [], workers: new Set(), failed: false };
    return chunks.map((chunk, seq) => ({ doc: state, seq, pos: chunk.pos, text: this.#deps.format(chunk.text, body, doc.path) }));
  }

  /** Only worker failures are survivable. Anything else is a bug or a broken disk. */
  #noteFailure(err: unknown): void {
    if (!(err instanceof WorkerError)) throw err;
    if (!this.#deps.canEmbed()) this.#outage = true;
  }

  async #lane(source: AsyncGenerator<Item[]>): Promise<void> {
    while (!this.#stopping) {
      // Async generators queue concurrent next() calls, so lanes never share a batch.
      const { value: batch, done } = await source.next();
      if (done) return;
      if (this.#options.signal.aborted) return;
      await this.#embed(batch, this.#retries);
    }
  }

  /** Retry a brief failure, then split, until what cannot be embedded is alone. */
  async #embed(items: Item[], triesLeft: number): Promise<void> {
    if (this.#stopping) return;
    try {
      const { vectors, worker } = await this.#deps.embed(items.map((item) => item.text));
      this.#options.onWorker(worker);
      this.#accept(items, vectors, worker);
    } catch (err) {
      this.#noteFailure(err);
      await this.#recover(items, triesLeft, err as WorkerError);
    }
  }

  async #recover(items: Item[], triesLeft: number, err: WorkerError): Promise<void> {
    if (this.#stopping) return;
    if (triesLeft > 1 && err.code !== "rejected") {
      await this.#deps.sleep(this.#retryBaseMs * 2 ** (this.#retries - triesLeft));
      await this.#embed(items, triesLeft - 1);
      return;
    }
    if (items.length > 1) {
      const middle = Math.ceil(items.length / 2);
      await this.#embed(items.slice(0, middle), 1);
      await this.#embed(items.slice(middle), 1);
      return;
    }
    this.#errors++;
    this.#failDoc(items[0]!.doc);
  }

  #failDoc(doc: DocState): void {
    doc.failed = true;
    doc.chunks = [];
  }

  #accept(items: Item[], vectors: number[][], worker: string): void {
    for (const [i, item] of items.entries()) {
      const { doc } = item;
      if (doc.failed) continue;
      doc.chunks.push({ seq: item.seq, pos: item.pos, vector: vectors[i]! });
      doc.workers.add(worker);
      if (doc.chunks.length === doc.total) this.#write(doc);
    }
  }

  #write(doc: DocState): void {
    if (!this.#fitsIndex(doc.chunks[0]!.vector.length)) {
      this.#errors++;
      this.#failDoc(doc);
      return;
    }
    this.#deps.replaceDocument(doc.hash, doc.chunks, doc.total, [...doc.workers]);
    this.#chunksEmbedded += doc.chunks.length;
    this.#docsWritten++;
    doc.chunks = [];
    this.#options.onProgress?.();
  }

  /**
   * A new length can only go in by dropping every old vector, which only a force
   * job may do, and only now that a real vector of that length is in hand.
   */
  #fitsIndex(length: number): boolean {
    const existing = this.#deps.vectorDimensions();
    if (existing === null || existing === length) return true;
    if (!this.#options.force) return false;
    if (!this.#reset) this.#deps.resetVectors();
    this.#reset = true;
    return true;
  }
}

export async function runEmbedFarm(deps: FarmDeps, options: FarmOptions): Promise<EmbedSummary> {
  const redoBefore = options.force ? (options.forceSince ?? new Date().toISOString()) : undefined;
  const docs = deps.pendingDocs(options.collection, redoBefore);
  if (docs.length === 0) return { docsProcessed: 0, chunksEmbedded: 0, errors: 0, durationMs: 0, docsPending: 0 };
  if (!deps.canEmbed()) throw new JobDeferred(NO_WORKER);
  return new Farm(deps, options).run(docs);
}
