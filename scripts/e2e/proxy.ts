/**
 * Sits between the API and the sidecar. It records the exact URL of every call, so a
 * check can say what the sidecar was really asked, and on request it answers the next
 * batch call as a misbehaving sidecar would. It is its own process because the driver
 * blocks while the CLI runs, and a proxy inside the driver would stop answering.
 *
 * Usage: node scripts/e2e/proxy.ts <port> <sidecar port> <record file>
 */
import { appendFileSync, writeFileSync } from "node:fs";
import http from "node:http";
import { once } from "node:events";

/** How the next `POST /docs:multiGet` is answered, instead of asking the sidecar. */
export type BatchPlan = { kind: "canned"; body: string } | { kind: "oversize"; bytes: number };

/** The sidecar accepts this many more documents, then fails until told otherwise. `null` ends it. */
export type DocumentPlan = { kind: "failDocuments"; after: number | null };

/**
 * What happens to writes. `drop` lets one land and loses its answer. `holdTail` sends all of a body but its end, and sends the end when the caller goes away, with
 * the close right behind it, as the kernel delivers what a dead process had sent. `every` picks every nth write, and `count` the next few.
 */
export type WritePlan = { kind: "writes"; mode: "drop" | "holdTail" | "off"; every?: number; count?: number; ms?: number };

export type Plan = BatchPlan | DocumentPlan | WritePlan;

export interface ProxyRecord {
  method?: string;
  url?: string;
  body?: string;
  oversize?: { offered: number; sent: number; closedEarly: boolean };
}

const CONTROL_PATH = "/__e2e/plan";
const BATCH_PATH = "/docs:multiGet";
const DOCUMENTS_PATH = "/documents";
const MAX_RECORDED_BODY = 4096;
const CHUNK = Buffer.alloc(1024 * 1024, "x");

const [listenPort, sidecarPort, recordFile] = process.argv.slice(2) as [string, string, string];
let plan: BatchPlan | null = null;
let documentsLeft: number | null = null;
let writes: WritePlan = { kind: "writes", mode: "off" };
let writesSeen = 0;

function record(entry: ProxyRecord): void {
  appendFileSync(recordFile, JSON.stringify(entry) + "\n");
}

async function readBody(request: http.IncomingMessage): Promise<Buffer> {
  const parts: Buffer[] = [];
  for await (const part of request) parts.push(part as Buffer);
  return Buffer.concat(parts);
}

function open(request: http.IncomingMessage, onAnswer: (answer: http.IncomingMessage) => void): http.ClientRequest {
  const upstream = http.request({ host: "127.0.0.1", port: sidecarPort, method: request.method, path: request.url, headers: request.headers }, onAnswer);
  return upstream;
}

/** A closed connection is passed on, or the sidecar could not tell its caller had gone, as it can on a direct connection. */
function forward(request: http.IncomingMessage, response: http.ServerResponse, body: Buffer): void {
  const upstream = open(request, (answer) => {
    response.writeHead(answer.statusCode ?? 502, answer.headers);
    answer.pipe(response);
  });
  response.once("close", () => {
    if (!response.writableFinished) upstream.destroy();
  });
  upstream.on("error", () => response.destroy());
  upstream.end(body);
}

function dropAnswer(request: http.IncomingMessage, response: http.ServerResponse, body: Buffer): void {
  const upstream = open(request, (answer) => {
    answer.resume();
    answer.once("end", () => response.destroy());
  });
  upstream.on("error", () => response.destroy());
  upstream.end(body);
}

function holdTail(request: http.IncomingMessage, response: http.ServerResponse, body: Buffer, ms: number): void {
  const upstream = open(request, (answer) => {
    response.writeHead(answer.statusCode ?? 502, answer.headers);
    answer.pipe(response);
  });
  upstream.on("error", () => response.destroy());
  const cut = Math.max(0, body.length - 1024);
  upstream.write(body.subarray(0, cut));
  const finish = setTimeout(() => upstream.end(body.subarray(cut)), ms);
  response.once("close", () => {
    if (response.writableFinished) return;
    clearTimeout(finish);
    upstream.write(body.subarray(cut), () => upstream.destroy());
  });
}

/** Which treatment this write gets, as the plan counts them. */
function writeTreatment(request: http.IncomingMessage): WritePlan["mode"] {
  if (writes.mode === "off" || request.method !== "POST" || request.url !== DOCUMENTS_PATH) return "off";
  writesSeen++;
  if (writes.every !== undefined) return writesSeen % writes.every === 0 ? writes.mode : "off";
  if ((writes.count ?? 0) <= 0) return "off";
  writes.count = (writes.count ?? 0) - 1;
  return writes.mode;
}

/** Valid JSON as far as it goes, and far past any limit. Stops when the API hangs up. */
async function sendOversized(response: http.ServerResponse, offered: number): Promise<void> {
  let closedEarly = false;
  response.once("close", () => (closedEarly = !response.writableFinished));
  response.writeHead(200, { "content-type": "application/json" });
  response.write('{"docs":[],"skipped":[],"errors":[],"pad":"');
  let sent = 0;
  while (sent < offered && !response.destroyed) {
    sent += CHUNK.length;
    if (!response.write(CHUNK)) await Promise.race([once(response, "drain"), once(response, "close")]);
  }
  response.end('"}');
  if (!response.destroyed) await once(response, "close");
  record({ oversize: { offered, sent, closedEarly } });
}

function setPlan(next: Plan): void {
  if (next.kind === "failDocuments") documentsLeft = next.after;
  else if (next.kind === "writes") {
    writes = next;
    writesSeen = 0;
  } else plan = next;
}

/** True when this write should be refused. */
function refuseDocument(request: http.IncomingMessage): boolean {
  if (documentsLeft === null || request.method !== "POST" || request.url !== DOCUMENTS_PATH) return false;
  if (documentsLeft > 0) {
    documentsLeft--;
    return false;
  }
  return true;
}

async function answerFromPlan(response: http.ServerResponse, current: BatchPlan): Promise<void> {
  if (current.kind === "oversize") return sendOversized(response, current.bytes);
  response.writeHead(200, { "content-type": "application/json" });
  response.end(current.body);
}

async function handle(request: http.IncomingMessage, response: http.ServerResponse): Promise<void> {
  const body = await readBody(request);
  if (request.url === CONTROL_PATH) {
    setPlan(JSON.parse(body.toString("utf8")) as Plan);
    response.end("ok");
    return;
  }
  // Recorded before anything else happens, so a check that has its answer can read it.
  record({ method: request.method ?? "", url: request.url ?? "", body: body.toString("utf8").slice(0, MAX_RECORDED_BODY) });
  if (refuseDocument(request)) {
    response.writeHead(502, { "content-type": "application/json" });
    response.end(JSON.stringify({ error: "unavailable", message: "The proxy is standing in for a sidecar that went away." }));
    return;
  }
  const current = request.url === BATCH_PATH ? plan : null;
  if (current) {
    plan = null;
    return answerFromPlan(response, current);
  }
  const treatment = writeTreatment(request);
  if (treatment === "drop") return dropAnswer(request, response, body);
  if (treatment === "holdTail") return holdTail(request, response, body, writes.ms ?? 0);
  forward(request, response, body);
}

writeFileSync(recordFile, "");
http.createServer((request, response) => void handle(request, response).catch(() => response.destroy())).listen(Number(listenPort), "127.0.0.1");
