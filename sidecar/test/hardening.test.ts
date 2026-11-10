import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { connect, type Socket } from "node:net";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { buildApp, type App } from "../src/app.ts";
import { loggablePath, refuseAtBoundary, validRequestId } from "../src/boundary.ts";
import { loadConfig } from "../src/config.ts";
import { parseLineRange } from "../src/line-range.ts";
import { DEFAULT_HEAD_BYTES, MetadataIndex, parseBound, parseFrontmatter } from "../src/metadata.ts";
import { patchedStore } from "../scripts/patch-qmd.ts";
import { missingSdkFixes, requireSdkFixes, type SdkStoreFunctions } from "../src/sdk-check.ts";
import { SearchBusy, SearchGate } from "../src/search-gate.ts";
import { FakeBackend } from "./fake-backend.ts";
import { authHeaders, makeApp, testConfig } from "./helpers.ts";

/** Replays of what a review proved: a document or request must not freeze or crash the sidecar. */

const sdkUrl = new URL("./store.js", import.meta.resolve("@tobilu/qmd")).href;
const sdk = (await import(sdkUrl)) as {
  chunkDocument(text: string): Array<{ text: string; pos: number }>;
  findBestCutoff(points: Array<{ pos: number; score: number }>, target: number, window: number, decay: number, fences: Array<{ start: number; end: number }>): number;
  isInsideCodeFence(pos: number, fences: Array<{ start: number; end: number }>): boolean;
};

// --- the SDK patch --------------------------------------------------------------------------------

const installedStore = fileURLToPath(sdkUrl);

test("installing applied the SDK patch", () => {
  assert.deepEqual(missingSdkFixes(sdk as unknown as SdkStoreFunctions), []);
});

test("the patch refuses an SDK whose text has moved, and running it twice changes nothing", () => {
  assert.throws(() => patchedStore("export function somethingElse() {}"), /The QMD SDK has changed.*patch-qmd\.ts/);
  assert.throws(() => patchedStore("// memoria-patched\nexport function somethingElse() {}"), /The QMD SDK has changed/, "a marker alone proves nothing");
  const dropped = readFileSync(installedStore, "utf8").replace("const bodyOf = db.prepare", "const bodyWas = db.prepare");
  assert.throws(() => patchedStore(dropped), /The QMD SDK has changed/, "each edit is checked");
  const once = readFileSync(installedStore, "utf8");
  assert.equal(patchedStore(once), once);
  assert.equal(patchedStore(once.replace(/\n/g, "\r\n")), once, "whatever the line endings");
});

/** The SDK's originals, word for word, to hold the patched versions to their answers. */
const originalInside = (pos: number, fences: Array<{ start: number; end: number }>): boolean => fences.some((f) => pos > f.start && pos < f.end);
function originalCutoff(points: Array<{ pos: number; score: number }>, target: number, windowChars: number, decay: number, fences: Array<{ start: number; end: number }>): number {
  const windowStart = target - windowChars;
  let bestScore = -1;
  let bestPos = target;
  for (const bp of points) {
    if (bp.pos < windowStart) continue;
    if (bp.pos > target) break;
    if (originalInside(bp.pos, fences)) continue;
    const distance = target - bp.pos;
    const normalizedDist = distance / windowChars;
    const finalScore = bp.score * (1.0 - normalizedDist * normalizedDist * decay);
    if (finalScore > bestScore) {
      bestScore = finalScore;
      bestPos = bp.pos;
    }
  }
  return bestPos;
}

test("the patched scans answer exactly as the originals did", () => {
  let seed = 12345;
  const random = (max: number): number => (seed = (seed * 1103515245 + 12345) % 2147483648) % max;
  for (let round = 0; round < 300; round++) {
    const points = Array.from({ length: random(60) }, (_, i) => ({ pos: i * 7 + random(7), score: 1 + random(100) })).sort((a, b) => a.pos - b.pos);
    const fences: Array<{ start: number; end: number }> = [];
    for (let at = random(50); at < 500 && fences.length < 12; at += 30 + random(60)) fences.push({ start: at, end: at + 1 + random(29) });
    for (let check = 0; check < 40; check++) {
      const pos = random(520);
      assert.equal(sdk.isInsideCodeFence(pos, fences), originalInside(pos, fences), `inside at ${pos}`);
      const window = 1 + random(200);
      assert.equal(sdk.findBestCutoff(points, pos, window, 0.7, fences), originalCutoff(points, pos, window, 0.7, fences), `cutoff at ${pos}`);
    }
  }
  assert.equal(sdk.isInsideCodeFence(5, []), false);
});

function chunkInChild(expression: string, limitMs: number): { chunks: number; ms: number } {
  const script = `const { chunkDocument } = await import(${JSON.stringify(sdkUrl)}); const text = ${expression}; const t = performance.now(); const chunks = chunkDocument(text).length; console.log(JSON.stringify({ chunks, ms: Math.round(performance.now() - t) }));`;
  const run = spawnSync(process.execPath, ["--input-type=module", "-e", script], { encoding: "utf8", timeout: limitMs });
  assert.equal(run.error, undefined, `over the hard limit of ${limitMs} ms`);
  assert.equal(run.status, 0, run.stderr);
  return JSON.parse(run.stdout) as { chunks: number; ms: number };
}

test("4 MiB of code-fence lines chunks in moments, where it ran past 120 s", { timeout: 60_000 }, () => {
  assert.ok(chunkInChild(`"\`\`\`\\n".repeat(1024 * 1024)`, 20_000).chunks > 1000);
});

test("6 MiB of blank lines chunks in moments", { timeout: 60_000 }, () => {
  assert.ok(chunkInChild(`"\\n".repeat(6 * 1024 * 1024)`, 15_000).chunks > 1000);
});

// --- frontmatter ----------------------------------------------------------------------------------

const manyKeys = (n: number): string => {
  const lines = ["---"];
  for (let i = 0; i < n; i++) lines.push(`key${i}: value`);
  lines.push("---", "", "body");
  return lines.join("\n");
};

test("frontmatter with 100,000 keys takes no time, and costs nothing beyond the head", { timeout: 30_000 }, () => {
  const index = new MetadataIndex({ readDocumentHeads: () => [] });
  const text = manyKeys(100_000);
  const started = performance.now();
  assert.equal(index.set("c/x.md", text), undefined, "it never closes within the head");
  assert.ok(performance.now() - started < 2_000, `${performance.now() - started} ms`);
});

test("a head full of duplicate keys parses quickly, and the last one wins", { timeout: 30_000 }, () => {
  const lines = ["---"];
  while (lines.join("\n").length < DEFAULT_HEAD_BYTES - 40) lines.push("project: first");
  lines.push("project: last", "---", "");
  const started = performance.now();
  assert.equal(parseFrontmatter(lines.join("\n"))?.project, "last");
  assert.ok(performance.now() - started < 1_000);
});

test("YAML warnings do not send frontmatter text to stderr", () => {
  const seen: unknown[] = [];
  const original = process.emitWarning;
  process.emitWarning = ((warning: unknown) => void seen.push(warning)) as typeof process.emitWarning;
  try {
    assert.ok(parseFrontmatter("---\nproject: !!unknown SECRET-IN-FRONTMATTER\n---\n"));
  } finally {
    process.emitWarning = original;
  }
  assert.deepEqual(seen, []);
});

// --- document size ----------------------------------------------------------------------------------

test("a document over the cap is refused before anything is written", async () => {
  const { app, roots } = await makeApp({ config: { maxDocumentBytes: 1024 } });
  const dir = roots.claude;
  try {
    const post = (content: string, name: string) => app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: name, content } });
    const over = await post("x".repeat(1025), "over.md");
    assert.equal(over.statusCode, 413);
    assert.equal(over.json().error, "document_too_large");
    assert.match(over.json().message, /at most 1024 bytes/);
    assert.equal(existsSync(path.join(dir, "over.md")), false);
    // Bytes, not characters: this is 1,023 characters and over a thousand more bytes.
    assert.equal((await post("é".repeat(1023), "wide.md")).statusCode, 413);
    assert.equal((await post("x".repeat(1024), "fits.md")).statusCode, 202);
    assert.equal(existsSync(path.join(dir, "fits.md")), true);
  } finally {
    await app.close();
  }
});

test("the cap has a sensible default and can be set", () => {
  assert.equal(testConfig().maxDocumentBytes, 2 * 1024 * 1024);
  assert.equal(loadConfig({ MEMORIA_QMD_MAX_DOCUMENT_BYTES: "4096" }).maxDocumentBytes, 4096);
  assert.throws(() => loadConfig({ MEMORIA_QMD_MAX_DOCUMENT_BYTES: "1" }), /MEMORIA_QMD_MAX_DOCUMENT_BYTES/);
});

// --- server limits ------------------------------------------------------------------------------------

async function listening(over: Parameters<typeof testConfig>[0]): Promise<{ app: App; port: number }> {
  const app = await buildApp({ backend: new FakeBackend({ collections: [], docs: [] }), config: testConfig(over), logger: false });
  await app.listen({ host: "127.0.0.1", port: 0 });
  return { app, port: (app.server.address() as { port: number }).port };
}

const closedOrNot = (socket: Socket, withinMs: number): Promise<boolean> =>
  new Promise((resolve) => {
    if (socket.destroyed) resolve(true);
    socket.once("close", () => resolve(true));
    setTimeout(() => resolve(false), withinMs).unref();
  });

function opened(port: number, send: string): Promise<Socket> {
  return new Promise((resolve, reject) => {
    const socket = connect(port, "127.0.0.1", () => {
      socket.write(send);
      resolve(socket);
    });
    socket.on("error", (err) => (socket.destroyed ? undefined : reject(err)));
    socket.on("data", () => {});
  });
}

test("clients that never finish their headers are dropped, and others still get an answer", { timeout: 30_000 }, async () => {
  const { app, port } = await listening({ headersTimeoutMs: 200, maxConnectionsPerSource: 1000 });
  const sockets: Socket[] = [];
  try {
    for (let i = 0; i < 100; i++) sockets.push(await opened(port, "GET /health HTTP/1.1\r\nHost: localhost\r\nX-Slow: "));
    assert.equal((await fetch(`http://127.0.0.1:${port}/health`)).status, 200, "while they hang");
    const closed = await Promise.all(sockets.map((s) => closedOrNot(s, 10_000)));
    assert.equal(closed.filter(Boolean).length, 100);
  } finally {
    for (const s of sockets) s.destroy();
    await app.close();
  }
});

test("connections past the limit are turned away", { timeout: 30_000 }, async () => {
  const { app, port } = await listening({ maxConnections: 5, maxConnectionsPerSource: 1000 });
  const sockets: Socket[] = [];
  try {
    for (let i = 0; i < 5; i++) sockets.push(await opened(port, "GET /health HTTP/1.1\r\nHost: localhost\r\nX-Slow: "));
    const extra = await opened(port, "GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n");
    sockets.push(extra);
    assert.equal(await closedOrNot(extra, 5_000), true);
    assert.equal(sockets[0]!.destroyed, false);
  } finally {
    for (const s of sockets) s.destroy();
    await app.close();
  }
});

test("server limits have defaults and can be set", () => {
  const config = testConfig();
  assert.equal(config.headersTimeoutMs, 10_000);
  assert.equal(config.requestTimeoutMs, 300_000);
  assert.equal(config.maxConnections, 1024);
  assert.equal(loadConfig({ MEMORIA_QMD_MAX_CONNECTIONS: "7" }).maxConnections, 7);
  assert.equal(loadConfig({ MEMORIA_QMD_HEADERS_TIMEOUT_MS: "500" }).headersTimeoutMs, 500);
  assert.equal(loadConfig({ MEMORIA_QMD_REQUEST_TIMEOUT_MS: "5000" }).requestTimeoutMs, 5000);
});

// --- what gets logged -----------------------------------------------------------------------------------

async function logging(): Promise<{ app: App; lines: string[] }> {
  const lines: string[] = [];
  const app = await buildApp({ backend: new FakeBackend({ collections: [], docs: [] }), config: testConfig(), logger: { level: "info", stream: { write: (line: string) => void lines.push(line) } } });
  return { app, lines };
}

test("the log holds route patterns, never a path or query string a caller sent", async () => {
  const { app, lines } = await logging();
  const marker = "markerdocumentpath";
  try {
    const headers = { authorization: `Bearer ${testConfig().secret}` };
    for (const url of [`/docs/${marker}.md?query=${marker}`, `/documents?collection=${marker}&path=${marker}.md`, `/no-such-route/${marker}`]) {
      await app.inject({ method: url.startsWith("/documents") ? "DELETE" : "GET", url, headers });
    }
    assert.ok(lines.some((line) => line.includes("/docs/")), "the route pattern is logged");
    assert.ok(lines.every((line) => !line.includes(marker)), lines.join("\n"));
  } finally {
    await app.close();
  }
});

test("one unauthenticated request cannot write much to the log", async () => {
  const { app, lines } = await logging();
  try {
    await app.inject({ method: "GET", url: `/docs/${"a".repeat(15_000)}?q=${"b".repeat(15_000)}` });
    const written = lines.join("");
    assert.ok(written.length < 1_000, `${written.length} bytes`);
    assert.equal(lines.length, 1);
    assert.match(written, /"status":401/);
  } finally {
    await app.close();
  }
});

test("query strings are not logged", async () => {
  const { app, lines } = await logging();
  try {
    await app.inject({ method: "DELETE", url: "/documents?collection=c&path=private-name.md", headers: authHeaders });
    assert.ok(!lines.join("").includes("private-name"));
    assert.ok(lines.join("").includes('"route":"/documents"'));
  } finally {
    await app.close();
  }
});

test("a request id is adopted only if it looks like one", async () => {
  const { app, lines } = await logging();
  try {
    const reqIds = async (id: string) => {
      lines.length = 0;
      await app.inject({ method: "GET", url: "/health", headers: { "request-id": id } });
      return (JSON.parse(lines[0]!) as { reqId: string }).reqId;
    };
    const trace = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    assert.equal(await reqIds(trace), trace);
    assert.notEqual(await reqIds("forged line\nlevel=info"), "forged line\nlevel=info");
    const long = await reqIds("x".repeat(5_000));
    assert.ok(long.length <= 128);
  } finally {
    await app.close();
  }
  assert.equal(validRequestId(["a", "b"]), null);
  assert.equal(validRequestId(undefined), null);
});

test("logged paths are bounded, and an unknown route's error does not echo all of it", async () => {
  assert.equal(loggablePath("/a?b=c"), "/a");
  assert.equal(loggablePath(`/${"x".repeat(1000)}`).length, 203);
  const { app } = await makeApp();
  try {
    const reply = await app.inject({ method: "GET", url: `/${"z".repeat(5000)}`, headers: authHeaders });
    assert.equal(reply.statusCode, 404);
    assert.ok(reply.body.length < 500);
  } finally {
    await app.close();
  }
});

// --- the network boundary -----------------------------------------------------------------------------------

test("a hostile Host name cannot read or delete, with or without the secret", async () => {
  const { app, backend } = await makeApp();
  try {
    const evil = { ...authHeaders, host: "evil.example.com" };
    const read = await app.inject({ method: "GET", url: "/docs/anything", headers: evil });
    assert.equal(read.statusCode, 421);
    assert.equal(read.json().error, "host_not_allowed");
    assert.match(read.json().message, /MEMORIA_QMD_ALLOWED_HOSTS/);
    const before = backend.calls.length;
    assert.equal((await app.inject({ method: "DELETE", url: "/documents?collection=c&path=a.md", headers: evil })).statusCode, 421);
    assert.equal(backend.calls.length, before, "nothing reached the backend");
    assert.equal((await app.inject({ method: "GET", url: "/health", headers: { host: "evil.example.com" } })).statusCode, 421);
  } finally {
    await app.close();
  }
});

test("addresses, localhost and listed names are let through", async () => {
  const { app } = await makeApp({ config: { allowedHosts: new Set(["localhost", "memoria.lan"]) } });
  try {
    for (const host of ["192.168.1.20:8282", "127.0.0.1", "[::1]:8282", "localhost:8282", "LOCALHOST", "memoria.lan:8282", "MEMORIA.LAN"]) {
      assert.equal((await app.inject({ method: "GET", url: "/health", headers: { host } })).statusCode, 200, host);
    }
    assert.equal((await app.inject({ method: "GET", url: "/health", headers: { host: "other.lan" } })).statusCode, 421);
  } finally {
    await app.close();
  }
  assert.deepEqual([...loadConfig({ MEMORIA_QMD_ALLOWED_HOSTS: " Memoria.LAN , ,host.docker.internal" }).allowedHosts], ["localhost", "memoria.lan", "host.docker.internal"]);
  assert.equal(refuseAtBoundary({}, new Set())?.error, "host_not_allowed");
});

test("any Origin is refused: this service takes no requests from browsers", async () => {
  const { app } = await makeApp();
  try {
    for (const origin of ["https://evil.example.com", "http://localhost:8282", "null"]) {
      const reply = await app.inject({ method: "POST", url: "/search", headers: { ...authHeaders, origin }, payload: { query: "x" } });
      assert.equal(reply.statusCode, 403, origin);
      assert.equal(reply.json().error, "origin_forbidden");
    }
  } finally {
    await app.close();
  }
});

test("with no secret set, a run makes its own, so there is no open mode", async () => {
  const a = loadConfig({});
  const b = loadConfig({});
  assert.equal(a.secretGenerated, true);
  assert.ok(a.secret.length >= 32);
  assert.notEqual(a.secret, b.secret);
  assert.equal(loadConfig({ MEMORIA_QMD_SECRET: "Xk29vQmB7fLp3ZtRw" }).secretGenerated, false);
  const app = await buildApp({ backend: new FakeBackend({ collections: [], docs: [] }), config: { ...a, warmup: false }, logger: false });
  try {
    assert.equal((await app.inject({ method: "GET", url: "/status" })).statusCode, 401);
    assert.equal((await app.inject({ method: "GET", url: "/status", headers: { authorization: `Bearer ${a.secret}` } })).statusCode, 200);
  } finally {
    await app.close();
  }
});

test("weak or placeholder secrets are refused, without repeating them", () => {
  for (const secret of ["a-long-random-string-at-least-16-chars", "a".repeat(16), "abababababababab", "CHANGEME-CHANGEME-1234567890", "my-example-secret-value-123", "password-1234567890"]) {
    assert.throws(() => loadConfig({ MEMORIA_QMD_SECRET: secret }), (err: Error) => /too weak/.test(err.message) && !err.message.includes(secret), secret);
  }
  assert.throws(() => loadConfig({ MEMORIA_QMD_SECRET: "short" }), /at least 16/);
  assert.throws(() => loadConfig({ MEMORIA_QMD_LLM: "remote", MEMORIA_QMD_WORKER_URL: "http://127.0.0.1:1", MEMORIA_QMD_WORKER_TOKEN: "b".repeat(16) }), /MEMORIA_QMD_WORKER_TOKEN is too weak/);
  assert.equal(loadConfig({ MEMORIA_QMD_SECRET: "Xk29vQmB7fLp3ZtR" }).secret, "Xk29vQmB7fLp3ZtR");
});

test("plain http beyond loopback needs an explicit opt-in, with a message that says what to do", () => {
  const secret = { MEMORIA_QMD_SECRET: "Xk29vQmB7fLp3ZtRw" };
  const lan = { ...secret, MEMORIA_QMD_HOST: "192.168.1.20" };
  assert.throws(() => loadConfig(lan), /in clear.*MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK=1/);
  assert.equal(loadConfig({ ...lan, MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK: "1" }).host, "192.168.1.20");
  assert.throws(() => loadConfig({ ...lan, MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK: "0" }), /in clear/);
  assert.equal(loadConfig(secret).host, "127.0.0.1");

  const token = { MEMORIA_QMD_LLM: "remote", MEMORIA_QMD_WORKER_TOKEN: "worker-token-0123456789" };
  assert.throws(() => loadConfig({ ...token, MEMORIA_QMD_WORKER_URL: "http://192.168.1.30:8383" }), /MEMORIA_QMD_WORKER_URL is plain http.*MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK/);
  assert.equal(loadConfig({ ...token, MEMORIA_QMD_WORKER_URL: "https://192.168.1.30:8383" }).llm.mode, "remote");
  assert.equal(loadConfig({ ...token, MEMORIA_QMD_WORKER_URL: "http://192.168.1.30:8383", MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK: "1" }).llm.mode, "remote");
  assert.equal(loadConfig({ ...token, MEMORIA_QMD_WORKER_URL: "http://[::1]:8383" }).llm.mode, "remote");
  const list = JSON.stringify([{ name: "a", url: "http://10.0.0.2:8383", tokenEnv: "T" }]);
  assert.throws(() => loadConfig({ MEMORIA_QMD_LLM: "remote", MEMORIA_QMD_WORKERS: list, T: "worker-token-0123456789" }), /in clear/);
});

// --- input errors -----------------------------------------------------------------------------------------------

test("a line suffix past any real line number is part of the name, and a 404 rather than a 500", async () => {
  const huge = `x.md:${"9".repeat(400)}`;
  assert.deepEqual(parseLineRange(huge), { lookup: huge, fromLine: undefined, maxLines: undefined });
  assert.deepEqual(parseLineRange(`${huge}:5`), { lookup: huge, fromLine: 5, maxLines: undefined });
  assert.deepEqual(parseLineRange("x.md:10:20"), { lookup: "x.md", fromLine: 10, maxLines: 20 });
  const { app } = await makeApp();
  try {
    assert.equal((await app.inject({ method: "GET", url: `/docs/${huge}`, headers: authHeaders })).statusCode, 404);
    for (const param of ["fromLine", "maxLines"]) {
      const reply = await app.inject({ method: "GET", url: `/docs/2026-02-11_alpha.md?${param}=${"9".repeat(400)}`, headers: authHeaders });
      assert.ok(reply.statusCode < 500, `${param}: ${reply.statusCode}`);
    }
  } finally {
    await app.close();
  }
});

test("a bad date bound is refused without being echoed, however long", async () => {
  assert.throws(() => parseBound("not-a-date-SECRET", "since"), (err: Error) => !err.message.includes("SECRET"));
  assert.throws(() => parseBound("2026-99-99", "until"), (err: Error) => !err.message.includes("2026-99-99"));
  const { app } = await makeApp();
  try {
    const search = (since: string) => app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { query: "x", filters: { since } } });
    const long = await search("Z".repeat(1024 * 1024));
    assert.equal(long.statusCode, 400);
    assert.ok(long.body.length < 500, `${long.body.length} bytes`);
    const short = await search("not-a-date-SECRET");
    assert.equal(short.statusCode, 400);
    assert.ok(!short.body.includes("SECRET"));
  } finally {
    await app.close();
  }
});

test("a search of only quotes is a 400", async () => {
  const { app } = await makeApp();
  try {
    const search = (payload: object) => app.inject({ method: "POST", url: "/search", headers: authHeaders, payload });
    assert.equal((await search({ searches: [{ type: "lex", query: '""' }] })).statusCode, 400);
    assert.equal((await search({ query: '"""' })).statusCode, 400);
    assert.equal((await search({ searches: [{ type: "lex", query: "!!!" }] })).statusCode, 400);
    assert.equal((await search({ searches: [{ type: "lex", query: '"日本語"' }] })).statusCode, 200);
    assert.equal((await search({ searches: [{ type: "vec", query: "???" }] })).statusCode, 200, "a vector search has no such syntax");
  } finally {
    await app.close();
  }
});

// --- an unpatched SDK --------------------------------------------------------------------------------

const pristine = (): SdkStoreFunctions => ({
  isInsideCodeFence: originalInside,
  findBestCutoff: originalCutoff as never,
  scanBreakPoints: () => [],
  searchVec: (() => `content.doc as body
    FROM content_vectors cv`) as never,
  hybridQuery: (() => {}) as never,
  structuredSearch: (() => {}) as never,
  syncConfigToDb: originalSync,
});

/** The SDK's config sync before the patch, word for word in what it does to the index. */
function originalSync(db: unknown, config: { collections: Record<string, unknown>; global_context?: string }): void {
  const index = db as { prepare(sql: string): { get(...a: unknown[]): unknown; all(): Array<{ name: string }>; run(...a: unknown[]): unknown } };
  if (index.prepare(`SELECT value FROM store_config WHERE key = 'config_hash'`).get() !== undefined) return;
  const configNames = new Set(Object.keys(config.collections));
  for (const row of index.prepare(`SELECT name FROM store_collections`).all()) {
    if (!configNames.has(row.name)) index.prepare(`DELETE FROM store_collections WHERE name = ?`).run(row.name);
  }
  if (config.global_context === undefined) index.prepare(`DELETE FROM store_config WHERE key = 'global_context'`).run();
}

test("the start-up check names each fix an unpatched SDK lacks, with no marker and no clock", () => {
  assert.equal(missingSdkFixes(pristine()).length, 10);
  assert.throws(() => requireSdkFixes(pristine()), /not patched.*findBestCutoff.*scripts\/patch-qmd\.ts/);
  const real = sdk as unknown as SdkStoreFunctions;
  assert.doesNotThrow(() => requireSdkFixes(real));
  for (const key of Object.keys(pristine()) as Array<keyof SdkStoreFunctions>) {
    // These two carry the yield and the rerank blend, and the sync keeps both records.
    const fixes = key === "hybridQuery" || key === "structuredSearch" || key === "syncConfigToDb" ? 2 : 1;
    assert.equal(missingSdkFixes({ ...real, [key]: pristine()[key] }).length, fixes, key);
  }
  const throwing = { ...real, syncConfigToDb: () => {
    throw new Error("moved");
  } };
  assert.deepEqual(missingSdkFixes(throwing).filter((m) => m.startsWith("syncConfigToDb")).length, 2, "a sync it cannot run counts as unpatched");
});

test("a collection's documents with no record stop the sidecar, naming the collection", async () => {
  const { requireCollectionRecords } = await import("../src/store.ts");
  const db = (rows: Array<{ collection: string }>) => ({ prepare: (sql: string) => ({ all: () => (/NOT IN \(SELECT name FROM store_collections\)/.test(sql) && /active = 1/.test(sql) ? rows : []) }) });
  assert.doesNotThrow(() => requireCollectionRecords(db([])));
  assert.throws(() => requireCollectionRecords(db([{ collection: "claude-sessions" }, { collection: "codex-sessions" }])), /no record of: claude-sessions, codex-sessions\. .*Nothing has been changed.*Lost collection records.*docs\/deployment\.md/);
});

test("a sidecar on an unpatched SDK refuses to start", { timeout: 60_000 }, () => {
  const fixture = fileURLToPath(new URL("./fixtures/unpatched-sdk.ts", import.meta.url));
  const run = spawnSync(process.execPath, [fixture], { encoding: "utf8", timeout: 50_000 });
  assert.equal(run.status, 0, run.stderr);
  assert.match(run.stdout, /refused: .*not patched.*isInsideCodeFence/);
});

test("/ready says the SDK is patched", async () => {
  const { app } = await makeApp();
  try {
    assert.deepEqual((await app.inject({ method: "GET", url: "/ready", headers: authHeaders })).json().sdk, { patched: true });
  } finally {
    await app.close();
  }
});

// --- a stored file too large for the chunker's table -------------------------------------------------

test("17 MiB of blank lines chunks without overflowing, and within the cap nothing changed", { timeout: 60_000 }, () => {
  assert.ok(chunkInChild(`"\\n".repeat(17 * 1024 * 1024)`, 40_000).chunks > 1000);
  const twoMiB = "\n".repeat(2 * 1024 * 1024);
  const chunks = sdk.chunkDocument(twoMiB);
  // Every cut at the plain size minus the overlap would mean break points had been dropped.
  assert.ok(chunks.length > 100);
  assert.ok(chunks.every((c) => c.text.length <= 3600));
});

// --- connections per source --------------------------------------------------------------------------

function from(port: number, localAddress: string, send = "GET /health HTTP/1.1\r\nHost: localhost\r\nX-Slow: "): Promise<Socket> {
  return new Promise((resolve) => {
    const socket = connect({ port, host: "127.0.0.1", localAddress }, () => {
      socket.write(send);
      resolve(socket);
    });
    socket.on("error", () => {});
    socket.on("data", () => {});
  });
}

test("one source cannot hold the connections: past its share it is dropped, and others still get in", { timeout: 30_000 }, async () => {
  const { app, port } = await listening({ maxConnections: 50, maxConnectionsPerSource: 8 });
  const sockets: Socket[] = [];
  try {
    for (let i = 0; i < 100; i++) sockets.push(await from(port, "127.0.0.1"));
    const closed = await Promise.all(sockets.map((s) => closedOrNot(s, 2_000)));
    assert.equal(closed.filter((c) => !c).length, 8, "it keeps its share and no more");
    // 100 tries would have used up the total of 50. Another address is untouched.
    const other = await from(port, "127.0.0.2", "GET /health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
    sockets.push(other);
    const reply = await new Promise<string>((resolve) => {
      let text = "";
      other.on("data", (chunk) => (text += String(chunk)));
      other.on("close", () => resolve(text));
    });
    assert.match(reply, /^HTTP\/1\.1 200 /);
    // A dropped share comes back once its connections have gone.
    for (const s of sockets) s.destroy();
    await new Promise((resolve) => setTimeout(resolve, 100));
    const again = await from(port, "127.0.0.1");
    sockets.push(again);
    assert.equal(await closedOrNot(again, 500), false);
  } finally {
    for (const s of sockets) s.destroy();
    await app.close();
  }
});

test("Memoria.Api's address gets more, and the settings have defaults", { timeout: 30_000 }, async () => {
  const { app, port } = await listening({ maxConnectionsPerSource: 2, apiAddresses: new Set(["127.0.0.1"]), maxConnectionsPerApiAddress: 6 });
  const sockets: Socket[] = [];
  try {
    for (let i = 0; i < 9; i++) sockets.push(await from(port, "127.0.0.1"));
    const closed = await Promise.all(sockets.map((s) => closedOrNot(s, 1_000)));
    assert.equal(closed.filter((c) => !c).length, 6);
  } finally {
    for (const s of sockets) s.destroy();
    await app.close();
  }
  const config = testConfig();
  assert.equal(config.maxConnectionsPerSource, 64);
  assert.equal(config.maxConnectionsPerApiAddress, 512);
  assert.deepEqual([...loadConfig({ MEMORIA_QMD_API_ADDRESSES: "10.0.0.5, 10.0.0.6" }).apiAddresses], ["10.0.0.5", "10.0.0.6"]);
});

test("keep-alive stays at Fastify's 72 s, so a pooled client does not meet a closed socket", async () => {
  const { app } = await listening({});
  try {
    assert.equal(app.server.keepAliveTimeout, 72_000);
  } finally {
    await app.close();
  }
});

// --- concurrent searches -------------------------------------------------------------------------------

test("the gate holds to its limit, queues in order, and turns the rest away", async () => {
  const gate = new SearchGate({ running: 2, queued: 1, maxWaitMs: 60_000 });
  const releases: Array<() => void> = [];
  let running = 0;
  let most = 0;
  const order: number[] = [];
  const search = (id: number) =>
    gate.run(async () => {
      order.push(id);
      most = Math.max(most, ++running);
      await new Promise<void>((resolve) => releases.push(resolve));
      running--;
      return id;
    });
  const a = search(1);
  const b = search(2);
  const c = search(3);
  await assert.rejects(search(4), SearchBusy);
  await new Promise((r) => setImmediate(r));
  assert.deepEqual(order, [1, 2]);
  releases.shift()!();
  assert.equal(await a, 1);
  await new Promise((r) => setImmediate(r));
  assert.deepEqual(order, [1, 2, 3]);
  releases.shift()!();
  releases.shift()!();
  await Promise.all([b, c]);
  assert.equal(most, 2);
  assert.equal(await gate.run(async () => 5), 5, "the gate is open again");
});

test("a search that fails gives up its turn, and one that waits too long is turned away", async () => {
  const gate = new SearchGate({ running: 1, queued: 4, maxWaitMs: 20 });
  await assert.rejects(gate.run(() => Promise.reject(new Error("boom"))), /boom/);
  assert.equal(await gate.run(async () => "free"), "free");
  let release = (): void => {};
  const held = gate.run(() => new Promise<void>((resolve) => (release = resolve)));
  await assert.rejects(gate.run(async () => {}), SearchBusy);
  release();
  await held;
  assert.equal(await gate.run(async () => "free again"), "free again", "the waiter that left took no slot");
});

test("past the queue, /search answers 503 with Retry-After, and other routes still answer", async () => {
  const { app, backend } = await makeApp({ config: { searchConcurrency: 1, searchQueue: 0 } });
  let release = (): void => {};
  const original = backend.searchDetailed.bind(backend);
  backend.searchDetailed = async (o) => {
    await new Promise<void>((resolve) => (release = resolve));
    return original(o);
  };
  try {
    const search = () => app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { query: "api keys" } });
    const first = search();
    await new Promise((r) => setTimeout(r, 20));
    const busy = await search();
    assert.equal(busy.statusCode, 503);
    assert.equal(busy.json().error, "search_busy");
    assert.equal(busy.headers["retry-after"], "2");
    assert.equal((await app.inject({ method: "GET", url: "/health" })).statusCode, 200);
    release();
    assert.equal((await first).statusCode, 200);
  } finally {
    await app.close();
  }
  const config = testConfig();
  assert.deepEqual([config.searchConcurrency, config.searchQueue, config.searchMaxWaitMs], [4, 16, 10_000]);
});

test("a search error that is not about the query is still a 500", async () => {
  const { app, backend } = await makeApp();
  backend.searchDetailed = async () => {
    throw new Error("disk full");
  };
  try {
    assert.equal((await app.inject({ method: "POST", url: "/search", headers: authHeaders, payload: { query: "x" } })).statusCode, 500);
  } finally {
    await app.close();
  }
});

// --- weak secrets ------------------------------------------------------------------------------------------

test("obvious sequences are refused, and random secrets are not", () => {
  for (const secret of ["1234567890123456", "qwertyuiopasdfgh", "abcdefghabcdefgh", "0123456789abcdef", "ponmlkjihgfedcba"]) {
    assert.throws(() => loadConfig({ MEMORIA_QMD_SECRET: secret }), /too weak/, secret);
  }
  for (let i = 0; i < 20_000; i++) {
    assert.doesNotThrow(() => loadConfig({ MEMORIA_QMD_SECRET: randomBytes(12).toString("base64url") }));
  }
  assert.doesNotThrow(() => loadConfig({ MEMORIA_QMD_SECRET: "test-secret-0123456789abcdef" }), "some sequence in a longer secret is fine");
});
