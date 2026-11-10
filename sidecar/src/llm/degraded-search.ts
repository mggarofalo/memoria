import type { BackendSearchOptions, SearchHit, SearchOutcome, SearchQuery } from "../store.ts";
import { WorkerError } from "./client.ts";
import type { RemoteLlm } from "./remote.ts";
import type { DegradedReason } from "./types.ts";

/** QMD's query syntax checks, from its store module. */
export interface QueryValidators {
  validateLexQuery(query: string): string | null;
  validateSemanticQuery(query: string): string | null;
}

/**
 * The typed sub-queries for a plain query when expansion is unavailable. Each is
 * kept only if QMD accepts it, because QMD's syntax rules that the expansion path
 * never applied to the raw text would otherwise turn degraded search into a 500.
 */
export function plainQueries(validators: QueryValidators, query: string): SearchQuery[] {
  const text = query.replace(/\s+/g, " ").trim();
  const out: SearchQuery[] = [];
  if (validators.validateLexQuery(text) === null) out.push({ type: "lex", query: text });
  if (validators.validateSemanticQuery(text) === null) out.push({ type: "vec", query: text });
  if (out.length > 0) return out;
  const words = text.replace(/[^\p{L}\p{N}\s]/gu, " ").replace(/\s+/g, " ").trim();
  return words === "" ? [] : [{ type: "lex", query: words }];
}

/** `minScore` goes too: it is on the reranker's scale, and rank-derived scores would all fall below it. */
function withoutRerank(options: BackendSearchOptions, reasons: Set<DegradedReason>): BackendSearchOptions {
  reasons.add("no_rerank");
  return { ...options, rerank: false, minScore: 0 };
}

function plan(remote: RemoteLlm, validators: QueryValidators, options: BackendSearchOptions, reasons: Set<DegradedReason>): BackendSearchOptions {
  let attempt = options;
  // An unexpanded query needs no expansion model, so it goes without nothing and stays as it is.
  if (options.query !== undefined && options.expand !== false && !remote.canServe("generate")) {
    reasons.add("no_expansion");
    const { query, ...rest } = options;
    attempt = { ...rest, queries: plainQueries(validators, query) };
  }
  return options.rerank && !remote.canServe("rerank") ? withoutRerank(attempt, reasons) : attempt;
}

/**
 * Search that says what it went without. When a worker role is missing, the step
 * that needs it is skipped rather than faked: the SDK caches any score or expansion
 * it receives, so a placeholder would outlive the outage. Skipping means nothing
 * reaches `llm_cache`, and a rerank that fails mid-search throws before the SDK
 * writes anything.
 */
export function searchDegradable(remote: RemoteLlm, validators: QueryValidators, run: (options: BackendSearchOptions) => Promise<SearchHit[]>, options: BackendSearchOptions): Promise<SearchOutcome> {
  return remote.track(async (reasons) => {
    const attempt = plan(remote, validators, options, reasons);
    const hits = await runAttempt(run, attempt, reasons);
    return { hits, degradedReasons: [...reasons] };
  });
}

async function runAttempt(run: (options: BackendSearchOptions) => Promise<SearchHit[]>, attempt: BackendSearchOptions, reasons: Set<DegradedReason>): Promise<SearchHit[]> {
  if (attempt.queries?.length === 0) return [];
  try {
    return await run(attempt);
  } catch (err) {
    if (!attempt.rerank || !(err instanceof WorkerError)) throw err;
    return run(withoutRerank(attempt, reasons));
  }
}
