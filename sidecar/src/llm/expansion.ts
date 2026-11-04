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

/** Same terms the SDK uses to drop a line that wandered off the query. */
function queryTerms(query: string): string[] {
  return query.toLowerCase().replace(/[^a-z0-9\s]/g, " ").split(/\s+/).filter(Boolean);
}

/** Parse model output the way the SDK does, including its fallback for useless output. */
export function parseExpansion(query: string, output: string, includeLexical: boolean): Queryable[] {
  const terms = queryTerms(query);
  const hasQueryTerm = (text: string): boolean => {
    if (terms.length === 0) return true;
    const lower = text.toLowerCase();
    return terms.some((term) => lower.includes(term));
  };
  const queryables: Queryable[] = [];
  for (const line of output.trim().split("\n")) {
    const colon = line.indexOf(":");
    if (colon === -1) continue;
    const type = line.slice(0, colon).trim();
    if (type !== "lex" && type !== "vec" && type !== "hyde") continue;
    const text = line.slice(colon + 1).trim();
    if (hasQueryTerm(text)) queryables.push({ type, text });
  }
  const filtered = includeLexical ? queryables : queryables.filter((q) => q.type !== "lex");
  if (filtered.length > 0) return filtered;
  const fallback: Queryable[] = [
    { type: "hyde", text: `Information about ${query}` },
    { type: "lex", text: query },
    { type: "vec", text: query },
  ];
  return includeLexical ? fallback : fallback.filter((q) => q.type !== "lex");
}

/** What the SDK returns when generation itself fails: the query, unexpanded. */
export function expansionFailure(query: string, includeLexical: boolean): Queryable[] {
  return includeLexical ? [{ type: "lex", text: query }, { type: "vec", text: query }] : [{ type: "vec", text: query }];
}
