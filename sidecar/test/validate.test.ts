import assert from "node:assert/strict";
import { test } from "node:test";
import { checkedScore, parseCapabilities, parseEmbeddings, parseEmbedResponse, parseHealth, parseRerankResults, parseText, parseTokenLists, parseTokens, ResponseViolation } from "../src/llm/validate.ts";

/** Each rule, held to the exact message, which is fixed text and never quotes the worker's values. */
function refuses(run: () => unknown, message: string): void {
  assert.throws(run, (err: unknown) => {
    assert.ok(err instanceof ResponseViolation, `not a ResponseViolation: ${String(err)}`);
    assert.equal(err.name, "ResponseViolation");
    assert.equal(err.message, message);
    return true;
  });
}

test("a response that is not an object is refused, whatever it is instead", () => {
  for (const raw of [null, undefined, 3, "x", [], [{ embeddings: [[1]] }]]) refuses(() => parseEmbeddings(raw, 1, 0), "response is not an object");
});

test("embeddings: the batch must be an array of the requested size", () => {
  refuses(() => parseEmbeddings({}, 1, 0), "embeddings is not an array");
  refuses(() => parseEmbeddings({ embeddings: { 0: [1] } }, 1, 0), "embeddings is not an array");
  refuses(() => parseEmbeddings({ embeddings: [[1], [1]] }, 1, 0), "embedding count does not match the request");
  refuses(() => parseEmbeddings({ embeddings: [] }, 1, 0), "embedding count does not match the request");
  assert.deepEqual(parseEmbeddings({ embeddings: [] }, 0, 3), []);
});

test("embeddings: each is a non-empty array of the expected, or else the batch's, dimensions", () => {
  refuses(() => parseEmbeddings({ embeddings: [[]] }, 1, 0), "an embedding is not a non-empty array");
  refuses(() => parseEmbeddings({ embeddings: ["1"] }, 1, 0), "an embedding is not a non-empty array");
  refuses(() => parseEmbeddings({ embeddings: [[1, 2]] }, 1, 3), "an embedding has the wrong number of dimensions");
  // With no dimensions given, the first vector sets them for the rest.
  refuses(() => parseEmbeddings({ embeddings: [[1, 2], [1, 2, 3]] }, 2, 0), "an embedding has the wrong number of dimensions");
  assert.deepEqual(parseEmbeddings({ embeddings: [[1, 2], [3, 4]] }, 2, 0), [[1, 2], [3, 4]]);
  assert.deepEqual(parseEmbeddings({ embeddings: [[1, 2, 3]] }, 1, 3), [[1, 2, 3]]);
});

test("embeddings: values must survive the float32 cast, and the vector must have a usable length", () => {
  refuses(() => parseEmbeddings({ embeddings: [["1"]] }, 1, 0), "an embedding holds a non-finite value");
  for (const bad of [Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY]) refuses(() => parseEmbeddings({ embeddings: [[1, bad]] }, 1, 0), "an embedding holds a non-finite value");
  refuses(() => parseEmbeddings({ embeddings: [[1e308]] }, 1, 0), "an embedding holds a value float32 cannot store");
  refuses(() => parseEmbeddings({ embeddings: [[-1e308]] }, 1, 0), "an embedding holds a value float32 cannot store");
  refuses(() => parseEmbeddings({ embeddings: [[1, 1e-320]] }, 1, 0), "an embedding holds a value float32 cannot store");
  refuses(() => parseEmbeddings({ embeddings: [[0, 0, 0]] }, 1, 0), "an embedding is all zeros");
  // Each value fits float32, but the sum of their squares does not.
  refuses(() => parseEmbeddings({ embeddings: [[3e38, 3e38]] }, 1, 0), "an embedding is too long for float32");
  // A zero among other values is fine, and so are negative values.
  assert.deepEqual(parseEmbeddings({ embeddings: [[0, 1]] }, 1, 0), [[0, 1]]);
  assert.deepEqual(parseEmbeddings({ embeddings: [[-0.5, 0.25]] }, 1, 0), [[-0.5, 0.25]]);
  // A value whose square underflows float32 still counts toward a nonzero length when another value does.
  assert.deepEqual(parseEmbeddings({ embeddings: [[1e-30, 1]] }, 1, 0), [[1e-30, 1]]);
  refuses(() => parseEmbeddings({ embeddings: [[1e-30]] }, 1, 0), "an embedding is all zeros");
});

test("an embed response carries the model it names, unchecked, beside the vectors", () => {
  assert.deepEqual(parseEmbedResponse({ embeddings: [[1]], model: "m.gguf" }, 1, 1), { vectors: [[1]], model: "m.gguf" });
  assert.deepEqual(parseEmbedResponse({ embeddings: [[1]] }, 1, 1), { vectors: [[1]], model: undefined });
  refuses(() => parseEmbedResponse({ embeddings: [[1]] }, 2, 1), "embedding count does not match the request");
});

test("rerank: one result per document, each index once and in range, scores realigned to the request", () => {
  refuses(() => parseRerankResults({}, 1), "results is not an array");
  refuses(() => parseRerankResults({ results: [] }, 1), "result count does not match the request");
  refuses(() => parseRerankResults({ results: [7] }, 1), "response is not an object");
  for (const index of [-1, 2, 1.5, "0", undefined, Number.NaN]) refuses(() => parseRerankResults({ results: [{ index, score: 0.5 }, { index: 0, score: 0.5 }] }, 2), "a result index is out of range");
  refuses(() => parseRerankResults({ results: [{ index: 0, score: 0.5 }, { index: 0, score: 0.6 }] }, 2), "a result index is repeated");
  // A repeated index is caught even when its first score was zero.
  refuses(() => parseRerankResults({ results: [{ index: 0, score: 0 }, { index: 0, score: 0 }] }, 2), "a result index is repeated");
  refuses(() => parseRerankResults({ results: [{ index: 0, score: 2 }] }, 1), "a score is outside 0 to 1");
  assert.deepEqual(parseRerankResults({ results: [{ index: 1, score: 0.25 }, { index: 0, score: 1 }] }, 2), [1, 0.25]);
  assert.deepEqual(parseRerankResults({ results: [{ index: 0, score: 0 }] }, 1), [0]);
});

test("a score is a finite probability, ends included", () => {
  for (const bad of [-0.01, 1.01, Number.NaN, Number.POSITIVE_INFINITY, "0.5", null]) refuses(() => checkedScore(bad), "a score is outside 0 to 1");
  for (const good of [0, 0.5, 1]) assert.equal(checkedScore(good), good);
});

test("tokens: non-negative safe integers, no more than one per byte of the text plus 16", () => {
  refuses(() => parseTokens({}, "a"), "tokens is not an array");
  refuses(() => parseTokens({ tokens: "1" }, "a"), "tokens is not an array");
  // "é" is two bytes, so 2 + 16 tokens are allowed and one more is not.
  assert.equal(parseTokens({ tokens: new Array(18).fill(1) }, "é").length, 18);
  refuses(() => parseTokens({ tokens: new Array(19).fill(1) }, "é"), "more tokens than the text can produce");
  for (const bad of [-1, 1.5, "1", Number.MAX_SAFE_INTEGER + 1, null]) refuses(() => parseTokens({ tokens: [1, bad] }, "ab"), "a token is not a non-negative integer");
  assert.deepEqual(parseTokens({ tokens: [0, Number.MAX_SAFE_INTEGER] }, "ab"), [0, Number.MAX_SAFE_INTEGER]);
});

test("token lists: one list per text, each held to the single-answer rules against its own text", () => {
  for (const raw of [null, {}, { tokens: "x" }, { tokens: [[1]] }]) refuses(() => parseTokenLists(raw, ["a", "b"]), "token list count does not match the request");
  assert.deepEqual(parseTokenLists({ tokens: [[1], [2, 3]] }, ["a", "bc"]), [[1], [2, 3]]);
  // The second text is short, so 17 tokens are too many for it though not for the first.
  refuses(() => parseTokenLists({ tokens: [new Array(17).fill(1), new Array(18).fill(1)] }, ["ab", "a"]), "more tokens than the text can produce");
  refuses(() => parseTokenLists({ tokens: [[1], [-1]] }, ["a", "b"]), "a token is not a non-negative integer");
});

test("text: a string no longer than the limit", () => {
  refuses(() => parseText({}, 10), "text is not a string");
  refuses(() => parseText({ text: 5 }, 10), "text is not a string");
  refuses(() => parseText({ text: "abc" }, 2), "text is longer than the limit");
  assert.equal(parseText({ text: "ab" }, 2), "ab");
  assert.equal(parseText({ text: "" }, 0), "");
});

test("capabilities: operations must be strings; a model name that is not a string reads as none", () => {
  refuses(() => parseCapabilities({ models: {} }), "operations is not a list of strings");
  refuses(() => parseCapabilities({ operations: ["embed", 3], models: {} }), "operations is not a list of strings");
  refuses(() => parseCapabilities({ operations: "embed", models: {} }), "operations is not a list of strings");
  refuses(() => parseCapabilities({ operations: [] }), "response is not an object");
  assert.deepEqual(parseCapabilities({ operations: ["embed"], models: { embed: "e.gguf", rerank: 7, generate: null }, rerankTransport: "completion" }), {
    operations: ["embed"],
    models: { embed: "e.gguf", rerank: null, generate: null },
    rerankTransport: "completion",
  });
  assert.deepEqual(parseCapabilities({ operations: [], models: {}, rerankTransport: 1 }), { operations: [], models: { embed: null, rerank: null, generate: null }, rerankTransport: null });
});

test("health: status, loaded models and queue depth are coerced to safe values", () => {
  refuses(() => parseHealth({ status: "ok" }), "response is not an object");
  assert.deepEqual(parseHealth({ status: "busy", models: { embed: { loaded: true }, rerank: { loaded: "yes" }, generate: true }, queueDepth: 3 }), {
    status: "busy",
    models: { embed: true, rerank: false, generate: false },
    queueDepth: 3,
  });
  assert.equal(parseHealth({ status: "starting", models: {} }).status, "starting");
  for (const status of ["ok", "down", undefined, 1]) assert.equal(parseHealth({ status, models: {} }).status, "ok");
  assert.deepEqual(parseHealth({ models: { embed: null } }).models, { embed: false, rerank: false, generate: false });
  for (const queueDepth of [-1, 0, Number.POSITIVE_INFINITY, Number.NaN, "5", undefined]) assert.equal(parseHealth({ models: {}, queueDepth }).queueDepth, 0);
  assert.equal(parseHealth({ models: {}, queueDepth: 0.5 }).queueDepth, 0.5);
});
