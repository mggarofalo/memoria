import { HttpError } from "./errors.ts";
import type { DocumentRef, MultiGetEntry, QmdBackend } from "./store.ts";

/**
 * Selectors for `/docs:multiGet` and references for `/docs/*`.
 *
 * Globs never reach QMD. The SDK compiles them with picomatch and runs the
 * result over every active path on this single event loop, so one crafted
 * pattern (`+(*)Z`) can wedge the process until it restarts. Instead a strict
 * grammar is enforced here, `matchGlob` matches in bounded time, and QMD only
 * ever receives the exact `qmd://` URIs that matched.
 *
 * Memoria.Api enforces the same grammar; `test/glob-policy-cases.json` pins the
 * two together. Change them in step.
 */

export const MAX_GLOB_LENGTH = 256;
export const MAX_GLOB_WILDCARDS = 12;
export const MAX_GLOBSTARS = 2;
export const MAX_GLOBS_PER_REQUEST = 10;
export const MAX_REFERENCE_LENGTH = 1024;
export const MAX_REFERENCES_PER_REQUEST = 200;
export const MAX_BYTES_CEILING = 1_048_576;

/** Any of these marks a selector as a glob, which must then pass the grammar. */
const GLOB_META = /[*?{}[\]()|!^$\\]/;
const GLOB_CHARS = /^[\p{L}\p{N}._\-/:*? ]+$/u;
const DOCID = /^#?[a-f0-9]{6,}$/i;

/** Why a glob is refused, or null when it is within the grammar. */
export function globViolation(glob: string): string | null {
  if (glob.length > MAX_GLOB_LENGTH) return `a glob may be at most ${MAX_GLOB_LENGTH} characters`;
  if (!GLOB_CHARS.test(glob)) return "a glob may contain only letters, digits, space, '.', '_', '-', '/', ':', '*' and '?'; braces, brackets, parentheses and extglobs are refused";
  // `??` is fixed-width and is left alone; a run involving `*` is what backtracks.
  if (/\*\?|\?\*/.test(glob)) return "a glob may not put '*' next to '?'";
  const segments = glob.split("/");
  if (segments.some((s) => s.includes("**") && s !== "**")) return "'**' must be a whole path segment";
  if (segments.filter((s) => s === "**").length > MAX_GLOBSTARS) return `a glob may contain at most ${MAX_GLOBSTARS} '**' segments`;
  const wildcards = (glob.replaceAll("**", "*").match(/[*?]/g) ?? []).length;
  if (wildcards > MAX_GLOB_WILDCARDS) return `a glob may contain at most ${MAX_GLOB_WILDCARDS} wildcards`;
  return null;
}

export interface Selectors {
  globs: string[];
  references: string[];
}

/** Splits and validates what a caller sent. Throws a 400 for anything outside the grammar or the caps. */
export function parseSelectors(pattern: string | undefined, ids: readonly string[] | undefined): Selectors {
  if ((ids ?? []).some((id) => id.includes(","))) {
    throw new HttpError(400, "validation", "An id may not contain a comma");
  }
  const parts = [...(pattern ? pattern.split(",") : []), ...(ids ?? [])].map((p) => p.trim()).filter((p) => p !== "");
  const unique = [...new Set(parts)];
  const globs = unique.filter((p) => GLOB_META.test(p));
  const references = unique.filter((p) => !GLOB_META.test(p));

  for (const glob of globs) {
    const violation = globViolation(glob);
    if (violation) throw new HttpError(400, "validation", `Refused glob: ${violation}`);
  }
  if (globs.length > MAX_GLOBS_PER_REQUEST) throw new HttpError(400, "validation", `At most ${MAX_GLOBS_PER_REQUEST} globs per request`);
  if (references.length > MAX_REFERENCES_PER_REQUEST) throw new HttpError(400, "validation", `At most ${MAX_REFERENCES_PER_REQUEST} references per request`);
  if (references.some((r) => r.length > MAX_REFERENCE_LENGTH)) throw new HttpError(400, "validation", `A reference may be at most ${MAX_REFERENCE_LENGTH} characters`);
  return { globs, references };
}

function matchSegment(pattern: string, segment: string): boolean {
  if (segment.startsWith(".") && (pattern[0] === "*" || pattern[0] === "?")) return false;
  let p = 0;
  let s = 0;
  let star = -1;
  let mark = 0;
  while (s < segment.length) {
    if (p < pattern.length && pattern[p] === "*") {
      star = p++;
      mark = s;
    } else if (p < pattern.length && (pattern[p] === "?" || pattern[p] === segment[s])) {
      p++;
      s++;
    } else if (star >= 0) {
      p = star + 1;
      s = ++mark;
    } else {
      return false;
    }
  }
  while (p < pattern.length && pattern[p] === "*") p++;
  // `*` needs at least one character; only an empty pattern, as in `qmd://`, matches an empty segment.
  return p === pattern.length && (segment.length > 0 || pattern.length === 0);
}

/**
 * Glob matching in O(segments² × segment length²) with no regular expression, so
 * no input can backtrack. Follows picomatch's defaults for this grammar: `*` and
 * `?` stay inside a segment, `**` spans segments, and neither matches a leading dot.
 */
export function matchGlob(glob: string, path: string): boolean {
  const pattern = glob.split("/");
  const segments = path.split("/");
  let next: boolean[] = segments.map(() => false).concat(true);
  for (let i = pattern.length - 1; i >= 0; i--) {
    const current: boolean[] = new Array(segments.length + 1).fill(false);
    for (let j = segments.length; j >= 0; j--) {
      if (pattern[i] === "**") {
        current[j] = next[j]! || (j < segments.length && !segments[j]!.startsWith(".") && current[j + 1]!);
      } else {
        current[j] = j < segments.length && next[j + 1]! && matchSegment(pattern[i]!, segments[j]!);
      }
    }
    next = current;
  }
  return next[0]!;
}

function displayPath(ref: DocumentRef): string {
  return `${ref.collection}/${ref.path}`;
}

function virtualPath(ref: DocumentRef): string {
  return `qmd://${displayPath(ref)}`;
}

/** Same three forms the SDK tries: `qmd://collection/path`, bare `path`, `collection/path`. */
function matchesGlob(glob: string, ref: DocumentRef): boolean {
  return matchGlob(glob, displayPath(ref)) || matchGlob(glob, ref.path) || matchGlob(glob, virtualPath(ref));
}

/**
 * Resolves a reference against the given rows only: docid, then `qmd://` URI,
 * `collection/path`, bare path, and path-boundary suffix, exact case before any
 * case. It looks at nothing but those rows and runs no similar-files scan, so
 * how long a caller waits cannot depend on anything outside their allowlist.
 */
export function resolveReference(rows: readonly DocumentRef[], reference: string): DocumentRef[] {
  const trimmed = reference.trim();
  if (DOCID.test(trimmed)) {
    const prefix = trimmed.replace(/^#/, "").toLowerCase();
    return rows.filter((r) => r.hash.toLowerCase().startsWith(prefix));
  }
  const isUri = trimmed.startsWith("qmd://");
  const wanted = isUri ? trimmed.slice("qmd://".length) : trimmed;
  const find = (fold: (s: string) => string): DocumentRef[] => {
    const target = fold(wanted);
    const display = rows.filter((r) => fold(displayPath(r)) === target);
    if (display.length > 0 || isUri) return display;
    const bare = rows.filter((r) => fold(r.path) === target);
    if (bare.length > 0) return bare;
    return rows.filter((r) => fold(r.path).endsWith(`/${target}`));
  };
  const exact = find((s) => s);
  return exact.length > 0 ? exact : find((s) => s.toLowerCase());
}

/**
 * Resolve every selector and merge, keeping the first entry for each file. With
 * `collections` set, only those collections are ever looked at: anything else
 * is not matched, not measured, and has no body read.
 */
export async function resolveMultiGet(
  backend: Pick<QmdBackend, "multiGet" | "listDocumentRefs">,
  pattern: string | undefined,
  ids: readonly string[] | undefined,
  options: { includeBody: boolean; maxBytes: number },
  collections?: readonly string[],
): Promise<{ docs: MultiGetEntry[]; errors: string[] }> {
  const { globs, references } = parseSelectors(pattern, ids);
  const scoped = collections !== undefined;
  const rows = globs.length > 0 || scoped ? await backend.listDocumentRefs(collections) : [];

  const errors = new Set<string>();
  const uris = new Set<string>();
  for (const glob of globs) {
    const matched = rows.filter((row) => matchesGlob(glob, row));
    if (matched.length === 0) errors.add(`No files matched pattern: ${glob}`);
    for (const row of matched) uris.add(virtualPath(row));
  }
  if (scoped) {
    for (const reference of references) {
      const matched = resolveReference(rows, reference);
      // A docid matching several rows is the same content; any copy will do.
      if (matched.length === 0) errors.add(`File not found: ${reference}`);
      else if (matched.length > 1 && !DOCID.test(reference.trim())) errors.add(`Ambiguous path ${reference}: ${matched.map(virtualPath).join(", ")}`);
      else uris.add(virtualPath(matched[0]!));
    }
  }

  // A trailing comma selects the SDK's list mode, which resolves each name exactly.
  const lists = [...(uris.size > 0 ? [`${[...uris].join(",")},`] : []), ...(!scoped && references.length > 0 ? [`${references.join(",")},`] : [])];
  const docs: MultiGetEntry[] = [];
  const seen = new Set<string>();
  for (const list of lists) {
    const result = await backend.multiGet(list, options);
    for (const error of result.errors) errors.add(error);
    for (const entry of result.docs) {
      if (seen.has(entry.doc.displayPath)) continue;
      seen.add(entry.doc.displayPath);
      docs.push(entry);
    }
  }
  return { docs, errors: [...errors] };
}
