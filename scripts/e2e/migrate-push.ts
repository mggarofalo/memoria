/**
 * `memoria migrate push` against a real API and sidecar, on fixtures this file makes in the driver's private directory. Between the CLI and
 * the API is a proxy that loses answers and stands in for a server having a bad moment. Nothing here points at a real setup.
 */
import { createHash, randomBytes } from "node:crypto";
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, utimesSync, writeFileSync } from "node:fs";
import { createServer, request, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import path from "node:path";
import { issueKey } from "./auth-helpers.ts";
import { pause, redactionCheckDll, repo, type CliResult, type Harness } from "./harness.ts";
import { forceIndex } from "./ingest.ts";

const TARGET = "migrated";
const SECOND_TARGET = "migrated-resume";
const REFUSING_TARGET = "migrated-refusing";
const KILLED_TARGET = "migrated-killed";
const RULES_TARGET = "migrated-rules";
const DECOMPOSED_TARGET = "migrated-decomposed";
const CREDENTIAL_TARGET = "migrated-credential";
const OLDER_TARGET = "migrated-older";
const CLOSED = "closed";
const FILLED = "filled";
const FILE_TIME = new Date("2025-06-01T12:00:00.123Z");
const MAX_DOCUMENT_BYTES = 2 * 1024 * 1024;
const ROUTE = "/v1/ingest/rendered";

/** Each is what the proxy does with one POST to the route, in order. Past the list it forwards. */
type Move = "forward" | "drop-answer" | "drop-request" | "502" | "busy" | "in-progress";

interface Faults {
  plan: Move[];
  /** How many POSTs it forwarded, and how many of those the server answered. */
  seen: number;
  /** Paths it was asked to send, in order. */
  paths: string[];
  /** The `updated` sent for each path, as a re-send must repeat it. */
  updated: Map<string, Set<string>>;
}

interface Summary {
  selected: number;
  sent: number;
  alreadySent: number;
  overCap: number;
  notReached: number;
  refused: Record<string, number>;
  frontmatterIssues: Record<string, number>;
}

function refusal(response: ServerResponse, status: number, code: string, retryAfter?: string): void {
  if (retryAfter) response.setHeader("Retry-After", retryAfter);
  response.setHeader("Content-Type", "application/problem+json");
  response.statusCode = status;
  response.end(JSON.stringify({ title: "refused", code }));
}

function note(faults: Faults, incoming: IncomingMessage): void {
  const url = new URL(incoming.url ?? "/", "http://x");
  const name = url.searchParams.get("path") ?? "";
  faults.paths.push(name);
  const versions = faults.updated.get(name) ?? new Set<string>();
  versions.add(url.searchParams.get("updated") ?? "");
  faults.updated.set(name, versions);
}

function forward(api: URL, incoming: IncomingMessage, outgoing: ServerResponse, dropAnswer: boolean): void {
  const upstream = request({ host: api.hostname, port: api.port, path: incoming.url, method: incoming.method, headers: incoming.headers }, (answer) => {
    if (dropAnswer) {
      // The write landed, and the client never hears so.
      answer.resume();
      answer.once("end", () => incoming.socket.destroy());
      return;
    }
    outgoing.writeHead(answer.statusCode ?? 502, answer.headers);
    answer.pipe(outgoing);
  });
  upstream.once("error", () => incoming.socket.destroy());
  incoming.pipe(upstream);
}

function act(api: URL, faults: Faults, move: Move, incoming: IncomingMessage, outgoing: ServerResponse): void {
  if (move === "forward" || move === "drop-answer") return forward(api, incoming, outgoing, move === "drop-answer");
  incoming.resume();
  incoming.once("end", () => {
    if (move === "drop-request") incoming.socket.destroy();
    if (move === "502") refusal(outgoing, 502, "bad_gateway");
    if (move === "busy") refusal(outgoing, 503, "ingest_busy", "1");
    if (move === "in-progress") refusal(outgoing, 409, "ingest_in_progress", "1");
  });
}

async function startFaultProxy(apiUrl: string, faults: Faults): Promise<{ url: string; server: Server }> {
  const api = new URL(apiUrl);
  const server = createServer((incoming, outgoing) => {
    const isPush = incoming.method === "POST" && (incoming.url ?? "").startsWith(ROUTE);
    if (isPush) {
      note(faults, incoming);
      faults.seen++;
    }
    act(api, faults, (isPush ? faults.plan.shift() : undefined) ?? "forward", incoming, outgoing);
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  return { url: `http://127.0.0.1:${(server.address() as AddressInfo).port}`, server };
}

function freshFaults(plan: Move[] = []): Faults {
  return { plan, seen: 0, paths: [], updated: new Map() };
}

function write(root: string, name: string, text: string): void {
  const file = path.join(root, ...name.split("/"));
  mkdirSync(path.dirname(file), { recursive: true });
  writeFileSync(file, text);
  utimesSync(file, FILE_TIME, FILE_TIME);
}

interface Setup {
  h: Harness;
  key: string;
  config: string;
  root: string;
}

function setup(h: Harness, key: string, name: string, target: string, files: Record<string, string>): Setup {
  const root = h.privatePath(`push-${name}`);
  for (const [file, text] of Object.entries(files)) write(root, file, text);
  const config = h.privatePath(`push-${name}.yml`);
  writeFileSync(config, `collections:\n  ${target}:\n    path: ${JSON.stringify(root)}\n    pattern: "**/*.md"\n    ignore: ["skip/**"]\n`);
  return { h, key, config, root };
}

function push(s: Setup, url: string, ...extra: string[]): CliResult {
  return s.h.memoria(["migrate", "push", "--qmd-config", s.config, "--api-url", url, ...extra], { key: s.key });
}

/** The proxy lives in this process, so the CLI must not block it. */
function pushThrough(s: Setup, url: string): Promise<CliResult> {
  return s.h.memoriaAsync(["migrate", "push", "--qmd-config", s.config, "--api-url", url, "--apply", "--format", "json"], s.key);
}

function rowOf(run: CliResult): Summary & { report: string } {
  const result = JSON.parse(run.out || "{}") as { collections?: Summary[]; reportFile?: string };
  return { ...(result.collections?.[0] ?? ({} as Summary)), report: result.reportFile ?? "" };
}

function storedFiles(h: Harness, target: string): string[] {
  const root = path.join(h.dir, "corpus", target);
  return existsSync(root) ? (readdirSync(root, { recursive: true, withFileTypes: true }).filter((entry) => entry.isFile()).map((entry) => path.relative(root, path.join(entry.parentPath, entry.name)).replaceAll("\\", "/")).sort()) : [];
}

/** A hash of every source, with its time, to show a run left them alone. */
function fingerprint(root: string): string {
  const hash = createHash("sha256");
  for (const entry of readdirSync(root, { recursive: true, withFileTypes: true }).filter((item) => item.isFile()).sort((a, b) => a.name.localeCompare(b.name))) {
    const file = path.join(entry.parentPath, entry.name);
    hash.update(file).update(readFileSync(file)).update(String(statSync(file).mtimeMs));
  }
  return hash.digest("hex");
}

function many(count: number, prefix: string): Record<string, string> {
  return Object.fromEntries(Array.from({ length: count }, (_, index) => [`${prefix}/doc-${index}.md`, `---\ndate: "2024-03-01"\n---\n\n${prefix} word${index} text\n`]));
}

async function status(h: Harness, key: string, target: string, relative: string): Promise<{ state?: string; updated?: string }> {
  const response = await fetch(`${h.apiUrl}${ROUTE}?${new URLSearchParams({ collection: target, path: relative })}`, { headers: { authorization: `Bearer ${key}` } });
  return (await response.json()) as { state?: string; updated?: string };
}

async function dryRunAndSend(h: Harness, key: string): Promise<void> {
  const token = h.remember("gh" + "p_" + randomBytes(27).toString("base64url").replace(/[-_]/g, "a").padEnd(36, "b").slice(0, 36));
  const s = setup(h, key, "first", TARGET, {
    "a.md": `---\nproject: "pushproject"\ndate: "2024-03-01"\n---\n\npushedword and ${token}\n`,
    "my dir/b+c &d.md": "---\ndate: \"2024-03-02\"\n---\n\nspacedword\n",
    "café/文.md": "---\ndate: \"2024-03-03\"\n---\n\nwideword\n",
    "long/front.md": `---\nfiles_touched: ["${"x".repeat(9000)}"]\n---\n\nlongword\n`,
    "skip/ignored.md": "ignoredword\n",
    "node_modules/ignored.md": "ignoredword\n",
  });
  writeFileSync(path.join(s.root, "index.sqlite"), "not a database, and nothing should open it");
  const before = fingerprint(s.root);
  const dry = h.memoria(["migrate", "push", "--qmd-config", s.config, "--format", "json"], { noUrl: true });
  h.check("push: without --apply it is a dry run, with no key or server, and says what it would send", dry.code === 0 && rowOf(dry).selected === 4 && storedFiles(h, TARGET).length === 0, dry.out + dry.err);

  const plan = h.memoria(["migrate", "plan", "--qmd-config", s.config, "--format", "json"], { noUrl: true });
  const planned = (JSON.parse(plan.out || "{}") as { collections?: Array<{ files: number }> }).collections?.[0]?.files;
  h.check("push: selects what plan counts", planned === rowOf(dry).selected, { planned, selected: rowOf(dry).selected });

  const sent = push(s, h.apiUrl, "--apply", "--format", "json");
  const row = rowOf(sent);
  h.check("push: --apply stores what was selected, at the same paths", sent.code === 0 && row.sent === 4 && storedFiles(h, TARGET).join() === ["a.md", "café/文.md", "long/front.md", "my dir/b+c &d.md"].sort().join(), sent.out + sent.err + storedFiles(h, TARGET).join());
  h.check("push: frontmatter the server cannot use is counted, and does not fail the run", row.frontmatterIssues?.["past_window"] === 1, sent.out);
  const stored = readFileSync(path.join(h.dir, "corpus", TARGET, "a.md"), "utf8");
  h.check("push: the server's redaction applied to what it stored", !stored.includes(token) && stored.includes("pushedword"), stored.length);
  h.check("push: sources, and the index beside them, are left as they were", fingerprint(s.root) === before);
  const ledger = await status(h, key, TARGET, "my dir/b+c &d.md");
  h.check("push: the ledger holds the path, with the file's time", ledger.state === "complete" && new Date(ledger.updated ?? 0).getTime() === FILE_TIME.getTime(), ledger);
  const again = push(s, h.apiUrl, "--apply", "--format", "json");
  h.check("push: a second run sends nothing, and says everything is already sent", again.code === 0 && rowOf(again).sent === 0 && rowOf(again).alreadySent === 4, again.out + again.err);
  await forceIndex({ h });
  const hits = await fetch(`${h.apiUrl}/v1/search?query=spacedword&rerank=false&limit=20&collection=${TARGET}`, { headers: { authorization: `Bearer ${key}` } });
  h.check("push: what it stored is searchable, once", ((await hits.json()) as { results: unknown[] }).results.length === 1);
  report(h, s, row.report);
}

function report(h: Harness, s: Setup, file: string): void {
  const data = readFileSync(file, "utf8");
  h.check("push: the local report names a frontmatter problem", data.includes("long/front.md") && data.includes("past_window"), data);
  const state = readdirSync(path.dirname(file)).filter((name) => !name.endsWith("-report.json")).map((name) => readFileSync(path.join(path.dirname(file), name), "utf8")).join();
  h.check("push: the progress file holds paths and progress, and no text", state.includes("a.md") && !state.includes("pushedword") && !state.includes(s.root), state.slice(0, 200));
}

/** The rules a plan copies, against the real thing: the same cases its Go test reads go to a real API here. */
async function pathRules(h: Harness, key: string): Promise<void> {
  const cases = JSON.parse(readFileSync(path.join(repo, "cli", "internal", "migrate", "testdata", "path-rules.json"), "utf8")) as Array<{ path: string; reason: string; decomposed?: boolean }>;
  const differ: string[] = [];
  for (const item of cases) {
    const stored = await postPath(h, key, item.decomposed ? item.path.normalize("NFC") : item.path);
    if ((item.reason === "") !== (stored === 202)) differ.push(`${JSON.stringify(item.path)}: ${stored}, plan says ${item.reason || "fine"}`);
    if (item.decomposed && (await postPath(h, key, item.path)) !== 400) differ.push(`${JSON.stringify(item.path)} as it stands was not refused`);
  }
  h.check(`push: plan's path rules and the real server agree on ${cases.length} names`, differ.length === 0, differ);
  await postPath(h, key, "conflict/Note.md");
  const conflict = await postPath(h, key, "conflict/note.md");
  h.check("push: names differing only in case get a conflict, as plan predicts", conflict === 409, conflict);
}

async function postPath(h: Harness, key: string, relative: string): Promise<number> {
  const query = new URLSearchParams({ collection: RULES_TARGET, path: relative, updated: FILE_TIME.toISOString() });
  const answer = await fetch(`${h.apiUrl}${ROUTE}?${query}`, { method: "POST", headers: { authorization: `Bearer ${key}`, "content-type": "text/markdown" }, body: "---\ndate: \"2024-03-01\"\n---\n\ntext\n" });
  await answer.arrayBuffer();
  return answer.status;
}

/** Decomposed names are stored composed, and a re-send stays the same document. Only some filesystems keep them. */
function decomposed(h: Harness, key: string): void {
  const name = "cafe\u0301-decomposed.md";
  const s = setup(h, key, "decomposed", DECOMPOSED_TARGET, { [name]: "---\ndate: \"2024-03-01\"\n---\n\ntext\n" });
  if (!readdirSync(s.root).includes(name)) {
    console.log("NOT RUN push: a decomposed name (this filesystem composed it)");
    return;
  }
  const dry = h.memoria(["migrate", "plan", "--qmd-config", s.config, "--format", "json"], { noUrl: true });
  h.check("push: plan says a decomposed name will be stored composed", dry.code === 0 && (JSON.parse(dry.out) as { collections: Array<{ decomposedNames?: number }> }).collections[0]?.decomposedNames === 1, dry.out + dry.err);
  const first = push(s, h.apiUrl, "--apply", "--format", "json");
  const again = push(s, h.apiUrl, "--apply", "--ignore-state", "--format", "json");
  h.check("push: a decomposed name is stored composed, and a re-send is the same document", first.code === 0 && again.code === 0 && storedFiles(h, DECOMPOSED_TARGET).join() === name.normalize("NFC"), first.out + first.err + again.err + storedFiles(h, DECOMPOSED_TARGET).join());
}

function planCounts(h: Harness, key: string): void {
  const s = setup(h, key, "unstorable", "unused-by-plan", { "good.md": "text\n", "x~1.md": "text\n", "aux.md": "text\n", "Vendor/a.md": "text\n" });
  const run = h.memoria(["migrate", "plan", "--qmd-config", s.config, "--format", "json"], { noUrl: true });
  const row = (JSON.parse(run.out || "{}") as { collections?: Array<{ unstorable?: Record<string, number> }> }).collections?.[0]?.unstorable ?? {};
  h.check("plan: counts what can never be stored, by reason, and exits 1", run.code === 1 && row["windows_short_name"] === 1 && row["windows_device_name"] === 1 && row["folder_qmd_excludes"] === 1 && run.err.includes("3 selected"), run.out + run.err);
  h.check("plan: names none of them", !(run.out + run.err).includes("x~1"), run.out + run.err);
  const accepted = h.memoria(["migrate", "plan", "--qmd-config", s.config, "--accept-skips"], { noUrl: true });
  h.check("plan: --accept-skips does not excuse them", accepted.code === 1, accepted.err);
}

/** A name only the server's own rules can judge. The token is built here, and neither reaches a summary. */
function credentialNames(h: Harness, key: string): void {
  const token = h.remember("gh" + "p_" + randomBytes(27).toString("base64url").replace(/[-_]/g, "a").padEnd(36, "b").slice(0, 36));
  const s = setup(h, key, "credential-name", CREDENTIAL_TARGET, { [`${token}.md`]: "---\ndate: \"2024-03-01\"\n---\n\ntext\n", "good.md": "---\ndate: \"2024-03-01\"\n---\n\ntext\n" });
  const report = h.memoria(["migrate", "report", "--qmd-config", s.config, "--tool", redactionCheckDll(), "--no-gitleaks", "--format", "json"], { noUrl: true });
  const row = (JSON.parse(report.out || "[{}]") as Array<{ credentialNames?: number }>)[0];
  h.check("report: a file name that looks like a credential is counted, and exits 1", report.code === 1 && row?.credentialNames === 1, report.out + report.err);
  const pushed = push(s, h.apiUrl, "--apply", "--format", "json");
  h.check("push: the server refuses that name, and the summary repeats neither", pushed.code === 1 && Object.values(rowOf(pushed).refused ?? {}).length === 1 && rowOf(pushed).sent === 1 && !(pushed.out + pushed.err).includes(token), pushed.out + pushed.err);
}

/** A copy restored to an older time after a newer one was sent. */
function olderCopy(h: Harness, key: string): void {
  const s = setup(h, key, "older", OLDER_TARGET, { "a.md": "---\ndate: \"2024-03-01\"\n---\n\nnewercopy\n" });
  const first = push(s, h.apiUrl, "--apply", "--format", "json");
  const file = path.join(s.root, "a.md");
  writeFileSync(file, "---\ndate: \"2024-03-01\"\n---\n\nolder\n");
  utimesSync(file, new Date("2020-01-01T00:00:00Z"), new Date("2020-01-01T00:00:00Z"));
  const second = push(s, h.apiUrl, "--apply", "--format", "json");
  const stored = readFileSync(path.join(h.dir, "corpus", OLDER_TARGET, "a.md"), "utf8");
  h.check("push: an older copy is not sent, is reported, and the newer one stays", first.code === 0 && second.code === 1 && rowOf(second).refused?.["older_than_sent"] === 1 && stored.includes("newercopy"), second.out + stored.slice(0, 80));
}

/** The moves that lose an answer, and the ones a server says itself, on real writes. */
async function lostAnswers(h: Harness, key: string): Promise<void> {
  const files = many(12, "lost");
  const s = setup(h, key, "lost", SECOND_TARGET, files);
  const faults = freshFaults(["drop-answer", "drop-request", "502", "busy", "in-progress", "drop-answer", "forward", "drop-answer"]);
  const proxy = await startFaultProxy(h.apiUrl, faults);
  try {
    const run = await pushThrough(s, proxy.url);
    const row = rowOf(run);
    h.check("push: lost answers, a bad gateway, a busy server and a write in progress are all recovered from", run.code === 0 && row.sent === 12 && row.notReached === 0, run.out + run.err);
    h.check("push: every document is stored exactly once", storedFiles(h, SECOND_TARGET).length === 12, storedFiles(h, SECOND_TARGET));
    h.check("push: and what was lost was really sent again", faults.seen > 12, faults.seen);
    h.check("push: a re-send repeats the same updated", [...faults.updated.values()].every((versions) => versions.size === 1), faults.seen);
    h.check("push: no file name reached the summary", !(run.out + run.err).includes("doc-"), run.out + run.err);
  } finally {
    proxy.server.close();
  }
}

/** A kill in the middle of sending, at a point the proxy chooses, and then a run to the end. */
async function killed(h: Harness, key: string): Promise<void> {
  const files = many(40, "killed");
  const s = setup(h, key, "killed", KILLED_TARGET, files);
  const faults = freshFaults();
  const proxy = await startFaultProxy(h.apiUrl, faults);
  try {
    const child = h.startMemoria(["migrate", "push", "--qmd-config", s.config, "--api-url", proxy.url, "--apply"], { MEMORIA_API_KEY: key });
    const exit = new Promise((resolve) => child.once("exit", resolve));
    const started = Date.now();
    while (faults.seen < 15 && Date.now() - started < 60_000) await pause(5);
    child.kill("SIGKILL");
    await exit;
    const partway = storedFiles(h, KILLED_TARGET).length;
    h.check("push: the kill came partway through", partway >= 1 && partway < 40, partway);
    const run = await pushThrough(s, proxy.url);
    const row = rowOf(run);
    h.check("push: a run after a kill ends with everything sent", run.code === 0 && row.sent + row.alreadySent === 40 && row.notReached === 0, run.out + run.err);
    h.check("push: and nothing duplicated", storedFiles(h, KILLED_TARGET).length === 40, storedFiles(h, KILLED_TARGET).length);
    h.check("push: only what was not recorded went again", faults.seen < 40 + 15 + 4, faults.seen);
  } finally {
    proxy.server.close();
  }
}

function refusals(h: Harness, key: string): void {
  const big = "x".repeat(MAX_DOCUMENT_BYTES + 1);
  const s = setup(h, key, "refusing", REFUSING_TARGET, { "good.md": "---\ndate: \"2024-03-01\"\n---\n\nfine\n", "secretname-big.md": big, "secretname-binary.md": "ok" });
  writeFileSync(path.join(s.root, "secretname-binary.md"), Buffer.from([0xff, 0xfe, 0xfd]));
  utimesSync(path.join(s.root, "secretname-binary.md"), FILE_TIME, FILE_TIME);
  const first = push(s, h.apiUrl, "--apply", "--format", "json");
  const row = rowOf(first);
  h.check("push: over the cap and refused are counted apart, the good one is stored, and it exits 1", first.code === 1 && row.overCap === 1 && Object.values(row.refused ?? {}).reduce((a, b) => a + b, 0) === 1 && row.sent === 1, first.out + first.err);
  h.check("push: a refusal is named only in the local report", !(first.out + first.err).includes("secretname") && readFileSync(row.report, "utf8").includes("secretname-big.md"), first.out + first.err);
  const accepted = push(s, h.apiUrl, "--apply", "--accept-over-cap", "--format", "json");
  h.check("push: accepting over-cap does not excuse a refusal", accepted.code === 1, accepted.out + accepted.err);
  const table = push(s, h.apiUrl, "--apply");
  h.check("push: the summary says plainly that not everything was stored", table.out.includes("NOT everything was stored") && table.out.includes("OVER CAP"), table.out);
}

/** The test held back from the first plan: a target with files in it is refused, and nothing is written. */
function stops(h: Harness, key: string, narrow: string): void {
  const filled = setup(h, key, "to-filled", FILLED, { "new.md": "---\ndate: \"2024-03-01\"\n---\n\nnewword\n" });
  const before = storedFiles(h, FILLED).join();
  const run = push(filled, h.apiUrl, "--apply");
  h.check("push: a target that is not empty is refused, clearly, and nothing is written", run.code !== 0 && run.err.includes("cannot be opened") && storedFiles(h, FILLED).join() === before, run.err);
  const closed = setup(h, key, "to-closed", CLOSED, { "new.md": "newword\n" });
  const refused = push(closed, h.apiUrl, "--apply");
  h.check("push: a collection nobody enabled stops the run", refused.code !== 0 && refused.err.includes("Nothing else was sent"), refused.err);
  const wrongKey = setup(h, narrow, "to-forbidden", TARGET, { "x.md": "x\n" });
  const forbidden = push(wrongKey, h.apiUrl, "--apply");
  h.check("push: a key without the ingest scope stops the run", forbidden.code === 3 && forbidden.err.includes("forbidden"), forbidden.err);
}

/** Runs against a server started with INGEST_SERVER, where these targets are opened and empty. */
export async function migratePush(h: Harness, admin: string): Promise<void> {
  const collections = [TARGET, SECOND_TARGET, REFUSING_TARGET, KILLED_TARGET, RULES_TARGET, DECOMPOSED_TARGET, CREDENTIAL_TARGET, OLDER_TARGET, FILLED, CLOSED];
  // A key has a budget of requests a minute, and this should not spend its time waiting out a window. A real push does, and goes at that rate.
  const fresh = async (label: string): Promise<string> => (await issueKey(h, admin, { label, scopes: ["ingest", "read"], collections })).token;
  await dryRunAndSend(h, await fresh("push"));
  await pathRules(h, await fresh("push-rules"));
  const other = await fresh("push-other");
  planCounts(h, other);
  decomposed(h, other);
  credentialNames(h, other);
  olderCopy(h, other);
  await lostAnswers(h, await fresh("push-lost"));
  await killed(h, await fresh("push-killed"));
  refusals(h, await fresh("push-refusals"));
  stops(h, await fresh("push-stops"), (await issueKey(h, admin, { label: "push-reader", scopes: ["read"], collections })).token);
}
