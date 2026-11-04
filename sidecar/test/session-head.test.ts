import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { test } from "node:test";
import { DEFAULT_HEAD_BYTES, MetadataIndex, matchesFilters, parseFrontmatter } from "../src/metadata.ts";
import type { DocumentMetadata } from "../src/metadata.ts";

/**
 * Documents rendered by Memoria.Core must keep their metadata when only the head window is read.
 * The fixtures are that code's own output, pinned by SessionHeadBudgetTests. Each is cut the three
 * ways a head can be taken, then goes through the real metadata index.
 */
const FIXTURES = path.join(import.meta.dirname, "..", "..", "tests", "Memoria.Core.Tests", "Sessions", "Fixtures", "head");

const CUTS: Record<string, (text: string, limit: number) => string> = {
  // substr() in store.ts counts characters.
  "code points": (text, limit) => Array.from(text).slice(0, limit).join(""),
  // The fake backend slices UTF-16 units.
  "utf-16 units": (text, limit) => text.slice(0, limit),
  // The smallest reading of the "head bytes" the index asks for.
  "utf-8 bytes": (text, limit) => Buffer.from(text, "utf8").subarray(0, limit).toString("utf8"),
};

function indexed(text: string, cut: (text: string, limit: number) => string): { withMetadata: number; meta: DocumentMetadata | undefined } {
  const index = new MetadataIndex({
    readDocumentHeads: (maxBytes) => [{ collection: "c", path: "p.md", head: cut(text, maxBytes) }],
  });
  const { withMetadata } = index.refresh();
  return { withMetadata, meta: index.get("c/p.md") };
}

function fixture(name: string): string {
  const text = readFileSync(path.join(FIXTURES, `${name}.md`), "utf8");
  assert.ok(text.length > DEFAULT_HEAD_BYTES, `${name} must be longer than the window, or nothing is cut`);
  return text;
}

test("the head window the renderer budgets for is still 8192", () => {
  // SessionFrontmatter.SidecarHeadWindow pins the same value from the C# side.
  assert.equal(DEFAULT_HEAD_BYTES, 8192);
});

test("10,000 touched files are recorded as truncated, and none of it reaches past the window", () => {
  const text = fixture("claude-10000-files");
  const frontmatter = parseFrontmatter(text.slice(0, DEFAULT_HEAD_BYTES));
  assert.ok(frontmatter);
  const kept = frontmatter.files_touched as string[];
  assert.equal(kept.length + (frontmatter.files_touched_truncated as number), 10_000);
  assert.equal(kept[0], "f/00000.cs");
});

for (const [label, cut] of Object.entries(CUTS)) {
  test(`10,000 touched files keep every filter field, cut by ${label}`, () => {
    const { withMetadata, meta } = indexed(fixture("claude-10000-files"), cut);

    assert.equal(withMetadata, 1);
    assert.equal(meta?.project, "demo-app");
    assert.equal(meta?.branch, "feature/parity");
    assert.equal(meta?.sessionId, "abcdef12-3456-7890-abcd-ef1234567890");
    assert.equal(meta?.date, "2026-10-01");
    assert.equal(meta?.startTime, "2026-10-01T10:00:00+00:00");
    assert.equal(meta?.endTime, "2026-10-01T10:00:00+00:00");
    assert.deepEqual(meta?.tags, ["project/demo-app", "branch/feature-parity"]);
    assert.ok(matchesFilters(meta, { project: "demo-app", branch: "feature/parity", tags: ["project/demo-app"], since: "2026-10-01", until: "2026-10-01" }));
    assert.ok(!matchesFilters(meta, { project: "other" }));
  });

  test(`the longest hostile fields with 10,000 multibyte touched files stay parseable, cut by ${label}`, () => {
    const { withMetadata, meta } = indexed(fixture("claude-hostile-10000-files"), cut);

    assert.equal(withMetadata, 1);
    assert.equal(meta?.date, "2026-10-01");
    assert.ok(meta?.project && meta.branch && meta.sessionId);
    assert.equal(meta?.tags.length, 2);
    assert.ok(matchesFilters(meta, { since: "2026-10-01", until: "2026-10-01" }));
  });

  test(`many codex turn ids keep every filter field, cut by ${label}`, () => {
    const { withMetadata, meta } = indexed(fixture("codex-2500-turns"), cut);

    assert.equal(withMetadata, 1);
    assert.equal(meta?.project, "demo-app");
    assert.equal(meta?.sessionId, "11111111-2222-4333-8444-555555555555");
    assert.equal(meta?.date, "2026-09-09");
    assert.ok(matchesFilters(meta, { project: "demo-app", since: "2026-09-09" }));
  });

  test(`the longest hostile codex fields stay parseable, cut by ${label}`, () => {
    const { withMetadata, meta } = indexed(fixture("codex-hostile"), cut);

    assert.equal(withMetadata, 1);
    assert.equal(meta?.date, "2026-09-09");
    assert.ok(meta?.project && meta.sessionId);
  });

  test(`a pathological envelope title and tag list stay parseable, cut by ${label}`, () => {
    const { withMetadata, meta } = indexed(fixture("envelope-longest"), cut);

    assert.equal(withMetadata, 1);
    assert.equal(meta?.date, "2026-01-02");
    assert.ok(meta?.project && meta.source);
    assert.equal(meta?.tags.length, 32);
  });

  test(`control: frontmatter that closes past the window loses all metadata, cut by ${label}`, () => {
    const files = Array.from({ length: 10_000 }, (_, i) => `  - "f/${String(i).padStart(5, "0")}.cs"`).join("\n");
    const exporterOrder = `---\nsession_id: "a"\ndate: "2026-10-01"\nproject: "demo-app"\nfiles_touched:\n${files}\ntags:\n  - "x"\n---\n\nbody\n`;
    const fieldsFirstOnly = `---\nsession_id: "a"\ndate: "2026-10-01"\nproject: "demo-app"\ntags:\n  - "x"\nfiles_touched:\n${files}\n---\n\nbody\n`;

    assert.equal(indexed(exporterOrder, cut).withMetadata, 0);
    // Moving the fields first is not enough on its own: the closing fence must also be inside the window.
    assert.equal(indexed(fieldsFirstOnly, cut).withMetadata, 0);
  });
}
