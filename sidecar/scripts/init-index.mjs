// First run in a container: makes the index with one empty collection under /corpus per name.
//
// Safe to run again, with any names in any order. It records only the collections the index
// does not have yet, and scans only those. A collection the index already has is left exactly
// as it is: its root, rules, contexts and documents. Other collections are never touched, and
// no config file is read or written. No model is involved.
//
// MEMORIA_QMD_INIT_CORPUS moves the parent of the roots from /corpus, for tests.
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { createStore } from "@tobilu/qmd";

const names = [...new Set(process.argv.slice(2))];
if (names.length === 0) {
  console.error("Name at least one collection: init-index.mjs <name> [<name> ...]");
  process.exit(2);
}
// A name becomes a folder under the corpus, so it may not leave it or hide in it.
const unsafe = names.filter((name) => !/^[A-Za-z0-9_][A-Za-z0-9._-]{0,63}$/.test(name));
if (unsafe.length > 0) {
  console.error(`Not a collection name: ${unsafe.join(", ")}. Use up to 64 letters, digits, '.', '_' and '-', not starting with '.' or '-'.`);
  process.exit(2);
}
const dbPath = process.env.MEMORIA_QMD_DB_PATH;
if (!dbPath) {
  console.error("Set MEMORIA_QMD_DB_PATH to the index's path. The image sets it.");
  process.exit(2);
}
const corpus = process.env.MEMORIA_QMD_INIT_CORPUS || "/corpus";

// No config here: given one, QMD rewrites every record it names and deletes every record it
// does not, contexts and ignore rules included.
const store = await createStore({ dbPath });
// As the sidecar does: what a scan later deletes, and what a write moves, is overwritten, not left in free pages.
store.internal.db.exec("PRAGMA secure_delete = ON");
const added = [];
try {
  const before = await store.listCollections();
  const known = new Map(before.map((c) => [c.name, c.pwd]));
  const missing = [];
  for (const name of names) {
    const root = path.join(corpus, name);
    if (!known.has(name)) {
      missing.push({ name, root });
      continue;
    }
    if (known.get(name) !== root) console.warn(`${name} is kept at ${known.get(name)}, not moved to ${root}.`);
    if (!existsSync(known.get(name))) console.warn(`${name}'s root ${known.get(name)} does not exist. Is the corpus volume mounted?`);
  }
  // Every root first, so a failure here leaves no collection recorded and never scanned.
  // A root made here gets the sidecar's marker (src/collection-roots.ts), so a scan can tell it emptied by deletes from a mount
  // that failed. One already there is left as it is: it may be the empty folder under a mount that is down right now.
  for (const { root } of missing) {
    if (existsSync(root)) continue;
    mkdirSync(root, { recursive: true });
    writeFileSync(path.join(root, ".memoria-root"), "This folder is a Memoria collection root. The sidecar keeps this file to tell a folder emptied by deletes from a mount that is missing. Leave it here.\n", { flag: "wx" });
  }
  for (const { name, root } of missing) {
    await store.addCollection(name, { path: root, pattern: "**/*.md" });
    added.push(name);
  }
  const others = before.map((c) => c.name).filter((name) => !names.includes(name)).sort();
  console.log(`index at ${dbPath} holds: ${[...names, ...others].join(", ")}`);
  console.log(`added: ${added.join(", ") || "none"}; already there, left as they are: ${names.filter((n) => !added.includes(n)).join(", ") || "none"}`);
} finally {
  // Whatever was recorded is scanned, even if a later step failed.
  try {
    if (added.length > 0) await store.update({ collections: added });
  } finally {
    await store.close();
  }
}
