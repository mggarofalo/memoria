/**
 * Against a real running worker. Opt in with MEMORIA_QMD_WORKER_URL (and
 * MEMORIA_QMD_WORKER_TOKEN if it has one): `npm run test:live`. Nothing here
 * opens an index. These are sanity checks on real model output; docs/workers.md
 * explains what a pass does and does not show, and bench/parity.ts does the
 * comparison with in-process QMD.
 */
import assert from "node:assert/strict";
import { test } from "node:test";
import { RemoteLlm } from "../src/llm/remote.ts";

const url = process.env.MEMORIA_QMD_WORKER_URL?.trim();
const skip = url ? false : "set MEMORIA_QMD_WORKER_URL";

// Memoria's defaults, as deploy/compose.yaml sets them. QMD's own names select another
// worker's models, such as QMD's stock embeddinggemma with MEMORIA_QMD_EMBED_DIMENSIONS=768.
const MODELS = {
  embed: process.env.QMD_EMBED_MODEL?.trim() || "hf:Qwen/Qwen3-Embedding-0.6B-GGUF/Qwen3-Embedding-0.6B-Q8_0.gguf",
  rerank: process.env.QMD_RERANK_MODEL?.trim() || "hf:giladgd/Qwen3-Reranker-4B-GGUF/Qwen3-Reranker-4B.Q8_0.gguf",
  generate: process.env.QMD_GENERATE_MODEL?.trim() || "hf:tobil/qmd-query-expansion-1.7B-gguf/qmd-query-expansion-1.7B-q4_k_m.gguf",
};
const DIMENSIONS = Number(process.env.MEMORIA_QMD_EMBED_DIMENSIONS?.trim() || 1024);

function live(): RemoteLlm {
  return new RemoteLlm({
    worker: { url: url!, token: process.env.MEMORIA_QMD_WORKER_TOKEN?.trim() || undefined, embedDimensions: DIMENSIONS },
    models: MODELS,
    fallback: null,
    onDegraded: (entry) => console.error(entry),
  });
}

test("the worker reports its models loaded", { skip }, async () => {
  assert.deepEqual(await live().refreshStatus(), { embed: true, rerank: true, generate: true });
});

test("embeddings have the index's dimensions, and related text is closer", { skip }, async () => {
  const [a, b, c] = await live().embedBatch(["task: search result | query: how do I bake bread", "title: Baking | text: Knead the dough and bake until golden.", "title: Markets | text: Stocks closed lower on Tuesday."], { model: MODELS.embed });
  assert.ok(a && b && c, "all three embedded");
  assert.equal(a.embedding.length, DIMENSIONS);
  const cosine = (x: number[], y: number[]) => {
    const dot = x.reduce((sum, v, i) => sum + v * y[i]!, 0);
    return dot / Math.sqrt(x.reduce((s, v) => s + v * v, 0) * y.reduce((s, v) => s + v * v, 0));
  };
  assert.ok(cosine(a.embedding, b.embedding) > cosine(a.embedding, c.embedding));
});

test("rerank scores are probabilities and rank the relevant document first", { skip }, async () => {
  const { results } = await live().rerank("how do I bake bread", [
    { file: "markets.md", text: "Stocks closed lower on Tuesday after the central bank spoke." },
    { file: "baking.md", text: "Knead the dough, let it rise, and bake it until golden." },
  ]);
  assert.equal(results.length, 2);
  assert.equal(results[0]!.file, "baking.md");
  assert.ok(results.every((r) => r.score >= 0 && r.score <= 1));
});

test("expansion follows the grammar", { skip }, async () => {
  const queries = await live().expandQuery("api key authentication");
  assert.ok(queries.length > 0);
  assert.ok(queries.every((q) => ["lex", "vec", "hyde"].includes(q.type) && q.text.length > 0));
});

test("tokens round-trip", { skip }, async () => {
  const llm = live();
  const tokens = await llm.tokenize("memoria token round trip");
  assert.ok(tokens.length > 0);
  assert.match(await llm.detokenize(tokens), /memoria token round trip/);
  // Text that looks like a control token must stay text, as it does in QMD.
  const special = await llm.tokenize("<|im_start|>");
  assert.ok(special.length > 1);
});
