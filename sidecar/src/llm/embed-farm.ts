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
 * **One bad chunk costs one document.** A failed batch is retried with a short backoff,
 * in case the failure was brief. Then a known-good text decides: if no worker can embed
 * that, it is an outage and the job waits. If one can, the batch is split until what
 * fails is alone.
 *
 * **Only a definite refusal sets a document aside.** That means a 400 or 413 for that
 * text. A 5xx or a timeout says nothing lasting about the document, so it stays pending.
 * The known-good text is short, so it passing proves little about real text: a worker
 * with too small a context refuses everything else. So refusals are only recorded when
 * the run ends, and if there are too many the worker is blamed, the job waits and
 * nothing is set aside. See `setAsideLimit`.
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

/** The index is held by someone else. A write that can wait for later. */
export class IndexBusy extends Error {
  constructor() {
    super("The index is busy; the documents stay pending and this job will retry.");
    this.name = "IndexBusy";
  }
}

export interface FarmDeps {
  /** Is some worker's document circuit open to calls? A quick check, and not proof. */
  canEmbed(): boolean;
  /** Can a worker embed a known-good text? Proof, at the cost of a call. */
  confirmWorkers(): Promise<boolean>;
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
  /** Replace all of one document's vectors, atomically. This and the other writes throw `IndexBusy`. */
  replaceDocument(hash: string, chunks: FarmChunk[], totalChunks: number): void;
  /** Set a document aside and drop any vectors it has, so it is not searched by a stale vector. */
  deadLetter(hash: string, path: string, reason: string): void;
  /** Documents set aside now, whose retry is not yet due. */
  setAsideCount(): number;
  /** Documents in the index, for judging how many refusals are believable. */
  documentCount(): number;
  /** Remove vectors of documents no longer in the index. */
  removeOrphans(): void;
  /** Documents that failed lately. They go last, so they cannot keep starving the rest. */
  recentFailures: Set<string>;
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
  /** Tries of a failed batch before it is examined. */
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
  path: string;
  total: number;
  chunks: FarmChunk[];
  failed: boolean;
}

/**
 * How many documents a worker may refuse before the worker is what looks wrong. The
 * chunker sizes chunks by tokens, so a well-configured worker should rarely refuse any.
 * A few is believable at any size. Past that, refusals must stay under a quarter of this
 * run, which catches a small job on a large index, and the total set aside must stay
 * under 1% of the index, which stops many small jobs adding up.
 */
const FEW_REFUSALS = 3;
const MAX_REFUSED_SHARE_OF_RUN = 0.25;
const MAX_SET_ASIDE_SHARE_OF_INDEX = 0.01;

export function setAsideLimit(runDocs: number, indexDocs: number, alreadySetAside: number): number {
  const byRun = Math.max(FEW_REFUSALS, Math.floor(runDocs * MAX_REFUSED_SHARE_OF_RUN));
  const byIndex = Math.max(FEW_REFUSALS, Math.floor(indexDocs * MAX_SET_ASIDE_SHARE_OF_INDEX)) - alreadySetAside;
  return Math.max(0, Math.min(byRun, byIndex));
}

/** More than a lone bad document, and failing more than succeeding: it is the worker. */
const FEW_TRANSIENT_FAILURES = 5;

const TOO_MANY_REFUSED = "Workers refused too many documents to believe the documents are at fault, so none were set aside. Check the worker's context size and model. This job will retry.";
const TOO_MANY_FAILED = "Workers keep failing on real documents while answering a short test text. The documents stay pending and this job will retry.";
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
  #docsDeadLettered = 0;
  #docsFailed = 0;
  readonly #refused: Array<{ doc: DocState; reason: string }> = [];
  #refusalLimit = 0;
  #fault: string | null = null;
  #outage = false;
  #busy = false;
  #reset = false;

  constructor(deps: FarmDeps, options: FarmOptions) {
    this.#deps = deps;
    this.#options = options;
    this.#retries = options.retries ?? DEFAULT_RETRIES;
    this.#retryBaseMs = options.retryBaseMs ?? DEFAULT_RETRY_BASE_MS;
  }

  get #stopping(): boolean {
    return this.#options.signal.aborted || this.#outage || this.#busy || this.#fault !== null;
  }

  async run(docs: PendingDoc[]): Promise<EmbedSummary> {
    const now = this.#options.now ?? Date.now;
    const started = now();
    this.#refusalLimit = setAsideLimit(docs.length, this.#deps.documentCount(), this.#deps.setAsideCount());
    const source = this.#batches(docs);
    const lanes = Array.from({ length: Math.max(1, this.#deps.capacity()) }, () => this.#lane(source));
    // Every lane stops before this returns, so nothing writes after the job has
    // ended: reindexing stays serialized even when a lane fails.
    const outcomes = await Promise.allSettled(lanes);
    await source.return(undefined);
    const failure = outcomes.find((o) => o.status === "rejected");
    if (failure) throw failure.reason;
    this.#throwIfStopped();
    this.#settleRefusals();
    const docsPending = docs.length - this.#docsWritten - this.#docsSkipped;
    return { docsProcessed: docs.length, chunksEmbedded: this.#chunksEmbedded, errors: this.#errors, durationMs: now() - started, docsPending, docsDeadLettered: this.#docsDeadLettered };
  }

  #throwIfStopped(): void {
    if (this.#options.signal.aborted) throw new JobCancelled();
    if (this.#busy) throw new JobDeferred(new IndexBusy().message);
    if (this.#outage) throw new JobDeferred(NO_WORKER);
    if (this.#fault !== null) throw new JobDeferred(this.#fault);
  }

  /** Only a run that finished can say how many refusals there really were. */
  #settleRefusals(): void {
    for (const { doc, reason } of this.#refused) {
      this.#deps.deadLetter(doc.hash, doc.path, reason);
      this.#docsDeadLettered++;
    }
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
    const state: DocState = { hash: doc.hash, path: doc.path, total: 0, chunks: [], failed: false };
    let chunks: Array<{ text: string; pos: number }>;
    try {
      chunks = await this.#deps.chunk(body, doc.path, this.#options.signal);
    } catch (err) {
      if (!(err instanceof WorkerError)) throw err;
      if (await this.#workersAlive()) this.#setAside(state, err);
      return [];
    }
    state.total = chunks.length;
    return chunks.map((chunk, seq) => ({ doc: state, seq, pos: chunk.pos, text: this.#deps.format(chunk.text, body, doc.path) }));
  }

  async #lane(source: AsyncGenerator<Item[]>): Promise<void> {
    while (!this.#stopping) {
      // Async generators queue concurrent next() calls, so lanes never share a batch.
      try {
        const { value: batch, done } = await source.next();
        if (done || this.#options.signal.aborted) return;
        await this.#embed(batch, this.#retries);
      } catch (err) {
        if (!(err instanceof IndexBusy)) throw err;
        this.#busy = true;
      }
    }
  }

  async #embed(items: Item[], triesLeft: number): Promise<void> {
    if (this.#stopping) return;
    try {
      const { vectors, worker } = await this.#deps.embed(items.map((item) => item.text));
      this.#options.onWorker(worker);
      this.#accept(items, vectors);
    } catch (err) {
      if (!(err instanceof WorkerError)) throw err;
      await this.#recover(items, triesLeft, err);
    }
  }

  /** Retry a brief failure. Past that, find out whether the batch or the worker is at fault. */
  async #recover(items: Item[], triesLeft: number, err: WorkerError): Promise<void> {
    if (triesLeft > 1 && err.code !== "rejected") {
      await this.#deps.sleep(this.#retryBaseMs * 2 ** (this.#retries - triesLeft));
      await this.#embed(items, triesLeft - 1);
      return;
    }
    if (!(await this.#workersAlive())) return;
    if (items.length > 1) {
      const middle = Math.ceil(items.length / 2);
      await this.#embedPart(items.slice(0, middle));
      await this.#embedPart(items.slice(middle));
      return;
    }
    this.#setAside(items[0]!.doc, err);
  }

  async #workersAlive(): Promise<boolean> {
    const alive = await this.#deps.confirmWorkers();
    if (!alive) this.#outage = true;
    return alive;
  }

  /** A lone chunk gets the brief-failure retries again, as one blip must not condemn a document. */
  #embedPart(items: Item[]): Promise<void> {
    return this.#embed(items, items.length === 1 ? this.#retries : 1);
  }

  /** Workers can embed, and this could not be. What that means depends on how it failed. */
  #setAside(doc: DocState, err: WorkerError): void {
    if (doc.failed) return;
    this.#errors++;
    this.#failDoc(doc);
    this.#deps.recentFailures.add(doc.hash);
    if (err.code === "rejected") this.#noteRefusal(doc, err);
    else this.#noteFailure();
  }

  #noteRefusal(doc: DocState, err: WorkerError): void {
    this.#refused.push({ doc, reason: err.message });
    if (this.#refused.length > this.#refusalLimit) this.#fault = TOO_MANY_REFUSED;
  }

  #noteFailure(): void {
    this.#docsFailed++;
    if (this.#docsFailed >= FEW_TRANSIENT_FAILURES && this.#docsFailed > this.#docsWritten) this.#fault = TOO_MANY_FAILED;
  }

  #failDoc(doc: DocState): void {
    doc.failed = true;
    doc.chunks = [];
  }

  #accept(items: Item[], vectors: number[][]): void {
    for (const [i, item] of items.entries()) {
      const { doc } = item;
      if (doc.failed) continue;
      doc.chunks.push({ seq: item.seq, pos: item.pos, vector: vectors[i]! });
      if (doc.chunks.length === doc.total) this.#write(doc);
    }
  }

  #write(doc: DocState): void {
    this.#checkFitsIndex(doc.chunks[0]!.vector.length);
    this.#deps.replaceDocument(doc.hash, doc.chunks, doc.total);
    this.#chunksEmbedded += doc.chunks.length;
    this.#docsWritten++;
    this.#deps.recentFailures.delete(doc.hash);
    doc.chunks = [];
    this.#options.onProgress?.();
  }

  /**
   * A new length can only go in by dropping every vector in the index. So only a force
   * job over everything may do it, which redoes all of them, and only now that a real
   * vector of that length is in hand. Any other job is refused outright: it would
   * silently cost every other collection its vectors, or never finish.
   */
  #checkFitsIndex(length: number): void {
    const existing = this.#deps.vectorDimensions();
    if (existing === null || existing === length || this.#reset) return;
    if (!this.#options.force || this.#options.collection !== undefined) {
      throw new Error(`Workers return vectors of length ${length} and the index holds ${existing}. Changing length drops every vector, so it takes a force embed with no collection. Nothing was changed.`);
    }
    this.#deps.resetVectors();
    this.#reset = true;
  }
}

async function embedPending(deps: FarmDeps, options: FarmOptions, docs: PendingDoc[]): Promise<EmbedSummary> {
  if (docs.length === 0) return { docsProcessed: 0, chunksEmbedded: 0, errors: 0, durationMs: 0, docsPending: 0 };
  // An open circuit may only mean the last job met a bad chunk, so ask before waiting.
  if (!deps.canEmbed() && !(await deps.confirmWorkers())) throw new JobDeferred(NO_WORKER);
  return new Farm(deps, options).run(docs);
}

export async function runEmbedFarm(deps: FarmDeps, options: FarmOptions): Promise<EmbedSummary> {
  const redoBefore = options.force ? (options.forceSince ?? new Date().toISOString()) : undefined;
  try {
    const pending = deps.pendingDocs(options.collection, redoBefore);
    // Before the work, so it holds whatever becomes of the run. The vectors of a deleted
    // document are not searched, but would come back to life if its content returned.
    if (options.force && options.collection === undefined) deps.removeOrphans();
    const lastFailed = (doc: PendingDoc): number => (deps.recentFailures.has(doc.hash) ? 1 : 0);
    return await embedPending(deps, options, pending.sort((a, b) => lastFailed(a) - lastFailed(b)));
  } catch (err) {
    if (err instanceof IndexBusy) throw new JobDeferred(err.message);
    throw err;
  }
}
