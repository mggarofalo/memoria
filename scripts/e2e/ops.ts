/**
 * The control plane through the real CLI, API and sidecar: workers, the queue and
 * reindexing. The sidecar's one worker is the URL shorthand's, named by its host and
 * port, which nothing listens on. A refused caller must leave no call at the sidecar.
 */
import { pause, type Harness } from "./harness.ts";

/** The sidecar's name for the worker at MEMORIA_QMD_WORKER_URL, http://127.0.0.1:9. */
const WORKER = "127.0.0.1:9";

interface ListedWorker {
  name: string;
  state: string;
  drained: boolean;
}

interface ListedJob {
  id: string;
  state: string;
  collections: string[] | null;
  error: string | null;
}

interface Queue {
  paused: boolean;
  jobs: ListedJob[];
}

function workers(h: Harness, key: string): ListedWorker[] {
  const listed = h.memoria(["workers", "list", "--format", "json"], { key });
  h.check("ops: workers list is real JSON", listed.code === 0, listed.out + listed.err);
  return listed.code === 0 ? (JSON.parse(listed.out) as { workers: ListedWorker[] }).workers : [];
}

function queue(h: Harness, key: string): Queue {
  const status = h.memoria(["queue", "status", "--format", "json"], { key });
  return status.code === 0 ? (JSON.parse(status.out) as Queue) : { paused: false, jobs: [] };
}

function workerCalls(h: Harness, from: number): string[] {
  return h.proxied(from).map((r) => `${r.method} ${r.url}`).filter((call) => call.includes("/workers") || call.includes("/queue") || call.includes("/reindex"));
}

function drainAndEnable(h: Harness, ops: string): void {
  const before = workers(h, ops);
  h.check("ops: the pool lists the shorthand worker by host and port, not drained", before.length === 1 && before[0]?.name === WORKER && before[0]?.drained === false, before);

  const drained = h.memoria(["workers", "drain", WORKER], { key: ops });
  h.check("ops: workers drain says the worker is drained", drained.code === 0 && drained.out.includes("drained"), drained.out + drained.err);
  h.check("ops: workers list then shows it drained", workers(h, ops)[0]?.drained === true);
  const enabled = h.memoria(["workers", "enable", WORKER, "--format", "json"], { key: ops });
  h.check("ops: workers enable puts it back", enabled.code === 0 && (JSON.parse(enabled.out || "{}") as ListedWorker).drained === false, enabled.out + enabled.err);

  const ghost = h.memoria(["workers", "drain", "ghost"], { key: ops });
  h.check("ops: draining a name not in the pool is a real 404, exit 4", ghost.code === 4 && ghost.err.includes("ghost"), ghost.out + ghost.err);
}

async function refusedCallers(h: Harness, scoped: string, reader: string, ops: string): Promise<void> {
  const from = h.proxiedCount();
  const bound = h.memoria(["workers", "drain", WORKER], { key: scoped });
  h.check("ops: an admin key bound to collections cannot drain: 403, exit 3, with what to do", bound.code === 3 && bound.err.includes("--scope admin"), bound.out + bound.err);
  const malformed = await h.request({ path: "/v1/admin/reindex", method: "POST", key: scoped, body: "not an object" });
  h.check("ops: a bound admin key sending a malformed reindex body still gets 403 collection_scoped_key", malformed.status === 403 && malformed.body.includes("collection_scoped_key"), malformed);
  const notAdmin = h.memoria(["queue", "pause"], { key: reader });
  h.check("ops: a read key cannot pause the queue: 403, exit 3", notAdmin.code === 3, notAdmin.out + notAdmin.err);
  // The marker: a call that does reach the sidecar, so an empty record cannot pass for a refusal.
  h.memoria(["workers", "list"], { key: ops });
  const calls = workerCalls(h, from);
  h.check("ops: the refused callers left no call at the sidecar, and the marker did", calls.length === 1 && calls[0] === "GET /workers", calls);
}

async function waitForJob(h: Harness, key: string, id: string, state: string): Promise<ListedJob | undefined> {
  for (let attempt = 0; attempt < 100; attempt++) {
    const job = queue(h, key).jobs.find((j) => j.id === id);
    if (job?.state === state) return job;
    await pause(100);
  }
  return queue(h, key).jobs.find((j) => j.id === id);
}

/** Pause, then queue a scan, which must wait. Returns its id. */
async function pauseAndQueue(h: Harness, ops: string): Promise<string> {
  const paused = h.memoria(["queue", "pause"], { key: ops });
  h.check("ops: queue pause", paused.code === 0 && paused.out.includes("paused"), paused.out + paused.err);
  const queued = h.memoria(["reindex", "--collection", "notes", "--no-embed", "--format", "json"], { key: ops });
  const job = queued.code === 0 ? (JSON.parse(queued.out) as ListedJob) : undefined;
  h.check("ops: reindex queues a job for exactly the collection asked", job?.state === "queued" && job.collections?.join() === "notes", queued.out + queued.err);
  const id = job?.id ?? "";
  await pause(300);
  const held = queue(h, ops);
  h.check("ops: while paused, the job waits and queue status says paused", held.paused && held.jobs.find((j) => j.id === id)?.state === "queued", held);
  return id;
}

async function pauseReindexAndResume(h: Harness, ops: string): Promise<void> {
  const id = await pauseAndQueue(h, ops);
  const cancelled = h.memoria(["queue", "cancel", id], { key: ops });
  h.check("ops: queue cancel ends the waiting job", cancelled.code === 0 && cancelled.out.includes("Cancelled"), cancelled.out + cancelled.err);
  const again = h.memoria(["queue", "cancel", id], { key: ops });
  h.check("ops: cancelling a finished job is a real 409, exit 1", again.code === 1 && again.err.includes("409"), again.out + again.err);
  const retried = h.memoria(["queue", "retry", id], { key: ops });
  h.check("ops: queue retry queues the cancelled job again under its id", retried.code === 0 && retried.out.includes(id), retried.out + retried.err);

  const resumed = h.memoria(["queue", "resume"], { key: ops });
  h.check("ops: queue resume", resumed.code === 0 && resumed.out.includes("running"), resumed.out + resumed.err);
  const done = await waitForJob(h, ops, id, "succeeded");
  h.check("ops: after resume the job runs and succeeds", done?.state === "succeeded", done);
  h.check("ops: the queue reads as running again", queue(h, ops).paused === false);
}

/** Logs are written as they come, so give each a moment to show the line. */
async function findInLogs(h: Harness, text: string): Promise<{ api: boolean; sidecar: boolean }> {
  const logged = { api: false, sidecar: false };
  for (let attempt = 0; attempt < 50 && text !== "" && !(logged.api && logged.sidecar); attempt++) {
    await pause(100);
    logged.api = h.log("api").includes(text);
    logged.sidecar = h.log("sidecar").includes(text);
  }
  return logged;
}

async function readiness(h: Harness, ops: string, reader: string): Promise<void> {
  const full = h.memoria(["ready"], { key: ops });
  h.check("ops: ready with an admin key reports the backend: no worker up, so degraded", full.code === 0 && full.out.includes("Status: degraded.") && full.out.includes("0 of 1 workers up"), full.out + full.err);
  const trace = /Trace: ([0-9a-f]{32})/.exec(full.out)?.[1] ?? "";
  const logged = await findInLogs(h, trace);
  h.check("ops: the trace id ready prints is in the API's log and the sidecar's", logged.api && logged.sidecar, { trace, logged, sidecarTail: h.log("sidecar").slice(-600) });
  const word = h.memoria(["ready"], { key: reader });
  h.check("ops: ready with a read key gets the status word only", word.code === 0 && word.out.includes("Status: degraded.") && !word.out.includes("workers up"), word.out + word.err);
  const strict = h.memoria(["ready", "--strict"], { key: ops });
  h.check("ops: ready --strict on a degraded server exits 5 and still says why", strict.code === 5 && strict.out.includes("0 of 1 workers up"), strict.out + strict.err);
}

export async function operations(h: Harness, admin: string): Promise<void> {
  const make = (args: string[]): string => h.remember(h.memoria(["keys", "create", ...args], { key: admin }).out.trim());
  const ops = make(["--label", "ops", "--scope", "admin"]);
  const scoped = make(["--label", "ops-scoped", "--scope", "admin", "--scope", "read", "--collection", "notes"]);
  const reader = make(["--label", "ops-reader", "--scope", "read", "--collection", "notes"]);
  drainAndEnable(h, ops);
  await refusedCallers(h, scoped, reader, ops);
  await pauseReindexAndResume(h, ops);
  await readiness(h, ops, reader);
}
