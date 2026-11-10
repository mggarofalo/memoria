import assert from "node:assert/strict";
import { test } from "node:test";
import { Inference, LlamaServers, QmdTokensRerank, UpstreamError } from "../src/inference.ts";
import { busyReasons, CachedMachine, readMachine, runCommand, systemProbes } from "../src/probes.ts";

/** Answers each llama-server route from a table, and records what the worker sent. */
function servers(replies: Record<string, unknown>): { servers: LlamaServers; sent: Array<{ path: string; body: Record<string, unknown> }> } {
  const sent: Array<{ path: string; body: Record<string, unknown> }> = [];
  const fetchFn = (async (url: URL, init: RequestInit) => {
    const route = url.pathname.replace(/^\//, "");
    sent.push({ path: route, body: JSON.parse(String(init.body)) as Record<string, unknown> });
    return new Response(JSON.stringify(replies[route] ?? {}), { status: 200 });
  }) as unknown as typeof globalThis.fetch;
  return { servers: new LlamaServers({ baseUrlFor: () => "http://127.0.0.1:1/" }, { fetch: fetchFn, timeoutMs: 1000 }), sent };
}

const invalid = (err: unknown): boolean => err instanceof UpstreamError && err.code === "upstream_invalid";

test("a completion sends only the options it was given, under llama-server's names", async () => {
  const content = { choices: [{ message: { content: "ok" } }] };
  const bare = servers({ "v1/chat/completions": content });
  assert.equal(await new Inference(bare.servers).complete({ prompt: "p" }), "ok");
  assert.deepEqual(bare.sent[0]!.body, { messages: [{ role: "user", content: "p" }], stream: false });

  const full = servers({ "v1/chat/completions": content });
  await new Inference(full.servers).complete({ prompt: "p", grammar: "g", maxTokens: 5, temperature: 0, topK: 0, topP: 0.5, repeatLastN: -1, presencePenalty: 0 });
  assert.deepEqual(full.sent[0]!.body, {
    messages: [{ role: "user", content: "p" }],
    stream: false,
    grammar: "g",
    max_tokens: 5,
    temperature: 0,
    top_k: 0,
    top_p: 0.5,
    repeat_last_n: -1,
    presence_penalty: 0,
  });
});

test("a completion with no text is an error", async () => {
  for (const reply of [{}, { choices: [] }, { choices: [{}] }, { choices: [{ message: { content: 7 } }] }]) {
    await assert.rejects(new Inference(servers({ "v1/chat/completions": reply }).servers).complete({ prompt: "p" }), invalid);
  }
});

test("malformed tokens and detokenized text are errors", async () => {
  for (const reply of [{}, { tokens: "no" }, { tokens: [1, "two"] }, { tokens: [1.5] }]) {
    await assert.rejects(new Inference(servers({ tokenize: reply }).servers).tokenize("x"), invalid);
  }
  await assert.rejects(new Inference(servers({ detokenize: { content: 1 } }).servers).detokenize([1]), invalid);
});

test("a malformed embedding response is refused, and a gap is not filled in", async () => {
  const tokenize = { tokens: [1] };
  const embed = (data: unknown) => new Inference(servers({ tokenize, "v1/embeddings": { data } }).servers).embed(["a", "b"]);
  for (const data of [
    undefined,
    [{ index: 0, embedding: [1] }],
    [{ index: 0, embedding: [1] }, { index: 2, embedding: [1] }],
    [{ index: -1, embedding: [1] }, { index: 0, embedding: [1] }],
    [{ index: 0.5, embedding: [1] }, { index: 0, embedding: [1] }],
    [{ index: "0", embedding: [1] }, { index: 1, embedding: [1] }],
    [{ index: 0, embedding: "no" }, { index: 1, embedding: [1] }],
    [{ index: 0, embedding: [1] }, { index: 0, embedding: [2] }],
  ]) {
    await assert.rejects(embed(data), invalid, JSON.stringify(data));
  }
  assert.deepEqual(await embed([{ index: 1, embedding: [2] }, { index: 0, embedding: [1] }]), [[1], [2]]);
});

test("a rerank probability must be a number from 0 to 1, inclusive", async () => {
  const score = (value: unknown) =>
    new QmdTokensRerank(servers({ tokenize: { tokens: [1] }, completion: { completion_probabilities: [{ top_probs: [{ id: 1, prob: value }] }] } }).servers).score("q", ["d"]);
  assert.deepEqual(await score(0), [0]);
  assert.deepEqual(await score(1), [0.5]);
  for (const value of [-0.001, 1.001, "0.5", null, undefined]) {
    await assert.rejects(score(value), (err: unknown) => err instanceof UpstreamError && err.code === "rerank_score_out_of_range", String(value));
  }
});

test("system probes read this machine, and a missing command is a failed probe rather than a crash", async () => {
  assert.ok(systemProbes.cores() >= 1);
  assert.ok(systemProbes.totalMemoryMb() >= systemProbes.freeMemoryMb());
  assert.ok(systemProbes.loadAverage() >= 0);
  await assert.rejects(runCommand("memoria-no-such-command", []));
  // No deadline here: how long a process takes to start is up to the machine, not the probe.
  assert.match(await runCommand(process.execPath, ["--version"], 0), /^v\d+/);
});

test("a probe that never answers is killed at its deadline and counts as failed", async () => {
  await assert.rejects(runCommand(process.execPath, ["-e", "setInterval(() => {}, 1000)"], 50), (err: NodeJS.ErrnoException & { killed?: boolean }) => err.killed === true);
});

test("the machine reading tolerates failed probes, and thresholds of zero disable a reason", async () => {
  const source = { platform: "linux" as const, run: async () => { throw new Error("absent"); }, loadAverage: () => 8, cores: () => 4, freeMemoryMb: () => 1, totalMemoryMb: () => 2 };
  const machine = await readMachine(source);
  assert.deepEqual(machine, { loadPerCore: 2, cpuSpeedLimit: null, vram: null });
  assert.deepEqual(busyReasons(machine, { busyLoadPerCore: 1, busyVramFreeMb: 100, busyOnThermal: true }), ["load"]);
  assert.deepEqual(busyReasons(machine, { busyLoadPerCore: 0, busyVramFreeMb: 0, busyOnThermal: false }), []);
  const darwin = await readMachine({ ...source, platform: "darwin" });
  assert.deepEqual(darwin, { loadPerCore: 2, cpuSpeedLimit: null, vram: { freeMb: 1, totalMb: 2, source: "unified" } });
  let reads = 0;
  const cached = new CachedMachine({ ...source, loadAverage: () => { reads++; return 0; } }, 60_000);
  await cached.read();
  await cached.read();
  assert.equal(reads, 1);
});
