import { randomBytes } from "node:crypto";
import { constants, type Stats } from "node:fs";
import { link, lstat, mkdir, open, readdir, realpath, rename, unlink } from "node:fs/promises";
import path from "node:path";
import picomatch from "picomatch";
import { HttpError } from "./errors.ts";
import { MAX_PATH_SEGMENTS, foldName, matchesIgnorePattern, segmentProblem, unindexableProblem, unsupportedIgnorePattern } from "./path-rules.ts";

/**
 * Ingest and delete by value: the caller names a path, the sidecar changes the
 * file in the collection's directory and queues an index update. The markdown
 * corpus on disk stays the durable artifact; index.sqlite remains derived from
 * it.
 *
 * Writes go to a temp file in the target's own directory and are renamed into
 * place, which matters for three reasons. The rename replaces a hard link or
 * symlink instead of writing through it. A reader never sees half a document.
 * And the source and destination share a parent path, so swapping a directory
 * for a junction after the last check leaves the source missing and the rename
 * fails. That only holds if the temp file really is inside the root, so its real
 * location is checked as well as the directory's.
 *
 * What this does not close: Node has no `openat` or `renameat`, so paths are
 * resolved again on each call. An attacker who can write to the collection
 * root can still win the very short gap inside the rename itself, between the
 * kernel resolving the source and resolving the destination. A swap at the
 * moment the temp file is created can leave an empty file outside the root. A
 * delete's rename aside can land on a file outside the root; it is put back when
 * the swap is still in place, and otherwise is left under a `.memoria-*.tmp`
 * name. A local user with write access to a collection root is outside the threat
 * this defends (docs/deployment.md).
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

function invalidPath(message: string): HttpError {
  return new HttpError(400, "invalid_path", message);
}

/** Forward slashes, no leading `./`, and every segment within the name rules. */
function normalizeRelative(requestedPath: string): string {
  if (requestedPath === "") throw invalidPath("path must not be empty");
  const relative = requestedPath.replace(/\\/g, "/").replace(/^\.\//, "");
  if (relative.startsWith("/") || WINDOWS_DRIVE_RE.test(relative)) {
    throw invalidPath("path must be relative to the collection root");
  }
  const segments = relative.split("/");
  if (segments.length > MAX_PATH_SEGMENTS) throw invalidPath(`path must not have more than ${MAX_PATH_SEGMENTS} segments`);
  for (const segment of segments) {
    const problem = segmentProblem(segment);
    if (problem) throw invalidPath(problem);
  }
  return relative;
}

/**
 * Validate a caller-supplied relative path against a collection root and
 * glob pattern. Rejects anything that could escape the root, that a supported
 * filesystem would treat as a different name, or that QMD would never index (a
 * file QMD ignores is a file that silently never becomes searchable).
 *
 * This is lexical and gives the same answer on every platform. `assertSafePath`
 * covers what only the filesystem knows, and runs before any write or delete.
 * `ignore` is the collection's own ignore list from QMD's configuration.
 */
export function resolveTarget(collectionRoot: string, pattern: string, requestedPath: string, ignore: readonly string[] = []): ResolvedTarget {
  const relative = normalizeRelative(requestedPath);
  const segments = relative.split("/");
  const root = path.resolve(collectionRoot);
  const absolute = path.resolve(root, ...segments);
  const rel = path.relative(root, absolute);
  if (rel === "" || rel.startsWith("..") || path.isAbsolute(rel)) {
    throw invalidPath("path resolves outside the collection root");
  }
  const matcher = picomatch(pattern || "**/*.md", { dot: false });
  if (!matcher(relative)) {
    throw new HttpError(400, "pattern_mismatch", `path does not match the collection pattern "${pattern}"`);
  }
  const unindexable = unindexableProblem(segments);
  if (unindexable) throw invalidPath(unindexable);
  assertNotIgnored(segments, ignore);
  return { relative, absolute, root };
}

function assertNotIgnored(segments: readonly string[], ignore: readonly string[]): void {
  const unsupported = unsupportedIgnorePattern(ignore);
  if (unsupported !== undefined) {
    throw new HttpError(500, "unsupported_ignore_pattern", `this collection's ignore pattern "${unsupported}" uses syntax the sidecar will not evaluate against request paths: extglobs, a leading "!", or more than three wildcards. Simplify it in QMD's configuration`);
  }
  if (matchesIgnorePattern(segments, ignore)) {
    throw new HttpError(400, "ignored_path", "path matches one of this collection's ignore patterns, so QMD would never index it");
  }
}

function codeOf(err: unknown): string | undefined {
  return (err as NodeJS.ErrnoException).code;
}

function isMissing(err: unknown): boolean {
  const code = codeOf(err);
  return code === "ENOENT" || code === "ENOTDIR";
}

async function lstatIfPresent(file: string): Promise<Stats | undefined> {
  try {
    return await lstat(file);
  } catch (err) {
    if (isMissing(err)) return undefined;
    throw err;
  }
}

async function namesIn(dir: string): Promise<string[] | undefined> {
  try {
    return await readdir(dir);
  } catch (err) {
    if (isMissing(err)) return undefined;
    throw err;
  }
}

/**
 * Refuse a path that differs only in case or normalisation from what is on
 * disk. On NTFS and APFS it would reach the existing file under a name QMD and
 * the metadata index do not know. The rule applies on case-sensitive hosts too,
 * so a corpus never holds names that collide when it moves.
 */
async function assertNoCaseAlias(target: ResolvedTarget): Promise<void> {
  let dir = target.root;
  for (const segment of target.relative.split("/")) {
    const names = await namesIn(dir);
    if (names === undefined) return;
    if (!names.includes(segment)) {
      const folded = foldName(segment);
      if (names.some((name) => foldName(name) === folded)) {
        throw new HttpError(409, "case_conflict", "path differs only in case or normalization from an existing entry; use the existing name");
      }
      return;
    }
    dir = path.join(dir, segment);
  }
}

/**
 * Refuse a link anywhere between the root and the file. Links that stay inside
 * the root are refused as well: QMD's scan does not follow them, so a document
 * behind one would never be indexed.
 */
async function assertNoLinkedParent(target: ResolvedTarget): Promise<void> {
  let dir = target.root;
  for (const segment of target.relative.split("/").slice(0, -1)) {
    dir = path.join(dir, segment);
    const stats = await lstatIfPresent(dir);
    if (stats === undefined) return;
    if (stats.isSymbolicLink()) throw invalidPath("path resolves outside the collection root");
  }
}

/** The real path of the target's nearest existing parent, or undefined when even the root is missing. */
async function nearestRealParent(target: ResolvedTarget): Promise<string | undefined> {
  let probe = path.dirname(target.absolute);
  for (;;) {
    try {
      return await realpath(probe);
    } catch (err) {
      if (!isMissing(err)) throw err;
      if (probe === target.root) return undefined;
      probe = path.dirname(probe);
    }
  }
}

async function assertRealPathInRoot(target: ResolvedTarget, real: string): Promise<void> {
  const rel = path.relative(await realpath(target.root), real);
  if (rel === ".." || rel.startsWith(`..${path.sep}`) || path.isAbsolute(rel)) {
    throw invalidPath("path resolves outside the collection root");
  }
}

/** Refuse a target whose parent directory really lives outside the collection root. */
async function assertContained(target: ResolvedTarget): Promise<void> {
  await assertNoLinkedParent(target);
  const real = await nearestRealParent(target);
  // The root itself is missing, so nothing under it can be a link.
  if (real !== undefined) await assertRealPathInRoot(target, real);
}

/**
 * Where the scratch file really is, from one path resolution. Checking the
 * directory is not enough: a directory swapped only while the scratch file was
 * created, then swapped back for the check, leaves it outside the root, and a
 * second swap before the rename would land it on a file there. A scratch file
 * outside the root shows up here as outside when the swap is in place, and as
 * missing when it is not.
 */
async function assertScratchInRoot(target: ResolvedTarget, scratch: string): Promise<void> {
  await assertRealPathInRoot(target, await realpath(scratch));
}

async function assertSafePath(target: ResolvedTarget): Promise<void> {
  await assertNoCaseAlias(target);
  await assertContained(target);
}

const TRANSIENT_CODES = new Set(["EPERM", "EACCES", "EBUSY"]);
const RETRY_DELAYS_MS = [10, 40, 160];

/**
 * Windows refuses to rename over a file that another process, such as a virus
 * scanner or QMD's own scan, has open for a moment. A plain write mostly did
 * not hit this; a rename does.
 */
export async function retryTransient<T>(operation: () => Promise<T>, delaysMs: readonly number[] = RETRY_DELAYS_MS): Promise<T> {
  for (const delayMs of delaysMs) {
    try {
      return await operation();
    } catch (err) {
      if (!TRANSIENT_CODES.has(codeOf(err) ?? "")) throw err;
      await new Promise((resolve) => setTimeout(resolve, delayMs));
    }
  }
  return operation();
}

const SCRATCH_NAME = /^\.memoria-[0-9a-f]{24}\.tmp$/;
const STALE_SCRATCH_MS = 60 * 60 * 1000;

/** A hidden name, so QMD's scan skips it even if a crash leaves it behind. */
function scratchPath(target: ResolvedTarget): string {
  return path.join(path.dirname(target.absolute), `.memoria-${randomBytes(12).toString("hex")}.tmp`);
}

function pathChanged(): HttpError {
  return new HttpError(409, "path_changed", "a directory on the path changed during the operation; nothing was committed");
}

/** Points in a write or delete where tests can change the filesystem underneath it. */
export type IngestStep = "ready" | "opened" | "written" | "checked" | "renamed" | "moved";

export interface IngestHooks {
  onStep?: (step: IngestStep) => Promise<void>;
  /** Stands in for `fs.link`, so tests can act as a filesystem with no hard links. */
  link?: (existing: string, created: string) => Promise<void>;
}

export interface WriteResult {
  bytes: number;
  created: boolean;
}

/** What is at the target now, and whether it may be replaced. Returns undefined when nothing is there. */
async function inspectExisting(target: ResolvedTarget, overwrite: boolean): Promise<Stats | undefined> {
  const stats = await lstatIfPresent(target.absolute);
  if (stats === undefined) return undefined;
  if (stats.isDirectory()) throw new HttpError(409, "is_directory", "path refers to a directory");
  if (!stats.isFile()) throw invalidPath("path refers to a symbolic link or special file");
  if (!overwrite) throw new HttpError(409, "exists", "document already exists and overwrite is false");
  // The rename would be safe, but it would silently split a file that something else also uses.
  if (stats.nlink > 1) throw new HttpError(409, "hard_link", "path refers to a file with more than one hard link");
  return stats;
}

/** Create the parent directories one at a time, refusing a link or file at any level. */
async function ensureParents(target: ResolvedTarget): Promise<void> {
  await mkdir(target.root, { recursive: true });
  let dir = target.root;
  for (const segment of target.relative.split("/").slice(0, -1)) {
    dir = path.join(dir, segment);
    await mkdir(dir).catch((err: unknown) => {
      if (codeOf(err) !== "EEXIST") throw err;
    });
    const stats = await lstat(dir);
    if (stats.isSymbolicLink()) throw invalidPath("path resolves outside the collection root");
    if (!stats.isDirectory()) throw new HttpError(409, "not_a_directory", "a parent of the path is a file");
  }
}

// O_EXCL already refuses a link at the last segment. O_NOFOLLOW states it twice where it exists; Windows has no such flag.
const CREATE_FLAGS = constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | (constants.O_NOFOLLOW ?? 0);

/**
 * Put the finished temp file at the target. A rename replaces whatever is there,
 * so `overwrite: false` links instead, which fails if the name is taken. That is
 * atomic on NTFS and POSIX filesystems. There is no fallback where hard links are
 * unavailable: quietly replacing a document is what the caller asked not to happen.
 */
async function publish(target: ResolvedTarget, scratch: string, overwrite: boolean, hooks: IngestHooks): Promise<void> {
  if (overwrite) {
    await rename(scratch, target.absolute);
    return;
  }
  await (hooks.link ?? link)(scratch, target.absolute);
  // The document is in place. A leftover temp name is harmless and the sweep removes it.
  await unlink(scratch).catch(() => undefined);
}

function exclusiveCreateFailure(err: unknown): unknown {
  if (err instanceof HttpError || isMissing(err)) return err;
  if (codeOf(err) === "EEXIST") return new HttpError(409, "exists", "document already exists and overwrite is false");
  return new HttpError(500, "exclusive_create_failed", `overwrite=false needs a hard link in the collection directory, and creating one failed (${codeOf(err) ?? "unknown error"}). This filesystem may not support them; retry with overwrite=true if replacing an existing document is acceptable`);
}

async function commit(target: ResolvedTarget, scratch: string, overwrite: boolean, hooks: IngestHooks): Promise<void> {
  try {
    await retryTransient(async () => {
      await assertSafePath(target);
      await assertScratchInRoot(target, scratch);
      await hooks.onStep?.("checked");
      await publish(target, scratch, overwrite, hooks);
    });
  } catch (err) {
    throw overwrite ? err : exclusiveCreateFailure(err);
  }
}

/**
 * Remove temp files a crash left in the target's directory. QMD never sees them,
 * since their names are hidden, so this is only tidiness, except that a crash
 * between link and unlink leaves a document looking hard-linked, which would
 * block overwriting it. The age limit keeps it off another request's live temp
 * file. Best effort: a failure here must not fail the write.
 */
async function sweepStaleScratch(target: ResolvedTarget): Promise<void> {
  const dir = path.dirname(target.absolute);
  const names = await namesIn(dir).catch(() => undefined);
  for (const name of names?.filter((n) => SCRATCH_NAME.test(n)) ?? []) {
    const file = path.join(dir, name);
    await removeIfStale(target, file).catch(() => undefined);
  }
}

async function removeIfStale(target: ResolvedTarget, file: string): Promise<void> {
  const stats = await lstat(file);
  if (!stats.isFile() || Date.now() - stats.mtimeMs < STALE_SCRATCH_MS) return;
  await assertScratchInRoot(target, file);
  await unlink(file);
}

async function writeAndCommit(target: ResolvedTarget, scratch: string, content: string, mode: number, overwrite: boolean, hooks: IngestHooks): Promise<void> {
  await hooks.onStep?.("ready");
  // The file stays empty until it is known to be inside the root, so a swapped directory receives no content.
  const handle = await open(scratch, CREATE_FLAGS, mode);
  try {
    await hooks.onStep?.("opened");
    await assertContained(target);
    await assertScratchInRoot(target, scratch);
    await handle.writeFile(content, "utf8");
    await handle.sync();
  } finally {
    await handle.close();
  }
  await hooks.onStep?.("written");
  await commit(target, scratch, overwrite, hooks);
  await hooks.onStep?.("renamed");
  // Too late to undo, but a caller must not be told a swapped write went where it asked.
  await assertContained(target);
}

export async function writeDocument(target: ResolvedTarget, content: string, overwrite: boolean, hooks: IngestHooks = {}): Promise<WriteResult> {
  // QMD skips a blank file without touching its row, so the old text would stay searchable.
  if (content.trim() === "") throw new HttpError(400, "empty_content", "content must not be blank; QMD does not index blank documents");
  await assertSafePath(target);
  await sweepStaleScratch(target);
  const existing = await inspectExisting(target, overwrite);
  await ensureParents(target);
  await assertContained(target);
  const scratch = scratchPath(target);
  // An overwrite keeps the old permission bits, so it cannot widen access to the document.
  const mode = existing ? existing.mode & 0o777 : 0o666;
  try {
    await writeAndCommit(target, scratch, content, mode, overwrite, hooks);
  } catch (err) {
    await unlink(scratch).catch(() => undefined);
    throw isMissing(err) ? pathChanged() : err;
  }
  return { bytes: Buffer.byteLength(content, "utf8"), created: existing === undefined };
}

/**
 * Remove a document's file. Idempotent: a path with no file returns false
 * rather than failing, so a retried delete succeeds. A link at the last segment
 * is removed itself, and removing one hard link leaves the file's other names
 * alone.
 *
 * The file is renamed aside first and containment is checked again before it
 * is unlinked. If a directory was swapped, the rename can be undone, where an
 * unlink could not.
 */
export async function removeDocument(target: ResolvedTarget, hooks: IngestHooks = {}): Promise<boolean> {
  await assertSafePath(target);
  const stats = await lstatIfPresent(target.absolute);
  if (stats === undefined) return false;
  if (stats.isDirectory()) throw new HttpError(409, "is_directory", "path refers to a directory");

  const scratch = scratchPath(target);
  await hooks.onStep?.("checked");
  try {
    await retryTransient(() => rename(target.absolute, scratch));
  } catch (err) {
    if (isMissing(err)) return false;
    throw err;
  }
  await hooks.onStep?.("moved");
  try {
    await assertContained(target);
    // A link's real path is wherever it points, which says nothing about where the link itself is.
    await (stats.isSymbolicLink() ? lstat(scratch) : assertScratchInRoot(target, scratch));
    await retryTransient(() => unlink(scratch));
  } catch (err) {
    await rename(scratch, target.absolute).catch(() => undefined);
    throw isMissing(err) ? pathChanged() : err;
  }
  return true;
}
