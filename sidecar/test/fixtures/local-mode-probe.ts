/**
 * Run as its own process so module loading can be observed from the very start.
 * Opens a backend in the default mode with a worker URL sitting in the
 * environment, exercises everything that needs no model, and reports which
 * adapter modules were loaded and what the process tried to fetch.
 *
 * argv: <dbPath>. Writes one JSON line to stdout.
 */
import { registerHooks } from "node:module";

const loaded: string[] = [];
registerHooks({
  load(url, context, next) {
    if (url.includes("/src/llm/")) loaded.push(url);
    return next(url, context);
  },
});

const fetched: string[] = [];
globalThis.fetch = (async (input: unknown) => {
  fetched.push(String(input));
  throw new Error("local mode must not use the network");
}) as typeof globalThis.fetch;

const { loadConfig } = await import("../../src/config.ts");
const { openBackend } = await import("../../src/store.ts");

const config = loadConfig();
const backend = await openBackend({ dbPath: process.argv[2], keepWarm: false });
await backend.update({});
const hits = await backend.search({ queries: [{ type: "lex", query: "AuthenticationHandler" }], limit: 5, minScore: 0, rerank: false });
const models = backend.models();
await backend.close();

console.log(JSON.stringify({ mode: config.llm.mode, probeMode: backend.llm.mode, transport: backend.llm.rerankTransport, loaded, fetched, hits: hits.length, models, forceCpu: process.env.QMD_FORCE_CPU ?? null }));
process.exit(0);
