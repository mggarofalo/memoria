import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { test, type TestContext } from "node:test";
import { fileURLToPath } from "node:url";
import { gunzipSync } from "node:zlib";
import { assertPublishable, commitsSince, previousTag, run } from "../cli.ts";
import { fakeTools, tempDir, tempRepo, type TempRepo } from "./helpers.ts";

const reference = (number: number): string => ["MEMORIA", String(number)].join("-");
const REPO_ROOT = fileURLToPath(new URL("../../../", import.meta.url));

const GREEN_RULES = JSON.stringify([{ type: "required_status_checks", parameters: { required_status_checks: [{ context: "Build" }] } }]);
const greenRun = JSON.stringify({ name: "Build", status: "completed", conclusion: "success" });

function greenGh(args: string[]): string {
  return args.some((arg) => arg.includes("/rules/branches/")) ? GREEN_RULES : `${greenRun}\n`;
}

/** A repository whose main is pushed to a local bare origin, so origin/main exists. */
function withOrigin(t: TestContext): TempRepo {
  const repo = tempRepo(t);
  const origin = tempDir(t);
  execFileSync("git", ["init", "-q", "--bare", origin]);
  repo.git("remote", "add", "origin", origin);
  return repo;
}

function pushMain(repo: TempRepo): void {
  repo.git("push", "-q", "origin", "main");
}

test("next reports the first release over a whole history", (t) => {
  const repo = tempRepo(t);
  repo.commit("chore: scaffold");
  repo.commit("feat(api): a");
  const { tools, lines } = fakeTools(repo);

  assert.equal(run(["next"], tools, assert.fail), 0);

  assert.equal(lines[0], "last tag: none");
  assert.equal(lines[1], "next: v0.1.0 (first release)");
  assert.match(lines[2] as string, /^ {2}minor [0-9a-f]{12} feat\(api\): a$/);
  assert.match(lines[3] as string, /^ {2}none {2}[0-9a-f]{12} chore: scaffold$/);
});

test("next counts only commits since the last tag, and the same history gives the same answer", (t) => {
  const repo = tempRepo(t);
  repo.commit("feat: old");
  repo.tag("v0.1.0");
  repo.commit("fix: a");
  repo.commit("docs: b");
  const first = fakeTools(repo);
  const second = fakeTools(repo);

  run(["next", "--json"], first.tools, assert.fail);
  run(["next", "--json"], second.tools, assert.fail);

  const report = JSON.parse(first.lines[0] as string);
  assert.equal(report.last, "v0.1.0");
  assert.equal(report.next, "0.1.1");
  assert.equal(report.tag, "v0.1.1");
  assert.deepEqual(report.commits.map((c: { subject: string }) => c.subject), ["docs: b", "fix: a"]);
  assert.deepEqual(first.lines, second.lines);
});

test("next reads a breaking footer from the commit body", (t) => {
  const repo = tempRepo(t);
  repo.commit("feat: old");
  repo.tag("v1.0.0");
  repo.commit("fix: a\n\nDetail.\n\nBREAKING CHANGE: the wire format moved.");
  const { tools, lines } = fakeTools(repo);

  run(["next"], tools, assert.fail);

  assert.equal(lines[1], "next: v2.0.0 (major bump over v1.0.0)");
});

test("next says so when there is nothing to release", (t) => {
  const repo = tempRepo(t);
  repo.commit("feat: old");
  repo.tag("v0.1.0");
  repo.commit("docs: only docs");
  const text = fakeTools(repo);
  const json = fakeTools(repo);

  assert.equal(run(["next"], text.tools, assert.fail), 0);
  assert.equal(run(["next", "--json"], json.tools, assert.fail), 0);

  assert.equal(text.lines[1], "next: nothing to release");
  assert.deepEqual(JSON.parse(json.lines[0] as string).next, null);
  assert.deepEqual(JSON.parse(json.lines[0] as string).tag, null);
});

test("previous tags ignore tags off this line of history and non-release tags", (t) => {
  const repo = tempRepo(t);
  repo.commit("feat: a");
  repo.tag("v0.1.0");
  repo.tag("nightly");
  repo.git("checkout", "-q", "-b", "side");
  repo.commit("fix: side");
  repo.tag("v0.9.0");
  repo.git("checkout", "-q", "main");
  repo.commit("fix: b");
  const { tools } = fakeTools(repo);

  assert.equal(previousTag(tools, "HEAD"), "v0.1.0");
  assert.equal(previousTag(tools, "HEAD", "v0.1.0"), undefined);
  assert.equal(previousTag(tools, "HEAD", "v0.2.0"), "v0.1.0");
  // A higher tag on the same commit is not behind this one.
  repo.tag("v0.3.0");
  assert.equal(previousTag(tools, "HEAD", "v0.2.0"), "v0.1.0");
  assert.deepEqual(commitsSince(tools, "v0.1.0", "HEAD").map((c) => c.message), ["fix: b"]);
});

test("describe prints the checkout's version", (t) => {
  const repo = tempRepo(t);
  repo.commit("feat: a");
  repo.tag("v0.1.0");
  const { tools, lines } = fakeTools(repo);

  assert.equal(run(["describe"], tools, assert.fail), 0);

  assert.deepEqual(lines, ["0.1.0"]);
});

test("validate accepts a strict computed tag on main with green checks", (t) => {
  const repo = withOrigin(t);
  repo.commit("feat: a");
  pushMain(repo);
  repo.tag("v0.1.0");
  const { tools, lines, ghCalls } = fakeTools(repo, greenGh);

  assert.equal(run(["validate", "v0.1.0"], tools, assert.fail), 0);

  assert.match(lines[0] as string, /^v0\.1\.0 is valid/);
  assert.ok(ghCalls.some((call) => call.includes("repos/owner/name/rules/branches/main")));
  const sha = repo.git("rev-parse", "HEAD").trim();
  assert.ok(ghCalls.some((call) => call.includes(`repos/owner/name/commits/${sha}/check-runs`)));
});

test("validate looks behind the tag itself, so a workflow re-run gets the same answer", (t) => {
  const repo = withOrigin(t);
  repo.commit("feat: a");
  repo.tag("v0.1.0");
  repo.commit("fix: b");
  pushMain(repo);
  repo.tag("v0.1.1");
  const { tools } = fakeTools(repo, greenGh);

  assert.equal(run(["validate", "v0.1.1"], tools, assert.fail), 0);
  assert.equal(run(["validate", "v0.1.1"], tools, assert.fail), 0);
});

test("validate refuses a tag that is not strict semver", (t) => {
  const repo = withOrigin(t);
  repo.commit("feat: a");
  const errors: string[] = [];
  const { tools } = fakeTools(repo);

  for (const bad of ["v1.2", "v1.2.3-rc1", "1.2.3"]) assert.equal(run(["validate", bad], tools, (e) => errors.push(e)), 1);
  assert.equal(run(["validate"], tools, (e) => errors.push(e)), 1);

  assert.equal(errors.length, 4);
  assert.ok(errors.every((e) => e.startsWith("not a strict vX.Y.Z")));
  assert.match(errors[3] as string, /\(none\)/);
});

test("validate refuses a tag off main", (t) => {
  const repo = withOrigin(t);
  repo.commit("feat: a");
  pushMain(repo);
  repo.git("checkout", "-q", "-b", "side");
  repo.commit("fix: b");
  repo.tag("v0.1.0");
  const errors: string[] = [];
  const { tools } = fakeTools(repo, greenGh);

  assert.equal(run(["validate", "v0.1.0"], tools, (e) => errors.push(e)), 1);

  assert.match(errors[0] as string, /is not on origin\/main/);
});

test("validate refuses a tag that is not the computed version, before asking GitHub anything", (t) => {
  const repo = withOrigin(t);
  repo.commit("feat: a");
  pushMain(repo);
  repo.tag("v3.0.0");
  const errors: string[] = [];
  const { tools, ghCalls } = fakeTools(repo, greenGh);

  assert.equal(run(["validate", "v3.0.0"], tools, (e) => errors.push(e)), 1);

  assert.match(errors[0] as string, /call for v0\.1\.0, not v3\.0\.0/);
  assert.equal(ghCalls.length, 0);
});

test("validate refuses when a required check has not passed", (t) => {
  const repo = withOrigin(t);
  repo.commit("feat: a");
  pushMain(repo);
  repo.tag("v0.1.0");
  const errors: string[] = [];
  const failing = (args: string[]): string =>
    args.some((arg) => arg.includes("/rules/")) ? GREEN_RULES : `${JSON.stringify({ name: "Build", status: "completed", conclusion: "failure" })}\n`;
  const { tools } = fakeTools(repo, failing);

  assert.equal(run(["validate", "v0.1.0"], tools, (e) => errors.push(e)), 1);

  assert.match(errors[0] as string, /required checks have not passed/);
  assert.match(errors[0] as string, /Build: failure/);
});

test("validate fails closed when main requires no checks", (t) => {
  const repo = withOrigin(t);
  repo.commit("feat: a");
  pushMain(repo);
  repo.tag("v0.1.0");
  const errors: string[] = [];
  const { tools } = fakeTools(repo, () => "[]");

  assert.equal(run(["validate", "v0.1.0"], tools, (e) => errors.push(e)), 1);

  assert.match(errors[0] as string, /no required status checks/);
});

test("build stamps from the commit timestamp, into the default or a given directory", (t) => {
  const repo = tempRepo(t);
  repo.commit("feat: a");
  const epoch = Number(repo.git("log", "-1", "--format=%ct").trim());
  const go = (args: string[]): void => writeFileSync(args[args.indexOf("-o") + 1] as string, "binary");
  const { tools, lines } = fakeTools(repo, undefined, go);

  assert.equal(run(["build", "1.2.3"], tools, assert.fail), 0);
  assert.equal(run(["build", "1.2.3", "--out", "elsewhere"], tools, assert.fail), 0);

  assert.equal(lines.length, 14);
  assert.ok(lines[6]?.endsWith(path.join("cli", "dist", "checksums.txt")));
  assert.ok(lines[13]?.endsWith(path.join("elsewhere", "checksums.txt")));
  const defaultSums = readFileSync(path.join(repo.dir, "cli", "dist", "checksums.txt"), "utf8");
  assert.equal(defaultSums, readFileSync(path.join(repo.dir, "elsewhere", "checksums.txt"), "utf8"), "the output directory does not change the bytes");
  const tar = gunzipSync(readFileSync(path.join(repo.dir, "cli", "dist", "memoria_1.2.3_linux_amd64.tar.gz")));
  assert.equal(parseInt(tar.toString("ascii", 136, 147), 8), epoch);
});

test("build needs a version", (t) => {
  const repo = tempRepo(t);
  repo.commit("feat: a");
  const errors: string[] = [];
  assert.equal(run(["build"], fakeTools(repo).tools, (e) => errors.push(e)), 1);
  assert.match(errors[0] as string, /needs a version/);
});

function repoWithNotesFile(t: TestContext): TempRepo {
  const repo = tempRepo(t);
  mkdirSync(path.join(repo.dir, "scripts", "release"), { recursive: true });
  copyFileSync(path.join(REPO_ROOT, "scripts", "release", "first-release-notes.md"), path.join(repo.dir, "scripts", "release", "first-release-notes.md"));
  return repo;
}

test("the first release uses the written notes and never asks GitHub to generate any", (t) => {
  const repo = repoWithNotesFile(t);
  repo.commit(`feat: an old subject (${reference(9)})`);
  repo.tag("v0.1.0");
  const out = path.join(tempDir(t), "notes.md");
  const { tools, lines, ghCalls } = fakeTools(repo);

  assert.equal(run(["notes", "v0.1.0", "--out", out], tools, assert.fail), 0);

  assert.match(readFileSync(out, "utf8"), /^First tagged release/);
  assert.equal(ghCalls.length, 0);
  assert.match(lines[0] as string, /first release/);
});

test("later releases generate notes only from after the previous tag", (t) => {
  const repo = repoWithNotesFile(t);
  repo.commit("feat: a");
  repo.tag("v0.1.0");
  repo.commit("fix: b");
  repo.tag("v0.1.1");
  const out = path.join(tempDir(t), "notes.md");
  const { tools, ghCalls, lines } = fakeTools(repo, () => "## What's Changed\n* fix: b (#12)\n");

  assert.equal(run(["notes", "v0.1.1", "--out", out], tools, assert.fail), 0);

  assert.equal(readFileSync(out, "utf8"), "## What's Changed\n* fix: b (#12)\n");
  assert.deepEqual(ghCalls[0], ["api", "-X", "POST", "repos/owner/name/releases/generate-notes", "-f", "tag_name=v0.1.1", "-f", "previous_tag_name=v0.1.0", "--jq", ".body"]);
  assert.match(lines[0] as string, /changes since v0\.1\.0/);
});

test("generated notes with a tracker reference are refused and nothing is written", (t) => {
  const repo = repoWithNotesFile(t);
  repo.commit("feat: a");
  repo.tag("v0.1.0");
  repo.commit("fix: b");
  repo.tag("v0.1.1");
  const out = path.join(tempDir(t), "notes.md");
  const errors: string[] = [];
  const { tools } = fakeTools(repo, () => `* fix: b (${reference(7)})\n`);

  assert.equal(run(["notes", "v0.1.1", "--out", out], tools, (e) => errors.push(e)), 1);

  assert.match(errors[0] as string, /must not be published/);
  assert.ok(errors[0]?.includes(reference(7)));
  assert.throws(() => readFileSync(out));
});

test("notes needs a tag and an output file", (t) => {
  const repo = tempRepo(t);
  repo.commit("feat: a");
  const errors: string[] = [];
  const { tools } = fakeTools(repo);
  assert.equal(run(["notes", "v0.1.0"], tools, (e) => errors.push(e)), 1);
  assert.equal(run(["notes", "--out"], tools, (e) => errors.push(e)), 1);
  assert.ok(errors.every((e) => /needs a tag and --out/.test(e)));
});

test("assertPublishable passes clean notes", () => {
  assert.doesNotThrow(() => assertPublishable("fix: a (#12)"));
  assert.throws(() => assertPublishable(reference(3)));
});

test("an unknown command prints usage, and an unexpected error is not swallowed", (t) => {
  const repo = tempRepo(t);
  repo.commit("feat: a");
  const errors: string[] = [];
  assert.equal(run(["nope"], fakeTools(repo).tools, (e) => errors.push(e)), 1);
  assert.equal(run([], fakeTools(repo).tools, (e) => errors.push(e)), 1);
  assert.match(errors[0] as string, /^usage: release/);

  const { tools } = fakeTools(repo);
  tools.git = () => {
    throw new TypeError("boom");
  };
  assert.throws(() => run(["next"], tools, assert.fail), /boom/);
});

test("the entry point runs against this repository", () => {
  const entry = path.join(REPO_ROOT, "scripts", "release", "release.ts");
  const version = execFileSync(process.execPath, [entry, "describe"], { encoding: "utf8" }).trim();
  assert.match(version, /^\d+\.\d+\.\d+/);
  assert.throws(() => execFileSync(process.execPath, [entry, "nope"], { stdio: "pipe" }));
});
