import assert from "node:assert/strict";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";
import { test } from "node:test";
import { requireKnownSchema, SUPPORTED_INDEX_SCHEMA } from "../src/store.ts";
import { tempDir } from "./helpers.ts";

async function indexWith(setup: string): Promise<string> {
  const file = path.join(await tempDir("memoria-schema-"), "index.sqlite");
  const db = new DatabaseSync(file);
  db.exec(`CREATE TABLE documents (id INTEGER PRIMARY KEY); ${setup}`);
  db.close();
  return file;
}

test("an index at or below the SDK's schema version opens", async () => {
  for (const version of [0, SUPPORTED_INDEX_SCHEMA]) {
    requireKnownSchema(await indexWith(`PRAGMA user_version = ${version};`));
  }
});

test("an index a newer QMD migrated is refused, naming what gave it away", async () => {
  const newer = await indexWith(`PRAGMA user_version = ${SUPPORTED_INDEX_SCHEMA + 1};`);
  assert.throws(() => requireKnownSchema(newer), new RegExp(`schema version ${SUPPORTED_INDEX_SCHEMA + 1},.*writes version ${SUPPORTED_INDEX_SCHEMA}`));
  const replaced = await indexWith("CREATE TABLE vector_rows (hash_seq TEXT PRIMARY KEY);");
  assert.throws(() => requireKnownSchema(replaced), /vector_rows table/);
});

test("the check only reads the index", async () => {
  const file = await indexWith(`PRAGMA user_version = ${SUPPORTED_INDEX_SCHEMA + 1};`);
  assert.throws(() => requireKnownSchema(file));
  const db = new DatabaseSync(file, { readOnly: true });
  assert.equal((db.prepare("PRAGMA user_version").get() as { user_version: number }).user_version, SUPPORTED_INDEX_SCHEMA + 1);
  db.close();
});
