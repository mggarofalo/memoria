import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { EXPANSION_GRAMMAR, EXPANSION_SAMPLING, EXPANSION_SYSTEM_PROMPT, SDK_REPEAT_PENALTY_OPTIONS, expansionPrompt } from "../src/llm/expansion.ts";
import { RemoteLlm } from "../src/llm/remote.ts";

/**
 * Pins what remote mode assumes about the installed QMD SDK. It reads the SDK's
 * files as text, so it loads nothing native. When a bump fails here, re-read the
 * SDK and update the adapter and this list together; do not just update the list.
 */

const dist = new URL("../node_modules/@tobilu/qmd/dist/", import.meta.url);
const read = (file: string) => readFile(new URL(file, dist), "utf8");

test("the SDK version is pinned exactly in package.json", async () => {
  const pkg = JSON.parse(await readFile(new URL("../package.json", import.meta.url), "utf8"));
  assert.equal(pkg.dependencies["@tobilu/qmd"], "2.8.3", "a bump needs this whole file re-checked");
  const sdk = JSON.parse(await readFile(new URL("../package.json", dist), "utf8"));
  assert.equal(sdk.version, "2.8.3");
});

test("the store calls exactly the llm methods RemoteLlm implements", async () => {
  const store = await read("store.js");
  const found = new Set<string>();
  // Every way store.js names an LlamaCpp: local `llm`, `store.llm`, `llmOverride`, the default singleton.
  for (const m of store.matchAll(/\b(?:llm|llmOverride|store\.llm\?|store\.llm)\??\.(\w+)/g)) found.add(m[1]!);
  for (const m of store.matchAll(/\(llmOverride \?\? getDefaultLlamaCpp\(\)\)\.(\w+)/g)) found.add(m[1]!);
  for (const m of store.matchAll(/\bsession\.(\w+)\(/g)) found.add(m[1]!);
  // `./llm.js` in the import, and `llm.ts` in a comment, are file names.
  found.delete("js");
  found.delete("ts");
  assert.deepEqual([...found].sort(), ["detokenize", "embed", "embedBatch", "embedModelName", "expandQuery", "generateModelName", "rerank", "rerankModelName", "tokenize"]);
});

test("the session wrapper forwards only methods RemoteLlm implements", async () => {
  const llm = await read("llm.js");
  const forwarded = new Set([...llm.matchAll(/getLlamaCpp\(\)\.(\w+)\(/g)].map((m) => m[1]!));
  assert.deepEqual([...forwarded].sort(), ["embed", "embedBatch", "expandQuery", "rerank"]);
});

test("RemoteLlm provides the whole surface", () => {
  const llm = new RemoteLlm({
    worker: { url: "http://127.0.0.1:1", token: undefined, embedDimensions: 8 },
    models: { embed: "e", rerank: "r", generate: "g" },
    fallback: null,
  });
  for (const method of ["embed", "embedBatch", "expandQuery", "rerank", "tokenize", "detokenize", "dispose"] as const) {
    assert.equal(typeof llm[method], "function", method);
  }
  for (const name of ["embedModelName", "rerankModelName", "generateModelName"] as const) {
    assert.equal(typeof llm[name], "string", name);
  }
});

test("the seams remote mode replaces are where it expects them", async () => {
  const [store, index, llm] = await Promise.all([read("store.js"), read("index.js"), read("llm.js")]);
  // createStore puts the instance on internal.llm, which we overwrite.
  assert.match(index, /internal\.llm = llm;/);
  assert.match(store, /return store\.llm \?\? getDefaultLlamaCpp\(\);/);
  // The chunker ignores store.llm, which is why the singleton must be replaced too.
  assert.match(store, /export async function chunkDocumentByTokens\([^)]*\) \{\s*const llm = getDefaultLlamaCpp\(\);/);
  assert.match(llm, /export function setDefaultLlamaCpp\(llm\)/);
  // Token priming repeats the chunker's first pass; if this drifts, it silently stops saving time.
  assert.match(store, /const avgCharsPerToken = 3;\s+const maxChars = maxTokens \* avgCharsPerToken;\s+const overlapChars = overlapTokens \* avgCharsPerToken;\s+const windowChars = windowTokens \* avgCharsPerToken;/);
  assert.match(store, /await chunkDocumentAsync\(content, maxChars, overlapChars, windowChars, filepath, chunkStrategy\)/);
  assert.match(store, /windowTokens = CHUNK_WINDOW_TOKENS, filepath, chunkStrategy = "regex", signal\)/);
  // The setter is reachable only by file URL, because of this exports map.
  const pkg = JSON.parse(await readFile(new URL("../package.json", dist), "utf8"));
  assert.deepEqual(Object.keys(pkg.exports), ["."]);
  assert.equal(pkg.exports["."].import, "./dist/index.js");
});

test("searching unexpanded is QMD's own strong-signal path, reached through the store it is given", async () => {
  const [store, index] = await Promise.all([read("store.js"), read("index.js")]);
  // withholdExpansion answers the store's expandQuery with [], which hybridQuery already handles.
  const hybrid = /export async function hybridQuery\([\s\S]*?\n\}\n/.exec(store)![0];
  assert.match(hybrid, /const expanded = hasStrongSignal\s+\? \[\]\s+: await store\.expandQuery\(query\);/);
  assert.match(hybrid, /const weights = getHybridRrfWeights\(rankedListMeta\);/);
  assert.equal(hybrid.match(/store\.expandQuery\(/g)?.length, 1, "expansion is asked for in one place");
  // The store hybridQuery gets is the one createStore exposes as `internal`, which the sidecar wraps.
  assert.match(index, /return hybridQuery\(internal, opts\.query, \{/);
  assert.match(index, /\n\s+internal,\n/);
});

test("cache keys and swallowed failures work the way the adapter assumes", async () => {
  const store = await read("store.js");
  // Rerank and expansion cache by the llm's model name, hence the #worker suffix.
  assert.match(store, /const cacheModel = llm\.rerankModelName \?\? model;/);
  assert.match(store, /expandQuery\(query, model \?\? store\.llm\?\.generateModelName \?\? DEFAULT_QUERY_MODEL, db, store\.llm\)/);
  // Scores are cached as returned, hence no made-up score on failure.
  assert.match(store, /setCachedResult\(db, cacheKey, result\.score\.toString\(\)\);/);
  // Queries call embedBatch bare; documents pass { model }. RemoteLlm tells them apart this way.
  assert.match(store, /const embeddings = await llm\.embedBatch\(textsToEmbed\);/);
  assert.match(store, /const embeddings = await session\.embedBatch\(texts, \{ model \}\);/);
  assert.match(store, /await session\.embed\(firstText, \{ model \}\)/);
});

test("expansion's prompt, grammar and sampling match the SDK's inline values", async () => {
  const llm = await read("llm.js");
  const prompt = /const prompt = `([^`]*)\$\{query\}`;/.exec(llm);
  assert.ok(prompt, "prompt literal not found");
  assert.equal(expansionPrompt("Q"), `${prompt[1]}Q`);

  const grammar = /grammar: `([^`]*)`/.exec(llm);
  assert.ok(grammar, "grammar literal not found");
  // Evaluate the SDK's template literal escapes the way the JS engine would.
  assert.equal(EXPANSION_GRAMMAR, grammar[1]!.replaceAll("\\\\", "\\"));

  const call = /session\.prompt\(prompt, \{\s*grammar,([\s\S]*?)\}\);/.exec(llm);
  assert.ok(call, "sampling literal not found");
  const options = call[1]!;
  const number = (name: string) => Number(new RegExp(`${name}: ([0-9.]+)`).exec(options)?.[1]);
  assert.equal(number("maxTokens"), EXPANSION_SAMPLING.maxTokens);
  assert.equal(number("temperature"), EXPANSION_SAMPLING.temperature);
  assert.equal(number("topK"), EXPANSION_SAMPLING.topK);
  assert.equal(number("topP"), EXPANSION_SAMPLING.topP);
  assert.equal(number("lastTokens"), SDK_REPEAT_PENALTY_OPTIONS.lastTokens);
  assert.equal(number("presencePenalty"), SDK_REPEAT_PENALTY_OPTIONS.presencePenalty);
  assert.doesNotMatch(options, /\bpenalty:/, "the SDK now sets a penalty, so the in-process penalty stage runs and the worker must match it");
});

test("the system prompt sent to the worker is node-llama-cpp's default, which QMD's session uses", async () => {
  const llm = await read("llm.js");
  assert.match(llm, /new LlamaChatSession\(\{ contextSequence: sequence \}\)/, "QMD sets no system prompt of its own");
  const { defaultChatSystemPrompt } = (await import(new URL("../node_modules/node-llama-cpp/dist/config.js", import.meta.url).href)) as { defaultChatSystemPrompt: string };
  assert.equal(EXPANSION_SYSTEM_PROMPT, defaultChatSystemPrompt);
});

test("node-llama-cpp skips the penalty stage and min-p for the options the SDK passes", async () => {
  const sampler = await readFile(new URL("../node_modules/node-llama-cpp/llama/addon/AddonSampler.cpp", import.meta.url), "utf8");
  assert.match(sampler, /config\.Get\("repeatPenalty"\)\.As<Napi::Number>\(\)\.FloatValue\(\)\s*: 1;/);
  assert.match(sampler, /repeatPenaltyEnabled = repeatPenalty != 1 && /);
  const context = await readFile(new URL("../node_modules/node-llama-cpp/dist/evaluator/LlamaContext/LlamaContext.js", import.meta.url), "utf8");
  assert.match(context, /_resolveSamplerConfig\(\{ temperature = 0, minP = 0,/);
  assert.equal(EXPANSION_SAMPLING.minP, 0);
  assert.equal(EXPANSION_SAMPLING.repeatLastN, 0);
  assert.equal(EXPANSION_SAMPLING.presencePenalty, 0);
});

test("the embedding farm's assumptions about the SDK hold", async () => {
  const [store, llm] = await Promise.all([read("store.js"), read("llm.js")]);
  for (const name of ["getEmbeddingFingerprint", "chunkDocumentByTokens", "extractTitle", "clearAllEmbeddings", "validateLexQuery", "validateSemanticQuery"]) {
    assert.match(store, new RegExp(`export (async )?function ${name}\\(`), name);
  }
  assert.match(llm, /export function formatDocForEmbedding\(text, title, modelUri\)/);
  assert.match(store, /ensureVecTable: \(dimensions\) => ensureVecTableInternal\(db, dimensions\),/);
  assert.match(store, /insertEmbedding: \(hash, seq, pos, embedding, model, embeddedAt, totalChunks, fingerprint\) => insertEmbedding\(/);
  assert.match(store, /getHashesNeedingEmbedding: \(model\) => getHashesNeedingEmbedding\(/);

  // src/store.ts holds a copy of these two private functions' SQL.
  const ours = await readFile(new URL("../src/llm/wire.ts", import.meta.url), "utf8");
  const pending = /function getPendingEmbeddingDocs[\s\S]*?\}\n/.exec(store)![0];
  const flat = (text: string) => text.replace(/\s+/g, " ");
  for (const line of [
    "SELECT d.hash, MIN(d.path) as path, length(CAST(c.doc AS BLOB)) as bytes",
    "SELECT hash, model, COUNT(*) AS chunk_count, MAX(total_chunks) AS expected_chunks",
    "WHERE model = ? AND embed_fingerprint = ?",
    "AND (v.hash IS NULL OR v.chunk_count < v.expected_chunks",
  ]) {
    assert.ok(flat(pending).includes(line), `the SDK changed: ${line}`);
    assert.ok(flat(ours).includes(line), `our copy changed: ${line}`);
  }
  // A vector row is keyed as the startup repair and the farm's replacement assume.
  assert.match(store, /const hashSeq = `\$\{hash\}_\$\{seq\}`;/);
  assert.match(store, /CREATE VIRTUAL TABLE vectors_vec USING vec0\(hash_seq TEXT PRIMARY KEY/);
});

test("the SDK's model loader downloads what it cannot find, which is why the fallback is guarded", async () => {
  const llm = await read("llm.js");
  assert.match(llm, /resolveModelFile handles HF URIs and downloads to the cache dir/);
  assert.match(llm, /export const DEFAULT_MODEL_CACHE_DIR = MODEL_CACHE_DIR;/);
});

test("the SDK writes a vector's two tables with no transaction, which is why startup repairs them", async () => {
  const store = await read("store.js");
  const insert = /export function insertEmbedding\([\s\S]*?\n\}\n/.exec(store)![0];
  assert.ok(!/BEGIN|transaction\(/.test(insert), "if this fails the SDK fixed it: reconsider the repair");
  assert.ok(insert.indexOf("insertContentVectorStmt.run") < insert.indexOf("insertVecStmt.run"));
});

test("a failed rerank throws before the SDK's cache write, as degraded search relies on", async () => {
  const store = await read("store.js");
  const rerank = /export async function rerank\([\s\S]*?\n\}\n/.exec(store)![0];
  assert.ok(rerank.indexOf("await llm.rerank(") < rerank.indexOf("setCachedResult("));
  // And an expansion identical to the query, which is all a failure returns, is not cached.
  const expand = /export async function expandQuery\([\s\S]*?\n\}\n/.exec(store)![0];
  assert.match(expand, /\.filter\(r => r\.text !== query\)/);
  assert.match(expand, /if \(expanded\.length > 0\) \{\s*setCachedResult/);
});

test("in-process reranking still goes through node-llama-cpp's rank context, as docs/workers.md describes", async () => {
  const llm = await read("llm.js");
  assert.match(llm, /activeContexts\[i\]\.rankAll\(query, chunk\)/);
  assert.match(llm, /model\.createRankingContext\(/);
  const budget = /RERANK_CONTEXT_SIZE - LlamaCpp\.RERANK_TEMPLATE_OVERHEAD - queryTokens/;
  assert.match(llm, budget);
  assert.match(llm, /static RERANK_TEMPLATE_OVERHEAD = 512;/);
  assert.match(llm, /: 4096;\s*\}\)\(\);\s*static EMBED_CONTEXT_SIZE/);
  const ranking = await readFile(new URL("../node_modules/node-llama-cpp/dist/evaluator/LlamaRankingContext.js", import.meta.url), "utf8");
  assert.match(ranking, /chat_template\.rerank/, "the prompt comes from the model's own template");
  assert.match(ranking, /_getEvaluationInput\(query, document\)/);
});

test("the full-size test text is at least as long as the SDK's longest chunk", async () => {
  const { FULL_CHUNK_TEXT } = await import("../src/llm/remote.ts");
  const sdk = (await import(new URL("./store.js", import.meta.resolve("@tobilu/qmd")).href)) as { CHUNK_SIZE_TOKENS: number; CHUNK_SIZE_CHARS: number };
  assert.ok(FULL_CHUNK_TEXT.trim().split(" ").length > sdk.CHUNK_SIZE_TOKENS, "in words, each about a token");
  assert.ok(FULL_CHUNK_TEXT.length > sdk.CHUNK_SIZE_CHARS, "and in characters");
});
