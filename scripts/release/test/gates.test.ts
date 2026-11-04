import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { requiredChecks, trackerReferences, unmetChecks } from "../gates.ts";

// Assembled at runtime so that this public repository holds no literal reference.
const reference = (number: number): string => ["MEMORIA", String(number)].join("-");

test("tracker references are found wherever they sit, in any case", () => {
  assert.deepEqual(trackerReferences(`feat: a (${reference(9)}) and ${reference(12)}`), [reference(9), reference(12)]);
  assert.deepEqual(trackerReferences(`see ${reference(63).toLowerCase()}.`), [reference(63).toLowerCase()]);
  assert.deepEqual(trackerReferences(`\n* ${reference(1)}: multi\nline`), [reference(1)]);
});

test("text without a tracker reference passes", () => {
  for (const clean of ["", "feat(api): add a thing (#5)", "MEMORIA", "MEMORIA-", "MEMORIA-x", "Memoria 12", "memoria_1.2.3_linux_amd64.tar.gz", "MEMORIA_QMD_SECRET"]) {
    assert.deepEqual(trackerReferences(clean), [], clean);
  }
});

test("the written first-release notes are publishable", () => {
  const notes = readFileSync(new URL("../first-release-notes.md", import.meta.url), "utf8");
  assert.deepEqual(trackerReferences(notes), []);
  assert.ok(notes.split("\n").length < 15, "short written notes, not a generated list");
});

test("required checks are read from the rules response", () => {
  const rules = [
    { type: "deletion" },
    { type: "required_status_checks", parameters: { required_status_checks: [{ context: "Build & Test" }, { context: "CLI Lint" }] } },
    { type: "pull_request", parameters: { required_approving_review_count: 0 } },
  ];
  assert.deepEqual(requiredChecks(rules), ["Build & Test", "CLI Lint"]);
  assert.deepEqual(requiredChecks([{ type: "deletion" }]), []);
  assert.deepEqual(requiredChecks({ message: "Not Found" }), []);
});

test("a required check counts only when it finished successfully", () => {
  const passed = { name: "A", status: "completed", conclusion: "success" };
  assert.deepEqual(unmetChecks(["A"], [passed, { name: "Other", status: "completed", conclusion: "failure" }]), []);
  assert.deepEqual(unmetChecks(["A", "B"], [passed]), ["B: no run found"]);
  assert.deepEqual(unmetChecks(["A"], []), ["A: no run found"]);
  assert.deepEqual(unmetChecks(["A"], [{ name: "A", status: "completed", conclusion: "failure" }]), ["A: failure"]);
  assert.deepEqual(unmetChecks(["A"], [{ name: "A", status: "completed", conclusion: "skipped" }]), ["A: skipped"]);
  assert.deepEqual(unmetChecks(["A"], [{ name: "A", status: "in_progress", conclusion: null }]), ["A: in_progress"]);
  assert.deepEqual(unmetChecks(["A"], [passed, { name: "A", status: "completed", conclusion: "cancelled" }]), ["A: cancelled"]);
});
