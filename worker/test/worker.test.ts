import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { connect } from "node:net";
import { tmpdir } from "node:os";
import { createServer } from "node:http";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { EventEmitter } from "node:events";
import { runSmoke } from "../scripts/smoke.ts";
import { Supervisor } from "../src/supervisor.ts";
import type { MachineState } from "../src/probes.ts";
import { resolveVersion } from "../src/version.ts";
import { FAST_TIMINGS, fakeSupervisor, fixturePath, quietMachine, roleConfigs, startWorker, TOKEN, type RunningWorker } from "./helpers.ts";
/**
 * The whole worker over real HTTP, with llama-server played by a fixture launched
 * as a real child process. This proves supervision, routing, auth and validation.
 * It cannot show anything about a real model.
 */

/** Response bodies here are asserted on, not used, so loose typing is fine. */
const json = async (r: Response | Promise<Response>): Promise<any> => (await r).json();

const auth = { authorization: `Bearer ${TOKEN}`, "content-type": "application/json" };

async function waitUntil(check: () => boolean | Promise<boolean>, what: string, timeoutMs = 10_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await check()) return;
    await new Promise((r) => setTimeout(r, 20));
  }
  assert.fail(`timed out waiting for ${what}`);
}

async function withWorker(run: (w: RunningWorker) => Promise<void>, options: Parameters<typeof startWorker>[0] = {}): Promise<void> {
  const worker = await startWorker(options);
  try {
    assert.equal(await worker.supervisor.waitReady(30_000), true, "models ready");
    await run(worker);
  } finally {
    await worker.stop();
  }
}

const post = (w: RunningWorker, path: string, body: unknown, headers: Record<string, string> = auth) => fetch(`${w.url}${path}`, { method: "POST", headers, body: JSON.stringify(body) });

test("every endpoint answers, and the smoke script passes against a running worker", () =>
  withWorker(async (w) => {
    const results = await runSmoke(w.url, TOKEN);
    for (const r of results) assert.equal(r.ok, true, `${r.name}: ${r.detail}`);
    assert.deepEqual(results.map((r) => r.name), ["auth", "health", "capabilities", "embed", "rerank", "complete", "tokenize", "detokenize"]);

    const caps = await json(fetch(`${w.url}/capabilities`, { headers: auth }));
    assert.deepEqual(caps.operations, ["embed", "tokenize", "detokenize", "rerank", "complete"]);
    assert.equal(caps.models.embed, "embeddinggemma-300M-Q8_0.gguf");
    assert.equal(caps.rerankTransport, "llama-server-qmd-tokens");

    const health = await json(fetch(`${w.url}/health`, { headers: auth }));
    assert.equal(health.status, "ok");
    assert.equal(health.version, resolveVersion(path.dirname(fileURLToPath(new URL("../src/server.ts", import.meta.url)))));
    assert.equal(health.models.rerank.loaded, true);
    assert.equal(health.models.embed.state, "ready");
    assert.equal(health.inFlight, 0);
    assert.equal(health.queueDepth, 0);
    assert.deepEqual(health.vram, quietMachine.vram);
  }));

test("an embed response names the model that produced it", () =>
  withWorker(async (w) => {
    const body = await json(post(w, "/v1/embed", { texts: ["x"] }));
    assert.equal(body.model, "embeddinggemma-300M-Q8_0.gguf");
    assert.equal(body.embeddings.length, 1);
  }));

test("the smoke script reports failures rather than passing a dead worker", async () => {
  const results = await runSmoke("http://127.0.0.1:1", TOKEN);
  assert.ok(results.some((r) => !r.ok));
  await withWorker(async (w) => {
    const wrong = await runSmoke(w.url, "wrong-token-0123456789");
    assert.equal(wrong.find((r) => r.name === "health")?.ok, false);
  });
});

test("every route requires the token, in constant-format responses that do not echo it", () =>
  withWorker(async (w) => {
    for (const [method, path] of [["GET", "/health"], ["GET", "/capabilities"], ["POST", "/v1/embed"], ["POST", "/v1/rerank"], ["POST", "/v1/complete"], ["POST", "/v1/tokenize"], ["POST", "/v1/detokenize"], ["GET", "/nope"]] as const) {
      for (const headers of [{}, { authorization: "Bearer wrong-token-0123456789" }, { authorization: TOKEN }, { authorization: `Basic ${TOKEN}` }] as Array<Record<string, string>>) {
        const r = await fetch(`${w.url}${path}`, { method, headers, ...(method === "POST" ? { body: "{}" } : {}) });
        assert.equal(r.status, 401, `${method} ${path}`);
        assert.equal(r.headers.get("www-authenticate"), 'Bearer realm="memoria-worker"');
        assert.deepEqual(await json(r), { error: "unauthorized", message: "Missing or invalid bearer token" });
      }
    }
    const lower = await fetch(`${w.url}/health`, { headers: { authorization: `bearer ${TOKEN}` } });
    assert.equal(lower.status, 200);
  }));

test("the token reaches neither the logs nor llama-server", () =>
  withWorker(
    async (w) => {
      await runSmoke(w.url, TOKEN);
      await post(w, "/v1/rerank", { query: "", documents: [] });
      await fetch(`${w.url}/health`, { headers: { authorization: `Bearer ${TOKEN}x` } });
      assert.ok(w.logs.length > 0);
      for (const line of w.logs) assert.ok(!line.includes(TOKEN), line);

      const embed = w.roles.find((r) => r.role === "embed")!;
      const env = await json(fetch(`http://127.0.0.1:${embed.port}/debug/env`));
      assert.deepEqual(env.keys, [], "no MEMORIA_ settings in the child");
      assert.ok(!JSON.stringify(env.args).includes(TOKEN));
    },
    { env: { ...process.env, MEMORIA_WORKER_TOKEN: TOKEN } },
  ));

test("logs carry no document text", () =>
  withWorker(async (w) => {
    await post(w, "/v1/embed", { texts: ["PRIVATE-EMBED-TEXT"] });
    await post(w, "/v1/rerank", { query: "PRIVATE-QUERY", documents: ["PRIVATE-DOCUMENT"] });
    for (const line of w.logs) assert.ok(!line.includes("PRIVATE"), line);
  }));

test("rerank scores align with the documents, whatever order the server returns", () =>
  withWorker(async (w) => {
    const r = await post(w, "/v1/rerank", { query: "bread oven", documents: ["no match at all", "bread in the oven", "only bread"] });
    assert.equal(r.status, 200);
    const body = await json(r);
    assert.deepEqual(body.results, [{ index: 0, score: 0 }, { index: 1, score: 1 }, { index: 2, score: 0.5 }]);
    assert.equal(body.transport, "llama-server-qmd-tokens");
  }));

test("a server that returns raw logits is an error, not a score to rescale", () =>
  withWorker(
    async (w) => {
      const r = await post(w, "/v1/rerank", { query: "bread", documents: ["bread"] });
      assert.equal(r.status, 502);
      assert.equal((await json(r)).error, "rerank_score_out_of_range");
    },
    { env: { ...process.env, FAKE_RERANK_RAW_LOGITS: "1" } },
  ));

test("requests are validated before any model work", () =>
  withWorker(async (w) => {
    const cases: Array<[string, unknown]> = [
      ["/v1/embed", {}],
      ["/v1/embed", { texts: [] }],
      ["/v1/embed", { texts: [1] }],
      ["/v1/embed", { texts: new Array(257).fill("x") }],
      ["/v1/rerank", { documents: ["d"] }],
      ["/v1/rerank", { query: "q", documents: [] }],
      ["/v1/complete", {}],
      ["/v1/complete", { prompt: "p", grammar: 7 }],
      ["/v1/complete", { prompt: "p", temperature: 99 }],
      ["/v1/tokenize", { text: 3 }],
      ["/v1/detokenize", { tokens: [-1] }],
      ["/v1/detokenize", { tokens: "abc" }],
      ["/v1/embed", []],
    ];
    for (const [path, body] of cases) {
      const r = await post(w, path, body);
      assert.equal(r.status, 400, `${path} ${JSON.stringify(body).slice(0, 40)}`);
      assert.equal((await json(r)).error, "invalid_body");
    }
    const notJson = await fetch(`${w.url}/v1/embed`, { method: "POST", headers: auth, body: "{not json" });
    assert.equal(notJson.status, 400);
    assert.equal((await json(notJson)).error, "invalid_json");
    const missing = await post(w, "/v1/transcribe", {});
    assert.equal(missing.status, 404);
  }));

test("an oversized body is refused", () =>
  withWorker(
    async (w) => {
      const r = await post(w, "/v1/tokenize", { text: "x".repeat(4096) });
      assert.equal(r.status, 413);
    },
    { bodyLimitBytes: 1024 },
  ));

test("a worker reports and refuses roles it does not serve", () =>
  withWorker(
    async (w) => {
      const caps = await json(fetch(`${w.url}/capabilities`, { headers: auth }));
      assert.deepEqual(caps.operations, ["rerank"]);
      assert.equal(caps.models.embed, null);
      const r = await post(w, "/v1/embed", { texts: ["x"] });
      assert.equal(r.status, 501);
      assert.equal((await json(r)).error, "not_served");
    },
    { roles: ["rerank"] },
  ));

test("while models load the worker says so and refuses work", async () => {
  const gate = path.join(await mkdtemp(path.join(tmpdir(), "memoria-load-")), "loaded");
  const worker = await startWorker({ env: { ...process.env, FAKE_LOAD_UNTIL_FILE: gate } });
  try {
    const health = await json(fetch(`${worker.url}/health`, { headers: auth }));
    assert.equal(health.status, "starting");
    const r = await post(worker, "/v1/embed", { texts: ["x"] });
    assert.equal(r.status, 503);
    assert.equal((await json(r)).error, "model_not_ready");
    await writeFile(gate, "");
    assert.equal(await worker.supervisor.waitReady(30_000), true);
    assert.equal((await post(worker, "/v1/embed", { texts: ["x"] })).status, 200);
  } finally {
    await worker.stop();
  }
});

test("health reports busy with its reasons when the machine is overloaded", () => {
  let machine: MachineState = quietMachine;
  return withWorker(
    async (w) => {
      const read = () => json(fetch(`${w.url}/health`, { headers: auth }));
      assert.equal((await read()).status, "ok");
      machine = { ...quietMachine, loadPerCore: 2, cpuSpeedLimit: 70 };
      const busy = await read();
      assert.equal(busy.status, "busy");
      assert.deepEqual(busy.busyReasons, ["load", "thermal"]);
      // Busy steers a dispatcher; it does not stop the worker serving.
      assert.equal((await post(w, "/v1/tokenize", { text: "x" })).status, 200);
    },
    { machine: async () => machine },
  );
});

test("a crashed llama-server is restarted and service recovers", () =>
  withWorker(async (w) => {
    const rerank = w.roles.find((r) => r.role === "rerank")!;
    await fetch(`http://127.0.0.1:${rerank.port}/debug/crash`);
    await waitUntil(() => w.supervisor.status().find((s) => s.role === "rerank")!.restarts >= 1, "the restart");
    await waitUntil(() => w.supervisor.ready("rerank"), "the restarted server", 30_000);
    const r = await post(w, "/v1/rerank", { query: "x", documents: ["x"] });
    assert.equal(r.status, 200);
    assert.ok(w.logs.some((l) => l.includes("llama-server exited")));
  }));

test("a health check in flight when the server exits cannot cancel its restart", async () => {
  const roles = await roleConfigs(["embed"]);
  const children: EventEmitter[] = [];
  const spawnFake = (() => {
    const child = Object.assign(new EventEmitter(), { stderr: new EventEmitter(), kill: () => true });
    children.push(child);
    return child;
  }) as never;
  let answer = (_: Response) => {};
  const supervisor = new Supervisor({
    command: "fake",
    gpuLayers: 0,
    roles,
    modelPaths: { embed: "m.gguf" },
    log: { info: () => {}, warn: () => {}, error: () => {} },
    spawn: spawnFake,
    fetch: (() => new Promise((resolve) => (answer = resolve))) as never,
    timings: { pollMs: 1, backoffBaseMs: 10, backoffMaxMs: 10 },
  });
  supervisor.start();
  await new Promise((r) => setTimeout(r, 20)); // the check is now waiting on the fetch
  children[0]!.emit("exit", 3);
  answer(new Response("", { status: 503 })); // and comes back after the exit
  await new Promise((r) => setTimeout(r, 60));
  assert.equal(children.length, 2, "it restarted");
  children[1]!.emit("exit", 0);
  await supervisor.stop();
});

test("a health probe in flight when the server dies does not cancel the restart", async () => {
  const roles = await roleConfigs(["embed"]);
  const port = roles[0]!.port;
  let probeHeld!: () => void;
  let releaseProbe!: () => void;
  const held = new Promise<void>((resolve) => (probeHeld = resolve));
  const release = new Promise<void>((resolve) => (releaseProbe = resolve));
  let holdNextProbe = false;
  const { Supervisor } = await import("../src/supervisor.ts");
  const supervisor = new Supervisor({
    command: process.execPath,
    prefixArgs: [fixturePath],
    gpuLayers: 0,
    roles,
    modelPaths: { embed: "models/m.gguf" },
    log: { info: () => {}, warn: () => {}, error: () => {} },
    // The backoff must outlast the test's poll, so the probe lands before the restart would.
    timings: { ...FAST_TIMINGS, backoffBaseMs: 1_000 },
    env: process.env,
    fetch: async (url, init) => {
      if (!holdNextProbe) return fetch(url, init);
      holdNextProbe = false;
      probeHeld();
      await release;
      throw new Error("connection refused");
    },
  });
  supervisor.start();
  try {
    assert.equal(await supervisor.waitReady(10_000), true);
    holdNextProbe = true;
    await held;
    await fetch(`http://127.0.0.1:${port}/debug/crash`);
    await waitUntil(() => supervisor.status()[0]!.restarts === 1, "the exit");
    releaseProbe();
    await waitUntil(() => supervisor.ready("embed"), "the restart that the stale probe used to cancel");
  } finally {
    await supervisor.stop();
  }
});

test("a hung llama-server is killed and restarted", () =>
  withWorker(async (w) => {
    const generate = w.roles.find((r) => r.role === "generate")!;
    await fetch(`http://127.0.0.1:${generate.port}/debug/hang`);
    await waitUntil(() => w.supervisor.status().find((s) => s.role === "generate")!.restarts >= 1, "the kill");
    await waitUntil(() => w.supervisor.ready("generate"), "the restarted server", 30_000);
    assert.ok(w.logs.some((l) => l.includes("stopped answering")));
  }));

test("a missing llama-server binary backs off rather than crashing the worker", async () => {
  const roles = await roleConfigs(["embed"]);
  const supervisor = fakeSupervisor(roles);
  const spawnFails = new (await import("../src/supervisor.ts")).Supervisor({
    command: "/definitely/not/a/llama-server",
    gpuLayers: 0,
    roles,
    modelPaths: { embed: "m.gguf" },
    log: { info: () => {}, warn: () => {}, error: () => {} },
    timings: { pollMs: 20, backoffBaseMs: 10, backoffMaxMs: 20 },
  });
  spawnFails.start();
  await waitUntil(() => spawnFails.status()[0]!.restarts >= 2, "repeated restart attempts");
  assert.equal(spawnFails.ready("embed"), false);
  await spawnFails.stop();
  await supervisor.stop();
});

test("something else on the port is never marked ready or sent text", async () => {
  const roles = await roleConfigs(["embed"]);
  const seen: string[] = [];
  const impostor = createServer((request, response) => {
    seen.push(`${request.method} ${request.url} ${request.headers.authorization ? "keyed" : "open"}`);
    request.resume();
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({ status: "ok", model_alias: "guess", tokens: [1] }));
  });
  await new Promise<void>((resolve) => impostor.listen(roles[0]!.port, "127.0.0.1", resolve));
  const supervisor = fakeSupervisor(roles);
  supervisor.start();
  try {
    assert.equal(await supervisor.waitReady(1_500), false);
    assert.equal(supervisor.ready("embed"), false);
    assert.ok(seen.length > 0, "the check did reach it");
    assert.ok(seen.every((line) => line.startsWith("GET /props")), "no call but the identity check");
  } finally {
    await supervisor.stop();
    await new Promise((resolve) => impostor.close(resolve));
  }
});

test("a launched server takes calls only with this start's key, which is not on its command line", async () => {
  await withWorker(async (w) => {
    const child = `http://127.0.0.1:${w.roles[0]!.port}`;
    assert.equal((await fetch(`${child}/tokenize`, { method: "POST", body: "{}" })).status, 401);
    const debug = await json(fetch(`${child}/debug/env`));
    assert.equal(debug.api, true);
    const key = w.supervisor.apiKeyFor(w.roles[0]!.role)!;
    assert.ok(key.length > 20);
    assert.ok(!debug.args.join(" ").includes(key));
    assert.equal((await post(w, "/v1/embed", { texts: ["hello"] })).status, 200);
  });
});

test("llama-server's output stays out of the logs", async () => {
  await withWorker(async (w) => {
    const child = `http://127.0.0.1:${w.roles[0]!.port}`;
    await fetch(`${child}/debug/say`, { method: "POST", headers: { authorization: `Bearer ${w.supervisor.apiKeyFor(w.roles[0]!.role)!}` }, body: JSON.stringify({ text: "PRIVATE-TRANSCRIPT" }) });
    await waitUntil(() => w.logs.some((l) => l.includes("llama-server exited")), "the exit to be logged");
    assert.ok(!w.logs.join("\n").includes("PRIVATE-TRANSCRIPT"));
  });
});

test("a full queue refuses before reading the body", async () => {
  await withWorker(
    async (w) => {
      const gate = w.gates.get("embed")!;
      let release = () => {};
      const held = new Promise<void>((resolve) => (release = resolve));
      const busy = [gate.run(() => held), gate.run(() => held)];
      const response = await fetch(`${w.url}/v1/embed`, { method: "POST", headers: auth, body: JSON.stringify({ texts: ["x".repeat(500_000)] }) });
      assert.equal(response.status, 503);
      assert.equal((await response.json() as { error: string }).error, "overloaded");
      release();
      await Promise.all(busy);
    },
    { maxQueue: 0 },
  );
});

test("request bodies held in memory are capped, and released with the answer", async () => {
  await withWorker(
    async (w) => {
      const over = await fetch(`${w.url}/v1/embed`, { method: "POST", headers: auth, body: JSON.stringify({ texts: ["x".repeat(3000)] }) });
      assert.equal(over.status, 503);
      assert.equal((await over.json() as { error: string }).error, "overloaded");
      for (let i = 0; i < 5; i++) assert.equal((await post(w, "/v1/embed", { texts: ["x".repeat(1200)] })).status, 200);
    },
    { maxBufferedBytes: 2048 },
  );
});

test("an oversized grammar is refused before it reaches llama-server", async () => {
  await withWorker(async (w) => {
    const r = await post(w, "/v1/complete", { prompt: "p", grammar: `root ::= "${"a".repeat(17 * 1024)}"` });
    assert.equal(r.status, 400);
    assert.equal((await r.json() as { error: string }).error, "grammar_too_large");
  });
});

test("a batch of texts is tokenized in order, and its size is limited", async () => {
  await withWorker(async (w) => {
    const ok = await json(post(w, "/v1/tokenize-batch", { texts: ["ab", "c", ""] }));
    assert.deepEqual(ok.tokens, [Array.from("ab", (c) => c.codePointAt(0)! + 1), ["c".codePointAt(0)! + 1], []]);
    assert.equal((await post(w, "/v1/tokenize-batch", { texts: Array.from({ length: 257 }, () => "x") })).status, 400);
    assert.equal((await post(w, "/v1/tokenize-batch", { texts: [] })).status, 400);
  });
});

test("stopping the worker stops its children", async () => {
  const worker = await startWorker();
  let stopped = false;
  try {
    assert.equal(await worker.supervisor.waitReady(10_000), true);
    const port = worker.roles[0]!.port;
    assert.equal((await fetch(`http://127.0.0.1:${port}/health`)).status, 200);
    await worker.stop();
    stopped = true;
    await assert.rejects(fetch(`http://127.0.0.1:${port}/health`));
    assert.ok(worker.supervisor.status().every((s) => s.state === "stopped" && !s.loaded));
  } finally {
    // A child left running holds this file's process open, so one failure here would hang the whole run.
    if (!stopped) await worker.stop();
  }
});

test("an already-running llama-server can stand in for a launched one", async () => {
  const launched = await roleConfigs(["embed"]);
  const host = fakeSupervisor(launched);
  host.start();
  const external = [{ ...launched[0]!, externalUrl: `http://127.0.0.1:${launched[0]!.port}`, port: 1 }];
  const supervisor = fakeSupervisor(external);
  try {
    assert.equal(await host.waitReady(10_000), true);
    supervisor.start();
    assert.equal(await supervisor.waitReady(5_000), true);
    assert.equal(supervisor.baseUrlFor("embed"), `http://127.0.0.1:${launched[0]!.port}/`);
  } finally {
    await supervisor.stop();
    await host.stop();
  }
});

test("a body that is not declared JSON is refused, as a browser can send text/plain anywhere", () =>
  withWorker(async (w) => {
    const send = (headers: Record<string, string>) => fetch(`${w.url}/v1/embed`, { method: "POST", headers: { authorization: `Bearer ${TOKEN}`, ...headers }, body: JSON.stringify({ texts: ["x"] }) });
    for (const type of ["text/plain", "application/x-www-form-urlencoded", "application/jsonp", "multipart/form-data"]) {
      const refused = await send({ "content-type": type });
      assert.equal(refused.status, 415, type);
      assert.equal((await json(refused)).error, "unsupported_media_type");
    }
    assert.equal((await send({ "content-type": "application/json; charset=utf-8" })).status, 200);
    assert.equal((await send({ "content-type": "APPLICATION/JSON" })).status, 200);
  }));

test("a browser cannot reach the worker: any Origin is refused, and so is a hostile Host name", () =>
  withWorker(async (w) => {
    const raw = (headers: Record<string, string>) =>
      new Promise<string>((resolve, reject) => {
        const { port } = new URL(w.url);
        const lines = Object.entries(headers).map(([k, v]) => `${k}: ${v}\r\n`).join("");
        const socket = connect(Number(port), "127.0.0.1", () => socket.write(`GET /health HTTP/1.1\r\n${lines}Authorization: Bearer ${TOKEN}\r\nConnection: close\r\n\r\n`));
        let received = "";
        socket.on("data", (chunk) => (received += chunk.toString("utf8")));
        socket.on("end", () => resolve(received));
        socket.on("error", reject);
      });
    assert.match(await raw({ Host: "evil.example.com" }), /^HTTP\/1\.1 421 /);
    assert.match(await raw({ Host: "127.0.0.1:8383", Origin: "https://evil.example.com" }), /^HTTP\/1\.1 403 /);
    assert.match(await raw({ Host: "localhost:8383" }), /^HTTP\/1\.1 200 /);
    assert.match(await raw({ Host: "192.168.1.30:8383" }), /^HTTP\/1\.1 200 /);
  }));
