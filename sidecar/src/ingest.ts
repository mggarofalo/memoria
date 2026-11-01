import { mkdir, writeFile, stat } from "node:fs/promises";
import path from "node:path";
import picomatch from "picomatch";
import { HttpError } from "./errors.ts";

/**
 * Ingest by value: the caller sends markdown, the sidecar writes it into the
 * collection's directory and queues an index update. The markdown corpus on
 * disk stays the durable artifact; index.sqlite remains derived from it.
 */

export interface ResolvedTarget {
  /** Collection-relative path with forward slashes, as QMD stores it. */
  relative: string;
  /** Absolute filesystem path inside the collection root. */
  absolute: string;
}

const WINDOWS_DRIVE_RE = /^[a-zA-Z]:/;

/**
 * Validate a caller-supplied relative path against a collection root and
 * glob pattern. Rejects anything that could escape the root or that the
 * collection's pattern would never index (a file QMD ignores is a file that
 * silently never becomes searchable).
 */
export function resolveTarget(collectionRoot: string, pattern: string, requestedPath: string): ResolvedTarget {
  const raw = requestedPath.trim();
  if (raw === "") throw new HttpError(400, "invalid_path", "path must not be empty");
  if (raw.includes("\0")) throw new HttpError(400, "invalid_path", "path must not contain NUL");
  const relative = raw.replace(/\\/g, "/").replace(/^\.\//, "");
  if (relative.startsWith("/") || WINDOWS_DRIVE_RE.test(relative) || relative.startsWith("//")) {
    throw new HttpError(400, "invalid_path", "path must be relative to the collection root");
  }
  const segments = relative.split("/");
  if (segments.some((s) => s === "" || s === "." || s === "..")) {
    throw new HttpError(400, "invalid_path", "path must not contain empty, '.' or '..' segments");
  }
  const root = path.resolve(collectionRoot);
  const absolute = path.resolve(root, ...segments);
  const rel = path.relative(root, absolute);
  if (rel === "" || rel.startsWith("..") || path.isAbsolute(rel)) {
    throw new HttpError(400, "invalid_path", "path resolves outside the collection root");
  }
  const matcher = picomatch(pattern || "**/*.md", { dot: false });
  if (!matcher(relative)) {
    throw new HttpError(400, "pattern_mismatch", `path does not match the collection pattern "${pattern}"`);
  }
  return { relative, absolute };
}

export interface WriteResult {
  bytes: number;
  created: boolean;
}

export async function writeDocument(target: ResolvedTarget, content: string, overwrite: boolean): Promise<WriteResult> {
  let existed = false;
  try {
    const s = await stat(target.absolute);
    existed = s.isFile();
    if (s.isDirectory()) throw new HttpError(409, "is_directory", "path refers to a directory");
  } catch (err) {
    if (err instanceof HttpError) throw err;
    if ((err as NodeJS.ErrnoException).code !== "ENOENT") throw err;
  }
  if (existed && !overwrite) {
    throw new HttpError(409, "exists", "document already exists and overwrite is false");
  }
  await mkdir(path.dirname(target.absolute), { recursive: true });
  const bytes = Buffer.byteLength(content, "utf8");
  await writeFile(target.absolute, content, "utf8");
  return { bytes, created: !existed };
}
