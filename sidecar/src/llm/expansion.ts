import type { Queryable } from "./types.ts";

/**
 * Query expansion as `LlamaCpp.expandQuery` does it in QMD 2.8.3. The SDK keeps
 * these values inline, so they are copied; test/llm-surface.test.ts reads them
 * back out of the installed SDK and fails if they drift.
 */

export const EXPANSION_GRAMMAR = `
        root ::= line+
        line ::= type ": " content "\\n"
        type ::= "lex" | "vec" | "hyde"
        content ::= [^\\n]+
      `;

export const EXPANSION_SAMPLING = {
  maxTokens: 600,
  temperature: 0.7,
  topK: 20,
  topP: 0.8,
  repeatLastN: 64,
  presencePenalty: 0.5,
} as const;

export function expansionPrompt(query: string): string {
  return `/no_think Expand this search query: ${query}`;
}

/** QMD's model writes a handful of sub-queries. Past these, it is not an expansion. */
export const MAX_EXPANSION_QUERIES = 20;
export const MAX_EXPANSION_LINE_CHARS = 1000;

/** Same terms the SDK uses to drop a line that wandered off the query. */
function queryTerms(query: string): string[] {
  return query.toLowerCase().replace(/[^a-z0-9\s]/g, " ").split(/\s+/).filter(Boolean);
}

function parseLine(line: string, hasQueryTerm: (text: string) => boolean): Queryable | null {
  if (line.length > MAX_EXPANSION_LINE_CHARS) return null;
  const colon = line.indexOf(":");
  if (colon === -1) return null;
  const type = line.slice(0, colon).trim();
  if (type !== "lex" && type !== "vec" && type !== "hyde") return null;
  const text = line.slice(colon + 1).trim();
  return hasQueryTerm(text) ? { type, text } : null;
}

/**
 * Parse model output the way the SDK does, within limits it does not have: each
 * sub-query is a real search, so an endless expansion would freeze the event loop
 * and then replay from the cache. Unlike the SDK, this returns nothing for useless
 * output rather than a placeholder, because the caller treats that as a failure.
 */
export function parseExpansion(query: string, output: string, includeLexical: boolean): Queryable[] {
  const terms = queryTerms(query);
  const hasQueryTerm = (text: string): boolean => {
    if (terms.length === 0) return true;
    const lower = text.toLowerCase();
    return terms.some((term) => lower.includes(term));
  };
  const queryables: Queryable[] = [];
  for (const line of output.trim().split("\n")) {
    if (queryables.length >= MAX_EXPANSION_QUERIES) break;
    const parsed = parseLine(line, hasQueryTerm);
    if (parsed) queryables.push(parsed);
  }
  return includeLexical ? queryables : queryables.filter((q) => q.type !== "lex");
}

/** What the SDK returns when generation itself fails: the query, unexpanded. */
export function expansionFailure(query: string, includeLexical: boolean): Queryable[] {
  return includeLexical ? [{ type: "lex", text: query }, { type: "vec", text: query }] : [{ type: "vec", text: query }];
}
