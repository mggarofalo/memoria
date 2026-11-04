/**
 * Run as its own process, so a test can kill it with the file lock held. Opens a queue
 * file as a sidecar would, accepts a job that never finishes, and says when it is ready.
 *
 * argv: <queueFile>. Writes "held" to stdout, then waits to be killed.
 */
import { JobQueue } from "../../src/jobs.ts";
import { openJobStore } from "../../src/queue-store.ts";

const { store } = openJobStore(process.argv[2]!, (message) => console.error(message));
if (!store) process.exit(2);
const queue = new JobQueue(() => new Promise(() => {}), { store });
queue.enqueue({ collections: ["held"], embed: true, force: false, reason: "accepted before the kill" });
console.log("held");
setInterval(() => {}, 1000);
