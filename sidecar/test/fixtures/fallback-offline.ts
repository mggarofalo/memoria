/**
 * Run as its own process, with the network blocked at every level the SDK's model
 * loader could use and a temp XDG_CACHE_HOME. Opens a remote backend whose only worker
 * is down and with the CPU fallback on, asks for a query vector, and reports what tried
 * to leave the machine and whether the SDK's model loader was reached. Never touches
 * the real model cache, and never loads a model.
 *
 * argv: <dbPath>. Writes one JSON line to stdout.
 */
import net from "node:net";
import tls from "node:tls";

const attempts: string[] = [];
const blocked = (what: string) => (target: unknown) => {
  // The dead worker is on loopback. Everything else is a download attempt.
  const text = JSON.stringify(target) ?? String(target);
  if (!/127\.0\.0\.1/.test(text)) attempts.push(`${what}: ${text}`);
  throw new Error("the network is blocked in this test");
};
globalThis.fetch = (async (input: unknown) => {
  const url = String(input);
  if (!url.startsWith("http://127.0.0.1")) attempts.push(`fetch: ${url}`);
  throw new TypeError("fetch failed");
}) as typeof globalThis.fetch;
net.connect = blocked("net.connect") as never;
net.createConnection = blocked("net.createConnection") as never;
tls.connect = blocked("tls.connect") as never;

// The SDK's model loader. Reaching it is what would start a download, so stand in for it.
const loaderCalls: string[] = [];
const sdkLlm = (await import(new URL("./llm.js", import.meta.resolve("@tobilu/qmd")).href)) as { LlamaCpp: { prototype: Record<string, unknown> } };
for (const method of ["embed", "embedBatch", "tokenize", "detokenize"]) {
  sdkLlm.LlamaCpp.prototype[method] = async () => {
    loaderCalls.push(method);
    throw new Error("the model loader was reached");
  };
}

const { openBackend } = await import("../../src/store.ts");

const backend = await openBackend({
  dbPath: process.argv[2]!,
  keepWarm: false,
  llm: { mode: "remote", workers: [{ name: "down", url: "http://127.0.0.1:9", token: undefined, capabilities: ["embed", "rerank", "generate"], priority: 1, maxConcurrent: 1 }], embedDimensions: 8, fallback: true, statusPollMs: 0 },
});
// Directly, because a search would skip vectors altogether on an index holding none.
const [vector] = await backend.llm.embedBatch(["AuthenticationHandler"]);
await backend.close();
console.log(JSON.stringify({ attempts, loaderCalls, vector: vector ?? null }));
process.exit(0);
