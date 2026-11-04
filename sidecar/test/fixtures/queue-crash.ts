/**
 * The sidecar's queue in its own process, to be killed outright by a test.
 * Accepts a document, optionally starts a job that never ends, then reports what
 * it holds and waits to be killed.
 *
 * argv: <queueDb> <collectionRoot> <mode: held | running>
 */
import { buildApp } from "../../src/app.ts";
import { SqliteJobStore } from "../../src/queue-store.ts";
import { FakeBackend } from "../fake-backend.ts";
import { authHeaders, testConfig } from "../helpers.ts";

const [queueDb, root, mode] = process.argv.slice(2) as [string, string, string];
const backend = new FakeBackend({ collections: [{ name: "c", root }], docs: [] });
const jobStore = new SqliteJobStore(queueDb);
const app = await buildApp({ backend, config: testConfig({ reindexDebounceMs: mode === "held" ? 3_600_000 : 0, reindexMaxWaitMs: 3_600_000 }), jobStore, logger: false });
if (mode === "running") backend.updateGate = new Promise(() => {});
const response = await app.inject({
  method: "POST",
  url: "/documents",
  headers: authHeaders,
  payload: { collection: "c", path: "survivor.md", content: "# Survivor\n\nPRIVATE-BODY-TEXT\n" },
});
if (mode === "running") await new Promise((r) => setTimeout(r, 50));
console.log(JSON.stringify({ status: response.statusCode, id: response.json().job.id, state: app.ctx.jobs.get(response.json().job.id)?.state }));
setInterval(() => {}, 60_000);
