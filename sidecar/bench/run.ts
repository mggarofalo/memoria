import type { Queryable, RerankDocument, RerankResult } from "../src/llm/types.ts";
import { cosine, expansionKey, jaccard, meanPairwiseJaccard, scoreDelta, spearman, summarize, topKOverlap, type ScoreDelta, type Summary } from "./metrics.ts";

/**
 * The comparison itself, separated from opening indexes so it can be tested with
 * scripted sides. Each measurement isolates one thing:
 *
 * - **Rerank:** the same candidate chunks go to both sides, bypassing the cache.
 *   This is the direct test of the main risk.
 * - **Embedding:** the same query text through both sides.
 * - **End to end:** typed sub-queries through each side's own full pipeline, with
 *   its cache cleared. Typed queries skip expansion, whose sampling is random and
 *   would swamp everything else.
 * - **Expansion:** repeated runs per side. In-process expansion samples at
 *   temperature 0.7, so it differs from itself. The local-versus-local figure is
 *   the baseline the local-versus-remote figure has to be read against.
 */

export interface BenchQuery {
  id: string;
  collection: string;
  query: string;
  intent: string;
  searches: Array<{ type: "lex" | "vec" | "hyde"; query: string }>;
}

export interface SearchOutcome {
  files: string[];
  scores: number[];
}

/** One way of running models: in-process or through a worker. */
export interface ParitySide {
  /** Candidate chunks, without reranking. Only the local side's are used, for both. */
  candidates(query: BenchQuery, limit: number): Promise<RerankDocument[]>;
  rerank(query: string, documents: RerankDocument[]): Promise<RerankResult>;
  embed(text: string): Promise<number[] | null>;
  search(query: BenchQuery, limit: number): Promise<SearchOutcome>;
  expand(query: string): Promise<Queryable[]>;
  clearCache(): void;
}

export interface QueryReport {
  id: string;
  collection: string;
  candidates: number;
  rerank: { delta: ScoreDelta; spearman: number | null; top10Overlap: number; localMs: number; remoteMs: number } | null;
  embedCosine: number | null;
  endToEnd: { top10Overlap: number; spearman: number | null; delta: ScoreDelta; localMs: number; remoteMs: number; localHits: number; remoteHits: number } | null;
  expansion: { localVsLocal: number | null; localVsRemote: number | null; remoteVsRemote: number | null; exactMatchRate: number };
  errors: string[];
}

export interface ParityReport {
  queries: QueryReport[];
  summary: {
    rerankMeanDelta: Summary;
    rerankMaxDelta: Summary;
    rerankSpearman: Summary;
    rerankTop10Overlap: Summary;
    embedCosine: Summary;
    endToEndTop10Overlap: Summary;
    endToEndSpearman: Summary;
    latencyMs: { rerankLocal: Summary; rerankRemote: Summary; searchLocal: Summary; searchRemote: Summary };
    expansion: { localVsLocal: Summary; localVsRemote: Summary; remoteVsRemote: Summary; exactMatchRate: Summary };
  };
}

export interface ParityOptions {
  candidateLimit: number;
  expansionRuns: number;
  now?: () => number;
  onProgress?: (done: number, total: number, id: string) => void;
}

/** QMD prefixes the intent to the rerank query; the direct comparison has to as well. */
export function rerankQueryFor(query: BenchQuery): string {
  return query.intent ? `${query.intent}\n\n${query.query}` : query.query;
}

function scoresByFile(result: RerankResult): Map<string, number> {
  return new Map(result.results.map((r) => [r.file, r.score]));
}

function ranked(result: RerankResult): string[] {
  return [...result.results].sort((a, b) => b.score - a.score || a.file.localeCompare(b.file)).map((r) => r.file);
}

/** Runs a measurement and times it. */
type Timed = <T>(run: () => Promise<T>) => Promise<[T, number]>;
/** Runs a step for one query. One bad query should not lose a long run, and a failure is a finding, so it is kept. */
type Attempt = <T>(label: string, run: () => Promise<T>) => Promise<T | null>;

function attemptInto(errors: string[]): Attempt {
  return async (label, run) => {
    try {
      return await run();
    } catch (err) {
      errors.push(`${label}: ${err instanceof Error ? err.message : "failed"}`);
      return null;
    }
  };
}

interface QueryRun {
  query: BenchQuery;
  local: ParitySide;
  remote: ParitySide;
  options: ParityOptions;
  timed: Timed;
  attempt: Attempt;
}

async function compareRerank(run: QueryRun, candidates: Parameters<ParitySide["rerank"]>[1]): Promise<QueryReport["rerank"]> {
  const { query, local, remote, timed, attempt } = run;
  if (candidates.length === 0) return null;
  const rerankQuery = rerankQueryFor(query);
  const localResult = await attempt("rerank local", () => timed(() => local.rerank(rerankQuery, candidates)));
  const remoteResult = await attempt("rerank remote", () => timed(() => remote.rerank(rerankQuery, candidates)));
  if (!localResult || !remoteResult) return null;
  const l = scoresByFile(localResult[0]);
  const r = scoresByFile(remoteResult[0]);
  const files = candidates.map((c) => c.file).filter((f) => l.has(f) && r.has(f));
  const a = files.map((f) => l.get(f)!);
  const b = files.map((f) => r.get(f)!);
  return {
    delta: scoreDelta(a, b),
    spearman: spearman(a, b),
    top10Overlap: topKOverlap(ranked(localResult[0]), ranked(remoteResult[0]), 10),
    localMs: localResult[1],
    remoteMs: remoteResult[1],
  };
}

async function compareEmbed({ query, local, remote, attempt }: QueryRun): Promise<number | null> {
  const vectors = await attempt("embed", async () => {
    const text = `task: search result | query: ${query.query}`;
    return [await local.embed(text), await remote.embed(text)] as const;
  });
  return vectors?.[0] && vectors[1] ? cosine(vectors[0], vectors[1]) : null;
}

async function compareSearch({ query, local, remote, timed, attempt }: QueryRun): Promise<QueryReport["endToEnd"]> {
  local.clearCache();
  remote.clearCache();
  const localSearch = await attempt("search local", () => timed(() => local.search(query, 10)));
  const remoteSearch = await attempt("search remote", () => timed(() => remote.search(query, 10)));
  if (!localSearch || !remoteSearch) return null;
  const [l, localMs] = localSearch;
  const [r, remoteMs] = remoteSearch;
  const shared = l.files.filter((f) => r.files.includes(f));
  const a = shared.map((f) => l.scores[l.files.indexOf(f)]!);
  const b = shared.map((f) => r.scores[r.files.indexOf(f)]!);
  return { top10Overlap: topKOverlap(l.files, r.files, 10), spearman: spearman(a, b), delta: scoreDelta(a, b), localMs, remoteMs, localHits: l.files.length, remoteHits: r.files.length };
}

async function expansionRuns({ query, options, attempt }: QueryRun, side: ParitySide, label: string): Promise<Array<Set<string>>> {
  const runs: Array<Set<string>> = [];
  for (let i = 0; i < options.expansionRuns; i++) {
    const out = await attempt(`expand ${label}`, () => side.expand(query.query));
    if (out) runs.push(new Set(out.map(expansionKey)));
  }
  return runs;
}

function exactMatchRate(localRuns: Array<Set<string>>, remoteRuns: Array<Set<string>>): number {
  const pairs = localRuns.length * remoteRuns.length;
  if (pairs === 0) return 0;
  const exact = localRuns.flatMap((a) => remoteRuns.filter((b) => jaccard(a, b) === 1)).length;
  return exact / pairs;
}

async function compareExpansion(run: QueryRun): Promise<QueryReport["expansion"]> {
  const localRuns = await expansionRuns(run, run.local, "local");
  const remoteRuns = await expansionRuns(run, run.remote, "remote");
  return {
    localVsLocal: meanPairwiseJaccard(localRuns),
    localVsRemote: meanPairwiseJaccard(localRuns, remoteRuns),
    remoteVsRemote: meanPairwiseJaccard(remoteRuns),
    exactMatchRate: exactMatchRate(localRuns, remoteRuns),
  };
}

async function reportQuery(query: BenchQuery, local: ParitySide, remote: ParitySide, options: ParityOptions, timed: Timed): Promise<QueryReport> {
  const errors: string[] = [];
  const run: QueryRun = { query, local, remote, options, timed, attempt: attemptInto(errors) };
  const candidates = (await run.attempt("candidates", () => local.candidates(query, options.candidateLimit))) ?? [];
  const rerank = await compareRerank(run, candidates);
  const embedCosine = await compareEmbed(run);
  const endToEnd = await compareSearch(run);
  const expansion = await compareExpansion(run);
  return { id: query.id, collection: query.collection, candidates: candidates.length, rerank, embedCosine, endToEnd, expansion, errors };
}

export async function runParity(queries: readonly BenchQuery[], local: ParitySide, remote: ParitySide, options: ParityOptions): Promise<ParityReport> {
  const now = options.now ?? (() => performance.now());
  const timed: Timed = async (run) => {
    const started = now();
    return [await run(), now() - started];
  };
  const reports: QueryReport[] = [];

  for (const [position, query] of queries.entries()) {
    reports.push(await reportQuery(query, local, remote, options, timed));
    options.onProgress?.(position + 1, queries.length, query.id);
  }

  return {
    queries: reports,
    summary: {
      rerankMeanDelta: summarize(reports.map((q) => q.rerank?.delta.mean ?? null)),
      rerankMaxDelta: summarize(reports.map((q) => q.rerank?.delta.max ?? null)),
      rerankSpearman: summarize(reports.map((q) => q.rerank?.spearman ?? null)),
      rerankTop10Overlap: summarize(reports.map((q) => q.rerank?.top10Overlap ?? null)),
      embedCosine: summarize(reports.map((q) => q.embedCosine)),
      endToEndTop10Overlap: summarize(reports.map((q) => q.endToEnd?.top10Overlap ?? null)),
      endToEndSpearman: summarize(reports.map((q) => q.endToEnd?.spearman ?? null)),
      latencyMs: {
        rerankLocal: summarize(reports.map((q) => q.rerank?.localMs ?? null)),
        rerankRemote: summarize(reports.map((q) => q.rerank?.remoteMs ?? null)),
        searchLocal: summarize(reports.map((q) => q.endToEnd?.localMs ?? null)),
        searchRemote: summarize(reports.map((q) => q.endToEnd?.remoteMs ?? null)),
      },
      expansion: {
        localVsLocal: summarize(reports.map((q) => q.expansion.localVsLocal)),
        localVsRemote: summarize(reports.map((q) => q.expansion.localVsRemote)),
        remoteVsRemote: summarize(reports.map((q) => q.expansion.remoteVsRemote)),
        exactMatchRate: summarize(reports.map((q) => q.expansion.exactMatchRate)),
      },
    },
  };
}

/** Structural checks on the query file, so a bad edit fails before a long run starts. */
export function validateQuerySet(raw: unknown): BenchQuery[] {
  if (!Array.isArray(raw)) throw new Error("The query set must be a list");
  const ids = new Set<string>();
  for (const q of raw as BenchQuery[]) {
    if (!isFilled(q.id) || ids.has(q.id)) throw new Error(`Bad or repeated query id: ${String(q.id)}`);
    ids.add(q.id);
    validateQuery(q);
  }
  return raw as BenchQuery[];
}

function isFilled(value: unknown): value is string {
  return typeof value === "string" && value !== "";
}

function validateQuery(q: BenchQuery): void {
  if (!isFilled(q.collection)) throw new Error(`${q.id}: no collection`);
  if (!isFilled(q.query)) throw new Error(`${q.id}: no query`);
  if (!isFilled(q.intent)) throw new Error(`${q.id}: no intent`);
  if (!Array.isArray(q.searches) || q.searches.length === 0) throw new Error(`${q.id}: no searches`);
  for (const s of q.searches) {
    if (!["lex", "vec", "hyde"].includes(s.type) || !isFilled(s.query)) throw new Error(`${q.id}: bad search`);
  }
}
