import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { meetsFloor } from "../../scripts/check-node.mjs";

const SCRIPT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../scripts/check-node.mjs");

test("a Node at or above the engines floor meets it, compared part by part as numbers", () => {
  for (const version of ["26.13.0", "26.13.1", "26.14.0", "27.0.0", "v26.13.0", "100.0.0"]) assert.equal(meetsFloor(version, ">=26.13.0"), true, version);
  for (const version of ["26.12.9", "26.9.99", "24.18.0", "9.99.99"]) assert.equal(meetsFloor(version, ">=26.13.0"), false, version);
  assert.equal(meetsFloor("26.13.0", " >= 26.13.0 "), true, "space around the range is fine");
});

test("a range it cannot read is not judged, rather than called failed", () => {
  for (const range of ["^26", "26.x", ">=26", "", ">=26.13.0 <27"]) assert.equal(meetsFloor("1.0.0", range), true, range);
  assert.equal(meetsFloor("not a version", ">=26.13.0"), true);
});

test("the hook's check never stops a commit, and speaks only when Node is too old", () => {
  const run = spawnSync(process.execPath, [SCRIPT], { encoding: "utf8" });
  assert.equal(run.status, 0);
  assert.equal(run.stdout, "");
  const floor = (JSON.parse(readFileSync(path.join(path.dirname(SCRIPT), "..", "package.json"), "utf8")) as { engines: { node: string } }).engines.node;
  assert.equal(run.stderr.includes("warning: this is Node"), !meetsFloor(process.versions.node, floor));
});
