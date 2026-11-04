import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { buildAssets } from "./assets.ts";
import { requiredChecks, trackerReferences, unmetChecks, type CheckRun } from "./gates.ts";
import { classifyCommit, compareVersions, highestBump, latestTag, nextVersion, parseTag, tagProblem, type Bump, type Semver } from "./version.ts";
import { describedVersion } from "../../worker/src/version.ts";

/**
 * The release commands. docs/releasing.md explains the scheme; `run` takes its
 * tools as arguments so tests can drive it on a temporary repository and a fake gh.
 */

export interface Tools {
  /** Repository root. */
  root: string;
  /** `owner/name`, for GitHub API calls. */
  repo: string;
  /** Run git in the root and return stdout. Throws on a non-zero exit. */
  git: (args: string[]) => string;
  /** Run gh in the root and return stdout. Throws on a non-zero exit. */
  gh: (args: string[]) => string;
  /** Run go in the CLI module with extra environment. Throws on a non-zero exit. */
  go: (args: string[], env: Record<string, string>) => void;
  print: (line: string) => void;
}

export interface Commit {
  sha: string;
  message: string;
}

const USAGE = `usage: release <command>
  next [--json]              compute the next version from commits since the last tag
  describe                   print the version of this checkout, as every component derives it
  validate <tag>             refuse a tag that is not strict, not on main, not the computed version, or not green
  build <version> [--out d]  build the CLI assets and checksums.txt (default out: cli/dist)
  notes <tag> --out <file>   write the release notes, refusing any tracker reference`;

class ReleaseError extends Error {}

function fail(message: string): never {
  throw new ReleaseError(message);
}

function subjectOf(message: string): string {
  return message.split("\n", 1)[0] as string;
}

/** The latest release tag reachable from `ref`; given `before`, only tags lower than it, so a tag can look behind itself. */
export function previousTag(tools: Tools, ref: string, before?: string): string | undefined {
  const tags = tools.git(["tag", "--merged", ref, "--list", "v*"]).split("\n").map((tag) => tag.trim());
  const limit = before === undefined ? undefined : parseTag(before);
  return latestTag(tags.filter((tag) => isBelow(tag, limit)));
}

function isBelow(tag: string, limit: Semver | undefined): boolean {
  const version = parseTag(tag);
  return version !== undefined && (limit === undefined || compareVersions(version, limit) < 0);
}

export function commitsSince(tools: Tools, last: string | undefined, ref: string): Commit[] {
  const log = tools.git(["log", "--format=%H%x00%B%x1e", last ? `${last}..${ref}` : ref]);
  return log
    .split("\u001e")
    .map((entry) => entry.trim())
    .filter(Boolean)
    .map((entry) => {
      const [sha, message] = entry.split("\u0000") as [string, string];
      return { sha, message };
    });
}

function describeBump(last: string | undefined, bump: Bump): string {
  return last ? `${bump} bump over ${last}` : "first release";
}

function nextCommand(tools: Tools, json: boolean): void {
  const last = previousTag(tools, "HEAD");
  const commits = commitsSince(tools, last, "HEAD");
  const next = nextVersion(last, commits.map((commit) => commit.message));
  const rows = commits.map((commit) => ({ sha: commit.sha.slice(0, 12), bump: classifyCommit(commit.message), subject: subjectOf(commit.message) }));

  if (json) {
    tools.print(JSON.stringify({ last: last ?? null, next: next ?? null, tag: next ? `v${next}` : null, commits: rows }, null, 2));
    return;
  }
  tools.print(`last tag: ${last ?? "none"}`);
  tools.print(next ? `next: v${next} (${describeBump(last, highestBump(commits.map((commit) => commit.message)))})` : "next: nothing to release");
  for (const row of rows) tools.print(`  ${row.bump.padEnd(5)} ${row.sha} ${row.subject}`);
}

function requireOnMain(tools: Tools, sha: string): void {
  try {
    tools.git(["merge-base", "--is-ancestor", sha, "origin/main"]);
  } catch {
    fail(`commit ${sha} is not on origin/main`);
  }
}

function requireGreenChecks(tools: Tools, sha: string): void {
  const required = requiredChecks(JSON.parse(tools.gh(["api", `repos/${tools.repo}/rules/branches/main`])));
  if (required.length === 0) fail("main has no required status checks, so nothing proves this commit is green");
  const lines = tools.gh(["api", "--paginate", `repos/${tools.repo}/commits/${sha}/check-runs`, "--jq", ".check_runs[] | {name,status,conclusion}"]);
  const runs = lines.split("\n").filter(Boolean).map((line) => JSON.parse(line) as CheckRun);
  const unmet = unmetChecks(required, runs);
  if (unmet.length > 0) fail(`required checks have not passed on ${sha}:\n  ${unmet.join("\n  ")}`);
}

function validateCommand(tools: Tools, tag: string | undefined): void {
  if (tag === undefined || !parseTag(tag)) fail(`not a strict vX.Y.Z release tag: ${tag ?? "(none)"}`);
  const sha = tools.git(["rev-parse", `refs/tags/${tag}^{commit}`]).trim();
  requireOnMain(tools, sha);

  const last = previousTag(tools, sha, tag);
  const problem = tagProblem(tag, last, commitsSince(tools, last, sha).map((commit) => commit.message));
  if (problem) fail(problem);

  requireGreenChecks(tools, sha);
  tools.print(`${tag} is valid: strict semver, on main, the computed version, required checks green`);
}

function buildCommand(tools: Tools, version: string | undefined, out: string | undefined): void {
  if (version === undefined) fail("build needs a version");
  const epoch = Number(tools.git(["log", "-1", "--format=%ct", "HEAD"]).trim());
  const outDir = path.resolve(tools.root, out ?? "cli/dist");
  for (const name of buildAssets({ version, outDir, epoch, go: tools.go })) tools.print(path.join(outDir, name));
}

/** Refuse notes with a tracker reference; called on whatever the notes came from. */
export function assertPublishable(notes: string): void {
  const references = trackerReferences(notes);
  if (references.length > 0) fail(`release notes contain tracker references, which must not be published: ${references.join(", ")}`);
}

function notesCommand(tools: Tools, tag: string | undefined, out: string | undefined): void {
  if (tag === undefined || out === undefined) fail("notes needs a tag and --out");
  const previous = previousTag(tools, `refs/tags/${tag}`, tag);
  // The first release is written by hand: generating over the whole history would republish old subjects.
  const notes = previous
    ? tools.gh(["api", "-X", "POST", `repos/${tools.repo}/releases/generate-notes`, "-f", `tag_name=${tag}`, "-f", `previous_tag_name=${previous}`, "--jq", ".body"])
    : readFileSync(path.join(tools.root, "scripts/release/first-release-notes.md"), "utf8");
  assertPublishable(notes);
  writeFileSync(out, notes, "utf8");
  tools.print(`wrote ${out} (${previous ? `changes since ${previous}` : "first release"})`);
}

function flagValue(args: readonly string[], flag: string): string | undefined {
  const at = args.indexOf(flag);
  return at === -1 ? undefined : args[at + 1];
}

function dispatch(args: readonly string[], tools: Tools): void {
  const [command, first] = args;
  const out = flagValue(args, "--out");
  switch (command) {
    case "next":
      return nextCommand(tools, args.includes("--json"));
    case "describe":
      return tools.print(describedVersion(tools.root));
    case "validate":
      return validateCommand(tools, first);
    case "build":
      return buildCommand(tools, first, out);
    case "notes":
      return notesCommand(tools, first, out);
    default:
      fail(USAGE);
  }
}

/** Returns the process exit code. An unexpected error still throws. */
export function run(args: readonly string[], tools: Tools, printError: (line: string) => void): number {
  try {
    dispatch(args, tools);
    return 0;
  } catch (error) {
    if (!(error instanceof ReleaseError)) throw error;
    printError(error.message);
    return 1;
  }
}
