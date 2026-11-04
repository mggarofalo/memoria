/**
 * The embedding farm in its own process, to be killed outright by a test. Each
 * start finds whatever the last one left pending, so a test can kill it many times.
 *
 * argv: <dbPath> <workerUrl>. Prints a line when it starts writing.
 */
import { openBackend } from "../../src/store.ts";

const [dbPath, url] = process.argv.slice(2) as [string, string];
const backend = await openBackend({
  dbPath,
  keepWarm: false,
  llm: {
    mode: "remote",
    workers: [{ name: "w", url, token: undefined, capabilities: ["embed"], priority: 1, maxConcurrent: 4 }],
    embedDimensions: 768,
    fallback: false,
    statusPollMs: 0,
    embedBatchBytes: 1024,
  },
});
console.log("writing");
await backend.embed({ force: true });
setInterval(() => {}, 60_000);
