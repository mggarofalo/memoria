/**
 * Generate sidecar/openapi.json from the route schemas, or with --check
 * verify the committed file is current. Uses the fake backend so no index or
 * models are needed.
 */
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { buildApp } from "../src/app.ts";
import { loadConfig } from "../src/config.ts";
import { FakeBackend } from "../test/fake-backend.ts";

const here = path.dirname(fileURLToPath(import.meta.url));
const target = path.resolve(here, "..", "openapi.json");

export async function generateOpenApi(): Promise<string> {
  const backend = new FakeBackend({ collections: [], docs: [] });
  const app = await buildApp({ backend, config: loadConfig({ MEMORIA_QMD_SECRET: "openapi-generation-secret" }), logger: false });
  await app.ready();
  const doc = app.swagger();
  await app.close();
  return JSON.stringify(doc, null, 2) + "\n";
}

const check = process.argv.includes("--check");
const generated = await generateOpenApi();
if (check) {
  let existing = "";
  try {
    existing = await readFile(target, "utf8");
  } catch {
    console.error(`openapi.json is missing at ${target}; run \`npm run openapi\``);
    process.exit(1);
  }
  if (existing.replace(/\r\n/g, "\n") !== generated) {
    console.error("openapi.json is stale; run `npm run openapi` and commit the result");
    process.exit(1);
  }
  console.log("openapi.json is current");
} else {
  await writeFile(target, generated, "utf8");
  console.log(`wrote ${target}`);
}
