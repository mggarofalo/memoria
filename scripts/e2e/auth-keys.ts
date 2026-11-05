/**
 * Auth mode, the life of a key, and its audit trail, over real HTTP. Each check pairs
 * what must not happen with a marker that shows the check could have seen it.
 */
import { createHash } from "node:crypto";
import { ADMIN_KEYS, BAD_KEY, COLLECTIONS, PEER, auditByTrace, codeOf, count, idOf, issueKey, logShows, newTrace, parse, readerOf, repeat, statuses, type AuditRow, type IssuedKey } from "./auth-helpers.ts";
import { FAILED_AUTH_LIMIT, pause, repo, type Harness } from "./harness.ts";

const MODE_ERROR = "Memoria:Auth:Mode must be exactly";
const REFUSED_MODES = ["1", "0", "disabled", "DISABLED", " Disabled ", "apikey", "None", "ApiKey,Disabled", ""];
const EXPIRY_MS = 10_000;

interface ListedKey {
  id: string;
  revokedAt: string | null;
}

interface Refusal {
  name: string;
  expects: string;
  env: NodeJS.ProcessEnv;
}

function hostRefusals(h: Harness): Refusal[] {
  const modes = REFUSED_MODES.map((mode, index): Refusal => ({
    name: `Auth:Mode ${JSON.stringify(mode)} stops the host`,
    expects: MODE_ERROR,
    env: { Memoria__Auth__Mode: mode, Memoria__Auth__DatabasePath: h.privatePath(`refused-${index}.sqlite`) },
  }));
  return [
    ...modes,
    { name: "ApiKey with no database stops the host", expects: "Memoria:Auth:DatabasePath is required", env: { Memoria__Auth__DatabasePath: undefined } },
    { name: "a /0 trusted proxy stops the host", expects: "TrustedProxies has an invalid entry", env: { Memoria__Auth__TrustedProxies__0: "0.0.0.0/0", Memoria__Auth__DatabasePath: h.privatePath("refused-all.sqlite") } },
    { name: "an unparseable trusted proxy stops the host", expects: "TrustedProxies has an invalid entry", env: { Memoria__Auth__TrustedProxies__0: "not-an-address", Memoria__Auth__DatabasePath: h.privatePath("refused-bad.sqlite") } },
  ];
}

export async function authModes(h: Harness, admin: string): Promise<void> {
  const refusals = hostRefusals(h);
  const outcomes = await Promise.all(refusals.map((refusal) => h.refusedHost(refusal.env)));
  refusals.forEach((refusal, index) => {
    const outcome = outcomes[index];
    h.check(refusal.name, outcome?.refused && outcome.output.includes(refusal.expects), outcome);
  });

  const anonymous = await h.request({ path: COLLECTIONS });
  const withKey = await h.request({ path: ADMIN_KEYS, key: admin });
  h.check('Auth:Mode "ApiKey" exactly: no key is 401, a real key works', anonymous.status === 401 && withKey.status === 200, statuses([anonymous, withKey]));
  await disabledHost(h);
  await unsetHost(h);
}

async function disabledHost(h: Harness): Promise<void> {
  const host = await h.startExtraHost("disabled", { Memoria__Auth__Mode: "Disabled", Memoria__Auth__DatabasePath: undefined });
  const anonymous = await h.request({ baseUrl: host.url, path: COLLECTIONS });
  h.check('Auth:Mode "Disabled" exactly: no key reads every collection, with no database', anonymous.status === 200 && anonymous.body.includes('"secret"'), anonymous);
  const management = await h.request({ baseUrl: host.url, path: ADMIN_KEYS });
  h.check("Disabled mode has no key management", management.status === 404 && codeOf(management) === "auth_disabled", management);
  h.check("Disabled mode warns loudly", await logShows(host.log, "AUTHENTICATION IS DISABLED"));
}

/** The shipped settings file names a mode, so only a host that does not load it falls back on the default. */
async function unsetHost(h: Harness): Promise<void> {
  const host = await h.startExtraHost("unset-mode", { Memoria__Auth__Mode: undefined, Memoria__Auth__DatabasePath: h.privatePath("unset-mode.sqlite") }, repo);
  const anonymous = await h.request({ baseUrl: host.url, path: COLLECTIONS });
  h.check("Auth:Mode unset, with no settings file, fails closed: no key is 401", anonymous.status === 401 && !anonymous.body.includes('"secret"'), anonymous);
}

export async function rotation(h: Harness, admin: string): Promise<void> {
  const key = await issueKey(h, admin, readerOf("rotated"));
  const rotate = await h.request({ method: "POST", path: `${ADMIN_KEYS}/${key.id}:rotate`, key: admin, issuing: true });
  const rotated = h.remember(parse<{ key: string }>(rotate).key);
  h.check("rotation: a new secret, the same id, not cacheable", rotate.status === 200 && rotated !== key.token && idOf(rotated) === key.id && rotate.headers["cache-control"] === "no-store", rotate.status);

  const [old, fresh] = await Promise.all([h.request({ path: COLLECTIONS, key: key.token, from: PEER.fresh }), h.request({ path: COLLECTIONS, key: rotated, from: PEER.fresh })]);
  h.check("rotation: the old token is 401 at once, and the new one works", old.status === 401 && fresh.status === 200, statuses([old, fresh]));
  const keepsScope = fresh.body.includes('"notes"') && !fresh.body.includes('"secret"');
  h.check("rotation: the new token keeps the key's scope", keepsScope, fresh.body);
}

async function revokedAtOf(h: Harness, admin: string, id: string): Promise<string | null | undefined> {
  const keys = parse<{ keys: ListedKey[] }>(await h.request({ path: ADMIN_KEYS, key: admin })).keys;
  return keys.find((key) => key.id === id)?.revokedAt;
}

export async function revocation(h: Harness, admin: string): Promise<void> {
  const key = await issueKey(h, admin, readerOf("revoked"));
  const before = await h.request({ path: COLLECTIONS, key: key.token, from: PEER.fresh });
  const revoked = await h.request({ method: "DELETE", path: `${ADMIN_KEYS}/${key.id}`, key: admin });
  const after = await h.request({ path: COLLECTIONS, key: key.token, from: PEER.fresh });
  h.check("revocation: the key works, then is 401 on its next request", before.status === 200 && revoked.status === 204 && after.status === 401, statuses([before, revoked, after]));

  const first = await revokedAtOf(h, admin, key.id);
  await pause(20);
  const again = await h.request({ method: "DELETE", path: `${ADMIN_KEYS}/${key.id}`, key: admin });
  const second = await revokedAtOf(h, admin, key.id);
  h.check("revoking again succeeds and keeps the first timestamp", again.status === 204 && Boolean(first) && first === second, { first, second });

  const rotate = await h.request({ method: "POST", path: `${ADMIN_KEYS}/${key.id}:rotate`, key: admin });
  h.check("a revoked key cannot be rotated: 409", rotate.status === 409 && codeOf(rotate) === "key_inactive", rotate);
  const unknown = await h.request({ method: "POST", path: `${ADMIN_KEYS}/${"f".repeat(16)}:rotate`, key: admin });
  h.check("rotating an unknown key: 404", unknown.status === 404, unknown);
}

export interface Expiring {
  key: IssuedKey;
  expiresAt: number;
}

/** Started early, so the rest of the run spends the wait for it. */
export async function startExpiry(h: Harness, admin: string): Promise<Expiring> {
  const past = await h.request({ method: "POST", path: ADMIN_KEYS, key: admin, body: { ...readerOf("expired-at-birth"), expiresAt: new Date(Date.now() - 1000).toISOString() } });
  h.check("a key cannot be issued already expired: 400", past.status === 400 && past.body.includes("expiresAt"), past);

  const expiresAt = Date.now() + EXPIRY_MS;
  const key = await issueKey(h, admin, { ...readerOf("expiring"), expiresAt: new Date(expiresAt).toISOString() });
  const before = await h.request({ path: COLLECTIONS, key: key.token, from: PEER.fresh });
  h.check("a key works before it expires", before.status === 200 && Date.now() < expiresAt, before.status);
  return { key, expiresAt };
}

async function exhaustKnownPeer(h: Harness): Promise<void> {
  const spent = statuses(await repeat(FAILED_AUTH_LIMIT, () => h.request({ path: COLLECTIONS, key: BAD_KEY, from: PEER.known })));
  const over = await h.request({ path: COLLECTIONS, key: BAD_KEY, from: PEER.known });
  h.check("known-good setup: the peer is over its failed-auth budget", count(spent, 401) === FAILED_AUTH_LIMIT && over.status === 429, { spent, over: over.status });
}

/** Run with the peer over budget, so a 429 here means no lookup and a 200 means the bypass. */
export async function knownGood(h: Harness, admin: string): Promise<void> {
  await exhaustKnownPeer(h);
  const key = await issueKey(h, admin, readerOf("known"));
  const from = { from: PEER.known };
  const works = await h.request({ ...from, path: COLLECTIONS, key: key.token });
  h.check("known-good: a real key is not locked out by a guesser at its address", works.status === 200, works.status);
  const guess = await h.request({ ...from, path: COLLECTIONS, key: `mem_${key.id}_${"a".repeat(64)}` });
  // A guess that were let through to the store would fail there and evict the real key, so the real key is the proof.
  const stillKnown = await h.request({ ...from, path: COLLECTIONS, key: key.token });
  h.check("known-good: a wrong secret on a known id gets no bypass, and cannot evict the real key", guess.status === 429 && stillKnown.status === 200, statuses([guess, stillKnown]));

  const rotate = await h.request({ method: "POST", path: `${ADMIN_KEYS}/${key.id}:rotate`, key: admin, issuing: true });
  const rotated = h.remember(parse<{ key: string }>(rotate).key);
  const oldToken = await h.request({ ...from, path: COLLECTIONS, key: key.token });
  const newToken = await h.request({ ...from, path: COLLECTIONS, key: rotated });
  h.check("known-good after rotation: the old token pays, and the new one is known", oldToken.status === 429 && newToken.status === 200, statuses([oldToken, newToken]));

  await h.request({ method: "DELETE", path: `${ADMIN_KEYS}/${key.id}`, key: admin });
  const revokedHere = await h.request({ ...from, path: COLLECTIONS, key: rotated });
  const revokedElsewhere = await h.request({ path: COLLECTIONS, key: rotated, from: PEER.fresh });
  h.check("known-good after revocation: no bypass, and 401 from an address with budget", revokedHere.status === 429 && revokedElsewhere.status === 401, statuses([revokedHere, revokedElsewhere]));
}

/** A known key that has expired is found out at the store, then stops being known and pays, like any failure. */
export async function finishExpiry(h: Harness, admin: string, { key, expiresAt }: Expiring): Promise<void> {
  await pause(Math.max(0, expiresAt - Date.now()) + 200);
  const overBudget = await h.request({ path: COLLECTIONS, key: key.token, from: PEER.known });
  h.check("an expired key gets no bypass from an over-budget peer: 429", overBudget.status === 429, overBudget.status);
  const elsewhere = await h.request({ path: COLLECTIONS, key: key.token, from: PEER.fresh });
  h.check("an expired key is 401 from an address with budget", elsewhere.status === 401, elsewhere.status);
  const rotate = await h.request({ method: "POST", path: `${ADMIN_KEYS}/${key.id}:rotate`, key: admin });
  h.check("an expired key cannot be rotated: 409", rotate.status === 409 && codeOf(rotate) === "key_inactive", rotate);
}

function targetIs(row: AuditRow | undefined, id: string): boolean {
  if (!row?.target) return false;
  const target = JSON.parse(row.target) as { KeyId?: string; Scopes?: string[] };
  return target.KeyId === id && Array.isArray(target.Scopes);
}

/** UUID version 7 as 32 hex digits: made by the server, and not the trace id the caller chose. */
function serverGenerated(row: AuditRow | undefined, traceId: string): boolean {
  return /^[0-9a-f]{12}7[0-9a-f]{19}$/.test(row?.audit_id ?? "") && row?.audit_id !== traceId;
}

async function adminRows(h: Harness, admin: string): Promise<void> {
  const created = newTrace();
  const created2 = await h.request({ method: "POST", path: ADMIN_KEYS, key: admin, issuing: true, body: readerOf("audited"), headers: { ...created.headers, "X-Audit-Id": "caller-chosen", "Request-Id": "caller-chosen" } });
  const token = h.remember(parse<{ key: string }>(created2).key);
  const id = idOf(token);
  const [row] = await auditByTrace(h, created.traceId);
  h.check("audit: issuing a key records its server-made id and actor, naming the target", row?.operation === "createApiKey" && row.key_id === idOf(admin) && row.status === 201 && serverGenerated(row, created.traceId) && targetIs(row, id), row);
  await rotatedAndRevokedRows(h, admin, token, id);
}

async function rotatedAndRevokedRows(h: Harness, admin: string, token: string, id: string): Promise<void> {
  const rotated = newTrace();
  const rotate = await h.request({ method: "POST", path: `${ADMIN_KEYS}/${id}:rotate`, key: admin, issuing: true, headers: rotated.headers });
  const newToken = h.remember(parse<{ key: string }>(rotate).key);
  const [rotatedRow] = await auditByTrace(h, rotated.traceId);
  h.check("audit: rotation names the target", rotatedRow?.operation === "rotateApiKey" && rotatedRow.status === 200 && targetIs(rotatedRow, id), rotatedRow);
  h.check("the stored value is the new token's SHA-256, and the old token's is gone", storedHash(h, id) === sha256(newToken) && storedHash(h, id) !== sha256(token));

  const revoked = newTrace();
  await h.request({ method: "DELETE", path: `${ADMIN_KEYS}/${id}`, key: admin, headers: revoked.headers });
  const [revokedRow] = await auditByTrace(h, revoked.traceId);
  h.check("audit: revocation names the target", revokedRow?.operation === "revokeApiKey" && revokedRow.status === 204 && targetIs(revokedRow, id), revokedRow);
}

function sha256(token: string): string {
  return createHash("sha256").update(token).digest("hex");
}

function storedHash(h: Harness, id: string): string {
  const [row] = h.control<{ hash: string }>("SELECT hex(sha256_hash) AS hash FROM api_keys WHERE id = ?", id);
  return row?.hash.toLowerCase() ?? "";
}

async function callerCannotChooseId(h: Harness, reader: string): Promise<void> {
  const trace = newTrace();
  await h.request({ path: COLLECTIONS, key: reader, headers: trace.headers });
  await h.request({ path: COLLECTIONS, key: reader, headers: trace.headers });
  const rows = await auditByTrace(h, trace.traceId, 2);
  const ids = new Set(rows.map((row) => row.audit_id));
  h.check("audit: one trace id sent twice makes two rows with different ids", rows.length === 2 && ids.size === 2, rows);
}

async function deniedAndAnonymous(h: Harness, reader: string): Promise<void> {
  const denied = newTrace();
  await h.request({ path: ADMIN_KEYS, key: reader, headers: denied.headers });
  const [row] = await auditByTrace(h, denied.traceId);
  h.check("audit: a 403 is recorded, with no target", row?.status === 403 && row.key_id === idOf(reader) && row.target === null, row);

  const anonymous = newTrace();
  const bad = await h.request({ path: COLLECTIONS, key: BAD_KEY, from: PEER.audit, headers: anonymous.headers });
  const marker = newTrace();
  await h.request({ path: COLLECTIONS, key: reader, headers: marker.headers });
  const markerRows = await auditByTrace(h, marker.traceId);
  const anonymousRows = h.control<AuditRow>("SELECT * FROM audit_log WHERE trace_id = ?", anonymous.traceId);
  h.check("audit: a bad key writes no row, and the marker after it does", bad.status === 401 && markerRows.length === 1 && anonymousRows.length === 0, { bad: bad.status, markerRows, anonymousRows });
}

async function queriesStayOut(h: Harness, reader: string): Promise<void> {
  const trace = newTrace();
  const search = await h.request({ path: "/v1/search?query=zebrafish&rerank=false", key: reader, headers: trace.headers });
  const [row] = await auditByTrace(h, trace.traceId);
  h.check("audit: a search records its collections and count, and not its query", search.status === 200 && row?.query === null && row.collections === '["notes"]' && row.result_count === 1, row);
}

export async function auditTrail(h: Harness, admin: string, reader: string): Promise<void> {
  await adminRows(h, admin);
  await callerCannotChooseId(h, reader);
  await deniedAndAnonymous(h, reader);
  await queriesStayOut(h, reader);
}
