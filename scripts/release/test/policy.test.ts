import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

/** The rules that keep the tag the only version, which nothing else would catch being broken. */

const REPO_ROOT = fileURLToPath(new URL("../../../", import.meta.url));
const read = (file: string): string => readFileSync(path.join(REPO_ROOT, file), "utf8");

test("no component carries a hand-maintained version", () => {
  for (const file of ["sidecar/package.json", "worker/package.json"]) {
    assert.equal(JSON.parse(read(file)).version, "0.0.0", file);
  }
  for (const file of ["sidecar/package-lock.json", "worker/package-lock.json"]) {
    const lock = JSON.parse(read(file));
    assert.equal(lock.version, "0.0.0", file);
    assert.equal(lock.packages[""].version, "0.0.0", file);
  }
  assert.match(read("src/Memoria.Api/Memoria.Api.csproj"), /<Version>0\.0\.0-dev<\/Version>/);
  assert.doesNotMatch(read("src/Memoria.Api/Memoria.Api.csproj"), /<InformationalVersion>/);
  assert.match(read("cli/cmd/root.go"), /var Version = "dev"/);
  assert.match(read("sidecar/src/app.ts"), /SIDECAR_VERSION = resolveVersion\(\)/);
  assert.match(read("worker/src/server.ts"), /WORKER_VERSION = resolveVersion\(\)/);
});

test("the Dockerfile stamps the API from a build argument", () => {
  const dockerfile = read("Dockerfile");
  assert.match(dockerfile, /ARG MEMORIA_VERSION=0\.0\.0-dev/);
  assert.match(dockerfile, /-p:Version=\$MEMORIA_VERSION/);
});

test("the image workflow stamps the derived version, on the same tag pattern as releases", () => {
  const docker = read(".github/workflows/docker.yml");
  assert.match(docker, /tags: \["v\*\.\*\.\*"\]/);
  assert.match(docker, /release\.ts validate "\$GITHUB_REF_NAME"/);
  assert.match(docker, /release\.ts describe/);
  assert.equal(docker.match(/build-args: MEMORIA_VERSION=\$\{\{ steps\.version\.outputs\.version \}\}/g)?.length, 2);
  assert.match(read(".github/workflows/release.yml"), /tags: \["v\*\.\*\.\*"\]/);
});

test("the release workflow pins every action to a commit and keeps permissions narrow", () => {
  const release = read(".github/workflows/release.yml");
  const uses = [...release.matchAll(/uses: (\S+)/g)].map((match) => match[1] as string);
  assert.ok(uses.length > 0);
  for (const action of uses) assert.match(action, /@[0-9a-f]{40}$/, action);

  assert.match(release, /^permissions: \{\}$/m);
  assert.doesNotMatch(release.slice(0, release.indexOf("  publish:")), /write/, "the build job holds no write permission");
  assert.match(release, /if: github\.event_name == 'push'/);
  assert.match(release, /gh release create "\$GITHUB_REF_NAME" --verify-tag/);
  assert.doesNotMatch(release, /--generate-notes/);
});

test("the release skill stays free of tracker references", () => {
  const skill = read(".claude/skills/release/SKILL.md");
  assert.match(skill, /^---\nname: release\n/);
  assert.doesNotMatch(skill, /\bplane\b|MEMORIA-[0-9]/i);
});
