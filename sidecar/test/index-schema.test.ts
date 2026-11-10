import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { writeFile } from "node:fs/promises";
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
  assert.throws(() => requireKnownSchema(newer), new RegExp(`schema version ${SUPPORTED_INDEX_SCHEMA + 1},.*writes schema version ${SUPPORTED_INDEX_SCHEMA}`));
  const replaced = await indexWith("CREATE TABLE vector_rows (hash_seq TEXT PRIMARY KEY);");
  assert.throws(() => requireKnownSchema(replaced), /migrated by a newer QMD: it has schema version 0 and a vector_rows table/);
});

test("an index caught partway through a newer QMD's upgrade is refused as that", async () => {
  const partway = await indexWith(`PRAGMA user_version = ${SUPPORTED_INDEX_SCHEMA}; CREATE TABLE vectors_vec (hash_seq TEXT); CREATE TABLE vector_rows (hash_seq TEXT);`);
  assert.throws(() => requireKnownSchema(partway), /partway through a newer QMD's upgrade: it has schema version 1 with both vector_rows and vectors_vec/);
});

test("a file that is not an index is refused with its path", async () => {
  const dir = await tempDir("memoria-schema-");
  const junk = path.join(dir, "index.sqlite");
  await writeFile(junk, "not a database at all, and long enough to be read as one ".repeat(20));
  assert.throws(() => requireKnownSchema(junk), (err: Error) => err.message.startsWith(`The index at ${junk} could not be read to check its schema:`));
});

test("a write in progress in another process is waited for, not taken as a failure", async () => {
  const file = await indexWith("PRAGMA journal_mode = DELETE;");
  const holder = spawn(process.execPath, ["-e", `
    const { DatabaseSync } = require("node:sqlite");
    const db = new DatabaseSync(process.argv[1]);
    db.exec("BEGIN EXCLUSIVE; INSERT INTO documents VALUES (1);");
    console.log("locked");
    setTimeout(() => { db.exec("COMMIT"); db.close(); }, 800);
  `, file], { stdio: ["ignore", "pipe", "inherit"] });
  const exited = new Promise((resolve) => holder.once("exit", resolve));
  await new Promise<void>((resolve) => holder.stdout.once("data", () => resolve()));
  const started = Date.now();
  requireKnownSchema(file);
  assert.ok(Date.now() - started >= 300, "it waited for the write");
  await exited;
});

test("a refused index keeps its schema version", async () => {
  const file = await indexWith(`PRAGMA user_version = ${SUPPORTED_INDEX_SCHEMA + 1};`);
  assert.throws(() => requireKnownSchema(file));
  const db = new DatabaseSync(file, { readOnly: true });
  assert.equal((db.prepare("PRAGMA user_version").get() as { user_version: number }).user_version, SUPPORTED_INDEX_SCHEMA + 1);
  db.close();
});
