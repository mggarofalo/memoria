/**
 * `memoria sources` against the real ingest routes, on fixture folders in the driver's
 * private directory. Every folder here is made by this file: nothing points at a real corpus.
 */
import { mkdirSync, rmSync, symlinkSync, utimesSync, writeFileSync } from "node:fs";
import path from "node:path";
import { issueKey, parse } from "./auth-helpers.ts";
import { type CliResult, type Harness } from "./harness.ts";
import { forceIndex, SAFE } from "./ingest.ts";

const SOURCE = "notes-e2e";
const DOCUMENTS = "/documents";
const OLD_TIME = new Date("2020-01-01T00:00:00Z");
/** Inside the hour the server allows ahead of its clock. */
const NEWER_BY_MS = 30 * 60 * 1000;

interface Context {
  h: Harness;
  admin: string;
  writer: string;
  reader: string;
  folder: string;
  secret: string;
}

/** Built at run time, so no scanner mistakes this file for a leak. */
function credential(): string {
  return "gh" + "p_" + "s".repeat(18) + "t".repeat(18);
}

function write(c: Context, name: string, text: string): string {
  const file = path.join(c.folder, name);
  mkdirSync(path.dirname(file), { recursive: true });
  writeFileSync(file, text);
  return file;
}

function sync(c: Context, ...args: string[]): CliResult {
  return c.h.memoria(["sources", "sync", ...args], { key: c.writer });
}

function bodies(c: Context, from: number): string[] {
  return c.h.proxied(from).filter((call) => call.url === DOCUMENTS && call.method === "POST").map((call) => call.body ?? "");
}

/** Whether the sidecar was asked to store a file's text. Each fixture holds a word of its own. */
function stored(c: Context, from: number, word: string): boolean {
  return bodies(c, from).some((body) => body.includes(word));
}

async function hits(c: Context, word: string): Promise<number> {
  const answer = await c.h.request({ path: `/v1/search?query=${word}&rerank=false&limit=20&collection=${SAFE}`, key: c.reader });
  return parse<{ results: unknown[] }>(answer).results.length;
}

function configure(c: Context): void {
  const added = c.h.memoria(["sources", "add", SOURCE, "--path", c.folder, "--collection", SAFE]);
  c.h.check("sources add configures a folder, with no key and no request", added.code === 0 && added.err.includes("Nothing has been sent"), added.out + added.err);
  const listed = c.h.memoria(["sources", "list"]);
  c.h.check("sources list shows the folder and its collection, with no key", listed.code === 0 && listed.out.includes(SOURCE) && listed.out.includes(SAFE) && listed.out.includes("never"), listed.out + listed.err);
}

function firstRunIsDry(c: Context): void {
  const from = c.h.proxiedCount();
  const dry = c.h.memoria(["sources", "sync"]);
  c.h.check("sources sync: the first run reports what it would send, with no key", dry.code === 0 && dry.out.includes("would send first.md (new)") && dry.err.includes("--apply"), dry.out + dry.err);
  const again = sync(c);
  c.h.check("sources sync: a dry run records nothing, so the next is a first run too", again.code === 0 && again.out.includes("would send first.md (new)"), again.out + again.err);
  // The marker after it shows this record would have held them.
  c.h.check("sources sync: a dry run reaches nothing", bodies(c, from).length === 0, c.h.proxied(from));
  c.h.check("sources sync: a dry run prints no text from the files", !(dry.out + dry.err + again.out + again.err).includes(c.secret));
}

/** The real sidecar goes away after one document, so what is resumed is what really did not arrive. */
async function resumes(c: Context): Promise<void> {
  const from = c.h.proxiedCount();
  await c.h.planProxy({ kind: "failDocuments", after: 1 });
  const stopped = sync(c, "--apply");
  await c.h.planProxy({ kind: "failDocuments", after: null });
  c.h.check("sources sync: an unavailable backend stops the run, exit 5, and says the place is saved", stopped.code === 5 && stopped.err.includes("1 sent and 2 not reached") && stopped.err.includes("sync again"), stopped.out + stopped.err);
  c.h.check("sources sync: it stopped at the first refusal", bodies(c, from).length === 2, bodies(c, from).length);

  const resume = c.h.proxiedCount();
  const finished = sync(c);
  c.h.check("sources sync: the next run sends without --apply, and only what did not arrive", finished.code === 0 && !stored(c, resume, "alphaword") && stored(c, resume, "betaword") && stored(c, resume, "gammaword") && bodies(c, resume).length === 2, { out: finished.out + finished.err, sent: bodies(c, resume).length });

  await forceIndex(c);
  c.h.check("sources sync: documents from before and after the stop are all searchable", (await hits(c, "alphaword")) === 1 && (await hits(c, "betaword")) === 1 && (await hits(c, "gammaword")) === 1);
}

function redacts(c: Context, from: number): void {
  const stored = bodies(c, from).join("\n");
  c.h.check("sources sync: the secret never reached the sidecar, and its document did", stored.includes("alphaword") && !stored.includes(c.secret), stored.length);
}

function unchangedNeedsNothing(c: Context): void {
  const from = c.h.proxiedCount();
  const idle = c.h.memoria(["sources", "sync"]);
  c.h.check("sources sync: with nothing changed it needs no key and sends nothing", idle.code === 0 && bodies(c, from).length === 0 && idle.out.includes("sync"), idle.out + idle.err);
}

async function changes(c: Context): Promise<void> {
  const from = c.h.proxiedCount();
  const edited = write(c, "first.md", "# First\n\nepsilonword replaces the text\n");
  const sent = sync(c);
  c.h.check("sources sync: an edit sends that file alone", sent.code === 0 && stored(c, from, "epsilonword") && bodies(c, from).length === 1, sent.out + sent.err);

  // The server refuses a version older than it holds, so this proves the version moved on.
  const older = c.h.proxiedCount();
  write(c, "first.md", "# First\n\nzetaword from a restored backup\n");
  utimesSync(edited, OLD_TIME, OLD_TIME);
  const restored = sync(c);
  c.h.check("sources sync: a change carrying an old time is stored, not refused as stale", restored.code === 0 && bodies(c, older).length === 1, restored.out + restored.err);
  await forceIndex(c);
  c.h.check("sources sync: the restored text is what is searchable", (await hits(c, "zetaword")) === 1 && (await hits(c, "epsilonword")) === 0);
}

async function neverDeletes(c: Context): Promise<void> {
  rmSync(path.join(c.folder, "second.md"));
  const from = c.h.proxiedCount();
  const gone = sync(c, "--format", "json");
  const report = (JSON.parse(gone.out) as Array<{ missingLocally: number }>)[0];
  const deletes = c.h.proxied(from).filter((call) => call.method === "DELETE");
  c.h.check("sources sync: a file gone locally is counted, and nothing is deleted", gone.code === 0 && report?.missingLocally === 1 && deletes.length === 0, gone.out + gone.err);
  await forceIndex(c);
  c.h.check("sources sync: that document is still searchable", (await hits(c, "betaword")) === 1);
}

/** Another writer's version, ahead of anything this run can reach, so the refusal does not depend on how fast the machine is. */
async function perFileRefusal(c: Context): Promise<void> {
  const newer = new Date(Date.now() + NEWER_BY_MS).toISOString();
  const other = await c.h.request({ method: "POST", path: "/v1/documents", key: c.writer, body: { collection: SAFE, source: SOURCE, sourceId: "first.md", updated: newer, body: "from another machine" } });
  write(c, "first.md", "# First\n\nthetaword from this machine\n");
  write(c, "fourth.md", "# Fourth\n\niotaword\n");
  const from = c.h.proxiedCount();
  const mixed = sync(c);
  c.h.check("sources sync: a file older than the server's version is refused, and named", other.status === 202 && mixed.code === 1 && mixed.err.includes("first.md was not sent") && mixed.err.includes("1 file(s) were not sent"), mixed.out + mixed.err);
  c.h.check("sources sync: the refused file stored nothing, and did not stop the others", stored(c, from, "iotaword") && !stored(c, from, "thetaword"), bodies(c, from).length);
}

async function needsTheScope(c: Context): Promise<void> {
  const readOnly = await issueKey(c.h, c.admin, { label: "sources-reader", scopes: ["read"], collections: [SAFE] });
  write(c, "fifth.md", "# Fifth\n\nkappaword\n");
  const from = c.h.proxiedCount();
  const refused = c.h.memoria(["sources", "sync"], { key: readOnly.token });
  c.h.check("sources sync: a key with no ingest scope stops at a real 403, exit 3", refused.code === 3 && bodies(c, from).length === 0, refused.out + refused.err);
}

function linksStayOut(c: Context): void {
  const outside = path.join(c.h.privatePath("outside.md"));
  writeFileSync(outside, "lambdaword outside the folder\n");
  try {
    symlinkSync(outside, path.join(c.folder, "link.md"));
  } catch {
    console.log("NOT RUN sources sync: a link out of the folder (this host cannot make links)");
    return;
  }
  const from = c.h.proxiedCount();
  const run = sync(c);
  const sent = bodies(c, from).join("\n");
  c.h.check("sources sync: a link out of the folder is named and not sent, while a real file is", run.err.includes("left out link.md") && sent.includes("kappaword") && !sent.includes("lambdaword"), run.out + run.err);
}

/** Runs against a server started with INGEST_SERVER. */
export async function sourcesSync(h: Harness, admin: string): Promise<void> {
  const writer = await issueKey(h, admin, { label: "sources-writer", scopes: ["ingest"], collections: [SAFE] });
  const reader = await issueKey(h, admin, { label: "sources-search", scopes: ["read"], collections: [SAFE] });
  const c: Context = { h, admin, writer: writer.token, reader: reader.token, folder: h.privatePath("source-folder"), secret: credential() };
  h.remember(c.secret);
  write(c, "first.md", `# First\n\nalphaword the key is ${c.secret} here\n`);
  write(c, "second.md", "# Second\n\nbetaword\n");
  write(c, "sub/third.md", "# Third\n\ngammaword\n");
  write(c, ".hidden.md", "# Hidden\n\nmuword\n");

  configure(c);
  firstRunIsDry(c);
  const from = h.proxiedCount();
  await resumes(c);
  redacts(c, from);
  unchangedNeedsNothing(c);
  await changes(c);
  await neverDeletes(c);
  await perFileRefusal(c);
  await needsTheScope(c);
  linksStayOut(c);
  h.check("sources sync: a hidden file is never sent", (await hits(c, "muword")) === 0);
}
