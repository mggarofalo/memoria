// First run in a container: makes the index with one empty collection under /corpus per name.
// Safe to run again: it adds what is missing and keeps what is there. No model is involved.
import { mkdirSync } from "node:fs";
import { createStore } from "@tobilu/qmd";

const names = process.argv.slice(2);
if (names.length === 0) {
  console.error("Name at least one collection: init-index.mjs <name> [<name> ...]");
  process.exit(2);
}

const dbPath = process.env.MEMORIA_QMD_DB_PATH;
const collections = {};
for (const name of names) {
  const root = `/corpus/${name}`;
  mkdirSync(root, { recursive: true });
  collections[name] = { path: root, pattern: "**/*.md" };
}

const store = await createStore({ dbPath, config: { collections } });
await store.update();
await store.close();
console.log(`index at ${dbPath} holds: ${names.join(", ")}`);
