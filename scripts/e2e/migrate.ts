/**
 * `memoria migrate plan` and `report`, on a QMD config and collections this file makes in the
 * driver's private directory. Nothing here points at a real setup. The redaction check is the
 * real tool, run with no gitleaks, as it is not in CI.
 */
import { spawn, spawnSync } from "node:child_process";
import { mkdirSync, readdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { pause, redactionCheckDll, repo, type Harness } from "./harness.ts";

interface PlanRow {
  name: string;
  target: string;
  files: number;
  bytes: number;
}

interface ReportRow {
  name: string;
  files: number;
  changed: number;
  refused: number;
  replacements: Record<string, number>;
  filesByRule: Record<string, number>;
  gitleaksRan: boolean;
}

/** Built at run time, so no scanner mistakes this file for a leak. */
function credential(): string {
  return "gh" + "p_" + "m".repeat(18) + "n".repeat(18);
}

function write(root: string, name: string, text: string): void {
  const file = path.join(root, name);
  mkdirSync(path.dirname(file), { recursive: true });
  writeFileSync(file, text);
}

function makeSetup(h: Harness, secret: string): { config: string; codex: string; notes: string } {
  const notes = h.privatePath("migrate-notes");
  const clean = h.privatePath("migrate-clean");
  const codex = h.privatePath("migrate-codex");
  write(notes, "secretfilename.md", `# Notes\n\nthe key is ${secret} here\n`);
  write(notes, "plain.md", "# Plain\n\nnothing here\n");
  write(notes, "node_modules/excluded.md", `excluded ${secret}\n`);
  write(notes, ".hidden/hidden.md", `hidden ${secret}\n`);
  write(clean, "only.md", "# Only\n");
  write(codex, "2026/one.jsonl", "{}\n");
  const config = h.privatePath("migrate-index.yml");
  writeFileSync(config, `collections:\n  notes:\n    path: ${JSON.stringify(notes)}\n  clean:\n    path: ${JSON.stringify(clean)}\n`);
  return { config, codex, notes };
}

interface PlanResult {
  collections?: PlanRow[];
  rawCodexSessions?: { files: number };
}

/** "name:files:target" for each collection, which says all a check needs in one string. */
function planSummary(result: PlanResult): string {
  return (result.collections ?? []).map((row) => `${row.name}:${row.files}:${row.target}`).sort().join(" ");
}

function plan(h: Harness, config: string, codex: string): void {
  const run = h.memoria(["migrate", "plan", "--qmd-config", config, "--target", "clean=archive", "--codex-sessions", codex, "--format", "json"]);
  const result = JSON.parse(run.out || "{}") as PlanResult;
  h.check("migrate plan: counts what QMD would index and names each target, with no key or server", run.code === 0 && planSummary(result) === "clean:1:archive notes:2:notes", run.out + run.err);
  h.check("migrate plan: counts raw Codex sessions", result.rawCodexSessions?.files === 1, run.out);
  const none = h.memoria(["migrate", "plan"]);
  h.check("migrate plan: has no default location", none.code === 2 && none.err.includes("no default"), none.out + none.err);
}

function rowsOf(out: string): { notes: ReportRow | undefined; clean: ReportRow | undefined } {
  const rows = JSON.parse(out || "[]") as ReportRow[];
  return { notes: rows.find((item) => item.name === "notes"), clean: rows.find((item) => item.name === "clean") };
}

function reportJson(h: Harness, config: string, tool: string): string {
  const json = h.memoria(["migrate", "report", "--qmd-config", config, "--tool", tool, "--no-gitleaks", "--format", "json"]);
  const { notes, clean } = rowsOf(json.out);
  const replaced = Object.keys(notes?.replacements ?? {}).length > 0;
  h.check("migrate report: the real rules ran over exactly the plan's files", json.code === 0 && notes?.files === 2 && notes.changed === 1 && replaced && clean?.changed === 0, json.out + json.err);
  h.check("migrate report: says gitleaks did not run", notes?.gitleaksRan === false, json.out);
  return json.out + json.err;
}

/** `scripts/redaction-report.sh` shows the tool's output as it is, and the CLI would hide a leak from it. */
function toolAlone(h: Harness, tool: string, notes: string, secret: string): void {
  const run = spawnSync("dotnet", [tool, "--no-gitleaks", notes], { encoding: "utf8", timeout: 120_000 });
  const all = run.stdout + run.stderr;
  // Given a folder, it reads every Markdown file in it, which is why the CLI gives it a list.
  h.check("redaction check: printed by itself, it counts the file, and says gitleaks did not run", run.status === 0 && /documents changed: [1-9]/.test(all) && all.includes("NOT RUN"), all.slice(0, 400));
  h.check("redaction check: printed by itself, it holds no value, no path and no file name", !all.includes(secret) && !all.includes(notes) && !all.includes("secretfilename"), all.slice(0, 400));
}

/** A file gone by the time it is read is counted and not named, and does not end the report. */
function missingFile(h: Harness, tool: string, notes: string): void {
  const list = h.privatePath("migrate-missing.txt");
  writeFileSync(list, `${path.join(notes, "gone-secretfilename.md")}\n${path.join(notes, "plain.md")}\n`);
  const run = spawnSync("dotnet", [tool, "--json", "--no-gitleaks", "--manifest", list], { encoding: "utf8", timeout: 120_000 });
  const row = (JSON.parse(run.stdout || "[{}]") as Array<{ files?: number; skipped?: number }>)[0];
  h.check("redaction check: a file it cannot read is counted, the rest are read, and it exits 1", run.status === 1 && row?.files === 2 && row.skipped === 1, run.stdout + run.stderr);
  h.check("redaction check: that file is not named", !(run.stdout + run.stderr).includes("secretfilename"), run.stdout + run.stderr);
}

/** Forces the unexpected: a NUL in a path. The Windows event log gets what an unhandled exception says. */
function crash(h: Harness, tool: string): void {
  const list = h.privatePath("migrate-crash.txt");
  writeFileSync(list, "secretfilename\0.md\n");
  const run = spawnSync("dotnet", [tool, "--no-gitleaks", "--manifest", list], { encoding: "utf8", timeout: 120_000 });
  const all = run.stdout + run.stderr;
  h.check("redaction check: when it cannot finish it exits 3, with no stack and no file name", run.status === 3 && all.includes("could not finish") && !all.includes("secretfilename") && !all.includes("   at "), all.slice(0, 400));
}

/** What is in a temp folder, apart from the sockets and locks the .NET runtime keeps there on Linux. */
function written(temp: string): number {
  return readdirSync(temp).filter((name) => !/^(clr-debug-pipe|dotnet-diagnostic|\.dotnet)/.test(name)).length;
}

/** Watches its temp folder the whole time, and then kills it: there must be nothing to find, whether it finishes or not. */
async function leavesNothing(h: Harness, tool: string, secret: string): Promise<void> {
  const folder = h.privatePath("migrate-many");
  for (let i = 0; i < 300; i++) write(folder, `doc-${i}.md`, `# Doc ${i}\n\nthe key is ${secret} here\n`);
  const temp = h.privatePath("migrate-temp");
  mkdirSync(temp);
  // With gitleaks where it is installed, as that is the path that once needed copies.
  const hasGitleaks = spawnSync("gitleaks", ["version"]).status === 0;
  const child = spawn("dotnet", [tool, ...(hasGitleaks ? [] : ["--no-gitleaks"]), folder], { stdio: "ignore", env: { ...process.env, TMPDIR: temp, TEMP: temp, TMP: temp } });
  let exited = false;
  child.once("exit", () => (exited = true));
  let seen = 0;
  const started = Date.now();
  while (!exited && Date.now() - started < 2000) {
    seen += written(temp);
    await pause(5);
  }
  child.kill("SIGKILL");
  await pause(100);
  seen += written(temp);
  h.check("redaction check: killed partway, it leaves no copy, and wrote none while running", seen === 0, seen);
}

/** Only where gitleaks is installed: CI has a pinned one. Without it, this says plainly that it did not run. */
function realGitleaks(h: Harness, config: string, tool: string): void {
  if (spawnSync("gitleaks", ["version"], { encoding: "utf8" }).status !== 0) {
    console.log("NOT RUN migrate report: with real gitleaks (it is not on this machine)");
    return;
  }
  const run = h.memoria(["migrate", "report", "--qmd-config", config, "--tool", redactionCheckDll(), "--format", "json"]);
  const { notes } = rowsOf(run.out);
  h.check("migrate report: with real gitleaks, it reads the redacted text on stdin and finds nothing left", run.code === 0 && notes?.gitleaksRan === true && notes.files === 2, run.out + run.err);
  const clean = h.privatePath("migrate-leak");
  // A shape redaction leaves on purpose, so that a finding shows this pipe really reaches gitleaks.
  write(clean, "leak.md", `# Leak\n\n${"AW" + "S_SECRET_VALUE"} = "${"wJalrXUtnFEMI" + "K7MDENGbPxRfiCY" + "zzzzzzzzzzzzzz"}"\n`);
  const direct = spawnSync("dotnet", [redactionCheckDll(), "--json", clean], { encoding: "utf8", timeout: 120_000 });
  const row = (JSON.parse(direct.stdout || "[{}]") as Array<{ gitleaksRan?: boolean }>)[0];
  h.check("redaction check: with real gitleaks, the pipe gives back a report it can read", row?.gitleaksRan === true && [0, 1].includes(direct.status ?? -1), direct.stdout + direct.stderr);
}

function report(h: Harness, config: string, notes: string, secret: string): void {
  const tool = redactionCheckDll();
  const json = reportJson(h, config, tool);
  const table = h.memoria(["migrate", "report", "--qmd-config", config, "--tool", tool, "--no-gitleaks"]);
  h.check("migrate report: the table counts by rule, which shows the check could see the file", table.code === 0 && table.out.includes("replacements in 1 files"), table.out + table.err);
  h.check("migrate report: without gitleaks, it says that is a weaker check", table.err.includes("weaker check") && table.out.includes("NOT RUN"), table.out + table.err);
  const all = json + table.out + table.err;
  h.check("migrate report: holds no value, no path and no file name", !all.includes(secret) && !all.includes(notes) && !all.includes("secretfilename"), all);
  toolAlone(h, tool, notes, secret);
  missingFile(h, tool, notes);
  crash(h, tool);
}

/** A tree where each form below would make a difference. */
function conformanceTree(root: string): void {
  for (const name of ["a.md", "b.txt", "c.org", "ab.md", "sub/d.md", "sub/e.txt", "sub/ax.md", "sub/deep/f.md", "deep/g.md", "my dir/h.md", ".hidden/i.md", "node_modules/j.md", "sub/build/k.md"]) {
    write(root, name, `# ${name}\n\nsome text so it is indexed\n`);
  }
}

const SUPPORTED: Array<{ pattern: string; ignore?: string[] }> = [
  { pattern: "**/*.md" },
  { pattern: "*.md" },
  { pattern: "./**/*.md" },
  { pattern: "**/*.md, **/*.txt" },
  { pattern: "**/*.md,,**/*.txt" },
  { pattern: ",**/*.md" },
  { pattern: "**/*.{md,txt}" },
  { pattern: "**/*.{md,txt},*.org" },
  { pattern: "sub/**" },
  { pattern: "sub/*.md" },
  { pattern: "**/a?.md" },
  { pattern: "**/*" },
  { pattern: "my dir/*.md" },
  { pattern: "**/*.md", ignore: ["sub"] },
  { pattern: "**/*.md", ignore: ["./sub/**"] },
  { pattern: "**/*.md", ignore: ["sub/**"] },
  { pattern: "**/*.md", ignore: ["**/deep"] },
  { pattern: "**/*.md", ignore: ["**/deep/**"] },
  { pattern: "**/*.md", ignore: ["*.md"] },
  { pattern: "**/*.md", ignore: ["sub/*.md"] },
  { pattern: "**/*", ignore: ["**/*.txt", "deep"] },
];

const REFUSED: Array<{ pattern: string; ignore?: string[] }> = [
  { pattern: "**/*.@(md|txt)" },
  { pattern: "**/!(a).md" },
  { pattern: "**/+(a|b).md" },
  { pattern: "!**/*.md" },
  { pattern: "[ab].md" },
  { pattern: "**/*.{1..3}" },
  { pattern: "**/*.md", ignore: ["**/*.@(md)"] },
  { pattern: "**/*.md", ignore: [",!sub/**"] },
  { pattern: "**/*.md", ignore: ["!sub/**"] },
];

/** What the real QMD indexes for each form, one store each. */
function qmdCounts(h: Harness, root: string): number[] {
  const list = h.privatePath("migrate-conformance.json");
  writeFileSync(list, JSON.stringify(SUPPORTED));
  const code = `const qmd = await import("@tobilu/qmd");
const fs = await import("node:fs");
const cases = JSON.parse(fs.readFileSync(${JSON.stringify(list)}, "utf8"));
const counts = [];
let i = 0;
for (const { pattern, ignore } of cases) {
  const store = await qmd.createStore({ dbPath: ${JSON.stringify(h.privatePath("migrate-conformance"))} + (i++) + ".sqlite", config: { collections: { c: { path: ${JSON.stringify(root)}, pattern, ...(ignore ? { ignore } : {}) } } } });
  await store.update();
  counts.push(store.internal.db.prepare("SELECT count(*) AS n FROM documents WHERE active = 1").get().n);
  await store.close();
}
console.log(JSON.stringify(counts));`;
  const run = spawnSync(process.execPath, ["--input-type=module", "-e", code], { cwd: path.join(repo, "sidecar"), env: h.offlineEnvironment(), encoding: "utf8", timeout: 120_000 });
  h.check("selection: the real QMD indexed every form", run.status === 0, run.stderr.slice(0, 400));
  return JSON.parse(run.stdout.trim().split("\n").at(-1) ?? "[]") as number[];
}

function planConfig(h: Harness, root: string, form: { pattern: string; ignore?: string[] }, index: number): string {
  const config = h.privatePath(`migrate-conformance-${index}.yml`);
  writeFileSync(config, `collections:\n  c:\n    path: ${JSON.stringify(root)}\n    pattern: ${JSON.stringify(form.pattern)}\n${form.ignore ? `    ignore: ${JSON.stringify(form.ignore)}\n` : ""}`);
  return config;
}

function conformance(h: Harness): void {
  const root = h.privatePath("migrate-conformance-tree");
  conformanceTree(root);
  const counts = qmdCounts(h, root);
  SUPPORTED.forEach((form, index) => {
    const run = h.memoria(["migrate", "plan", "--qmd-config", planConfig(h, root, form, index), "--format", "json"]);
    const planned = (JSON.parse(run.out || "{}") as PlanResult).collections?.[0]?.files;
    h.check(`selection: plan and the real QMD agree on ${JSON.stringify(form)}`, run.code === 0 && counts[index] !== undefined && planned === counts[index], { qmd: counts[index], plan: planned, err: run.err });
  });
  REFUSED.forEach((form, index) => {
    const run = h.memoria(["migrate", "plan", "--qmd-config", planConfig(h, root, form, 100 + index), "--format", "json"]);
    h.check(`selection: refuses ${JSON.stringify(form)}, rather than a silent count`, run.code === 1 && run.err.includes("refused"), run.out + run.err);
  });
}

/** The tool is built with the servers, so `--no-build` needs it already built. */
export async function migrate(h: Harness): Promise<void> {
  const secret = h.remember(credential());
  const { config, codex, notes } = makeSetup(h, secret);
  plan(h, config, codex);
  report(h, config, notes, secret);
  await leavesNothing(h, redactionCheckDll(), secret);
  realGitleaks(h, config, redactionCheckDll());
  conformance(h);
}
