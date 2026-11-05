import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { patchedStore } from "../scripts/patch-qmd.ts";

/**
 * The reranker blend fix from upstream tobi/qmd#1032, through the installed SDK's own search
 * functions. Only the store under them is scripted, so no model or index is involved and
 * every number is exact. The first list is the rank-fusion order: doc1.md is rank 1.
 */

interface Hit {
  file: string;
  score: number;
  explain?: { rerankScore: number; blendedScore: number; rrf: { rank: number; positionScore: number; weight: number } };
}
interface Options {
  limit: number;
  minScore?: number;
  explain?: boolean;
}

const sdkUrl = new URL("./store.js", import.meta.resolve("@tobilu/qmd")).href;
const sdk = (await import(sdkUrl)) as {
  hybridQuery(store: unknown, query: string, options: Options): Promise<Hit[]>;
  structuredSearch(store: unknown, searches: Array<{ type: string; query: string }>, options: Options): Promise<Hit[]>;
};

const fileAt = (rank: number): string => `doc${rank}.md`;

/** `scoreOf` is the reranker's answer for an RRF rank. It answers in reverse, so a tie cannot fall out right by luck. */
function scriptedStore(candidates: number, scoreOf: (rank: number) => number): object {
  const ranks = Array.from({ length: candidates }, (_, i) => i + 1);
  return {
    db: { prepare: () => ({ get: () => undefined }) },
    searchFTS: () => ranks.map((rank) => ({ filepath: fileAt(rank), displayPath: fileAt(rank), title: "", body: `body of ${rank}`, score: 0.1, docid: `d${rank}` })),
    expandQuery: async () => [],
    rerank: async () => ranks.toReversed().map((rank) => ({ file: fileAt(rank), score: scoreOf(rank) })),
    getContextForFile: () => undefined,
    invalidateExpansionCache: () => {},
  };
}

const searches: Array<[string, (store: object, options: Options) => Promise<Hit[]>]> = [
  ["hybridQuery", (store, options) => sdk.hybridQuery(store, "alpha", options)],
  ["structuredSearch", (store, options) => sdk.structuredSearch(store, [{ type: "lex", query: "alpha" }], options)],
];

const ranksOf = (hits: Hit[]): number[] => hits.map((hit) => Number(/\d+/.exec(hit.file)?.[0]));

for (const [name, search] of searches) {
  test(`${name}: at equal reranker scores the rank-fusion order stands`, async () => {
    // The stock blend scores rank 3 at 0.500 and rank 11 at 0.636, so it put 11 to 14 above 3 to 10.
    const hits = await search(scriptedStore(14, () => 1), { limit: 14, minScore: 0 });
    assert.deepEqual(ranksOf(hits), Array.from({ length: 14 }, (_, i) => i + 1));
    assert.ok(hits.every((hit) => hit.score === 1));
  });

  test(`${name}: the reranker's score decides, whatever the rank`, async () => {
    // Stock: rank 1 at 0.75 + 0.25 * 0.2 = 0.80 beat rank 5 at 0.12 + 0.4 * 0.9 = 0.48.
    const scores = new Map([[1, 0.2], [2, 0.5], [3, 0.1], [4, 0.3], [5, 0.9]]);
    const hits = await search(scriptedStore(5, (rank) => scores.get(rank) ?? 0), { limit: 5, minScore: 0 });
    assert.deepEqual(ranksOf(hits), [5, 2, 4, 1, 3]);
    assert.deepEqual(hits.map((hit) => hit.score), [0.9, 0.5, 0.3, 0.2, 0.1]);
  });

  test(`${name}: minScore reads the reranker's score, with no floor from rank`, async () => {
    // Stock: rank 1 scored at least 0.75 whatever the reranker said, so it survived this.
    const hits = await search(scriptedStore(3, (rank) => (rank === 1 ? 0.2 : 0.6)), { limit: 3, minScore: 0.5 });
    assert.deepEqual(ranksOf(hits), [2, 3]);
  });

  test(`${name}: explain still shows the rank fusion, and blendedScore is the reranker's score`, async () => {
    const [hit] = await search(scriptedStore(2, (rank) => (rank === 2 ? 0.7 : 0.1)), { limit: 1, minScore: 0, explain: true });
    assert.ok(hit?.explain);
    assert.equal(hit.file, fileAt(2));
    assert.equal(hit.explain.rerankScore, 0.7);
    assert.equal(hit.explain.blendedScore, 0.7);
    assert.deepEqual([hit.explain.rrf.rank, hit.explain.rrf.positionScore, hit.explain.rrf.weight], [2, 0.5, 0.75]);
  });
}

// --- applying it, and a future SDK --------------------------------------------------------------------

const installed = readFileSync(fileURLToPath(sdkUrl), "utf8");
const FIXED_LINE = "const blendedScore = r.score;";
const STOCK_LINE = "const blendedScore = rrfWeight * rrfScore + (1 - rrfWeight) * r.score;";
const FIXED_SORT = "b.score - a.score || (rrfRankMap.get(a.file) ?? candidateLimit) - (rrfRankMap.get(b.file) ?? candidateLimit)";
const FIXED_COMMENT = /\/\/ Step 7: Order by reranker score; RRF rank breaks ties\.[\s\S]*?deep candidates\./;
const STOCK_COMMENT = "// Step 7: Blend RRF position score with reranker score\n    // Position-aware weights: top retrieval results get more protection from reranker disagreement";

test("an install that has only the earlier fixes gets the blend fix, once at each site", () => {
  const before = installed.replaceAll(FIXED_LINE, STOCK_LINE).replaceAll(FIXED_SORT, "b.score - a.score").replace(FIXED_COMMENT, STOCK_COMMENT);
  assert.equal(before.split(FIXED_LINE).length, 1);
  const after = patchedStore(before);
  assert.equal(after, installed);
  assert.equal(after.split(FIXED_LINE).length, 3, "both functions, and nowhere else");
  assert.equal(after.split(FIXED_SORT).length, 3);
});

test("an SDK that already has the upstream fix is left as it is", () => {
  // The fix as upstream wrote it is the patch's own text, so nothing can apply on top of it.
  assert.equal(patchedStore(installed), installed);
  assert.equal(patchedStore(patchedStore(installed)), installed);
});

test("an SDK that fixed the blend some other way stops the install, rather than taking half a patch", () => {
  const otherFix = "const blendedScore = 0.2 * (1 - (rrfRank - 1) / 40) + 0.8 * r.score;";
  assert.throws(() => patchedStore(installed.replaceAll(FIXED_LINE, otherFix)), /The QMD SDK has changed.*monotonic.*patch-qmd\.ts/);
  assert.throws(() => patchedStore(installed.replaceAll(FIXED_SORT, "b.score - a.score || a.file.localeCompare(b.file)")), /The QMD SDK has changed.*patch-qmd\.ts/);
});

test("a sidecar with the stock blend refuses to start", { timeout: 60_000 }, () => {
  const fixture = fileURLToPath(new URL("./fixtures/stock-blend-sdk.ts", import.meta.url));
  const run = spawnSync(process.execPath, [fixture], { encoding: "utf8", timeout: 50_000 });
  assert.equal(run.status, 0, run.stderr);
  assert.match(run.stdout, /refused: .*not patched.*hybridQuery has the stock rerank blend.*structuredSearch has the stock rerank blend.*patch-qmd\.ts/);
});
