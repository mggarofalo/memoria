import assert from "node:assert/strict";
import { mkdir, readdir, readFile, stat, utimes, writeFile } from "node:fs/promises";
import path from "node:path";
import { test } from "node:test";
import fastGlob from "fast-glob";
import { HttpError } from "../src/errors.ts";
import { resolveTarget, writeDocument } from "../src/ingest.ts";
import { matchesIgnorePattern, unsupportedIgnorePattern } from "../src/path-rules.ts";
import { authHeaders, makeApp, tempDir } from "./helpers.ts";

const isCode = (code: string) => (err: unknown) => err instanceof HttpError && err.code === code;

// --- exclusive creation ---------------------------------------------------------

test("simultaneous overwrite=false writes of a new path: one wins, the rest get 409, and its content is whole", async () => {
  const root = await tempDir();
  const target = resolveTarget(root, "**/*.md", "race.md");
  // Several rounds, because a single one can pass by luck of scheduling.
  for (let round = 0; round < 10; round++) {
    const results = await Promise.allSettled(Array.from({ length: 16 }, (_, i) => writeDocument(target, `writer ${i}\n`, false)));
    const won = results.filter((r) => r.status === "fulfilled");
    const lost = results.filter((r) => r.status === "rejected");
    if (round === 0) {
      assert.equal(won.length, 1, "exactly one writer creates the document");
      assert.equal((won[0] as PromiseFulfilledResult<{ created: boolean }>).value.created, true);
    } else {
      assert.equal(won.length, 0, "the document exists from round 0 on");
    }
    for (const r of lost) assert.ok(isCode("exists")((r as PromiseRejectedResult).reason), String((r as PromiseRejectedResult).reason));
    assert.match(await readFile(target.absolute, "utf8"), /^writer \d+\n$/);
    assert.deepEqual(await readdir(root), ["race.md"], "no temp file is left behind");
    assert.equal((await stat(target.absolute)).nlink, 1, "the temp name is gone, so the document can be overwritten later");
  }
});

test("fresh paths each get exactly one winner", async () => {
  const root = await tempDir();
  for (let round = 0; round < 20; round++) {
    const target = resolveTarget(root, "**/*.md", `new-${round}.md`);
    const results = await Promise.allSettled(Array.from({ length: 8 }, (_, i) => writeDocument(target, `writer ${i}\n`, false)));
    assert.equal(results.filter((r) => r.status === "fulfilled").length, 1, `round ${round}`);
  }
});

test("through the route: exactly one 202 with created=true, the rest 409 exists", async () => {
  const { app, roots } = await makeApp();
  try {
    const responses = await Promise.all(
      Array.from({ length: 16 }, (_, i) =>
        app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "race.md", content: `# writer ${i}\n`, overwrite: false } }),
      ),
    );
    const accepted = responses.filter((r) => r.statusCode === 202);
    assert.equal(accepted.length, 1, responses.map((r) => r.statusCode).join(","));
    assert.equal(accepted[0]!.json().created, true);
    for (const r of responses.filter((x) => x.statusCode !== 202)) {
      assert.equal(r.statusCode, 409, r.body);
      assert.equal(r.json().error, "exists");
    }
    // The metadata and the file describe the same winner.
    assert.deepEqual(await readdir(roots.claude), ["race.md"]);
  } finally {
    await app.close();
  }
});

test("an existing file created between the check and the link is not replaced", async () => {
  const root = await tempDir();
  const target = resolveTarget(root, "**/*.md", "late.md");
  await assert.rejects(
    writeDocument(target, "mine", false, {
      onStep: async (s) => {
        if (s === "checked") await writeFile(target.absolute, "theirs");
      },
    }),
    isCode("exists"),
  );
  assert.equal(await readFile(target.absolute, "utf8"), "theirs");
  assert.deepEqual(await readdir(root), ["late.md"]);
});

test("a filesystem without hard links gets a clear failure, never a silent replace", async () => {
  const root = await tempDir();
  const target = resolveTarget(root, "**/*.md", "x.md");
  for (const code of ["ENOTSUP", "ENOSYS", "EISDIR"]) {
    const noHardLinks = async () => {
      throw Object.assign(new Error("no hard links"), { code });
    };
    await assert.rejects(writeDocument(target, "body", false, { link: noHardLinks }), (err: unknown) => {
      assert.ok(isCode("exclusive_create_failed")(err));
      assert.equal((err as HttpError).statusCode, 500);
      assert.match((err as Error).message, new RegExp(code));
      return true;
    });
  }
  assert.deepEqual(await readdir(root), [], "nothing was published and the temp file is gone");
  // overwrite=true does not need hard links.
  assert.deepEqual(
    await writeDocument(target, "body", true, {
      link: async () => {
        throw new Error("must not be called");
      },
    }),
    { bytes: 4, created: true },
  );
});

test("a missing directory during the link is a changed path, not a filesystem complaint", async () => {
  const root = await tempDir();
  const target = resolveTarget(root, "**/*.md", "x.md");
  const gone = async () => {
    throw Object.assign(new Error("gone"), { code: "ENOENT" });
  };
  await assert.rejects(writeDocument(target, "body", false, { link: gone }), isCode("path_changed"));
});

// --- stale temp files -------------------------------------------------------------

test("a write sweeps old temp files from its directory, leaving recent ones and other names", async () => {
  const root = await tempDir();
  const old = new Date(Date.now() - 2 * 60 * 60 * 1000);
  const staleName = `.memoria-${"a".repeat(24)}.tmp`;
  const liveName = `.memoria-${"b".repeat(24)}.tmp`;
  const otherName = ".memoria-notes.tmp";
  for (const name of [staleName, liveName, otherName]) await writeFile(path.join(root, name), "x");
  await utimes(path.join(root, staleName), old, old);
  await utimes(path.join(root, otherName), old, old);
  await writeDocument(resolveTarget(root, "**/*.md", "doc.md"), "body", true);
  assert.deepEqual((await readdir(root)).sort(), [liveName, otherName, "doc.md"].sort());
});

test("a crash between link and unlink stops blocking an overwrite once the temp file is stale", async () => {
  const root = await tempDir();
  const { link } = await import("node:fs/promises");
  const target = resolveTarget(root, "**/*.md", "doc.md");
  await writeDocument(target, "one", true);
  const leftover = path.join(root, `.memoria-${"c".repeat(24)}.tmp`);
  await link(target.absolute, leftover);
  await assert.rejects(writeDocument(target, "two", true), isCode("hard_link"));
  const old = new Date(Date.now() - 2 * 60 * 60 * 1000);
  await utimes(leftover, old, old);
  await writeDocument(target, "two", true);
  assert.equal(await readFile(target.absolute, "utf8"), "two");
  assert.deepEqual(await readdir(root), ["doc.md"]);
});

// --- ignore patterns ---------------------------------------------------------------

test("ignored paths return 400 from both routes, write nothing, and queue nothing", async () => {
  const { app, backend, roots } = await makeApp();
  backend.collections[0]!.ignore = ["drafts", "**/private/**", "**/*.secret.md"];
  try {
    for (const p of ["drafts/x.md", "drafts/deep/x.md", "a/private/x.md", "private/x.md", "note.secret.md", "a/note.secret.md"]) {
      const created = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: p, content: "# x\n" } });
      assert.equal(created.statusCode, 400, p);
      assert.equal(created.json().error, "ignored_path", p);
      const deleted = await app.inject({ method: "DELETE", url: `/documents?collection=claude-sessions&path=${encodeURIComponent(p)}`, headers: authHeaders });
      assert.equal(deleted.statusCode, 400, p);
    }
    assert.deepEqual(await readdir(roots.claude), []);
    assert.deepEqual(app.ctx.jobs.list(), []);

    const fine = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "published/x.md", content: "# x\n" } });
    assert.equal(fine.statusCode, 202, fine.body);
    // Each collection has its own list.
    const other = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "codex-sessions", path: "drafts/x.md", content: "# x\n" } });
    assert.equal(other.statusCode, 202, other.body);
  } finally {
    await app.close();
  }
});

test("matching agrees with fast-glob, which is what QMD's scan runs", async () => {
  const root = await tempDir();
  const files = ["top.md", "drafts/a.md", "drafts/deep/b.md", "draftsish/c.md", "x/private/d.md", "private/e.md", "note.secret.md", "x/note.secret.md", "a/b/f.md", "a/g.md", "c/h.md", "p/i.md", "q/j.md", "r/k.md", "1/q.md", "22/q.md", "Mixed/l.md"];
  for (const f of files) {
    await mkdir(path.dirname(path.join(root, f)), { recursive: true });
    await writeFile(path.join(root, f), "x");
  }
  const rules = ["drafts", "drafts/", "drafts/**", "**/drafts/**", "**/private/**", "private", "*.secret.md", "**/*.secret.md", "a/b/*.md", "a/**", "a/*", "./c/*.md", "{p,q}/*.md", "[pq]/*.md", "?/q.md", "*/q.md", "mixed/**", "Mixed/**", "**/*.md", "nomatch", "c\\h.md", "/drafts/**", "/top.md", "./drafts"];
  for (const rule of rules) {
    // The same options as reindexCollection in the SDK's store.js.
    const kept = new Set(await fastGlob("**/*.md", { cwd: root, onlyFiles: true, followSymbolicLinks: false, dot: false, ignore: [rule] }));
    for (const f of files) {
      assert.equal(matchesIgnorePattern(f.split("/"), [rule]), !kept.has(f), `rule ${rule} on ${f}: fast-glob ${kept.has(f) ? "keeps" : "skips"} it`);
    }
  }
});

test("patterns that could backtrack are refused, loudly, and evaluated never", async () => {
  for (const bad of ["+(a|aa)", "**/+(a|aa)*.md", "@(a|b)/x.md", "*(a)", "?(a)", "!(a)", "!drafts", "a*a*a*a*b", "**/*/*/*/*.md"]) {
    assert.equal(unsupportedIgnorePattern(["fine/**", bad]), bad, bad);
    assert.throws(
      () => resolveTarget(path.resolve("root"), "**/*.md", "x.md", ["fine/**", bad]),
      (err: unknown) => isCode("unsupported_ignore_pattern")(err) && (err as HttpError).statusCode === 500,
      bad,
    );
  }
  for (const ok of ["drafts", "**/private/**", "**/*.secret.md", "a/*/b/*.md", "{p,q}/*.md", "[pq]/*.md", "?/q.md", "a*b*c*.md"]) {
    assert.equal(unsupportedIgnorePattern([ok]), undefined, ok);
  }
});

test("the worst pattern allowed stays quick on the longest path a request can send", () => {
  // What the schema's 1024-character cap and the segment limit leave an attacker: deep with short names, or wide.
  const longSegment = `${"a".repeat(250)}.md`;
  const deep = Array.from({ length: 32 }, () => "a");
  const deepAndLong = Array.from({ length: 32 }, () => "a".repeat(30));
  const cases: Array<[string, string[]]> = [
    ["a*a*a*b", [longSegment]],
    ["**/a*a*.nope", [longSegment]],
    ["*a*a*a", ["a".repeat(255)]],
    ["**/a/**/a/**/b", deep],
    ["a/**/a/**/a/**/b", deep],
    ["**/a*/**/b", deep],
    ["**/a*/**/b", deepAndLong],
    ["**/a*a/**/b", deepAndLong],
    ["**/a/**/a/**/b", deepAndLong],
    ["**/*a*.nope", deepAndLong],
    ["**/a*/*.nope", deepAndLong],
    ["**/a/**/a/**", deep],
  ];
  for (const [rule, segments] of cases) {
    assert.equal(unsupportedIgnorePattern([rule]), undefined, `${rule} is meant to be allowed`);
    const started = performance.now();
    matchesIgnorePattern(segments, [rule]);
    const elapsed = performance.now() - started;
    // Generous for a loaded CI host. Before the depth limit, the third wildcard here took 20 seconds.
    assert.ok(elapsed < 1000, `${rule} took ${elapsed.toFixed(0)} ms`);
  }
});

test("a path deeper than the limit is refused before any pattern sees it", () => {
  const root = path.resolve("root");
  const deep = (n: number) => `${Array.from({ length: n - 1 }, () => "d").join("/")}/x.md`;
  assert.ok(resolveTarget(root, "**/*.md", deep(32)));
  assert.throws(() => resolveTarget(root, "**/*.md", deep(33)), isCode("invalid_path"));
});

test("patterns are only ever read from the backend, never from a request", async () => {
  const { app, backend } = await makeApp();
  backend.collections[0]!.ignore = [];
  try {
    const r = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "x.md", content: "# x\n", ignore: ["**"] } });
    assert.equal(r.statusCode, 400, "the schema has no such property");
  } finally {
    await app.close();
  }
});
