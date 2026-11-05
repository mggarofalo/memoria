/**
 * Moving an old corpus: rendered documents through the real route, to a real sidecar and index. Tokens are built at run time and never printed.
 */
import { randomBytes } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { type ApiKeyMaker, type Harness } from "./harness.ts";
import { forceIndex } from "./ingest.ts";
import { code, folder, headers, SAFE, send, type Answer } from "./ingest-races.ts";

const VERSION = "2026-02-01T00:00:00Z";
const MAX_DOCUMENT_BYTES = 2 * 1024 * 1024;

function post(h: Harness, key: string, relative: string, text: string, collection = SAFE): Promise<Answer> {
  const query = new URLSearchParams({ collection, path: relative, updated: VERSION });
  return send(`${h.apiUrl}/v1/ingest/rendered?${query}`, { method: "POST", headers: headers(key, "text/markdown"), body: text });
}

function field(answer: Answer, name: string): unknown {
  return (JSON.parse(answer.text) as Record<string, unknown>)[name];
}

async function hits(h: Harness, key: string, word: string): Promise<number> {
  const answer = await send(`${h.apiUrl}/v1/search?query=${word}&rerank=false&limit=20&collection=${SAFE}`, { headers: headers(key) });
  return (JSON.parse(answer.text) as { results: unknown[] }).results.length;
}

async function refusals(h: Harness, key: string, token: string): Promise<void> {
  const over = await post(h, key, "old-export/too-big.md", "x".repeat(MAX_DOCUMENT_BYTES + 1));
  h.check("rendered: over the cap gets 413, and nothing is written", over.status === 413 && !existsSync(path.join(folder(h), "old-export", "too-big.md")), over.status);
  const off = await post(h, key, "off.md", `---\na: "${token}"\n---\n\nbody\n`, "open");
  h.check("rendered: a collection with redaction off follows its switch and says so", off.status === 202 && field(off, "redaction") === "disabled", off.text);
  const traversal = await post(h, key, "../outside.md", "body\n");
  h.check("rendered: a path out of the collection is refused", traversal.status === 400, traversal.status);
  const closed = await post(h, key, "x.md", "body\n", "closed");
  h.check("rendered: a collection nobody enabled is refused", closed.status === 409 && code(closed) === "ingest_not_enabled", closed.text);
}

/** Runs against a server started with INGEST_SERVER. */
export async function ingestRendered(h: Harness, keys: ApiKeyMaker): Promise<void> {
  const key = await keys("rendered", ["ingest", "read"]);
  const token = h.remember("gh" + "p_" + randomBytes(27).toString("base64url").replace(/[-_]/g, "a").padEnd(36, "b").slice(0, 36));
  const relative = "old-export/2024-03-01_abcdef01.md";
  const file = path.join(folder(h), ...relative.split("/"));
  const text = `---\nsession_id: "abcdef01-rendered"\nproject: "renderedproject"\ndate: "2024-03-01"\ngit_remote: "${token}"\n---\n\n## User\n\nrenderedword and ${token}\n`;

  const first = await post(h, key, relative, text);
  const stored = existsSync(file) ? readFileSync(file, "utf8") : "";
  h.check("rendered: a document lands at its existing path, frontmatter kept", first.status === 202 && field(first, "path") === relative && stored.startsWith('---\nsession_id: "abcdef01-rendered"\nproject: "renderedproject"\n'), first.text);
  h.check("rendered: a credential in the frontmatter and the body is gone, and counted", !stored.includes(token) && Object.keys(field(first, "redactions") as object).length > 0, stored.length);
  h.check("rendered: the sidecar read its frontmatter", field(first, "frontmatter") === "read", first.text);

  const again = await post(h, key, relative, text);
  h.check("rendered: sending it again overwrites with the same result", again.status === 202 && field(again, "created") === false && readFileSync(file, "utf8") === stored, again.text);
  const status = await send(`${h.apiUrl}/v1/ingest/rendered?${new URLSearchParams({ collection: SAFE, path: relative })}`, { headers: headers(key) });
  h.check("rendered: the ledger answers by path", status.status === 200 && field(status, "state") === "complete", status.text);

  const tooLong = await post(h, key, "old-export/long-frontmatter.md", `---\nfiles_touched: ["${"x".repeat(9000)}"]\n---\n\nlongfrontmatterword\n`);
  h.check("rendered: frontmatter past the window is stored whole and reported", tooLong.status === 202 && field(tooLong, "frontmatter") === "past_window", tooLong.text.slice(0, 200));

  await forceIndex({ h });
  h.check("rendered: the document is searchable", (await hits(h, key, "renderedword")) === 1);
  h.check("rendered: and so is the one with frontmatter past the window", (await hits(h, key, "longfrontmatterword")) === 1);
  await refusals(h, key, token);
}
