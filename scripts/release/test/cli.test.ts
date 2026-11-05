import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { test, type TestContext } from "node:test";
import { fileURLToPath } from "node:url";
import { gunzipSync } from "node:zlib";
import { SERVICES, SHARED_FILES } from "../assets.ts";
import { assertPublishable, commitsSince, previousTag, run, type Tools } from "../cli.ts";
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

/** A repository with what a service package ships, committed, and tools that stand in for npm and tsc. */
function repoWithServices(t: TestContext): { repo: TempRepo; exec: Tools["exec"]; calls: string[] } {
  const repo = tempRepo(t);
  for (const service of SERVICES) {
    for (const item of service.include.filter((name) => name !== "dist")) {
      const file = path.join(repo.dir, service.dir, item === "src" || item === "scripts" ? `${item}/file.ts` : item);
      mkdirSync(path.dirname(file), { recursive: true });
      writeFileSync(file, `committed ${service.dir}/${item}\n`);
    }
  }
  for (const name of SHARED_FILES) {
    mkdirSync(path.dirname(path.join(repo.dir, name)), { recursive: true });
    writeFileSync(path.join(repo.dir, name), "shared\n");
  }
  repo.git("add", "sidecar", "worker", "deploy");
  repo.commit("feat: a");
  const calls: string[] = [];
  const exec: Tools["exec"] = (command, args, cwd) => {
    calls.push(`${path.basename(command)} ${args[0]}`);
    if (command === "tar") {
      execFileSync(command, args, { cwd, stdio: "pipe" });
    } else if (args[0]?.endsWith("tsc")) {
      mkdirSync(path.join(cwd, "dist"));
      writeFileSync(path.join(cwd, "dist", "main.js"), "compiled\n");
    }
  };
  return { repo, exec, calls };
}

test("build stamps from the commit timestamp, into the default or a given directory", (t) => {
  const { repo, exec, calls } = repoWithServices(t);
  const epoch = Number(repo.git("log", "-1", "--format=%ct").trim());
  const go = (args: string[]): void => writeFileSync(args[args.indexOf("-o") + 1] as string, "binary");
  const { tools, lines } = fakeTools(repo, undefined, go, { exec });

  assert.equal(run(["build", "1.2.3"], tools, assert.fail), 0);
  assert.equal(run(["build", "1.2.3", "--out", "elsewhere"], tools, assert.fail), 0);

  assert.equal(lines.length, 18);
  assert.ok(lines[8]?.endsWith(path.join("cli", "dist", "checksums.txt")));
  assert.ok(lines[17]?.endsWith(path.join("elsewhere", "checksums.txt")));
  assert.deepEqual(calls.slice(0, 3).map((call) => call.split(" ")[0]), ["tar", "npm", path.basename(process.execPath)]);
  const defaultSums = readFileSync(path.join(repo.dir, "cli", "dist", "checksums.txt"), "utf8");
  assert.equal(defaultSums, readFileSync(path.join(repo.dir, "elsewhere", "checksums.txt"), "utf8"), "the output directory does not change the bytes");
  const tar = gunzipSync(readFileSync(path.join(repo.dir, "cli", "dist", "memoria_1.2.3_linux_amd64.tar.gz")));
  assert.equal(parseInt(tar.toString("ascii", 136, 147), 8), epoch);
});

test("service packages hold what was committed, not the working tree, stamped with the version", (t) => {
  const { repo, exec } = repoWithServices(t);
  writeFileSync(path.join(repo.dir, "worker", "README.md"), "uncommitted edit\n");
  const go = (args: string[]): void => writeFileSync(args[args.indexOf("-o") + 1] as string, "binary");
  const out = tempDir(t);

  assert.equal(run(["build", "1.2.3", "--out", out], fakeTools(repo, undefined, go, { exec }).tools, assert.fail), 0);

  const tar = (args: string[]): string => execFileSync("tar", args, { cwd: out, encoding: "utf8" });
  assert.equal(tar(["-xzOf", "memoria-worker_1.2.3.tar.gz", "src/VERSION"]), "1.2.3\n");
  assert.equal(tar(["-xzOf", "memoria-worker_1.2.3.tar.gz", "README.md"]), "committed worker/README.md\n");
  assert.equal(tar(["-xzOf", "memoria-sidecar_1.2.3.tar.gz", "dist/VERSION"]), "1.2.3\n");
  assert.equal(tar(["-xzOf", "memoria-sidecar_1.2.3.tar.gz", "dist/main.js"]), "compiled\n");
  assert.equal(tar(["-xzOf", "memoria-sidecar_1.2.3.tar.gz", "deploy/windows/MemoriaService.psm1"]), "shared\n");
});

test("a real failure during a build is not swallowed", (t) => {
  const { repo } = repoWithServices(t);
  const boom: Tools["exec"] = () => {
    throw new TypeError("tar exploded");
  };
  assert.throws(() => run(["build", "1.2.3"], fakeTools(repo, undefined, undefined, { exec: boom }).tools, assert.fail), /tar exploded/);
});

test("validate fails closed, with a clear message, when the token cannot read the checks", (t) => {
  const repo = withOrigin(t);
  repo.commit("feat: a");
  pushMain(repo);
  repo.tag("v0.1.0");
  const denied = (): string => {
    throw new Error("gh: Resource not accessible by integration (HTTP 403)");
  };
  const errors: string[] = [];
  const garbled: string[] = [];

  assert.equal(run(["validate", "v0.1.0"], fakeTools(repo, denied).tools, (e) => errors.push(e)), 1);
  assert.equal(run(["validate", "v0.1.0"], fakeTools(repo, () => "<html>").tools, (e) => garbled.push(e)), 1);

  assert.match(errors[0] as string, /could not read the required checks .* with this token, so the tag is refused: gh: Resource not accessible/);
  assert.match(garbled[0] as string, /could not read the required checks/);
});

test("read-checks proves a token can read without judging what it finds", (t) => {
  const repo = tempRepo(t);
  repo.commit("feat: a");
  const failing = (args: string[]): string => (args.some((arg) => arg.includes("/rules/")) ? GREEN_RULES : `${JSON.stringify({ name: "Other", status: "completed", conclusion: "success" })}\n`);
  const met = fakeTools(repo, greenGh);
  const unmet = fakeTools(repo, failing);
  const errors: string[] = [];

  assert.equal(run(["read-checks", "abc"], met.tools, assert.fail), 0);
  assert.equal(run(["read-checks", "abc"], unmet.tools, assert.fail), 0);
  assert.equal(run(["read-checks", "abc"], fakeTools(repo, () => "[]").tools, (e) => errors.push(e)), 1);
  assert.equal(run(["read-checks"], met.tools, (e) => errors.push(e)), 1);

  assert.deepEqual(met.lines, ["read 1 required checks and 1 runs on abc", "  Build: met"]);
  assert.equal(unmet.lines[1], "  Build: not met");
  assert.match(errors[0] as string, /no required status checks/);
  assert.match(errors[1] as string, /needs a commit/);
});

function fakeDocker(calls: string[][], manifests: Record<string, string> = {}): Tools["docker"] {
  return (args) => {
    calls.push(args);
    const tag = args[3] as string;
    return args[2] === "inspect" ? JSON.stringify({ digest: manifests[tag] }) : "";
  };
}

test("verify-image waits for the container, compares the version, and always removes it", (t) => {
  const repo = tempRepo(t);
  repo.commit("feat: a");
  const calls: string[][] = [];
  let polls = 0;
  const httpGet = (): string | undefined => (++polls < 3 ? undefined : JSON.stringify({ status: "ok", version: "1.2.3" }));
  const { tools, lines } = fakeTools(repo, undefined, undefined, { docker: fakeDocker(calls), httpGet });

  assert.equal(run(["verify-image", "repo@sha256:abc", "1.2.3"], tools, assert.fail), 0);

  assert.equal(polls, 3);
  assert.ok(calls[0]?.includes("repo@sha256:abc"));
  assert.ok(calls[0]?.some((arg) => /^Memoria__Ingest__DigestKey=.{32,}$/.test(arg)), "the image refuses to start without a digest key");
  assert.deepEqual(calls.at(-1), ["rm", "-f", "memoria-verify"]);
  assert.match(lines[0] as string, /reports 1\.2\.3/);
});

test("verify-image refuses a wrong version or a silent container, and still removes it", (t) => {
  const repo = tempRepo(t);
  repo.commit("feat: a");
  const errors: string[] = [];
  const calls: string[][] = [];

  const wrong = fakeTools(repo, undefined, undefined, { docker: fakeDocker(calls), httpGet: () => JSON.stringify({ version: "9.9.9" }) });
  assert.equal(run(["verify-image", "img", "1.2.3"], wrong.tools, (e) => errors.push(e)), 1);
  assert.match(errors[0] as string, /img reports version 9\.9\.9, expected 1\.2\.3/);
  assert.deepEqual(calls.at(-1), ["rm", "-f", "memoria-verify"]);

  let sleeps = 0;
  const silent = fakeTools(repo, undefined, undefined, { docker: fakeDocker(calls), sleep: () => sleeps++ });
  assert.equal(run(["verify-image", "img", "1.2.3"], silent.tools, (e) => errors.push(e)), 1);
  assert.match(errors[1] as string, /no answer/);
  assert.equal(sleeps, 30);

  const noVersion = fakeTools(repo, undefined, undefined, { docker: fakeDocker(calls), httpGet: () => "{}" });
  assert.equal(run(["verify-image", "img", "1.2.3"], noVersion.tools, (e) => errors.push(e)), 1);
  assert.equal(run(["verify-image", "img"], noVersion.tools, (e) => errors.push(e)), 1);
  assert.match(errors[3] as string, /needs an image and a version/);
});

test("tag-image points every tag at the digest and confirms each one resolves to it", (t) => {
  const repo = tempRepo(t);
  repo.commit("feat: a");
  const calls: string[][] = [];
  const manifests = { "r:1.2.3": "sha256:abc", "r:latest": "sha256:abc" };
  const { tools, lines } = fakeTools(repo, undefined, undefined, { docker: fakeDocker(calls, manifests) });

  assert.equal(run(["tag-image", "r@sha256:abc", "r:1.2.3", "r:latest"], tools, assert.fail), 0);

  assert.deepEqual(calls[0], ["buildx", "imagetools", "create", "--prefer-index=false", "-t", "r:1.2.3", "-t", "r:latest", "r@sha256:abc"]);
  assert.deepEqual(lines, ["r:1.2.3 -> sha256:abc", "r:latest -> sha256:abc"]);
});

test("tag-image refuses a tag that resolves elsewhere, or arguments without a digest or tags", (t) => {
  const repo = tempRepo(t);
  repo.commit("feat: a");
  const errors: string[] = [];
  const drifted = { docker: fakeDocker([], { "r:1.2.3": "sha256:other" }) };

  assert.equal(run(["tag-image", "r@sha256:abc", "r:1.2.3"], fakeTools(repo, undefined, undefined, drifted).tools, (e) => errors.push(e)), 1);
  assert.equal(run(["tag-image", "r@sha256:abc", "r:gone"], fakeTools(repo, undefined, undefined, { docker: fakeDocker([]) }).tools, (e) => errors.push(e)), 1);
  assert.equal(run(["tag-image", "r:no-digest", "r:1.2.3"], fakeTools(repo).tools, (e) => errors.push(e)), 1);
  assert.equal(run(["tag-image", "r@sha256:abc"], fakeTools(repo).tools, (e) => errors.push(e)), 1);
  assert.equal(run(["tag-image"], fakeTools(repo).tools, (e) => errors.push(e)), 1);

  assert.match(errors[0] as string, /resolves to sha256:other, not the verified sha256:abc/);
  assert.match(errors[1] as string, /resolves to nothing/);
  assert.ok(errors.slice(2).every((e) => /needs an image@digest/.test(e)));
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

test("the test helpers keep git's hook variables away from every git command here", () => {
  const helpers = new URL("./helpers.ts", import.meta.url).href;
  const script = `await import(${JSON.stringify(helpers)}); console.log(Object.keys(process.env).filter((k) => k.toUpperCase().startsWith("GIT_")).length);`;
  const out = execFileSync(process.execPath, ["--input-type=module", "-e", script], { encoding: "utf8", env: { ...process.env, GIT_DIR: "/somewhere/real", GIT_INDEX_FILE: "/somewhere/real/index", GIT_WORK_TREE: "/somewhere" } });
  assert.equal(out.trim(), "0");
});
