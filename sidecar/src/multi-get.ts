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
/** Total bytes of document bodies in one response: the default, and the most a caller may ask for. */
export const DEFAULT_MAX_TOTAL_BYTES = 16 * 1_048_576;
export const MAX_TOTAL_BYTES_CEILING = 256 * 1_048_576;
/** Documents the globs of one request can select. */
export const MAX_GLOB_DOCUMENTS = 1000;
/** References that may reach QMD's miss path in one request, which scans every path for each. Memoria.Api never takes it. */
export const MAX_SCANNING_REFERENCES = 10;

const URI_PREFIX = "qmd://";
const DOCID_LENGTH = 6;

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

/** Neither `*` nor `?` matches a leading dot. */
function hiddenFromWildcard(pattern: string, segment: string): boolean {
  return segment.startsWith(".") && /^[*?]/.test(pattern);
}

function onlyStarsFrom(pattern: string, start: number): boolean {
  return pattern.slice(start).replaceAll("*", "") === "";
}

function matchSegment(pattern: string, segment: string): boolean {
  if (hiddenFromWildcard(pattern, segment)) return false;
  // `*` needs at least one character; only an empty pattern, as in `qmd://`, matches an empty segment.
  if (segment.length === 0) return pattern.length === 0;
  let p = 0;
  let s = 0;
  let star = -1;
  let mark = 0;
  while (s < segment.length) {
    const c = pattern[p];
    if (c === "*") {
      star = p++;
      mark = s;
    } else if (c === "?" || c === segment[s]) {
      p++;
      s++;
    } else if (star >= 0) {
      p = star + 1;
      s = ++mark;
    } else {
      return false;
    }
  }
  return onlyStarsFrom(pattern, p);
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
  return `${URI_PREFIX}${displayPath(ref)}`;
}

/** Same three forms the SDK tries: `qmd://collection/path`, bare `path`, `collection/path`. */
function matchesGlob(glob: string, ref: DocumentRef): boolean {
  return matchGlob(glob, displayPath(ref)) || matchGlob(glob, ref.path) || matchGlob(glob, virtualPath(ref));
}

function pushTo<T>(map: Map<string, T[]>, key: string, value: T): void {
  const bucket = map.get(key);
  if (bucket) bucket.push(value);
  else map.set(key, [value]);
}

function lastSegment(path: string): string {
  return path.slice(path.lastIndexOf("/") + 1);
}

/**
 * Resolves references against the given rows only: docid, then `qmd://` URI,
 * `collection/path`, bare path, and path-boundary suffix. It looks at nothing but
 * those rows and runs no similar-files scan, so how long a caller waits cannot depend
 * on anything outside their allowlist. Built once per request: each lookup after that
 * touches one bucket, so 200 misses cost little more than one.
 */
export class ReferenceIndex {
  readonly #byDisplay = new Map<string, DocumentRef[]>();
  readonly #byPath = new Map<string, DocumentRef[]>();
  readonly #byName = new Map<string, DocumentRef[]>();
  readonly #byDocid = new Map<string, DocumentRef[]>();

  constructor(rows: readonly DocumentRef[]) {
    for (const row of rows) {
      const path = row.path.toLowerCase();
      pushTo(this.#byDisplay, displayPath(row).toLowerCase(), row);
      pushTo(this.#byPath, path, row);
      pushTo(this.#byName, lastSegment(path), row);
      pushTo(this.#byDocid, row.hash.slice(0, DOCID_LENGTH).toLowerCase(), row);
    }
  }

  resolve(reference: string): DocumentRef[] {
    const trimmed = reference.trim();
    if (DOCID.test(trimmed)) return this.#resolveDocid(trimmed.replace(/^#/, "").toLowerCase());
    const isUri = trimmed.startsWith(URI_PREFIX);
    const wanted = isUri ? trimmed.slice(URI_PREFIX.length) : trimmed;
    const target = wanted.toLowerCase();
    const display = this.#byDisplay.get(target) ?? [];
    if (display.length > 0 || isUri) return preferExactCase(display, wanted, displayPath);
    const bare = this.#byPath.get(target) ?? [];
    if (bare.length > 0) return preferExactCase(bare, wanted, (r) => r.path);
    return (this.#byName.get(lastSegment(target)) ?? []).filter((r) => r.path.toLowerCase().endsWith(`/${target}`));
  }

  #resolveDocid(prefix: string): DocumentRef[] {
    return (this.#byDocid.get(prefix.slice(0, DOCID_LENGTH)) ?? []).filter((r) => r.hash.toLowerCase().startsWith(prefix));
  }
}

/** Lookups ignore case, but `Work/a.md` and `work/a.md` are two documents: an exact spelling wins. */
function preferExactCase(rows: DocumentRef[], wanted: string, spelling: (row: DocumentRef) => string): DocumentRef[] {
  const exact = rows.filter((r) => spelling(r) === wanted);
  return exact.length > 0 ? exact : rows;
}

export function resolveReference(rows: readonly DocumentRef[], reference: string): DocumentRef[] {
  return new ReferenceIndex(rows).resolve(reference);
}

/**
 * The URI to read for a single-document lookup, or null for a plain miss. Only a
 * caller that asked for neither an allowlist nor to skip the scan reaches QMD with
 * their own text, because QMD's miss path compares it against every path in the index.
 */
export async function resolveLookup(
  backend: Pick<QmdBackend, "listDocumentRefs">,
  lookup: string,
  collections: readonly string[] | undefined,
  similarFiles: boolean,
): Promise<string | null> {
  if (collections === undefined && similarFiles) return lookup;
  const [match] = resolveReference(await backend.listDocumentRefs(collections), lookup);
  return match ? virtualPath(match) : null;
}

export interface MultiGetOptions {
  includeBody: boolean;
  maxBytes: number;
  maxTotalBytes: number;
  /** False resolves every reference here, and never runs QMD's similar-files scan. */
  similarFiles: boolean;
  /** Match and read only inside these. */
  collections?: readonly string[] | undefined;
}

export interface MultiGetOutcome {
  docs: MultiGetEntry[];
  errors: string[];
  /** True when globs matched more than MAX_GLOB_DOCUMENTS; the rest were not looked at. */
  truncated: boolean;
}

interface Selection {
  /** Comma lists for the SDK's list mode. A trailing comma selects that mode. */
  lists: string[];
  errors: Set<string>;
  truncated: boolean;
}

function selectGlobMatches(rows: readonly DocumentRef[], globs: readonly string[], uris: Set<string>, errors: Set<string>): boolean {
  for (const glob of globs) {
    const before = uris.size;
    let matched = false;
    for (const row of rows) {
      if (!matchesGlob(glob, row)) continue;
      matched = true;
      if (uris.size >= MAX_GLOB_DOCUMENTS) return true;
      uris.add(virtualPath(row));
    }
    if (!matched && uris.size === before) errors.add(`No files matched pattern: ${glob}`);
  }
  return false;
}

function selectReferences(rows: readonly DocumentRef[], references: readonly string[], uris: Set<string>, errors: Set<string>): void {
  const index = new ReferenceIndex(rows);
  for (const reference of references) {
    const matched = index.resolve(reference);
    // A docid matching several rows is the same content; any copy will do.
    if (matched.length === 0) errors.add(`File not found: ${reference}`);
    else if (matched.length > 1 && !DOCID.test(reference.trim())) errors.add(`Ambiguous path ${reference}: ${matched.map(virtualPath).join(", ")}`);
    else uris.add(virtualPath(matched[0]!));
  }
}

async function selectDocuments(backend: Pick<QmdBackend, "listDocumentRefs">, { globs, references }: Selectors, options: MultiGetOptions): Promise<Selection> {
  const local = options.collections !== undefined || !options.similarFiles;
  if (!local && references.length > MAX_SCANNING_REFERENCES) {
    throw new HttpError(400, "validation", `At most ${MAX_SCANNING_REFERENCES} references per request without similarFiles=false: each miss scans every path in the index`);
  }
  const rows = globs.length > 0 || local ? await backend.listDocumentRefs(options.collections) : [];
  const errors = new Set<string>();
  const uris = new Set<string>();
  const truncated = selectGlobMatches(rows, globs, uris, errors);
  if (local) selectReferences(rows, references, uris, errors);
  const lists = [...(uris.size > 0 ? [`${[...uris].join(",")},`] : []), ...(!local && references.length > 0 ? [`${references.join(",")},`] : [])];
  return { lists, errors, truncated };
}

function overBudget(entry: { doc: { filepath: string; displayPath: string } }): MultiGetEntry {
  return { doc: { filepath: entry.doc.filepath, displayPath: entry.doc.displayPath }, skipped: true, skipReason: "Response size budget reached" };
}

/**
 * Reads bodies one at a time against the response budget, so a document that will
 * not fit is never read. QMD's length counts characters, a lower bound on bytes, so
 * a document can pass that check and still not fit once read; it is then dropped, and
 * what is ever held is the budget plus one document.
 */
async function readWithinBudget(backend: Pick<QmdBackend, "multiGet">, entries: readonly MultiGetEntry[], options: MultiGetOptions): Promise<MultiGetEntry[]> {
  const out: MultiGetEntry[] = [];
  let remaining = options.maxTotalBytes;
  let sinceYield = 0;
  for (const entry of entries) {
    if (entry.skipped) {
      out.push(entry);
      continue;
    }
    if (entry.doc.bodyLength > remaining) {
      out.push(overBudget(entry));
      continue;
    }
    const { docs } = await backend.multiGet(`${entry.doc.filepath},`, { includeBody: true, maxBytes: options.maxBytes });
    const read = docs[0];
    if (!read) continue;
    const bytes = read.skipped ? 0 : Buffer.byteLength(read.doc.body ?? "", "utf8");
    if (bytes > remaining) {
      out.push(overBudget(entry));
      continue;
    }
    remaining -= bytes;
    out.push(read);
    // The SDK is synchronous: give the event loop a turn so /health still answers.
    if (++sinceYield % 50 === 0) await new Promise((resolve) => setImmediate(resolve));
  }
  return out;
}

/**
 * Resolve every selector and merge, keeping the first entry for each file. Sizes
 * come first, with no bodies, and bodies are then read within the budget. With
 * `collections` set, only those collections are ever looked at: anything else is not
 * matched, not measured, and has no body read.
 */
export async function resolveMultiGet(
  backend: Pick<QmdBackend, "multiGet" | "listDocumentRefs">,
  pattern: string | undefined,
  ids: readonly string[] | undefined,
  options: MultiGetOptions,
): Promise<MultiGetOutcome> {
  const { lists, errors, truncated } = await selectDocuments(backend, parseSelectors(pattern, ids), options);
  const listed: MultiGetEntry[] = [];
  const seen = new Set<string>();
  for (const list of lists) {
    const result = await backend.multiGet(list, { includeBody: false, maxBytes: options.maxBytes });
    for (const error of result.errors) errors.add(error);
    for (const entry of result.docs) {
      if (seen.has(entry.doc.displayPath)) continue;
      seen.add(entry.doc.displayPath);
      listed.push(entry);
    }
  }
  const docs = options.includeBody ? await readWithinBudget(backend, listed, options) : listed;
  return { docs, errors: [...errors], truncated };
}
