/**
 * Allowlisted reads and glob refusal against the real QMD SDK, no models. See
 * integration.test.ts for how to opt in.
 */
import assert from "node:assert/strict";
import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { test } from "node:test";
import { buildApp } from "../src/app.ts";
import { openBackend } from "../src/store.ts";
import { authHeaders, tempDir, testConfig } from "./helpers.ts";

const enabled = process.env.MEMORIA_QMD_INTEGRATION === "1";

test("allowlisted reads stay inside the allowlist on a real index", { skip: enabled ? false : "set MEMORIA_QMD_INTEGRATION=1", timeout: 60_000 }, async () => {
  const root = await tempDir("memoria-qmd-scope-");
  const dbPath = path.join(root, "index.sqlite");
  const shared = "# Shared\n\nThe same words in two collections.\n";
  const files: [string, string, string][] = [
    ["alpha", "own.md", "# Own\n\nalpha only\n"],
    ["alpha", "copy.md", shared],
    ["beta", "copy.md", shared],
    ["beta", "secret-plans.md", "# Secret\n\nbeta only\n"],
  ];
  for (const [collection, name, body] of files) {
    await mkdir(path.join(root, collection), { recursive: true });
    await writeFile(path.join(root, collection, name), body, "utf8");
  }

  const qmd = await import("@tobilu/qmd");
  const builder = await qmd.createStore({
    dbPath,
    config: { collections: { alpha: { path: path.join(root, "alpha"), pattern: "**/*.md" }, beta: { path: path.join(root, "beta"), pattern: "**/*.md" } } },
  });
  await builder.update();
  await builder.close();

  const backend = await openBackend({ dbPath, keepWarm: false });
  const app = await buildApp({ backend, config: testConfig(), logger: false });
  const get = (reference: string, collection: string) => app.inject({ method: "GET", url: `/docs/${reference}?collections=${collection}`, headers: authHeaders });
  const multi = (payload: object) => app.inject({ method: "POST", url: "/docs:multiGet", headers: authHeaders, payload });
  try {
    await app.ready();

    // Duplicate content shares a docid: each caller gets their own copy, whichever row QMD would pick.
    const docid = (await get("beta/copy.md", "beta")).json().docid;
    assert.equal((await get(docid, "alpha")).json().file, "alpha/copy.md");
    assert.equal((await get(docid, "beta")).json().file, "beta/copy.md");
    const viaMulti = (await multi({ ids: [docid], collections: ["alpha"] })).json();
    assert.deepEqual(viaMulti.docs.map((d: { file: string }) => d.file), ["alpha/copy.md"]);

    // Out-of-scope hits and misses answer alike, with no similar files.
    const outOfScope = await get("beta/secret-plans.md", "alpha");
    const missing = await get("beta/secret-plan.md", "alpha");
    assert.equal(outOfScope.statusCode, 404);
    assert.equal(missing.statusCode, 404);
    assert.deepEqual(outOfScope.json().details, { similarFiles: [] });
    assert.deepEqual(missing.json().details, { similarFiles: [] });

    // Case variants resolve only to what the allowlist holds.
    assert.equal((await get("ALPHA/own.md", "alpha")).statusCode, 200);
    assert.equal((await get("BETA/secret-plans.md", "alpha")).statusCode, 404);

    // A glob spanning collections matches and measures only the allowlist.
    const glob = (await multi({ pattern: "*/*.md", maxBytes: 1, collections: ["alpha"] })).json();
    assert.deepEqual(glob.skipped.map((s: { file: string }) => s.file).sort(), ["alpha/copy.md", "alpha/own.md"]);
    assert.deepEqual(glob.errors, []);

    // Without an allowlist, the same grammar still matches across collections.
    const unscoped = (await multi({ pattern: "*/copy.md" })).json();
    assert.deepEqual(unscoped.docs.map((d: { file: string }) => d.file).sort(), ["alpha/copy.md", "beta/copy.md"]);

    // These wedged the event loop for good when they reached the SDK's matcher.
    for (const pattern of ["+(*)Z", "*(*)Z", "(*)*(*)*(*)*Z", "@(*|*)+Z", `${"*?".repeat(20)}Z`]) {
      assert.equal((await multi({ pattern })).statusCode, 400, pattern);
      assert.equal((await multi({ ids: [pattern] })).statusCode, 400, pattern);
    }
    assert.equal((await app.inject({ method: "GET", url: "/health" })).statusCode, 200);
  } finally {
    await app.close();
  }
});
