/**
 * The schema version the sidecar accepts is the one the pinned SDK writes, and a sidecar given an
 * index a newer QMD migrated stops before the SDK opens it.
 *
 * Opt in with MEMORIA_QMD_INTEGRATION=1 (CI sets it; `npm test` does not).
 */
import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";
import { test } from "node:test";
import { openBackend, SUPPORTED_INDEX_SCHEMA } from "../src/store.ts";
import { tempDir } from "./helpers.ts";

const skip = process.env.MEMORIA_QMD_INTEGRATION === "1" ? false : "set MEMORIA_QMD_INTEGRATION=1";

async function freshIndex(): Promise<string> {
  const root = await tempDir("memoria-schema-it-");
  const corpus = path.join(root, "notes");
  await mkdir(corpus, { recursive: true });
  const dbPath = path.join(root, "index.sqlite");
  const qmd = await import("@tobilu/qmd");
  const builder = await qmd.createStore({ dbPath, config: { collections: { notes: { path: corpus, pattern: "**/*.md" } } } });
  await builder.update();
  await builder.close();
  return dbPath;
}

const userVersion = (dbPath: string): number => {
  const db = new DatabaseSync(dbPath, { readOnly: true });
  try {
    return Number((db.prepare("PRAGMA user_version").get() as { user_version: number }).user_version);
  } finally {
    db.close();
  }
};

test("the pinned SDK writes the schema version the sidecar accepts", { skip }, async () => {
  const dbPath = await freshIndex();
  assert.equal(userVersion(dbPath), SUPPORTED_INDEX_SCHEMA, "a QMD bump that moves the schema must move SUPPORTED_INDEX_SCHEMA with it");
  const backend = await openBackend({ dbPath, keepWarm: false, llm: { mode: "local" } });
  await backend.close();
});

test("a sidecar given an index a newer QMD migrated stops, and leaves it as it was", { skip }, async () => {
  const dbPath = await freshIndex();
  const db = new DatabaseSync(dbPath);
  db.exec(`PRAGMA user_version = ${SUPPORTED_INDEX_SCHEMA + 1}`);
  db.close();
  await assert.rejects(openBackend({ dbPath, keepWarm: false, llm: { mode: "local" } }), /migrated by a newer QMD/);
  assert.equal(userVersion(dbPath), SUPPORTED_INDEX_SCHEMA + 1);
});
