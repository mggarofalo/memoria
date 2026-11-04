/**
 * Stands in for llama-server in tests, launched as a real child process. It
 * reproduces the request and response shapes the worker relies on, as documented
 * upstream. It says nothing about whether a real llama-server scores the same.
 */
import { createServer } from "node:http";

const args = process.argv.slice(2);
const flag = (name: string): string | undefined => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};
const port = Number(flag("--port"));
const dimensions = Number(process.env.FAKE_EMBED_DIMENSIONS ?? "768");
const loadUntil = Date.now() + Number(process.env.FAKE_LOAD_MS ?? "0");
let hung = false;

function vectorFor(text: string): number[] {
  let h = 7;
  for (const ch of text) h = (h * 31 + ch.codePointAt(0)!) % 1000;
  return Array.from({ length: dimensions }, (_, i) => ((h + i) % 97) / 97 + 0.01);
}

const server = createServer((request, response) => {
  if (hung) return; // never answers, like a wedged process
  const chunks: Buffer[] = [];
  request.on("data", (c: Buffer) => chunks.push(c));
  request.on("end", () => {
    const send = (status: number, body: unknown): void => {
      response.writeHead(status, { "content-type": "application/json" });
      response.end(JSON.stringify(body));
    };
    const body = chunks.length > 0 ? (JSON.parse(Buffer.concat(chunks).toString("utf8")) as Record<string, any>) : {};
    switch (request.url) {
      case "/health":
        return Date.now() < loadUntil ? send(503, { error: { message: "Loading model" } }) : send(200, { status: "ok" });
      case "/tokenize": {
        // One token per code point, offset so a token is never just the char code.
        const tokens = Array.from(String(body.content), (ch) => ch.codePointAt(0)! + 1);
        return send(200, { tokens });
      }
      case "/detokenize":
        return send(200, { content: (body.tokens as number[]).map((t) => String.fromCodePoint(t - 1)).join("") });
      case "/v1/embeddings":
        return send(200, { data: (body.input as string[]).map((text, index) => ({ index, embedding: vectorFor(text) })) });
      case "/rerank": {
        const terms = String(body.query).toLowerCase().split(/\s+/);
        const results = (body.documents as string[]).map((doc, index) => {
          const lower = doc.toLowerCase();
          const score = terms.filter((t) => lower.includes(t)).length / terms.length;
          return { index, relevance_score: process.env.FAKE_RERANK_RAW_LOGITS ? score * 20 - 10 : score };
        });
        return send(200, { results: results.reverse() });
      }
      case "/v1/chat/completions": {
        const prompt = String(body.messages?.[0]?.content);
        const query = prompt.replace("/no_think Expand this search query: ", "");
        return send(200, { choices: [{ message: { content: body.grammar ? `lex: ${query}\nvec: ${query} explained\n` : "ok" } }] });
      }
      case "/debug/env":
        return send(200, { keys: Object.keys(process.env).filter((k) => k.startsWith("MEMORIA_")), args });
      case "/debug/crash":
        send(200, {});
        return void setTimeout(() => process.exit(3), 10);
      case "/debug/hang":
        send(200, {});
        hung = true;
        return;
      default:
        return send(404, { error: { message: "no such route" } });
    }
  });
});
server.listen(port, "127.0.0.1");
process.on("SIGTERM", () => process.exit(0));
process.on("SIGINT", () => process.exit(0));
