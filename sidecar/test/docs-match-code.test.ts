import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { loadConfig } from "../src/config.ts";

/**
 * The docs' tables against the code. Every MEMORIA_QMD_* the sidecar reads is in a table in
 * sidecar/README.md or docs/workers.md and nothing else is, and a default written in backticks
 * leaves loadConfig unchanged when set. Every operation in openapi.json is in the README's route
 * table, and every error code the README gives with a status exists in src.
 */

const repo = fileURLToPath(new URL("../../", import.meta.url));
const read = (...parts: string[]): string => readFileSync(path.join(repo, ...parts), "utf8");
const DOCS = [read("sidecar", "README.md"), read("docs", "workers.md")];

function sources(dir: string): string {
  return readdirSync(dir, { withFileTypes: true, recursive: true })
    .filter((e) => e.isFile() && e.name.endsWith(".ts"))
    .map((e) => readFileSync(path.join(e.parentPath, e.name), "utf8"))
    .join("\n");
}

const SRC = sources(path.join(repo, "sidecar", "src"));

/** Names the code reads: each literal, and the `_FILE` form of a secret it resolves. */
function variablesRead(): Set<string> {
  const names = new Set(SRC.match(/MEMORIA_QMD_[A-Z0-9_]+/g));
  for (const m of SRC.matchAll(/resolveSecret\(env, "(MEMORIA_QMD_[A-Z0-9_]+)"/g)) names.add(`${m[1]}_FILE`);
  return names;
}

/** Variable to default cell, from table rows that start with a backticked MEMORIA_QMD_ name. */
function variablesDocumented(): Map<string, string> {
  const rows = new Map<string, string>();
  for (const doc of DOCS) {
    for (const m of doc.matchAll(/^\| `(MEMORIA_QMD_[A-Z0-9_]+)` \| ([^|]*) \|/gm)) rows.set(m[1]!, m[2]!.trim());
  }
  return rows;
}

test("every variable the sidecar reads is documented, and every documented one is read", () => {
  const read = variablesRead();
  const documented = new Set(variablesDocumented().keys());
  assert.deepEqual([...read].filter((v) => !documented.has(v)).sort(), [], "read but not in a doc table");
  assert.deepEqual([...documented].filter((v) => !read.has(v)).sort(), [], "in a doc table but never read");
});

test("a documented default is what loadConfig already uses", () => {
  const secret = { MEMORIA_QMD_SECRET: "Xk29vQmB7fLp3ZtRwq8N" };
  const bases = [secret, { ...secret, MEMORIA_QMD_LLM: "remote", MEMORIA_QMD_WORKER_URL: "http://127.0.0.1:8383" }];
  for (const [name, cell] of variablesDocumented()) {
    const literal = /^`([^`]*)`$/.exec(cell)?.[1];
    if (literal === undefined) continue;
    for (const base of bases) {
      if (name in base) continue;
      assert.deepEqual(loadConfig({ ...base, [name]: literal }), loadConfig(base), `${name} documented as ${cell}`);
    }
  }
});

test("every operation in openapi.json is in the README's route table, and every row is an operation", () => {
  const spec = JSON.parse(read("sidecar", "openapi.json")) as { paths: Record<string, Record<string, unknown>> };
  const operations = new Set(Object.entries(spec.paths).flatMap(([p, methods]) => Object.keys(methods).map((m) => `${m.toUpperCase()} ${p}`)));
  const documented = new Set<string>();
  for (const row of DOCS[0]!.matchAll(/^\| (`(?:GET|POST|PUT|PATCH|DELETE) [^|]*)\|/gm)) {
    for (const route of row[1]!.matchAll(/`((?:GET|POST|PUT|PATCH|DELETE) [^`?]+)[^`]*`/g)) documented.add(route[1]!);
  }
  assert.deepEqual([...operations].filter((o) => !documented.has(o)).sort(), [], "missing from sidecar/README.md");
  assert.deepEqual([...documented].filter((o) => !operations.has(o)).sort(), [], "not in sidecar/openapi.json");
});

test("every error code the README gives with a status exists in src", () => {
  const codes = new Set([...DOCS[0]!.matchAll(/\b[45]\d\d `([a-z_]+)`/g)].map((m) => m[1]!));
  assert.ok(codes.size > 5, "found the README's error codes");
  assert.deepEqual([...codes].filter((c) => !SRC.includes(`"${c}"`)).sort(), [], "documented but not in sidecar/src");
});
