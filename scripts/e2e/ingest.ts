/**
 * The most important proof in the project: a secret goes in through the real ingest routes, and
 * cannot come out through any route, file or log. This runs on its own server, so its settings
 * cannot affect the other checks. Every token is built at run time from random bytes, so no
 * scanner mistakes this file for a leak, and none is ever printed.
 */
import { randomInt } from "node:crypto";
import { readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import http from "node:http";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { pause, type ApiKeyMaker, type Harness, type HarnessOptions } from "./harness.ts";

export const SAFE = "safe";
const OPEN = "open";
const TYPO = "typo";
const CLOSED = "closed";
const FILLED = "filled";
/** Targets of `migrate push`, each opened and empty, so a count of what is on disk means something. */
const MIGRATED = ["migrated", "migrated-resume", "migrated-refusing", "migrated-killed", "migrated-rules", "migrated-takeover", "migrated-trust", "migrated-spelling", "migrated-decomposed", "migrated-credential", "migrated-older", "migrated-shapes", "migrated-rerender", "migrated-rerender-chunks"];
const VERSION = "2026-02-01T00:00:00Z";
const LATER = "2026-02-02T00:00:00Z";
const ALPHABET = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
export const SESSION_DAY = "2026-01-15";

/** A debounce far longer than the run: nothing reaches the index unless something forces it. */
export const INGEST_SERVER: HarnessOptions = {
  extraCollections: [SAFE, OPEN, TYPO, CLOSED, ...MIGRATED],
  filledCollections: [FILLED],
  sidecarEnv: { MEMORIA_QMD_REINDEX_DEBOUNCE_MS: "600000", MEMORIA_QMD_REINDEX_MAX_WAIT_MS: "600000", MEMORIA_QMD_LOG_LEVEL: "debug", NODE_OPTIONS: `--import=${pathToFileURL(path.join(import.meta.dirname, "stall-writes.mjs")).href}` },
  // The most anyone could ask of the log: nothing below may depend on a settings file keeping it quiet.
  apiEnv: {
    Logging__LogLevel__Default: "Debug",
    "Logging__LogLevel__Microsoft.AspNetCore": "Debug",
    "Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics": "Debug",
    Logging__Console__LogLevel__Default: "Debug",
    [`Memoria__Ingest__Collections__${OPEN}__Redaction`]: "Disabled",
    [`Memoria__Ingest__Collections__${TYPO}__Redaction`]: "disabled",
    Memoria__Ingest__MinReindexInterval: "00:00:00",
    ...Object.fromEntries([SAFE, OPEN, TYPO, FILLED, ...MIGRATED].map((name) => [`Memoria__Ingest__Collections__${name}__Enabled`, "true"])),
    // Short, so the check that the API gives up on a stalled write does not take two minutes.
    Memoria__Sidecar__Timeout: "00:00:15",
  },
};

function randomText(length: number): string {
  return Array.from({ length }, () => ALPHABET[randomInt(ALPHABET.length)]).join("");
}

function randomLetters(length: number): string {
  return Array.from({ length }, () => ALPHABET[randomInt(52)]).join("");
}

function randomUpper(length: number): string {
  return randomText(length * 3).replace(/[a-z]/g, "").padEnd(length, "A").slice(0, length).toUpperCase();
}

/** One of each common shape, spelled so that the source holds none of them. */
function tokens(): Record<string, string> {
  return {
    github: "gh" + "p_" + randomText(36),
    aws: "AK" + "IA" + randomUpper(16),
    gitlab: "gl" + "pat-" + randomText(20),
    // A real token can hold no digit, and its own alphabet can split it into short pieces. These are here so that
    // every run covers what a random token only reaches now and then.
    gitlabLetters: "gl" + "pat-" + randomLetters(20),
    gitlabPieces: "gl" + "pat-" + `${randomLetters(7)}-${randomLetters(6)}_${randomLetters(5)}`.toLowerCase(),
    anthropic: "sk-" + "ant-" + "api03-" + randomText(80),
    openai: "s" + "k-" + randomText(48),
    slack: "xo" + "xb-" + "1234567890" + "12-" + "1234567890" + "12-" + randomText(24),
    npm: "np" + "m_" + randomText(36),
  };
}

interface Context {
  h: Harness;
  admin: string;
  writer: string;
  reader: string;
}

interface Answer {
  status: number;
  text: string;
  headers: Headers;
}

function headersFor(key: string, contentType = "application/json"): Record<string, string> {
  return { authorization: `Bearer ${key}`, "content-type": contentType };
}

async function call(url: string, init: RequestInit): Promise<Answer> {
  const response = await fetch(url, init);
  return { status: response.status, text: await response.text(), headers: response.headers };
}

function parsed(answer: Answer): Record<string, never> {
  return JSON.parse(answer.text) as Record<string, never>;
}

function postDocument(c: Context, key: string, body: Record<string, unknown>, query = ""): Promise<Answer> {
  return call(`${c.h.apiUrl}/v1/documents${query}`, { method: "POST", headers: headersFor(key), body: JSON.stringify({ updated: VERSION, source: "notes", ...body }) });
}

function postSession(c: Context, query: Record<string, string>, body: string): Promise<Answer> {
  const parameters = new URLSearchParams({ collection: SAFE, format: "claude", updated: VERSION, ...query });
  return call(`${c.h.apiUrl}/v1/ingest/session?${parameters}`, { method: "POST", headers: headersFor(c.writer, "application/x-ndjson"), body });
}

function deleteDocument(c: Context, query: string, key = c.writer): Promise<Answer> {
  return call(`${c.h.apiUrl}/v1/documents?${query}`, { method: "DELETE", headers: headersFor(key) });
}

function search(c: Context, query: string, collection: string): Promise<Answer> {
  return call(`${c.h.apiUrl}/v1/search?query=${encodeURIComponent(query)}&rerank=false&limit=20&collection=${collection}`, { headers: headersFor(c.reader) });
}

function hitCount(answer: Answer): number {
  return (JSON.parse(answer.text) as { results: unknown[] }).results.length;
}

function line(role: string, text: string, minute = 0): string {
  const time = `${SESSION_DAY}T10:${String(minute).padStart(2, "0")}:00.000Z`;
  return JSON.stringify({ type: role, timestamp: time, cwd: "/srv/demo", message: { role, content: text } }) + "\n";
}

/** Text this long fills a part by itself, so a transcript of it has as many parts as messages. */
function transcript(messages: number, word: string): string {
  const filler = Array(1500).fill("x".repeat(60)).join(" ");
  return Array.from({ length: messages }, (_, i) => line(i % 2 === 0 ? "user" : "assistant", `${word}${i} ${filler}`, i)).join("");
}

function holdsAny(text: string, secrets: readonly string[]): boolean {
  return secrets.some((secret) => text.includes(secret));
}

async function waitForJob(c: Pick<Context, "h">, id: string): Promise<void> {
  for (let attempt = 0; attempt < 240; attempt++) {
    const answer = await c.h.sidecar("GET", `/reindex/jobs/${id}`);
    const state = (JSON.parse(answer.text) as { job: { state: string } }).job.state;
    if (state === "succeeded" || state === "failed") return;
    await pause(250);
  }
  throw new Error("an index job never finished");
}

export async function forceIndex(c: Pick<Context, "h">): Promise<void> {
  const answer = await c.h.sidecar("POST", "/reindex", { embed: false });
  await waitForJob(c, (JSON.parse(answer.text) as { job: { id: string } }).job.id);
}

async function createKey(h: Harness, admin: string, label: string, scopes: string[]): Promise<string> {
  const answer = await call(`${h.apiUrl}/v1/admin/keys`, { method: "POST", headers: headersFor(admin), body: JSON.stringify({ label, scopes, collections: [SAFE, OPEN, TYPO, CLOSED, FILLED] }) });
  return h.remember((JSON.parse(answer.text) as { key: string }).key);
}

function mcpCall(c: Context, name: string, args: Record<string, unknown>): Promise<string> {
  return mcpTool(c.h, c.reader, name, args);
}

export interface FilterArguments {
  project?: string[];
  branch?: string[];
  since?: string;
  until?: string;
  tags?: string[];
}

/** The text of a `search` tool call, as a client of the MCP server gets it. */
export function mcpSearch(h: Harness, key: string, args: { query: string; collections: string[]; rerank: boolean } & FilterArguments): Promise<string> {
  return mcpTool(h, key, "search", { ...args });
}

async function mcpTool(h: Harness, key: string, name: string, args: Record<string, unknown>): Promise<string> {
  const headers = { ...headersFor(key), accept: "application/json, text/event-stream" };
  const url = `${h.apiUrl}/mcp`;
  const opened = await fetch(url, { method: "POST", headers, body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "e2e", version: "1" } } }) });
  await opened.text();
  const session = opened.headers.get("mcp-session-id");
  const next = session ? { ...headers, "mcp-session-id": session } : headers;
  await (await fetch(url, { method: "POST", headers: next, body: JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) })).text();
  return (await call(url, { method: "POST", headers: next, body: JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/call", params: { name, arguments: args } }) })).text;
}

/** Every file of the run, raw: collections, index, control database and write-ahead logs, logs included. */
function filesHolding(root: string, secrets: readonly string[], skip: (file: string) => boolean): string[] {
  const found: string[] = [];
  for (const entry of readdirSync(root, { recursive: true, encoding: "utf8" })) {
    const file = path.join(root, entry);
    if (statSync(file).isFile() && !skip(file) && holdsAny(readFileSync(file, "latin1"), secrets)) found.push(entry);
  }
  return found;
}

async function writeSecrets(c: Context, secrets: Record<string, string>): Promise<string> {
  const all = Object.values(secrets);
  const document = await postDocument(c, c.writer, { collection: SAFE, sourceId: "doc-1", title: `about ${secrets["github"]}`, body: `zebraword the keys are ${all.join(" and ")} here` });
  const body = parsed(document) as unknown as { redaction: string; redactions: Record<string, number>; refusals: Array<{ field: string }>; path: string };
  c.h.check("ingest: a document with credentials is accepted, redacted and counted", document.status === 202 && body.redaction === "enabled" && Object.keys(body.redactions).length >= 5 && body.refusals.some((refusal) => refusal.field === "title"), document.text);
  c.h.check("ingest: the answer repeats no credential", !holdsAny(document.text, all));

  const session = await postSession(c, { sessionId: "aaaaaaaa-1111-2222-3333-444444444444" }, line("user", `zebrasession my key ${all.join(" ")}`) + line("assistant", "noted", 1));
  c.h.check("ingest: a transcript with credentials is complete and repeats none", session.status === 202 && !holdsAny(session.text, all), session.text);
  await forceIndex(c);
  return body.path;
}

function returnsOnlyRedacted(c: Context, name: string, text: string, all: readonly string[]): void {
  c.h.check(`ingest: ${name} returns only redacted text`, text.includes("zebraword") && !holdsAny(text, all), text);
}

/** Nothing but redacted text comes back through any route that can read. */
async function readEveryRoute(c: Context, secrets: Record<string, string>, stored: string): Promise<void> {
  const all = Object.values(secrets);
  await searchEveryWay(c, secrets);
  const hit = (JSON.parse((await search(c, "zebraword", SAFE)).text) as { results: Array<{ docid: string }> }).results[0];
  const docid = hit?.docid ?? "";
  returnsOnlyRedacted(c, "GET /v1/docs by id", (await call(`${c.h.apiUrl}/v1/docs/${encodeURIComponent(docid)}`, { headers: headersFor(c.reader) })).text, all);
  returnsOnlyRedacted(c, "GET /v1/docs by path", (await call(`${c.h.apiUrl}/v1/docs/${SAFE}/${stored}`, { headers: headersFor(c.reader) })).text, all);
  returnsOnlyRedacted(c, "multi-get", (await call(`${c.h.apiUrl}/v1/docs:multiGet`, { method: "POST", headers: headersFor(c.reader), body: JSON.stringify({ pattern: "*.md", collections: [SAFE] }) })).text, all);
  returnsOnlyRedacted(c, "MCP search", await mcpCall(c, "search", { query: "zebraword", collections: [SAFE], rerank: false }), all);
  returnsOnlyRedacted(c, "MCP get_document", await mcpCall(c, "get_document", { reference: docid }), all);
}

async function searchEveryWay(c: Context, secrets: Record<string, string>): Promise<void> {
  const all = Object.values(secrets);
  for (const word of ["zebraword", "zebrasession"]) {
    const found = await search(c, word, SAFE);
    c.h.check(`ingest: search finds ${word}, redacted`, found.status === 200 && found.text.includes(word) && found.text.includes("[REDACTED:") && !holdsAny(found.text, all), found.text);
  }
  for (const [name, secret] of Object.entries(secrets)) {
    const found = await search(c, secret, SAFE);
    c.h.check(`ingest: searching for the ${name} token itself finds no secret`, found.status === 200 && !holdsAny(found.text, all));
  }
}

/** Everything a URL can carry, sent at debug level everywhere. None of it may reach a log. */
async function nothingInTheLogs(c: Context, secrets: Record<string, string>, stored: string): Promise<void> {
  const token = secrets["github"] ?? "";
  const marker = "markerdocumentpath";
  const reader = headersFor(c.reader);
  await call(`${c.h.apiUrl}/v1/search?query=${token}&rerank=false&collection=${SAFE}`, { headers: reader });
  await call(`${c.h.apiUrl}/v1/docs/${SAFE}/${marker}.md`, { headers: reader });
  await call(`${c.h.apiUrl}/v1/docs/${marker}`, { headers: reader });
  await call(`${c.h.apiUrl}/v1/no-such-route/${marker}?query=${marker}`, { headers: reader });
  await deleteDocument(c, `collection=${SAFE}&path=${marker}.md`);
  await mcpCall(c, "search", { query: token, collections: [SAFE], rerank: false });
  await mcpCall(c, "get_document", { reference: `${SAFE}/${marker}.md` });
  await pause(200);

  for (const service of ["api", "sidecar"] as const) {
    const log = c.h.log(service);
    c.h.check(`logging: at debug level the ${service} log still runs`, log.length > 0);
    c.h.check(`logging: a token in a query string or tool argument is not in the ${service} log`, !holdsAny(log, Object.values(secrets)));
    c.h.check(`logging: a document path in a URL is not in the ${service} log`, !log.includes(marker));
    c.h.check(`logging: a stored document's path is not in the ${service} log`, !log.includes(stored));
  }
}

function nothingOnDisk(c: Context, secrets: Record<string, string>): void {
  // The proxy's record holds what this run searched for, which is each token by value. What was written is checked next.
  const found = filesHolding(c.h.dir, Object.values(secrets), (file) => /\.(exe|dll)$/.test(file) || ["memoria", "proxy-records.jsonl"].includes(path.basename(file)));
  c.h.check("ingest: no file of the run holds a credential, in raw bytes, logs and databases included", found.length === 0, found);

  if (!c.h.hasProxy) {
    console.log("NOT RUN ingest: every write on the wire to the sidecar is free of credentials (needs the recording proxy; the process layout runs it)");
    return;
  }
  const writes = c.h.proxied().filter((call) => call.method === "POST" && call.url === "/documents");
  c.h.check("ingest: every write on the wire to the sidecar is free of credentials", writes.length >= 2 && !writes.some((call) => holdsAny(call.body ?? "", Object.values(secrets))), writes.length);
}

/** The same key and secret, one collection along, where an operator chose otherwise. */
async function offSwitch(c: Context, secrets: Record<string, string>): Promise<void> {
  const secret = secrets["github"] ?? "";
  const open = await postDocument(c, c.writer, { collection: OPEN, sourceId: "doc-1", body: `tigerword the key is ${secret} here` });
  const openBody = JSON.parse(open.text) as { redaction: string; redactions: object };
  c.h.check("ingest: an operator's off switch stores the text and says so", open.status === 202 && openBody.redaction === "disabled" && Object.keys(openBody.redactions).length === 0, open.text);

  const typo = JSON.parse((await postDocument(c, c.writer, { collection: TYPO, sourceId: "doc-1", body: `lionword ${secret}` })).text) as { redaction: string };
  c.h.check("ingest: lower-case disabled leaves redaction on", typo.redaction === "enabled");

  const sneaky = await call(`${c.h.apiUrl}/v1/documents?redaction=Disabled`, { method: "POST", headers: { ...headersFor(c.writer), "x-memoria-redaction": "Disabled" }, body: JSON.stringify({ collection: SAFE, source: "notes", sourceId: "sneaky", updated: VERSION, body: `pumaword ${secret}`, redaction: "Disabled" }) });
  c.h.check("ingest: a query, header and body field cannot turn it off", (JSON.parse(sneaky.text) as { redaction: string }).redaction === "enabled");

  await forceIndex(c);
  c.h.check("ingest: search in the off collection returns the text as written", (await search(c, "tigerword", OPEN)).text.includes(secret));
  const neighbour = await search(c, "pumaword", SAFE);
  c.h.check("ingest: search next door still has no secret", neighbour.text.includes("pumaword") && !neighbour.text.includes(secret), neighbour.text);

  // An admin key acts on every collection, so one that also reads does too.
  const both = await call(`${c.h.apiUrl}/v1/admin/keys`, { method: "POST", headers: headersFor(c.admin), body: JSON.stringify({ label: "both", scopes: ["read", "admin"], allCollections: true }) });
  const adminStatus = await call(`${c.h.apiUrl}/v1/status`, { headers: headersFor(c.h.remember((JSON.parse(both.text) as { key: string }).key)) });
  const readerStatus = await call(`${c.h.apiUrl}/v1/status`, { headers: headersFor(c.reader) });
  c.h.check("ingest: /v1/status lists the off collection for an admin only", JSON.stringify(parsed(adminStatus)["unredactedCollections"]) === `["${OPEN}"]` && parsed(readerStatus)["unredactedCollections"] == null, adminStatus.text);
  await pause(200);
  c.h.check("ingest: startup warned about the off collection and the typo", c.h.log("api").includes(`REDACTION IS OFF for collection ${OPEN}`) && c.h.log("api").includes(`${TYPO}:Redaction is not exactly Disabled`));
}

async function deletes(c: Context): Promise<void> {
  c.h.check("ingest: searchable before the delete", hitCount(await search(c, "zebraword", SAFE)) > 0);
  const deleted = await deleteDocument(c, `collection=${SAFE}&source=notes&sourceId=doc-1`);
  const body = JSON.parse(deleted.text) as { deleted: boolean; reindexJobId: string };
  c.h.check("ingest: a delete starts its index update at once", deleted.status === 202 && body.deleted && Boolean(body.reindexJobId), deleted.text);
  await waitForJob(c, body.reindexJobId);
  c.h.check("ingest: unsearchable once that update ends, with a 10-minute debounce standing", hitCount(await search(c, "zebraword", SAFE)) === 0);
  c.h.check("ingest: an older copy cannot come back", (await postDocument(c, c.writer, { collection: SAFE, sourceId: "doc-1", body: "replayed" })).status === 409);

  const nothing = await deleteDocument(c, `collection=${SAFE}&source=notes&sourceId=never`);
  c.h.check("ingest: deleting nothing is safe and costs no update", nothing.status === 202 && (JSON.parse(nothing.text) as { reindexState: string }).reindexState === "not_needed", nothing.text);
  c.h.check("ingest: a dot path is refused", (await deleteDocument(c, `collection=${SAFE}&path=${encodeURIComponent(".git/config")}`)).status === 400);
}

function scopedKeyFor(c: Context, label: string): Promise<string> {
  return createKeyFor(c, label, ["ingest"], [SAFE]);
}

async function createKeyFor(c: Context, label: string, scopes: string[], collections: string[]): Promise<string> {
  const answer = await call(`${c.h.apiUrl}/v1/admin/keys`, { method: "POST", headers: headersFor(c.admin), body: JSON.stringify({ label, scopes, collections }) });
  return c.h.remember((JSON.parse(answer.text) as { key: string }).key);
}

async function scope(c: Context): Promise<void> {
  const safeOnly = await scopedKeyFor(c, "safe only");
  c.h.check("ingest: a key for one collection cannot write to another", (await postDocument(c, safeOnly, { collection: OPEN, sourceId: "x", body: "b" })).status === 403);
  c.h.check("ingest: a document that does not exist out of scope still gets 403", (await deleteDocument(c, `collection=${OPEN}&path=never-existed.md`, safeOnly)).status === 403);
  c.h.check("ingest: a read key cannot write", (await postDocument(c, c.reader, { collection: SAFE, sourceId: "x", body: "b" })).status === 403);
}

function onDisk(c: Context, part: string): string[] {
  return readdirSync(path.join(c.h.dir, "corpus", SAFE)).filter((file) => file.includes(part));
}

async function sessions(c: Context): Promise<void> {
  const id = "cccccccc-1111-2222-3333-444444444444";
  const five = await postSession(c, { sessionId: id }, transcript(5, "partword"));
  c.h.check("ingest: one session becomes five parts", five.status === 202 && onDisk(c, "cccccccc").length === 5, five.text);
  await forceIndex(c);

  const cut = await postSession(c, { sessionId: id, updated: LATER }, transcript(1, "partword"));
  c.h.check("ingest: a cut copy is refused and deletes nothing", cut.status === 409 && onDisk(c, "cccccccc").length === 5, cut.text);
  const older = await postSession(c, { sessionId: id, updated: "2020-01-01T00:00:00Z" }, transcript(5, "oldword"));
  c.h.check("ingest: an older upload is refused", older.status === 409, older.text);

  const shrink = await postSession(c, { sessionId: id, updated: LATER, allowShrink: "true" }, transcript(1, "partword"));
  const shrunk = JSON.parse(shrink.text) as { superseded: { deleted: string[] }; reindexJobId: string };
  c.h.check("ingest: a real shrink removes the old parts through the real multi-get and delete", shrink.status === 202 && shrunk.superseded.deleted.length === 4 && onDisk(c, "cccccccc").length === 1, shrink.text);
  await waitForJob(c, shrunk.reindexJobId);
  c.h.check("ingest: the old parts are unsearchable at once", hitCount(await search(c, "partword3", SAFE)) === 0);

  const giant = await postSession(c, { sessionId: "dddddddd-1111-2222-3333-444444444444" }, line("user", ("y".repeat(79) + "\n").repeat(70000)));
  const sizes = onDisk(c, "dddddddd").map((file) => statSync(path.join(c.h.dir, "corpus", SAFE, file)).size);
  c.h.check("ingest: a 5.6 MB message is split so each file fits the 2 MiB cap", giant.status === 202 && sizes.length >= 3 && sizes.every((size) => size <= 2097152), sizes);

  const noMessages = await postSession(c, { sessionId: "eeeeeeee-1111-2222-3333-444444444444" }, "garbage that is no transcript\n");
  c.h.check("ingest: a transcript with no messages is never complete", noMessages.status === 207 && (JSON.parse(noMessages.text) as { complete: boolean }).complete === false, noMessages.text);
}

export function codexMeta(id: string): string {
  return JSON.stringify({ timestamp: `${SESSION_DAY}T10:00:00.000Z`, type: "session_meta", payload: { id, cwd: "/srv/demo" } }) + "\n";
}

export function codexMessage(text: string): string {
  return JSON.stringify({ timestamp: `${SESSION_DAY}T10:00:01.000Z`, type: "response_item", payload: { type: "message", role: "user", content: [{ type: "input_text", text }] } }) + "\n";
}

async function damagedCopies(c: Context): Promise<void> {
  const id = "bbbbbbbb-1111-2222-3333-444444444444";
  const a = line("user", "firstmessage"), b = line("assistant", "middlemessage", 1), d = line("user", "thirdmessage", 2), e = line("assistant", "fourthmessage", 3);
  await postSession(c, { sessionId: id }, a + b + d);
  const damaged = await postSession(c, { sessionId: id, updated: LATER }, a + "{corrupt\n" + d + e);
  c.h.check("ingest: a copy with a corrupt middle line is refused", damaged.status === 409 && (JSON.parse(damaged.text) as { code: string }).code === "lost_content", damaged.text);
  c.h.check("ingest: and the line it lost is still stored", onDisk(c, "bbbbbbbb").some((file) => readFileSync(path.join(c.h.dir, "corpus", SAFE, file), "utf8").includes("middlemessage")));
}

async function deleteWinsAndContentStaysGone(c: Context): Promise<void> {
  const body = "contentword replayed under a later date";
  await postDocument(c, c.writer, { collection: SAFE, sourceId: "hash-1", body });
  await deleteDocument(c, `collection=${SAFE}&source=notes&sourceId=hash-1`);
  const later = new Date(Date.now() + 30 * 60_000).toISOString();
  c.h.check("ingest: the same content under a later date does not come back after a delete", (await postDocument(c, c.writer, { collection: SAFE, sourceId: "hash-1", body, updated: later })).status === 409);
  c.h.check("ingest: new content dated after the delete does", (await postDocument(c, c.writer, { collection: SAFE, sourceId: "hash-1", body: "really new", updated: later })).status === 202);
}

/**
 * A delete racing an upload, over and over, against the real sidecar. Cancelling a write does not stop it there, so a file could land after the
 * delete and stay searchable. Only a real sidecar can show this. What counts as a race is a delete that found the upload first: the two
 * orders where the delete really came first, or the upload had already finished, prove nothing here and are left alone.
 */
async function deleteRacesUpload(c: Context): Promise<void> {
  const body = (i: number) => `raceword${i} ${"w".repeat(1_500_000)}`;
  let lost = 0;
  let preempted = 0;
  for (let round = 0; round < 40; round++) {
    const sourceId = `race-${round}`;
    const uploading = postDocument(c, c.writer, { collection: OPEN, sourceId, body: body(round) });
    await pause(round % 12);
    const deleted = await deleteDocument(c, `collection=${OPEN}&source=notes&sourceId=${sourceId}`);
    const uploaded = await uploading;
    const tookOver = uploaded.status === 409;
    preempted += tookOver ? 1 : 0;
    const deleteWonOrCameLast = tookOver || (JSON.parse(deleted.text) as { deleted: boolean }).deleted;
    const onDiskNow = readdirSync(path.join(c.h.dir, "corpus", OPEN)).some((file) => file.startsWith("notes-") && readFileSync(path.join(c.h.dir, "corpus", OPEN, file), "utf8").includes(`raceword${round} `));
    if (deleteWonOrCameLast && onDiskNow) lost++;
  }

  console.log(`     (${preempted} of 40 rounds had the delete take over a write)`);
  await forceIndex(c);
  c.h.check("ingest: after a delete wins a race with an upload, the file never comes back", lost === 0, `${lost} files survived`);
}

/** The server cuts off a body slower than the floor, on real Kestrel. A slow but steady one is not affected. */
function slowUploadIsCutOff(c: Context): Promise<void> {
  const started = Date.now();
  return new Promise((resolve) => {
    const url = new URL(`${c.h.apiUrl}/v1/ingest/session?collection=${SAFE}&format=claude&sessionId=55555551-1111-1111-1111-111111111111&updated=${VERSION}`);
    const request = http.request(url, { method: "POST", headers: { ...headersFor(c.writer, "application/x-ndjson"), "transfer-encoding": "chunked" } });
    const timer = setInterval(() => request.write(Buffer.alloc(512, "x")), 500);
    const finish = (status: number): void => {
      clearInterval(timer);
      request.destroy();
      c.h.check("ingest: a body arriving a byte at a time is cut off within seconds", status === 408 || status === 0, status);
      c.h.check("ingest: and well before any flat deadline", Date.now() - started < 60_000, Date.now() - started);
      resolve();
    };
    request.on("response", (response) => finish(response.statusCode ?? 0));
    request.on("error", () => finish(0));
  });
}

async function ownership(c: Context): Promise<void> {
  const victim = "11112222-aaaa-bbbb-cccc-111111111111";
  await postSession(c, { format: "codex", sessionId: victim }, codexMeta(victim) + codexMessage("victimword original"));
  const attack = await postSession(c, { format: "codex", sessionId: "attacker-session", updated: "2020-01-01T00:00:00Z" }, codexMeta(victim) + codexMessage("attackerword"));
  await forceIndex(c);
  c.h.check("ingest: an upload naming another session in its meta is refused", attack.status === 400, attack.text);
  c.h.check("ingest: and the victim is intact", (await search(c, "victimword", SAFE)).text.includes("victimword") && hitCount(await search(c, "attackerword", SAFE)) === 0);

  await postSession(c, { sessionId: "aabbccdd-1111-1111-1111-111111111111" }, transcript(4, "firstword"));
  const clash = await postSession(c, { sessionId: "aabbccdd-2222-2222-2222-222222222222" }, line("user", "clashword"));
  const owned = onDisk(c, "aabbccdd").filter((file) => !file.includes("aabbccdd-"));
  c.h.check("ingest: a session sharing a short id deletes nothing of the other", clash.status === 202 && (JSON.parse(clash.text) as { superseded: { deleted: string[] } }).superseded.deleted.length === 0 && owned.length === 4, clash.text);

  const exporter = "99887766-1111-1111-1111-111111111111";
  const stray = path.join(c.h.dir, "corpus", SAFE, `${SESSION_DAY}_${exporter.slice(0, 8)}.md`);
  writeFileSync(stray, `---\nsession_id: "${exporter}"\n---\n\n## User\n\nexporterword\n`);
  const replaced = await postSession(c, { sessionId: exporter }, line("user", "onepart"));
  c.h.check("ingest: everything in the collection is ours, so a file at a path is overwritten", replaced.status === 202 && !readFileSync(stray, "utf8").includes("exporterword"), replaced.text);
}

async function inputThatStopsHere(c: Context, secrets: Record<string, string>): Promise<void> {
  const secret = secrets["npm"] ?? "";
  const collection = await postDocument(c, c.writer, { collection: secret, sourceId: "x", body: "b" });
  const deletion = await deleteDocument(c, `collection=${SAFE}&path=${secret}`);
  c.h.check("ingest: a credential sent as a collection or path gets 400 and is not repeated", collection.status === 400 && deletion.status === 400 && !holdsAny(collection.text + deletion.text, [secret]));
}

/** Makes keys on the server these checks run against, for any collection they use. */
export function keyMaker(h: Harness, admin: string): ApiKeyMaker {
  return (label, scopes) => createKey(h, admin, label, scopes);
}

/** Runs against a server started with INGEST_SERVER. */
export async function ingest(h: Harness, admin: string): Promise<void> {
  const secrets = tokens();
  for (const secret of Object.values(secrets)) h.remember(secret);
  const c: Context = { h, admin, writer: await createKey(h, admin, "writer", ["ingest", "read"]), reader: await createKey(h, admin, "reader", ["read"]) };

  const stored = await writeSecrets(c, secrets);
  await readEveryRoute(c, secrets, stored);
  await inputThatStopsHere(c, secrets);
  await nothingInTheLogs(c, secrets, stored);
  nothingOnDisk(c, secrets);
  await offSwitch(c, secrets);
  await deletes(c);
  await scope(c);
  await sessions(c);
  await damagedCopies(c);
  await deleteWinsAndContentStaysGone(c);
  await ownership(c);
  // Keys of their own, as a key has a budget of 100 requests in this run.
  await deleteRacesUpload({ ...c, writer: await createKey(h, admin, "racer", ["ingest", "read"]) });
  await slowUploadIsCutOff({ ...c, writer: await createKey(h, admin, "slow", ["ingest"]) });
}
