/**
 * The rerank e2e's downloads, which run in CI, so a network hiccup must be ridden out and anything worse
 * must fail with a message that says it was the download. No network: the fetcher is scripted.
 */
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { existsSync, mkdtempSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fetchAsset, loadAssets, type Asset } from "../../scripts/e2e/rerank-assets.ts";

const BODY = Buffer.from("a small model, pretend");
const asset: Asset = { name: "test model", file: "model.gguf", url: "https://example.invalid/model.gguf", sha256: createHash("sha256").update(BODY).digest("hex"), bytes: BODY.length };
const noSleep = async (): Promise<void> => {};

function scripted(...answers: Array<Response | Error>): { fetcher: (url: string, init: RequestInit) => Promise<Response>; calls: () => number } {
  let calls = 0;
  return {
    fetcher: async () => {
      const answer = answers[Math.min(calls++, answers.length - 1)]!;
      if (answer instanceof Error) throw answer;
      return answer;
    },
    calls: () => calls,
  };
}

test("a hiccup is retried, and the file lands only once its hash matches", async () => {
  const dir = mkdtempSync(path.join(tmpdir(), "rerank-assets-"));
  const net = scripted(new Error("ECONNRESET"), new Response("busy", { status: 503 }), new Response(BODY));
  assert.equal(await fetchAsset(asset, dir, net.fetcher, noSleep), "downloaded");
  assert.equal(net.calls(), 3);
  assert.deepEqual(readFileSync(path.join(dir, asset.file)), BODY);
  assert.deepEqual(readdirSync(dir), [asset.file], "no partial file left");
});

test("a file already there with the right hash is kept, which is what a cache restores", async () => {
  const dir = mkdtempSync(path.join(tmpdir(), "rerank-assets-"));
  writeFileSync(path.join(dir, asset.file), BODY);
  const net = scripted(new Error("must not be called"));
  assert.equal(await fetchAsset(asset, dir, net.fetcher, noSleep), "cached");
  assert.equal(net.calls(), 0);
});

test("a wrong hash, from the cache or the network, is never kept, and the failure says it was the download", async () => {
  const dir = mkdtempSync(path.join(tmpdir(), "rerank-assets-"));
  writeFileSync(path.join(dir, asset.file), "a corrupted cache entry");
  const net = scripted(new Response("not the file at all"));
  await assert.rejects(fetchAsset(asset, dir, net.fetcher, noSleep), (err: Error) => {
    assert.match(err.message, /Could not fetch test model \(model\.gguf\)/);
    assert.match(err.message, /SHA-256 [0-9a-f]{64}, expected/);
    assert.match(err.message, /not a test failure/);
    return true;
  });
  assert.equal(net.calls(), 4, "every attempt was made");
  assert.equal(existsSync(path.join(dir, asset.file)), false);
  assert.deepEqual(readdirSync(dir), []);
});

test("the manifest pins every asset by SHA-256, and every model by a revision", () => {
  const assets = loadAssets();
  assert.ok(assets.length >= 3);
  for (const a of assets) {
    assert.match(a.sha256, /^[0-9a-f]{64}$/, a.file);
    assert.ok(a.bytes > 0, a.file);
    assert.ok(a.url.startsWith("https://"), a.file);
    if (a.url.includes("huggingface.co")) assert.match(a.url, /\/resolve\/[0-9a-f]{40}\//, `${a.file} is pinned to a revision, not main`);
  }
});
