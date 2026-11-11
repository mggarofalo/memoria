/**
 * Batch limits, scope by key type on REST and MCP, MCP denials in the audit table, and
 * the final scan for secrets. The proxy records what the sidecar was asked, and can
 * stand in for a sidecar that breaks its own limits.
 */
import { ADMIN_KEYS, COLLECTIONS, auditByKey, codeOf, idOf, issueKey, parse, statuses, type IssuedKey } from "./auth-helpers.ts";
import { INITIALIZE, mcpFrames, toolCall, type Reply } from "./checks.ts";
import { BATCH_BUDGET, BULK_DOCUMENTS, BULK_DOCUMENT_BYTES, TOTAL_DOCUMENTS, type ApiResponse, type Harness } from "./harness.ts";

const BATCH = "/v1/docs:multiGet";
const SIDECAR_BATCH = "/docs:multiGet";
const MAX_BYTES_CEILING = 1_048_576;
const GLOB_DOCUMENT_LIMIT = 1000;
const SIDE_LOOKUP = "/status";
/** Past the API's cutoff for this budget: six bytes per character, plus 16 MiB. */
const WIRE_LIMIT = BATCH_BUDGET * 6 + 16 * 1024 * 1024;
const OVERSIZE_BYTES = WIRE_LIMIT * 3;

interface Batch {
  docs: Array<{ file: string; body: string | null; collection: string; maxLines?: number | null }>;
  skipped: Array<{ file: string; reason: string }>;
  errors: string[];
  truncated: boolean;
}

function batch(h: Harness, key: string, body: object): Promise<ApiResponse> {
  return h.request({ method: "POST", path: BATCH, key, body });
}

function batchCalls(h: Harness, from: number): string[] {
  return h.proxied(from).filter((call) => call.url === SIDECAR_BATCH).map((call) => call.body ?? "");
}

async function maxBytesCeiling(h: Harness, wide: string): Promise<void> {
  const from = h.proxiedCount();
  const over = await batch(h, wide, { pattern: "notes/*.md", maxBytes: MAX_BYTES_CEILING + 1 });
  const zero = await batch(h, wide, { pattern: "notes/*.md", maxBytes: 0 });
  const refused = over.status === 400 && zero.status === 400 && over.body.includes("maxBytes");
  const atCeiling = await batch(h, wide, { pattern: "notes/*.md", maxBytes: MAX_BYTES_CEILING });
  const calls = batchCalls(h, from);
  h.check("maxBytes: one over the ceiling and zero are 400, and the ceiling itself works", refused && atCeiling.status === 200 && atCeiling.body.includes("zebrafish protocol"), statuses([over, zero, atCeiling]));
  h.check("maxBytes: refused requests never reach the sidecar, and the marker does", calls.length === 1 && calls[0]?.includes(`"maxBytes":${MAX_BYTES_CEILING}`), calls);
}

async function perDocumentAndBudget(h: Harness, wide: string): Promise<void> {
  const capped = parse<Batch>(await batch(h, wide, { pattern: "bulk/*.md", maxBytes: 1000 }));
  h.check("maxBytes: a document over the cap is listed under skipped, with no body", capped.docs.length === 0 && capped.skipped.length === BULK_DOCUMENTS, { docs: capped.docs.length, skipped: capped.skipped });

  const from = h.proxiedCount();
  const budgeted = parse<Batch>(await batch(h, wide, { pattern: "bulk/*.md" }));
  const fits = Math.floor(BATCH_BUDGET / BULK_DOCUMENT_BYTES);
  const bytes = budgeted.docs.reduce((total, doc) => total + (doc.body?.length ?? 0), 0);
  h.check(`total budget: exactly ${fits} of ${BULK_DOCUMENTS} documents fit`, budgeted.docs.length === fits && budgeted.skipped.length === BULK_DOCUMENTS - fits && bytes <= BATCH_BUDGET, { docs: budgeted.docs.length, skipped: budgeted.skipped, bytes });
  h.check("total budget: the API sends its budget to the sidecar", batchCalls(h, from)[0]?.includes(`"maxTotalBytes":${BATCH_BUDGET}`), batchCalls(h, from));
}

async function truncation(h: Harness, wide: string): Promise<void> {
  const many = parse<Batch>(await batch(h, wide, { pattern: "many/*.md" }));
  h.check(`truncation: a glob past ${GLOB_DOCUMENT_LIMIT} documents says so, and returns exactly that many`, many.truncated && many.docs.length + many.skipped.length === GLOB_DOCUMENT_LIMIT, { truncated: many.truncated, docs: many.docs.length, skipped: many.skipped.length });
  const few = parse<Batch>(await batch(h, wide, { pattern: "notes/*.md" }));
  h.check("truncation: a small glob is not marked truncated", !few.truncated && few.docs.length === 1, few.truncated);
  const lines = parse<Batch>(await batch(h, wide, { pattern: "notes/*.md", maxLines: 1 }));
  h.check("truncation: maxLines cuts the body and says so", lines.docs[0]?.maxLines === 1 && !lines.docs[0].body?.includes("zebrafish"), lines.docs[0]);
}

function sidecarDoc(collection: string, name: string, body: string): object {
  const file = `${collection}/${name}`;
  return { docid: "abc123", file, collection, path: name, title: name, context: null, modifiedAt: "2026-01-01T00:00:00Z", bodyLength: body.length, fromLine: null, maxLines: null, body, metadata: null };
}

/** The sidecar is not a security boundary, so these answers come from a stand-in that ignores its limits. */
async function sidecarBreaksItsLimits(h: Harness, wide: string, reader: string): Promise<void> {
  const big = "y".repeat(60_000);
  await h.planProxy({ kind: "canned", body: JSON.stringify({ docs: ["a", "b", "c"].map((n) => sidecarDoc("bulk", `${n}.md`, big)), skipped: [], errors: [], truncated: false }) });
  const budget = parse<Batch>(await batch(h, wide, { pattern: "bulk/*.md" }));
  h.check("API budget: a sidecar that sends too much is held to it", budget.docs.length === 1 && budget.skipped.filter((entry) => entry.reason === "Response size budget reached").length === 2, { docs: budget.docs.length, skipped: budget.skipped });

  const canned = { docs: [sidecarDoc("notes", "seen.md", "in scope"), sidecarDoc("secret", "leak.md", "the leaked ledger")], skipped: [{ file: "secret/leak2.md", reason: "too big" }, { file: "notes/kept.md", reason: "too big" }], errors: ["secret/leak3.md matched"], truncated: false };
  await h.planProxy({ kind: "canned", body: JSON.stringify(canned) });
  const scoped = await batch(h, reader, { pattern: "*/*.md" });
  const seen = parse<Batch>(scoped);
  h.check("API scope: results are checked again on the way out, per list", seen.docs.length === 1 && seen.docs[0]?.file === "notes/seen.md" && seen.skipped.length === 1 && seen.skipped[0]?.file === "notes/kept.md" && seen.errors.length === 0 && !scoped.body.includes("leak"), scoped.body);
}

async function oversizedResponse(h: Harness, reader: string): Promise<void> {
  await h.planProxy({ kind: "oversize", bytes: OVERSIZE_BYTES });
  const before = h.proxiedCount();
  const answer = await batch(h, reader, { pattern: "notes/*.md" });
  h.check("oversized sidecar response: 502, and the padding does not reach the caller", answer.status === 502 && codeOf(answer) === "sidecar_response_too_large" && !answer.body.includes("xxxx"), answer);
  const outcome = await oversizeOutcome(h, before);
  const cutOff = outcome !== undefined && outcome.closedEarly && outcome.sent < outcome.offered && outcome.sent >= WIRE_LIMIT;
  h.check("oversized sidecar response: the API hangs up at its limit, not at the end", cutOff, outcome);
}

async function oversizeOutcome(h: Harness, from: number): Promise<{ offered: number; sent: number; closedEarly: boolean } | undefined> {
  for (let attempt = 0; attempt < 50; attempt++) {
    const found = h.proxied(from).find((call) => call.oversize);
    if (found?.oversize) return found.oversize;
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  return undefined;
}

export async function multiGetLimits(h: Harness, keys: { wide: string; reader: string }): Promise<void> {
  await maxBytesCeiling(h, keys.wide);
  await perDocumentAndBudget(h, keys.wide);
  await truncation(h, keys.wide);
  await sidecarBreaksItsLimits(h, keys.wide, keys.reader);
  await oversizedResponse(h, keys.reader);
}

interface Status {
  totalDocuments: number;
  needsEmbedding: number | null;
  collections: Array<{ name: string }>;
}

export interface ScopeKeys {
  scoped: IssuedKey;
  wide: IssuedKey;
  adminOnly: IssuedKey;
  ingestOnly: IssuedKey;
  /** Read and admin bound to one collection, as an older version issued: it is not given admin. */
  readAdmin: IssuedKey;
  /** Read and admin on every collection, the way such a key is issued now. */
  readAdminWide: IssuedKey;
}

function planted(token: string): IssuedKey {
  return { token, id: idOf(token) };
}

export async function issueScopeKeys(h: Harness, admin: string): Promise<ScopeKeys> {
  return {
    scoped: await issueKey(h, admin, { label: "scope-read", scopes: ["read"], collections: ["notes"] }),
    wide: await issueKey(h, admin, { label: "scope-wide", scopes: ["read"], allCollections: true }),
    adminOnly: await issueKey(h, admin, { label: "scope-admin", scopes: ["admin"] }),
    ingestOnly: await issueKey(h, admin, { label: "scope-ingest", scopes: ["ingest"], collections: ["notes"] }),
    readAdmin: planted(h.plantKey("scope-read-admin", ["read", "admin"], ["notes"])),
    readAdminWide: await issueKey(h, admin, { label: "scope-read-admin-wide", scopes: ["read", "admin"], allCollections: true }),
  };
}

function names(items: Array<{ name: string }> | undefined): string {
  return (items ?? []).map((item) => item.name).sort().join(",");
}

async function restReadable(h: Harness, label: string, token: string): Promise<void> {
  const status = await h.request({ path: "/v1/status", key: token });
  const seen = parse<Status>(status);
  h.check(`${label}: /v1/status covers only its collection, with no global backlog`, status.status === 200 && seen.totalDocuments === 1 && seen.needsEmbedding === null && names(seen.collections) === "notes", seen);
  const listed = await h.request({ path: COLLECTIONS, key: token });
  h.check(`${label}: /v1/collections shows only its collection`, listed.status === 200 && names(parse<{ collections: Array<{ name: string }> }>(listed).collections) === "notes", listed.body);
}

async function restWide(h: Harness, token: string): Promise<void> {
  const seen = parse<Status>(await h.request({ path: "/v1/status", key: token }));
  h.check("an all-collections key: /v1/status covers every collection and the backlog", seen.totalDocuments === TOTAL_DOCUMENTS && typeof seen.needsEmbedding === "number" && names(seen.collections) === "bulk,many,notes,secret", seen);
  const listed = await h.request({ path: COLLECTIONS, key: token });
  h.check("an all-collections key: /v1/collections shows them all", names(parse<{ collections: Array<{ name: string }> }>(listed).collections) === "bulk,many,notes,secret", listed.body);
}

/** The scoped key's call after them is the marker: the proxy has to show that one, and only that one. */
async function restRefused(h: Harness, keys: ScopeKeys): Promise<void> {
  const from = h.proxiedCount();
  for (const [label, key] of [["an admin-only key", keys.adminOnly], ["an ingest-only key", keys.ingestOnly]] as const) {
    const status = await h.request({ path: "/v1/status", key: key.token });
    const listed = await h.request({ path: COLLECTIONS, key: key.token });
    h.check(`${label}: /v1/status and /v1/collections are 403`, status.status === 403 && listed.status === 403 && codeOf(status) === "insufficient_scope", statuses([status, listed]));
  }
  const anonymous = await h.request({ path: "/v1/status" });
  h.check("no key: /v1/status is 401", anonymous.status === 401, anonymous.status);
  await h.request({ path: "/v1/status", key: keys.scoped.token });
  const calls = h.proxied(from).map((call) => call.url);
  h.check("refused keys never reach the sidecar, and the marker does", calls.length === 1 && calls[0] === SIDE_LOOKUP, calls);
}

export async function scopeMatrix(h: Harness, keys: ScopeKeys): Promise<void> {
  await restReadable(h, "a collection-scoped key", keys.scoped.token);
  await restReadable(h, "an old read and admin key scoped to one collection", keys.readAdmin.token);
  await restWide(h, keys.wide.token);
  await restRefused(h, keys);
  const bound = await h.request({ path: ADMIN_KEYS, key: keys.readAdmin.token });
  h.check("an old read and admin key scoped to one collection cannot manage keys", bound.status === 403 && codeOf(bound) === "collection_scoped_key", bound.status);
  const management = await h.request({ path: ADMIN_KEYS, key: keys.readAdminWide.token });
  h.check("a read and admin key on every collection also manages keys", management.status === 200, management.status);
}

function mcpSession(h: Harness, key: string, calls: Array<[string, object]>): Map<number, Reply> {
  const frames = [INITIALIZE, { jsonrpc: "2.0", method: "notifications/initialized" }, ...calls.map(([tool, args], index) => toolCall(index + 2, tool, args))];
  const session = h.memoria(["mcp"], { key, stdin: frames.map((frame) => JSON.stringify(frame)).join("\n") + "\n" });
  return mcpFrames(session.out).replies;
}

const SCOPED_CALLS: Array<[string, object]> = [
  ["list_collections", {}],
  ["status", {}],
  ["search", { query: "zebrafish", rerank: false }],
  ["search", { query: "zebrafish", rerank: false, collections: ["secret"] }],
  ["get_document", { reference: "secret/hidden.md" }],
  ["get_documents", { pattern: "secret/*.md" }],
  ["get_documents", { pattern: "notes/*.md" }],
];

function mcpScoped(h: Harness, label: string, token: string): void {
  const replies = mcpSession(h, token, SCOPED_CALLS);
  const text = (id: number): string => JSON.stringify(replies.get(id) ?? null);
  const isError = (id: number): boolean => replies.get(id)?.result?.isError === true;
  h.check(`${label}: MCP list_collections and status cover only its collection`, text(2).includes('"notes"') && !text(2).includes('"secret"') && text(3).includes('"totalDocuments":1') && !text(3).includes('"secret"'), [text(2), text(3)]);
  h.check(`${label}: MCP search finds only its collection`, text(4).includes("notes/hello.md") && !text(4).includes("hidden"), text(4));
  h.check(`${label}: MCP search of another collection is refused`, isError(5) && text(5).includes("outside this caller's scope"), text(5));
  h.check(`${label}: MCP get_document outside its scope is refused, with nothing read`, isError(6) && !text(6).includes("ledger"), text(6));
  h.check(`${label}: MCP get_documents outside its scope is empty, and inside it is not`, !text(7).includes("hidden") && text(8).includes("zebrafish protocol"), [text(7), text(8)]);
}

function mcpWide(h: Harness, token: string): void {
  const replies = mcpSession(h, token, [["list_collections", {}], ["status", {}], ["get_document", { reference: "secret/hidden.md" }]]);
  const text = (id: number): string => JSON.stringify(replies.get(id) ?? null);
  h.check("an all-collections key: MCP sees every collection, and reads across them", text(2).includes('"secret"') && text(3).includes(`"totalDocuments":${TOTAL_DOCUMENTS}`) && text(4).includes("ledger"), [text(2), text(3), text(4)]);
}

function mcpRefused(h: Harness, label: string, token: string): void {
  const replies = mcpSession(h, token, []);
  h.check(`${label}: MCP is refused with a real 403`, replies.get(1)?.error?.data?.status === 403, [...replies.values()]);
}

async function mcpAudit(h: Harness, keys: ScopeKeys): Promise<void> {
  // Rows are written as each response ends, in no set order, so wait for every call in the session.
  const rows = await auditByKey(h, keys.scoped.id, (found) => found.filter((row) => row.operation.startsWith("mcp.")).length >= SCOPED_CALLS.length);
  const seen = rows.map((row) => [row.operation, row.status, row.collections]);
  const denied = rows.find((row) => row.operation === "mcp.search" && row.status === 403);
  h.check("MCP denial audit: 403 and the collection asked for, though HTTP said 200", denied?.attempted_collections === '["secret"]', rows.map((row) => [row.operation, row.status, row.attempted_collections]));
  const notFound = rows.find((row) => row.operation === "mcp.get_document");
  h.check("MCP denial audit: an out-of-scope get_document is recorded as 404", notFound?.status === 404 && notFound.result_count === 0, seen);
  const allowed = rows.find((row) => row.operation === "mcp.list_collections");
  h.check("MCP audit marker: an allowed tool call is recorded as 200", allowed?.status === 200 && allowed.collections === '["notes"]', seen);

  const adminRows = await auditByKey(h, keys.adminOnly.id, (found) => found.some((row) => row.status === 403 && !row.operation.startsWith("get")));
  h.check("MCP denial audit: an admin-only key's refused session is recorded as 403", adminRows.some((row) => row.status === 403), adminRows.map((row) => [row.operation, row.status]));
}

export async function mcpScope(h: Harness, keys: ScopeKeys): Promise<void> {
  mcpScoped(h, "a collection-scoped key", keys.scoped.token);
  mcpScoped(h, "a read and admin key scoped to one collection", keys.readAdmin.token);
  mcpWide(h, keys.wide.token);
  mcpRefused(h, "an admin-only key", keys.adminOnly.token);
  mcpRefused(h, "an ingest-only key", keys.ingestOnly.token);
  await mcpAudit(h, keys);
}

/** Things a caller may send that must not be kept: a wrong secret, a query string, and a header. */
export async function sendCanaries(h: Harness, reader: string): Promise<void> {
  const canary = h.remember(`mem_${idOf(reader)}_${"c0ffee".repeat(10)}abcd`);
  const wrongSecret = await h.request({ path: COLLECTIONS, key: canary, from: "127.0.0.10" });
  const query = h.remember(`mem_${"e".repeat(16)}_${"d00d".repeat(16)}`);
  const inUrl = await h.request({ path: `${COLLECTIONS}?token=${query}`, key: reader, headers: { "X-Api-Key": query } });
  h.check("canaries delivered: a wrong secret, and a key in a query string and a header", wrongSecret.status === 401 && inUrl.status === 200, statuses([wrongSecret, inUrl]));
}

const STATUS_CLASSES_SCANNED = [400, 401, 403, 404, 409, 429, 502];

/** Every search says what it scanned and proves that scan could see: each has its own marker. */
export function secretsAbsent(h: Harness, reader: string): void {
  const sensitive = h.sensitiveValues();
  // What surrounds a match says where it came from. `check` scrubs the key itself.
  const absentFrom = (text: string): string[] => sensitive.filter((value) => text.includes(value)).map((value) => text.slice(Math.max(0, text.indexOf(value) - 160), text.indexOf(value) + value.length + 40));

  const api = h.log("api");
  // Without appsettings.json, framework logging writes every query string, and this would test something nobody ships.
  h.check("the API runs on its shipped appsettings.json, so its log lines are JSON", api.trimStart().startsWith("{"), api.slice(0, 200));
  h.check("no secret in the API log, which does name a key's public id", absentFrom(api).length === 0 && api.includes(idOf(reader)), absentFrom(api));
  const sidecar = h.log("sidecar");
  h.check("no secret in the sidecar log, which does show requests", absentFrom(sidecar).length === 0 && sidecar.includes('"route":"/docs'), absentFrom(sidecar));

  const rows = h.control<Record<string, unknown>>("SELECT * FROM audit_log");
  const audit = JSON.stringify(rows);
  h.check(`no secret in the audit table's ${rows.length} rows`, rows.length > 0 && absentFrom(audit).length === 0 && audit.includes(idOf(reader)), absentFrom(audit));

  const newest = h.control<{ audit_id: string }>("SELECT audit_id FROM audit_log ORDER BY id DESC LIMIT 1")[0]?.audit_id ?? "newest row missing";
  const { wal } = h.controlFiles();
  h.check("no secret in the write-ahead log, which does hold the newest audit row", wal.includes(newest) && absentFrom(wal).length === 0, { walBytes: wal.length, found: absentFrom(wal) });
  h.checkpointControl();
  const { main } = h.controlFiles();
  h.check("no secret in the control database file, which after a checkpoint holds that row too", main.includes(newest) && absentFrom(main).length === 0, { mainBytes: main.length, found: absentFrom(main) });

  const scanned = new Set(h.responses.map((response) => response.status));
  const bodies = h.responses.map((response) => response.body + JSON.stringify(response.headers)).join("\n");
  const missing = STATUS_CLASSES_SCANNED.filter((status) => !scanned.has(status));
  h.check(`no secret in ${h.responses.length} error and other response bodies, across every error status`, missing.length === 0 && absentFrom(bodies).length === 0, { missing, found: absentFrom(bodies) });
}
