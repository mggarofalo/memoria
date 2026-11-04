import assert from "node:assert/strict";
import { mkdir, readFile, symlink, writeFile } from "node:fs/promises";
import path from "node:path";
import { test } from "node:test";
import { bearerMatches } from "../src/auth.ts";
import { HttpError } from "../src/errors.ts";
import { removeDocument, resolveTarget, writeDocument } from "../src/ingest.ts";
import { JobQueue, type JobResult, type ReindexRequest } from "../src/jobs.ts";
import { parseLineRange } from "../src/line-range.ts";
import { multiGetSelectors } from "../src/multi-get.ts";
import { ManualScheduler, tempDir } from "./helpers.ts";

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
  // Windows strips trailing dots and spaces, so these reach the filesystem as `../`.
  bad("a/.. /.. /escape.md", "invalid_path");
  bad("a/../escape.md", "invalid_path");
  bad("a/.../escape.md", "invalid_path");
  bad("dir./x.md", "invalid_path");
  bad("x.md:stream", "invalid_path");
  bad("a/C:/x.md", "invalid_path");
  bad("notes.txt", "pattern_mismatch");
  bad(".hidden.md", "pattern_mismatch");
  bad(".git/config.md", "pattern_mismatch");
});

test("removeDocument deletes the file and is idempotent on anything missing", async () => {
  const root = await tempDir();
  const t = resolveTarget(root, "**/*.md", "deep/x.md");
  await writeDocument(t, "one", true);
  assert.equal(await removeDocument(t), true);
  await assert.rejects(readFile(t.absolute), /ENOENT/);
  assert.equal(await removeDocument(t), false);
  assert.equal(await removeDocument(resolveTarget(root, "**/*.md", "never/existed/y.md")), false);
  // A file where a directory is expected is also "no such document".
  await writeFile(path.join(root, "file.md"), "keep");
  assert.equal(await removeDocument(resolveTarget(root, "**/*.md", "file.md/z.md")), false);
  assert.equal(await readFile(path.join(root, "file.md"), "utf8"), "keep");
  // The root itself may not exist yet.
  assert.equal(await removeDocument(resolveTarget(path.join(root, "not-yet"), "**/*.md", "a/b.md")), false);
});

test("removeDocument refuses a directory", async () => {
  const root = await tempDir();
  await mkdir(path.join(root, "folder.md"));
  await assert.rejects(removeDocument(resolveTarget(root, "**/*.md", "folder.md")), (err: unknown) => err instanceof HttpError && err.code === "is_directory");
});

test("delete and ingest cannot follow a link out of the collection root", async () => {
  const root = await tempDir();
  const outside = await tempDir();
  await writeFile(path.join(outside, "victim.md"), "keep");
  // A junction on Windows needs no privilege; the type is ignored elsewhere.
  await symlink(outside, path.join(root, "linked"), "junction");
  const escapes = (err: unknown) => err instanceof HttpError && err.statusCode === 400 && err.code === "invalid_path";

  const victim = resolveTarget(root, "**/*.md", "linked/victim.md");
  await assert.rejects(removeDocument(victim), escapes);
  await assert.rejects(writeDocument(victim, "overwritten", true), escapes);
  await assert.rejects(writeDocument(resolveTarget(root, "**/*.md", "linked/new/dir/x.md"), "x", true), escapes);
  assert.equal(await readFile(path.join(outside, "victim.md"), "utf8"), "keep");
});

test("a symlink at the last segment is removed itself and never written through", async (t) => {
  const root = await tempDir();
  const outside = await tempDir();
  const victim = path.join(outside, "victim.md");
  await writeFile(victim, "keep");
  const link = path.join(root, "link.md");
  try {
    await symlink(victim, link, "file");
  } catch (err) {
    // Unprivileged Windows accounts cannot create file symlinks.
    if ((err as NodeJS.ErrnoException).code === "EPERM") return t.skip("no symlink privilege");
    throw err;
  }
  const target = resolveTarget(root, "**/*.md", "link.md");
  await assert.rejects(writeDocument(target, "overwritten", true), (err: unknown) => err instanceof HttpError && err.code === "invalid_path");
  assert.equal(await readFile(victim, "utf8"), "keep");
  assert.equal(await removeDocument(target), true);
  assert.equal(await readFile(victim, "utf8"), "keep");
  await assert.rejects(readFile(link), /ENOENT/);
});

// --- multi-get selectors --------------------------------------------------------

test("multiGetSelectors keeps globs apart from a reference list", () => {
  assert.deepEqual(multiGetSelectors("a/*.md", undefined), ["a/*.md"]);
  assert.deepEqual(multiGetSelectors(undefined, ["2f2681"]), ["2f2681,"]);
  assert.deepEqual(multiGetSelectors(undefined, ["a/x.md", "2f2681"]), ["a/x.md,2f2681,"]);
  assert.deepEqual(multiGetSelectors("codex-sessions/2026-10-29*.md", ["2f2681"]), ["codex-sessions/2026-10-29*.md", "2f2681,"]);
  // The mixed string the SDK cannot read on its own.
  assert.deepEqual(multiGetSelectors("codex-sessions/2026-10-29*.md,2f2681", undefined), ["codex-sessions/2026-10-29*.md", "2f2681,"]);
  // Commas inside a glob belong to it.
  assert.deepEqual(multiGetSelectors("{a,b}/*.md,c/[xy,z].md", undefined), ["{a,b}/*.md", "c/[xy,z].md"]);
  assert.deepEqual(multiGetSelectors("a.md, b.md,,", ["b.md", " "]), ["a.md,b.md,"]);
  assert.deepEqual(multiGetSelectors(undefined, undefined), []);
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
  const queue = new JobQueue(async () => ({ update: { collections: 0, indexed: 0, updated: 0, unchanged: 0, removed: 0, skipped: 0, needsEmbedding: 0 }, embed: null, metadata: { documents: 0, withMetadata: 0 } }), { keep: 3 });
  for (let i = 0; i < 6; i++) {
    queue.enqueue(req({ reason: `j${i}`, collections: [`c${i}`] }));
    await queue.idle();
  }
  assert.equal(queue.list().length, 3);
  assert.deepEqual(queue.list().map((j) => j.request.reason), ["j5", "j4", "j3"]);
});

// --- debounce ------------------------------------------------------------------

const emptyResult: JobResult = { update: { collections: 0, indexed: 0, updated: 0, unchanged: 0, removed: 0, skipped: 0, needsEmbedding: 0 }, embed: null, metadata: { documents: 0, withMetadata: 0 } };

/** A queue on a manual clock that records each run and can be held mid-run. */
function debouncedQueue(options: { debounceMs?: number; maxWaitMs?: number } = {}) {
  const scheduler = new ManualScheduler();
  const runs: ReindexRequest[] = [];
  let running = 0;
  const state = { maxRunning: 0, gate: null as Promise<void> | null };
  const queue = new JobQueue(
    async (r) => {
      runs.push({ ...r });
      running++;
      state.maxRunning = Math.max(state.maxRunning, running);
      if (state.gate) await state.gate;
      running--;
      return emptyResult;
    },
    { debounceMs: 60_000, maxWaitMs: 300_000, ...options, scheduler },
  );
  return { queue, scheduler, runs, state };
}

const doc = (name: string, over: Partial<ReindexRequest> = {}): ReindexRequest => req({ collections: ["c"], reason: name, ...over });

test("a burst of debounced requests becomes one job that waits for quiet", async () => {
  const { queue, scheduler, runs } = debouncedQueue();
  const first = queue.enqueue(doc("d0"), { debounce: true });
  for (let i = 1; i < 10; i++) {
    await scheduler.advance(10_000);
    assert.equal(queue.enqueue(doc(`d${i}`), { debounce: true }), first);
  }
  assert.equal(first.state, "queued");
  assert.equal(first.request.reason, "d0 (+9 more)");
  assert.equal(queue.pending, 1);
  assert.equal(queue.list().length, 1);

  // Each request restarted the quiet period; 59 s after the last one, still held.
  await scheduler.advance(59_000);
  assert.equal(runs.length, 0);
  await scheduler.advance(1_000);
  await queue.idle();
  assert.equal(runs.length, 1);
  assert.equal(first.state, "succeeded");
  assert.equal(scheduler.pendingTimers, 0);
});

test("steady requests cannot hold a job past the max wait", async () => {
  const { queue, scheduler, runs } = debouncedQueue({ debounceMs: 60_000, maxWaitMs: 150_000 });
  const first = queue.enqueue(doc("d0"), { debounce: true });
  await scheduler.advance(50_000);
  queue.enqueue(doc("d1"), { debounce: true });
  await scheduler.advance(50_000);
  queue.enqueue(doc("d2"), { debounce: true });
  await scheduler.advance(40_000); // 140 s in, under both limits
  queue.enqueue(doc("d3"), { debounce: true });
  assert.equal(runs.length, 0);
  await scheduler.advance(10_000); // 150 s: only 10 s after d3, but at the deadline
  await queue.idle();
  assert.equal(runs.length, 1);
  assert.equal(first.state, "succeeded");
});

test("a request during a run gets exactly one follow-up, and never overlaps it", async () => {
  const { queue, scheduler, runs, state } = debouncedQueue();
  let open = () => {};
  state.gate = new Promise((resolve) => (open = resolve));

  const first = queue.enqueue(doc("d0"), { debounce: true });
  await scheduler.advance(60_000);
  assert.equal(first.state, "running");

  const second = queue.enqueue(doc("d1"), { debounce: true });
  assert.notEqual(second, first);
  assert.equal(queue.enqueue(doc("d2"), { debounce: true }), second);
  // The follow-up's window closes while the first run is still going: it waits.
  await scheduler.advance(60_000);
  assert.equal(second.state, "queued");
  assert.equal(runs.length, 1);

  state.gate = null;
  open();
  await queue.idle();
  assert.equal(runs.length, 2);
  assert.equal(state.maxRunning, 1);
  assert.equal(second.state, "succeeded");
  assert.equal(queue.list().length, 2);
});

test("debounced jobs are scoped by collection and force; embed widens rather than splits", async () => {
  const { queue, scheduler, runs } = debouncedQueue();
  const a = queue.enqueue(doc("a", { embed: false }), { debounce: true });
  const b = queue.enqueue(doc("b", { collections: ["other"] }), { debounce: true });
  assert.notEqual(a, b);
  assert.equal(queue.enqueue(doc("a2", { embed: true }), { debounce: true }), a);
  assert.equal(a.request.embed, true);
  // Once widened it stays widened.
  queue.enqueue(doc("a3", { embed: false }), { debounce: true });
  assert.equal(a.request.embed, true);
  const forced = queue.enqueue(doc("f", { force: true }), { debounce: true });
  assert.notEqual(forced, a);
  await scheduler.advance(60_000);
  await queue.idle();
  assert.deepEqual(runs.map((r) => `${r.collections} embed=${r.embed} force=${r.force}`).sort(), ["c embed=true force=false", "c embed=true force=true", "other embed=true force=false"]);
});

test("an immediate request does not wait, and releases an identical held job", async () => {
  const { queue, scheduler, runs } = debouncedQueue();
  const held = queue.enqueue(doc("held"), { debounce: true });
  const other = queue.enqueue(doc("now", { collections: ["other"] }));
  await scheduler.advance(0);
  assert.equal(other.state, "succeeded");
  assert.equal(held.state, "queued");

  assert.equal(queue.enqueue(doc("flush me")), held);
  await queue.idle();
  assert.equal(held.state, "succeeded");
  assert.equal(scheduler.pendingTimers, 0);
  assert.deepEqual(runs.map((r) => r.collections), [["other"], ["c"]]);
});

test("debounce of 0 makes debounced requests eligible at once but still coalesces", async () => {
  const { queue, runs } = debouncedQueue({ debounceMs: 0 });
  const a = queue.enqueue(doc("a"), { debounce: true });
  assert.equal(queue.enqueue(doc("b"), { debounce: true }), a);
  await queue.idle();
  assert.equal(runs.length, 1);
});

test("flush releases held jobs; drain waits for them, up to its timeout", async () => {
  const { queue, scheduler, runs, state } = debouncedQueue();
  queue.enqueue(doc("a"), { debounce: true });
  queue.enqueue(doc("b", { collections: ["other"] }), { debounce: true });
  assert.equal(await queue.drain(1_000), true);
  assert.equal(runs.length, 2);
  assert.equal(scheduler.pendingTimers, 0);
  assert.equal(await queue.drain(1_000), true, "an empty queue drains at once");

  let open = () => {};
  state.gate = new Promise((resolve) => (open = resolve));
  queue.enqueue(doc("c"), { debounce: true });
  const draining = queue.drain(1_000);
  await scheduler.advance(1_000);
  assert.equal(await draining, false);
  assert.equal(queue.pending, 1);
  state.gate = null;
  open();
  await queue.idle();
});

test("a failed debounced job is recorded and the next one still runs", async () => {
  const scheduler = new ManualScheduler();
  const queue = new JobQueue(
    async (r) => {
      if (r.reason.startsWith("boom")) throw new Error("kaboom");
      return emptyResult;
    },
    { debounceMs: 1_000, scheduler },
  );
  const bad = queue.enqueue(doc("boom"), { debounce: true });
  const good = queue.enqueue(doc("fine", { collections: ["other"] }), { debounce: true });
  await scheduler.advance(1_000);
  await queue.idle();
  assert.equal(bad.state, "failed");
  assert.equal(bad.error, "kaboom");
  assert.equal(good.state, "succeeded");
});
