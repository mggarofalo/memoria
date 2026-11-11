/**
 * Stands in for llama-server in tests, launched as a real child process. It
 * reproduces the request and response shapes the worker relies on, as documented
 * upstream. It says nothing about whether a real llama-server scores the same.
 */
import { existsSync } from "node:fs";
import { createServer } from "node:http";

const args = process.argv.slice(2);
const flag = (name: string): string | undefined => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};
const port = Number(flag("--port"));
const dimensions = Number(process.env.FAKE_EMBED_DIMENSIONS ?? "768");
const loadUntil = Date.now() + Number(process.env.FAKE_LOAD_MS ?? "0");
// Loads until this file exists, so a test decides when, whatever the machine's load.
const loadGate = process.env.FAKE_LOAD_UNTIL_FILE;
const loading = (): boolean => Date.now() < loadUntil || (loadGate !== undefined && !existsSync(loadGate));
let hung = false;
/** How this process got its socket: handed over by the test, or bound here. */
let listenedBy: "handle" | "port" = "port";

function vectorFor(text: string): number[] {
  let h = 7;
  for (const ch of text) h = (h * 31 + ch.codePointAt(0)!) % 1000;
  return Array.from({ length: dimensions }, (_, i) => ((h + i) % 97) / 97 + 0.01);
}

type Body = Record<string, any>;
type Reply = [status: number, body: unknown];

const detokenize = (tokens: number[]): string => tokens.map((t) => String.fromCodePoint(t - 1)).join("");

/** The worker's rerank backend: token arrays in, probabilities of yes and no out. */
function completion(body: Body): Reply {
  const answers = (body.prompt as number[][]).map((tokens, index) => {
    const text = detokenize(tokens);
    const query = text.slice(text.indexOf("<Query>: ") + 9, text.indexOf("\n<Document>: "));
    const doc = text.slice(text.indexOf("\n<Document>: ") + 13, text.indexOf("<|im_end|>\n<|im_start|>assistant"));
    const terms = query.toLowerCase().split(/\s+/);
    const score = terms.filter((t) => doc.toLowerCase().includes(t)).length / terms.length;
    const yes = process.env.FAKE_RERANK_RAW_LOGITS ? score * 20 - 10 : score;
    return { index, completion_probabilities: [{ top_probs: [{ id: "y".codePointAt(0)! + 1, prob: yes }, { id: "n".codePointAt(0)! + 1, prob: 1 - yes }] }] };
  });
  return [200, answers.reverse()];
}

function chat(body: Body): Reply {
  const prompt = String(body.messages?.at(-1)?.content);
  const query = prompt.replace("/no_think Expand this search query: ", "");
  return [200, { choices: [{ message: { content: body.grammar ? `lex: ${query}\nvec: ${query} explained\n` : "ok" } }] }];
}

const routes = new Map<string, (body: Body) => Reply>([
  ["/health", () => (loading() ? [503, { error: { message: "Loading model" } }] : [200, { status: "ok" }])],
  // One token per code point, offset so a token is never just the char code.
  ["/tokenize", (body) => [200, { tokens: Array.from(String(body.content), (ch) => ch.codePointAt(0)! + 1) }]],
  ["/detokenize", (body) => [200, { content: detokenize(body.tokens as number[]) }]],
  ["/v1/embeddings", (body) => [200, { data: (body.input as number[][]).map((tokens, index) => ({ index, embedding: vectorFor(detokenize(tokens)) })) }]],
  ["/completion", completion],
  ["/v1/chat/completions", chat],
  ["/props", () => (loading() ? [503, { error: { message: "Loading model" } }] : [200, { model_alias: process.env.LLAMA_ARG_ALIAS ?? "" }])],
  ["/debug/env", () => [200, { keys: Object.keys(process.env).filter((k) => k.toUpperCase().startsWith("MEMORIA_")), args, api: Boolean(process.env.LLAMA_API_KEY), listenedBy }]],
  [
    "/debug/say",
    (body) => {
      process.stderr.write(`srv prompt: ${String(body.text)}\n`);
      setTimeout(() => process.exit(3), 10);
      return [200, {}];
    },
  ],
  [
    "/debug/crash",
    () => {
      setTimeout(() => process.exit(3), 10);
      return [200, {}];
    },
  ],
  [
    "/debug/hang",
    () => {
      hung = true;
      return [200, {}];
    },
  ],
]);

const server = createServer((request, response) => {
  if (hung) return; // never answers, like a wedged process
  const chunks: Buffer[] = [];
  request.on("data", (c: Buffer) => chunks.push(c));
  request.on("end", () => {
    const finish = ([status, reply]: Reply): void => {
      response.writeHead(status, { "content-type": "application/json" });
      response.end(JSON.stringify(reply));
    };
    // As the real server: /health is open, and everything else needs the key when one is set.
    const key = process.env.LLAMA_API_KEY;
    if (key && request.url !== "/health" && !request.url?.startsWith("/debug/") && request.headers.authorization !== `Bearer ${key}`) return finish([401, { error: { message: "Invalid API Key" } }]);
    const body = chunks.length > 0 ? (JSON.parse(Buffer.concat(chunks).toString("utf8")) as Body) : {};
    const route = routes.get(request.url ?? "");
    return finish(typeof route === "function" ? route(body) : [404, { error: { message: "no such route" } }]);
  });
});
// Launched by the tests' supervisor, the port is already bound and comes over the IPC channel (handOverPort in helpers.ts). Told to bind it
// itself, or run with no channel, it binds the port as llama-server does, and fails the same way if something holds it.
if (process.send) {
  process.once("message", (message: { listen?: string }, handle?: unknown) => {
    if (message.listen === "handle" && handle) {
      listenedBy = "handle";
      server.listen(handle as Parameters<typeof server.listen>[0]);
    } else {
      server.listen(port, "127.0.0.1");
    }
    process.channel?.unref();
  });
} else {
  server.listen(port, "127.0.0.1");
}
// Never outlive the test process. A test that fails, or a mutant that breaks the
// supervisor's stop, would otherwise leave these running and use up the machine.
// The starter names itself in FAKE_LLAMA_PARENT_PID (handOverPort in helpers.ts):
// a test process that exits within the second this takes to start is already gone
// by now, and the parent seen here would be the one that adopted the orphan.
const parent = Number(process.env.FAKE_LLAMA_PARENT_PID) || process.ppid;
const checkParent = (): void => {
  if (process.ppid !== parent) process.exit(0);
};
checkParent();
setInterval(checkParent, 1000).unref();
process.on("SIGTERM", () => process.exit(0));
process.on("SIGINT", () => process.exit(0));
