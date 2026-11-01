import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { test } from "node:test";
import { bearerMatches } from "../src/auth.ts";
import { HttpError } from "../src/errors.ts";
import { resolveTarget, writeDocument } from "../src/ingest.ts";
import { JobQueue, type ReindexRequest } from "../src/jobs.ts";
import { parseLineRange } from "../src/line-range.ts";
import { tempDir } from "./helpers.ts";

// --- line ranges -------------------------------------------------------------

test("parseLineRange mirrors QMD's suffix grammar", () => {
  assert.deepEqual(parseLineRange("a/b.md"), { lookup: "a/b.md", fromLine: undefined, maxLines: undefined });
  assert.deepEqual(parseLineRange("a/b.md:100"), { lookup: "a/b.md", fromLine: 100, maxLines: undefined });
  assert.deepEqual(parseLineRange("a/b.md:100:40"), { lookup: "a/b.md", fromLine: 100, maxLines: 40 });
  assert.deepEqual(parseLineRange("#abc123:7:2"), { lookup: "#abc123", fromLine: 7, maxLines: 2 });
  assert.deepEqual(parseLineRange("x.md:0"), { lookup: "x.md", fromLine: 1, maxLines: undefined });
  // A Windows-looking path is not a range.
  assert.deepEqual(parseLineRange("C:/x/y.md"), { lookup: "C:/x/y.md", fromLine: undefined, maxLines: undefined });
});

// --- auth ----------------------------------------------------------------------

test("bearerMatches accepts only the exact secret", () => {
  const secret = "0123456789abcdefghij";
  assert.equal(bearerMatches(`Bearer ${secret}`, secret), true);
  assert.equal(bearerMatches(`bearer ${secret}`, secret), true);
  assert.equal(bearerMatches(`Bearer ${secret} `, secret), true);
  assert.equal(bearerMatches(`Bearer ${secret}x`, secret), false);
  assert.equal(bearerMatches(`Bearer ${secret.slice(1)}`, secret), false);
  assert.equal(bearerMatches(secret, secret), false);
  assert.equal(bearerMatches(`Basic ${secret}`, secret), false);
  assert.equal(bearerMatches(undefined, secret), false);
  assert.equal(bearerMatches("", secret), false);
});

// --- ingest path resolution -----------------------------------------------------

test("resolveTarget accepts collection-relative paths that match the pattern", async () => {
  const root = await tempDir();
  const t = resolveTarget(root, "**/*.md", "2026-10-31_abc.md");
  assert.equal(t.relative, "2026-10-31_abc.md");
  assert.equal(t.absolute, path.join(root, "2026-10-31_abc.md"));
  const nested = resolveTarget(root, "**/*.md", "sub\\dir\\note.md");
  assert.equal(nested.relative, "sub/dir/note.md");
  assert.equal(nested.absolute, path.join(root, "sub", "dir", "note.md"));
  assert.equal(resolveTarget(root, "**/*.md", "./x.md").relative, "x.md");
});

test("resolveTarget rejects traversal, absolute paths, and pattern misses", async () => {
  const root = await tempDir();
  const bad = (p: string, code: string) => {
    assert.throws(
      () => resolveTarget(root, "**/*.md", p),
      (err: unknown) => err instanceof HttpError && err.statusCode === 400 && err.code === code,
      p,
    );
  };
  bad("../escape.md", "invalid_path");
  bad("a/../../escape.md", "invalid_path");
  bad("/abs.md", "invalid_path");
  bad("C:/abs.md", "invalid_path");
  bad("\\\\server\\share\\x.md", "invalid_path");
  bad("a//b.md", "invalid_path");
  bad("./.", "invalid_path");
  bad("   ", "invalid_path");
  bad("with\0nul.md", "invalid_path");
  bad("notes.txt", "pattern_mismatch");
  bad(".hidden.md", "pattern_mismatch");
});

test("writeDocument creates, overwrites, and honours overwrite=false", async () => {
  const root = await tempDir();
  const t = resolveTarget(root, "**/*.md", "deep/x.md");
  const first = await writeDocument(t, "one", true);
  assert.deepEqual(first, { bytes: 3, created: true });
  assert.equal(await readFile(t.absolute, "utf8"), "one");
  const second = await writeDocument(t, "twoé", true);
  assert.deepEqual(second, { bytes: 5, created: false });
  await assert.rejects(writeDocument(t, "three", false), (err: unknown) => err instanceof HttpError && err.statusCode === 409);
  const dir = resolveTarget(root, "**/*.md", "deep.md");
  await (await import("node:fs/promises")).mkdir(dir.absolute);
  await assert.rejects(writeDocument(dir, "x", true), (err: unknown) => err instanceof HttpError && err.code === "is_directory");
});

// --- job queue -----------------------------------------------------------------

const req = (over: Partial<ReindexRequest> = {}): ReindexRequest => ({ collections: null, embed: true, force: false, reason: "test", ...over });

test("JobQueue runs jobs one at a time in order and records results", async () => {
  const order: string[] = [];
  let running = 0;
  let maxRunning = 0;
  const queue = new JobQueue(async (r) => {
    running++;
    maxRunning = Math.max(maxRunning, running);
    await new Promise((res) => setTimeout(res, 5));
    order.push(r.reason);
    running--;
    return { update: { collections: 1, indexed: 0, updated: 0, unchanged: 0, removed: 0, skipped: 0, needsEmbedding: 0 }, embed: null, metadata: { documents: 0, withMetadata: 0 } };
  });
  const a = queue.enqueue(req({ reason: "a", collections: ["x"] }));
  const b = queue.enqueue(req({ reason: "b", collections: ["y"] }));
  assert.equal(queue.pending, 2);
  assert.equal(a.state, "queued");
  await queue.idle();
  assert.deepEqual(order, ["a", "b"]);
  assert.equal(maxRunning, 1);
  assert.equal(a.state, "succeeded");
  assert.ok(a.startedAt && a.finishedAt && a.result);
  assert.equal(b.state, "succeeded");
  assert.equal(queue.pending, 0);
  assert.deepEqual(queue.list().map((j) => j.id), [b.id, a.id]);
  assert.equal(queue.get(a.id), a);
});

test("JobQueue coalesces identical queued jobs and records failures", async () => {
  let calls = 0;
  const queue = new JobQueue(async (r) => {
    calls++;
    await new Promise((res) => setTimeout(res, 5));
    if (r.reason === "boom") throw new Error("kaboom");
    return { update: { collections: 0, indexed: 0, updated: 0, unchanged: 0, removed: 0, skipped: 0, needsEmbedding: 0 }, embed: null, metadata: { documents: 0, withMetadata: 0 } };
  });
  queue.enqueue(req({ reason: "first" })); // starts running immediately
  const second = queue.enqueue(req({ collections: ["c"], reason: "second" }));
  const dup = queue.enqueue(req({ collections: ["c"], reason: "dup" }));
  assert.equal(dup, second);
  const different = queue.enqueue(req({ collections: ["c"], embed: false, reason: "different" }));
  assert.notEqual(different, second);
  const failing = queue.enqueue(req({ reason: "boom", force: true }));
  await queue.idle();
  assert.equal(calls, 4);
  assert.equal(failing.state, "failed");
  assert.equal(failing.error, "kaboom");
});

test("JobQueue trims finished history to the cap", async () => {
  const queue = new JobQueue(async () => ({ update: { collections: 0, indexed: 0, updated: 0, unchanged: 0, removed: 0, skipped: 0, needsEmbedding: 0 }, embed: null, metadata: { documents: 0, withMetadata: 0 } }), 3);
  for (let i = 0; i < 6; i++) {
    queue.enqueue(req({ reason: `j${i}`, collections: [`c${i}`] }));
    await queue.idle();
  }
  assert.equal(queue.list().length, 3);
  assert.deepEqual(queue.list().map((j) => j.request.reason), ["j5", "j4", "j3"]);
});
