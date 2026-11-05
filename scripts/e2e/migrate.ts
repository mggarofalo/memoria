/**
 * `memoria migrate plan` and `report`, on a QMD config and collections this file makes in the
 * driver's private directory. Nothing here points at a real setup. The redaction check is the
 * real tool, run with no gitleaks, as it is not in CI.
 */
import { spawnSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { redactionCheckDll, type Harness } from "./harness.ts";

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
  h.check("redaction check: printed by itself, it counts the file", run.status === 0 && /documents changed: [1-9]/.test(all), all.slice(0, 400));
  h.check("redaction check: printed by itself, it holds no value, no path and no file name", !all.includes(secret) && !all.includes(notes) && !all.includes("secretfilename"), all.slice(0, 400));
}

function report(h: Harness, config: string, notes: string, secret: string): void {
  const tool = redactionCheckDll();
  const json = reportJson(h, config, tool);
  const table = h.memoria(["migrate", "report", "--qmd-config", config, "--tool", tool, "--no-gitleaks"]);
  h.check("migrate report: the table counts by rule, which shows the check could see the file", table.code === 0 && table.out.includes("replacements in 1 files"), table.out + table.err);
  const all = json + table.out + table.err;
  h.check("migrate report: holds no value, no path and no file name", !all.includes(secret) && !all.includes(notes) && !all.includes("secretfilename"), all);
  toolAlone(h, tool, notes, secret);
}

/** The tool is built with the servers, so `--no-build` needs it already built. */
export function migrate(h: Harness): void {
  const secret = h.remember(credential());
  const { config, codex, notes } = makeSetup(h, secret);
  plan(h, config, codex);
  report(h, config, notes, secret);
}
