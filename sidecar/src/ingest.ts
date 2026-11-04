import { lstat, mkdir, realpath, unlink, writeFile } from "node:fs/promises";
import path from "node:path";
import picomatch from "picomatch";
import { HttpError } from "./errors.ts";

/**
 * Ingest and delete by value: the caller names a path, the sidecar changes the
 * file in the collection's directory and queues an index update. The markdown
 * corpus on disk stays the durable artifact; index.sqlite remains derived from
 * it.
 */

export interface ResolvedTarget {
  /** Collection-relative path with forward slashes, as QMD stores it. */
  relative: string;
  /** Absolute filesystem path inside the collection root. */
  absolute: string;
  /** Absolute collection root. */
  root: string;
}

const WINDOWS_DRIVE_RE = /^[a-zA-Z]:/;

/**
 * Validate a caller-supplied relative path against a collection root and
 * glob pattern. Rejects anything that could escape the root or that the
 * collection's pattern would never index (a file QMD ignores is a file that
 * silently never becomes searchable).
 *
 * This is lexical. `assertContained` covers what only the filesystem knows,
 * namely symlinks, and runs before any write or delete.
 *
 * Windows strips trailing dots and spaces from path segments when it opens a
 * file, so `.. /` reaches the filesystem as `../`, and a colon addresses an
 * alternate data stream. Those shapes are refused on every platform so the
 * rules do not depend on the host.
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
  if (segments.some((s) => s.endsWith(".") || s.endsWith(" ") || s.includes(":"))) {
    throw new HttpError(400, "invalid_path", "path segments must not end in a dot or space or contain a colon");
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
  return { relative, absolute, root };
}

function isMissing(err: unknown): boolean {
  const code = (err as NodeJS.ErrnoException).code;
  return code === "ENOENT" || code === "ENOTDIR";
}

/**
 * Refuse a target whose parent directory really lives outside the collection
 * root, as it would behind a symlink or junction. Walks up to the nearest
 * directory that exists, since ingest may be about to create the rest.
 */
async function assertContained(target: ResolvedTarget): Promise<void> {
  let probe = path.dirname(target.absolute);
  let real: string;
  for (;;) {
    try {
      real = await realpath(probe);
      break;
    } catch (err) {
      if (!isMissing(err)) throw err;
      // The root itself is missing, so nothing under it can be a link.
      if (probe === target.root) return;
      probe = path.dirname(probe);
    }
  }
  const rel = path.relative(await realpath(target.root), real);
  if (rel === ".." || rel.startsWith(`..${path.sep}`) || path.isAbsolute(rel)) {
    throw new HttpError(400, "invalid_path", "path resolves outside the collection root");
  }
}

export interface WriteResult {
  bytes: number;
  created: boolean;
}

export async function writeDocument(target: ResolvedTarget, content: string, overwrite: boolean): Promise<WriteResult> {
  await assertContained(target);
  let existed = false;
  try {
    const s = await lstat(target.absolute);
    // writeFile would follow a link at the last segment out of the root.
    if (s.isSymbolicLink()) throw new HttpError(400, "invalid_path", "path refers to a symbolic link");
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

/**
 * Remove a document's file. Idempotent: a path with no file returns false
 * rather than failing, so a retried delete succeeds. A symlink at the last
 * segment is removed itself; `unlink` does not follow it.
 */
export async function removeDocument(target: ResolvedTarget): Promise<boolean> {
  await assertContained(target);
  try {
    const s = await lstat(target.absolute);
    if (s.isDirectory()) throw new HttpError(409, "is_directory", "path refers to a directory");
    await unlink(target.absolute);
    return true;
  } catch (err) {
    if (err instanceof HttpError) throw err;
    if (isMissing(err)) return false;
    throw err;
  }
}
