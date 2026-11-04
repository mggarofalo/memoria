import assert from "node:assert/strict";
import { test } from "node:test";
import { WorkerClient, WorkerError } from "../src/llm/client.ts";
import { EXPANSION_GRAMMAR, EXPANSION_SAMPLING, parseExpansion } from "../src/llm/expansion.ts";
import { acceptedEmbedFiles, RemoteLlm, WORKER_CACHE_MARK, type RemoteLlmOptions } from "../src/llm/remote.ts";
import type { RerankTransport } from "../src/llm/rerank-transport.ts";
import type { LlmCallLog, LocalFallback } from "../src/llm/types.ts";
import { EMBED_FILE, MockWorker, WORKER_TOKEN } from "./mock-worker.ts";

const URI = `hf:ggml-org/embeddinggemma-300M-GGUF/${EMBED_FILE}`;
const docs = (...texts: string[]) => texts.map((text, i) => ({ file: `f${i}.md`, text }));

interface Harness {
  worker: MockWorker;
  llm: RemoteLlm;
  calls: LlmCallLog[];
  degraded: Array<{ operation: string; error: string; fallback: boolean }>;
  done(): Promise<void>;
}

async function harness(options: Partial<RemoteLlmOptions> & { dimensions?: number } = {}): Promise<Harness> {
  const dimensions = options.dimensions ?? 8;
  const worker = await new MockWorker(dimensions).start();
  const calls: LlmCallLog[] = [];
  const degraded: Harness["degraded"] = [];
  const llm = new RemoteLlm({
    worker: { url: worker.url, token: WORKER_TOKEN, embedDimensions: dimensions },
    models: { embed: URI, rerank: "hf:x/rerank.gguf", generate: "hf:x/gen.gguf" },
    fallback: null,
    onCall: (e) => calls.push(e),
    onDegraded: (e) => degraded.push(e),
    // These tests are about one call at a time. test/pool.test.ts covers the breaker.
    breaker: { threshold: 1000 },
    ...options,
  });
  return {
    worker,
    llm,
    calls,
    degraded,
    done: async () => {
      await llm.dispose();
      await worker.stop();
    },
  };
}

function fakeFallback(over: Partial<LocalFallback> = {}): LocalFallback & { used: string[] } {
  const used: string[] = [];
  return {
    used,
    embedBatch: async (texts) => {
      used.push("embedBatch");
      return texts.map(() => ({ embedding: [1, 0, 0, 0, 0, 0, 0, 0], model: "local" }));
    },
    tokenize: async () => {
      used.push("tokenize");
      return [1, 2, 3];
    },
    detokenize: async () => {
      used.push("detokenize");
      return "local text";
    },
    ...over,
  };
}

// --- happy paths ---------------------------------------------------------------

test("embedBatch sends the token and returns validated vectors under the plain model name", async () => {
  const h = await harness();
  try {
    const out = await h.llm.embedBatch(["a", "b"], { model: "m" });
    assert.equal(out.length, 2);
    assert.equal(out[0]!.embedding.length, 8);
    assert.equal(out[0]!.model, URI);
    assert.equal(h.llm.embedModelName, URI, "stored vectors are keyed by this; it must not change");
    const embed = h.worker.requests.find((r) => r.path === "/v1/embed")!;
    assert.equal(embed.authorization, `Bearer ${WORKER_TOKEN}`);
    assert.deepEqual(embed.body, { texts: ["a", "b"] });
    assert.deepEqual(await h.llm.embedBatch([]), []);
    assert.equal((await h.llm.embed("solo"))!.embedding.length, 8);
  } finally {
    await h.done();
  }
});

test("rerank returns the SDK's shape, sorted, and scores identical texts once", async () => {
  const h = await harness();
  try {
    h.worker.on("POST /v1/rerank", (b) => ({ body: { results: (b.documents as string[]).map((d, index) => ({ index, score: d === "best" ? 0.9 : 0.2 })) } }));
    const out = await h.llm.rerank("q", [{ file: "a.md", text: "dup" }, { file: "b.md", text: "best" }, { file: "c.md", text: "dup" }]);
    assert.deepEqual(h.worker.requests.find((r) => r.path === "/v1/rerank")!.body, { query: "q", documents: ["dup", "best"] });
    assert.deepEqual(out.results[0], { file: "b.md", score: 0.9, index: 1 });
    assert.deepEqual(out.results.slice(1).map((r) => r.file).sort(), ["a.md", "c.md"]);
    assert.ok(out.results.every((r) => typeof r.index === "number"));
    assert.equal(out.model, h.llm.rerankModelName);
    assert.deepEqual((await h.llm.rerank("q", [])).results, []);
  } finally {
    await h.done();
  }
});

test("worker-derived cache entries cannot be confused with in-process ones", async () => {
  const h = await harness();
  try {
    assert.ok(h.llm.rerankModelName.includes(WORKER_CACHE_MARK));
    assert.ok(h.llm.generateModelName.includes(WORKER_CACHE_MARK));
    assert.ok(!h.llm.embedModelName.includes(WORKER_CACHE_MARK));
  } finally {
    await h.done();
  }
});

test("expansion sends QMD's prompt, grammar and sampling, and returns Queryables", async () => {
  const h = await harness();
  try {
    const out = await h.llm.expandQuery("api keys");
    assert.deepEqual(out, [{ type: "lex", text: "api keys" }, { type: "vec", text: "about api keys" }, { type: "hyde", text: "a passage on api keys" }]);
    const sent = h.worker.requests.find((r) => r.path === "/v1/complete")!.body;
    assert.deepEqual(sent, { prompt: "/no_think Expand this search query: api keys", grammar: EXPANSION_GRAMMAR, ...EXPANSION_SAMPLING });
    assert.deepEqual((await h.llm.expandQuery("api keys", { includeLexical: false })).map((q) => q.type), ["vec", "hyde"]);
  } finally {
    await h.done();
  }
});

test("parseExpansion behaves as the SDK does on messy output", () => {
  // Off-topic lines and unknown types are dropped.
  assert.deepEqual(parseExpansion("api keys", "lex: api\nvec: bananas\nnope: api\nno colon api\nhyde: keys here\n", true), [{ type: "lex", text: "api" }, { type: "hyde", text: "keys here" }]);
  // Nothing usable gives nothing, unlike the SDK's placeholder, which would be cached.
  assert.deepEqual(parseExpansion("api", "garbage", true), []);
  assert.deepEqual(parseExpansion("api", "lex: api\n", false), []);
  // A query of only punctuation has no terms to check against.
  assert.deepEqual(parseExpansion("!!!", "vec: anything", true), [{ type: "vec", text: "anything" }]);
});

test("tokenize and detokenize round-trip", async () => {
  const h = await harness();
  try {
    const tokens = await h.llm.tokenize("héllo");
    assert.deepEqual(tokens, Array.from("héllo", (c) => c.codePointAt(0)));
    assert.equal(await h.llm.detokenize(tokens), "héllo");
  } finally {
    await h.done();
  }
});

test("every worker call logs worker, operation, batch size and duration, and nothing else of substance", async () => {
  const h = await harness();
  try {
    await h.llm.embedBatch(["secret-text-a", "secret-text-b", "secret-text-c"], { model: "m" });
    await h.llm.rerank("secret-query", docs("secret-doc"));
    const embed = h.calls.find((c) => c.operation === "embed")!;
    assert.equal(embed.worker, new URL(h.worker.url).host);
    assert.equal(embed.batchSize, 3);
    assert.equal(typeof embed.durationMs, "number");
    assert.equal(embed.ok, true);
    assert.equal(h.calls.find((c) => c.operation === "rerank")!.batchSize, 1);
    const text = JSON.stringify(h.calls);
    assert.ok(!text.includes("secret-"));
    assert.ok(!text.includes(WORKER_TOKEN));
  } finally {
    await h.done();
  }
});

// --- the rerank seam -----------------------------------------------------------

test("a different rerank transport changes nothing else", async () => {
  const seen: Array<[string, string[]]> = [];
  const transport: RerankTransport = {
    name: "test-transport",
    scores: async (query, texts) => {
      seen.push([query, texts]);
      return texts.map((_, i) => 1 - i / 10);
    },
  };
  const h = await harness({ rerankTransport: transport });
  try {
    assert.equal(h.llm.transportName, "test-transport");
    const out = await h.llm.rerank("q", docs("a", "b"));
    assert.deepEqual(seen, [["q", ["a", "b"]]]);
    assert.equal(h.worker.count("/v1/rerank"), 0);
    assert.deepEqual(out.results.map((r) => r.score), [1, 0.9]);
  } finally {
    await h.done();
  }
});

test("a transport's output gets the same validation as the worker's", async () => {
  for (const scores of [[0.5], [0.5, 1.5], [0.5, Number.NaN], [0.5, -0.1], [0.5, Number.POSITIVE_INFINITY], "nope"] as unknown as number[][]) {
    const h = await harness({ rerankTransport: { name: "bad", scores: async () => scores } });
    try {
      await assert.rejects(h.llm.rerank("q", docs("a", "b")), (err) => err instanceof WorkerError && err.code === "invalid_response", JSON.stringify(scores));
    } finally {
      await h.done();
    }
  }
});

// --- untrusted responses --------------------------------------------------------

test("embeddings of the wrong shape are rejected, never passed to the store", async () => {
  const good = Array.from({ length: 8 }, (_, i) => i + 1);
  const cases: Array<[string, unknown]> = [
    ["wrong dimensions", { embeddings: [good.slice(0, 7)] }],
    ["too many dimensions", { embeddings: [[...good, 9]] }],
    ["too few vectors", { embeddings: [] }],
    ["too many vectors", { embeddings: [good, good] }],
    ["not an array", { embeddings: "x" }],
    ["missing", {}],
    ["not an object", [good]],
    ["null", null],
    ["a string value", { embeddings: [[...good.slice(0, 7), "8"]] }],
    ["a null value", { embeddings: [[...good.slice(0, 7), null]] }],
    ["all zeros", { embeddings: [new Array(8).fill(0)] }],
    ["an empty vector", { embeddings: [[]] }],
  ];
  for (const [label, body] of cases) {
    const h = await harness();
    try {
      h.worker.on("POST /v1/embed", () => ({ body }));
      assert.deepEqual(await h.llm.embedBatch(["a"], { model: "m" }), [null], label);
      assert.equal(h.degraded.length, 1, label);
      assert.match(h.degraded[0]!.error, /invalid_response/, label);
    } finally {
      await h.done();
    }
  }
});

test("JSON cannot smuggle NaN, but an overflowing number is caught as non-finite", async () => {
  const h = await harness();
  try {
    h.worker.on("POST /v1/embed", () => ({ body: `{"embeddings":[[1e999,1,1,1,1,1,1,1]]}` }));
    assert.deepEqual(await h.llm.embedBatch(["a"], { model: "m" }), [null]);
    h.worker.on("POST /v1/embed", () => ({ body: "not json" }));
    assert.deepEqual(await h.llm.embedBatch(["a"], { model: "m" }), [null]);
  } finally {
    await h.done();
  }
});

test("with dimensions unset, the first response pins the length", async () => {
  const worker = await new MockWorker(4).start();
  const llm = new RemoteLlm({ worker: { url: worker.url, token: undefined, embedDimensions: 0 }, models: { embed: URI, rerank: "r", generate: "g" }, fallback: null });
  try {
    assert.equal((await llm.embed("a"))!.embedding.length, 4);
    worker.on("POST /v1/embed", () => ({ body: { embeddings: [[1, 2, 3]] } }));
    assert.equal(await llm.embed("b"), null);
    worker.on("POST /v1/embed", () => ({ body: { embeddings: [[1, 2, 3, 4], [1, 2, 3]] } }));
    assert.deepEqual(await llm.embedBatch(["a", "b"]), [null, null]);
  } finally {
    await worker.stop();
  }
});

test("a worker serving a different embedding model is refused before it can mix vector spaces", async () => {
  const h = await harness();
  try {
    h.worker.on("GET /capabilities", () => ({ body: { operations: ["embed"], models: { embed: "some-other-model.gguf" }, rerankTransport: null } }));
    assert.deepEqual(await h.llm.embedBatch(["a"], { model: "m" }), [null]);
    assert.equal(h.worker.count("/v1/embed"), 0, "nothing was embedded");
    assert.match(h.degraded[0]!.error, /model_mismatch/);

    // QMD's prefixed cache name is the same model; a fixed worker recovers at the next heartbeat.
    h.worker.on("GET /capabilities", () => ({ body: { operations: ["embed"], models: { embed: `hf_ggml-org_${EMBED_FILE}` }, rerankTransport: null } }));
    h.worker.on("POST /v1/embed", (b) => ({ body: { model: `hf_ggml-org_${EMBED_FILE}`, embeddings: (b.texts as string[]).map(() => [1, 2, 3, 4, 5, 6, 7, 8]) } }));
    assert.deepEqual(await h.llm.embedBatch(["a"], { model: "m" }), [null], "still refused until then");
    await h.llm.refreshStatus();
    assert.equal((await h.llm.embedBatch(["a"], { model: "m" }))[0]!.embedding.length, 8);
    const before = h.worker.count("/capabilities");
    await h.llm.embedBatch(["b"], { model: "m" });
    assert.equal(h.worker.count("/capabilities"), before, "checked once");
  } finally {
    await h.done();
  }
});

test("rerank results that break the rules fail the call rather than reach the cache", async () => {
  const cases: Array<[string, unknown]> = [
    ["score above 1", { results: [{ index: 0, score: 1.01 }, { index: 1, score: 0.5 }] }],
    ["negative score", { results: [{ index: 0, score: -0.01 }, { index: 1, score: 0.5 }] }],
    ["raw logit", { results: [{ index: 0, score: 7.3 }, { index: 1, score: -4.2 }] }],
    ["string score", { results: [{ index: 0, score: "0.5" }, { index: 1, score: 0.5 }] }],
    ["missing score", { results: [{ index: 0 }, { index: 1, score: 0.5 }] }],
    ["too few", { results: [{ index: 0, score: 0.5 }] }],
    ["too many", { results: [{ index: 0, score: 0.5 }, { index: 1, score: 0.5 }, { index: 2, score: 0.5 }] }],
    ["repeated index", { results: [{ index: 0, score: 0.5 }, { index: 0, score: 0.5 }] }],
    ["index out of range", { results: [{ index: 0, score: 0.5 }, { index: 2, score: 0.5 }] }],
    ["negative index", { results: [{ index: -1, score: 0.5 }, { index: 1, score: 0.5 }] }],
    ["fractional index", { results: [{ index: 0.5, score: 0.5 }, { index: 1, score: 0.5 }] }],
    ["not a list", { results: "x" }],
  ];
  for (const [label, body] of cases) {
    const h = await harness();
    try {
      h.worker.on("POST /v1/rerank", () => ({ body }));
      await assert.rejects(h.llm.rerank("q", docs("a", "b")), (err) => err instanceof WorkerError && err.code === "invalid_response", label);
    } finally {
      await h.done();
    }
  }
});

test("scores at the limits are accepted", async () => {
  const h = await harness();
  try {
    h.worker.on("POST /v1/rerank", () => ({ body: { results: [{ index: 1, score: 1 }, { index: 0, score: 0 }] } }));
    const out = await h.llm.rerank("q", docs("a", "b"));
    assert.deepEqual(out.results.map((r) => [r.file, r.score]), [["f1.md", 1], ["f0.md", 0]]);
  } finally {
    await h.done();
  }
});

test("expansion output that is not text falls back to the plain query", async () => {
  for (const body of [{ text: 7 }, {}, [], { text: "x".repeat(70_000) }]) {
    const h = await harness();
    try {
      h.worker.on("POST /v1/complete", () => ({ body }));
      assert.deepEqual(await h.llm.expandQuery("api"), [{ type: "lex", text: "api" }, { type: "vec", text: "api" }]);
    } finally {
      await h.done();
    }
  }
});

test("bad tokens and bad detokenized text are rejected", async () => {
  for (const tokens of [["1"], [-1], [1.5], "abc", new Array(100).fill(1)]) {
    const h = await harness();
    try {
      h.worker.on("POST /v1/tokenize", () => ({ body: { tokens } }));
      await assert.rejects(h.llm.tokenize("hi"), (err) => err instanceof WorkerError && err.code === "invalid_response", JSON.stringify(tokens).slice(0, 30));
    } finally {
      await h.done();
    }
  }
  const h = await harness();
  try {
    h.worker.on("POST /v1/detokenize", () => ({ body: { text: 3 } }));
    await assert.rejects(h.llm.detokenize([1]), (err) => err instanceof WorkerError && err.code === "invalid_response");
  } finally {
    await h.done();
  }
});

test("an enormous response is cut off while reading", async () => {
  const worker = await new MockWorker().start();
  const client = new WorkerClient({ url: worker.url, token: undefined, maxResponseBytes: 1024 });
  try {
    worker.on("GET /huge", () => ({ body: { padding: "x".repeat(4096) } }));
    await assert.rejects(client.get("huge", "/huge"), (err) => err instanceof WorkerError && err.code === "invalid_response" && /size limit/.test(err.message));
  } finally {
    await worker.stop();
  }
});

// --- degraded mode --------------------------------------------------------------

test("with no worker, query embedding and tokens use the CPU fallback", async () => {
  const fallback = fakeFallback();
  const h = await harness({ fallback });
  try {
    await h.worker.stop();
    const out = await h.llm.embedBatch(["query vector"]);
    assert.deepEqual(out[0]!.embedding, [1, 0, 0, 0, 0, 0, 0, 0]);
    assert.equal(out[0]!.model, URI);
    assert.deepEqual(await h.llm.embed("q", { isQuery: true }), { embedding: [1, 0, 0, 0, 0, 0, 0, 0], model: URI });
    assert.deepEqual(await h.llm.tokenize("x"), [1, 2, 3]);
    assert.equal(await h.llm.detokenize([1]), "local text");
    assert.deepEqual(fallback.used, ["embedBatch", "embedBatch", "tokenize", "detokenize"]);
    assert.ok(h.degraded.every((d) => d.fallback === true));
    assert.equal(h.calls.at(-1)!.ok, false);
  } finally {
    await h.done();
  }
});

test("document embedding never falls back to CPU", async () => {
  const fallback = fakeFallback();
  const h = await harness({ fallback });
  try {
    await h.worker.stop();
    assert.deepEqual(await h.llm.embedBatch(["chunk"], { model: URI }), [null]);
    assert.equal(await h.llm.embed("chunk", { model: URI }), null);
    assert.deepEqual(fallback.used, []);
    assert.equal(h.degraded[0]!.fallback, false);
  } finally {
    await h.done();
  }
});

test("a fallback of the wrong size or a failing one yields nulls, not a throw", async () => {
  const wrong = fakeFallback({ embedBatch: async (t) => t.map(() => ({ embedding: [1, 2, 3], model: "x" })) });
  const h = await harness({ fallback: wrong });
  try {
    await h.worker.stop();
    assert.deepEqual(await h.llm.embedBatch(["q"]), [null]);
  } finally {
    await h.done();
  }
  const broken = fakeFallback({ embedBatch: async () => { throw new Error("no model on this host"); } });
  const h2 = await harness({ fallback: broken });
  try {
    await h2.worker.stop();
    assert.deepEqual(await h2.llm.embedBatch(["q"]), [null]);
  } finally {
    await h2.done();
  }
});

test("with the fallback off, tokens fail loudly and embeddings come back null", async () => {
  const h = await harness({ fallback: null });
  try {
    await h.worker.stop();
    assert.deepEqual(await h.llm.embedBatch(["q"]), [null]);
    await assert.rejects(h.llm.tokenize("x"), (err) => err instanceof WorkerError && err.code === "unavailable");
    await assert.rejects(h.llm.detokenize([1]), (err) => err instanceof WorkerError && err.code === "unavailable");
  } finally {
    await h.done();
  }
});

test("rerank has no fallback and invents no score when the worker is down", async () => {
  const fallback = fakeFallback();
  const h = await harness({ fallback });
  try {
    await h.worker.stop();
    await assert.rejects(h.llm.rerank("q", docs("a")), (err) => err instanceof WorkerError && err.code === "unavailable");
    assert.deepEqual(fallback.used, []);
  } finally {
    await h.done();
  }
});

test("expansion with no worker returns the plain query, as the in-process failure path does", async () => {
  const h = await harness();
  try {
    await h.worker.stop();
    assert.deepEqual(await h.llm.expandQuery("api"), [{ type: "lex", text: "api" }, { type: "vec", text: "api" }]);
    assert.deepEqual(await h.llm.expandQuery("api", { includeLexical: false }), [{ type: "vec", text: "api" }]);
  } finally {
    await h.done();
  }
});

// --- status ---------------------------------------------------------------------

test("status reflects worker health and goes false when it disappears or lies", async () => {
  const h = await harness();
  try {
    assert.deepEqual(h.llm.status(), { embed: false, rerank: false, generate: false });
    assert.deepEqual(await h.llm.refreshStatus(), { embed: true, rerank: true, generate: true });
    h.worker.on("GET /health", () => ({ body: { models: { embed: { loaded: "yes" }, rerank: { loaded: true } } } }));
    assert.deepEqual(await h.llm.refreshStatus(), { embed: false, rerank: true, generate: false });
    h.worker.on("GET /health", () => ({ body: "garbage" }));
    assert.deepEqual(await h.llm.refreshStatus(), { embed: false, rerank: false, generate: false });
    await h.worker.stop();
    assert.deepEqual(await h.llm.refreshStatus(), { embed: false, rerank: false, generate: false });
  } finally {
    await h.done();
  }
});

// --- the token ------------------------------------------------------------------

test("a hostile worker cannot get the token into an error, even by echoing it", async () => {
  const echo = (_: unknown, request: { headers: { authorization?: string } }) => ({
    status: 500,
    body: { error: request.headers.authorization, message: `your token is ${request.headers.authorization}` },
  });
  const h = await harness();
  try {
    for (const route of ["POST /v1/rerank", "POST /v1/embed", "POST /v1/tokenize", "POST /v1/complete", "GET /capabilities", "GET /health"]) h.worker.on(route, echo as never);
    const seen: string[] = [];
    await h.llm.rerank("q", docs("a")).catch((e: Error) => seen.push(e.message, String(e.stack), JSON.stringify(e)));
    await h.llm.tokenize("x").catch((e: Error) => seen.push(e.message, String(e.stack), JSON.stringify(e)));
    await h.llm.embedBatch(["a"], { model: "m" });
    await h.llm.expandQuery("x");
    // An unsafe remote code is dropped entirely; a safe one is kept.
    h.worker.on("POST /v1/rerank", () => ({ status: 502, body: { error: "rerank_score_out_of_range", message: "free text" } }));
    await assert.rejects(h.llm.rerank("q", docs("a")), (err: Error) => /rerank_score_out_of_range/.test(err.message) && !/free text/.test(err.message));
    await h.llm.refreshStatus();
    seen.push(JSON.stringify(h.calls), JSON.stringify(h.degraded), JSON.stringify(h.llm.pool.workers()));
    assert.ok(seen.length >= 5);
    for (const text of seen) assert.ok(!text.includes(WORKER_TOKEN), text);
  } finally {
    await h.done();
  }
});

test("a success body echoing the token still cannot reach a validation error", async () => {
  const h = await harness();
  try {
    h.worker.on("POST /v1/rerank", (_b, request) => ({ body: { results: [{ index: 0, score: request.headers.authorization }] } }));
    await assert.rejects(h.llm.rerank("q", docs("a")), (err: Error) => !err.message.includes(WORKER_TOKEN) && err instanceof WorkerError);
  } finally {
    await h.done();
  }
});

test("redirects are refused, so the token is never resent elsewhere", async () => {
  const elsewhere = await new MockWorker().start();
  const h = await harness();
  try {
    h.worker.on("POST /v1/rerank", () => ({ status: 200, body: {} }));
    const real = h.worker;
    real.handlers.set("POST /v1/tokenize", () => ({ status: 307, body: {} }));
    // Scripted fetch shows the option; the real one enforces it.
    let redirect: unknown;
    const client = new WorkerClient({
      url: real.url,
      token: WORKER_TOKEN,
      fetch: (async (url: URL, init: RequestInit) => {
        redirect = init.redirect;
        return globalThis.fetch(url, init);
      }) as unknown as typeof globalThis.fetch,
    });
    await client.get("health", "/health");
    assert.equal(redirect, "error");
    assert.equal(elsewhere.requests.length, 0);
  } finally {
    await elsewhere.stop();
    await h.done();
  }
});

test("a worker URL holding credentials is refused, and errors name only host and port", async () => {
  assert.throws(() => new WorkerClient({ url: "http://user:pw@127.0.0.1:1", token: undefined }), /credentials/);
  assert.throws(() => new WorkerClient({ url: "http://127.0.0.1:1?token=abc", token: undefined }), /query/);
  assert.throws(() => new WorkerClient({ url: "ftp://127.0.0.1:1", token: undefined }), /http/);
  const client = new WorkerClient({ url: "http://127.0.0.1:1/base", token: WORKER_TOKEN });
  await assert.rejects(client.get("health", "/health"), (err: WorkerError) => {
    assert.equal(err.worker, "127.0.0.1:1");
    assert.match(err.message, /ECONNREFUSED|connection failed/);
    assert.ok(!err.message.includes(WORKER_TOKEN));
    return true;
  });
});

test("a slow worker hits the operation's deadline", async () => {
  const worker = await new MockWorker().start();
  const client = new WorkerClient({ url: worker.url, token: undefined, timeouts: { status: 20 } });
  try {
    worker.on("GET /health", () => new Promise((resolve) => setTimeout(() => resolve({ body: {} }), 500)));
    await assert.rejects(client.get("health", "/health"), (err) => err instanceof WorkerError && err.code === "timeout");
  } finally {
    await worker.stop();
  }
});

test("HTTP statuses map to the right error kinds", async () => {
  const worker = await new MockWorker().start();
  const client = new WorkerClient({ url: worker.url, token: undefined });
  try {
    for (const [status, code] of [[401, "unauthorized"], [403, "unauthorized"], [400, "rejected"], [501, "unavailable"], [503, "unavailable"], [429, "unavailable"]] as const) {
      worker.on("GET /x", () => ({ status, body: {} }));
      await assert.rejects(client.get("x", "/x"), (err) => err instanceof WorkerError && err.code === code && err.status === status, String(status));
    }
  } finally {
    await worker.stop();
  }
});
