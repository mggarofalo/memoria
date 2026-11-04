import assert from "node:assert/strict";
import { test } from "node:test";
import { documentInstant, extractMetadata, hasFilters, matchesFilters, parseBound, parseFrontmatter, type DocumentMetadata } from "../src/metadata.ts";

const meta = (over: Partial<DocumentMetadata> = {}): DocumentMetadata => ({ tags: [], ...over });

test("no active filter matches everything, even a document with no metadata", () => {
  assert.equal(matchesFilters(undefined, {}), true);
  assert.equal(matchesFilters(undefined, { project: " ", branch: [], tags: [], since: "", until: "  " }), true);
});

test("a document with no metadata fails any active filter", () => {
  for (const filters of [{ project: "a" }, { branch: "a" }, { tags: ["a"] }, { since: "2026-01-01" }, { until: "2026-01-01" }]) {
    assert.equal(matchesFilters(undefined, filters), false, JSON.stringify(filters));
  }
});

test("project and branch match any listed value, ignoring case, and a missing value matches none", () => {
  assert.equal(matchesFilters(meta({ project: "Memoria" }), { project: "memoria" }), true);
  assert.equal(matchesFilters(meta({ project: "memoria" }), { project: ["other", " MEMORIA "] }), true);
  assert.equal(matchesFilters(meta({ project: "memoria" }), { project: "other" }), false);
  assert.equal(matchesFilters(meta(), { project: "memoria" }), false);
  assert.equal(matchesFilters(meta({ project: "" }), { project: "memoria" }), false);
  assert.equal(matchesFilters(meta({ branch: "Main" }), { branch: "main" }), true);
  assert.equal(matchesFilters(meta({ branch: "dev" }), { branch: "main" }), false);
  assert.equal(matchesFilters(meta(), { branch: "main" }), false);
});

test("every listed tag must be present, ignoring case and surrounding space", () => {
  const tagged = meta({ tags: ["Alpha", "beta"] });
  assert.equal(matchesFilters(tagged, { tags: ["alpha"] }), true);
  assert.equal(matchesFilters(tagged, { tags: [" ALPHA ", "BETA"] }), true);
  assert.equal(matchesFilters(tagged, { tags: ["alpha", "gamma"] }), false);
  assert.equal(matchesFilters(meta(), { tags: ["alpha"] }), false);
});

test("the since and until bounds are inclusive, and a bare until covers the whole day", () => {
  const at = meta({ startTime: "2026-03-10T12:00:00Z" });
  assert.equal(matchesFilters(at, { since: "2026-03-10T12:00:00Z" }), true);
  assert.equal(matchesFilters(at, { since: "2026-03-10T12:00:01Z" }), false);
  assert.equal(matchesFilters(at, { until: "2026-03-10T12:00:00Z" }), true);
  assert.equal(matchesFilters(at, { until: "2026-03-10T11:59:59Z" }), false);
  assert.equal(matchesFilters(at, { until: "2026-03-10" }), true);
  assert.equal(matchesFilters(at, { until: "2026-03-09" }), false);
  assert.equal(matchesFilters(at, { since: "2026-03-10", until: "2026-03-10" }), true);
  assert.equal(matchesFilters(at, { since: "2026-03-11", until: "2026-03-12" }), false);
  assert.equal(matchesFilters(at, { since: "2026-03-01", until: "2026-03-05" }), false);
});

test("a date bound fails a document that has no usable date", () => {
  assert.equal(matchesFilters(meta(), { since: "2026-01-01" }), false);
  assert.equal(matchesFilters(meta({ startTime: "not a date" }), { until: "2026-01-01" }), false);
});

test("the document's instant is its start time, then its date, and nothing else", () => {
  assert.equal(documentInstant(meta({ startTime: "2026-03-10T12:00:00Z", date: "2020-01-01" })), Date.parse("2026-03-10T12:00:00Z"));
  assert.equal(documentInstant(meta({ startTime: "garbage", date: "2020-01-01" })), Date.parse("2020-01-01T00:00:00Z"));
  assert.equal(documentInstant(meta({ date: "9999-99-99" })), undefined);
  assert.equal(documentInstant(meta()), undefined);
});

test("a bound is checked, and the message names which one", () => {
  assert.equal(parseBound(" 2026-03-10 ", "since"), Date.parse("2026-03-10T00:00:00Z"));
  assert.equal(parseBound("2026-03-10", "until"), Date.parse("2026-03-11T00:00:00Z") - 1);
  assert.throws(() => parseBound("2026-99-99", "since"), /Invalid since date/);
  assert.throws(() => parseBound("2026-99-99", "until"), /Invalid until date/);
  assert.throws(() => parseBound("soon", "since"), /Invalid since timestamp/);
  assert.throws(() => matchesFilters(meta(), { until: "soon" }), /Invalid until timestamp/);
});

test("hasFilters is false for nothing, and true for each kind of filter", () => {
  assert.equal(hasFilters(undefined), false);
  assert.equal(hasFilters({}), false);
  for (const filters of [{ project: "a" }, { branch: ["a"] }, { since: "x" }, { until: "x" }, { tags: ["a"] }]) {
    assert.equal(hasFilters(filters), true, JSON.stringify(filters));
  }
});

test("metadata fields accept what YAML hands back, and ignore what they cannot use", () => {
  const when = new Date("2026-03-10T12:00:00Z");
  assert.deepEqual(extractMetadata({ project: 42, start_time: when, tags: "solo", date: when, branch: "  " }), {
    tags: ["solo"],
    project: "42",
    date: "2026-03-10",
    startTime: "2026-03-10T12:00:00.000Z",
  });
  assert.deepEqual(extractMetadata({ project: { nested: true }, date: "soon", tags: ["a", "", 7, null] }), { tags: ["a", "7"] });
  assert.equal(extractMetadata({ git_branch: "a", branch: "b" }).branch, "a");
  assert.equal(parseFrontmatter("---\n: : :\n  bad: [\n---\n"), null);
  assert.equal(parseFrontmatter("---\n- a\n- b\n---\n"), null);
  assert.equal(parseFrontmatter("no frontmatter"), null);
});
