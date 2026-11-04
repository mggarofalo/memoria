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
  ["/debug/env", () => [200, { keys: Object.keys(process.env).filter((k) => k.startsWith("MEMORIA_")), args }]],
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
    const body = chunks.length > 0 ? (JSON.parse(Buffer.concat(chunks).toString("utf8")) as Body) : {};
    const route = routes.get(request.url ?? "");
    const [status, reply] = typeof route === "function" ? route(body) : ([404, { error: { message: "no such route" } }] as Reply);
    response.writeHead(status, { "content-type": "application/json" });
    response.end(JSON.stringify(reply));
  });
});
server.listen(port, "127.0.0.1");
// Never outlive the test process. A test that fails, or a mutant that breaks the
// supervisor's stop, would otherwise leave these running and use up the machine.
const parent = process.ppid;
setInterval(() => {
  if (process.ppid !== parent) process.exit(0);
}, 1000).unref();
process.on("SIGTERM", () => process.exit(0));
process.on("SIGINT", () => process.exit(0));
