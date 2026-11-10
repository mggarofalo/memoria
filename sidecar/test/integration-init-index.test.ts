/**
 * `scripts/init-index.mjs` is the first-run command, and it is safe to run again: with fewer
 * names, other names or another order, it adds and never takes away. Real SDK, no models.
 *
 * Opt in with MEMORIA_QMD_INTEGRATION=1 (CI sets it; `npm test` does not).
 */
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdir, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { tempDir } from "./helpers.ts";

const enabled = process.env.MEMORIA_QMD_INTEGRATION === "1";
const skip = enabled ? false : "set MEMORIA_QMD_INTEGRATION=1";
const script = fileURLToPath(new URL("../scripts/init-index.mjs", import.meta.url));

const Database = createRequire(import.meta.url)("better-sqlite3") as new (file: string, options?: { readonly?: boolean }) => {
  prepare(sql: string): { all(...a: unknown[]): unknown[]; get(...a: unknown[]): unknown; run(...a: unknown[]): unknown };
  close(): void;
};

interface Snapshot {
  collections: Array<{ name: string; path: string; pattern: string; ignore: string | null; context: string | null }>;
  documents: Array<{ collection: string; path: string; active: number }>;
  globalContext: string | null;
}

function snapshot(dbPath: string): Snapshot {
  const db = new Database(dbPath, { readonly: true });
  try {
    return {
      collections: db.prepare("SELECT name, path, pattern, ignore_patterns AS ignore, context FROM store_collections ORDER BY name").all() as Snapshot["collections"],
      documents: db.prepare("SELECT collection, path, active FROM documents ORDER BY collection, path").all() as Snapshot["documents"],
      globalContext: (db.prepare("SELECT value FROM store_config WHERE key = 'global_context'").get() as { value: string } | undefined)?.value ?? null,
    };
  } finally {
    db.close();
  }
}

function init(env: NodeJS.ProcessEnv, ...names: string[]): { status: number | null; stdout: string; stderr: string } {
  return spawnSync(process.execPath, [script, ...names], { env, encoding: "utf8", timeout: 60_000 });
}

test("running the first-run command again with other names, fewer or in another order, only adds", { skip, timeout: 120_000 }, async () => {
  const root = await tempDir("memoria-qmd-it-");
  const corpus = path.join(root, "corpus");
  const dbPath = path.join(root, "index", "qmd", "index.sqlite");
  await mkdir(path.dirname(dbPath), { recursive: true });
  // A config already on the config volume, which must stay byte for byte.
  const configHome = path.join(root, "config");
  await mkdir(path.join(configHome, "qmd"), { recursive: true });
  const config = "collections:\n  elsewhere:\n    path: /somewhere/else\n    pattern: '**/*.md'\nglobal_context: from the file\n";
  await writeFile(path.join(configHome, "qmd", "index.yml"), config, "utf8");
  const env: NodeJS.ProcessEnv = { ...process.env, MEMORIA_QMD_DB_PATH: dbPath, MEMORIA_QMD_INIT_CORPUS: corpus, XDG_CONFIG_HOME: configHome, XDG_CACHE_HOME: path.join(root, "index") };
  delete env.QMD_CONFIG_DIR;
  delete env.INDEX_PATH;

  const first = init(env, "alpha", "beta");
  assert.equal(first.status, 0, first.stderr);
  assert.match(first.stdout, /holds: alpha, beta\n/);
  assert.deepEqual(snapshot(dbPath).collections.map((c) => [c.name, c.path]), [["alpha", path.join(corpus, "alpha")], ["beta", path.join(corpus, "beta")]]);

  // Life after the first run: documents, an ignore rule, a context and a global context.
  await writeFile(path.join(corpus, "beta", "kept.md"), "# Kept\n\nbetamarker\n", "utf8");
  await mkdir(path.join(corpus, "beta", "drafts"), { recursive: true });
  await writeFile(path.join(corpus, "beta", "drafts", "secret.md"), "# Draft\n\ndraftmarker\n", "utf8");
  const qmd = await import("@tobilu/qmd");
  const store = await qmd.createStore({ dbPath });
  (store.internal.db as unknown as { prepare(sql: string): { run(...a: unknown[]): unknown } }).prepare("UPDATE store_collections SET ignore_patterns = ? WHERE name = 'beta'").run(JSON.stringify(["drafts/**"]));
  await store.update();
  await store.addContext("beta", "/", "beta context");
  await store.setGlobalContext("global context");
  await store.close();
  const lived = snapshot(dbPath);
  assert.deepEqual(lived.documents, [{ collection: "beta", path: "kept.md", active: 1 }], "the ignore rule keeps the draft out");

  // Fewer names, then the same names in another order, then a new one among them.
  for (const names of [["alpha"], ["beta", "alpha"], ["gamma", "alpha"]]) {
    const run = init(env, ...names);
    assert.equal(run.status, 0, run.stderr);
    const after = snapshot(dbPath);
    assert.deepEqual(after.collections.filter((c) => c.name !== "gamma"), lived.collections, `records after ${names.join(" ")}`);
    assert.deepEqual(after.documents, lived.documents, `documents after ${names.join(" ")}`);
    assert.equal(after.globalContext, "global context", `global context after ${names.join(" ")}`);
  }
  const final = snapshot(dbPath);
  assert.deepEqual(final.collections.map((c) => c.name), ["alpha", "beta", "gamma"]);
  assert.ok(existsSync(path.join(corpus, "gamma")));
  assert.equal(readFileSync(path.join(configHome, "qmd", "index.yml"), "utf8"), config, "the config file is not read or written");
  const again = init(env, "beta");
  assert.match(again.stdout, /holds: beta, alpha, gamma\n/);
  assert.match(again.stdout, /added: none; already there, left as they are: beta/);
});

test("the first-run command refuses a name that is not a plain folder name, and changes nothing", { skip }, async () => {
  const root = await tempDir("memoria-qmd-it-");
  const dbPath = path.join(root, "index.sqlite");
  const env: NodeJS.ProcessEnv = { ...process.env, MEMORIA_QMD_DB_PATH: dbPath, MEMORIA_QMD_INIT_CORPUS: path.join(root, "corpus") };
  for (const bad of ["../escape", ".hidden", "a/b", "a\\b", ""]) {
    const run = init(env, "fine", bad);
    assert.equal(run.status, 2, bad);
    assert.match(run.stderr, /Not a collection name/);
  }
  assert.equal(existsSync(dbPath), false);
  assert.equal(init({ ...env, MEMORIA_QMD_DB_PATH: "" }, "fine").status, 2);
  assert.equal(init(env).status, 2);
});
