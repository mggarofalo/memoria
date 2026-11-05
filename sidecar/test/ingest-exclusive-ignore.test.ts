import assert from "node:assert/strict";
import fsPromises, { mkdir, readdir, readFile, utimes, writeFile } from "node:fs/promises";
import { syncBuiltinESMExports } from "node:module";
import net from "node:net";
import path from "node:path";
import { test } from "node:test";
import fastGlob from "fast-glob";
import { HttpError } from "../src/errors.ts";
import { resolveTarget, writeDocument } from "../src/ingest.ts";
import { matchesIgnorePattern, unsupportedIgnorePattern } from "../src/path-rules.ts";
import { authHeaders, makeApp, tempDir } from "./helpers.ts";

const isCode = (code: string) => (err: unknown) => err instanceof HttpError && err.code === code;

// --- a write tied to its connection --------------------------------------------

test("a write whose caller has gone is dropped at the last moment, and an old file stays", async () => {
  const root = await tempDir();
  const target = resolveTarget(root, "**/*.md", "doc.md");
  await writeDocument(target, "old");
  await assert.rejects(writeDocument(target, "new", { stillConnected: () => false }), isCode("client_gone"));
  assert.equal(await readFile(target.absolute, "utf8"), "old");
  assert.deepEqual(await readdir(root), ["doc.md"], "no temp file is left behind");
});

test("the check runs after a turn of the event loop, so a close already delivered is seen", async () => {
  const root = await tempDir();
  const target = resolveTarget(root, "**/*.md", "doc.md");
  let connected = true;
  await assert.rejects(
    writeDocument(target, "new", {
      stillConnected: () => connected,
      onStep: async (step) => {
        if (step === "checked") setImmediate(() => (connected = false));
      },
    }),
    isCode("client_gone"),
  );
  assert.deepEqual(await readdir(root), []);
});

test("a caller still connected gets its write, replacing what is there", async () => {
  const root = await tempDir();
  const target = resolveTarget(root, "**/*.md", "doc.md");
  assert.deepEqual(await writeDocument(target, "one", { stillConnected: () => true }), { bytes: 3, created: true });
  assert.deepEqual(await writeDocument(target, "two", { stillConnected: () => true }), { bytes: 3, created: false });
  assert.equal(await readFile(target.absolute, "utf8"), "two");
});

/** Holds a write just before its last check, by holding the real-path lookup of its temp file, so a real socket can close during it. */
function stallWrites(ms: number, onStall: () => void): () => void {
  const patchable = fsPromises as unknown as { realpath: (...args: unknown[]) => Promise<string> };
  const real = patchable.realpath;
  patchable.realpath = async (...args: unknown[]) => {
    if (String(args[0]).includes(".memoria-")) {
      onStall();
      await new Promise((resolve) => setTimeout(resolve, ms));
    }
    return real(...args);
  };
  syncBuiltinESMExports();
  return () => {
    patchable.realpath = real;
    syncBuiltinESMExports();
  };
}

/** What a directory holds once no write in it is still running, however long the stalls took. */
async function settled(dir: string): Promise<string[]> {
  for (let attempt = 0; attempt < 100 && (await readdir(dir)).some((name) => name.startsWith(".memoria-")); attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  await new Promise((resolve) => setTimeout(resolve, 100));
  return readdir(dir);
}

function send(port: number, name: string): { socket: net.Socket; answer: Promise<string> } {
  const body = JSON.stringify({ collection: "claude-sessions", path: name, content: "# from a socket\n", embed: false });
  const socket = net.connect(port, "127.0.0.1");
  socket.write(`POST /documents HTTP/1.1\r\nHost: 127.0.0.1\r\nAuthorization: ${authHeaders.authorization}\r\nContent-Type: application/json\r\nContent-Length: ${Buffer.byteLength(body)}\r\nConnection: close\r\n\r\n${body}`);
  const answer = new Promise<string>((resolve) => {
    let text = "";
    socket.on("data", (chunk) => (text += chunk.toString("utf8")));
    socket.on("close", () => resolve(text));
    socket.on("error", () => undefined);
  });
  return { socket, answer };
}

for (const [how, close] of [
  ["a FIN", (socket: net.Socket) => socket.end()],
  ["a destroyed socket", (socket: net.Socket) => socket.destroy()],
  ["an RST", (socket: net.Socket) => socket.resetAndDestroy()],
] as const) {
  test(`through a real socket: ${how} during a stalled write leaves no document`, async () => {
    const { app, roots } = await makeApp();
    const name = "gone.md";
    let closing: net.Socket | undefined;
    const restore = stallWrites(200, () => {
      if (closing) close(closing);
      closing = undefined;
    });
    try {
      await app.listen({ host: "127.0.0.1", port: 0 });
      const { socket, answer } = send((app.server.address() as net.AddressInfo).port, name);
      closing = socket;
      await answer;
      assert.deepEqual(await settled(roots.claude), [], "neither the document nor a temp file");
    } finally {
      restore();
      await app.close();
    }
  });
}

test("through a real socket: an RST right after the body never stores anything", async () => {
  const { app, roots } = await makeApp();
  const restore = stallWrites(300, () => undefined);
  try {
    await app.listen({ host: "127.0.0.1", port: 0 });
    const port = (app.server.address() as net.AddressInfo).port;
    for (let round = 0; round < 25; round++) {
      const { socket, answer } = send(port, `rst-${round}.md`);
      socket.once("connect", () => setTimeout(() => socket.resetAndDestroy(), round % 3));
      await answer;
    }
    assert.deepEqual(await settled(roots.claude), [], "neither a document nor a temp file");
  } finally {
    restore();
    await app.close();
  }
});

test("through a real socket: a caller that stays connected through a stall gets its document", async () => {
  const { app, roots } = await makeApp();
  const restore = stallWrites(100, () => undefined);
  try {
    await app.listen({ host: "127.0.0.1", port: 0 });
    const { answer } = send((app.server.address() as net.AddressInfo).port, "kept.md");
    assert.match(await answer, /^HTTP\/1\.1 202/);
    assert.deepEqual(await readdir(roots.claude), ["kept.md"]);
  } finally {
    restore();
    await app.close();
  }
});

test("a collection's emptiness: temp files do not count, and anything else does", async () => {
  const { app, roots } = await makeApp();
  try {
    const ask = async () => (await app.inject({ method: "GET", url: "/collections/claude-sessions/empty", headers: authHeaders })).json();
    assert.deepEqual(await ask(), { collection: "claude-sessions", empty: true });
    await writeFile(path.join(roots.claude, `.memoria-${"a".repeat(24)}.tmp`), "left by a crash");
    assert.equal((await ask()).empty, true);
    await writeFile(path.join(roots.claude, "foreign.md"), "someone else's");
    assert.equal((await ask()).empty, false);
    assert.equal((await app.inject({ method: "GET", url: "/collections/nope/empty", headers: authHeaders })).statusCode, 404);
  } finally {
    await app.close();
  }
});

// --- stale temp files -------------------------------------------------------------

test("a write sweeps old temp files from its directory, leaving recent ones and other names", async () => {
  const root = await tempDir();
  const old = new Date(Date.now() - 2 * 60 * 60 * 1000);
  const staleName = `.memoria-${"a".repeat(24)}.tmp`;
  const liveName = `.memoria-${"b".repeat(24)}.tmp`;
  const otherName = ".memoria-notes.tmp";
  for (const name of [staleName, liveName, otherName]) await writeFile(path.join(root, name), "x");
  await utimes(path.join(root, staleName), old, old);
  await utimes(path.join(root, otherName), old, old);
  await writeDocument(resolveTarget(root, "**/*.md", "doc.md"), "body");
  assert.deepEqual((await readdir(root)).sort(), [liveName, otherName, "doc.md"].sort());
});

test("a crash between link and unlink stops blocking an overwrite once the temp file is stale", async () => {
  const root = await tempDir();
  const { link } = await import("node:fs/promises");
  const target = resolveTarget(root, "**/*.md", "doc.md");
  await writeDocument(target, "one");
  const leftover = path.join(root, `.memoria-${"c".repeat(24)}.tmp`);
  await link(target.absolute, leftover);
  await assert.rejects(writeDocument(target, "two"), isCode("hard_link"));
  const old = new Date(Date.now() - 2 * 60 * 60 * 1000);
  await utimes(leftover, old, old);
  await writeDocument(target, "two");
  assert.equal(await readFile(target.absolute, "utf8"), "two");
  assert.deepEqual(await readdir(root), ["doc.md"]);
});

// --- ignore patterns ---------------------------------------------------------------

test("ignored paths return 400 from POST, write nothing, and queue nothing", async () => {
  const { app, backend, roots } = await makeApp();
  backend.collections[0]!.ignore = ["drafts", "**/private/**", "**/*.secret.md"];
  try {
    for (const p of ["drafts/x.md", "drafts/deep/x.md", "a/private/x.md", "private/x.md", "note.secret.md", "a/note.secret.md"]) {
      const created = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: p, content: "# x\n" } });
      assert.equal(created.statusCode, 400, p);
      assert.equal(created.json().error, "ignored_path", p);
    }
    assert.deepEqual(await readdir(roots.claude), []);
    assert.deepEqual(app.ctx.jobs.list(), []);

    const fine = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "published/x.md", content: "# x\n" } });
    assert.equal(fine.statusCode, 202, fine.body);
    // Each collection has its own list.
    const other = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "codex-sessions", path: "drafts/x.md", content: "# x\n" } });
    assert.equal(other.statusCode, 202, other.body);
  } finally {
    await app.close();
  }
});

test("matching agrees with fast-glob, which is what QMD's scan runs", async () => {
  const root = await tempDir();
  const files = ["top.md", "drafts/a.md", "drafts/deep/b.md", "draftsish/c.md", "x/private/d.md", "private/e.md", "note.secret.md", "x/note.secret.md", "a/b/f.md", "a/g.md", "c/h.md", "p/i.md", "q/j.md", "r/k.md", "1/q.md", "22/q.md", "Mixed/l.md"];
  for (const f of files) {
    await mkdir(path.dirname(path.join(root, f)), { recursive: true });
    await writeFile(path.join(root, f), "x");
  }
  const rules = ["drafts", "drafts/", "drafts/**", "**/drafts/**", "**/private/**", "private", "*.secret.md", "**/*.secret.md", "a/b/*.md", "a/**", "a/*", "./c/*.md", "{p,q}/*.md", "[pq]/*.md", "?/q.md", "*/q.md", "mixed/**", "Mixed/**", "**/*.md", "nomatch", "c\\h.md", "/drafts/**", "/top.md", "./drafts"];
  for (const rule of rules) {
    // The same options as reindexCollection in the SDK's store.js.
    const kept = new Set(await fastGlob("**/*.md", { cwd: root, onlyFiles: true, followSymbolicLinks: false, dot: false, ignore: [rule] }));
    for (const f of files) {
      assert.equal(matchesIgnorePattern(f.split("/"), [rule]), !kept.has(f), `rule ${rule} on ${f}: fast-glob ${kept.has(f) ? "keeps" : "skips"} it`);
    }
  }
});

test("patterns that could backtrack are refused, loudly, and evaluated never", async () => {
  for (const bad of ["+(a|aa)", "**/+(a|aa)*.md", "@(a|b)/x.md", "*(a)", "?(a)", "!(a)", "!drafts", "a*a*a*a*b", "**/*/*/*/*.md"]) {
    assert.equal(unsupportedIgnorePattern(["fine/**", bad]), bad, bad);
    assert.throws(
      () => resolveTarget(path.resolve("root"), "**/*.md", "x.md", ["fine/**", bad]),
      (err: unknown) => isCode("unsupported_ignore_pattern")(err) && (err as HttpError).statusCode === 500,
      bad,
    );
  }
  for (const ok of ["drafts", "**/private/**", "**/*.secret.md", "a/*/b/*.md", "{p,q}/*.md", "[pq]/*.md", "?/q.md", "a*b*c*.md"]) {
    assert.equal(unsupportedIgnorePattern([ok]), undefined, ok);
  }
});

test("the worst pattern allowed stays quick on the longest path a request can send", () => {
  // What the schema's 1024-character cap and the segment limit leave an attacker: deep with short names, or wide.
  const longSegment = `${"a".repeat(250)}.md`;
  const deep = Array.from({ length: 32 }, () => "a");
  const deepAndLong = Array.from({ length: 32 }, () => "a".repeat(30));
  const cases: Array<[string, string[]]> = [
    ["a*a*a*b", [longSegment]],
    ["**/a*a*.nope", [longSegment]],
    ["*a*a*a", ["a".repeat(255)]],
    ["**/a/**/a/**/b", deep],
    ["a/**/a/**/a/**/b", deep],
    ["**/a*/**/b", deep],
    ["**/a*/**/b", deepAndLong],
    ["**/a*a/**/b", deepAndLong],
    ["**/a/**/a/**/b", deepAndLong],
    ["**/*a*.nope", deepAndLong],
    ["**/a*/*.nope", deepAndLong],
    ["**/a/**/a/**", deep],
  ];
  for (const [rule, segments] of cases) {
    assert.equal(unsupportedIgnorePattern([rule]), undefined, `${rule} is meant to be allowed`);
    const started = performance.now();
    matchesIgnorePattern(segments, [rule]);
    const elapsed = performance.now() - started;
    // Generous for a loaded CI host. Before the depth limit, the third wildcard here took 20 seconds.
    assert.ok(elapsed < 1000, `${rule} took ${elapsed.toFixed(0)} ms`);
  }
});

test("a path deeper than the limit is refused before any pattern sees it", () => {
  const root = path.resolve("root");
  const deep = (n: number) => `${Array.from({ length: n - 1 }, () => "d").join("/")}/x.md`;
  assert.ok(resolveTarget(root, "**/*.md", deep(32)));
  assert.throws(() => resolveTarget(root, "**/*.md", deep(33)), isCode("invalid_path"));
});

test("patterns are only ever read from the backend, never from a request", async () => {
  const { app, backend } = await makeApp();
  backend.collections[0]!.ignore = [];
  try {
    const r = await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: "x.md", content: "# x\n", ignore: ["**"] } });
    assert.equal(r.statusCode, 400, "the schema has no such property");
  } finally {
    await app.close();
  }
});
