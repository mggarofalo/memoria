import { randomBytes } from "node:crypto";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { buildAssets, SHARED_FILES } from "./assets.ts";
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
  /** Run any command in a directory. Throws on a non-zero exit. */
  exec: (command: string, args: string[], cwd: string) => void;
  /** Run docker and return stdout. Throws on a non-zero exit. */
  docker: (args: string[]) => string;
  /** The response body, or undefined while nothing answers successfully. */
  httpGet: (url: string) => string | undefined;
  sleep: (ms: number) => void;
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
  build <version> [--out d]  build every release asset and checksums.txt (default out: cli/dist)
  notes <tag> --out <file>   write the release notes, refusing any tracker reference
  read-checks <sha>          prove this token can read what validate reads, without judging the result
  verify-image <ref> <ver>   run an image and refuse unless /health reports the version
  tag-image <ref@digest> <tag...>  point tags at a verified digest and confirm each resolves to it`;

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

/** The required checks and this commit's runs. Anything unreadable refuses the tag: not knowing is not green. */
function readChecks(tools: Tools, sha: string): { required: string[]; runs: CheckRun[] } {
  let required: string[];
  let runs: CheckRun[];
  try {
    required = requiredChecks(JSON.parse(tools.gh(["api", `repos/${tools.repo}/rules/branches/main`])));
    const lines = tools.gh(["api", "--paginate", `repos/${tools.repo}/commits/${sha}/check-runs`, "--jq", ".check_runs[] | {name,status,conclusion}"]);
    runs = lines.split("\n").filter(Boolean).map((line) => JSON.parse(line) as CheckRun);
  } catch (error) {
    fail(`could not read the required checks for ${sha} with this token, so the tag is refused: ${(error as Error).message}`);
  }
  if (required.length === 0) fail("main reports no required status checks, so nothing proves this commit is green; the token may not be able to see the ruleset");
  return { required, runs };
}

function requireGreenChecks(tools: Tools, sha: string): void {
  const { required, runs } = readChecks(tools, sha);
  const unmet = unmetChecks(required, runs);
  if (unmet.length > 0) fail(`required checks have not passed on ${sha}:\n  ${unmet.join("\n  ")}`);
}

/** Prove this token can read what `validate` reads, without judging the result. */
function readChecksCommand(tools: Tools, sha: string | undefined): void {
  if (sha === undefined) fail("read-checks needs a commit");
  const { required, runs } = readChecks(tools, sha);
  tools.print(`read ${required.length} required checks and ${runs.length} runs on ${sha}`);
  const unmet = unmetChecks(required, runs);
  for (const name of required) tools.print(`  ${name}: ${unmet.some((line) => line.startsWith(`${name}:`)) ? "not met" : "met"}`);
}

const HEALTH_ATTEMPTS = 30;
const VERIFY_PORT = 18080;

function reportedVersion(tools: Tools): string | undefined {
  for (let attempt = 0; attempt < HEALTH_ATTEMPTS; attempt++) {
    const body = tools.httpGet(`http://127.0.0.1:${VERIFY_PORT}/health`);
    if (body !== undefined) return (JSON.parse(body) as { version?: string }).version;
    tools.sleep(1000);
  }
  return undefined;
}

/** Run this exact image reference and compare what it reports. The Sidecar URL and the throwaway digest key only satisfy startup validation. */
function verifyImageCommand(tools: Tools, image: string | undefined, version: string | undefined): void {
  if (image === undefined || version === undefined) fail("verify-image needs an image and a version");
  const name = "memoria-verify";
  tools.docker(["run", "-d", "--name", name, "-p", `127.0.0.1:${VERIFY_PORT}:8080`, "-e", "Memoria__Sidecar__BaseUrl=http://127.0.0.1:8282", "-e", `Memoria__Ingest__DigestKey=${randomBytes(32).toString("base64url")}`, image]);
  try {
    const reported = reportedVersion(tools);
    if (reported !== version) fail(`${image} reports version ${reported ?? "(no answer)"}, expected ${version}`);
    tools.print(`${image} reports ${reported}`);
  } finally {
    tools.docker(["rm", "-f", name]);
  }
}

/** Point tags at a verified digest without rebuilding, then confirm each tag resolves to exactly that digest. */
function tagImageCommand(tools: Tools, image: string | undefined, tags: readonly string[]): void {
  const digest = image?.split("@")[1];
  if (image === undefined || digest === undefined || tags.length === 0) fail("tag-image needs an image@digest and at least one tag");
  // By default this wraps a single image in a new index, which has a different digest; the check below would catch it.
  tools.docker(["buildx", "imagetools", "create", "--prefer-index=false", ...tags.flatMap((tag) => ["-t", tag]), image]);
  for (const tag of tags) {
    const manifest = JSON.parse(tools.docker(["buildx", "imagetools", "inspect", tag, "--format", "{{json .Manifest}}"])) as { digest?: string };
    if (manifest.digest !== digest) fail(`${tag} resolves to ${manifest.digest ?? "nothing"}, not the verified ${digest}`);
    tools.print(`${tag} -> ${digest}`);
  }
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
  const serviceTree = mkdtempSync(path.join(tmpdir(), "memoria-services-"));
  try {
    prepareServiceTree(tools, serviceTree);
    for (const name of buildAssets({ version, outDir, epoch, go: tools.go, serviceTree })) tools.print(path.join(outDir, name));
  } finally {
    rmSync(serviceTree, { recursive: true, force: true, maxRetries: 5 });
  }
}

/**
 * Export the services from the commit, not the working tree, and compile the
 * sidecar there, so a release holds what was committed and a developer's
 * node_modules survives. Install scripts stay off: the compiler needs no native code.
 */
function prepareServiceTree(tools: Tools, tree: string): void {
  // The eol attributes cover almost everything; this keeps a Windows setting from changing the rest.
  tools.git(["-c", "core.autocrlf=false", "archive", "--format=tar", "-o", path.join(tree, "source.tar"), "HEAD", "sidecar", "worker", ...SHARED_FILES]);
  tools.exec("tar", ["-xf", "source.tar"], tree);
  const sidecar = path.join(tree, "sidecar");
  tools.exec("npm", ["ci", "--ignore-scripts", "--no-audit", "--no-fund"], sidecar);
  tools.exec(process.execPath, ["node_modules/typescript/bin/tsc", "-p", "tsconfig.build.json"], sidecar);
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
    case "read-checks":
      return readChecksCommand(tools, first);
    case "verify-image":
      return verifyImageCommand(tools, first, args[2]);
    case "tag-image":
      return tagImageCommand(tools, first, args.slice(2));
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
