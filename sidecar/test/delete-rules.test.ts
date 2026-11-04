import assert from "node:assert/strict";
import { mkdir, readdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { test } from "node:test";
import { HttpError } from "../src/errors.ts";
import { resolveDeleteTarget } from "../src/ingest.ts";
import { deleteSegmentProblem } from "../src/path-rules.ts";
import { authHeaders, makeApp, type TestApp } from "./helpers.ts";

/** DELETE takes safety checks only: anything on disk stays removable, because removing leaked content is its job. */

const del = (app: TestApp["app"], p: string) => app.inject({ method: "DELETE", url: `/documents?collection=claude-sessions&path=${encodeURIComponent(p)}`, headers: authHeaders });

async function plant(root: string, relative: string): Promise<string> {
  const file = path.join(root, ...relative.split("/"));
  await mkdir(path.dirname(file), { recursive: true });
  await writeFile(file, "leaked");
  return file;
}

async function assertDeletes(t: TestApp, relative: string): Promise<void> {
  await plant(t.roots.claude, relative);
  const r = await del(t.app, relative);
  assert.equal(r.statusCode, 202, `${JSON.stringify(relative)}: ${r.body}`);
  assert.equal(r.json().deleted, true, JSON.stringify(relative));
  assert.equal(r.json().job.request.collections[0], "claude-sessions");
  await assert.rejects(readFile(path.join(t.roots.claude, ...relative.split("/"))), /ENOENT/, JSON.stringify(relative));
}

test("indexability rules do not stop a delete", async () => {
  const t = await makeApp();
  t.backend.collections[0]!.ignore = ["drafts", "**/private/**"];
  try {
    for (const p of ["notes.txt", "no-longer-matches.markdown", "drafts/a.md", "x/private/b.md", "node_modules/x.md", "vendor/x.md", "dist/x.md", "build/x.md", "a/vendor/y.md", ".hidden.md", ".git/config", ".cache/x.md"]) {
      await assertDeletes(t, p);
    }
  } finally {
    await t.app.close();
  }
});

test("POST is still refused for each of those, so the two routes really differ", async () => {
  const t = await makeApp();
  t.backend.collections[0]!.ignore = ["drafts"];
  try {
    for (const [p, code] of [["notes.txt", "pattern_mismatch"], ["drafts/a.md", "ignored_path"], ["node_modules/x.md", "invalid_path"]] as const) {
      const r = await t.app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: p, content: "# x\n" } });
      assert.equal(r.statusCode, 400, p);
      assert.equal(r.json().error, code, p);
    }
  } finally {
    await t.app.close();
  }
});

test("an unsupported ignore pattern breaks POST for the collection and never DELETE", async () => {
  const t = await makeApp();
  t.backend.collections[0]!.ignore = ["+(a|aa)"];
  try {
    const posted = await t.app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "x.md", content: "# x\n" } });
    assert.equal(posted.statusCode, 500);
    assert.equal(posted.json().error, "unsupported_ignore_pattern");
    await assertDeletes(t, "existing.md");
    // No regex runs at all, so a hostile path against a hostile pattern costs nothing.
    assert.equal((await del(t.app, `${"a".repeat(200)}.md`)).statusCode, 202);
  } finally {
    await t.app.close();
  }
});

test("names these rules would refuse today stay deletable", async () => {
  const t = await makeApp();
  try {
    const anywhere = ["notes~1.md", "OBSIDI~1/x.md", "a\u202eb.md", "a\u200bb.md", " lead.md", "re\u0301sume\u0301.md", "conference.md", `${"語".repeat(100)}.md`];
    const posixOnly = ["a:b.md", "a<b>.md", 'a"b.md', "a|b.md", "a?b.md", "a*b.md", "trail.md ", "dot.md.", "dir./x.md", "CON.md", "NUL/x.md", "com1.tar.md", "a\x01b.md", "tab\there.md"];
    for (const p of process.platform === "win32" ? [...anywhere, ...windowsOnly] : [...anywhere, ...posixOnly]) await assertDeletes(t, p);
  } finally {
    await t.app.close();
  }
});

test("a short name cannot delete the file it aliases, whether or not the volume has short names", async () => {
  const t = await makeApp();
  try {
    const hidden = await plant(t.roots.claude, ".hidden.md");
    const dir = await plant(t.roots.claude, ".obsidian/workspace.md");
    for (const alias of ["hidden~1.md", "HIDDEN~1.MD", "obsidi~1/workspace.md", "OBSIDI~1/workspace.md"]) {
      const r = await del(t.app, alias);
      assert.equal(r.statusCode, 202, alias);
      assert.equal(r.json().deleted, false, alias);
    }
    assert.equal(await readFile(hidden, "utf8"), "leaked");
    assert.equal(await readFile(dir, "utf8"), "leaked");
    // The real names work.
    await assertDeletes(t, ".hidden.md");
    await assertDeletes(t, ".obsidian/workspace.md");
  } finally {
    await t.app.close();
  }
});

test("case and normalization aliases are refused, and the exact name works", async () => {
  const t = await makeApp();
  try {
    await plant(t.roots.claude, "keep.md");
    await plant(t.roots.claude, "dir/x.md");
    await plant(t.roots.claude, "caf\u00e9.md");
    for (const alias of ["KEEP.md", "DIR/x.md", "cafe\u0301.md"]) {
      const r = await del(t.app, alias);
      assert.equal(r.statusCode, 409, alias);
      assert.equal(r.json().error, "case_conflict", alias);
    }
    assert.deepEqual((await readdir(t.roots.claude)).sort(), ["caf\u00e9.md", "dir", "keep.md"].sort());
    await assertDeletes(t, "keep.md");
    await assertDeletes(t, "dir/x.md");
    await assertDeletes(t, "caf\u00e9.md");
  } finally {
    await t.app.close();
  }
});

test("a name too long for the filesystem is simply not there", async () => {
  const t = await makeApp();
  try {
    const r = await del(t.app, `${"a".repeat(300)}.md`);
    assert.equal(r.statusCode, 202, r.body);
    assert.equal(r.json().deleted, false);
    const nested = await del(t.app, `${"a".repeat(300)}/x.md`);
    assert.equal(nested.statusCode, 202, nested.body);
    assert.equal(nested.json().deleted, false);
  } finally {
    await t.app.close();
  }
});

test("deep paths are not limited for a delete, which runs no glob", async () => {
  const t = await makeApp();
  try {
    await assertDeletes(t, `${Array.from({ length: 40 }, () => "d").join("/")}/x.md`);
  } finally {
    await t.app.close();
  }
});

test("the lexical rules depend on what the platform would do with the name", () => {
  const root = path.resolve("root");
  const refused = (p: string, platform: NodeJS.Platform) => assert.throws(() => resolveDeleteTarget(root, p, platform), (err: unknown) => err instanceof HttpError && err.statusCode === 400 && err.code === "invalid_path", `${JSON.stringify(p)} on ${platform}`);
  const accepted = (p: string, platform: NodeJS.Platform) => assert.equal(resolveDeleteTarget(root, p, platform).relative, p, `${JSON.stringify(p)} on ${platform}`);

  for (const platform of ["win32", "linux", "darwin"] as const) {
    for (const p of ["", "/abs.md", "../x.md", "a/../../x.md", "a/./x.md", "a//x.md", "a/..", "with\0nul.md"]) refused(p, platform);
    for (const p of ["x.md", "notes.txt", ".hidden.md", "node_modules/x.md", "hidden~1.md", "a\u202eb.md"]) accepted(p, platform);
  }
  // Only Windows reads these as something other than the name given.
  for (const p of ["a:b.md", "x.md:stream", "CON.md", "nul/x.md", "com1.tar.md", "CON .md", ".. /x.md", "dir./x.md", "x.md ", "a?b.md", "a*b.md", "a<b.md", 'a"b.md', "a|b.md", "a\x01b.md"]) {
    refused(p, "win32");
    // On a Windows host, this process's own path.resolve reads `a:` as a drive, whatever platform is passed.
    if (process.platform === "win32" && p.includes(":")) continue;
    accepted(p, "linux");
    accepted(p, "darwin");
  }
  refused("C:/x.md", "win32");
  // A directory called `C:` is an ordinary name on POSIX, but this host's own path rules decide where it resolves.
  if (process.platform !== "win32") accepted("C:/x.md", "linux");
  assert.equal(deleteSegmentProblem("fine.md", "win32"), undefined);
  // Backslashes are separators everywhere, so `..\x` cannot be a way out on any host.
  refused("..\\x.md", "linux");
  assert.equal(resolveDeleteTarget(root, "a\\b.md", "linux").relative, "a/b.md");
});

test("still contained: traversal and links are refused for every kind of name", async () => {
  const t = await makeApp();
  try {
    for (const p of ["../x.md", "a/../../x.md", "/etc/passwd"]) {
      const r = await del(t.app, p);
      assert.equal(r.statusCode, 400, p);
      assert.equal(r.json().error, "invalid_path", p);
    }
  } finally {
    await t.app.close();
  }
});
