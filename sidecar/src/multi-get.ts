import type { MultiGetEntry, QmdBackend } from "./store.ts";

/**
 * QMD's multi_get reads one string in one of two modes: a comma-separated list
 * of references, or, when the string contains `*`, `?` or `{`, a single glob
 * with any commas taken literally. A glob and a reference therefore cannot
 * share a call, so each glob gets its own and the references share another.
 */

/** Characters that mean a selector is a glob. `[` is ours; the SDK needs it only in a lone pattern. */
const GLOB_CHARS = /[*?{[]/;

/** Split on commas outside `{}`, `[]` and `()`, which belong to the glob. */
function splitTopLevel(pattern: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let current = "";
  for (const ch of pattern) {
    if (ch === "{" || ch === "[" || ch === "(") depth++;
    else if ((ch === "}" || ch === "]" || ch === ")") && depth > 0) depth--;
    if (ch === "," && depth === 0) {
      parts.push(current);
      current = "";
    } else {
      current += ch;
    }
  }
  parts.push(current);
  return parts;
}

/**
 * The strings to hand to `backend.multiGet`. References end in a comma even
 * when there is only one: that is what selects list mode, which resolves
 * docids and `qmd://` URIs and reports a miss by name.
 */
export function multiGetSelectors(pattern: string | undefined, ids: readonly string[] | undefined): string[] {
  const parts = [...(pattern ? splitTopLevel(pattern) : []), ...(ids ?? [])].map((p) => p.trim()).filter((p) => p !== "");
  const unique = [...new Set(parts)];
  const globs = unique.filter((p) => GLOB_CHARS.test(p));
  const references = unique.filter((p) => !GLOB_CHARS.test(p));
  return [...globs, ...(references.length > 0 ? [`${references.join(",")},`] : [])];
}

/** Resolve every selector and merge, keeping the first entry for each file. */
export async function resolveMultiGet(
  backend: Pick<QmdBackend, "multiGet">,
  pattern: string | undefined,
  ids: readonly string[] | undefined,
  options: { includeBody: boolean; maxBytes: number },
): Promise<{ docs: MultiGetEntry[]; errors: string[] }> {
  const docs: MultiGetEntry[] = [];
  const errors = new Set<string>();
  const seen = new Set<string>();
  for (const selector of multiGetSelectors(pattern, ids)) {
    const result = await backend.multiGet(selector, options);
    for (const error of result.errors) errors.add(error);
    for (const entry of result.docs) {
      if (seen.has(entry.doc.displayPath)) continue;
      seen.add(entry.doc.displayPath);
      docs.push(entry);
    }
  }
  return { docs, errors: [...errors] };
}
