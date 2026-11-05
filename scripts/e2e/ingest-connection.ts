/**
 * Writes the API has lost control of. The sidecar's stall is injected (see stall-writes.mjs), answers go missing at the proxy, and the API is really killed.
 * A fake sidecar answers in order, so neither test suite can show these. The proxy passes a closed connection on, as a direct connection would.
 */
import { existsSync, mkdirSync, readFileSync, symlinkSync, writeFileSync } from "node:fs";
import http from "node:http";
import path from "node:path";
import { pause, type ApiKeyMaker, type Harness } from "./harness.ts";
import { code, deleteById, documentPath, folder, headers, line, postDocument, postSession, SAFE, send, textOf, type Answer } from "./ingest-races.ts";

const VERSION = "2026-02-01T00:00:00Z";
const LATER = "2026-02-01T01:00:00Z";
const LATEST = "2026-02-01T02:00:00Z";
/** Far longer than any allowance for clocks would have been, which is the point: there is no clock. */
const LONG_STALL_MS = 46_000;
const STALL_MS = 5_000;
/** Longer than INGEST_SERVER's Sidecar:Timeout. */
const PAST_TIMEOUT_MS = 17_000;
const LARGE_BYTES = 8 * 1024 * 1024;

const fileOf = (h: Harness, sourceId: string): string => path.join(folder(h), documentPath(sourceId));
const swallowed = (promise: Promise<Answer>): Promise<Answer | null> => promise.catch(() => null);
const deleted = (answer: Answer): boolean | undefined => (JSON.parse(answer.text) as { deleted?: boolean }).deleted;

/** Starts a write and holds it in the sidecar, then lets later writes through. The held one is still held. */
async function holdOneWrite(h: Harness, where: { before?: number; after?: number }, write: () => Promise<Answer>): Promise<{ answer: Promise<Answer | null>; started: number }> {
  h.stallWrites(where);
  const started = Date.now();
  const answer = swallowed(write());
  await pause(300);
  h.stallWrites({});
  return { answer, started };
}

async function until(started: number, ms: number): Promise<void> {
  await pause(Math.max(0, ms - (Date.now() - started)));
}

/** Findings 1 and 4: an overwrite is held, the API dies, the retry lands and a delete says done. The held write must not bring it back, however long it was held. */
async function restartThenDelete(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const key = await keys("restart-delete", ["ingest"]);
  const sourceId = "restart-delete";
  await postDocument(h, key, sourceId, "first", { updated: VERSION });
  const held = await holdOneWrite(h, { before: LONG_STALL_MS }, () => postDocument(h, key, sourceId, "secretword about to be deleted", { updated: LATER }));
  await h.restartApi();
  const retry = await postDocument(h, key, sourceId, "secretword about to be deleted", { updated: LATER });
  const gone = await deleteById(h, key, sourceId);
  await until(held.started, LONG_STALL_MS + 1_500);
  await held.answer;
  h.check("connection: a deleted secret stays deleted when a write held 46 s wakes after a restart", retry.status === 202 && deleted(gone) === true && !existsSync(fileOf(h, sourceId)), [retry.text, gone.text]);
}

async function restartThenNewerUpload(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const key = await keys("restart-upload", ["ingest"]);
  const sourceId = "restart-upload";
  await postDocument(h, key, sourceId, "first", { updated: VERSION });
  const held = await holdOneWrite(h, { before: STALL_MS }, () => postDocument(h, key, sourceId, "oldword from before the restart", { updated: LATER }));
  await h.restartApi();
  const newer = await postDocument(h, key, sourceId, "newword from after the restart", { updated: LATEST });
  await until(held.started, STALL_MS + 1_500);
  await held.answer;
  const text = textOf(fileOf(h, sourceId)) ?? "";
  h.check("connection: a held write does not replace a newer upload after a restart", newer.status === 202 && text.includes("newword") && !text.includes("oldword"), newer.text);
}

/** The whole body reaches the sidecar only after the API has died, with the close right behind it, as the kernel would deliver them. */
async function bodyArrivingAfterDeath(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const key = await keys("tail", ["ingest"]);
  const sourceId = "tail-after-death";
  await h.planProxy({ kind: "writes", mode: "holdTail", ms: 30_000, count: 1 });
  const posting = swallowed(postDocument(h, key, sourceId, `tailword ${"w".repeat(100_000)}`));
  await pause(300);
  await h.restartApi();
  await posting;
  await pause(1_000);
  h.check("connection: a body arriving with its close behind it stores nothing", !existsSync(fileOf(h, sourceId)));
  const retry = await postDocument(h, key, sourceId, "tailword");
  h.check("connection: and sending it again works", retry.status === 202, retry.text);
}

/** The API gives up on a write at Sidecar:Timeout while staying alive. HttpClient must close the socket, or the write lands with nobody expecting it. */
async function timeoutClosesTheSocket(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const key = await keys("timeout", ["ingest"]);
  const sourceId = "gave-up";
  const before = h.log("sidecar").length;
  const dropped = "the caller disconnected";
  const held = await holdOneWrite(h, { before: PAST_TIMEOUT_MS }, () => postDocument(h, key, sourceId, "timeoutword"));
  const answer = await held.answer;
  await until(held.started, PAST_TIMEOUT_MS + 1_500);
  const log = h.log("sidecar").slice(before);
  h.check("connection: a write the API timed out on does not land", answer?.status !== 202 && !existsSync(fileOf(h, sourceId)), answer?.status);
  h.check("connection: because the sidecar saw the socket close", log.includes(dropped), log.length);

  const control = await holdOneWrite(h, { before: 1_000 }, () => postDocument(h, key, "stayed", "controlword"));
  const kept = await control.answer;
  h.check("connection: a write stalled inside the timeout does land", kept?.status === 202 && existsSync(fileOf(h, "stayed")), kept?.status);
}

/** The documented limit, forced open: past the check, the API dies, a delete finds nothing, and the file lands. Repeating the delete clears it. */
async function repeatedDeleteClearsALateWrite(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const key = await keys("late", ["ingest"]);
  const sourceId = "late-write";
  const held = await holdOneWrite(h, { after: STALL_MS }, () => postDocument(h, key, sourceId, "secretword landing late", { updated: VERSION }));
  await h.restartApi();
  const first = await deleteById(h, key, sourceId);
  await until(held.started, STALL_MS + 1_000);
  await held.answer;
  const landed = existsSync(fileOf(h, sourceId));
  const repeated = await deleteById(h, key, sourceId);
  h.check("connection: the window is real when forced open", first.status === 202 && landed, first.text);
  h.check("connection: repeating the delete clears a late write", deleted(repeated) === true && !existsSync(fileOf(h, sourceId)), repeated.text);
}

async function resend(attempt: () => Promise<Answer | null>, times: number): Promise<boolean> {
  for (let sent = 0; sent < times; sent++) {
    const answer = await attempt().catch(() => null);
    if (answer?.status === 202) return true;
    await pause(200);
  }

  return false;
}

/** Finding 3: answers go missing though the writes land. Sending again must finish the job. */
async function lostAnswers(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const workers = await Promise.all(Array.from({ length: 4 }, (_, n) => keys(`lost-${n}`, ["ingest"])));
  await h.planProxy({ kind: "writes", mode: "drop", every: 8 });
  const stuck = await Promise.all(workers.map(async (key, worker) => {
    let count = 0;
    for (let n = 0; n < 20; n++) {
      if (!(await resend(() => postDocument(h, key, `lost-${worker}-${n}`, `body ${n}`), 6))) count++;
    }

    return count;
  }));
  const sessionKey = await keys("lost-sessions", ["ingest"]);
  let stuckSessions = 0;
  for (let n = 0; n < 16; n++) {
    const id = `bbbb${String(n).padStart(4, "0")}-1111-2222-3333-444444444444`;
    if (!(await resend(() => postSession(h, sessionKey, id, line("user", `session ${n}`)), 6))) stuckSessions++;
  }

  await h.planProxy({ kind: "writes", mode: "off" });
  const total = stuck.reduce((sum, count) => sum + count, 0);
  h.check("connection: a lost answer on a first write is finished by sending again", total === 0, `${total} of 80`);
  h.check("connection: the same for a session", stuckSessions === 0, `${stuckSessions} of 16`);

  await h.planProxy({ kind: "writes", mode: "drop", count: 1 });
  const key = await keys("lost-delete", ["ingest"]);
  await swallowed(postDocument(h, key, "lost-delete", "landed, and nobody was told"));
  await h.planProxy({ kind: "writes", mode: "off" });
  const landed = existsSync(fileOf(h, "lost-delete"));
  const removed = await deleteById(h, key, "lost-delete");
  h.check("connection: a delete removes a file whose answer was lost", landed && deleted(removed) === true && !existsSync(fileOf(h, "lost-delete")), removed.text);
}

/** Eight workers, and the API is killed three times under them. */
async function killsUnderLoad(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const workers = await Promise.all(Array.from({ length: 8 }, (_, n) => keys(`kills-${n}`, ["ingest"])));
  let writing = true;
  const stuck = workers.map(async (key, worker) => {
    let count = 0;
    for (let n = 0; writing && n < 60; n++) {
      if (!(await resend(() => postDocument(h, key, `kills-${worker}-${n}`, `killword ${"w".repeat(200_000)}`), 60))) count++;
    }

    return count;
  });
  for (let kill = 0; kill < 3; kill++) {
    await pause(800);
    await h.restartApi();
  }

  writing = false;
  const total = (await Promise.all(stuck)).reduce((sum, count) => sum + count, 0);
  h.check("connection: nothing is stuck after the API is killed three times under load", total === 0, total);
}

/** Finding 6: on Linux a name in another case is another file. A delete of it changes nothing about the document, and a delete by its real name still leaves a tombstone. */
async function aDeleteInAnotherCase(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const key = await keys("upper", ["ingest"]);
  const sourceId = "upper-case";
  const body = "secretword replayed";
  await postDocument(h, key, sourceId, body, { updated: VERSION });
  const upper = await send(`${h.apiUrl}/v1/documents?collection=${SAFE}&path=${documentPath(sourceId).toUpperCase()}`, { method: "DELETE", headers: headers(key) });
  h.check("connection: an upper-cased delete does not claim to have removed the document", upper.status !== 202 || deleted(upper) === false, upper.text);
  h.check("connection: and leaves it where it was", existsSync(fileOf(h, sourceId)));
  const real = await deleteById(h, key, sourceId);
  const replayed = await postDocument(h, key, sourceId, body, { updated: VERSION });
  h.check("connection: a delete by its real name leaves a tombstone, so a replay stays out", deleted(real) === true && replayed.status === 409 && !existsSync(fileOf(h, sourceId)), [real.text, replayed.text]);
}

function drip(h: Harness, key: string, sessionId: string, bytes: number): { stop: () => void; reached: Promise<void> } {
  const url = new URL(`${h.apiUrl}/v1/ingest/session?collection=${SAFE}&format=claude&sessionId=${sessionId}&updated=${VERSION}`);
  const request = http.request(url, { method: "POST", headers: { ...headers(key, "application/x-ndjson"), "transfer-encoding": "chunked" } });
  request.on("error", () => undefined);
  const chunk = Buffer.alloc(1024 * 1024, "x");
  let timer: NodeJS.Timeout | undefined;
  const reached = (async () => {
    for (let sent = 0; sent < bytes; sent += chunk.length) {
      if (!request.write(chunk)) await new Promise((resolve) => request.once("drain", resolve));
    }

    timer = setInterval(() => request.write(Buffer.alloc(1740, "x")), 100);
  })();
  return { reached, stop: () => { clearInterval(timer); request.destroy(); } };
}

/** Finding 7: large uploads fill their places. A third gets an honest 503, and nobody's small document is turned away. */
async function bystanders(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const held = await Promise.all(["a", "b"].map((name) => keys(`hold-${name}`, ["ingest"])));
  const uploads = held.map((key, n) => drip(h, key, `cccccc0${n}-1111-2222-3333-444444444444`, 16 * 1024 * 1024));
  await Promise.all(uploads.map((upload) => upload.reached));
  await pause(500);
  const key = await keys("bystander", ["ingest"]);
  const third = await postSession(h, key, "cccccc09-1111-2222-3333-444444444444", "x".repeat(LARGE_BYTES + 1));
  const answers = await Promise.all(Array.from({ length: 40 }, (_, n) => postDocument(h, key, `bystander-${n}`, `small document ${n}`)));
  const session = await postSession(h, key, "cccccc08-1111-2222-3333-444444444444", line("user", "a small session"));
  for (const upload of uploads) upload.stop();
  const served = answers.filter((answer) => answer.status === 202).length;
  h.check("connection: a third large upload gets an honest 503", third.status === 503 && code(third) === "ingest_busy", third.text.slice(0, 200));
  h.check("connection: and small documents are served all the while", served === answers.length, `${served} of ${answers.length}`);
  h.check("connection: and small sessions", session.status === 202, session.text.slice(0, 200));
}

/** A collection must be opened on purpose, and empty. */
async function exclusiveCollections(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const key = await keys("exclusive", ["ingest"]);
  const body = (collection: string): RequestInit => ({ method: "POST", headers: headers(key), body: JSON.stringify({ collection, source: "notes", sourceId: "x", body: "b", updated: VERSION }) });
  const filled = await send(`${h.apiUrl}/v1/documents`, body("filled"));
  h.check("connection: a collection with files in it will not open to ingest", filled.status === 409 && code(filled) === "collection_not_empty", filled.text);
  h.check("connection: and nothing was written to it", !existsSync(path.join(h.dir, "corpus", "filled", documentPath("x"))));
  const closed = await send(`${h.apiUrl}/v1/documents`, body("closed"));
  h.check("connection: a collection nobody enabled cannot be written", closed.status === 409 && code(closed) === "ingest_not_enabled", closed.text);
}

/** Links are refused on the way in, a delete removes the link and not what it points at, and neither reaches through one. Only some hosts can make them. */
async function links(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const key = await keys("links", ["ingest"]);
  const outside = path.join(h.dir, "outside.md");
  const dirOutside = path.join(h.dir, "outside-dir");
  mkdirSync(dirOutside, { recursive: true });
  writeFileSync(outside, "outside the root\n");
  writeFileSync(path.join(dirOutside, "inner.md"), "outside the root\n");
  const fileLink = fileOf(h, "linked-file");
  try {
    symlinkSync(outside, fileLink);
    symlinkSync(dirOutside, path.join(folder(h), "linked-dir"), "junction");
  } catch {
    console.log("NOT RUN connection: links (this host cannot make them)");
    return;
  }

  const write = await postDocument(h, key, "linked-file", "through the link");
  h.check("connection: a write does not go through a file link", write.status !== 202 && readFileSync(outside, "utf8") === "outside the root\n", write.text);
  const remove = await deleteById(h, key, "linked-file");
  h.check("connection: a delete removes a file link and not what it points at", remove.status === 202 && !existsSync(fileLink) && readFileSync(outside, "utf8") === "outside the root\n", remove.text);
  const through = await send(`${h.apiUrl}/v1/documents?collection=${SAFE}&path=linked-dir/inner.md`, { method: "DELETE", headers: headers(key) });
  h.check("connection: a delete cannot reach through a directory link", through.status !== 202 && readFileSync(path.join(dirOutside, "inner.md"), "utf8") === "outside the root\n", through.text);
}

/** Runs against a server started with INGEST_SERVER. */
export async function ingestConnection(h: Harness, keys: ApiKeyMaker): Promise<void> {
  await exclusiveCollections(h, keys);
  await restartThenDelete(h, keys);
  await restartThenNewerUpload(h, keys);
  await bodyArrivingAfterDeath(h, keys);
  await timeoutClosesTheSocket(h, keys);
  await repeatedDeleteClearsALateWrite(h, keys);
  await lostAnswers(h, keys);
  await killsUnderLoad(h, keys);
  await aDeleteInAnotherCase(h, keys);
  await bystanders(h, keys);
  await links(h, keys);
}
