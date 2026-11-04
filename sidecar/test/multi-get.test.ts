import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { Worker } from "node:worker_threads";
import picomatch from "picomatch";
import { HttpError } from "../src/errors.ts";
import { globViolation, MAX_BYTES_CEILING, MAX_GLOB_DOCUMENTS, MAX_SCANNING_REFERENCES, MAX_TOTAL_BYTES_CEILING, matchGlob, parseSelectors, ReferenceIndex, resolveMultiGet, resolveReference } from "../src/multi-get.ts";
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
    assert.deepEqual([...new Set(backend.calls.filter((c) => c.method === "multiGet").map((c) => c.args[0]))], ["qmd://claude-sessions/2026-03-05_beta.md,"]);
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

test("similarFiles=false never asks the SDK about a miss, for a caller with no allowlist", async () => {
  await withApp(async (app, backend) => {
    backend.calls.length = 0;
    const get = await app.inject({ method: "GET", url: "/docs/nowhere/at-all.md?similarFiles=false", headers: authHeaders });
    assert.equal(get.statusCode, 404);
    assert.deepEqual(get.json().details, { similarFiles: [] });
    assert.deepEqual(backend.calls.map((c) => c.method), ["listDocumentRefs"]);

    backend.calls.length = 0;
    const ids = Array.from({ length: 200 }, (_, i) => `nowhere/${i}.md`);
    const multi = await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { ids, similarFiles: false } });
    assert.equal(multi.statusCode, 200);
    assert.equal(multi.json().errors.length, 200);
    assert.deepEqual(backend.calls.map((c) => c.method), ["listDocumentRefs"]);

    const hit = await app.inject({ method: "GET", url: "/docs/codex-sessions/2026-08-31_gamma.md?similarFiles=false", headers: authHeaders });
    assert.equal(hit.statusCode, 200);
    assert.equal(hit.json().collection, "codex-sessions");
  });
});

test("without the flag, a miss still goes to the SDK and may carry suggestions", async () => {
  await withApp(async (app, backend) => {
    backend.calls.length = 0;
    const response = await app.inject({ method: "GET", url: "/docs/claude-sessions/2026-nothing.md", headers: authHeaders });
    assert.equal(response.statusCode, 404);
    assert.ok(backend.calls.some((c) => c.method === "get"));
  });
});

test("references that would reach the scanning path are capped per request", async () => {
  await withApp(async (app, backend) => {
    const ids = (n: number) => Array.from({ length: n }, (_, i) => `nowhere/${i}.md`);
    backend.calls.length = 0;
    const over = await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { ids: ids(MAX_SCANNING_REFERENCES + 1) } });
    assert.equal(over.statusCode, 400);
    assert.deepEqual(backend.calls.filter((c) => c.method === "multiGet"), []);
    const at = await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { ids: ids(MAX_SCANNING_REFERENCES) } });
    assert.equal(at.statusCode, 200);
  });
});

test("a body that will not fit the response budget is listed as skipped and never read", async () => {
  const docs = Array.from({ length: 40 }, (_, i) => ({ collection: "claude-sessions", path: `big-${String(i).padStart(2, "0")}.md`, body: "x".repeat(10_000) }));
  const { app, backend } = await makeApp({ docs });
  try {
    const response = await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { pattern: "claude-sessions/*.md", maxTotalBytes: 25_000, similarFiles: false } });
    assert.equal(response.statusCode, 200, response.body);
    const body = response.json();
    assert.equal(body.docs.length, 2);
    assert.equal(body.skipped.length, 38);
    assert.ok(body.skipped.every((s: { reason: string }) => /budget/.test(s.reason)));
    assert.equal(body.truncated, false);
    const bodyReads = backend.calls.filter((c) => c.method === "multiGet" && (c.args[1] as { includeBody: boolean }).includeBody);
    assert.equal(bodyReads.length, 2, "documents past the budget must not be read");
  } finally {
    await app.close();
  }
});

test("the budget counts bytes: a document that passes on characters can still be dropped once read", async () => {
  const docs = [
    { collection: "c", path: "wide.md", body: "é".repeat(100) },
    { collection: "c", path: "z-small.md", body: "ok" },
  ];
  const { app, backend } = await makeApp({ docs });
  const lengthInCharacters = backend.multiGet.bind(backend);
  backend.multiGet = async (pattern, options) => {
    const result = await lengthInCharacters(pattern, options);
    for (const entry of result.docs) {
      if (!entry.skipped && entry.doc.displayPath === "c/wide.md") entry.doc.bodyLength = 100;
    }
    return result;
  };
  try {
    const body = (await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { pattern: "c/*.md", maxTotalBytes: 150 } })).json();
    assert.deepEqual(body.docs.map((d: { path: string }) => d.path), ["z-small.md"]);
    assert.deepEqual(body.skipped.map((s: { file: string }) => s.file), ["c/wide.md"]);
  } finally {
    await app.close();
  }
});

test("oversized and over-budget documents are both reported, and metadata-only reads ignore the budget", async () => {
  const docs = [
    { collection: "c", path: "a.md", body: "x".repeat(50) },
    { collection: "c", path: "b.md", body: "x".repeat(50) },
    { collection: "c", path: "huge.md", body: "x".repeat(500) },
  ];
  const { app, backend } = await makeApp({ docs });
  try {
    const multi = (payload: object) => app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { pattern: "c/*.md", ...payload } });
    const both = (await multi({ maxBytes: 100, maxTotalBytes: 60 })).json();
    assert.deepEqual(both.docs.map((d: { path: string }) => d.path), ["a.md"]);
    assert.deepEqual(both.skipped.map((s: { file: string }) => s.file).sort(), ["c/b.md", "c/huge.md"]);

    backend.calls.length = 0;
    const metadata = (await multi({ includeBody: false, maxTotalBytes: 1 })).json();
    assert.equal(metadata.docs.length, 3);
    assert.ok(backend.calls.every((c) => c.method !== "multiGet" || (c.args[1] as { includeBody: boolean }).includeBody === false));
  } finally {
    await app.close();
  }
});

test("a document that vanishes between sizing and reading is simply absent", async () => {
  const { app, backend } = await makeApp({ docs: [{ collection: "c", path: "gone.md", body: "x" }] });
  const real = backend.multiGet.bind(backend);
  backend.multiGet = async (pattern, options) => (options.includeBody ? { docs: [], errors: [] } : real(pattern, options));
  try {
    const body = (await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { pattern: "c/*.md" } })).json();
    assert.deepEqual(body.docs, []);
    assert.deepEqual(body.skipped, []);
  } finally {
    await app.close();
  }
});

test("a glob selects at most MAX_GLOB_DOCUMENTS across the request and says it stopped", async () => {
  const docs = Array.from({ length: MAX_GLOB_DOCUMENTS + 500 }, (_, i) => ({ collection: "large", path: `d-${i}.md`, body: "x" }));
  const { app } = await makeApp({ docs });
  try {
    const multi = (payload: object) => app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { includeBody: false, ...payload } });
    const capped = (await multi({ pattern: "large/*.md" })).json();
    assert.equal(capped.docs.length, MAX_GLOB_DOCUMENTS);
    assert.equal(capped.truncated, true);
    const split = (await multi({ pattern: "large/d-1*.md,large/d-2*.md,large/*.md" })).json();
    assert.equal(split.docs.length, MAX_GLOB_DOCUMENTS);
    assert.equal(split.truncated, true);
    const small = (await multi({ pattern: "large/d-1.md,large/d-2?.md" })).json();
    assert.equal(small.truncated, false);
  } finally {
    await app.close();
  }
});

test("POST /docs:multiGet refuses maxTotalBytes above its ceiling", async () => {
  await withApp(async (app) => {
    const over = await app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload: { pattern: "*/*.md", maxTotalBytes: MAX_TOTAL_BYTES_CEILING + 1 } });
    assert.equal(over.statusCode, 400);
  });
});

test("200 misses against a large index cost little more than one", HANG_LIMIT, () => {
  const rows = Array.from({ length: 50_000 }, (_, i) => ref("c", `dir/doc-${i}.md`, i.toString(16).padStart(12, "0")));
  const started = performance.now();
  const index = new ReferenceIndex(rows);
  for (let i = 0; i < 200; i++) assert.deepEqual(index.resolve(`nowhere/missing-${i}.md`), []);
  assert.ok(performance.now() - started < 10_000);
  const spelled = new ReferenceIndex([ref("c", "Work.md"), ref("c", "work.md")]);
  assert.deepEqual(spelled.resolve("work.md").map((r) => r.path), ["work.md"]);
  assert.deepEqual(spelled.resolve("WORK.md").map((r) => r.path), ["Work.md", "work.md"]);
});

test("an ambiguous path among the rows is reported, and a shared docid is not", async () => {
  const shared = "ffffffffffff";
  const backend = {
    listDocumentRefs: async () => [ref("a", "same.md", shared), ref("b", "same.md", shared)],
    multiGet: async () => ({ docs: [], errors: [] }),
  };
  const result = await resolveMultiGet(backend, undefined, ["same.md", "ffffff"], { includeBody: false, maxBytes: 100, maxTotalBytes: 100, similarFiles: false });
  assert.deepEqual(result.errors, ["Ambiguous path same.md: qmd://a/same.md, qmd://b/same.md"]);
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
