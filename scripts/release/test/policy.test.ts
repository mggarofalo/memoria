import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
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

test("the sidecar image is patched at install, stamped, non-root, and holds no way to run a model", () => {
  const dockerfile = read("sidecar/Dockerfile");
  assert.ok(dockerfile.indexOf("COPY scripts/patch-qmd.ts") < dockerfile.indexOf("RUN npm ci"), "postinstall needs the patch");
  assert.doesNotMatch(dockerfile, /^RUN .*--ignore-scripts/m);
  assert.match(dockerfile, /ARG MEMORIA_VERSION=0\.0\.0-dev/);
  assert.match(dockerfile, /dist\/VERSION/);
  assert.match(dockerfile, /^USER node$/m);
  assert.match(dockerfile, /rm -rf node_modules\/@node-llama-cpp/);
  assert.match(dockerfile, /MEMORIA_QMD_LLM_FALLBACK=0/);
});

test("the deployment doc lists exactly the variables compose reads, with the defaults compose gives them", () => {
  const compose = read("deploy/compose.yaml");
  const variables = new Map<string, string | undefined>();
  for (const match of compose.matchAll(/\$\{([A-Z_]+)(:-|:\?)?([^}]*)\}/g)) {
    const [, name, kind, rest] = match;
    if (name === undefined) continue;
    if (kind === ":-") variables.set(name, rest);
    else if (!variables.has(name)) variables.set(name, undefined);
  }
  const doc = read("docs/deployment.md");
  const section = (heading: string): string => {
    const start = doc.indexOf(`### ${heading}\n`);
    assert.notEqual(start, -1, heading);
    const end = doc.indexOf("\n### ", start + 1);
    return doc.slice(start, end === -1 ? undefined : end);
  };
  const rows = (text: string): Map<string, string> =>
    new Map([...text.matchAll(/^\| `([A-Z_]+)` \| ([^|]*) \|/gm)].map((row) => [row[1] as string, (row[2] as string).trim()]));
  const required = rows(section("Required environment"));
  const optional = rows(section("Optional environment"));
  const documented = new Set([...required.keys(), ...optional.keys()]);
  assert.deepEqual([...variables.keys()].filter((name) => !documented.has(name)).sort(), [], "compose reads these, and the doc does not list them");
  assert.deepEqual([...documented].filter((name) => !variables.has(name)).sort(), [], "the doc lists these, and compose does not read them");
  for (const [name, fallback] of variables) {
    if (fallback === undefined) assert.ok(required.has(name), `${name} has no default, so it is required`);
    else if (fallback === "") assert.equal(optional.get(name), "empty", name);
    else assert.match(optional.get(name) ?? "", new RegExp(fallback.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), name);
  }
});

test("compose keeps the sidecar unpublished, off the fallback, and the API on its heap", () => {
  const compose = read("deploy/compose.yaml");
  const sidecar = compose.slice(compose.indexOf("  memoria-qmd:\n"), compose.indexOf("  memoria-api:\n"));
  assert.doesNotMatch(sidecar, /^\s+ports:/m, "only the API may reach the sidecar");
  assert.match(sidecar, /MEMORIA_QMD_LLM_FALLBACK: "0"/);
  assert.doesNotMatch(sidecar, /gpus|devices|runtime:/);
  assert.match(compose, /Memoria__Sidecar__BaseUrl: http:\/\/memoria-qmd:8282/);
  assert.match(compose, /DOTNET_GCHeapHardLimit: "0x40000000"/);
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
  assert.equal(docker.match(/build-args: MEMORIA_VERSION=\$\{\{ steps\.version\.outputs\.version \}\}/g)?.length, 4);
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
  assert.match(publish, /needs: \[gate, image, sidecar-image\]/);
  assert.doesNotMatch(job(docker, "image"), /push: true|push=true|login-action/);
  assert.doesNotMatch(job(docker, "sidecar-image"), /push: true|push=true|login-action/);
  assert.deepEqual(permissions("sidecar-publish"), ["contents: read", "packages: write"]);
  assert.deepEqual(permissions("sidecar-attest"), ["contents: read", "id-token: write", "attestations: write"]);
});

test("what is published is the digest that was verified, and tags are applied only after", () => {
  const publish = job(read(".github/workflows/docker.yml"), "publish");
  assert.match(publish, /push-by-digest=true/);
  assert.doesNotMatch(publish, /^\s+tags: \$\{\{/m, "the push itself applies no tag");
  const push = publish.indexOf("name: Push by digest");
  const verify = publish.indexOf('verify-image "$IMAGE@$DIGEST"');
  const tag = publish.indexOf('tag-image "$IMAGE@$DIGEST"');
  assert.ok(push !== -1 && push < verify && verify < tag);

  const sidecar = job(read(".github/workflows/docker.yml"), "sidecar-publish");
  assert.match(sidecar, /push-by-digest=true/);
  const sidecarVerify = sidecar.indexOf('verify-sidecar-image "$SIDECAR_IMAGE@$DIGEST"');
  const sidecarTag = sidecar.indexOf('tag-image "$SIDECAR_IMAGE@$DIGEST"');
  assert.ok(sidecar.indexOf("name: Push by digest") < sidecarVerify && sidecarVerify < sidecarTag && sidecarVerify !== -1);

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
  assert.match(compose, /image: ghcr\.io\/mggarofalo\/memoria-qmd:\$\{MEMORIA_VERSION:\?/);
  assert.match(compose, /image: ghcr\.io\/mggarofalo\/memoria:\$\{MEMORIA_VERSION\}/);
  assert.doesNotMatch(compose, /memoria(-qmd)?:(latest|edge)/);
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

test("the required checks AGENTS.md names are workflow jobs, and it counts them right", () => {
  const sentence = /^(\w+) gates are required status checks on `main`: (.*?)\.(?: |$)/m.exec(read("AGENTS.md"));
  assert.ok(sentence, "AGENTS.md names the required checks");
  const names = [...sentence[2]!.matchAll(/`([^`]+)`/g)].map((m) => m[1]!);
  const counts = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"];
  assert.equal(counts.indexOf(sentence[1]!.toLowerCase()), names.length);
  const jobs = new Set([...`${read(".github/workflows/ci.yml")}\n${read(".github/workflows/docker.yml")}`.matchAll(/^ {4}name: (.+)$/gm)].map((m) => m[1]!.trim()));
  assert.deepEqual(names.filter((n) => !jobs.has(n)), [], "named in AGENTS.md but no job has that name");
});

test("docs say what the code does now, not what is planned or in progress", () => {
  const status = [
    /\b(?:is|are) (?:still )?in progress\b/i,
    /\bcomes? later\b/i,
    /\blater (?:epic|work|phase)s?\b/i,
    /\bheading to\b/i,
    /\bowner's decision\b|\bwaiting on the owner\b|\bto be decided\b/i,
    /\bnot yet (?:merged|confirmed|run|done|built|implemented|supported|decided)\b|\byet to be\b|\bhas yet to\b/i,
    /\bfor now\b|\(today\)|\btoday;/i,
    /\bstill unproven\b|\bwill (?:land|ship|be added|be built|come)\b/i,
    /\bTODO\b/,
  ];
  const files = execFileSync("git", ["ls-files", "*.md"], { cwd: REPO_ROOT, encoding: "utf8" })
    .split("\n")
    .filter((f) => f !== "" && !f.includes("Sessions/Fixtures/"));
  assert.ok(files.length > 10, "found the docs");
  const found = files.flatMap((file) =>
    read(file)
      .split("\n")
      .flatMap((line, i) => status.filter((p) => p.test(line)).map((p) => `${file}:${i + 1} ${p}`)),
  );
  assert.deepEqual(found, [], "Plane tracks status and plans; a doc describes what is true now");
});

test("the release skill stays free of tracker references", () => {
  const skill = read(".claude/skills/release/SKILL.md");
  assert.match(skill, /^---\nname: release\n/);
  assert.doesNotMatch(skill, /\bplane\b|MEMORIA-[0-9]/i);
});
