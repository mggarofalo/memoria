import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { loadConfig } from "../src/config.ts";

/**
 * worker/README.md's settings table against the code. Every MEMORIA_WORKER_* the worker reads is a
 * row and nothing else is, and a default written in backticks leaves loadConfig unchanged when set.
 * A per-role name is written `MEMORIA_WORKER_<ROLE>_…`, as the table does.
 */

const root = fileURLToPath(new URL("../", import.meta.url));
const SRC = readdirSync(path.join(root, "src"), { withFileTypes: true, recursive: true })
  .filter((e) => e.isFile() && e.name.endsWith(".ts"))
  .map((e) => readFileSync(path.join(e.parentPath, e.name), "utf8"))
  .join("\n");
const README = readFileSync(path.join(root, "README.md"), "utf8");

function variablesRead(): Set<string> {
  const names = new Set(SRC.match(/MEMORIA_WORKER_[A-Z0-9_]+/g));
  for (const m of SRC.matchAll(/\$\{prefix\}(_[A-Z0-9_]+)/g)) names.add(`MEMORIA_WORKER_<ROLE>${m[1]}`);
  for (const m of SRC.matchAll(/resolveSecret\(env, "(MEMORIA_WORKER_[A-Z0-9_]+)"/g)) names.add(`${m[1]}_FILE`);
  return names;
}

function variablesDocumented(): Map<string, string> {
  return new Map([...README.matchAll(/^\| `(MEMORIA_WORKER_[A-Z0-9_<>]+)` \| ([^|]*) \|/gm)].map((m) => [m[1]!, m[2]!.trim()]));
}

test("every variable the worker reads is documented, and every documented one is read", () => {
  const read = variablesRead();
  const documented = new Set(variablesDocumented().keys());
  assert.deepEqual([...read].filter((v) => !documented.has(v)).sort(), [], "read but not in worker/README.md");
  assert.deepEqual([...documented].filter((v) => !read.has(v)).sort(), [], "in worker/README.md but never read");
});

test("a documented default is what loadConfig already uses", () => {
  for (const [name, cell] of variablesDocumented()) {
    const literal = /^`([^`]*)`$/.exec(cell)?.[1];
    if (literal === undefined) continue;
    const roles = name.includes("<ROLE>") ? ["EMBED", "RERANK", "GENERATE"] : [""];
    for (const role of roles) {
      const variable = name.replace("<ROLE>", role);
      assert.deepEqual(loadConfig({ [variable]: literal }), loadConfig({}), `${variable} documented as ${cell}`);
    }
  }
});
