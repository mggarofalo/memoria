/**
 * A missing QMD config must not cost the index its collection records (upstream
 * tobi/qmd#1048). Real SDK and the real `qmd` CLI on a throwaway index, no models.
 *
 * Opt in with MEMORIA_QMD_INTEGRATION=1 (CI sets it; `npm test` does not).
 */
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdir, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { openBackend } from "../src/store.ts";
import { tempDir } from "./helpers.ts";

const enabled = process.env.MEMORIA_QMD_INTEGRATION === "1";
const skip = enabled ? false : "set MEMORIA_QMD_INTEGRATION=1";

const Database = createRequire(import.meta.url)("better-sqlite3") as new (file: string, options?: { readonly?: boolean }) => {
  prepare(sql: string): { all(...a: unknown[]): unknown[]; get(...a: unknown[]): unknown; run(...a: unknown[]): unknown };
  close(): void;
};
const cli = fileURLToPath(new URL("./cli/qmd.js", import.meta.resolve("@tobilu/qmd")));

interface Snapshot {
  collections: Array<{ name: string; ignore: string | null; context: string | null }>;
  documents: Array<{ collection: string; n: number }>;
  globalContext: string | null;
}

function snapshot(dbPath: string): Snapshot {
  const db = new Database(dbPath, { readonly: true });
  try {
    return {
      collections: db.prepare("SELECT name, ignore_patterns AS ignore, context FROM store_collections ORDER BY name").all() as Snapshot["collections"],
      documents: db.prepare("SELECT collection, COUNT(*) AS n FROM documents WHERE active = 1 GROUP BY collection ORDER BY collection").all() as Snapshot["documents"],
      globalContext: (db.prepare("SELECT value FROM store_config WHERE key = 'global_context'").get() as { value: string } | undefined)?.value ?? null,
    };
  } finally {
    db.close();
  }
}

/** Two collections with a document each, a context, an ignore rule and a global context. */
async function fixture(): Promise<{ root: string; dbPath: string; before: Snapshot }> {
  const root = await tempDir("memoria-qmd-it-");
  const collections: Record<string, { path: string; pattern: string; ignore?: string[] }> = {};
  for (const name of ["alpha", "beta"]) {
    const dir = path.join(root, "corpus", name);
    await mkdir(dir, { recursive: true });
    await writeFile(path.join(dir, `${name}.md`), `# ${name}\n\n${name}marker\n`, "utf8");
    collections[name] = { path: dir, pattern: "**/*.md", ...(name === "beta" ? { ignore: ["drafts/**"] } : {}) };
  }
  // Where the CLI looks for it, given XDG_CACHE_HOME.
  const dbPath = path.join(root, "cache", "qmd", "index.sqlite");
  await mkdir(path.dirname(dbPath), { recursive: true });
  const qmd = await import("@tobilu/qmd");
  const store = await qmd.createStore({ dbPath, config: { collections } });
  await store.update();
  await store.addContext("alpha", "/", "alpha context");
  await store.setGlobalContext("global context");
  await store.close();
  const before = snapshot(dbPath);
  assert.equal(before.collections.length, 2);
  assert.equal(before.globalContext, "global context");
  return { root, dbPath, before };
}

test("createStore with a config file that is missing keeps every collection record", { skip }, async () => {
  const { root, dbPath, before } = await fixture();
  const qmd = await import("@tobilu/qmd");
  const store = await qmd.createStore({ dbPath, configPath: path.join(root, "config", "qmd", "index.yml") });
  await store.close();
  assert.deepEqual(snapshot(dbPath), before);
});

test("qmd's read commands with an empty config directory keep every collection record", { skip, timeout: 120_000 }, async () => {
  const { root, dbPath, before } = await fixture();
  // The image's /config: a directory with nothing in it.
  const configHome = path.join(root, "config");
  await mkdir(configHome, { recursive: true });
  const env: NodeJS.ProcessEnv = { ...process.env, XDG_CACHE_HOME: path.join(root, "cache"), XDG_CONFIG_HOME: configHome, NO_COLOR: "1" };
  delete env.INDEX_PATH;
  delete env.QMD_CONFIG_DIR;
  for (const command of [["status"], ["collection", "list"], ["ls"], ["search", "alphamarker"]]) {
    const run = spawnSync(process.execPath, [cli, ...command], { env, cwd: root, encoding: "utf8", timeout: 60_000 });
    assert.equal(run.status, 0, `qmd ${command.join(" ")}: ${run.stderr}`);
    assert.deepEqual(snapshot(dbPath), before, `qmd ${command.join(" ")}`);
  }
  // The CLI writes its models into the config, so from here on the config exists and names no collection.
  assert.ok(existsSync(path.join(configHome, "qmd", "index.yml")));
  const backend = await openBackend({ dbPath, keepWarm: false });
  try {
    assert.deepEqual((await backend.listCollections()).map((c) => c.name).sort(), ["alpha", "beta"]);
  } finally {
    await backend.close();
  }
});

test("the sidecar refuses an index whose collection records are gone, and changes nothing", { skip }, async () => {
  const { dbPath } = await fixture();
  // What stock QMD's sync does with a missing config, as a qmd installed elsewhere still would.
  const db = new Database(dbPath);
  db.prepare("DELETE FROM store_collections WHERE name = 'beta'").run();
  db.close();
  const lost = snapshot(dbPath);
  await assert.rejects(openBackend({ dbPath, keepWarm: false }), /no record of: beta\. .*Lost collection records/);
  assert.deepEqual(snapshot(dbPath), lost);
  // A collection removed with its documents leaves nothing to refuse.
  const remove = new Database(dbPath);
  remove.prepare("DELETE FROM documents WHERE collection = 'beta'").run();
  remove.close();
  const backend = await openBackend({ dbPath, keepWarm: false });
  await backend.close();
});
