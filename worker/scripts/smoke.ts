/**
 * Exercise every endpoint of a running worker and report what each returned.
 *
 *   MEMORIA_WORKER_URL=http://192.168.1.20:8383 MEMORIA_WORKER_TOKEN=... node scripts/smoke.ts
 *
 * Exits non-zero if any check fails. Sends only the short fixed strings below.
 */
import { pathToFileURL } from "node:url";

export interface SmokeResult {
  name: string;
  ok: boolean;
  detail: string;
}

export async function runSmoke(baseUrl: string, token: string | undefined): Promise<SmokeResult[]> {
  const base = baseUrl.endsWith("/") ? baseUrl : `${baseUrl}/`;
  const headers = (auth: boolean): Record<string, string> => ({
    "content-type": "application/json",
    ...(auth && token !== undefined ? { authorization: `Bearer ${token}` } : {}),
  });
  const results: SmokeResult[] = [];

  const check = async (name: string, run: () => Promise<string>): Promise<void> => {
    const started = Date.now();
    try {
      results.push({ name, ok: true, detail: `${await run()} (${Date.now() - started} ms)` });
    } catch (err) {
      results.push({ name, ok: false, detail: err instanceof Error ? err.message : "failed" });
    }
  };
  const call = async (path: string, body?: unknown): Promise<Record<string, unknown>> => {
    const response = await fetch(new URL(path, base), {
      method: body === undefined ? "GET" : "POST",
      headers: headers(true),
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    const parsed = (await response.json()) as Record<string, unknown>;
    if (!response.ok) throw new Error(`HTTP ${response.status}: ${String(parsed.error)}`);
    return parsed;
  };
  const need = (condition: boolean, message: string): void => {
    if (!condition) throw new Error(message);
  };

  await check("auth", async () => {
    if (token === undefined) return "skipped: no token configured";
    const response = await fetch(new URL("capabilities", base), { headers: headers(false) });
    await response.body?.cancel();
    need(response.status === 401, `expected 401 without a token, got ${response.status}`);
    return "rejects a missing token";
  });
  await check("health", async () => {
    const body = await call("health");
    need(typeof body.status === "string", "no status");
    return `status=${String(body.status)} inFlight=${String(body.inFlight)} queueDepth=${String(body.queueDepth)} vram=${JSON.stringify(body.vram)}`;
  });
  let operations: string[] = [];
  await check("capabilities", async () => {
    const body = await call("capabilities");
    need(Array.isArray(body.operations), "no operations");
    operations = body.operations as string[];
    return `operations=${operations.join(",")} rerankTransport=${String(body.rerankTransport)}`;
  });
  const whenServed = async (operation: string, run: () => Promise<string>): Promise<void> => {
    if (operations.includes(operation)) await check(operation, run);
    else results.push({ name: operation, ok: true, detail: "skipped: not served by this worker" });
  };
  await whenServed("embed", async () => {
    const body = await call("v1/embed", { texts: ["task: search result | query: memoria smoke test"] });
    const vectors = body.embeddings as number[][];
    need(vectors?.length === 1 && vectors[0]!.length > 0, "no vector");
    return `dimensions=${vectors[0]!.length}`;
  });
  await whenServed("rerank", async () => {
    const body = await call("v1/rerank", { query: "how do I bake bread", documents: ["Knead the dough and bake it until golden.", "The stock market closed lower on Tuesday."] });
    const results = body.results as Array<{ index: number; score: number }>;
    need(results?.length === 2, "wrong result count");
    const score = (i: number) => results.find((r) => r.index === i)!.score;
    return `relevant=${score(0).toFixed(4)} irrelevant=${score(1).toFixed(4)}${score(0) > score(1) ? "" : " WARNING: order looks wrong"}`;
  });
  await whenServed("complete", async () => {
    const grammar = 'root ::= "lex: " [a-z ]+ "\\n"';
    const body = await call("v1/complete", { prompt: "/no_think Expand this search query: bread", grammar, maxTokens: 32, temperature: 0.7 });
    need(typeof body.text === "string" && body.text.startsWith("lex: "), "output does not follow the grammar");
    return `chars=${(body.text as string).length}`;
  });
  await whenServed("tokenize", async () => {
    const body = await call("v1/tokenize", { text: "memoria smoke test" });
    need(Array.isArray(body.tokens) && (body.tokens as number[]).length > 0, "no tokens");
    return `tokens=${(body.tokens as number[]).length}`;
  });
  await whenServed("detokenize", async () => {
    const { tokens } = await call("v1/tokenize", { text: "memoria smoke test" });
    const body = await call("v1/detokenize", { tokens });
    need(typeof body.text === "string" && (body.text as string).includes("memoria"), "round trip lost the text");
    return "round trip ok";
  });
  return results;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  const url = process.env.MEMORIA_WORKER_URL?.trim();
  if (!url) {
    console.error("Set MEMORIA_WORKER_URL (and MEMORIA_WORKER_TOKEN if the worker has one).");
    process.exit(2);
  }
  const results = await runSmoke(url, process.env.MEMORIA_WORKER_TOKEN?.trim() || undefined);
  for (const r of results) console.log(`${r.ok ? "PASS" : "FAIL"}  ${r.name}: ${r.detail}`);
  process.exit(results.every((r) => r.ok) ? 0 : 1);
}
