import assert from "node:assert/strict";
import { test } from "node:test";
import { isLanAddress, loadConfig } from "../src/config.ts";
import { Inference, InvalidRequest, LlamaServers, QWEN3_RERANK_TEMPLATE, UpstreamError } from "../src/inference.ts";
import { createLogger } from "../src/log.ts";
import { findModel } from "../src/models.ts";
import { busyReasons, CachedMachine, parseNvidiaSmi, parsePmsetThermal, readMachine, type ProbeSource } from "../src/probes.ts";
import { Gate } from "../src/server.ts";
import { childEnvironment, llamaServerArgs, stderrCode } from "../src/supervisor.ts";
import { MAX_UPSTREAM_BYTES, readBounded, ResponseTooLarge } from "../src/bounded-read.ts";

const GOOD_TOKEN = "Xk29vQmB7fLp3ZtRw";

// --- config --------------------------------------------------------------------

test("defaults bind loopback and serve all three roles", () => {
  const c = loadConfig({ XDG_CACHE_HOME: "/cache" });
  assert.equal(c.host, "127.0.0.1");
  assert.equal(c.port, 8383);
  assert.equal(c.token, undefined);
  assert.deepEqual(c.roles.map((r) => r.role), ["embed", "rerank", "generate"]);
  assert.deepEqual(c.roles.map((r) => r.port), [8384, 8385, 8386]);
  assert.equal(c.modelDir.replace(/\\/g, "/"), "/cache/qmd/models");
  assert.equal(c.warmup, true);
});

test("the bind address must be loopback or a private LAN address", () => {
  for (const host of ["0.0.0.0", "::", "8.8.8.8", "172.32.0.1", "example.com", "192.169.0.1", "2001:db8::1", "fc00:evil.example.com", "10.0.0.256", "fc00::1/0", "10.1", "127.0.0.1.example.com", "fe80:evil", "172.16.0.1/8", "010.0.0.1"]) {
    assert.throws(() => loadConfig({ MEMORIA_WORKER_HOST: host, MEMORIA_WORKER_TOKEN: GOOD_TOKEN }), /MEMORIA_WORKER_HOST/, host);
    assert.equal(isLanAddress(host), false, host);
  }
  for (const host of ["127.0.0.1", "::1", "localhost", "10.0.0.5", "172.16.0.1", "172.31.255.1", "192.168.1.20", "169.254.1.1", "fd00::1", "fe80::1"]) {
    assert.equal(isLanAddress(host), true, host);
  }
});

test("an external llama-server must be a plain loopback or private URL", () => {
  for (const url of ["http://example.com:8000", "http://8.8.8.8", "http://user:pw@127.0.0.1:9000", "ftp://127.0.0.1", "not a url", "http://10.0.0.256", "http://[2001:db8::1]:80"]) {
    assert.throws(() => loadConfig({ MEMORIA_WORKER_EMBED_URL: url }), /MEMORIA_WORKER_EMBED_URL/, url);
  }
  for (const url of ["http://127.0.0.1:9000", "http://[::1]:9000", "https://192.168.1.20:9000", "http://[fd00::1]:9000"]) {
    assert.equal(loadConfig({ MEMORIA_WORKER_EMBED_URL: url }).roles[0]!.externalUrl, url);
  }
  assert.equal(loadConfig({ MEMORIA_WORKER_EMBED_URL: "http://127.0.0.1:9000", MEMORIA_WORKER_EMBED_API_KEY: "k" }).roles[0]!.externalApiKey, "k");
});

test("a LAN bind needs a token of real length", () => {
  assert.throws(() => loadConfig({ MEMORIA_WORKER_HOST: "192.168.1.20" }), /MEMORIA_WORKER_TOKEN is required/);
  assert.throws(() => loadConfig({ MEMORIA_WORKER_TOKEN: "short" }), /at least 16/);
  assert.equal(loadConfig({ MEMORIA_WORKER_HOST: "192.168.1.20", MEMORIA_WORKER_ALLOW_HTTP_ON_PRIVATE_NETWORK: "1", MEMORIA_WORKER_TOKEN: GOOD_TOKEN }).token, GOOD_TOKEN);
});

test("roles, model paths and external servers are configurable, and bad values fail", () => {
  const c = loadConfig({ MEMORIA_WORKER_ROLES: "embed, rerank", MEMORIA_WORKER_RERANK_MODEL: "/m/custom.gguf", MEMORIA_WORKER_EMBED_URL: "http://127.0.0.1:9000", MEMORIA_WORKER_EMBED_PARALLEL: "8" });
  assert.deepEqual(c.roles.map((r) => r.role), ["embed", "rerank"]);
  assert.equal(c.roles[1]!.modelFile, "custom.gguf");
  assert.equal(c.roles[0]!.externalUrl, "http://127.0.0.1:9000");
  assert.equal(c.roles[0]!.parallel, 8);
  assert.throws(() => loadConfig({ MEMORIA_WORKER_ROLES: "embed,transcribe" }), /unknown role/);
  assert.throws(() => loadConfig({ MEMORIA_WORKER_ROLES: " , " }), /at least one/);
  assert.throws(() => loadConfig({ MEMORIA_WORKER_PORT: "99999" }), /MEMORIA_WORKER_PORT/);
  assert.throws(() => loadConfig({ MEMORIA_WORKER_WARMUP: "perhaps" }), /boolean/);
});

// --- models --------------------------------------------------------------------

test("with no model set, each role serves Memoria's default file, which QMD's cache name also matches", () => {
  const files = Object.fromEntries(loadConfig({}).roles.map((r) => [r.role, r.modelFile]));
  // Changing the embed file means re-embedding every index, so it changes only on purpose.
  assert.deepEqual(files, { embed: "Qwen3-Embedding-0.6B-Q8_0.gguf", rerank: "Qwen3-Reranker-4B.Q8_0.gguf", generate: "qmd-query-expansion-1.7B-q4_k_m.gguf" });
  const cache = () => ["hf_Qwen_Qwen3-Embedding-0.6B-Q8_0.gguf", "hf_giladgd_Qwen3-Reranker-4B.Q8_0.gguf"];
  assert.match(findModel("/m", files.embed!, cache), /hf_Qwen_Qwen3-Embedding-0\.6B-Q8_0\.gguf$/);
  assert.match(findModel("/m", files.rerank!, cache), /hf_giladgd_Qwen3-Reranker-4B\.Q8_0\.gguf$/);
});

test("findModel accepts the bare name and QMD's prefixed cache name", () => {
  const list = () => ["hf_ggml-org_embeddinggemma-300M-Q8_0.gguf", "hf_tobil_qmd-query-expansion-1.7B-q4_k_m.gguf.ipull", "other.gguf"];
  assert.match(findModel("/m", "embeddinggemma-300M-Q8_0.gguf", list), /hf_ggml-org_embeddinggemma-300M-Q8_0\.gguf$/);
  assert.match(findModel("/m", "other.gguf", list), /other\.gguf$/);
  assert.throws(() => findModel("/m", "qmd-query-expansion-1.7B-q4_k_m.gguf", list), /is not in/, "a partial download does not count");
  assert.throws(() => findModel("/m", "x.gguf", () => { throw new Error("ENOENT"); }), /cannot be read/);
});

// --- llama-server arguments ----------------------------------------------------

test("llama-server flags bind loopback and size contexts like QMD", () => {
  const base = { modelFile: "m.gguf", modelPath: undefined, externalUrl: undefined, port: 9001, parallel: 2 };
  const embed = llamaServerArgs({ ...base, role: "embed" }, "/m/e.gguf", 999);
  assert.deepEqual(embed.slice(0, 12), ["--model", "/m/e.gguf", "--host", "127.0.0.1", "--port", "9001", "--n-gpu-layers", "999", "--parallel", "2", "--ctx-size", "4096"]);
  assert.ok(embed.includes("--embeddings"));
  const rerank = llamaServerArgs({ ...base, role: "rerank" }, "/m/r.gguf", 0);
  assert.equal(rerank[rerank.indexOf("--ctx-size") + 1], "8192");
  assert.equal(rerank[rerank.indexOf("--ubatch-size") + 1], "4096");
  assert.ok(!rerank.includes("--reranking"), "the rerank backend needs logits, which rank pooling takes away");
  assert.equal(rerank[rerank.indexOf("--cache-ram") + 1], "0", "the prompt cache would otherwise hold 8 GiB of documents");
  assert.ok(llamaServerArgs({ ...base, role: "generate" }, "/m/g.gguf", 0).includes("--jinja"));
});

test("the child environment never holds the worker's settings", () => {
  const env = childEnvironment({ PATH: "/bin", CUDA_VISIBLE_DEVICES: "0", MEMORIA_WORKER_TOKEN: GOOD_TOKEN, MEMORIA_WORKER_HOST: "x" });
  assert.deepEqual(env, { PATH: "/bin", CUDA_VISIBLE_DEVICES: "0" });
  assert.deepEqual(childEnvironment({ Path: "x", memoria_worker_token: GOOD_TOKEN, Memoria_Worker_Token: GOOD_TOKEN }), { Path: "x" }, "Windows keeps a variable's case");
  assert.deepEqual(childEnvironment({ LLAMA_ARG_HOST: "0.0.0.0", llama_api_key: "x" }), {}, "inherited settings must not undo the flags");
});

test("llama-server's output reduces to a fixed code", () => {
  assert.equal(stderrCode("start: couldn't bind HTTP server socket, hostname: 127.0.0.1, port: 8384"), "port_in_use");
  assert.equal(stderrCode("srv load_model: failed to load model, 'x'"), "model_load_failed");
  assert.equal(stderrCode("ggml_gallocr: failed to allocate buffer"), "out_of_memory");
  assert.equal(stderrCode("error: invalid argument: --nope"), "bad_arguments");
  assert.equal(stderrCode("slot launch: PRIVATE TEXT"), null);
});

test("responses are read only up to a limit", async () => {
  assert.equal(await readBounded(new Response("héllo")), "héllo");
  await assert.rejects(readBounded(new Response("x".repeat(100)), 10), ResponseTooLarge);
  assert.ok(MAX_UPSTREAM_BYTES >= 32 * 1024 * 1024);
});

// --- machine probes -------------------------------------------------------------

test("nvidia-smi and pmset output parse, and junk reads as nothing", () => {
  assert.deepEqual(parseNvidiaSmi("8123, 12288\n"), { freeMb: 8123, totalMb: 12288, source: "nvidia-smi" });
  assert.deepEqual(parseNvidiaSmi("100, 200\r\n300, 400\r\n"), { freeMb: 400, totalMb: 600, source: "nvidia-smi" });
  assert.equal(parseNvidiaSmi("Failed to initialize NVML"), null);
  assert.equal(parseNvidiaSmi(""), null);
  assert.equal(parsePmsetThermal("Note: No thermal warning level has been recorded\nCPU_Speed_Limit \t= 72\n"), 72);
  assert.equal(parsePmsetThermal("nothing here"), null);
});

function source(over: Partial<ProbeSource>): ProbeSource {
  return { platform: "linux", run: async () => "", loadAverage: () => 0, cores: () => 4, freeMemoryMb: () => 1000, totalMemoryMb: () => 2000, ...over };
}

test("readMachine uses each platform's sources and survives a missing tool", async () => {
  const win = await readMachine(source({ platform: "win32", loadAverage: () => 9, run: async () => "4000, 8000\n" }));
  assert.equal(win.loadPerCore, null, "Windows has no load average");
  assert.equal(win.vram?.freeMb, 4000);

  const mac = await readMachine(source({ platform: "darwin", loadAverage: () => 2, run: async () => "CPU_Speed_Limit = 60" }));
  assert.equal(mac.loadPerCore, 0.5);
  assert.equal(mac.cpuSpeedLimit, 60);
  assert.deepEqual(mac.vram, { freeMb: 1000, totalMb: 2000, source: "unified" });

  const fails = async () => { throw new Error("ENOENT"); };
  assert.equal((await readMachine(source({ run: fails }))).vram, null);
  assert.equal((await readMachine(source({ platform: "darwin", run: fails }))).cpuSpeedLimit, null);
});

test("busy reasons follow the thresholds, and a missing reading is not a reason", () => {
  const on = { busyLoadPerCore: 0.9, busyVramFreeMb: 500, busyOnThermal: true };
  const calm = { loadPerCore: 0.2, cpuSpeedLimit: 100, vram: { freeMb: 4000, totalMb: 8000, source: "unified" as const } };
  assert.deepEqual(busyReasons(calm, on), []);
  assert.deepEqual(busyReasons({ loadPerCore: null, cpuSpeedLimit: null, vram: null }, on), []);
  assert.deepEqual(busyReasons({ ...calm, loadPerCore: 1.5 }, on), ["load"]);
  assert.deepEqual(busyReasons({ ...calm, cpuSpeedLimit: 80 }, on), ["thermal"]);
  assert.deepEqual(busyReasons({ ...calm, vram: { freeMb: 100, totalMb: 8000, source: "nvidia-smi" } }, on), ["vram"]);
  const off = { busyLoadPerCore: 0, busyVramFreeMb: 0, busyOnThermal: false };
  assert.deepEqual(busyReasons({ loadPerCore: 50, cpuSpeedLimit: 10, vram: { freeMb: 0, totalMb: 8000, source: "unified" } }, off), []);
});

test("CachedMachine does not probe on every read", async () => {
  let runs = 0;
  const cached = new CachedMachine(source({ run: async () => { runs++; return "1, 2"; } }), 60_000);
  await cached.read();
  await cached.read();
  assert.equal(runs, 1);
  const uncached = new CachedMachine(source({ run: async () => { runs++; return "1, 2"; } }), 0);
  await uncached.read();
  await uncached.read();
  assert.equal(runs, 3);
});

// --- logging -------------------------------------------------------------------

test("the logger scrubs the token even if a field carries it", () => {
  const lines: string[] = [];
  const log = createLogger(GOOD_TOKEN, (l) => lines.push(l));
  log.error({ detail: `Bearer ${GOOD_TOKEN}` }, `failed with ${GOOD_TOKEN}`);
  assert.ok(!lines[0]!.includes(GOOD_TOKEN));
  assert.match(lines[0]!, /\[redacted\]/);
});

// --- gate ----------------------------------------------------------------------

test("Gate limits concurrency, queues in order, and refuses past the queue limit", async () => {
  const gate = new Gate(1, 1);
  let release = () => {};
  const held = new Promise<void>((r) => (release = r));
  const order: string[] = [];
  const first = gate.run(async () => { order.push("a"); await held; });
  const second = gate.run(async () => { order.push("b"); });
  assert.equal(gate.active, 1);
  assert.equal(gate.queued, 1);
  await assert.rejects(gate.run(async () => {}), (err: { code?: string; status?: number }) => err.code === "overloaded" && err.status === 503);
  release();
  await Promise.all([first, second]);
  assert.deepEqual(order, ["a", "b"]);
  assert.equal(gate.active, 0);
  assert.equal(gate.queued, 0);
});

test("Gate frees the slot when work fails", async () => {
  const gate = new Gate(1, 1);
  await assert.rejects(gate.run(async () => { throw new Error("boom"); }), /boom/);
  assert.equal(gate.active, 0);
  assert.equal(await gate.run(async () => 7), 7);
});

// --- inference against scripted llama-server responses ---------------------------

interface Call { path: string; body: any }

function scripted(handler: (path: string, body: any) => unknown): { inference: Inference; calls: Call[] } {
  const calls: Call[] = [];
  const fetchFn = (async (url: URL, init: RequestInit) => {
    const path = new URL(url).pathname;
    const body = JSON.parse(String(init.body));
    calls.push({ path, body });
    const out = handler(path, body);
    if (out instanceof Response) return out;
    return new Response(JSON.stringify(out), { status: 200 });
  }) as unknown as typeof globalThis.fetch;
  const routes = { baseUrlFor: (role: string) => (role === "missing" ? undefined : `http://127.0.0.1:1/${role}/`) };
  return { inference: new Inference(new LlamaServers(routes, { fetch: fetchFn, timeoutMs: 1000 })), calls };
}

const charTokens = (text: string) => ({ tokens: Array.from(text, (c) => c.codePointAt(0)!) });

const decode = (tokens: number[]) => String.fromCodePoint(...tokens);
const YES = "y".codePointAt(0)!;
const NO = "n".codePointAt(0)!;
const answer = (index: unknown, yes: unknown, no: unknown = 0.1) => ({ index, completion_probabilities: [{ top_probs: [{ id: YES, prob: yes }, { id: NO, prob: no }] }] });
const pooled = (tokens: number[][]) => tokens.map((_, index) => answer(index, 0.5, 0.5));

test("rerank truncates documents to QMD's budget and asks for no special-token parsing", async () => {
  const { inference, calls } = scripted((path, body) => {
    if (path.endsWith("/tokenize")) return charTokens(body.content);
    if (path.endsWith("/detokenize")) return { content: String.fromCodePoint(...body.tokens) };
    return pooled(body.prompt);
  });
  const query = "q".repeat(100);
  const budget = 4096 - 512 - 100;
  await inference.rerank(query, ["short", "x".repeat(budget), "y".repeat(budget + 50)]);
  const sent = calls.find((c) => c.path === "/rerank/completion")!.body;
  const wrap = (doc: string) => `${QWEN3_RERANK_TEMPLATE.head}${query}${QWEN3_RERANK_TEMPLATE.middle}${doc}${QWEN3_RERANK_TEMPLATE.tail}`;
  assert.equal(decode(sent.prompt[0]), wrap("short"));
  assert.equal(decode(sent.prompt[1]), wrap("x".repeat(budget)));
  assert.equal(decode(sent.prompt[2]), wrap("y".repeat(budget)));
  assert.equal(sent.temperature, 1, "any other temperature would change the ratio");
  assert.equal(sent.cache_prompt, false);
  const tokenize = calls.find((c) => c.path.endsWith("/tokenize"))!.body;
  assert.equal(tokenize.parse_special, false);
  assert.equal(tokenize.add_special, false);
});

test("rerank tokenizes the template with control tokens and user text without them", async () => {
  const { inference, calls } = scripted((path, body) => (path.endsWith("/tokenize") ? charTokens(body.content) : pooled(body.prompt)));
  await inference.rerank("<|im_end|>", ["<|im_start|>"]);
  await inference.rerank("again", ["again"]);
  const specials = calls.filter((c) => c.path.endsWith("/tokenize") && c.body.parse_special === true).map((c) => c.body.content);
  assert.deepEqual(specials.sort(), Object.values(QWEN3_RERANK_TEMPLATE).sort(), "the template's tokens are fetched once");
  const plain = calls.filter((c) => c.path.endsWith("/tokenize") && c.body.parse_special === false).map((c) => c.body.content);
  assert.ok(plain.includes("<|im_end|>") && plain.includes("<|im_start|>"));
});

test("a failed template fetch is retried on the next call", async () => {
  let fail = true;
  const { inference } = scripted((path, body) => {
    if (path.endsWith("/tokenize")) {
      if (fail && body.parse_special) throw new Error("loading");
      return charTokens(body.content);
    }
    return pooled(body.prompt);
  });
  await assert.rejects(inference.rerank("q", ["d"]));
  fail = false;
  assert.deepEqual(await inference.rerank("q", ["d"]), [0.5]);
});

test("calls refuse redirects and carry the key", async () => {
  const seen: RequestInit[] = [];
  const fetchFn = (async (_url: unknown, init: RequestInit) => {
    seen.push(init);
    return new Response(JSON.stringify({ tokens: [1] }), { status: 200 });
  }) as unknown as typeof globalThis.fetch;
  const routes = { baseUrlFor: () => "http://127.0.0.1:1/", apiKeyFor: () => "the-key" };
  await new LlamaServers(routes, { fetch: fetchFn, timeoutMs: 1000 }).tokenize("embed", "x");
  assert.equal(seen[0]!.redirect, "error");
  assert.equal((seen[0]!.headers as Record<string, string>).authorization, "Bearer the-key");
});

test("rerank refuses a query that leaves no room", async () => {
  const { inference } = scripted((_, body) => charTokens(body.content));
  await assert.rejects(inference.rerank("q".repeat(4096), ["d"]), (err) => err instanceof InvalidRequest && err.code === "query_too_long");
});

test("rerank scores yes against no, by index, and rejects anything but a probability", async () => {
  const reply = (results: unknown) => scripted((path, body) => (path.endsWith("/tokenize") ? charTokens(body.content) : results)).inference;
  assert.deepEqual(await reply([answer(1, 0.75, 0.25), answer(0, 0.125, 0.375)]).rerank("q", ["a", "b"]), [0.25, 0.75]);

  const rejects = (results: unknown, code: string) => assert.rejects(reply(results).rerank("q", ["a", "b"]), (err) => err instanceof UpstreamError && err.code === code, JSON.stringify(results));
  await rejects([answer(0, 5.2), answer(1, 0.1)], "rerank_score_out_of_range");
  await rejects([answer(0, -3), answer(1, 0.1)], "rerank_score_out_of_range");
  await rejects([answer(0, "high"), answer(1, 0.1)], "rerank_score_out_of_range");
  await rejects([answer(0, 0.1)], "upstream_invalid");
  await rejects([answer(0, 0.1), answer(0, 0.2)], "upstream_invalid");
  await rejects([answer(0, 0.1), answer(2, 0.2)], "upstream_invalid");
  await rejects([{ index: 0 }, answer(1, 0.1)], "upstream_invalid");
  await rejects([{ index: 0, completion_probabilities: [{ top_probs: [{ id: 1, prob: 0.5 }] }] }, answer(1, 0.1)], "upstream_invalid");
});

test("a yes or no outside the list is bounded by the lowest listed probability", async () => {
  const only = (id: number) => [{ index: 0, completion_probabilities: [{ top_probs: [{ id, prob: 0.9 }, { id: 1, prob: 0.01 }] }] }];
  const score = (results: unknown) => scripted((path, body) => (path.endsWith("/tokenize") ? charTokens(body.content) : results)).inference.rerank("q", ["a"]);
  assert.deepEqual(await score(only(NO)), [0.01 / 0.91]);
  assert.deepEqual(await score(only(YES)), [0.9 / 0.91]);
});

test("one document may come back as a single object", async () => {
  const { inference } = scripted((path, body) => (path.endsWith("/tokenize") ? charTokens(body.content) : { completion_probabilities: [{ top_probs: [{ id: YES, prob: 0.75 }, { id: NO, prob: 0.25 }] }] }));
  assert.deepEqual(await inference.rerank("q", ["a"]), [0.75]);
});

test("embed shortens over-long text as QMD does and aligns vectors by index", async () => {
  const { inference, calls } = scripted((path, body) => {
    if (path.endsWith("/tokenize")) return charTokens(body.content);
    if (path.endsWith("/detokenize")) return { content: String.fromCodePoint(...body.tokens) };
    return { data: body.input.map((_: string, index: number) => ({ index, embedding: [index + 1] })).reverse() };
  });
  const vectors = await inference.embed(["a", "b".repeat(2049), "c".repeat(2048)]);
  assert.deepEqual(vectors, [[1], [2], [3]]);
  const input = calls.find((c) => c.path.endsWith("/embeddings"))!.body.input;
  assert.equal(input[1].length, 2044);
  assert.equal(input[2].length, 2048);
  const tokenizing = calls.filter((c) => c.path.endsWith("/tokenize")).map((c) => c.body);
  assert.ok(tokenizing.every((b) => b.parse_special === false), "text that looks like a control token stays text");
  assert.equal(tokenizing.filter((b) => b.add_special === true).length, 3, "what is sent carries the model's BOS and EOS");
});

test("complete forwards grammar and sampling through the chat route", async () => {
  const { inference, calls } = scripted(() => ({ choices: [{ message: { content: "lex: x\n" } }] }));
  const out = await inference.complete({ prompt: "p", system: "s", grammar: "root ::= x", maxTokens: 600, temperature: 0.7, topK: 20, topP: 0.8, minP: 0, repeatLastN: 64, presencePenalty: 0.5 });
  assert.equal(out, "lex: x\n");
  assert.deepEqual(calls[0], {
    path: "/generate/v1/chat/completions",
    body: { messages: [{ role: "system", content: "s" }, { role: "user", content: "p" }], stream: false, grammar: "root ::= x", max_tokens: 600, temperature: 0.7, top_k: 20, top_p: 0.8, min_p: 0, repeat_last_n: 64, presence_penalty: 0.5 },
  });
});

test("llama-server rejecting the input becomes a refusal, repeating none of its text, and a broken server does not", async () => {
  for (const status of [400, 413, 422]) {
    const { inference } = scripted(() => new Response("the prompt was PRIVATE TEXT", { status }));
    await assert.rejects(inference.tokenize("x"), (err) => err instanceof InvalidRequest && err.code === "upstream_rejected" && !err.message.includes("PRIVATE"), String(status));
  }
  for (const status of [404, 429, 500, 503]) {
    const { inference } = scripted(() => new Response("x", { status }));
    await assert.rejects(inference.tokenize("x"), (err) => err instanceof UpstreamError && err.code === "upstream_error", String(status));
  }
});

test("upstream failures become fixed codes that repeat none of the upstream's text", async () => {
  const secretish = "upstream said: the prompt was PRIVATE TEXT";
  const bad = scripted(() => new Response(secretish, { status: 500 })).inference;
  await assert.rejects(bad.tokenize("x"), (err) => err instanceof UpstreamError && err.code === "upstream_error" && !err.message.includes("PRIVATE"));
  const notJson = scripted(() => new Response(secretish, { status: 200 })).inference;
  await assert.rejects(notJson.tokenize("x"), (err) => err instanceof UpstreamError && err.code === "upstream_invalid" && !err.message.includes("PRIVATE"));
  const noRoute = new Inference(new LlamaServers({ baseUrlFor: () => undefined }, { timeoutMs: 100 }));
  await assert.rejects(noRoute.embed(["x"]), (err) => err instanceof UpstreamError && err.code === "not_served");
  const down = new Inference(new LlamaServers({ baseUrlFor: () => "http://127.0.0.1:1/" }, { fetch: (async () => { throw new TypeError("fetch failed"); }) as unknown as typeof globalThis.fetch, timeoutMs: 100 }));
  await assert.rejects(down.tokenize("x"), (err) => err instanceof UpstreamError && err.code === "upstream_unavailable");
});

test("a worker token cannot be a placeholder or a pattern, and random ones pass", () => {
  for (const token of ["a".repeat(16), "1234567890123456", "qwertyuiopasdfgh", "abcdefghabcdefgh", "0123456789abcdef", "changeme-changeme-123"]) {
    assert.throws(() => loadConfig({ MEMORIA_WORKER_TOKEN: token }), /too weak/, token);
  }
  assert.equal(loadConfig({ MEMORIA_WORKER_TOKEN: GOOD_TOKEN }).token, GOOD_TOKEN);
});

test("binding beyond loopback needs an explicit opt-in to plain http, and host names can be listed", () => {
  const lan = { MEMORIA_WORKER_HOST: "192.168.1.20", MEMORIA_WORKER_TOKEN: GOOD_TOKEN };
  assert.throws(() => loadConfig(lan), /in clear.*MEMORIA_WORKER_ALLOW_HTTP_ON_PRIVATE_NETWORK=1/);
  assert.throws(() => loadConfig({ ...lan, MEMORIA_WORKER_ALLOW_HTTP_ON_PRIVATE_NETWORK: "0" }), /in clear/);
  assert.equal(loadConfig({ ...lan, MEMORIA_WORKER_ALLOW_HTTP_ON_PRIVATE_NETWORK: "yes" }).host, "192.168.1.20");
  assert.equal(loadConfig({}).host, "127.0.0.1");
  assert.deepEqual([...loadConfig({ MEMORIA_WORKER_ALLOWED_HOSTS: " GPU.lan, ," }).allowedHosts], ["localhost", "gpu.lan"]);
});
