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

/** A job's text, from its key to the next job or the end of the file. */
function job(workflow: string, name: string): string {
  const start = workflow.indexOf(`\n  ${name}:\n`);
  assert.notEqual(start, -1, name);
  const next = workflow.slice(start + 1).search(/\n {2}[a-z-]+:\n/);
  return next === -1 ? workflow.slice(start) : workflow.slice(start, start + 1 + next);
}

test("the image workflow stamps the derived version, on the same tag pattern as releases", () => {
  const docker = read(".github/workflows/docker.yml");
  assert.match(docker, /tags: \["v\*\.\*\.\*"\]/);
  assert.match(job(docker, "gate"), /release\.ts validate "\$GITHUB_REF_NAME"/);
  assert.match(docker, /release\.ts describe/);
  assert.equal(docker.match(/build-args: MEMORIA_VERSION=\$\{\{ steps\.version\.outputs\.version \}\}/g)?.length, 2);
  assert.match(read(".github/workflows/release.yml"), /tags: \["v\*\.\*\.\*"\]/);
});

test("the image workflow's jobs hold only what each needs, and only publish can push", () => {
  const docker = read(".github/workflows/docker.yml");
  assert.match(docker, /^permissions: \{\}$/m);
  const permissions = (name: string): string[] => {
    const block = /permissions:\n((?: {6}[a-z-]+: [a-z]+\n)+)/.exec(job(docker, name));
    return (block?.[1] ?? "").trim().split(/\n\s*/);
  };
  assert.deepEqual(permissions("gate"), ["contents: read", "checks: read"]);
  assert.deepEqual(permissions("image"), ["contents: read"]);
  assert.deepEqual(permissions("publish"), ["contents: read", "packages: write"]);
  assert.deepEqual(permissions("attest"), ["contents: read", "id-token: write", "attestations: write"]);
  assert.equal(docker.match(/persist-credentials: false/g)?.length, docker.match(/actions\/checkout@/g)?.length);

  const publish = job(docker, "publish");
  assert.match(publish, /github\.event_name == 'push'/);
  assert.match(publish, /needs: \[gate, image\]/);
  assert.doesNotMatch(job(docker, "image"), /push: true|push=true|login-action/);
});

test("what is published is the digest that was verified, and tags are applied only after", () => {
  const publish = job(read(".github/workflows/docker.yml"), "publish");
  assert.match(publish, /push-by-digest=true/);
  assert.doesNotMatch(publish, /^\s+tags: \$\{\{/m, "the push itself applies no tag");
  const push = publish.indexOf("name: Push by digest");
  const verify = publish.indexOf('verify-image "$IMAGE@$DIGEST"');
  const tag = publish.indexOf('tag-image "$IMAGE@$DIGEST"');
  assert.ok(push !== -1 && push < verify && verify < tag);

  const attest = job(read(".github/workflows/docker.yml"), "attest");
  assert.match(attest, /subject-digest: \$\{\{ needs\.publish\.outputs\.digest \}\}/);
  assert.match(attest, /github\.ref_type == 'tag'/);
});

test("latest moves only on release tags, edge only on main, and compose names a version", () => {
  const docker = read(".github/workflows/docker.yml");
  assert.match(docker, /flavor: latest=false/);
  assert.match(docker, /type=raw,value=latest,enable=\$\{\{ github\.ref_type == 'tag' \}\}/);
  assert.match(docker, /type=raw,value=edge,enable=\$\{\{ github\.ref_type == 'branch' \}\}/);
  assert.match(docker, /type=sha,enable=\$\{\{ github\.ref_type == 'branch' \}\}/, "a tag build must not overwrite main's sha tag");
  assert.doesNotMatch(docker, /is_default_branch|type=ref/);

  const compose = read("deploy/compose.yaml");
  assert.match(compose, /image: ghcr\.io\/mggarofalo\/memoria:\$\{MEMORIA_VERSION:\?/);
  assert.doesNotMatch(compose, /memoria:latest|memoria:edge/);
});

test("every action in every workflow we own is pinned to a commit, with narrow defaults", () => {
  const release = read(".github/workflows/release.yml");
  const docker = read(".github/workflows/docker.yml");
  const uses = [...`${release}\n${docker}`.matchAll(/uses: (\S+)/g)].map((match) => match[1] as string);
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
