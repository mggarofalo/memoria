import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { writeFileSync } from "node:fs";
import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { describedVersion, describeToVersion, resolveVersion, STAMP_FILE, UNKNOWN_VERSION } from "../src/version.ts";

// A git hook exports these, and they would point git at this repository from anywhere.
for (const name of ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE"]) delete process.env[name];

test("git describe output maps to the documented versions", () => {
  const cases: Array<[string, string]> = [
    ["v1.2.3-0-gabc123", "1.2.3"],
    ["v1.2.3-0-gabc123-dirty", "1.2.4-dev.0+gabc123.dirty"],
    ["v1.2.3-5-gabc123", "1.2.4-dev.5+gabc123"],
    ["v1.2.3-5-gabc123-dirty", "1.2.4-dev.5+gabc123.dirty"],
    ["abc123", "0.0.0-dev+gabc123"],
    ["abc123-dirty", "0.0.0-dev+gabc123.dirty"],
    ["not a version", UNKNOWN_VERSION],
    ["", UNKNOWN_VERSION],
  ];
  for (const [described, expected] of cases) assert.equal(describeToVersion(described), expected, described);
});

test("describe output that is not exactly a release or a commit is unknown", () => {
  const unknown = [
    "xv1.2.3-0-gabc123", // something before the tag
    "v1.2.3-0-gabc123x", // something after the hash
    "v1.2.3-0-gabc123-dirtyx",
    "v1.2-0-gabc123", // not three numbers
    "v1.2.3.4-0-gabc123",
    "va.2.3-0-gabc123",
    "v1.2.3-x-gabc123",
    "v1.2.3-0-abc123", // no g before the hash
    "v1.2.3-0-gxyz", // not hex
    "v1.2.3-0-g",
    "xabc123", // an untagged hash, with something around it
    "abc123x",
    "abc123-dirtyx",
    "-dirty",
    "abc 123",
  ];
  for (const described of unknown) assert.equal(describeToVersion(described), UNKNOWN_VERSION, described);
  // Numbers, not text: patch 9 is followed by 10, and every part, however many digits, survives as written.
  assert.equal(describeToVersion("v10.20.9-3-g0123456789ab"), "10.20.10-dev.3+g0123456789ab");
  assert.equal(describeToVersion("v10.20.9-0-g0123456789ab"), "10.20.9");
  assert.equal(describeToVersion("v1.2.30-0-g0123456789ab"), "1.2.30");
  assert.equal(describeToVersion("v1.2.30-12-g0123456789ab"), "1.2.31-dev.12+g0123456789ab");
  // The documented fallback, spelled out: comparing with the constant alone would accept any value.
  assert.equal(UNKNOWN_VERSION, "0.0.0-dev+unknown");
  assert.equal(describeToVersion("0123456789ab"), "0.0.0-dev+g0123456789ab");
});

/** A throwaway repository, driven with fixed identity and no signing, so it behaves the same on any machine. */
async function scratchRepo() {
  const dir = await mkdtemp(path.join(tmpdir(), "memoria-version-git-"));
  const git = (...args: string[]) =>
    execFileSync("git", ["-c", "user.name=test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false", ...args], { cwd: dir, encoding: "utf8" }).trim();
  git("init", "-q");
  let count = 0;
  const commit = () => {
    count++;
    writeFileSync(path.join(dir, "file.txt"), `${count}\n`);
    git("add", "file.txt");
    git("commit", "-q", "-m", `change ${count}`);
    return git("rev-parse", "HEAD").slice(0, 12);
  };
  return { dir, git, commit };
}

// No deadline in these: how long git takes to start is up to the machine, not the code under test.
test("a real checkout describes itself: a commit, then its release tag, then commits ahead, then uncommitted changes", async () => {
  const repo = await scratchRepo();
  const first = repo.commit();
  assert.equal(describedVersion(repo.dir, 0), `0.0.0-dev+g${first}`, "no tag yet: the abbreviated hash, 12 characters");
  repo.git("tag", "v1.2.3"); // lightweight, as a release script may make
  assert.equal(describedVersion(repo.dir, 0), "1.2.3");
  assert.equal(resolveVersion(repo.dir, 0), "1.2.3", "with no stamp, the checkout decides");
  const second = repo.commit();
  assert.equal(describedVersion(repo.dir, 0), `1.2.4-dev.1+g${second}`);
  writeFileSync(path.join(repo.dir, "file.txt"), "uncommitted\n");
  assert.equal(describedVersion(repo.dir, 0), `1.2.4-dev.1+g${second}.dirty`);
});

test("a real checkout passes over tags that are not releases", async () => {
  const repo = await scratchRepo();
  repo.commit();
  repo.git("tag", "v1.0.0");
  const second = repo.commit();
  repo.git("tag", "v2.0.0-rc1"); // a prerelease
  repo.git("tag", "nightly"); // not a version at all
  repo.git("tag", "release-3.0.0");
  assert.equal(describedVersion(repo.dir, 0), `1.0.1-dev.1+g${second}`);
});

test("a stamp file wins, with surrounding space removed", async () => {
  const dir = await mkdtemp(path.join(tmpdir(), "memoria-version-"));
  await writeFile(path.join(dir, STAMP_FILE), "  7.8.9\n");
  assert.equal(resolveVersion(dir), "7.8.9");
});

test("without a usable stamp or git, the version is unknown", async () => {
  // A temp directory is outside any checkout, so git has nothing to describe.
  const missing = await mkdtemp(path.join(tmpdir(), "memoria-version-"));
  assert.equal(resolveVersion(missing), UNKNOWN_VERSION);
  assert.equal(describedVersion(missing), UNKNOWN_VERSION);
  const empty = await mkdtemp(path.join(tmpdir(), "memoria-version-"));
  await writeFile(path.join(empty, STAMP_FILE), "  \n");
  assert.equal(resolveVersion(empty), UNKNOWN_VERSION);
});
