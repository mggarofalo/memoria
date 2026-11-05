import { parse as parseYaml } from "yaml";

/**
 * Frontmatter-derived metadata. QMD indexes bodies but has no notion of
 * frontmatter, so the sidecar keeps its own small in-memory index keyed by
 * display path (`collection/path`) and applies filters after retrieval.
 */
export interface DocumentMetadata {
  project?: string;
  branch?: string;
  /** Calendar date (YYYY-MM-DD) from the `date` key when present. */
  date?: string;
  startTime?: string;
  endTime?: string;
  tags: string[];
  sessionId?: string;
  source?: string;
}

export interface SearchFilters {
  /** One project name or any-of several. Case-insensitive. */
  project?: string | string[];
  /** One git branch or any-of several. Case-insensitive. */
  branch?: string | string[];
  /** Inclusive lower bound; a date (YYYY-MM-DD) or ISO timestamp. */
  since?: string;
  /** Inclusive upper bound; a bare date covers the whole day. */
  until?: string;
  /** Every listed tag must be present. Case-insensitive. */
  tags?: string[];
}

export interface DocumentHead {
  collection: string;
  path: string;
  /** The first few KB of the document, enough to hold the frontmatter block. */
  head: string;
}

export interface MetadataSource {
  readDocumentHeads(maxBytes: number): DocumentHead[];
}

const FRONTMATTER_RE = /^(?:﻿)?---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/;

export function parseFrontmatter(text: string): Record<string, unknown> | null {
  const m = text.match(FRONTMATTER_RE);
  if (!m) return null;
  try {
    // Duplicate-key checks are quadratic, and warnings would send frontmatter text to stderr.
    // Not "silent", which would also accept malformed YAML instead of throwing.
    const parsed: unknown = parseYaml(m[1]!, { uniqueKeys: false, logLevel: "error" });
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {
      return parsed as Record<string, unknown>;
    }
    return null;
  } catch {
    return null;
  }
}

function str(value: unknown): string | undefined {
  if (typeof value === "string") {
    const t = value.trim();
    return t === "" ? undefined : t;
  }
  if (value instanceof Date) return value.toISOString();
  if (typeof value === "number") return String(value);
  return undefined;
}

function dateOnly(value: unknown): string | undefined {
  const s = str(value);
  if (!s) return undefined;
  const m = s.match(/^(\d{4}-\d{2}-\d{2})/);
  return m ? m[1] : undefined;
}

function strList(value: unknown): string[] {
  if (Array.isArray(value)) {
    return value.map(str).filter((v): v is string => v !== undefined);
  }
  const single = str(value);
  return single ? [single] : [];
}

export function extractMetadata(fm: Record<string, unknown>): DocumentMetadata {
  const meta: DocumentMetadata = { tags: strList(fm.tags) };
  const project = str(fm.project);
  if (project) meta.project = project;
  const branch = str(fm.git_branch ?? fm.branch);
  if (branch) meta.branch = branch;
  const date = dateOnly(fm.date);
  if (date) meta.date = date;
  const startTime = str(fm.start_time);
  if (startTime) meta.startTime = startTime;
  const endTime = str(fm.end_time);
  if (endTime) meta.endTime = endTime;
  const sessionId = str(fm.session_id);
  if (sessionId) meta.sessionId = sessionId;
  const source = str(fm.source);
  if (source) meta.source = source;
  return meta;
}

export function metadataFromText(text: string): DocumentMetadata | undefined {
  const fm = parseFrontmatter(text);
  return fm ? extractMetadata(fm) : undefined;
}

// --- filtering ---------------------------------------------------------------

function anyOf(filter: string | string[] | undefined): string[] | undefined {
  if (filter === undefined) return undefined;
  const list = (Array.isArray(filter) ? filter : [filter]).map((s) => s.trim().toLowerCase()).filter(Boolean);
  return list.length > 0 ? list : undefined;
}

const DATE_ONLY_RE = /^\d{4}-\d{2}-\d{2}$/;

/** Parse a filter bound. Bare dates are UTC midnight; `until` bare dates cover the full day. */
export function parseBound(value: string, edge: "since" | "until"): number {
  const trimmed = value.trim();
  if (DATE_ONLY_RE.test(trimmed)) {
    const ms = Date.parse(`${trimmed}T00:00:00Z`);
    if (Number.isNaN(ms)) throw new Error(`Invalid ${edge} date: expected YYYY-MM-DD`);
    return edge === "until" ? ms + 24 * 60 * 60 * 1000 - 1 : ms;
  }
  const ms = Date.parse(trimmed);
  if (Number.isNaN(ms)) throw new Error(`Invalid ${edge} timestamp: expected YYYY-MM-DD or an ISO timestamp`);
  return ms;
}

/** The instant a document is dated at: start_time when present, else the date at UTC midnight. */
export function documentInstant(meta: DocumentMetadata): number | undefined {
  if (meta.startTime) {
    const ms = Date.parse(meta.startTime);
    if (!Number.isNaN(ms)) return ms;
  }
  if (meta.date) {
    const ms = Date.parse(`${meta.date}T00:00:00Z`);
    if (!Number.isNaN(ms)) return ms;
  }
  return undefined;
}

export function hasFilters(filters: SearchFilters | undefined): filters is SearchFilters {
  if (!filters) return false;
  return (
    anyOf(filters.project) !== undefined ||
    anyOf(filters.branch) !== undefined ||
    (filters.since !== undefined && filters.since.trim() !== "") ||
    (filters.until !== undefined && filters.until.trim() !== "") ||
    (filters.tags !== undefined && filters.tags.length > 0)
  );
}

/**
 * Apply filters to one document's metadata. Documents without metadata fail
 * every active filter — a transcript with no frontmatter cannot be "in
 * project X", so excluding it is the only honest answer.
 */
export function matchesFilters(meta: DocumentMetadata | undefined, filters: SearchFilters): boolean {
  if (!hasFilters(filters)) return true;
  if (!meta) return false;
  return matchesAnyOf(meta.project, filters.project) && matchesAnyOf(meta.branch, filters.branch) && hasAllTags(meta.tags, filters.tags) && withinBounds(meta, filters);
}

/** True when the filter is inactive, or names this value. A missing value matches no active filter. */
function matchesAnyOf(value: string | undefined, filter: string | string[] | undefined): boolean {
  const wanted = anyOf(filter);
  if (!wanted) return true;
  return value !== undefined && value !== "" && wanted.includes(value.toLowerCase());
}

function hasAllTags(tags: string[], wanted: string[] | undefined): boolean {
  if (!wanted || wanted.length === 0) return true;
  const have = new Set(tags.map((t) => t.toLowerCase()));
  return wanted.every((want) => have.has(want.trim().toLowerCase()));
}

function bound(value: string | undefined, edge: "since" | "until"): number | undefined {
  return value?.trim() ? parseBound(value, edge) : undefined;
}

function withinBounds(meta: DocumentMetadata, filters: SearchFilters): boolean {
  const since = bound(filters.since, "since");
  const until = bound(filters.until, "until");
  if (since === undefined && until === undefined) return true;
  const instant = documentInstant(meta);
  if (instant === undefined) return false;
  return (since === undefined || instant >= since) && (until === undefined || instant <= until);
}

// --- index -------------------------------------------------------------------

export const DEFAULT_HEAD_BYTES = 8192;

/**
 * The first `count` characters, counted the way SQLite's `substr` counts them
 * on TEXT: by code point, where `String.slice` would count surrogate halves.
 */
export function firstCodePoints(text: string, count: number): string {
  if (text.length <= count) return text;
  let end = 0;
  for (let seen = 0; seen < count && end < text.length; seen++) {
    end += text.codePointAt(end)! > 0xffff ? 2 : 1;
  }
  return text.slice(0, end);
}

export class MetadataIndex {
  readonly #source: MetadataSource;
  readonly #headBytes: number;
  #entries = new Map<string, DocumentMetadata>();
  #refreshedAt: Date | undefined;

  constructor(source: MetadataSource, headBytes: number = DEFAULT_HEAD_BYTES) {
    this.#source = source;
    this.#headBytes = headBytes;
  }

  /** Rebuild from the source. Cheap: one SQL scan of document heads, no bodies. */
  refresh(): { documents: number; withMetadata: number } {
    const next = new Map<string, DocumentMetadata>();
    let withMetadata = 0;
    const heads = this.#source.readDocumentHeads(this.#headBytes);
    for (const h of heads) {
      const meta = metadataFromText(h.head);
      if (meta) {
        next.set(`${h.collection}/${h.path}`, meta);
        withMetadata++;
      }
    }
    this.#entries = next;
    this.#refreshedAt = new Date();
    return { documents: heads.length, withMetadata };
  }

  get(displayPath: string): DocumentMetadata | undefined {
    return this.#entries.get(displayPath);
  }

  /**
   * Update one entry without a full rescan (used after ingest-by-value). Reads
   * the same window `refresh` reads, so this answer matches the next rescan's: a
   * frontmatter block that does not close within the window has no metadata on
   * either path.
   */
  set(displayPath: string, text: string): DocumentMetadata | undefined {
    return this.store(displayPath, this.parse(text));
  }

  /** What `set` would record. Ingest calls this before writing, so a bad head costs nothing on disk. */
  parse(text: string): DocumentMetadata | undefined {
    return metadataFromText(firstCodePoints(text, this.#headBytes));
  }

  store(displayPath: string, meta: DocumentMetadata | undefined): DocumentMetadata | undefined {
    if (meta) this.#entries.set(displayPath, meta);
    else this.#entries.delete(displayPath);
    return meta;
  }

  /** Forget one entry without a full rescan (used after a delete). */
  delete(displayPath: string): void {
    this.#entries.delete(displayPath);
  }

  get size(): number {
    return this.#entries.size;
  }

  get refreshedAt(): Date | undefined {
    return this.#refreshedAt;
  }
}
