import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import * as sidecarVersion from "../../../sidecar/src/version.ts";
import * as workerVersion from "../../../worker/src/version.ts";
import { tempDir, tempRepo } from "./helpers.ts";

const REPO_ROOT = fileURLToPath(new URL("../../../", import.meta.url));

test("the sidecar and the worker share one version implementation, byte for byte", () => {
  const read = (file: string): string => readFileSync(path.join(REPO_ROOT, file), "utf8");
  assert.equal(read("sidecar/src/version.ts"), read("worker/src/version.ts"));
});

for (const [name, version] of [["sidecar", sidecarVersion], ["worker", workerVersion]] as const) {
  test(`${name}: describe output maps to one deterministic version`, () => {
    const sha = "0123456789ab";
    assert.equal(version.describeToVersion(`v1.2.3-0-g${sha}`), "1.2.3");
    assert.equal(version.describeToVersion(`v1.2.3-5-g${sha}`), `1.2.4-dev.5+g${sha}`);
    assert.equal(version.describeToVersion(`v1.2.3-5-g${sha}-dirty`), `1.2.4-dev.5+g${sha}.dirty`);
    assert.equal(version.describeToVersion(`v1.2.3-0-g${sha}-dirty`), `1.2.4-dev.0+g${sha}.dirty`);
    assert.equal(version.describeToVersion(sha), `0.0.0-dev+g${sha}`);
    assert.equal(version.describeToVersion(`${sha}-dirty`), `0.0.0-dev+g${sha}.dirty`);
    assert.equal(version.describeToVersion("v1.2.3-rc1-0-gabc"), version.UNKNOWN_VERSION);
    assert.equal(version.describeToVersion(""), version.UNKNOWN_VERSION);
  });

  test(`${name}: a real checkout reports its tag, then a dev version, then dirty`, (t) => {
    const repo = tempRepo(t);
    const first = repo.commit("feat: a");
    assert.equal(version.describedVersion(repo.dir), `0.0.0-dev+g${first.slice(0, 12)}`);

    repo.tag("v1.2.3");
    assert.equal(version.describedVersion(repo.dir), "1.2.3");

    const second = repo.commit("fix: b");
    assert.equal(version.describedVersion(repo.dir), `1.2.4-dev.1+g${second.slice(0, 12)}`);

    writeFileSync(path.join(repo.dir, "file.txt"), "changed\n");
    assert.equal(version.describedVersion(repo.dir), `1.2.4-dev.1+g${second.slice(0, 12)}.dirty`);
  });

  test(`${name}: tags that are not releases do not count`, (t) => {
    const repo = tempRepo(t);
    repo.commit("feat: a");
    repo.tag("v1.0.0");
    const second = repo.commit("fix: b");
    repo.tag("v2.0.0-rc1");
    repo.tag("nightly");
    assert.equal(version.describedVersion(repo.dir), `1.0.1-dev.1+g${second.slice(0, 12)}`);
  });

  test(`${name}: a directory with no git and no stamp is plainly unknown`, (t) => {
    const dir = tempDir(t);
    assert.equal(version.describedVersion(dir), version.UNKNOWN_VERSION);
    assert.equal(version.resolveVersion(dir), version.UNKNOWN_VERSION);
  });

  test(`${name}: a stamp wins over the checkout, and an empty one does not count`, (t) => {
    const repo = tempRepo(t);
    repo.commit("feat: a");
    repo.tag("v1.2.3");
    assert.equal(version.resolveVersion(repo.dir), "1.2.3");

    writeFileSync(path.join(repo.dir, version.STAMP_FILE), "7.7.7\n");
    assert.equal(version.resolveVersion(repo.dir), "7.7.7");

    writeFileSync(path.join(repo.dir, version.STAMP_FILE), "  \n");
    assert.equal(version.resolveVersion(repo.dir), "1.2.3");
  });

  test(`${name}: a stamp at the package root is read from a code directory, with no git anywhere`, (t) => {
    const root = tempDir(t);
    const code = path.join(root, "src");
    mkdirSync(code);
    writeFileSync(path.join(root, version.STAMP_FILE), "4.5.6\n");
    assert.equal(version.resolveVersion(code), "4.5.6");

    // A stamp beside the code is closer than one at the root.
    writeFileSync(path.join(code, version.STAMP_FILE), "4.5.7\n");
    assert.equal(version.resolveVersion(code), "4.5.7");
  });

  test(`${name}: a stamp beats a checkout's tag at the package root too`, (t) => {
    const repo = tempRepo(t);
    repo.commit("feat: a");
    repo.tag("v1.2.3");
    mkdirSync(path.join(repo.dir, "src"));
    assert.equal(version.resolveVersion(path.join(repo.dir, "src")), "1.2.3");
    writeFileSync(path.join(repo.dir, version.STAMP_FILE), "7.7.7\n");
    assert.equal(version.resolveVersion(path.join(repo.dir, "src")), "7.7.7");
  });

  test(`${name}: the module defaults to the checkout it lives in`, () => {
    assert.match(version.describedVersion(), /^\d+\.\d+\.\d+/);
    assert.notEqual(version.resolveVersion(), "");
  });
}

test("a module copied into a tagged checkout reports that tag when it is imported", async (t) => {
  const repo = tempRepo(t);
  repo.commit("feat: a");
  repo.tag("v3.4.5");
  mkdirSync(path.join(repo.dir, "src"));
  const copy = path.join(repo.dir, "src", "version.ts");
  copyFileSync(path.join(REPO_ROOT, "worker", "src", "version.ts"), copy);
  repo.git("add", "src");
  repo.git("commit", "-q", "--amend", "--no-edit");
  repo.git("tag", "-f", "-a", "v3.4.5", "-m", "v3.4.5");

  const program = `import(${JSON.stringify(new URL(`file:///${copy.replaceAll("\\", "/")}`).href)}).then((m) => console.log(m.resolveVersion()))`;
  const out = execFileSync(process.execPath, ["-e", program], { encoding: "utf8" }).trim();

  assert.equal(out, "3.4.5");
});
