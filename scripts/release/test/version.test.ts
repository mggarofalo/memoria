import assert from "node:assert/strict";
import { test } from "node:test";
import { applyBump, classifyCommit, compareVersions, highestBump, latestTag, nextVersion, parseTag, tagProblem } from "../version.ts";

test("only strict vX.Y.Z is a release tag", () => {
  assert.deepEqual(parseTag("v1.20.3"), { major: 1, minor: 20, patch: 3 });
  assert.deepEqual(parseTag("v0.0.0"), { major: 0, minor: 0, patch: 0 });
  for (const bad of ["1.2.3", "v1.2", "v1.2.3.4", "v01.2.3", "v1.02.3", "v1.2.03", "v1.2.3-rc1", "v1.2.3+build", "V1.2.3", "v1.2.x", " v1.2.3", "v1.2.3\n", ""]) {
    assert.equal(parseTag(bad), undefined, JSON.stringify(bad));
  }
});

test("commits classify by Conventional Commits type", () => {
  const cases: Array<[string, string]> = [
    ["feat: a", "minor"],
    ["feat(api): a (#5)", "minor"],
    ["Feat: types are case-insensitive", "minor"],
    ["fix: a", "patch"],
    ["fix(sidecar): a", "patch"],
    ["perf: a", "patch"],
    ["docs: a", "none"],
    ["chore: a", "none"],
    ["ci(release): a", "none"],
    ["refactor(core): a", "none"],
    ["feature: not a recognised type", "none"],
    ["fix a thing", "none"],
    ["feat:no space", "none"],
    ["Merge branch 'feat: a'", "none"],
    ["", "none"],
  ];
  for (const [message, bump] of cases) assert.equal(classifyCommit(message), bump, message);
});

test("a bang or a BREAKING CHANGE footer is breaking, whatever the type", () => {
  const breaking = ["feat!: a", "fix(api)!: a", "chore!: a", "docs: a\n\nBREAKING CHANGE: b", "fix: a\n\nbody\n\nBREAKING-CHANGE: b", "not conventional\n\nBREAKING CHANGE: b"];
  for (const message of breaking) assert.equal(classifyCommit(message), "major", message);
  // Prose that mentions the phrase is not the footer.
  const notBreaking = ["fix: a\n\nthis is not a BREAKING CHANGE: really", "fix: a\n\nbreaking change: lower case", "fix: a\n\nBREAKING CHANGE without colon"];
  for (const message of notBreaking) assert.equal(classifyCommit(message), "patch", message);
});

test("the strongest commit decides the bump", () => {
  assert.equal(highestBump([]), "none");
  assert.equal(highestBump(["docs: a", "chore: b"]), "none");
  assert.equal(highestBump(["docs: a", "fix: b", "chore: c"]), "patch");
  assert.equal(highestBump(["fix: a", "feat: b", "fix: c"]), "minor");
  assert.equal(highestBump(["feat: a", "fix!: b", "feat: c"]), "major");
});

test("bumps reset the lower fields, and a break is a minor bump while 0.x", () => {
  assert.deepEqual(applyBump({ major: 1, minor: 2, patch: 3 }, "patch"), { major: 1, minor: 2, patch: 4 });
  assert.deepEqual(applyBump({ major: 1, minor: 2, patch: 3 }, "minor"), { major: 1, minor: 3, patch: 0 });
  assert.deepEqual(applyBump({ major: 1, minor: 2, patch: 3 }, "major"), { major: 2, minor: 0, patch: 0 });
  assert.deepEqual(applyBump({ major: 0, minor: 2, patch: 3 }, "major"), { major: 0, minor: 3, patch: 0 });
});

test("the next version follows from the last tag and the commits", () => {
  assert.equal(nextVersion("v1.2.3", ["fix: a"]), "1.2.4");
  assert.equal(nextVersion("v1.2.3", ["feat: a", "fix: b"]), "1.3.0");
  assert.equal(nextVersion("v1.2.3", ["feat!: a"]), "2.0.0");
  assert.equal(nextVersion("v0.2.3", ["feat!: a"]), "0.3.0");
});

test("nothing releasable means no release", () => {
  assert.equal(nextVersion("v1.2.3", []), undefined);
  assert.equal(nextVersion("v1.2.3", ["docs: a", "chore: b"]), undefined);
  assert.equal(nextVersion(undefined, ["docs: a"]), undefined);
});

test("the first release is always 0.1.0, even over a breaking commit", () => {
  assert.equal(nextVersion(undefined, ["fix: a"]), "0.1.0");
  assert.equal(nextVersion(undefined, ["feat: a", "fix!: b"]), "0.1.0");
});

test("the same history gives the same answer in any order", () => {
  const messages = ["fix: a", "docs: b", "feat: c"];
  assert.equal(nextVersion("v1.0.0", messages), nextVersion("v1.0.0", [...messages].reverse()));
});

test("the latest tag is the numerically highest strict one", () => {
  assert.equal(latestTag([]), undefined);
  assert.equal(latestTag(["nightly", "v1.2", "v9.9.9-rc1", ""]), undefined);
  assert.equal(latestTag(["v0.9.0", "v0.10.0", "v0.2.0"]), "v0.10.0");
  assert.equal(latestTag(["v2.0.0", "v10.0.0", "v9.9.9"]), "v10.0.0");
  assert.equal(latestTag(["v1.2.9", "v1.2.10", "v1.2.3"]), "v1.2.10");
  assert.ok(compareVersions({ major: 1, minor: 0, patch: 0 }, { major: 1, minor: 0, patch: 0 }) === 0);
});

test("a tag must be the computed version", () => {
  assert.equal(tagProblem("v0.1.0", undefined, ["feat: a"]), undefined);
  assert.equal(tagProblem("v1.3.0", "v1.2.3", ["feat: a"]), undefined);
  assert.match(tagProblem("v1.2.4", "v1.2.3", ["feat: a"]) as string, /call for v1\.3\.0, not v1\.2\.4/);
  assert.match(tagProblem("v0.5.0", undefined, ["feat: a"]) as string, /start of history call for v0\.1\.0/);
  assert.match(tagProblem("v1.2.4", "v1.2.3", ["docs: a"]) as string, /no releasable commits since v1\.2\.3/);
  assert.match(tagProblem("v0.1.0", undefined, ["docs: a"]) as string, /no releasable commits since the start of history/);
});

test("v1.0.0 is the one deliberate exception, and only out of 0.x", () => {
  assert.equal(tagProblem("v1.0.0", "v0.4.2", ["docs: a"]), undefined);
  assert.ok(tagProblem("v1.0.0", undefined, ["feat: a"]));
  assert.ok(tagProblem("v1.0.0", "v1.0.0", ["feat: a"]));
  assert.ok(tagProblem("v2.0.0", "v0.4.2", ["feat!: a"]));
});
