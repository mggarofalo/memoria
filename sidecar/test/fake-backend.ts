import { readdir, readFile } from "node:fs/promises";
import path from "node:path";
import picomatch from "picomatch";
import type { EmbedSummary, UpdateSummary } from "../src/jobs.ts";
import { metadataEntries, type DocumentHead } from "../src/metadata.ts";
import { ScanStopped } from "../src/scan-batches.ts";
import type { DeadLetter, DegradedReason, LlmHealth, PoolLoad, WorkerView } from "../src/llm/types.ts";
import type {
  BackendEmbedOptions,
  BackendSearchOptions,
  SearchOutcome,
  CollectionRow,
  ContextRow,
  DocumentInfo,
  DocumentLookupError,
  DocumentRef,
  IndexStatusInfo,
  ModelStatus,
  MultiGetEntry,
  QmdBackend,
  ScanSummary,
  SearchHit,
} from "../src/store.ts";

/**
 * In-memory stand-in for the QMD SDK. Search is a naive case-insensitive
 * substring match over bodies, which is enough to exercise routing, filters,
 * snippets, ranges and error paths without models or SQLite.
 */
export interface FakeDoc {
  collection: string;
  path: string;
  body: string;
  title?: string;
  context?: string | null;
}

export interface FakeCollection {
  name: string;
  root: string;
  pattern?: string;
  ignore?: string[];
  context?: string;
  includeByDefault?: boolean;
}

export interface FakeBackendOptions {
  collections: FakeCollection[];
  docs: FakeDoc[];
  dbPath?: string;
  modelsResident?: boolean;
}

export interface RecordedCall {
  method: string;
  args: unknown[];
}

function docidFor(collection: string, path: string): string {
  let h = 0;
  const s = `${collection}/${path}`;
  for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0;
  return h.toString(16).padStart(6, "0").slice(-6);
}

async function markdownFiles(root: string): Promise<string[]> {
  let files: string[] = [];
  try {
    files = (await readdir(root, { recursive: true, withFileTypes: false })) as string[];
  } catch {
    files = [];
  }
  return files.map((f) => f.replace(/\\/g, "/")).filter((f) => f.endsWith(".md"));
}

export class FakeBackend implements QmdBackend {
  readonly dbPath: string;
  readonly calls: RecordedCall[] = [];
  readonly collections: FakeCollection[];
  docs: FakeDoc[];
  modelsResident: boolean;
  /** When set, search() throws this to exercise the 500 path. */
  searchError: Error | null = null;
  updateDelayMs = 0;
  /** When set, update() waits on it, so a test can act while a job is mid-run. */
  updateGate: Promise<void> | null = null;
  /** Off, update() returns no metadata, as a backend that does not read it on the scan thread. */
  scanReadsMetadata = true;
  /** Replaces embed's default answer, so a test can defer, fail or hold a job. */
  embedImpl: ((options: BackendEmbedOptions) => Promise<EmbedSummary>) | null = null;
  degradedReasons: DegradedReason[] = [];
  health: LlmHealth = { mode: "local", degraded: false, unavailable: [], workersUp: 0, workersTotal: 0, embedDisagreement: [], documentsSetAside: 0 };
  workerViews: WorkerView[] = [];
  load: PoolLoad | null = null;
  deadLetterList: DeadLetter[] = [];
  closed = false;
  /** Docs a scan found on disk. Only these can vanish; seeded docs have no file. */
  readonly #scanned = new Set<FakeDoc>();

  constructor(options: FakeBackendOptions) {
    this.collections = options.collections;
    this.docs = [...options.docs];
    this.dbPath = options.dbPath ?? "/fake/index.sqlite";
    this.modelsResident = options.modelsResident ?? false;
  }

  #record(method: string, ...args: unknown[]): void {
    this.calls.push({ method, args });
  }

  #info(d: FakeDoc): DocumentInfo {
    const displayPath = `${d.collection}/${d.path}`;
    return {
      filepath: `qmd://${displayPath}`,
      displayPath,
      title: d.title ?? d.path,
      context: d.context ?? this.collections.find((c) => c.name === d.collection)?.context ?? null,
      hash: docidFor(d.collection, d.path).repeat(4),
      docid: docidFor(d.collection, d.path),
      collectionName: d.collection,
      modifiedAt: "2026-01-01T00:00:00.000Z",
      bodyLength: Buffer.byteLength(d.body, "utf8"),
    };
  }

  #find(reference: string): FakeDoc | undefined {
    let ref = reference.trim();
    if (ref.startsWith("#")) ref = ref.slice(1);
    const byId = this.docs.find((d) => docidFor(d.collection, d.path) === ref);
    if (byId) return byId;
    const stripped = ref.replace(/^qmd:\/\//, "");
    return this.docs.find((d) => `${d.collection}/${d.path}` === stripped) ?? this.docs.find((d) => d.path === stripped);
  }

  async search(options: BackendSearchOptions): Promise<SearchHit[]> {
    this.#record("search", options);
    if (this.searchError) throw this.searchError;
    const terms = [options.query ?? "", ...(options.queries ?? []).map((q) => q.query)].filter(Boolean).map((t) => t.toLowerCase());
    const scoped = options.collections ? this.docs.filter((d) => options.collections!.includes(d.collection)) : this.docs;
    const hits: SearchHit[] = [];
    for (const d of scoped) {
      const lower = d.body.toLowerCase();
      const matches = terms.filter((t) => lower.includes(t)).length;
      if (matches === 0) continue;
      const score = Math.min(1, matches / terms.length);
      if (score < options.minScore) continue;
      const firstTerm = terms.find((t) => lower.includes(t))!;
      const pos = lower.indexOf(firstTerm);
      const info = this.#info(d);
      hits.push({
        file: info.filepath,
        displayPath: info.displayPath,
        title: info.title,
        body: d.body,
        bestChunk: d.body.slice(pos, pos + 80),
        bestChunkPos: pos,
        score,
        context: info.context,
        docid: info.docid,
      });
    }
    hits.sort((a, b) => b.score - a.score || a.displayPath.localeCompare(b.displayPath));
    return hits.slice(0, options.limit);
  }

  async get(reference: string): Promise<DocumentInfo | DocumentLookupError> {
    this.#record("get", reference);
    const d = this.#find(reference);
    if (!d) {
      const similar = this.docs.filter((x) => x.path.includes(reference.slice(0, 4))).map((x) => `${x.collection}/${x.path}`);
      return { error: "not_found", query: reference, similarFiles: similar };
    }
    if (d.path.includes("ignored")) {
      return { error: "excluded_by_ignore", query: reference, collection: d.collection, path: d.path, rule: "**/ignored*" };
    }
    return this.#info(d);
  }

  async getDocumentBody(filepath: string, options: { fromLine?: number; maxLines?: number }): Promise<string | null> {
    this.#record("getDocumentBody", filepath, options);
    const d = this.#find(filepath);
    if (!d) return null;
    const lines = d.body.split("\n");
    const start = Math.max(1, options.fromLine ?? 1) - 1;
    const end = options.maxLines !== undefined ? start + options.maxLines : lines.length;
    return lines.slice(start, end).join("\n");
  }

  /**
   * Mirrors the SDK's findDocuments, quirks included: the string is a reference
   * list only when it has a comma and no `*`, `?` or `{`, or is a lone docid.
   * Anything else is one glob, commas and all, and the SDK does not de-duplicate.
   */
  async multiGet(pattern: string, options: { includeBody: boolean; maxBytes: number }): Promise<{ docs: MultiGetEntry[]; errors: string[] }> {
    this.#record("multiGet", pattern, options);
    const isList = pattern.includes(",") && !/[*?{]/.test(pattern);
    const isDocid = /^#?[a-f0-9]{6,}$/i.test(pattern.trim());
    const errors: string[] = [];
    const matched: FakeDoc[] = [];
    if (isList || isDocid) {
      for (const name of pattern.split(",").map((p) => p.trim()).filter(Boolean)) {
        const one = this.#find(name);
        if (one) matched.push(one);
        else errors.push(`File not found: ${name}`);
      }
    } else {
      const isMatch = picomatch(pattern);
      matched.push(...this.docs.filter((d) => isMatch(`qmd://${d.collection}/${d.path}`) || isMatch(d.path) || isMatch(`${d.collection}/${d.path}`)));
      if (matched.length === 0) return { docs: [], errors: [`No files matched pattern: ${pattern}`] };
    }
    const out: MultiGetEntry[] = [];
    for (const d of matched) {
      const info = this.#info(d);
      if (info.bodyLength > options.maxBytes) {
        out.push({ doc: { filepath: info.filepath, displayPath: info.displayPath }, skipped: true, skipReason: `File too large (${info.bodyLength} bytes > ${options.maxBytes})` });
        continue;
      }
      out.push({ doc: options.includeBody ? { ...info, body: d.body } : info, skipped: false });
    }
    return { docs: out, errors };
  }

  async listDocumentRefs(collections?: readonly string[]): Promise<DocumentRef[]> {
    this.#record("listDocumentRefs", collections);
    return this.docs
      .filter((d) => !collections || collections.includes(d.collection))
      .map((d) => ({ collection: d.collection, path: d.path, hash: this.#info(d).hash }));
  }

  ignorePatterns(collection: string): string[] {
    return this.collections.find((c) => c.name === collection)?.ignore ?? [];
  }

  async listCollections(): Promise<CollectionRow[]> {
    this.#record("listCollections");
    return this.collections.map((c) => {
      const docs = this.docs.filter((d) => d.collection === c.name);
      return {
        name: c.name,
        pwd: c.root,
        glob_pattern: c.pattern ?? "**/*.md",
        doc_count: docs.length,
        active_count: docs.length,
        last_modified: docs.length > 0 ? "2026-01-01T00:00:00.000Z" : null,
        includeByDefault: c.includeByDefault ?? true,
      };
    });
  }

  async listContexts(): Promise<ContextRow[]> {
    this.#record("listContexts");
    return this.collections.filter((c) => c.context).map((c) => ({ collection: c.name, path: "", context: c.context! }));
  }

  async getDefaultCollectionNames(): Promise<string[]> {
    return this.collections.filter((c) => c.includeByDefault ?? true).map((c) => c.name);
  }

  /**
   * Like `qmd update`: scan each collection root for files matching the
   * pattern and fold them into the in-memory docs, so ingest-by-value
   * round-trips through the filesystem the way it does in production. A doc
   * whose file is gone is dropped, as the SDK deactivates it.
   */
  async update(options: { collections?: string[]; signal?: AbortSignal; headBytes?: number }): Promise<ScanSummary> {
    this.#record("update", options.collections ? { collections: options.collections } : {});
    if (this.updateDelayMs > 0) await new Promise((r) => setTimeout(r, this.updateDelayMs));
    if (this.updateGate) await this.updateGate;
    // As the real scan does: an abort stops it before the next document.
    if (options.signal?.aborted) throw new ScanStopped();
    const summary = await this.#scan(options.collections);
    // As the real scan's thread does, the metadata after it, unless a test wants the old way.
    if (options.headBytes === undefined || !this.scanReadsMetadata) return summary;
    return { ...summary, metadata: metadataEntries(this.docs.map((d) => ({ collection: d.collection, path: d.path, head: d.body.slice(0, options.headBytes) }))) };
  }

  async #scan(collections: string[] | undefined): Promise<UpdateSummary> {
    const options = { collections };
    const names = options.collections ?? this.collections.map((c) => c.name);
    const counts = { indexed: 0, updated: 0, unchanged: 0, removed: 0 };
    for (const name of names) {
      const col = this.collections.find((c) => c.name === name);
      if (!col) continue;
      const onDisk = await markdownFiles(col.root);
      for (const rel of onDisk) await this.#scanFile(name, rel, await readFile(path.join(col.root, rel), "utf8"), counts);
      for (const doc of [...this.#scanned]) {
        if (doc.collection === name && !onDisk.includes(doc.path)) {
          this.#scanned.delete(doc);
          this.docs.splice(this.docs.indexOf(doc), 1);
          counts.removed++;
        }
      }
    }
    return { collections: names.length, ...counts, skipped: 0, needsEmbedding: counts.indexed + counts.updated };
  }

  async #scanFile(collection: string, rel: string, body: string, counts: { indexed: number; updated: number; unchanged: number }): Promise<void> {
    const existing = this.docs.find((d) => d.collection === collection && d.path === rel);
    if (!existing) {
      const doc: FakeDoc = { collection, path: rel, body };
      this.docs.push(doc);
      this.#scanned.add(doc);
      counts.indexed++;
      return;
    }
    this.#scanned.add(existing);
    if (existing.body === body) {
      counts.unchanged++;
      return;
    }
    existing.body = body;
    counts.updated++;
  }

  async embed(options: BackendEmbedOptions): Promise<EmbedSummary> {
    this.#record("embed", { force: options.force, ...(options.collection !== undefined ? { collection: options.collection } : {}) });
    if (this.embedImpl) return this.embedImpl(options);
    return { docsProcessed: 0, chunksEmbedded: 0, errors: 0, durationMs: 1 };
  }

  async searchDetailed(options: BackendSearchOptions): Promise<SearchOutcome> {
    return { hits: await this.search(options), degradedReasons: [...this.degradedReasons] };
  }

  llmHealth(): LlmHealth {
    return this.health;
  }

  workers(): WorkerView[] {
    return this.workerViews;
  }

  poolLoad(): PoolLoad | null {
    return this.load;
  }

  drainWorker(name: string): WorkerView | null {
    return this.#setDrained(name, true);
  }

  enableWorker(name: string): WorkerView | null {
    return this.#setDrained(name, false);
  }

  #setDrained(name: string, drained: boolean): WorkerView | null {
    const view = this.workerViews.find((w) => w.name === name);
    if (!view) return null;
    this.#record(drained ? "drainWorker" : "enableWorker", { name });
    view.drained = drained;
    return view;
  }

  purgeCache(): number {
    this.#record("purgeCache");
    return 3;
  }

  deadLetters(): DeadLetter[] {
    return this.deadLetterList;
  }

  clearDeadLetters(): number {
    const cleared = this.deadLetterList.length;
    this.deadLetterList = [];
    return cleared;
  }

  async getStatus(): Promise<IndexStatusInfo> {
    this.#record("getStatus");
    return {
      totalDocuments: this.docs.length,
      needsEmbedding: 0,
      documentsSetAside: 0,
      hasVectorIndex: true,
      collections: this.collections.map((c) => ({
        name: c.name,
        path: c.root,
        pattern: c.pattern ?? "**/*.md",
        documents: this.docs.filter((d) => d.collection === c.name).length,
        lastUpdated: "2026-01-01T00:00:00.000Z",
      })),
    };
  }

  readDocumentHeads(maxBytes: number): DocumentHead[] {
    this.#record("readDocumentHeads", maxBytes);
    return this.docs.map((d) => ({ collection: d.collection, path: d.path, head: d.body.slice(0, maxBytes) }));
  }

  snippet(body: string, query: string, maxLen: number, chunkPos: number): { line: number; snippet: string } {
    const pos = Math.max(0, Math.min(chunkPos, body.length));
    const line = body.slice(0, pos).split("\n").length;
    const lineStart = body.lastIndexOf("\n", pos - 1) + 1;
    return { line, snippet: body.slice(lineStart, lineStart + maxLen) };
  }

  addLineNumbers(text: string, startLine: number): string {
    return text
      .split("\n")
      .map((l, i) => `${startLine + i}: ${l}`)
      .join("\n");
  }

  models(): ModelStatus {
    const r = this.modelsResident;
    return { embed: "fake-embed", rerank: "fake-rerank", generate: "fake-generate", loaded: { embed: r, rerank: r, generate: r }, resident: r };
  }

  workerCalls: "open" | "refused" | "aborted" = "open";

  stopWorkerCalls(): void {
    if (this.workerCalls === "open") this.workerCalls = "refused";
  }

  abortWorkerCalls(): void {
    this.workerCalls = "aborted";
  }

  async close(): Promise<void> {
    this.closed = true;
  }
}

export const FRONTMATTER_DOC = (fields: Record<string, string | string[]>, body: string): string => {
  const lines = ["---"];
  for (const [k, v] of Object.entries(fields)) {
    if (Array.isArray(v)) {
      lines.push(`${k}:`);
      for (const item of v) lines.push(`  - "${item}"`);
    } else {
      lines.push(`${k}: "${v}"`);
    }
  }
  lines.push("---", "", body);
  return lines.join("\n");
};
