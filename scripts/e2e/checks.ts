/**
 * The checks, in the order they run. Later sections rely on keys made by earlier
 * ones, and the 429 section must stay last, since it uses up the failed-auth limit.
 * New sections go before it.
 */
import { BAD_KEY } from "./auth-helpers.ts";
import { FAILED_AUTH_LIMIT, KEY_RATE_LIMIT, PROFILE, type Harness } from "./harness.ts";

const FULL_KEY = /^mem_[0-9a-f]{16}_[0-9a-f]{64}$/;

export interface Keys {
  admin: string;
  reader: string;
}

export function bootstrap(h: Harness): string {
  const first = h.bootstrap("e2e-admin");
  const admin = h.remember(first.out.trim());
  h.check("--bootstrap-admin-key prints one key on stdout", first.code === 0 && FULL_KEY.test(admin), first.out + first.err);
  h.check("bootstrap's stderr does not repeat the key", !first.err.includes(admin) && first.err.includes("printed once"), first.err);
  const second = h.bootstrap("e2e-admin-2");
  h.check("a second bootstrap is refused", second.code === 1 && second.out.trim() === "", second.out + second.err);
  return admin;
}

function createKey(h: Harness, admin: string, args: string[]): string {
  const created = h.memoria(["keys", "create", ...args], { key: admin });
  const key = h.remember(created.out.trim());
  h.check(`keys create ${args[1]}: stdout is exactly one key, stderr never has it`, created.code === 0 && FULL_KEY.test(key) && !created.err.includes(key), created.out + created.err);
  return key;
}

interface ListedKey {
  id: string;
  label: string;
  allCollections: boolean;
  collections: string[];
}

function listKeys(h: Harness, admin: string): Map<string, ListedKey> {
  const listed = h.memoria(["keys", "list", "--format", "json"], { key: admin });
  h.check("keys list is real JSON with no key material", listed.code === 0 && !listed.out.includes("mem_"), listed.out + listed.err);
  return new Map((JSON.parse(listed.out) as { keys: ListedKey[] }).keys.map((key) => [key.label, key]));
}

export function keyManagement(h: Harness, admin: string): Keys {
  const reader = createKey(h, admin, ["--label", "reader", "--scope", "read", "--collection", "notes"]);
  const refused = h.memoria(["keys", "create", "--label", "noscope", "--scope", "read"], { key: admin });
  h.check("no collections: the real server refuses, exit 2", refused.code === 2 && refused.err.toLowerCase().includes("collections"), refused.out + refused.err);
  const wide = createKey(h, admin, ["--label", "wide", "--scope", "read", "--all-collections"]);

  const keys = listKeys(h, admin);
  h.check("keys list shows the real shapes", keys.get("wide")?.allCollections === true && keys.get("reader")?.collections[0] === "notes", [...keys.values()]);
  const wideId = keys.get("wide")?.id ?? "";
  rotateAndRevoke(h, admin, wide, wideId);
  refuseBadKeyOperations(h, admin, wideId, keys.get("e2e-admin")?.id ?? "");
  return { admin, reader };
}

function rotateAndRevoke(h: Harness, admin: string, wide: string, wideId: string): void {
  const rotated = h.remember(h.memoria(["keys", "rotate", wideId], { key: admin }).out.trim());
  h.check("keys rotate returns a new key", FULL_KEY.test(rotated) && rotated !== wide);
  h.check("the rotated-away key gets a real 401, exit 3", h.memoria(["collections"], { key: wide }).code === 3);
  const works = h.memoria(["collections"], { key: rotated });
  h.check("the rotated key works", works.code === 0 && works.out.includes("secret"), works.out + works.err);

  const revoked = h.memoria(["keys", "revoke", wideId], { key: admin });
  h.check("keys revoke", revoked.code === 0 && revoked.err.includes("Revoked"), revoked.out + revoked.err);
  h.check("the revoked key gets a real 401, exit 3", h.memoria(["collections"], { key: rotated }).code === 3);
  h.check("revoking twice succeeds", h.memoria(["keys", "revoke", wideId], { key: admin }).code === 0);
}

function refuseBadKeyOperations(h: Harness, admin: string, revokedId: string, adminId: string): void {
  const conflict = h.memoria(["keys", "rotate", revokedId], { key: admin });
  h.check("rotating a revoked key: real 409, exit 1", conflict.code === 1 && conflict.err.includes("409"), conflict.out + conflict.err);
  h.check("revoking an unknown id: real 404, exit 4", h.memoria(["keys", "revoke", "0".repeat(16)], { key: admin }).code === 4);
  const own = h.memoria(["keys", "revoke", adminId], { key: admin });
  h.check("revoking the key in use needs --force", own.code === 2 && own.err.includes("--force"), own.out + own.err);
}

export function scopeErrors(h: Harness, { admin, reader }: Keys): void {
  const listed = h.memoria(["keys", "list"], { key: reader });
  h.check("a read key listing keys: real 403, exit 3, names the way forward", listed.code === 3 && listed.err.includes("--bootstrap-admin-key"), listed.out + listed.err);
  for (const args of [["query", "zebrafish", "--no-rerank"], ["get", "notes/hello.md"], ["collections"], ["status"]]) {
    const result = h.memoria(args, { key: admin });
    h.check(`an admin key on ${args[0]}: real 403, exit 3, explains why`, result.code === 3 && result.err.includes("by design") && result.err.includes("memoria keys create"), result.out + result.err);
  }
  h.check("a document outside the key's scope reads as not found: exit 4", h.memoria(["get", "secret/hidden.md"], { key: reader }).code === 4);
  h.check("searching outside the key's scope: real 403, exit 3", h.memoria(["query", "zebrafish", "--collection", "secret", "--no-rerank"], { key: reader }).code === 3);
}

export function scopedReads(h: Harness, { reader }: Keys): void {
  const key = reader;
  const collections = h.memoria(["collections"], { key });
  h.check("collections shows only the key's scope", collections.code === 0 && collections.out.includes("notes") && !collections.out.includes("secret"), collections.out + collections.err);
  h.check("status", h.memoria(["status"], { key }).out.includes("documents:"));

  const got = h.memoria(["get", "notes/hello.md"], { key });
  h.check("get by path", got.code === 0 && got.out.includes("zebrafish protocol"), got.out + got.err);
  h.check("get strips a stored escape", !got.out.includes("\x1b") && !got.out.includes("\x07") && got.out.includes("stored escape"), got.out);
  const raw = h.memoria(["get", "notes/hello.md", "--format", "json"], { key });
  // A failed get prints no JSON. It has already been reported above, and the rest of the run still has things to say.
  const docid = raw.code === 0 ? (JSON.parse(raw.out) as { docid: string }).docid : "no-such-docid";
  h.check("get by qmd:// URI", h.memoria(["get", "qmd://notes/hello.md"], { key }).out.includes("zebrafish protocol"));
  h.check("get by docid", h.memoria(["get", docid], { key }).out.includes("zebrafish protocol"));
  const range = h.memoria(["get", "notes/hello.md", "--from-line", "3", "--max-lines", "1", "--line-numbers"], { key });
  h.check("get with a line range", range.out.includes("zebrafish protocol") && !range.out.includes("Hello"), range.out + range.err);

  h.check("multi-get", h.memoria(["multi-get", "notes/hello.md"], { key }).out.includes("zebrafish protocol"));
  h.check("multi-get by pattern", h.memoria(["multi-get", "--pattern", "notes/*.md", "--format", "table"], { key }).out.includes("hello.md"));
  h.check("multi-get of nothing: exit 4", h.memoria(["multi-get", "notes/none.md"], { key }).code === 4);

  const found = h.memoria(["query", "zebrafish", "--no-rerank", "--format", "json"], { key });
  h.check("query finds only the key's collection", found.code === 0 && found.out.includes("hello.md") && !found.out.includes("hidden"), found.out + found.err);
  h.check("query as a table", h.memoria(["query", "zebrafish", "--no-rerank"], { key }).out.includes("SCORE"));
}

/** References that must never reach a sidecar route other than document lookup. */
export const TRAVERSALS = [
  "..%2Fstatus",
  "..%2fstatus",
  "a%2F..%2F..%2Fstatus",
  "notes%2F..%2F..%2Fstatus",
  "..%2F..%2Fstatus",
  "..%2F",
  "%2E%2E%2Fstatus",
  "%2e%2e%2fstatus",
  "..%5Cstatus",
  "..%5C..%5Cstatus",
  "..%252Fstatus",
  "%252E%252E%252Fstatus",
  "%2F%2Fstatus",
];

/** What a URL means to whatever receives it: one round of decoding, and any backslash as a separator. */
function escapesDocuments(url: string): boolean {
  const path = decodeURIComponent(url.split("?", 1)[0] ?? "").replaceAll("\\", "/");
  return !path.startsWith("/docs/") || path.split("/").includes("..");
}

function strayCalls(h: Harness, from: number): string[] {
  return h.proxied(from).map((call) => call.url ?? "").filter(escapesDocuments);
}

function sawMarker(h: Harness, from: number): boolean {
  return h.proxied(from).some((call) => call.url?.startsWith("/docs/notes/marker.md"));
}

export async function traversal(h: Harness, { reader }: Keys): Promise<void> {
  // A control: the proxy has to show lookups, or finding no stray call proves nothing.
  const start = h.proxiedCount();
  const control = await h.rawGet("/v1/docs/notes%2Fhello.md", reader);
  const calls = h.proxied(start).map((call) => call.url ?? "");
  h.check("traversal control: an encoded slash works, as one scoped lookup at the sidecar", control.status === 200 && calls.length === 1 && calls[0]?.startsWith("/docs/notes/hello.md") && calls[0].includes("collections=notes"), { status: control.status, calls });

  for (const reference of TRAVERSALS) {
    const before = h.proxiedCount();
    const response = await h.rawGet(`/v1/docs/${reference}`, reader);
    await h.rawGet("/v1/docs/notes%2Fmarker.md", reader);
    // Without the marker on record, "no stray call" would prove nothing.
    h.check(`${reference}: 404 or 400, and only document lookups at the sidecar`, [400, 404].includes(response.status) && !response.body.includes("totalDocuments") && sawMarker(h, before) && strayCalls(h, before).length === 0, { status: response.status, calls: h.proxied(before) });
  }
  mcpTraversal(h, reader);
}

function mcpTraversal(h: Harness, reader: string): void {
  const references = ["../status", "notes/../../status", "..\\status", "notes/marker.md"];
  const frames = [INITIALIZE, { jsonrpc: "2.0", method: "notifications/initialized" }, ...references.map((reference, index) => toolCall(index + 2, "get_document", { reference }))];
  const before = h.proxiedCount();
  const session = h.memoria(["mcp"], { key: reader, stdin: frames.map((frame) => JSON.stringify(frame)).join("\n") + "\n" });
  h.check("MCP get_document: dot segments reach only document lookup, and the marker reaches it", session.code === 0 && sawMarker(h, before) && strayCalls(h, before).length === 0, h.proxied(before));
}

export interface Reply {
  id?: number;
  result?: { tools?: Array<{ name: string }>; isError?: boolean };
  error?: { message?: string; data?: { status?: number } };
}

export function mcpFrames(out: string): { replies: Map<number, Reply>; badLines: number } {
  const replies = new Map<number, Reply>();
  let badLines = 0;
  for (const line of out.split("\n").filter(Boolean)) {
    try {
      const parsed = JSON.parse(line) as Reply;
      if (parsed.id !== undefined) replies.set(parsed.id, parsed);
    } catch {
      badLines++;
    }
  }
  return { replies, badLines };
}

export const INITIALIZE = { jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "e2e", version: "1" } } };

export function toolCall(id: number, name: string, args: object): object {
  return { jsonrpc: "2.0", id, method: "tools/call", params: { name, arguments: args } };
}

export function mcp(h: Harness, keys: Keys): void {
  mcpSession(h, keys.reader);
  mcpRefused(h, "an admin key", keys.admin, 403, "by design");
  mcpRefused(h, "a bad key", BAD_KEY, 401, "authentication failed");
}

function mcpSession(h: Harness, reader: string): void {
  const frames = [
    INITIALIZE,
    { jsonrpc: "2.0", method: "notifications/initialized" },
    { jsonrpc: "2.0", id: 2, method: "tools/list" },
    toolCall(3, "list_collections", {}),
    toolCall(4, "get_document", { reference: "secret/hidden.md" }),
    toolCall(5, "get_document", { reference: "notes/hello.md" }),
    { jsonrpc: "2.0", id: 6, method: "no/such/method" },
  ];
  const session = h.memoria(["mcp"], { key: reader, stdin: frames.map((frame) => JSON.stringify(frame)).join("\n") + "\n" });
  const { replies, badLines } = mcpFrames(session.out);
  h.check("mcp: exit 0, stdout is only JSON-RPC frames, every request answered", session.code === 0 && badLines === 0 && [1, 2, 3, 4, 5, 6].every((id) => replies.has(id)), session.out + session.err);
  h.check("mcp: initialize and tools/list", replies.get(1)?.result && replies.get(2)?.result?.tools?.some((tool) => tool.name === "search"), [...replies.values()].slice(0, 2));
  const text = (id: number): string => JSON.stringify(replies.get(id) ?? null);
  h.check("mcp: list_collections sees only the key's scope", text(3).includes("notes") && !text(3).includes("secret"), text(3));
  h.check("mcp: a document outside the scope is refused", !text(4).includes("ledger"), text(4));
  h.check("mcp: a document in scope comes back", text(5).includes("zebrafish protocol"), text(5));
  h.check("mcp: an unknown method gets a JSON-RPC error", replies.get(6)?.error, text(6));
}

function mcpRefused(h: Harness, label: string, key: string, status: number, phrase: string): void {
  const refused = h.memoria(["mcp"], { key, stdin: JSON.stringify(INITIALIZE) + "\n" });
  const error = mcpFrames(refused.out).replies.get(1)?.error;
  h.check(`mcp with ${label}: real ${status} as a JSON-RPC error`, refused.code === 0 && error?.data?.status === status && error.message?.includes(phrase), refused.out + refused.err);
}

/** The file backend, so the driver needs no keyring. See README.md for the one keyring touch. */
export function login(h: Harness, { admin, reader }: Keys): void {
  const profile = ["--profile", PROFILE];
  const loggedIn = h.memoria(["auth", "login", ...profile, "--api-url", h.apiUrl, "--backend", "file"], { stdin: reader + "\n", noUrl: true });
  h.check("auth login stores a real scoped key", loggedIn.code === 0 && loggedIn.err.includes("Logging in to"), loggedIn.out + loggedIn.err);
  h.check("login's output never has the key", !(loggedIn.out + loggedIn.err).includes(reader));
  const stored = h.memoria(["collections", ...profile], { noUrl: true });
  h.check("a stored key works with no flag or env", stored.code === 0 && stored.out.includes("notes"), stored.out + stored.err);
  const status = h.memoria(["auth", "status", ...profile], { noUrl: true });
  h.check("auth status against the real server", status.code === 0 && status.out.includes("accepted by server: true"), status.out + status.err);
  const out = h.memoria(["auth", "logout", ...profile], { noUrl: true });
  h.check("auth logout removes it", out.code === 0 && out.err.includes("Removed the key for"), out.out + out.err);
  h.check("after logout there is no key: exit 3", h.memoria(["collections", ...profile]).code === 3);

  const withAdmin = h.memoria(["auth", "login", ...profile, "--api-url", h.apiUrl, "--backend", "file"], { stdin: admin + "\n", noUrl: true });
  h.check("logging in with an admin key says what it cannot do", withAdmin.code === 0 && withAdmin.err.includes("cannot search"), withAdmin.out + withAdmin.err);
  h.memoria(["auth", "logout", ...profile], { noUrl: true });
}

function createBurner(h: Harness, admin: string): string {
  return createKey(h, admin, ["--label", "burner", "--scope", "read", "--collection", "notes"]);
}

/** Must run last: it uses up the failed-auth limit for this address. */
export async function rateLimits(h: Harness, { admin }: Keys): Promise<void> {
  const burner = createBurner(h, admin);
  for (let spent = 0; spent < KEY_RATE_LIMIT; spent++) {
    await h.rawGet("/v1/collections", burner);
  }
  const perKey = h.memoria(["collections"], { key: burner });
  h.check("per-key 429 on exactly the first request over the limit: exit 6, Retry-After", perKey.code === 6 && /retry after \d+s/.test(perKey.err), perKey.out + perKey.err);

  for (let spent = 0; spent <= FAILED_AUTH_LIMIT; spent++) {
    await h.rawGet("/v1/collections", BAD_KEY);
  }
  const failed = h.memoria(["status"], { key: BAD_KEY });
  h.check("failed-auth 429 after real 401s: exit 6, Retry-After", failed.code === 6 && /retry after \d+s/.test(failed.err), failed.out + failed.err);
}

export function noModels(h: Harness): void {
  h.check("the sidecar ran in remote mode, so no local model could load", h.log("sidecar").includes('"llm":"remote"'));
  const files = h.modelFiles();
  h.check("no model file reached the private cache", files.length === 0, files);
}
