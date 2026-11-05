/**
 * Moving an old corpus: rendered documents through the real route, to a real sidecar and index. Tokens are built at run time and never printed.
 */
import { randomBytes } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { type ApiKeyMaker, type Harness } from "./harness.ts";
import { codexMessage, codexMeta, forceIndex, SESSION_DAY } from "./ingest.ts";
import { code, folder, headers, line, SAFE, send, type Answer } from "./ingest-races.ts";

const VERSION = "2026-02-01T00:00:00Z";
const LATER = "2026-02-01T01:00:00Z";
const LATEST = "2026-02-01T02:00:00Z";
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

async function sessionTakesOver(h: Harness, key: string): Promise<void> {
  const id = "cdcdcdcd-1111-2222-3333-444444444444";
  const same = "2026-01-15_cdcdcdcd.md";
  const other = "2026-01-15_cdcdcdcd_part-009.md";
  const old = (word: string, end: number): string => `---\nsession_id: "${id}"\nmessage_end: ${end}\n---\n\n## User\n\n${word}\n`;
  await post(h, key, same, old("takeoverold", 0));
  await post(h, key, other, old("takeoverother", 1));
  const session = (extra: Record<string, string>, ...lines: string[]): Promise<Answer> => send(`${h.apiUrl}/v1/ingest/session?${new URLSearchParams({ collection: SAFE, format: "claude", sessionId: id, updated: LATER, ...extra })}`, { method: "POST", headers: headers(key, "application/x-ndjson"), body: lines.join("") });
  const cut = await session({}, line("user", "takeovernew"));
  h.check("rendered: a cut copy of a session cannot replace its migrated documents", cut.status === 409 && code(cut) === "session_shrank" && readFileSync(path.join(folder(h), same), "utf8").includes("takeoverold"), cut.text);

  const whole = await session({}, line("user", "takeovernew"), line("assistant", "takeoversecond"));
  const stored = readFileSync(path.join(folder(h), same), "utf8");
  h.check("rendered: a session takes over its file name and replaces the old text", whole.status === 202 && stored.includes("takeovernew") && !stored.includes("takeoverold"), whole.text);
  h.check("rendered: and a document under a name it no longer writes is gone, so nothing stays twice", !existsSync(path.join(folder(h), other)), whole.text);
  const again = await post(h, key, same, old("takeoverold", 0));
  h.check("rendered: a rendered copy sent afterwards is refused", again.status === 409, again.text);
  const resumed = await session({ updated: LATEST }, line("user", "takeovernew"), line("assistant", "takeoversecond"), line("user", "takeoverresumed"));
  h.check("rendered: and the session carries on under its normal rules", resumed.status === 202 && readFileSync(path.join(folder(h), same), "utf8").includes("takeoverresumed"), resumed.text);
}

async function aDeletedPathStaysDeleted(h: Harness, key: string): Promise<void> {
  const id = "cecececc-1111-2222-3333-444444444444";
  const name = "2026-01-15_cecececc.md";
  await post(h, key, name, `---\nsession_id: "${id}"\nmessage_end: 0\n---\n\nsecretish\n`);
  await send(`${h.apiUrl}/v1/documents?${new URLSearchParams({ collection: SAFE, path: name })}`, { method: "DELETE", headers: headers(key) });
  const session = await send(`${h.apiUrl}/v1/ingest/session?${new URLSearchParams({ collection: SAFE, format: "claude", sessionId: id, updated: new Date(Date.now() + 60_000).toISOString() })}`, { method: "POST", headers: headers(key, "application/x-ndjson"), body: line("user", "secretish") });
  h.check("rendered: a deleted path stays deleted when its session arrives", !existsSync(path.join(folder(h), name)) && session.text.includes("part_deleted"), session.text);
}

function uploadSession(h: Harness, key: string, id: string, updated: string, body: string, format = "claude"): Promise<Answer> {
  return send(`${h.apiUrl}/v1/ingest/session?${new URLSearchParams({ collection: SAFE, format, sessionId: id, updated })}`, { method: "POST", headers: headers(key, "application/x-ndjson"), body });
}

function exported(id: string, word: string, end: number): string {
  return `---\nsession_id: "${id}"\nmessage_end: ${end}\n---\n\n## User\n\n${word}\n`;
}

async function aTakeoverIsFinal(h: Harness, key: string): Promise<void> {
  const id = "cfcfcfcf-1111-2222-3333-444444444444";
  const kept = "2026-01-15_cfcfcfcf.md";
  const dropped = "2026-01-15_cfcfcfcf_part-009.md";
  await post(h, key, kept, exported(id, "finalstayskept", 0));
  await post(h, key, dropped, exported(id, "finalstaysdropped", 0));
  const whole = await uploadSession(h, key, id, LATER, line("user", "finalstayskept"));
  await send(`${h.apiUrl}/v1/documents?${new URLSearchParams({ collection: SAFE, path: kept })}`, { method: "DELETE", headers: headers(key) });
  await forceIndex({ h });
  h.check("rendered: a part deleted after a takeover leaves search", whole.status === 202 && !existsSync(path.join(folder(h), kept)) && (await hits(h, key, "finalstayskept")) === 0, whole.text);

  const resent = await post(h, key, kept, exported(id, "finalstayskept", 0));
  const superseded = await post(h, key, dropped, exported(id, "finalstaysdropped", 0));
  await forceIndex({ h });
  h.check("rendered: sending that export again is refused, and its text stays out", resent.status === 409 && code(resent) === "path_owned" && !existsSync(path.join(folder(h), kept)) && (await hits(h, key, "finalstayskept")) === 0, resent.text);
  h.check("rendered: so is a name the session stopped writing", superseded.status === 409 && code(superseded) === "path_owned" && !existsSync(path.join(folder(h), dropped)) && (await hits(h, key, "finalstaysdropped")) === 0, superseded.text);
}

async function aNewNameLandsBeside(h: Harness, key: string): Promise<void> {
  const id = "c0c0c0c0-1111-2222-3333-444444444444";
  const fresh = "2026-01-15_c0c0c0c0_part-007.md";
  await uploadSession(h, key, id, LATER, line("user", "beside"));
  const landed = await post(h, key, fresh, exported(id, "freshnameword", 0));
  await forceIndex({ h });
  h.check("rendered: a copy under a name never used lands beside the session", landed.status === 202 && (await hits(h, key, "freshnameword")) === 1, landed.text);
  const next = await uploadSession(h, key, id, LATEST, line("user", "beside") + line("assistant", "next"));
  await forceIndex({ h });
  h.check("rendered: and the session's next upload removes it", next.status === 202 && !existsSync(path.join(folder(h), fresh)) && (await hits(h, key, "freshnameword")) === 0, next.text);
}

async function otherFoldersStay(h: Harness, key: string): Promise<void> {
  const id = "c1c1c1c1-1111-2222-3333-444444444444";
  const root = "2026-01-15_c1c1c1c1.md";
  const fork = "forked-export/2026-01-15_c1c1c1c1.md";
  await post(h, key, root, exported(id, "forkrootword", 0));
  await post(h, key, fork, exported(id, "onlyintheforkword", 50));
  const whole = await uploadSession(h, key, id, LATER, line("user", "forknewword"));
  await forceIndex({ h });
  h.check("rendered: a session replaces the copy in its own folder", whole.status === 202 && !readFileSync(path.join(folder(h), root), "utf8").includes("forkrootword"), whole.text);
  h.check("rendered: and leaves one in another folder, searchable, even one reaching further", existsSync(path.join(folder(h), ...fork.split("/"))) && (await hits(h, key, "onlyintheforkword")) === 1, whole.text);
  h.check("rendered: which still takes its export", (await post(h, key, fork, exported(id, "onlyintheforkword", 50))).status === 202);

  const codex = "c2c2c2c2-1111-2222-3333-444444444444";
  const chunk = `${SESSION_DAY}_codex_${codex}_chunk-00009.md`;
  await post(h, key, chunk, exported(codex, "codexoldword", 0));
  await post(h, key, `forked-export/${chunk}`, exported(codex, "codexforkword", 0));
  const session = await uploadSession(h, key, codex, LATER, codexMeta(codex) + codexMessage("codexnewword"), "codex");
  await forceIndex({ h });
  h.check("rendered: a Codex session replaces chunks in its own folder", session.status === 202 && !existsSync(path.join(folder(h), chunk)) && (await hits(h, key, "codexnewword")) === 1 && (await hits(h, key, "codexoldword")) === 0, session.text);
  h.check("rendered: and leaves those in another", (await hits(h, key, "codexforkword")) === 1, session.text);
}

async function refusals(h: Harness, key: string, token: string): Promise<void> {
  const over = await post(h, key, "old-export/too-big.md", "x".repeat(MAX_DOCUMENT_BYTES + 1));
  h.check("rendered: over the cap gets 413, and nothing is written", over.status === 413 && !existsSync(path.join(folder(h), "old-export", "too-big.md")), over.status);
  const off = await post(h, key, "off.md", `---\na: "${token}"\n---\n\nbody\n`, "open");
  h.check("rendered: a collection with redaction off follows its switch and says so", off.status === 202 && field(off, "redaction") === "disabled", off.text);
  const slash = await post(h, key, "old-export/spelled.md", "first spelling\n");
  const backslash = await post(h, key, "old-export\\spelled.md", "second spelling\n");
  h.check("rendered: a backslash is refused rather than overwriting another document", slash.status === 202 && backslash.status === 400 && readFileSync(path.join(folder(h), "old-export", "spelled.md"), "utf8") === "first spelling\n", backslash.text);
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
  await sessionTakesOver(h, key);
  await aDeletedPathStaysDeleted(h, key);
  await aTakeoverIsFinal(h, key);
  await aNewNameLandsBeside(h, key);
  await otherFoldersStay(h, key);
}
