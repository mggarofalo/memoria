import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { cosine, expansionKey, jaccard, meanPairwiseJaccard, scoreDelta, spearman, summarize, topKOverlap } from "../bench/metrics.ts";
import { rerankQueryFor, runParity, validateQuerySet, type BenchQuery, type ParitySide } from "../bench/run.ts";
import { createWorkspace, sweepDeadWorkspaces, WORKSPACE_PREFIX } from "../bench/workspace.ts";
import { tempDir } from "./helpers.ts";

const close = (a: number | null, b: number) => assert.ok(a !== null && Math.abs(a - b) < 1e-9, `${a} is not ${b}`);

test("topKOverlap only looks at the top k", () => {
  assert.equal(topKOverlap(["a", "b"], ["b", "a"], 10), 1);
  assert.equal(topKOverlap(["a", "b"], ["c", "d"], 10), 0);
  assert.equal(topKOverlap(["a", "b", "x"], ["a", "c", "x"], 2), 0.5);
  assert.equal(topKOverlap([], [], 10), 1);
  assert.equal(topKOverlap(["a"], [], 10), 0);
});

test("spearman measures rank agreement, handles ties, and is null when undefined", () => {
  close(spearman([1, 2, 3], [10, 20, 30]), 1);
  close(spearman([1, 2, 3], [30, 20, 10]), -1);
  // Scale does not matter, only order: this is why it is reported beside score deltas.
  close(spearman([0.1, 0.2, 0.9], [0.5, 0.51, 0.52]), 1);
  close(spearman([1, 2, 2, 3], [1, 3, 2, 4]), 0.9486832980505138);
  assert.equal(spearman([1], [1]), null);
  assert.equal(spearman([0.5, 0.5], [0.1, 0.2]), null);
  assert.throws(() => spearman([1], [1, 2]), /equal length/);
});

test("scoreDelta, cosine, jaccard and expansion keys", () => {
  assert.deepEqual(scoreDelta([0.5, 0.5], [0.25, 1]), { mean: 0.375, max: 0.5 });
  assert.deepEqual(scoreDelta([], []), { mean: 0, max: 0 });
  close(cosine([1, 0], [2, 0]), 1);
  close(cosine([1, 0], [0, 1]), 0);
  assert.equal(cosine([0, 0], [1, 1]), null);
  assert.equal(cosine([1], [1, 2]), null);
  assert.equal(jaccard(new Set(["a", "b"]), new Set(["b", "c"])), 1 / 3);
  assert.equal(jaccard(new Set(), new Set()), 1);
  assert.equal(expansionKey({ type: "lex", text: "  API Keys " }), "lex:api keys");
});

test("meanPairwiseJaccard compares within or across groups", () => {
  const a = new Set(["x"]);
  const b = new Set(["y"]);
  assert.equal(meanPairwiseJaccard([a, a]), 1);
  assert.equal(meanPairwiseJaccard([a, b]), 0);
  assert.equal(meanPairwiseJaccard([a]), null);
  assert.equal(meanPairwiseJaccard([a, b], [a]), 0.5);
});

test("summarize leaves undefined values out rather than counting them as zero", () => {
  const s = summarize([null, 1, 2, 3, 4, null]);
  assert.deepEqual(s, { n: 4, mean: 2.5, min: 1, p50: 2, p95: 4, max: 4 });
  assert.deepEqual(summarize([null]), { n: 0, mean: null, min: null, p50: null, p95: null, max: null });
});

// --- the runner -----------------------------------------------------------------

const QUERY: BenchQuery = { id: "q1", collection: "c", query: "bread", intent: "baking", searches: [{ type: "lex", query: "bread" }] };

function scriptedSide(over: Partial<ParitySide> & { name: string; log?: string[] }): ParitySide {
  const log = over.log ?? [];
  return {
    candidates: async () => [{ file: "a.md", text: "A" }, { file: "b.md", text: "B" }, { file: "c.md", text: "C" }],
    rerank: async () => ({ model: "m", results: [{ file: "a.md", score: 0.9, index: 0 }, { file: "b.md", score: 0.5, index: 1 }, { file: "c.md", score: 0.1, index: 2 }] }),
    embed: async () => [1, 0],
    search: async () => ({ files: ["a.md", "b.md"], scores: [0.9, 0.5] }),
    expand: async () => [{ type: "lex", text: "bread" }],
    clearCache: () => void log.push(`clear ${over.name}`),
    ...over,
  };
}

test("identical sides report perfect agreement", async () => {
  const report = await runParity([QUERY], scriptedSide({ name: "l" }), scriptedSide({ name: "r" }), { candidateLimit: 40, expansionRuns: 2 });
  const q = report.queries[0]!;
  assert.deepEqual(q.errors, []);
  assert.equal(q.candidates, 3);
  assert.deepEqual(q.rerank!.delta, { mean: 0, max: 0 });
  close(q.rerank!.spearman, 1);
  assert.equal(q.rerank!.top10Overlap, 1);
  close(q.embedCosine, 1);
  assert.equal(q.endToEnd!.top10Overlap, 1);
  assert.equal(q.expansion.localVsRemote, 1);
  assert.equal(q.expansion.exactMatchRate, 1);
  assert.equal(report.summary.rerankTop10Overlap.mean, 1);
});

test("the two sides score the same candidates, with QMD's intent prefix", async () => {
  const seen: string[] = [];
  const rerank = (label: string): ParitySide["rerank"] => async (query, documents) => {
    seen.push(`${label}|${query}|${documents.map((d) => d.text).join()}`);
    return { model: "m", results: documents.map((d, index) => ({ file: d.file, score: 0.5, index })) };
  };
  const remoteCandidates = async () => {
    throw new Error("the remote side's candidates must not be used");
  };
  await runParity([QUERY], scriptedSide({ name: "l", rerank: rerank("l") }), scriptedSide({ name: "r", rerank: rerank("r"), candidates: remoteCandidates }), { candidateLimit: 40, expansionRuns: 2 });
  assert.deepEqual(seen, ["l|baking\n\nbread|A,B,C", "r|baking\n\nbread|A,B,C"]);
  assert.equal(rerankQueryFor({ ...QUERY, intent: "" }), "bread");
});

test("divergence shows up in the numbers", async () => {
  const remote = scriptedSide({
    name: "r",
    rerank: async () => ({ model: "m", results: [{ file: "a.md", score: 0.1, index: 0 }, { file: "b.md", score: 0.5, index: 1 }, { file: "c.md", score: 0.9, index: 2 }] }),
    embed: async () => [0, 1],
    search: async () => ({ files: ["z.md"], scores: [0.9] }),
    expand: async () => [{ type: "vec", text: "something else" }],
  });
  const q = (await runParity([QUERY], scriptedSide({ name: "l" }), remote, { candidateLimit: 40, expansionRuns: 2 })).queries[0]!;
  close(q.rerank!.spearman, -1);
  close(q.rerank!.delta.max, 0.8);
  close(q.embedCosine, 0);
  assert.equal(q.endToEnd!.top10Overlap, 0);
  assert.equal(q.expansion.localVsRemote, 0);
  assert.equal(q.expansion.localVsLocal, 1);
  assert.equal(q.expansion.exactMatchRate, 0);
});

test("each side's cache is cleared before the end-to-end run, or it would compare stale results", async () => {
  const order: string[] = [];
  const side = (name: string) => scriptedSide({
    name,
    clearCache: () => void order.push(`clear ${name}`),
    search: async () => {
      order.push(`search ${name}`);
      return { files: [], scores: [] };
    },
  });
  await runParity([QUERY], side("l"), side("r"), { candidateLimit: 40, expansionRuns: 2 });
  assert.deepEqual(order, ["clear l", "clear r", "search l", "search r"]);
});

test("a failing remote is recorded per query and the run continues", async () => {
  const fail = async () => {
    throw new Error("LLM worker x: rerank failed (unavailable): HTTP 503");
  };
  const remote = scriptedSide({ name: "r", rerank: fail, search: fail });
  const report = await runParity([QUERY, { ...QUERY, id: "q2" }], scriptedSide({ name: "l" }), remote, { candidateLimit: 40, expansionRuns: 2 });
  assert.equal(report.queries.length, 2);
  assert.equal(report.queries[0]!.rerank, null);
  assert.equal(report.queries[0]!.endToEnd, null);
  assert.deepEqual(report.queries[0]!.errors, ["rerank remote: LLM worker x: rerank failed (unavailable): HTTP 503", "search remote: LLM worker x: rerank failed (unavailable): HTTP 503"]);
  assert.equal(report.summary.rerankSpearman.n, 0, "failures are not scored as disagreement or agreement");
});

test("latency is timed per side", async () => {
  let t = 0;
  const report = await runParity([QUERY], scriptedSide({ name: "l" }), scriptedSide({ name: "r" }), { candidateLimit: 1, expansionRuns: 2, now: () => (t += 5) });
  assert.equal(report.queries[0]!.rerank!.localMs, 5);
  assert.equal(report.summary.latencyMs.rerankRemote.n, 1);
});

// --- the committed query set and the entry point -----------------------------------

test("the committed query set has 20 to 30 queries across both collections", async () => {
  const raw = JSON.parse(await readFile(new URL("../bench/queries.json", import.meta.url), "utf8"));
  const queries = validateQuerySet(raw);
  assert.ok(queries.length >= 20 && queries.length <= 30, `${queries.length} queries`);
  assert.deepEqual([...new Set(queries.map((q) => q.collection))].sort(), ["claude-sessions", "codex-sessions"]);
  assert.ok(queries.every((q) => q.intent.length > 0));
});

test("validateQuerySet catches bad entries", () => {
  assert.throws(() => validateQuerySet({}), /must be a list/);
  assert.throws(() => validateQuerySet([QUERY, QUERY]), /repeated/);
  assert.throws(() => validateQuerySet([{ ...QUERY, intent: "" }]), /no intent/);
  assert.throws(() => validateQuerySet([{ ...QUERY, searches: [] }]), /no searches/);
  assert.throws(() => validateQuerySet([{ ...QUERY, searches: [{ type: "bm25", query: "x" }] }]), /bad search/);
});

const entry = fileURLToPath(new URL("../bench/parity.ts", import.meta.url));
const runEntry = (env: NodeJS.ProcessEnv, ...args: string[]) => spawnSync(process.execPath, [entry, ...args], { env: { ...process.env, MEMORIA_QMD_WORKER_URL: "", ...env }, encoding: "utf8", timeout: 30_000 });

test("with no worker URL the benchmark skips and exits 0, so it is never required", () => {
  const run = runEntry({});
  assert.equal(run.status, 0, run.stderr);
  assert.match(run.stdout, /skipped/);
});

test("with a worker URL it insists on a named index and has no default to fall back on", () => {
  const env = { MEMORIA_QMD_WORKER_URL: "http://127.0.0.1:1" };
  const none = runEntry(env);
  assert.equal(none.status, 2);
  assert.match(none.stderr, /--db is required/);
  const missing = runEntry(env, "--db", "/definitely/not/an/index.sqlite");
  assert.equal(missing.status, 2);
  assert.match(missing.stderr, /No index/);
});

test("the next run clears what a dead run left, and nothing else", async () => {
  const root = await tempDir("memoria-sweep-");
  const mine = await createWorkspace(root);
  const dead = await createWorkspace(root);
  writeFileSync(path.join(dead, ".memoria-parity-owner"), "99999999", "utf8");
  writeFileSync(path.join(dead, "local.sqlite"), "copy", "utf8");
  const unmarked = path.join(root, `${WORKSPACE_PREFIX}notours`);
  mkdirSync(unmarked);
  const otherName = path.join(root, "unrelated");
  mkdirSync(otherName);
  writeFileSync(path.join(otherName, ".memoria-parity-owner"), "99999999", "utf8");
  assert.deepEqual(sweepDeadWorkspaces(root), [dead]);
  assert.equal(existsSync(dead), false);
  assert.equal(existsSync(mine), true, "a live run keeps its copies");
  assert.equal(existsSync(unmarked), true, "no marker, not ours to delete");
  assert.equal(existsSync(otherName), true);
});

test("Ctrl-C closes what it can and removes the workspace", async () => {
  const root = await tempDir("memoria-sigint-");
  const module = new URL("../bench/workspace.ts", import.meta.url).href;
  const script = `
    import { createWorkspace, onInterrupt, removeWorkspace } from ${JSON.stringify(module)};
    const dir = await createWorkspace(${JSON.stringify(root)});
    console.log(dir);
    onInterrupt(async () => removeWorkspace(dir));
    process.emit("SIGINT", "SIGINT");
    setTimeout(() => process.exit(9), 5000);
  `;
  const run = spawnSync(process.execPath, ["--input-type=module", "-e", script], { encoding: "utf8", timeout: 20_000 });
  assert.equal(run.status, 130);
  assert.equal(existsSync(run.stdout.trim()), false);
});

test("each query is reported as it finishes, so a crash keeps the earlier ones", async () => {
  const side: ParitySide = {
    candidates: async () => [{ file: "a", text: "a" }],
    rerank: async () => ({ results: [{ file: "a", score: 0.5, index: 0 }], model: "m" }),
    embed: async () => [1, 0],
    search: async () => ({ files: ["a"], scores: [0.5] }),
    expand: async () => [{ type: "lex", text: "x" }],
    clearCache: () => {},
  };
  const q = (id: string): BenchQuery => ({ id, collection: "c", query: "q", intent: "i", searches: [{ type: "lex", query: "q" }] });
  const counts: number[] = [];
  await runParity([q("one"), q("two")], side, side, { candidateLimit: 1, expansionRuns: 2, onQuery: (reports) => counts.push(reports.length) });
  assert.deepEqual(counts, [1, 2]);
});
