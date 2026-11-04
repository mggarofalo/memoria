import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { test } from "node:test";
import { HttpError } from "../src/errors.ts";
import { resolveTarget } from "../src/ingest.ts";
import { firstCodePoints } from "../src/metadata.ts";
import { QMD_EXCLUDED_DIRS, foldName, segmentProblem, unindexableProblem } from "../src/path-rules.ts";

const ROOT = path.resolve("corpus-root-that-is-never-created");

function refusedWith(p: string, code: string, pattern = "**/*.md"): void {
  assert.throws(
    () => resolveTarget(ROOT, pattern, p),
    (err: unknown) => err instanceof HttpError && err.statusCode === 400 && err.code === code,
    JSON.stringify(p),
  );
}

test("the excluded directories match the installed QMD SDK's scan", () => {
  // Resolved, not imported: this reads the SDK's source without loading it.
  const source = readFileSync(new URL("store.js", import.meta.resolve("@tobilu/qmd")), "utf8");
  const declared = source.match(/const excludeDirs = (\[[^\]]*\]);/);
  assert.ok(declared, "the SDK no longer declares excludeDirs; re-read reindexCollection");
  assert.deepEqual(JSON.parse(declared[1]!), [...QMD_EXCLUDED_DIRS]);
});

test("real session filename shapes are accepted", () => {
  for (const name of [
    "2026-11-04_2e808e16_part-002.md",
    "2026-02-11_2e39c784.md",
    "2026-10-31_abc.md",
    "sub/dir/2026-11-04_2e808e16.md",
    "a name with spaces.md",
    "notes (old) [draft] {x} v1.2.md",
    "conference.md",
    "com10.md",
    "lpt.md",
    "nullable/x.md",
    "backup~.md",
    "a~b.md",
    "x~1y.md",
    "résumé.md",
    "vendor.md",
    "build-notes/dist.md",
    "日本語.md",
  ]) {
    assert.equal(resolveTarget(ROOT, "**/*.md", name).relative, name);
  }
});

test("8.3 short name shapes are refused in any segment", () => {
  for (const p of ["hidden~1.md", "HIDDEN~1.MD", "obsidi~1/workspace.md", "a/b~12/c.md", "ab12~3.md", "x/DOCUME~1/y.md"]) refusedWith(p, "invalid_path");
});

test("Windows reserved names are refused with any case, extension and position", () => {
  for (const stem of ["CON", "con", "PRN", "AUX", "NUL", "nul", "COM1", "com9", "COM0", "LPT1", "lpt9", "COM¹", "LPT³", "CONIN$", "CONOUT$", "CLOCK$"]) {
    refusedWith(`${stem}.md`, "invalid_path");
    refusedWith(`${stem}.tar.md`, "invalid_path");
    refusedWith(`${stem}/x.md`, "invalid_path");
    refusedWith(`a/${stem}/x.md`, "invalid_path");
  }
  refusedWith("CON .md", "invalid_path");
});

test("illegal, control, bidi and invisible characters are refused on every platform", () => {
  for (const c of ["<", ">", ":", '"', "|", "?", "*"]) refusedWith(`a${c}b.md`, "invalid_path");
  for (const c of ["\0", "\x01", "\t", "\n", "\r", "\x1f", "\x7f", "\x85"]) refusedWith(`a${c}b.md`, "invalid_path");
  // Bidi controls: the classic use is making `gpj.md` read as `dm.jpg`.
  for (const c of ["؜", "‎", "‏", "‪", "‫", "‬", "‭", "‮", "⁦", "⁧", "⁨", "⁩"]) refusedWith(`a${c}b.md`, "invalid_path");
  // Invisible characters that make two names look identical.
  for (const c of ["­", "​", "‌", "‍", "⁠", "﻿", " ", " "]) refusedWith(`a${c}b.md`, "invalid_path");
  // A lone surrogate becomes U+FFFD on the way to the filesystem, so two names would meet.
  refusedWith("a\ud800b.md", "invalid_path");
  refusedWith("a\udc00b.md", "invalid_path");
});

test("whitespace at a segment's edge is refused, not trimmed into another name", () => {
  for (const p of [" a.md", "a.md ", "\ta.md", "a.md\n", " sub/x.md", "sub /x.md", " a.md", "a /x.md", "　a.md", ""]) refusedWith(p, "invalid_path");
});

test("names too long for the filesystem are refused rather than failing at write", () => {
  refusedWith(`${"a".repeat(253)}.md`, "invalid_path");
  assert.ok(resolveTarget(ROOT, "**/*.md", `${"a".repeat(252)}.md`));
  // 85 three-byte characters is 255 bytes, plus the extension.
  refusedWith(`${"語".repeat(85)}.md`, "invalid_path");
});

test("decomposed Unicode is refused, because NTFS and APFS disagree about it", () => {
  refusedWith("résumé.md", "invalid_path");
  refusedWith("café/x.md", "invalid_path");
  assert.ok(resolveTarget(ROOT, "**/*.md", "résumé.md"));
});

test("foldName joins the aliases filesystems treat as one name", () => {
  assert.equal(foldName("KEEP.md"), foldName("keep.md"));
  assert.equal(foldName("Straße.md"), foldName("STRASSE.md"));
  assert.equal(foldName("café.md"), foldName("café.md"));
  assert.notEqual(foldName("keep.md"), foldName("keep2.md"));
});

test("directories QMD excludes are refused at any depth and in any case", () => {
  for (const dir of QMD_EXCLUDED_DIRS.filter((d) => !d.startsWith("."))) {
    refusedWith(`${dir}/x.md`, "invalid_path");
    refusedWith(`a/b/${dir}/x.md`, "invalid_path");
    refusedWith(`${dir.toUpperCase()}/x.md`, "invalid_path");
  }
  assert.match(unindexableProblem(["vendor", "x.md"])!, /vendor/);
  assert.equal(unindexableProblem(["vendors", "x.md"]), undefined);
});

test("a dot directory is refused even when the collection's own glob asks for it", () => {
  refusedWith(".notes/x.md", "invalid_path", ".notes/**/*.md");
  refusedWith(".hidden.md", "pattern_mismatch");
});

test("segmentProblem says nothing about an acceptable segment", () => {
  assert.equal(segmentProblem("2026-11-04_2e808e16_part-002.md"), undefined);
});

test("firstCodePoints counts as SQLite does, never splitting a surrogate pair", () => {
  assert.equal(firstCodePoints("short", 10), "short");
  assert.equal(firstCodePoints("abcdef", 3), "abc");
  assert.equal(firstCodePoints("\u{1f600}\u{1f600}\u{1f600}", 2), "\u{1f600}\u{1f600}");
  assert.equal(firstCodePoints("a\u{1f600}b", 2), "a\u{1f600}");
});
