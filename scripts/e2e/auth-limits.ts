/**
 * Rate limits and failed-auth limits at exact counts, on a forbidden route, and whose
 * X-Forwarded-For is believed. A peer here is a real source address on loopback.
 */
import { ADMIN_KEYS, BAD_KEY, COLLECTIONS, PEER, auditByKey, codeOf, count, hasRetryAfter, issueKey, logShows, readerOf, repeat, statuses, type IssuedKey } from "./auth-helpers.ts";
import { FAILED_AUTH_LIMIT, KEY_RATE_LIMIT, TRUSTED_PEER, type ApiResponse, type Harness } from "./harness.ts";

const SPOOFED = "203.0.113.50";
const CLIENT_A = "198.51.100.7";
const CLIENT_B = "198.51.100.8";
const CLIENT_C = "198.51.100.9";

/** The limiter runs before authorization, so a key spends its permits on routes it may not use. */
async function forbiddenRouteLimit(h: Harness, admin: string): Promise<IssuedKey> {
  const key = await issueKey(h, admin, readerOf("forbidden-burner"));
  const allowed = await repeat(KEY_RATE_LIMIT, (index) => h.request({ path: ADMIN_KEYS, key: key.token, from: index % 2 === 0 ? PEER.limits : PEER.fresh }));
  const every403 = allowed.every((answer) => answer.status === 403 && codeOf(answer) === "insufficient_scope");
  h.check(`per-key limit: exactly ${KEY_RATE_LIMIT} 403s on a forbidden route, from two peers`, every403, statuses(allowed));

  const over = await h.request({ path: ADMIN_KEYS, key: key.token, from: PEER.limits });
  h.check("per-key limit: the next request is 429, with Retry-After", over.status === 429 && codeOf(over) === "rate_limited" && hasRetryAfter(over), over);
  const allowedRoute = await h.request({ path: COLLECTIONS, key: key.token, from: PEER.limits });
  h.check("per-key limit: the budget covers every route", allowedRoute.status === 429, allowedRoute.status);

  const other = await issueKey(h, admin, readerOf("forbidden-neighbour"));
  const neighbour = await h.request({ path: COLLECTIONS, key: other.token, from: PEER.limits });
  h.check("per-key limit: another key at the same address is unaffected", neighbour.status === 200, neighbour.status);

  const rows = await auditByKey(h, key.id, (found) => found.length >= KEY_RATE_LIMIT + 2);
  const recorded = rows.map((row) => row.status);
  h.check("per-key limit: the audit table has every 403 and both 429s", count(recorded, 403) === KEY_RATE_LIMIT && count(recorded, 429) === 2, { total: rows.length, forbidden: count(recorded, 403), limited: count(recorded, 429) });
  return key;
}

/** Runs on a peer that has just made 100 forbidden requests, so a 403 spending budget would show here. */
async function failedAuthLimit(h: Harness): Promise<void> {
  const failures = await repeat(FAILED_AUTH_LIMIT, () => h.request({ path: ADMIN_KEYS, key: BAD_KEY, from: PEER.limits }));
  h.check(`failed-auth limit: exactly ${FAILED_AUTH_LIMIT} 401s on a forbidden route, and 403s spent none of them`, count(statuses(failures), 401) === FAILED_AUTH_LIMIT, statuses(failures));
  const over = await h.request({ path: ADMIN_KEYS, key: BAD_KEY, from: PEER.limits });
  h.check("failed-auth limit: the next request is 429, with Retry-After", over.status === 429 && codeOf(over) === "auth_rate_limited" && hasRetryAfter(over), over);
}

async function failedAuthConcurrent(h: Harness): Promise<void> {
  // No credential at all is not a failed attempt, so these spend nothing.
  const anonymous = await repeat(FAILED_AUTH_LIMIT + 5, () => h.request({ path: COLLECTIONS, from: PEER.concurrent }));
  h.check("failed-auth limit: no credential spends no budget", count(statuses(anonymous), 401) === anonymous.length, statuses(anonymous));

  const attempts = FAILED_AUTH_LIMIT * 2;
  const answers = statuses(await Promise.all(Array.from({ length: attempts }, () => h.request({ path: COLLECTIONS, key: BAD_KEY, from: PEER.concurrent }))));
  h.check(`failed-auth limit under concurrency: exactly ${FAILED_AUTH_LIMIT} of ${attempts} reach a lookup`, count(answers, 401) === FAILED_AUTH_LIMIT && count(answers, 429) === FAILED_AUTH_LIMIT, answers);

  const malformed = statuses(await repeat(FAILED_AUTH_LIMIT + 1, () => h.request({ path: COLLECTIONS, key: "not-a-key", from: PEER.malformed })));
  h.check("failed-auth limit: a malformed token spends budget too", count(malformed, 401) === FAILED_AUTH_LIMIT && malformed.at(-1) === 429, malformed);
}

export async function limits(h: Harness, admin: string): Promise<void> {
  await forbiddenRouteLimit(h, admin);
  await failedAuthLimit(h);
  await failedAuthConcurrent(h);
}

function badAuth(h: Harness, from: string, forwarded?: string): Promise<ApiResponse> {
  return h.request({ path: COLLECTIONS, key: BAD_KEY, from, headers: forwarded ? { "X-Forwarded-For": forwarded } : {} });
}

/** A different forwarded address every time would get a new budget every time, if it were believed. */
async function untrustedPeer(h: Harness): Promise<void> {
  const spoofed = await repeat(FAILED_AUTH_LIMIT, (index) => badAuth(h, PEER.untrusted, `${SPOOFED.slice(0, -2)}${index + 1}`));
  h.check("untrusted peer: rotating X-Forwarded-For does not make a new budget", count(statuses(spoofed), 401) === FAILED_AUTH_LIMIT, statuses(spoofed));
  const over = await badAuth(h, PEER.untrusted, SPOOFED);
  h.check("untrusted peer: the socket address's budget runs out", over.status === 429, over.status);
  h.check("untrusted peer: the log names the socket address", await logShows(() => h.log("api"), `Address ${PEER.untrusted} exceeded`));
  h.check("untrusted peer: the API warns about the ignored header", await logShows(() => h.log("api"), `private address ${PEER.untrusted} carried X-Forwarded-For`));
  h.check("untrusted peer: the spoofed address never appears as a client", !h.log("api").includes(`from ${SPOOFED}`) && !h.log("api").includes(`Address ${SPOOFED}`));
}

/** The chain is walked from the right and stops at the first address that is not a trusted proxy. */
async function trustedPeer(h: Harness, admin: string): Promise<void> {
  const spent = await repeat(FAILED_AUTH_LIMIT, () => badAuth(h, TRUSTED_PEER, CLIENT_A));
  const over = await badAuth(h, TRUSTED_PEER, CLIENT_A);
  h.check("trusted peer: the forwarded client has the budget, at the exact count", count(statuses(spent), 401) === FAILED_AUTH_LIMIT && over.status === 429, statuses([...spent, over]));
  h.check("trusted peer: the log names the forwarded client", await logShows(() => h.log("api"), `Address ${CLIENT_A} exceeded`));

  const other = await badAuth(h, TRUSTED_PEER, CLIENT_B);
  const proxyItself = await badAuth(h, TRUSTED_PEER);
  h.check("trusted peer: another client, and the proxy itself, have their own budgets", other.status === 401 && proxyItself.status === 401, statuses([other, proxyItself]));

  const leftIgnored = await badAuth(h, TRUSTED_PEER, `${CLIENT_A}, ${CLIENT_C}`);
  const leftLies = await badAuth(h, TRUSTED_PEER, `${CLIENT_C}, ${CLIENT_A}`);
  const throughProxy = await badAuth(h, TRUSTED_PEER, `${CLIENT_A}, ${TRUSTED_PEER}`);
  h.check("trusted peer: only the rightmost untrusted address counts", leftIgnored.status === 401 && leftLies.status === 429 && throughProxy.status === 429, statuses([leftIgnored, leftLies, throughProxy]));

  const key = await issueKey(h, admin, readerOf("behind-proxy"));
  const real = await h.request({ path: COLLECTIONS, key: key.token, from: TRUSTED_PEER, headers: { "X-Forwarded-For": CLIENT_A } });
  h.check("trusted peer: a real key still works for a client over budget", real.status === 200, real.status);
  h.check("trusted peer: the request log names the forwarded client", await logShows(() => h.log("api"), `from ${CLIENT_A}, key ${key.id}`));
}

export async function forwardedHeaders(h: Harness, admin: string): Promise<void> {
  await untrustedPeer(h);
  await trustedPeer(h, admin);
}
