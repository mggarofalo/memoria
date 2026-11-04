import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { describedVersion, describeToVersion, resolveVersion, STAMP_FILE, UNKNOWN_VERSION } from "../src/version.ts";

// A git hook exports these, and they would point git at this repository from anywhere.
for (const name of ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE"]) delete process.env[name];

test("git describe output maps to the documented versions", () => {
  const cases: Array<[string, string]> = [
    ["v1.2.3-0-gabc123", "1.2.3"],
    ["v1.2.3-0-gabc123-dirty", "1.2.4-dev.0+gabc123.dirty"],
    ["v1.2.3-5-gabc123", "1.2.4-dev.5+gabc123"],
    ["v1.2.3-5-gabc123-dirty", "1.2.4-dev.5+gabc123.dirty"],
    ["abc123", "0.0.0-dev+gabc123"],
    ["abc123-dirty", "0.0.0-dev+gabc123.dirty"],
    ["not a version", UNKNOWN_VERSION],
  ];
  for (const [described, expected] of cases) assert.equal(describeToVersion(described), expected, described);
});

test("a stamp file wins, and without a stamp or git the version is unknown", async () => {
  const stamped = await mkdtemp(path.join(tmpdir(), "memoria-version-"));
  await writeFile(path.join(stamped, STAMP_FILE), "7.8.9\n");
  assert.equal(resolveVersion(stamped), "7.8.9");
  const bare = await mkdtemp(path.join(tmpdir(), "memoria-version-"));
  assert.equal(resolveVersion(bare), UNKNOWN_VERSION);
  assert.equal(describedVersion(bare), UNKNOWN_VERSION);
});
