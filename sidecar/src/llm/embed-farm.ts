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
 * **A document is set aside only when the worker is shown to be fine.** Failures are
 * judged when a run ends, by whether every worker accepts a known-good full-size chunk.
 * If one does not, it is too small or broken, so a refusal says nothing about the
 * document and the job waits. If all do, a refusal (400 or 413) is believed at once,
 * and a 5xx or timeout only after it has repeated across runs: see `REPEATED_RUNS`.
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
  /** Does every worker accept a known-good text as long as the longest real chunk? */
  acceptsFullChunk(): Promise<boolean>;
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
  /** Documents in the index, for judging how many failures are believable. */
  documentCount(): number;
  /** Remove vectors of documents no longer in the index. */
  removeOrphans(): void;
  /** Documents that failed lately. They go last, so they cannot keep starving the rest. */
  recentFailures: Map<string, RepeatedFailure>;
  embed(texts: string[]): Promise<{ vectors: number[][]; worker: string }>;
  sleep(ms: number): Promise<void>;
}

/** Runs that have counted against a document, and when the last one did. */
export interface RepeatedFailure {
  runs: number;
  lastAt: number;
}

export interface FarmOptions {
  force: boolean;
  collection: string | undefined;
  /** Set when resuming: when the job was accepted, as it skips what it already redid. */
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

/** At least this many failures, outnumbering all that has embedded, and it is the worker. */
const FEW_FAILURES = 5;

/**
 * A document failing with a 5xx or timeout is set aside only after this many runs, this
 * far apart, each ending with the worker accepting a full-size text. A brief window of
 * errors or a restart cannot span that, and a document that fails like this for half an
 * hour would otherwise fail every later job.
 */
const REPEATED_RUNS = 3;
const REPEATED_RUN_GAP_MS = 10 * 60_000;

const TOO_SMALL = "A worker refused a full-size test text, so its refusals of real documents say nothing about them, and none were set aside. Check the worker's context size and model. This job will retry.";
const TOO_MANY_REFUSED = "Workers refused too many documents, more than have ever embedded, so none were set aside. Check the worker's context size and model. This job will retry.";
const TOO_MANY_FAILED = "Workers keep failing on real documents while answering a short test text. The documents stay pending and this job will retry.";
const NO_WORKER = "No worker can embed right now; the documents stay pending and this job will retry.";
/**
 * A force job with nothing to resume redoes everything, with no clock to compare. The time
 * of day is not good enough: a document last written in the same millisecond, or under a
 * clock that has stepped back, read as already redone and kept its old vectors.
 */
const REDO_EVERYTHING = "9999-12-31T23:59:59.999Z";
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
  readonly #refused: Array<{ doc: DocState; reason: string }> = [];
  readonly #failed: Array<{ doc: DocState; reason: string }> = [];
  /** Documents embedded before this run, not counting what it is about to redo. */
  #embeddedBefore = 0;
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
    this.#embeddedBefore = Math.max(0, this.#deps.documentCount() - this.#deps.setAsideCount() - docs.length);
    const source = this.#batches(docs);
    const lanes = Array.from({ length: Math.max(1, this.#deps.capacity()) }, () => this.#lane(source));
    // Every lane stops before this returns, so nothing writes after the job has
    // ended: reindexing stays serialized even when a lane fails.
    const outcomes = await Promise.allSettled(lanes);
    await source.return(undefined);
    const failure = outcomes.find((o) => o.status === "rejected");
    if (failure) throw failure.reason;
    this.#throwIfStopped();
    await this.#settleFailures();
    const docsPending = docs.length - this.#docsWritten - this.#docsSkipped;
    return { docsProcessed: docs.length, chunksEmbedded: this.#chunksEmbedded, errors: this.#errors, durationMs: now() - started, docsPending, docsDeadLettered: this.#docsDeadLettered };
  }

  #throwIfStopped(): void {
    if (this.#options.signal.aborted) throw new JobCancelled();
    if (this.#busy) throw new JobDeferred(new IndexBusy().message);
    if (this.#outage) throw new JobDeferred(NO_WORKER);
    if (this.#fault !== null) throw new JobDeferred(this.#fault);
  }

  /** Only a run that finished can say what its failures mean. */
  async #settleFailures(): Promise<void> {
    if (this.#refused.length + this.#failed.length === 0) return;
    if (!(await this.#deps.acceptsFullChunk())) {
      if (this.#refused.length > 0) throw new JobDeferred(TOO_SMALL);
      return;
    }
    for (const { doc, reason } of this.#refused) this.#setAside(doc, reason);
    for (const { doc, reason } of this.#failed) {
      if (this.#countRun(doc.hash) >= REPEATED_RUNS) this.#setAside(doc, `Failed in ${REPEATED_RUNS} separate runs: ${reason}`);
    }
  }

  #setAside(doc: DocState, reason: string): void {
    this.#deps.deadLetter(doc.hash, doc.path, reason);
    this.#docsDeadLettered++;
  }

  #countRun(hash: string): number {
    const seen = this.#deps.recentFailures.get(hash)!;
    const now = (this.#options.now ?? Date.now)();
    if (Math.abs(now - seen.lastAt) >= REPEATED_RUN_GAP_MS) {
      seen.runs++;
      seen.lastAt = now;
    }
    return seen.runs;
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
      if (this.#options.signal.aborted) throw err;
      // Not the workers' doing, and it would be the same on any retry.
      const failure = err instanceof WorkerError ? err : new WorkerError("rejected", "chunk", "sidecar", "the document could not be chunked");
      if (await this.#workersAlive()) this.#noteFailed(state, failure);
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
    // Shutdown refused it: no worker is at fault, and no retry can go, so the job stops here and waits for the next start.
    if (err.code === "closing") {
      this.#outage = true;
      return;
    }
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
    this.#noteFailed(items[0]!.doc, err);
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

  /** Workers can embed, and this could not be. What that means is settled when the run ends. */
  #noteFailed(doc: DocState, err: WorkerError): void {
    if (doc.failed) return;
    this.#errors++;
    this.#failDoc(doc);
    if (!this.#deps.recentFailures.has(doc.hash)) this.#deps.recentFailures.set(doc.hash, { runs: 0, lastAt: 0 });
    const [list, fault] = err.code === "rejected" ? [this.#refused, TOO_MANY_REFUSED] : [this.#failed, TOO_MANY_FAILED];
    list.push({ doc, reason: err.message });
    if (list.length >= FEW_FAILURES && list.length > this.#docsWritten + this.#embeddedBefore) this.#fault = fault;
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
  const redoBefore = options.force ? (options.forceSince ?? REDO_EVERYTHING) : undefined;
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
