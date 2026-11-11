import assert from "node:assert/strict";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { assignShards, mutatedFiles, parseShard, weigh } from "../../scripts/mutation-shard.mts";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

test("every file the sidecar's config mutates lands in exactly one shard, and nothing else does", () => {
  const files = mutatedFiles(ROOT, "sidecar");
  assert.ok(files.includes("sidecar/src/routes.ts"));
  assert.ok(files.includes("sidecar/src/llm/pool.ts"), "subdirectories too");
  assert.ok(!files.includes("sidecar/src/secret-file.ts"), "the config's exclusion holds");
  const shards = assignShards(weigh(ROOT, files), 8);
  const all = shards.flat();
  assert.equal(all.length, new Set(all).size, "no file twice");
  assert.deepEqual([...all].sort(), [...files].sort());
  for (const shard of shards) assert.ok(shard.length > 0, "no empty shard");
});

test("files go heaviest first to the lightest shard, and the split does not depend on input order", () => {
  const files = [
    { file: "a.ts", weight: 10 },
    { file: "b.ts", weight: 9 },
    { file: "c.ts", weight: 8 },
    { file: "d.ts", weight: 2 },
    { file: "e.ts", weight: 1 },
  ];
  // 10 to the first; 9 to the second; 8 to the second (9 < 10), now 17; 2 to the first, 12; 1 to the first, 13.
  assert.deepEqual(assignShards(files, 2), [["a.ts", "d.ts", "e.ts"], ["b.ts", "c.ts"]]);
  assert.deepEqual(assignShards([...files].reverse(), 2), assignShards(files, 2));
  // Equal weights are ordered by name, and equal loads go to the lower shard.
  assert.deepEqual(assignShards([{ file: "y.ts", weight: 1 }, { file: "x.ts", weight: 1 }], 2), [["x.ts"], ["y.ts"]]);
  assert.deepEqual(assignShards(files, 1), [["a.ts", "b.ts", "c.ts", "d.ts", "e.ts"]]);
  assert.deepEqual(assignShards(files.slice(0, 1), 3), [["a.ts"], [], []]);
});

test("a shard is written index/count, counted from one", () => {
  assert.deepEqual(parseShard("2/8"), { index: 2, shards: 8 });
  assert.deepEqual(parseShard("1/1"), { index: 1, shards: 1 });
  for (const bad of ["0/8", "9/8", "1/0", "8", "a/8", "1/8/2", " 1/8", ""]) assert.throws(() => parseShard(bad), /index>\/<count>/, bad);
  assert.throws(() => assignShards([], 0), /positive whole number/);
  assert.throws(() => assignShards([], 1.5), /positive whole number/);
});

test("a file weighs its non-blank lines", () => {
  const [weighed] = weigh(ROOT, ["sidecar/src/errors.ts"]);
  assert.ok(weighed!.weight > 0);
  assert.equal(weighed!.file, "sidecar/src/errors.ts");
});
