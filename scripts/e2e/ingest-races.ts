/**
 * What only a real sidecar and a real process can show: uploads and deletes racing, clients hanging up, an API killed mid-write.
 * A fake sidecar answers in order, so it hid every one of these. Tokens are built at run time and never printed.
 */
import { createHash, randomInt } from "node:crypto";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import http from "node:http";
import path from "node:path";
import { pause, type ApiKeyMaker, type Harness } from "./harness.ts";
import { forceIndex } from "./ingest.ts";

export const SAFE = "safe";
const VERSION = "2026-02-01T00:00:00Z";
const DAY = "2026-01-15";
const ALPHABET = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

export interface Answer {
  status: number;
  text: string;
}

function randomText(length: number): string {
  return Array.from({ length }, () => ALPHABET[randomInt(ALPHABET.length)]).join("");
}

export function headers(key: string, type = "application/json"): Record<string, string> {
  return { authorization: `Bearer ${key}`, "content-type": type };
}

export async function send(url: string, init: RequestInit): Promise<Answer> {
  const response = await fetch(url, init);
  return { status: response.status, text: await response.text() };
}

export function code(answer: Answer): string {
  return (JSON.parse(answer.text) as { code?: string }).code ?? "";
}

export function folder(h: Harness): string {
  return path.join(h.dir, "corpus", SAFE);
}

export function textOf(file: string): string | null {
  return existsSync(file) ? readFileSync(file, "utf8") : null;
}

export function documentPath(sourceId: string): string {
  const digest = createHash("sha256").update(`5:notes${sourceId}`).digest("hex").slice(0, 32);
  return `notes-${digest}.md`;
}

export function postDocument(h: Harness, key: string, sourceId: string, body: string, extra: Record<string, unknown> = {}, signal?: AbortSignal): Promise<Answer> {
  return send(`${h.apiUrl}/v1/documents`, { method: "POST", headers: headers(key), body: JSON.stringify({ collection: SAFE, source: "notes", sourceId, body, updated: VERSION, ...extra }), ...(signal ? { signal } : {}) });
}

export function deleteById(h: Harness, key: string, sourceId: string, signal?: AbortSignal): Promise<Answer> {
  return send(`${h.apiUrl}/v1/documents?collection=${SAFE}&source=notes&sourceId=${sourceId}`, { method: "DELETE", headers: headers(key), ...(signal ? { signal } : {}) });
}

export function line(role: string, text: string, minute = 0): string {
  return JSON.stringify({ type: role, timestamp: `${DAY}T10:${String(minute).padStart(2, "0")}:00.000Z`, cwd: "/srv/demo", message: { role, content: text } }) + "\n";
}

export function postSession(h: Harness, key: string, sessionId: string, body: string): Promise<Answer> {
  const query = new URLSearchParams({ collection: SAFE, format: "claude", updated: VERSION, sessionId });
  return send(`${h.apiUrl}/v1/ingest/session?${query}`, { method: "POST", headers: headers(key, "application/x-ndjson"), body });
}

/** A delete is finished only when it says so: repeat until it does, as a client told 503 would. */
async function deleteUntilDone(h: Harness, key: string, sourceId: string): Promise<Answer> {
  for (let attempt = 0; ; attempt++) {
    const answer = await deleteById(h, key, sourceId);
    if (answer.status !== 503 || attempt > 120) return answer;
    await pause(500);
  }
}

/** A delete with two uploads around it. Whatever the order, an upload answering success must find its file there. */
async function deleteBetweenUploads(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const key = await keys("between", ["ingest", "read"]);
  let lied = 0;
  for (let round = 0; round < 30; round++) {
    const sourceId = `between-${round}`;
    const first = postDocument(h, key, sourceId, `older ${"w".repeat(1_000_000)}`);
    await pause(round % 8);
    const deleting = deleteById(h, key, sourceId);
    await pause(round % 5);
    const newer = await postDocument(h, key, sourceId, `newer ${"w".repeat(1_000)}`, { updated: new Date(Date.now() + 60_000).toISOString() });
    await Promise.all([first, deleting]);
    const held = existsSync(path.join(folder(h), documentPath(sourceId)));
    if (newer.status === 202 && !held) lied++;
  }

  h.check("races: an upload answering success never finds a delete took its file", lied === 0, `${lied} of 30`);
}

/** The clients hang up at the worst moments. After a delete answers done, nothing may be there or arrive later. */
async function clientsHangUp(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const key = await keys("hangup", ["ingest", "read"]);
  let survived = 0;
  for (let round = 0; round < 20; round++) {
    const sourceId = `hangup-${round}`;
    const uploader = new AbortController();
    const deleter = new AbortController();
    const uploading = postDocument(h, key, sourceId, `hangword ${"w".repeat(1_500_000)}`, {}, uploader.signal).catch(() => null);
    await pause(round % 10);
    const deleting = deleteById(h, key, sourceId, deleter.signal).catch(() => null);
    await pause(round % 4);
    if (round % 2 === 0) uploader.abort();
    if (round % 3 === 0) deleter.abort();
    await Promise.all([uploading, deleting]);
    const done = await deleteUntilDone(h, key, sourceId);
    if (done.status !== 202) survived++;
    await pause(150);
    if (existsSync(path.join(folder(h), documentPath(sourceId)))) survived++;
  }

  h.check("races: after clients hang up, a delete that says done leaves no file, then or later", survived === 0, `${survived} of 20`);
}

/** The API dies with a delete open. Restarted, it must finish the job with no client asking. */
async function killedInTheWindow(h: Harness, keys: ApiKeyMaker): Promise<void> {
  let key = await keys("killed", ["ingest", "read"]);
  let lied = 0;
  for (let round = 0; round < 3; round++) {
    const sourceId = `killed-${round}`;
    const uploading = postDocument(h, key, sourceId, `killword ${"w".repeat(1_500_000)}`).catch(() => null);
    await pause(5 + round * 5);
    const deleting = deleteById(h, key, sourceId).catch(() => null);
    await pause(5);
    await h.restartApi();
    await Promise.all([uploading, deleting]);
    key = await keys(`killed-${round}`, ["ingest", "read"]);
    const done = await deleteUntilDone(h, key, sourceId);
    await pause(200);
    if (done.status !== 202 || existsSync(path.join(folder(h), documentPath(sourceId)))) lied++;
  }

  h.check("races: a delete survives the API being killed", lied === 0, `${lied} of 3`);
}

/** Text no rule matches, and what a stolen database could confirm about it. */
function storedDigestsConfirmNothing(h: Harness, bodies: readonly string[]): void {
  const database = ["control.sqlite", "control.sqlite-wal"].map((name) => path.join(h.dir, name)).filter((file) => existsSync(file)).map((file) => readFileSync(file, "latin1")).join("");
  const guessable = bodies.filter((body) => database.includes(createHash("sha256").update(body).digest("hex")));
  h.check("races: the database cannot confirm a guessed text with a plain hash", guessable.length === 0, guessable.length);
}

async function slowLinksHoldNoSlot(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const slowKeys = await Promise.all([1, 2, 3].map((n) => keys(`slow-${n}`, ["ingest"])));
  const requests = slowKeys.flatMap((key, k) => [0, 1].map((n) => {
    const url = new URL(`${h.apiUrl}/v1/ingest/session?collection=${SAFE}&format=claude&sessionId=7777777${k}-${n}111-1111-1111-111111111111&updated=${VERSION}`);
    const request = http.request(url, { method: "POST", headers: { ...headers(key, "application/x-ndjson"), "transfer-encoding": "chunked" } });
    request.on("error", () => undefined);
    return { request, timer: setInterval(() => request.write(Buffer.alloc(2048, "x")), 100) };
  }));
  await pause(1500);
  const fresh = await keys("fresh", ["ingest"]);
  const answer = await postSession(h, fresh, "88888888-1111-1111-1111-111111111111", line("user", "a fresh key"));
  for (const { request, timer } of requests) {
    clearInterval(timer);
    request.destroy();
  }

  h.check("races: six uploads on slow but legal links leave a fresh key served", answer.status === 202, answer.text);
}

/** Runs against a server started with INGEST_SERVER. */
export async function ingestRaces(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const writer = await keys("digests", ["ingest"]);
  const guess = `hunter2-${randomText(6)}`;
  const onlyGuess = `the text ${guess}`;
  await postDocument(h, writer, "digest-doc", onlyGuess);
  await postSession(h, writer, "dddddddd-aaaa-2222-3333-444444444444", line("user", guess));
  storedDigestsConfirmNothing(h, [guess, onlyGuess]);
  await slowLinksHoldNoSlot(h, keys);
  await deleteBetweenUploads(h, keys);
  await clientsHangUp(h, keys);
  await killedInTheWindow(h, keys);
}
