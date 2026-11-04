import assert from "node:assert/strict";
import { test } from "node:test";
import { MetadataIndex, documentInstant, extractMetadata, hasFilters, matchesFilters, metadataFromText, parseBound, parseFrontmatter } from "../src/metadata.ts";
import { FRONTMATTER_DOC } from "./fake-backend.ts";

const SESSION = `---
session_id: "2e39c784-6a79-4ea4-9781-b58415c377e1"
slug: "encapsulated-crafting-forest"
date: "2026-02-11"
start_time: "2026-02-11T04:47:57.868000+00:00"
end_time: "2026-02-11T04:49:20.661000+00:00"
project: "Receipts"
git_branch: "master"
files_touched:
  - "C:/Users/mggar/.claude/plans/encapsulated-crafting-forest.md"
tags:
  - "project/Receipts"
  - "branch/master"
---

# Title

body
`;

test("parses the transcript frontmatter shape QMD indexes", () => {
  const fm = parseFrontmatter(SESSION);
  assert.ok(fm);
  const meta = extractMetadata(fm);
  assert.deepEqual(meta, {
    tags: ["project/Receipts", "branch/master"],
    project: "Receipts",
    branch: "master",
    date: "2026-02-11",
    startTime: "2026-02-11T04:47:57.868000+00:00",
    endTime: "2026-02-11T04:49:20.661000+00:00",
    sessionId: "2e39c784-6a79-4ea4-9781-b58415c377e1",
  });
});

test("documents without frontmatter or with malformed YAML yield no metadata", () => {
  assert.equal(metadataFromText("# No frontmatter\n\ntext"), undefined);
  assert.equal(metadataFromText("---\nnot: [valid\n---\n"), undefined);
  assert.equal(metadataFromText("---\n- a list\n---\n"), undefined);
  assert.equal(metadataFromText(""), undefined);
});

test("CRLF and BOM frontmatter still parse; unquoted dates survive", () => {
  const meta = metadataFromText("\uFEFF---\r\nproject: X\r\ndate: 2026-05-01\r\ntags: solo\r\n---\r\nbody");
  assert.ok(meta);
  assert.equal(meta.project, "X");
  assert.equal(meta.date, "2026-05-01");
  assert.deepEqual(meta.tags, ["solo"]);
});

test("redacted frontmatter stays parseable: the redactor's quoting is what keeps metadata", () => {
  // What the redactor writes when a value starts with a placeholder. Without the quotes, YAML reads a flow sequence.
  const quoted = parseFrontmatter("---\ntitle: '[REDACTED:github-token] rotated, it''s done'\nproject: demo\ntags:\n  - '[REDACTED:github-token]'\n---\n");
  assert.deepEqual(quoted, { title: "[REDACTED:github-token] rotated, it's done", project: "demo", tags: ["[REDACTED:github-token]"] });
  assert.equal(parseFrontmatter("---\ntitle: [REDACTED:github-token] rotated\nproject: demo\n---\n"), null);

  // What the session renderer writes: every value double-quoted, so a placeholder is ordinary text.
  const rendered = metadataFromText("---\nsession_id: \"[REDACTED:github-token]\"\ndate: \"2026-10-01\"\nproject: \"[REDACTED:github-token]\"\ngit_branch: \"fix/[REDACTED:github-token]\"\ntags:\n  - \"project/[REDACTED:github-token]\"\n---\n");
  assert.equal(rendered?.project, "[REDACTED:github-token]");
  assert.equal(rendered?.branch, "fix/[REDACTED:github-token]");
  assert.equal(rendered?.date, "2026-10-01");
  assert.deepEqual(rendered?.tags, ["project/[REDACTED:github-token]"]);
});

test("branch falls back to `branch` when `git_branch` is absent", () => {
  const meta = metadataFromText("---\nbranch: feature/x\n---\n");
  assert.equal(meta?.branch, "feature/x");
});

test("hasFilters ignores empty filter objects", () => {
  assert.equal(hasFilters(undefined), false);
  assert.equal(hasFilters({}), false);
  assert.equal(hasFilters({ project: [] }), false);
  assert.equal(hasFilters({ since: "  " }), false);
  assert.equal(hasFilters({ tags: [] }), false);
  assert.equal(hasFilters({ project: "x" }), true);
});

test("filters match project, branch and tags case-insensitively", () => {
  const meta = metadataFromText(SESSION)!;
  assert.equal(matchesFilters(meta, { project: "receipts" }), true);
  assert.equal(matchesFilters(meta, { project: ["Memoria", "RECEIPTS"] }), true);
  assert.equal(matchesFilters(meta, { project: "Memoria" }), false);
  assert.equal(matchesFilters(meta, { branch: "MASTER" }), true);
  assert.equal(matchesFilters(meta, { branch: "main" }), false);
  assert.equal(matchesFilters(meta, { tags: ["project/receipts"] }), true);
  assert.equal(matchesFilters(meta, { tags: ["project/receipts", "branch/master"] }), true);
  assert.equal(matchesFilters(meta, { tags: ["project/receipts", "branch/main"] }), false);
});

test("date filters use start_time and treat a bare `until` as end of day", () => {
  const meta = metadataFromText(SESSION)!;
  assert.equal(matchesFilters(meta, { since: "2026-02-11" }), true);
  assert.equal(matchesFilters(meta, { since: "2026-02-12" }), false);
  assert.equal(matchesFilters(meta, { until: "2026-02-11" }), true);
  assert.equal(matchesFilters(meta, { until: "2026-02-10" }), false);
  assert.equal(matchesFilters(meta, { since: "2026-02-11T04:00:00Z", until: "2026-02-11T05:00:00Z" }), true);
  assert.equal(matchesFilters(meta, { since: "2026-02-11T05:00:00Z" }), false);
  assert.throws(() => parseBound("yesterday", "since"), /Invalid since/);
});

test("date-only documents are dated at UTC midnight", () => {
  const meta = extractMetadata({ date: "2026-04-01" });
  assert.equal(documentInstant(meta), Date.parse("2026-04-01T00:00:00Z"));
  assert.equal(matchesFilters(meta, { since: "2026-04-01", until: "2026-04-01" }), true);
  assert.equal(documentInstant(extractMetadata({})), undefined);
});

test("documents without metadata fail active filters but pass empty ones", () => {
  assert.equal(matchesFilters(undefined, { project: "x" }), false);
  assert.equal(matchesFilters(undefined, {}), true);
  assert.equal(matchesFilters(extractMetadata({ project: "x" }), { since: "2026-01-01" }), false);
});

test("MetadataIndex builds from document heads and supports point updates", () => {
  const heads = [
    { collection: "c", path: "a.md", head: FRONTMATTER_DOC({ project: "A" }, "x") },
    { collection: "c", path: "b.md", head: "no frontmatter" },
  ];
  const index = new MetadataIndex({ readDocumentHeads: () => heads });
  assert.equal(index.refreshedAt, undefined);
  assert.deepEqual(index.refresh(), { documents: 2, withMetadata: 1 });
  assert.ok(index.refreshedAt);
  assert.equal(index.size, 1);
  assert.equal(index.get("c/a.md")?.project, "A");
  assert.equal(index.get("c/b.md"), undefined);

  index.set("c/b.md", FRONTMATTER_DOC({ project: "B" }, "y"));
  assert.equal(index.get("c/b.md")?.project, "B");
  index.set("c/b.md", "stripped");
  assert.equal(index.get("c/b.md"), undefined);
});

test("a YAML warning does not put frontmatter text on stderr", async () => {
  const warnings: string[] = [];
  const onWarning = (warning: Error): void => void warnings.push(warning.message);
  process.on("warning", onWarning);
  parseFrontmatter("---\nkey: !unknowntag PRIVATE-FRONTMATTER\n---\nbody\n");
  await new Promise((resolve) => setImmediate(resolve));
  await new Promise((resolve) => setImmediate(resolve));
  process.off("warning", onWarning);
  assert.deepEqual(warnings, []);
});
