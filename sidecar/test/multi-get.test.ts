import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { Worker } from "node:worker_threads";
import picomatch from "picomatch";
import { HttpError } from "../src/errors.ts";
import { globViolation, MAX_BYTES_CEILING, matchGlob, parseSelectors, resolveReference } from "../src/multi-get.ts";
import type { DocumentRef } from "../src/store.ts";
import { authHeaders, makeApp, type TestApp } from "./helpers.ts";

const cases = JSON.parse(await readFile(new URL("./glob-policy-cases.json", import.meta.url), "utf8")) as { accepted: string[]; refused: string[] };

const HANG_LIMIT = { timeout: 30_000 };

async function withApp(run: (app: TestApp["app"], backend: TestApp["backend"]) => Promise<void>): Promise<void> {
  const { app, backend } = await makeApp();
  try {
    await run(app, backend);
  } finally {
    await app.close();
  }
}

test("the grammar accepts realistic globs and refuses everything else", () => {
  for (const glob of cases.accepted) assert.equal(globViolation(glob), null, glob);
  for (const glob of cases.refused) {
    assert.throws(() => parseSelectors(glob, undefined), HttpError, glob);
    assert.throws(() => parseSelectors(undefined, [glob]), HttpError, `ids: ${glob}`);
  }
  assert.notEqual(globViolation(`a*${"b".repeat(256)}`), null);
});

test("parseSelectors caps globs and references per request", () => {
  const globs = Array.from({ length: 11 }, (_, i) => `c${i}/*.md`);
  assert.throws(() => parseSelectors(globs.join(","), undefined), /At most 10 globs/);
  assert.throws(() => parseSelectors(undefined, globs), /At most 10 globs/);
  assert.throws(() => parseSelectors(Array.from({ length: 201 }, (_, i) => `r${i}.md`).join(","), undefined), /At most 200 references/);
  assert.throws(() => parseSelectors(undefined, ["x".repeat(1025)]), /at most 1024/);
  assert.throws(() => parseSelectors(undefined, ["a.md,b.md"]), /comma/);
});

test("matchGlob agrees with picomatch on the grammar it accepts", () => {
  const paths = [
    "claude-sessions/2026-10-01_a.md",
    "claude-sessions/sub/deep/2026-10-01_a.md",
    "claude-sessions/.hidden.md",
    "claude-sessions/.dot/a.md",
    "2026-02-11_x.md",
    "a.md",
    "qmd://claude-sessions/2026-10-01_a.md",
    "codex-sessions/2026-10-01_a.txt",
    "Claude-Sessions/2026-10-01_a.md",
  ];
  const globs = [...cases.accepted, "claude-sessions/*", "claude-sessions/**", "**", "*", "?.md", "*/*/*", "a.md", "claude-sessions/*/deep/*.md", "**/deep/**/*.md"];
  for (const glob of globs) {
    const isMatch = picomatch(glob);
    for (const path of paths) assert.equal(matchGlob(glob, path), isMatch(path), `${glob} vs ${path}`);
  }
});

test("the worst glob the grammar allows finishes quickly on hostile paths", HANG_LIMIT, async () => {
  const name = `${"a".repeat(240)}.md`;
  const paths = Array.from({ length: 5000 }, (_, i) => `collection/${"a/".repeat(20)}${i}-${name}`);
  const globs = ["**/*a*a*a*a*a*a*a*a*a*a*Z", "**/a/**/*a*a*a*a*a*a*a*a*Z", "*a*a*a*a*a*a*a*a*a*a*a*Z", `${"?".repeat(12)}Z`];
  const worker = new Worker(new URL("./glob-bound-worker.ts", import.meta.url), { workerData: { globs, paths } });
  try {
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("the glob worst case did not finish in 20 s")), 20_000);
      worker.once("message", () => {
        clearTimeout(timer);
        resolve();
      });
      worker.once("error", (err) => {
        clearTimeout(timer);
        reject(err);
      });
    });
  } finally {
    await worker.terminate();
  }
});

test("POST /docs:multiGet refuses hostile globs in pattern and ids before touching the backend", HANG_LIMIT, async () => {
  await withApp(async (app, backend) => {
    for (const glob of cases.refused) {
      for (const payload of [{ pattern: glob }, { ids: [glob] }, { pattern: `claude-sessions/*.md,${glob}` }]) {
        const response = await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload });
        assert.equal(response.statusCode, 400, `${JSON.stringify(payload)}: ${response.body}`);
        assert.equal(response.json().error, "validation");
      }
    }
    assert.deepEqual(backend.calls.filter((c) => c.method === "multiGet" || c.method === "listDocumentRefs"), []);
  });
});

test("globs never reach the SDK: it only receives the exact URIs that matched", async () => {
  await withApp(async (app, backend) => {
    const response = await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { pattern: "claude-sessions/2026-03*.md" } });
    assert.equal(response.statusCode, 200);
    assert.deepEqual(backend.calls.filter((c) => c.method === "multiGet").map((c) => c.args[0]), ["qmd://claude-sessions/2026-03-05_beta.md,"]);
  });
});

test("POST /docs:multiGet refuses maxBytes above the ceiling", async () => {
  await withApp(async (app) => {
    const over = await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { pattern: "*/*.md", maxBytes: MAX_BYTES_CEILING + 1 } });
    assert.equal(over.statusCode, 400);
    const at = await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { pattern: "*/*.md", maxBytes: MAX_BYTES_CEILING } });
    assert.equal(at.statusCode, 200);
  });
});

test("GET /docs/* refuses an oversized reference before the similar-files scan", async () => {
  await withApp(async (app, backend) => {
    const response = await app.inject({ method: "GET", url: `/docs/${"x".repeat(1025)}`, headers: authHeaders });
    assert.equal(response.statusCode, 400);
    assert.deepEqual(backend.calls.filter((c) => c.method === "get"), []);
  });
});

function ref(collection: string, path: string, hash = "ffffffffffff"): DocumentRef {
  return { collection, path, hash };
}

test("resolveReference finds docids, URIs, paths and suffixes among the rows it is given", () => {
  const rows = [ref("alpha", "sub/one.md", "aaaaaa111111"), ref("alpha", "two.md", "bbbbbb222222"), ref("Mixed", "three.md", "cccccc333333")];
  const paths = (reference: string): string[] => resolveReference(rows, reference).map((r) => `${r.collection}/${r.path}`);

  assert.deepEqual(paths("aaaaaa"), ["alpha/sub/one.md"]);
  assert.deepEqual(paths("#BBBBBB"), ["alpha/two.md"]);
  assert.deepEqual(paths("qmd://alpha/two.md"), ["alpha/two.md"]);
  assert.deepEqual(paths("alpha/two.md"), ["alpha/two.md"]);
  assert.deepEqual(paths("ALPHA/TWO.md"), ["alpha/two.md"]);
  assert.deepEqual(paths("two.md"), ["alpha/two.md"]);
  assert.deepEqual(paths("one.md"), ["alpha/sub/one.md"]);
  assert.deepEqual(paths("mixed/three.md"), ["Mixed/three.md"]);
  assert.deepEqual(paths("beta/secret.md"), []);
  assert.deepEqual(paths("dddddd"), []);
  assert.deepEqual(paths("wo.md"), [], "a mid-name fragment does not match");
});

test("GET /docs/* with collections resolves only inside them and never asks the SDK about a miss", async () => {
  await withApp(async (app, backend) => {
    const hit = await app.inject({ method: "GET", url: "/docs/claude-sessions/2026-02-11_alpha.md?collections=claude-sessions", headers: authHeaders });
    assert.equal(hit.statusCode, 200, hit.body);
    assert.equal(hit.json().collection, "claude-sessions");

    backend.calls.length = 0;
    const outOfScope = await app.inject({ method: "GET", url: "/docs/codex-sessions/2026-08-31_gamma.md?collections=claude-sessions", headers: authHeaders });
    const missing = await app.inject({ method: "GET", url: "/docs/codex-sessions/no-such-file.md?collections=claude-sessions", headers: authHeaders });

    assert.equal(outOfScope.statusCode, 404);
    assert.equal(missing.statusCode, 404);
    assert.deepEqual(outOfScope.json().details, { similarFiles: [] });
    assert.deepEqual(missing.json().details, { similarFiles: [] });
    for (const call of backend.calls) {
      assert.ok(call.method === "listDocumentRefs", `an out-of-scope lookup reached ${call.method}`);
      assert.deepEqual(call.args[0], ["claude-sessions"]);
    }
  });
});

test("GET /docs/* with collections finds a docid only inside them", async () => {
  await withApp(async (app) => {
    const doc = (await app.inject({ method: "GET", url: "/docs/codex-sessions/2026-08-31_gamma.md", headers: authHeaders })).json();
    assert.equal((await app.inject({ method: "GET", url: `/docs/${doc.docid}?collections=claude-sessions`, headers: authHeaders })).statusCode, 404);
    assert.equal((await app.inject({ method: "GET", url: `/docs/${doc.docid}?collections=codex-sessions`, headers: authHeaders })).statusCode, 200);
  });
});

test("POST /docs:multiGet with collections sees only them, in globs, ids and errors", async () => {
  await withApp(async (app, backend) => {
    const multi = (payload: object) => app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { collections: ["claude-sessions"], ...payload } });

    const glob = (await multi({ pattern: "*/*.md", maxBytes: 1 })).json();
    assert.ok(glob.skipped.length > 0);
    assert.ok(glob.skipped.every((s: { file: string }) => s.file.startsWith("claude-sessions/")), JSON.stringify(glob.skipped));

    const reads = backend.calls.filter((c) => c.method === "multiGet").flatMap((c) => String(c.args[0]).split(",").filter(Boolean));
    assert.ok(reads.length > 0);
    assert.ok(reads.every((uri) => uri.startsWith("qmd://claude-sessions/")), `read outside the allowlist: ${reads.join()}`);

    const ids = (await multi({ ids: ["codex-sessions/2026-08-31_gamma.md", "claude-sessions/2026-02-11_alpha.md"] })).json();
    assert.deepEqual(ids.docs.map((d: { path: string }) => d.path), ["2026-02-11_alpha.md"]);
    assert.deepEqual(ids.errors, ["File not found: codex-sessions/2026-08-31_gamma.md"]);
  });
});

test("an empty collections list means nothing, never everything", async () => {
  await withApp(async (app, backend) => {
    backend.calls.length = 0;
    const multi = (await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { pattern: "*/*.md", collections: [] } })).json();
    assert.deepEqual(multi.docs, []);
    assert.deepEqual(multi.skipped, []);
    assert.deepEqual(backend.calls.filter((c) => c.method === "multiGet"), []);
  });
});
