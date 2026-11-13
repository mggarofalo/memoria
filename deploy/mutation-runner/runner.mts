/**
 * Mutation testing for commits on main, run on a box of our own instead of hosted runners. See docs/mutation-runner.md.
 *
 * Each tick fetches the branch into a clone of its own, and mutates its newest commit. Commits that arrived together are
 * coalesced: only the newest is mutated, but the packages, and with MUTATION_SCOPE=changed the files, are those changed
 * since the last mutated commit, so coalescing skips nothing. The repository's own scripts do the work
 * (scripts/mutation-*.sh, with the Stryker and Gremlins configs). The result is a commit status named `mutation`, which
 * no ruleset requires.
 *
 * How a run is judged depends on its scope. A whole-package run (MUTATION_SCOPE=package, or a first run) is judged by
 * the scripts' exit codes, which are the configs' thresholds, so a score there means what it means in CI. A
 * changed-files run turns the thresholds off (MUTATION_NO_BREAK), so a nonzero exit means only that a tool failed.
 * It then scores just the mutants that overlap a line `git diff -U0 <base> HEAD` adds or changes, and judges each
 * config's changed-line score against that config's own break: a file below its package's bar can still be edited.
 * A config with no mutant on a changed line has no score, which is not a failure.
 *
 *   node runner.mts serve              poll every POLL_SECONDS, one run at a time
 *   node runner.mts once [options]     one tick now
 *   node runner.mts plan [options]     what one tick would mutate, without running it
 *   node runner.mts queue              the state, what is waiting, and recent results
 *
 * Options: --ref <branch> mutates that branch's head instead of BRANCH, starting from BRANCH's last mutated commit;
 * --since <commit> takes that as the last mutated commit, as for a first run; --packages <a,b> overrides the packages.
 *
 * With no GITHUB_TOKEN, the status it would post is written to the log instead.
 */
import { execFileSync, spawn } from "node:child_process";
import { closeSync, cpSync, existsSync, mkdirSync, openSync, readFileSync, readdirSync, renameSync, rmSync, statSync, utimesSync, writeFileSync } from "node:fs";
import { hostname } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const PACKAGES = ["dotnet", "sidecar", "worker", "cli"] as const;
export type Package = (typeof PACKAGES)[number];

const DOTNET_FILES = new Set(["dotnet-tools.json", "Memoria.slnx", "Directory.Build.props", "scripts/mutation-dotnet.sh", "scripts/mutation-dotnet-shards.mjs"]);
// StrykerJS and its runner come from the root package, and both packages run under the one script.
const NODE_FILES = new Set(["package.json", "package-lock.json", "scripts/mutation-js.sh"]);

const RULES: [Package[], (file: string) => boolean][] = [
  [["dotnet"], (f) => /^(src|tests|stryker)\//.test(f) || DOTNET_FILES.has(f)],
  [["sidecar"], (f) => f.startsWith("sidecar/") || f === "scripts/mutation-shard.mts"],
  [["worker"], (f) => f.startsWith("worker/")],
  [["sidecar", "worker"], (f) => NODE_FILES.has(f)],
  [["cli"], (f) => f.startsWith("cli/") || f === "scripts/mutation-go.sh"],
];

/** What a change to a path makes worth mutating. Markdown changes nothing a mutant can see. */
export function packagesFor(paths: readonly string[]): Package[] {
  const hit = new Set<Package>();
  for (const file of paths.filter((f) => !f.endsWith(".md"))) {
    for (const [packages, matches] of RULES) if (matches(file)) for (const p of packages) hit.add(p);
  }
  return PACKAGES.filter((p) => hit.has(p));
}

export interface Plan {
  target: string;
  /** The last mutated commit, or null for a first run, which mutates everything. */
  base: string | null;
  /** Every commit this run covers, oldest first. */
  commits: string[];
  packages: Package[];
}

/** Coalesces what arrived since the base into one run on the newest commit. Null when there is nothing new. */
export function planRun(input: { target: string; base: string | null; commits: readonly string[]; changed: readonly string[] | null; packages?: readonly Package[] | undefined }): Plan | null {
  if (input.base === input.target) return null;
  const packages = input.packages ? PACKAGES.filter((p) => input.packages!.includes(p)) : input.changed === null ? [...PACKAGES] : packagesFor(input.changed);
  return { target: input.target, base: input.base, commits: input.base === null ? [input.target] : [...input.commits], packages };
}

export function parsePackages(list: string): Package[] {
  const names = list.split(",").map((s) => s.trim()).filter(Boolean);
  for (const name of names) if (!(PACKAGES as readonly string[]).includes(name)) throw new Error(`unknown package "${name}"; the packages are ${PACKAGES.join(", ")}`);
  return PACKAGES.filter((p) => names.includes(p));
}

// --- scores -----------------------------------------------------------------------------------------------------

export interface Tally {
  detected: number;
  /** Mutants a test could have detected: detected, survived or uncovered. */
  valid: number;
}

/** A Stryker report's mutants, counted as Stryker counts its score. */
export function strykerTally(report: unknown): Tally {
  const files = (report as { files?: Record<string, { mutants?: { status?: string }[] }> } | null)?.files;
  if (!files || typeof files !== "object") return { detected: 0, valid: 0 };
  const statuses = Object.values(files).flatMap((file) => (file.mutants ?? []).map((m) => m.status ?? ""));
  const detected = statuses.filter((s) => DETECTED.has(s)).length;
  return { detected, valid: detected + statuses.filter((s) => UNDETECTED.has(s)).length };
}

/** The score Stryker reports: detected mutants over those a test could have detected. Null when there were none. */
export function strykerScore(report: unknown): number | null {
  return scoreOf(strykerTally(report));
}

export function addTally(sum: Tally | undefined, more: Tally): Tally {
  return { detected: (sum?.detected ?? 0) + more.detected, valid: (sum?.valid ?? 0) + more.valid };
}

export function scoreOf(tally: Tally): number | null {
  return tally.valid === 0 ? null : (100 * tally.detected) / tally.valid;
}

// security-core must be matched before security, which its name starts with.
const DOTNET_CONFIGS = ["security-core", "security", "core", "api"];

/** The Stryker.NET config a report belongs to: StrykerOutput/<config> or StrykerOutput/<config>-<shard>. */
export function dotnetConfigOf(name: string): string {
  return DOTNET_CONFIGS.find((c) => name === c || name.startsWith(`${c}-`)) ?? name;
}

const DETECTED = new Set(["Killed", "Timeout"]);
const UNDETECTED = new Set(["Survived", "NoCoverage"]);

/** Gremlins' test efficacy, already a percentage. */
export function gremlinsScore(report: unknown): number | null {
  const efficacy = (report as { test_efficacy?: unknown } | null)?.test_efficacy;
  return typeof efficacy === "number" && Number.isFinite(efficacy) ? efficacy : null;
}

// --- changed lines ----------------------------------------------------------------------------------------------

/** The lines a diff adds or changes, by repository path, as `git diff -U0` gives them. A pure deletion adds none. */
export type ChangedLines = Map<string, number[]>;

/** A hunk header's new lines, and how many body lines follow it. Null for any other line. */
function hunkOf(line: string): { lines: number[]; body: number } | null {
  const hunk = /^@@ -\d+(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/.exec(line);
  if (!hunk) return null;
  const [removed, start, added] = [Number(hunk[1] ?? 1), Number(hunk[2]), Number(hunk[3] ?? 1)];
  return { lines: Array.from({ length: added }, (_, i) => start + i), body: removed + added };
}

export function parseChangedLines(diff: string): ChangedLines {
  const changed: ChangedLines = new Map();
  let file: string | null = null;
  // Lines left in the current hunk, so a body line that looks like a header is read as a body line.
  let body = 0;
  for (const line of diff.split("\n")) {
    if (body > 0) {
      if (/^[+-]/.test(line)) body--;
      continue;
    }
    if (line.startsWith("+++ ")) {
      // b/<path>, or /dev/null for a deleted file.
      const target = line.slice(4).replace(/\r$/, "");
      file = target.startsWith("b/") ? target.slice(2) : null;
      continue;
    }
    const hunk = hunkOf(line);
    if (!hunk) continue;
    body = hunk.body;
    if (file !== null && hunk.lines.length > 0) changed.set(file, [...(changed.get(file) ?? []), ...hunk.lines]);
  }
  return changed;
}

/** Whether a mutant spanning lines start to end, inclusive, touches a changed line. */
export function touchesChange(lines: readonly number[] | undefined, start: number, end: number): boolean {
  return lines?.some((n) => n >= start && n <= end) ?? false;
}

/**
 * A report's file key as a repository path. Stryker.NET's keys are absolute, in the clone it ran in; StrykerJS's are
 * relative to the repository; Gremlins' to its module, which `prefix` names.
 */
export function repoPath(key: string, work: string, prefix = ""): string {
  const file = key.replaceAll("\\", "/");
  const root = `${work.replaceAll("\\", "/").replace(/\/$/, "")}/`;
  return file.startsWith(root) ? file.slice(root.length) : `${prefix}${file}`;
}

interface StrykerMutant {
  status?: string;
  location?: { start?: { line?: number }; end?: { line?: number } };
}

/** As strykerTally, counting only the mutants on a changed line. */
export function strykerChangedTally(report: unknown, changed: ChangedLines, fileOf: (key: string) => string): Tally {
  const files = (report as { files?: Record<string, { mutants?: StrykerMutant[] }> } | null)?.files;
  if (!files || typeof files !== "object") return { detected: 0, valid: 0 };
  const touches = (lines: number[] | undefined, m: StrykerMutant): boolean => {
    const start = m.location?.start?.line;
    return start !== undefined && touchesChange(lines, start, m.location?.end?.line ?? start);
  };
  const kept = Object.entries(files).map(([key, file]) => [key, { mutants: (file.mutants ?? []).filter((m) => touches(changed.get(fileOf(key)), m)) }]);
  return strykerTally({ files: Object.fromEntries(kept) });
}

/**
 * Gremlins' test efficacy as a tally, counting only the mutants on a changed line: killed over killed and lived, as
 * gremlinsScore's test_efficacy counts. Gremlins gives each mutant one line, where it starts.
 */
export function gremlinsChangedTally(report: unknown, changed: ChangedLines, fileOf: (key: string) => string): Tally {
  const files = (report as { files?: { file_name?: string; mutations?: { status?: string; line?: number }[] }[] } | null)?.files;
  if (!Array.isArray(files)) return { detected: 0, valid: 0 };
  const statuses = files.flatMap((f) => (f.mutations ?? []).filter((m) => m.line !== undefined && touchesChange(changed.get(fileOf(f.file_name ?? "")), m.line, m.line)).map((m) => m.status));
  const detected = statuses.filter((s) => s === "KILLED").length;
  return { detected, valid: detected + statuses.filter((s) => s === "LIVED").length };
}

/** cli/.gremlins.yaml's efficacy threshold, which is the one its score is: null when it sets none. */
export function gremlinsBreak(yaml: string): number | null {
  const match = /^[ \t]+efficacy:[ \t]*([\d.]+)[ \t]*$/m.exec(yaml);
  return match ? Number(match[1]) : null;
}

/** The scores under their config's break. A config with no score, or no break, is not judged. */
export function belowBreak(scores: Readonly<Record<string, number>>, breaks: Readonly<Record<string, number>>): string[] {
  return Object.entries(scores)
    .filter(([name, score]) => breaks[name] !== undefined && score < breaks[name])
    .map(([name]) => name);
}

export interface PackageResult {
  package: Package;
  /** The script's exit code: nonzero means a threshold failed, or the run did. */
  exitCode: number;
  /** Scores by report, such as "mutation" and "mutation-security". */
  scores: Record<string, number>;
  /** On a changed-lines run, where no threshold fails the script: the scores the runner found under their break. */
  below?: string[];
}

export function passed(result: PackageResult): boolean {
  return result.exitCode === 0 && (result.below ?? []).length === 0;
}

export type StatusState = "pending" | "success" | "failure" | "error";

const DESCRIPTION_LIMIT = 140;

/**
 * One line for the commit status: each package's scores, failures first, and on a changed-lines run a lead that says
 * the scores are for changed lines only. GitHub keeps 140 characters.
 */
export function describe(results: readonly PackageResult[], commits: number, changedLines = false): string {
  const part = (r: PackageResult): string => {
    const scores = Object.entries(r.scores).map(([name, score]) => `${name === "mutation" ? "" : `${name.replace(/^mutation-/, "")} `}${score.toFixed(1)}%`);
    return `${r.package}${passed(r) ? "" : " FAILED"}${scores.length > 0 ? ` ${scores.join(" ")}` : " no score"}`;
  };
  const ordered = [...results].sort((a, b) => Number(passed(a)) - Number(passed(b)));
  const text = `${changedLines ? "changed lines: " : ""}${ordered.map(part).join("; ")}${commits > 1 ? ` (${commits} commits)` : ""}`;
  return text.length <= DESCRIPTION_LIMIT ? text : `${text.slice(0, DESCRIPTION_LIMIT - 1)}…`;
}

export function overall(results: readonly PackageResult[]): StatusState {
  return results.every(passed) ? "success" : "failure";
}

/** owner/name from a GitHub clone URL. */
export function repositoryOf(url: string): string {
  const match = /github\.com[/:]([^/]+)\/([^/]+?)(?:\.git)?\/?$/.exec(url);
  if (!match) throw new Error("REPO_URL is not a GitHub repository; set GITHUB_REPOSITORY to owner/name");
  return `${match[1]}/${match[2]}`;
}

// --- the lock ---------------------------------------------------------------------------------------------------

/**
 * One run at a time, across the service and any `docker compose run`. They are different containers, so a process ID
 * says nothing: the holder touches the lock every minute, and a lock untouched for ten is a dead run's.
 */
export class Lock {
  readonly #file: string;
  readonly #now: () => number;
  readonly #staleMs: number;
  #heartbeat: NodeJS.Timeout | undefined;

  constructor(file: string, options: { now?: () => number; staleMs?: number } = {}) {
    this.#file = file;
    this.#now = options.now ?? Date.now;
    this.#staleMs = options.staleMs ?? 10 * 60_000;
  }

  /** Takes the lock, or says who holds it. */
  acquire(): { held: true } | { held: false; by: string } {
    const age = this.#age();
    if (age !== null && age < this.#staleMs) return { held: false, by: this.#holder() };
    if (age !== null) {
      // Moved aside rather than deleted: of two runs that both find it stale, only one can move it, and the other
      // then fails the exclusive create below instead of deleting the lock the first has just made.
      const aside = `${this.#file}.stale.${process.pid}.${this.#now()}`;
      try {
        renameSync(this.#file, aside);
      } catch {
        return { held: false, by: this.#holder() };
      }
      rmSync(aside, { force: true });
    }
    try {
      // Created exclusively, so of two runs starting together only one gets it.
      writeFileSync(this.#file, `${hostname()} pid ${process.pid} since ${new Date(this.#now()).toISOString()}\n`, { flag: "wx" });
    } catch (err) {
      if ((err as NodeJS.ErrnoException).code === "EEXIST") return { held: false, by: this.#holder() };
      throw err;
    }
    const at = new Date(this.#now());
    utimesSync(this.#file, at, at);
    return { held: true };
  }

  /** How long since the holder last touched the lock, or null when there is none. */
  #age(): number | null {
    try {
      return this.#now() - statSync(this.#file).mtimeMs;
    } catch {
      return null;
    }
  }

  #holder(): string {
    try {
      return readFileSync(this.#file, "utf8").trim();
    } catch {
      return "a run that has just finished";
    }
  }

  startHeartbeat(everyMs = 60_000): void {
    this.#heartbeat = setInterval(() => {
      try {
        this.touch();
      } catch (err) {
        // The lock went from under the run: say so, and let the run finish rather than crash the service.
        log(`the lock could not be touched: ${(err as Error).message}`);
      }
    }, everyMs);
    this.#heartbeat.unref();
  }

  touch(): void {
    const at = new Date(this.#now());
    utimesSync(this.#file, at, at);
  }

  release(): void {
    if (this.#heartbeat) clearInterval(this.#heartbeat);
    rmSync(this.#file, { force: true });
  }
}

// --- state ------------------------------------------------------------------------------------------------------

export interface RunRecord {
  target: string;
  base: string | null;
  ref: string;
  commits: number;
  packages: Package[];
  state: StatusState;
  description: string;
  finishedAt: string;
}

export interface State {
  /** The last mutated commit, by branch. */
  mutated: Record<string, string>;
  /** Runner errors so far, by commit, for commits still queued. */
  attempts: Record<string, number>;
  history: RunRecord[];
}

const HISTORY = 50;
/** Runner errors on one commit before it is given up as a failure, so a fault that repeats cannot run forever. */
export const MAX_ATTEMPTS = 3;

export function loadState(file: string): State {
  if (!existsSync(file)) return { mutated: {}, attempts: {}, history: [] };
  const raw = JSON.parse(readFileSync(file, "utf8")) as Partial<State>;
  return { mutated: raw.mutated ?? {}, attempts: raw.attempts ?? {}, history: raw.history ?? [] };
}

/**
 * Records a run, newest first in the history. An error is the runner's own failure, not the commit's, so the commit
 * stays queued for the next tick, up to MAX_ATTEMPTS errors; then it is recorded as a failure and the branch moves on.
 */
export function recordRun(state: State, run: RunRecord): State {
  const { [run.target]: previous = 0, ...attempts } = state.attempts;
  let record = run;
  if (run.state === "error") {
    if (previous + 1 < MAX_ATTEMPTS) return { mutated: state.mutated, attempts: { ...attempts, [run.target]: previous + 1 }, history: [run, ...state.history].slice(0, HISTORY) };
    record = { ...run, state: "failure", description: `gave up after ${MAX_ATTEMPTS} runner errors: ${run.description}`.slice(0, DESCRIPTION_LIMIT) };
  }
  return { mutated: { ...state.mutated, [run.ref]: run.target }, attempts, history: [record, ...state.history].slice(0, HISTORY) };
}

function saveState(file: string, state: State): void {
  writeFileSync(`${file}.tmp`, `${JSON.stringify(state, null, 2)}\n`);
  renameSync(`${file}.tmp`, file);
}

// --- running ----------------------------------------------------------------------------------------------------

interface Config {
  home: string;
  repoUrl: string;
  repository: string;
  branch: string;
  pollSeconds: number;
  scope: "changed" | "package";
  token: string | undefined;
}

function config(env: NodeJS.ProcessEnv = process.env): Config {
  // Compose passes an unset variable as empty, so empty means the default.
  const value = (name: string, fallback: string): string => env[name] || fallback;
  const repoUrl = value("REPO_URL", "https://github.com/mggarofalo/memoria.git");
  const scope = value("MUTATION_SCOPE", "changed");
  if (scope !== "changed" && scope !== "package") throw new Error('MUTATION_SCOPE is "changed" or "package"');
  const pollSeconds = Number(value("POLL_SECONDS", "900"));
  if (!(pollSeconds > 0)) throw new Error("POLL_SECONDS is a number of seconds");
  return {
    home: value("MUTATION_HOME", "/state"),
    repoUrl,
    repository: value("GITHUB_REPOSITORY", "") || repositoryOf(repoUrl),
    branch: value("BRANCH", "main"),
    pollSeconds,
    scope,
    token: value("GITHUB_TOKEN", "") || undefined,
  };
}

const log = (message: string): void => console.log(`${new Date().toISOString()} ${message}`);

function git(dir: string, ...args: string[]): string {
  // A big change's diff runs past execFileSync's default 1 MB.
  return execFileSync("git", ["-C", dir, ...args], { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], maxBuffer: 256 * 1024 * 1024 }).trim();
}

/**
 * The account the scripts run under. The runner itself is root and holds the token in its environment, which any
 * process of the same account could read from /proc, so the scripts, npm's install scripts and the tests run as
 * MUTATION_USER instead. Null when the runner is not root, as in a test.
 */
interface Identity {
  user: string;
  uid: number;
  gid: number;
  home: string;
}

let identity: Identity | null | undefined;

function childIdentity(): Identity | null {
  if (identity !== undefined) return identity;
  const user = process.env.MUTATION_USER;
  if (!user || process.getuid?.() !== 0) return (identity = null);
  const id = (flag: string): number => Number(execFileSync("id", [flag, user], { encoding: "utf8" }).trim());
  return (identity = { user, uid: id("-u"), gid: id("-g"), home: `/home/${user}` });
}

/** Runs a command with its output appended to the package's log, and resolves to its exit code. */
function run(command: string, args: string[], options: { cwd: string; env: NodeJS.ProcessEnv; logFile: string }): Promise<number> {
  writeFileSync(options.logFile, `$ ${command} ${args.join(" ")}\n`, { flag: "a" });
  const out = openSync(options.logFile, "a");
  const as = childIdentity();
  const env = as ? { ...options.env, HOME: as.home, USER: as.user } : options.env;
  return new Promise<number>((resolve) => {
    const child = spawn(command, args, { cwd: options.cwd, env, stdio: ["ignore", out, out], ...(as ? { uid: as.uid, gid: as.gid } : {}) });
    child.once("error", () => resolve(127));
    child.once("exit", (code, signal) => resolve(code ?? (signal ? 128 : 1)));
  }).finally(() => closeSync(out));
}

/**
 * The steps CI takes for each package (.github/workflows/mutation.yml), and where each leaves its reports. Setup steps
 * install; a setup step that fails is the runner's error, not the commit's failure.
 */
const STEPS: Record<Package, { setup: [string, string[]][]; mutate: [string, string[]]; reports: string[] }> = {
  // .NET's mutation may be several shards; see dotnetSteps.
  dotnet: { setup: [], mutate: ["sh", ["scripts/mutation-dotnet.sh"]], reports: ["StrykerOutput"] },
  sidecar: {
    setup: [
      ["npm", ["ci", "--no-fund", "--no-audit"]],
      ["npm", ["ci", "--no-fund", "--no-audit", "--prefix", "sidecar"]],
    ],
    mutate: ["sh", ["scripts/mutation-js.sh", "sidecar"]],
    reports: ["sidecar/reports"],
  },
  worker: {
    setup: [
      ["npm", ["ci", "--no-fund", "--no-audit"]],
      ["npm", ["ci", "--no-fund", "--no-audit", "--prefix", "worker"]],
    ],
    mutate: ["sh", ["scripts/mutation-js.sh", "worker"]],
    reports: ["worker/reports"],
  },
  cli: { setup: [], mutate: ["sh", ["scripts/mutation-go.sh"]], reports: ["cli/reports"] },
};

/** An exit code that says the mutation itself ran and judged the code: zero, or a failed threshold. Not a signal. */
export function judged(code: number): boolean {
  return code < 128 && code !== 127;
}

function findReports(dir: string): string[] {
  if (!existsSync(dir)) return [];
  return readdirSync(dir, { recursive: true, encoding: "utf8" })
    .filter((f) => /(^|[/\\])(mutation|mutation-report|gremlins)\.json$/.test(f))
    .map((f) => path.join(dir, f));
}

/** A report's name: its directory for Stryker (mutation, mutation-security, core, api…), "gremlins" for Gremlins. */
export function reportName(file: string): string {
  const parts = file.split(/[/\\]/);
  const base = parts.at(-1)!;
  if (base === "gremlins.json") return "mutation";
  // StrykerOutput/<config>/reports/mutation-report.json
  if (base === "mutation-report.json") return parts.at(-3) ?? "mutation";
  return parts.at(-2) ?? "mutation";
}

// --- .NET shards ------------------------------------------------------------------------------------------------

/** A shard as scripts/mutation-dotnet-shards.mjs lists it: its config, and its --mutate globs, relative to the project. */
export interface DotnetShard {
  name: string;
  configs: string;
  mutate: string;
}

/** What a Stryker.NET config mutates: files under its project's directory, narrowed by its own mutate globs. */
export interface DotnetConfig {
  projectDir: string;
  mutate: string[];
}

function globsMatch(file: string, globs: readonly string[]): boolean {
  const positive = globs.filter((g) => !g.startsWith("!"));
  const negative = globs.filter((g) => g.startsWith("!")).map((g) => g.slice(1));
  return (positive.length === 0 || positive.some((g) => path.posix.matchesGlob(file, g))) && !negative.some((g) => path.posix.matchesGlob(file, g));
}

/**
 * The shards that mutate any of the changed files, in their listed order. A file belongs to a shard when it is a .cs
 * file in the shard's config's project, matches the config's own mutate globs, and matches the shard's globs.
 */
export function shardsFor(changed: readonly string[], shards: readonly DotnetShard[], configs: Readonly<Record<string, DotnetConfig>>): DotnetShard[] {
  const holds = (shard: DotnetShard, file: string): boolean => {
    const config = configs[shard.configs];
    if (!config || !file.endsWith(".cs") || !file.startsWith(`${config.projectDir}/`)) return false;
    const relative = file.slice(config.projectDir.length + 1);
    return globsMatch(relative, config.mutate) && globsMatch(relative, shard.mutate.split(" ").filter(Boolean));
  };
  return shards.filter((shard) => changed.some((file) => holds(shard, file)));
}

/** The commit's shards and configs, or null for a commit from before the shards existed. */
function dotnetShards(work: string): { shards: DotnetShard[]; configs: Record<string, DotnetConfig> } | null {
  if (!existsSync(path.join(work, "scripts/mutation-dotnet-shards.mjs"))) return null;
  const shards = JSON.parse(execFileSync("node", ["scripts/mutation-dotnet-shards.mjs"], { cwd: work, encoding: "utf8" })) as DotnetShard[];
  const configs: Record<string, DotnetConfig> = {};
  for (const name of new Set(shards.map((s) => s.configs))) {
    const raw = JSON.parse(readFileSync(path.join(work, "stryker", `${name}.json`), "utf8")) as { "stryker-config": { project: string; mutate?: string[] } };
    configs[name] = { projectDir: path.posix.dirname(raw["stryker-config"].project), mutate: raw["stryker-config"].mutate ?? [] };
  }
  return { shards, configs };
}

interface Step {
  command: string;
  args: string[];
  env?: NodeJS.ProcessEnv;
}

const PLAIN_DOTNET: Step = { command: "sh", args: ["scripts/mutation-dotnet.sh"] };

/** Whether this run mutates only what changed since its base, and is judged on the changed lines. */
function changedScope(plan: Plan, cfg: Config): boolean {
  return cfg.scope === "changed" && plan.base !== null;
}

/** git in the work clone. It belongs to the scripts' account by now, and git refuses a root caller there unless told it is safe. */
function workGit(work: string, ...args: string[]): string {
  return git(work, "-c", `safe.directory=${work}`, ...args);
}

/** The lines changed since the base, in the work clone at the target. */
function changedLinesSince(work: string, base: string): ChangedLines {
  return parseChangedLines(workGit(work, "-c", "core.quotePath=false", "diff", "-U0", "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/", base, "HEAD"));
}

/**
 * How .NET is mutated. Whole-package runs take every shard in turn. A changed-files run takes only the shards holding
 * a changed source file, each with MUTATION_BASE, so it mutates just those files. With no source file changed, as when
 * only tests did, the plain script runs with MUTATION_BASE, and Stryker.NET's --since judges what the change touched.
 */
function dotnetSteps(work: string, plan: Plan, cfg: Config): Step[] {
  const listed = dotnetShards(work);
  if (!listed) return [PLAIN_DOTNET];
  const byShard = (shards: readonly DotnetShard[]): Step[] => shards.map((s) => ({ ...PLAIN_DOTNET, env: { MUTATION_SHARD: s.name } }));
  if (!changedScope(plan, cfg)) return byShard(listed.shards);
  const changed = workGit(work, "diff", "--name-only", "--diff-filter=d", plan.base!, "HEAD").split("\n").filter(Boolean);
  const chosen = shardsFor(changed, listed.shards, listed.configs);
  log(`dotnet: ${chosen.length > 0 ? `shards ${chosen.map((s) => `"${s.name}"`).join(", ")}` : "no changed source file in any shard; the plain run with --since"}`);
  return chosen.length > 0 ? byShard(chosen) : [PLAIN_DOTNET];
}

function mutationSteps(pkg: Package, work: string, plan: Plan, cfg: Config): Step[] {
  if (pkg === "dotnet") return dotnetSteps(work, plan, cfg);
  const [command, args] = STEPS[pkg].mutate;
  return [{ command, args }];
}

/**
 * Runs a package's setup, then its mutation steps, and resolves to the worst exit code among them. Throws, making the
 * run an error, when setup fails or a step was killed rather than finishing (the OOM killer, a signal, a missing command).
 */
async function runSteps(pkg: Package, work: string, logFile: string, env: NodeJS.ProcessEnv, steps: readonly Step[]): Promise<number> {
  for (const [command, args] of STEPS[pkg].setup) {
    const code = await run(command, args, { cwd: work, env, logFile });
    if (code !== 0) throw new Error(`${pkg}: ${command} ${args[0]} exited ${code}`);
  }
  let worst = 0;
  for (const step of steps) {
    const code = await run(step.command, step.args, { cwd: work, env: { ...env, ...step.env }, logFile });
    if (!judged(code)) throw new Error(`${pkg}: the mutation did not finish (exit ${code})`);
    worst = Math.max(worst, code);
  }
  return worst;
}

/**
 * A report's tally, or a score for a whole-package Gremlins report, or null for one that cannot be read. With changed
 * lines, only the mutants on them count, and Gremlins' are tallied too.
 */
function readReport(pkg: Package, work: string, report: string, changed: ChangedLines | null): { tally: Tally } | { score: number } | null {
  try {
    const json = JSON.parse(readFileSync(report, "utf8")) as unknown;
    const gremlins = path.basename(report) === "gremlins.json";
    if (changed) {
      const fileOf = (key: string): string => repoPath(key, work, gremlins ? `${pkg}/` : "");
      return { tally: gremlins ? gremlinsChangedTally(json, changed, fileOf) : strykerChangedTally(json, changed, fileOf) };
    }
    if (!gremlins) return { tally: strykerTally(json) };
    const score = gremlinsScore(json);
    return score === null ? null : { score };
  } catch (err) {
    log(`could not read ${report}: ${(err as Error).message}`);
    return null;
  }
}

function readJson(file: string): unknown {
  try {
    return JSON.parse(readFileSync(file, "utf8")) as unknown;
  } catch {
    return null;
  }
}

/** Each config's break threshold, by the name collectScores gives its score, read from the commit's own configs. */
export function breaksFor(pkg: Package, work: string): Record<string, number> {
  const found: [string, unknown][] = pkg === "dotnet" ? dotnetBreaks(work) : pkg === "cli" ? gremlinsBreaks(work) : strykerJsBreaks(pkg, work);
  return Object.fromEntries(found.filter((entry): entry is [string, number] => typeof entry[1] === "number"));
}

function dotnetBreaks(work: string): [string, unknown][] {
  return DOTNET_CONFIGS.map((name) => [name, (readJson(path.join(work, "stryker", `${name}.json`)) as { "stryker-config"?: { thresholds?: { break?: unknown } } } | null)?.["stryker-config"]?.thresholds?.break]);
}

/** StrykerJS configs, named by the directory their JSON report goes to, as reportName names the report. */
function strykerJsBreaks(pkg: Package, work: string): [string, unknown][] {
  return readdirSync(path.join(work, pkg))
    .filter((f) => /^stryker.*\.json$/.test(f))
    .map((f) => readJson(path.join(work, pkg, f)) as { jsonReporter?: { fileName?: string }; thresholds?: { break?: unknown } } | null)
    .filter((raw) => raw?.jsonReporter?.fileName)
    .map((raw) => [reportName(raw!.jsonReporter!.fileName!), raw!.thresholds?.break]);
}

function gremlinsBreaks(work: string): [string, unknown][] {
  const yaml = path.join(work, "cli", ".gremlins.yaml");
  return existsSync(yaml) ? [["mutation", gremlinsBreak(readFileSync(yaml, "utf8"))]] : [];
}

/**
 * Copies a package's reports into its results, and reads the scores: one per config, its shards counted together.
 * With changed lines, each score counts only the mutants on them.
 */
function collectScores(pkg: Package, work: string, results: string, changed: ChangedLines | null): Record<string, number> {
  const scores: Record<string, number> = {};
  const tallies: Record<string, Tally> = {};
  for (const dir of STEPS[pkg].reports.map((d) => path.join(work, d)).filter((d) => existsSync(d))) {
    cpSync(dir, path.join(results, pkg, path.basename(dir)), { recursive: true });
    for (const report of findReports(dir)) {
      const read = readReport(pkg, work, report, changed);
      const name = reportName(path.relative(work, report));
      const key = pkg === "dotnet" ? dotnetConfigOf(name) : name;
      if (read && "score" in read) scores[key] = read.score;
      if (read && "tally" in read) tallies[key] = addTally(tallies[key], read.tally);
    }
  }
  for (const [key, tally] of Object.entries(tallies)) {
    const score = scoreOf(tally);
    if (score !== null) scores[key] = score;
  }
  return scores;
}

/**
 * Mutates one package. A whole-package run is judged by the scripts' exit code, which is the configs' thresholds. A
 * changed-files run turns the thresholds off (MUTATION_NO_BREAK), so its exit code says only whether the tools ran,
 * and judges the score of the mutants on changed lines against each config's break instead.
 */
async function mutatePackage(pkg: Package, work: string, results: string, plan: Plan, cfg: Config, changed: ChangedLines | null): Promise<PackageResult> {
  const env: NodeJS.ProcessEnv = { ...process.env, CI: "true", ...(changed ? { MUTATION_BASE: plan.base!, MUTATION_NO_BREAK: "1" } : {}) };
  // The scripts and the tests they run never see the token.
  delete env.GITHUB_TOKEN;
  const exitCode = await runSteps(pkg, work, path.join(results, `${pkg}.log`), env, mutationSteps(pkg, work, plan, cfg));
  const scores = collectScores(pkg, work, results, changed);
  const below = changed ? belowBreak(scores, breaksFor(pkg, work)) : [];
  log(`${pkg}: exit ${exitCode}${Object.entries(scores).map(([n, s]) => `, ${n} ${s.toFixed(1)}%${below.includes(n) ? " (under its break)" : ""}`).join("")}${changed ? ", on changed lines" : ""}`);
  return { package: pkg, exitCode, scores, ...(changed ? { below } : {}) };
}

async function postStatus(cfg: Config, sha: string, state: StatusState, description: string): Promise<void> {
  if (!cfg.token) {
    log(`status (not posted, no GITHUB_TOKEN): ${cfg.repository}@${sha.slice(0, 12)} mutation=${state} "${description}"`);
    return;
  }
  const response = await fetch(`https://api.github.com/repos/${cfg.repository}/statuses/${sha}`, {
    method: "POST",
    headers: { authorization: `Bearer ${cfg.token}`, accept: "application/vnd.github+json", "x-github-api-version": "2022-11-28", "user-agent": "memoria-mutation-runner" },
    body: JSON.stringify({ state, context: "mutation", description }),
  });
  // Only the status code and GitHub's message: never the request, which holds the token.
  if (!response.ok) throw new Error(`posting the status failed: HTTP ${response.status} ${((await response.json().catch(() => ({}))) as { message?: string }).message ?? ""}`.trim());
  log(`status posted: ${cfg.repository}@${sha.slice(0, 12)} mutation=${state} "${description}"`);
}

interface Options {
  ref?: string | undefined;
  since?: string | undefined;
  packages?: Package[] | undefined;
}

function syncClone(cfg: Config, ref: string): string {
  const repo = path.join(cfg.home, "repo");
  if (!existsSync(path.join(repo, "HEAD"))) {
    log(`cloning ${cfg.repoUrl}`);
    execFileSync("git", ["clone", "--bare", "--quiet", cfg.repoUrl, repo], { stdio: "inherit" });
  }
  const refs = [...new Set([cfg.branch, ref])].map((r) => `+refs/heads/${r}:refs/heads/${r}`);
  git(repo, "fetch", "--quiet", "--prune", "origin", ...refs);
  return repo;
}

function planFor(cfg: Config, repo: string, options: Options): Plan | null {
  const ref = options.ref ?? cfg.branch;
  const state = loadState(path.join(cfg.home, "state.json"));
  const target = git(repo, "rev-parse", `refs/heads/${ref}^{commit}`);
  const base = options.since ? git(repo, "rev-parse", `${options.since}^{commit}`) : (state.mutated[ref] ?? state.mutated[cfg.branch] ?? null);
  const commits = base ? git(repo, "rev-list", "--reverse", `${base}..${target}`).split("\n").filter(Boolean) : [];
  const changed = base ? git(repo, "diff", "--name-only", base, target).split("\n").filter(Boolean) : null;
  return planRun({ target, base, commits, changed, packages: options.packages });
}

function describePlan(plan: Plan): string {
  return `${plan.target.slice(0, 12)}, covering ${plan.commits.length} commit(s) since ${plan.base ? plan.base.slice(0, 12) : "the start (a full run)"}: ${plan.packages.length > 0 ? plan.packages.join(", ") : "no package changed"}`;
}

/** One tick: fetch, plan, mutate, post. Resolves once the run is done, or at once if another holds the lock. */
export async function tick(options: Options = {}, cfg: Config = config()): Promise<void> {
  mkdirSync(cfg.home, { recursive: true });
  const lock = new Lock(path.join(cfg.home, "lock"));
  const taken = lock.acquire();
  if (!taken.held) {
    log(`another run holds the lock (${taken.by}); skipping this tick`);
    return;
  }
  lock.startHeartbeat();
  const ref = options.ref ?? cfg.branch;
  let plan: Plan | null = null;
  const stateFile = path.join(cfg.home, "state.json");
  const record = (p: Plan, state: StatusState, description: string): RunRecord => ({ target: p.target, base: p.base, ref, commits: p.commits.length, packages: p.packages, state, description, finishedAt: new Date().toISOString() });
  try {
    const repo = syncClone(cfg, ref);
    plan = planFor(cfg, repo, options);
    if (!plan) return log(`${ref} is at its last mutated commit; nothing to do`);
    log(`mutating ${ref} at ${describePlan(plan)}`);
    const { state, description } = await execute(plan, repo, cfg);
    await finish(cfg, stateFile, record(plan, state, description));
  } catch (err) {
    const message = (err instanceof Error ? err.message : String(err)).split("\n")[0]!.slice(0, 120);
    // Before a plan, as on a failed fetch, there is no commit to mark: the next tick tries again.
    log(`run failed: ${message}`);
    if (plan) await finish(cfg, stateFile, record(plan, "error", `the runner failed: ${message}`.slice(0, DESCRIPTION_LIMIT)));
  } finally {
    rmSync(path.join(cfg.home, "work"), { recursive: true, force: true });
    lock.release();
  }
}

/**
 * Records the run first, so a status GitHub refuses cannot send a finished run round again, then posts what was
 * recorded: a third runner error on one commit is recorded, and posted, as a failure.
 */
async function finish(cfg: Config, stateFile: string, run: RunRecord): Promise<void> {
  const next = recordRun(loadState(stateFile), run);
  saveState(stateFile, next);
  const recorded = next.history[0]!;
  await postStatus(cfg, recorded.target, recorded.state, recorded.description).catch((err: unknown) => log(`${(err as Error).message}; the result is kept in results/${recorded.target}`));
}

/** Mutates the plan's packages in a fresh clone at its commit, and keeps the reports and a summary under results/. */
async function execute(plan: Plan, repo: string, cfg: Config): Promise<{ state: StatusState; description: string }> {
  const results = path.join(cfg.home, "results", plan.target);
  mkdirSync(results, { recursive: true });
  if (plan.packages.length === 0) return { state: "success", description: `no package changed since ${plan.base?.slice(0, 7)}` };
  await postStatus(cfg, plan.target, "pending", `mutating ${plan.packages.join(", ")}`);
  const work = path.join(cfg.home, "work");
  rmSync(work, { recursive: true, force: true });
  // Copied rather than hardlinked, so handing the clone to the scripts' account changes nothing in the runner's own.
  execFileSync("git", ["clone", "--quiet", "--no-checkout", "--no-hardlinks", repo, work]);
  git(work, "checkout", "--quiet", "--detach", plan.target);
  const as = childIdentity();
  if (as) execFileSync("chown", ["-R", `${as.uid}:${as.gid}`, work]);
  const changed = changedScope(plan, cfg) ? changedLinesSince(work, plan.base!) : null;
  const outcomes: PackageResult[] = [];
  for (const pkg of plan.packages) outcomes.push(await mutatePackage(pkg, work, results, plan, cfg, changed));
  const outcome = { state: overall(outcomes), description: describe(outcomes, plan.commits.length, changed !== null) };
  writeFileSync(path.join(results, "summary.json"), `${JSON.stringify({ ...plan, ...outcome, results: outcomes }, null, 2)}\n`);
  return outcome;
}

function showQueue(cfg: Config): void {
  const state = loadState(path.join(cfg.home, "state.json"));
  console.log(`last mutated: ${Object.entries(state.mutated).map(([ref, sha]) => `${ref} ${sha.slice(0, 12)}`).join(", ") || "nothing yet"}`);
  const lockFile = path.join(cfg.home, "lock");
  console.log(existsSync(lockFile) ? `running: ${readFileSync(lockFile, "utf8").trim()}` : "running: nothing");
  const repo = path.join(cfg.home, "repo");
  if (existsSync(path.join(repo, "HEAD"))) {
    const plan = planFor(cfg, repo, {});
    console.log(plan ? `waiting: ${describePlan(plan)}` : `waiting: nothing; ${cfg.branch} is mutated as of the last fetch`);
  }
  for (const run of state.history.slice(0, 10)) console.log(`${run.finishedAt} ${run.ref} ${run.target.slice(0, 12)} ${run.state}: ${run.description}`);
}

export function parseArgs(argv: readonly string[]): { command: string; options: Options } {
  const [command = "serve", ...rest] = argv;
  const options: Options = {};
  for (let i = 0; i < rest.length; i += 2) {
    const [flag, value] = [rest[i], rest[i + 1]];
    if (value === undefined) throw new Error(`${flag} needs a value`);
    if (flag === "--ref") options.ref = value;
    else if (flag === "--since") options.since = value;
    else if (flag === "--packages") options.packages = parsePackages(value);
    else throw new Error(`unknown option ${flag}`);
  }
  return { command, options };
}

function showPlan(cfg: Config, options: Options): void {
  mkdirSync(cfg.home, { recursive: true });
  const repo = syncClone(cfg, options.ref ?? cfg.branch);
  const plan = planFor(cfg, repo, options);
  console.log(plan ? describePlan(plan) : "nothing to do");
  for (const sha of plan?.commits ?? []) console.log(`  ${git(repo, "log", "-1", "--format=%h %s", sha)}`);
}

async function serve(cfg: Config): Promise<never> {
  log(`polling ${cfg.repository} ${cfg.branch} every ${cfg.pollSeconds} s, scope ${cfg.scope}, ${cfg.token ? "posting statuses" : "no GITHUB_TOKEN: statuses go to the log"}`);
  for (;;) {
    await tick({}, cfg).catch((err: unknown) => log(`tick failed: ${String(err).slice(0, 200)}`));
    await new Promise((resolve) => setTimeout(resolve, cfg.pollSeconds * 1000));
  }
}

const COMMANDS: Record<string, (cfg: Config, options: Options) => unknown> = {
  serve: (cfg) => serve(cfg),
  once: (cfg, options) => tick(options, cfg),
  plan: showPlan,
  queue: (cfg) => showQueue(cfg),
};

async function main(): Promise<void> {
  const { command, options } = parseArgs(process.argv.slice(2));
  const handler = COMMANDS[command];
  if (!handler) throw new Error(`unknown command ${command}; use ${Object.keys(COMMANDS).join(", ")}`);
  await handler(config(), options);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((err: unknown) => {
    console.error(err instanceof Error ? err.message : String(err));
    process.exit(1);
  });
}
