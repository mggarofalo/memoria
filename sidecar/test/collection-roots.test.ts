import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import { test } from "node:test";
import { collectionsToKeep, markRoot, ROOT_MARKER, rootState } from "../src/collection-roots.ts";
import { isRootEmpty } from "../src/ingest.ts";
import type { PurgeDb } from "../src/purge.ts";
import { tempDir } from "./helpers.ts";

const Database = createRequire(import.meta.url)("better-sqlite3") as new (file: string) => PurgeDb;

test("a root is missing, unmarked or marked, and a file where a folder should be counts as missing", async () => {
  const dir = await tempDir("memoria-roots-");
  assert.equal(rootState(path.join(dir, "absent")), "missing");
  await writeFile(path.join(dir, "a-file"), "x");
  assert.equal(rootState(path.join(dir, "a-file")), "missing");
  const root = path.join(dir, "root");
  await mkdir(root);
  assert.equal(rootState(root), "unmarked");
  markRoot(root);
  assert.equal(rootState(root), "marked");
  const text = await readFile(path.join(root, ROOT_MARKER), "utf8");
  markRoot(root);
  assert.equal(await readFile(path.join(root, ROOT_MARKER), "utf8"), text, "marking again changes nothing");
  markRoot(path.join(dir, "absent"));
  assert.equal(existsSync(path.join(dir, "absent")), false, "a missing root is not created");
});

test("a root holding only its marker still counts as empty for a first ingest", async () => {
  const root = path.join(await tempDir("memoria-roots-"), "root");
  await mkdir(root);
  markRoot(root);
  assert.equal(await isRootEmpty(root), true);
  await writeFile(path.join(root, "note.md"), "x");
  assert.equal(await isRootEmpty(root), false);
});

test("after a scan, roots with documents are marked, and only a collection found empty with its marker is purged", async () => {
  const dir = await tempDir("memoria-roots-");
  const roots = { live: path.join(dir, "live"), emptied: path.join(dir, "emptied"), unmarked: path.join(dir, "unmarked"), gone: path.join(dir, "gone") };
  for (const name of ["live", "emptied", "unmarked"] as const) await mkdir(roots[name]);
  markRoot(roots.emptied);
  const db = new Database(":memory:");
  db.exec("CREATE TABLE documents (collection TEXT, path TEXT, hash TEXT, active INTEGER)");
  for (const [collection, active] of [["live", 1], ["live", 0], ["emptied", 0], ["unmarked", 0], ["gone", 0], ["unrecorded", 0]] as const) {
    db.prepare("INSERT INTO documents VALUES (?, 'a.md', 'h', ?)").run(collection, active);
  }
  const warnings: string[] = [];
  const keep = collectionsToKeep(db, Object.entries(roots).map(([name, root]) => ({ name, path: root })), (m) => warnings.push(m));
  assert.deepEqual(keep.sort(), ["gone", "unmarked", "unrecorded"]);
  assert.equal(rootState(roots.live), "marked", "a root holding documents is marked");
  assert.equal(warnings.length, 3);
  assert.ok(warnings.some((w) => w.startsWith("Collection gone:") && /missing or cannot be read/.test(w)));
  assert.ok(warnings.some((w) => w.startsWith("Collection unmarked:") && w.includes(ROOT_MARKER)));
});
