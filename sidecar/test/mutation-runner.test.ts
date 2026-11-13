import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, utimesSync } from "node:fs";
import path from "node:path";
import { test } from "node:test";
import { addTally, belowBreak, breaksFor, describe, dotnetConfigOf, gremlinsBreak, gremlinsChangedTally, gremlinsScore, judged, parseChangedLines, passed, repoPath, scoreOf, shardsFor, strykerChangedTally, strykerTally, touchesChange, type DotnetConfig, type DotnetShard, Lock, MAX_ATTEMPTS, overall, packagesFor, parseArgs, parsePackages, planRun, recordRun, repositoryOf, reportName, strykerScore, type RunRecord, type State } from "../../deploy/mutation-runner/runner.mts";
import { tempDir } from "./helpers.ts";

test("a changed path selects the packages whose mutation it can change, and markdown selects none", () => {
  assert.deepEqual(packagesFor(["src/Memoria.Core/Redaction/Rules.cs"]), ["dotnet"]);
  assert.deepEqual(packagesFor(["tests/Memoria.Api.Tests/X.cs", "stryker/core.json", "dotnet-tools.json"]), ["dotnet"]);
  assert.deepEqual(packagesFor(["stryker/shards.json", "scripts/mutation-dotnet-shards.mjs"]), ["dotnet"]);
  assert.deepEqual(packagesFor(["sidecar/src/store.ts", "scripts/mutation-shard.mts"]), ["sidecar"]);
  assert.deepEqual(packagesFor(["worker/test/main.test.ts"]), ["worker"]);
  assert.deepEqual(packagesFor(["cli/cmd/root.go", "scripts/mutation-go.sh"]), ["cli"]);
  // StrykerJS comes from the root package, and one script runs both packages.
  assert.deepEqual(packagesFor(["package-lock.json"]), ["sidecar", "worker"]);
  assert.deepEqual(packagesFor(["scripts/mutation-js.sh"]), ["sidecar", "worker"]);
  assert.deepEqual(packagesFor(["docs/deployment.md", "sidecar/README.md", "README.md", ".github/workflows/ci.yml", "deploy/compose.yaml"]), []);
  // Always in one order, whatever the order of the paths.
  assert.deepEqual(packagesFor(["cli/main.go", "worker/src/auth.ts", "src/Memoria.Api/Program.cs"]), ["dotnet", "worker", "cli"]);
});

test("commits that arrived together are one run on the newest, covering every package any of them changed", () => {
  const plan = planRun({ target: "c3", base: "c0", commits: ["c1", "c2", "c3"], changed: ["worker/src/auth.ts", "cli/main.go"] });
  assert.deepEqual(plan, { target: "c3", base: "c0", commits: ["c1", "c2", "c3"], packages: ["worker", "cli"] });
  assert.equal(planRun({ target: "c3", base: "c3", commits: [], changed: [] }), null, "nothing new");
  // A first run has no base, so everything is mutated.
  assert.deepEqual(planRun({ target: "c3", base: null, commits: [], changed: null }), { target: "c3", base: null, commits: ["c3"], packages: ["dotnet", "sidecar", "worker", "cli"] });
  assert.deepEqual(planRun({ target: "c3", base: "c0", commits: ["c3"], changed: ["README.md"] })?.packages, []);
  assert.deepEqual(planRun({ target: "c3", base: "c0", commits: ["c3"], changed: ["README.md"], packages: ["cli", "dotnet"] })?.packages, ["dotnet", "cli"], "an override, in the usual order");
});

test("options parse, and an unknown package or option is refused", () => {
  assert.deepEqual(parseArgs(["once", "--ref", "probe", "--since", "abc", "--packages", "worker,cli"]), { command: "once", options: { ref: "probe", since: "abc", packages: ["worker", "cli"] } });
  assert.deepEqual(parseArgs([]), { command: "serve", options: {} });
  assert.throws(() => parseArgs(["once", "--ref"]), /needs a value/);
  assert.throws(() => parseArgs(["once", "--force", "x"]), /unknown option/);
  assert.throws(() => parsePackages("worker,python"), /unknown package "python"/);
});

test("scores are Stryker's: detected over what a test could detect", () => {
  const report = { files: { "a.ts": { mutants: [{ status: "Killed" }, { status: "Timeout" }, { status: "Survived" }, { status: "NoCoverage" }, { status: "Ignored" }, { status: "CompileError" }] } } };
  assert.equal(strykerScore(report), 50);
  assert.equal(strykerScore({ files: { "a.ts": { mutants: [{ status: "Ignored" }] } } }), null);
  assert.equal(strykerScore(null), null);
  assert.equal(gremlinsScore({ test_efficacy: 87.5 }), 87.5);
  assert.equal(gremlinsScore({}), null);
});

test("a changed .NET file selects the shards that mutate it, from the repository's own shard list", () => {
  const root = path.resolve(import.meta.dirname, "../..");
  const shards = JSON.parse(execFileSync(process.execPath, ["scripts/mutation-dotnet-shards.mjs"], { cwd: root, encoding: "utf8" })) as DotnetShard[];
  const configs: Record<string, DotnetConfig> = {};
  for (const name of new Set(shards.map((s) => s.configs))) {
    const raw = JSON.parse(readFileSync(path.join(root, "stryker", `${name}.json`), "utf8")) as { "stryker-config": { project: string; mutate?: string[] } };
    configs[name] = { projectDir: path.posix.dirname(raw["stryker-config"].project), mutate: raw["stryker-config"].mutate ?? [] };
  }
  const names = (changed: string[]) => shardsFor(changed, shards, configs).map((s) => s.name);
  // Redaction is not mutated at all, and the part gate is security-core's, not core's.
  assert.deepEqual(names(["src/Memoria.Core/Redaction/GitleaksRules.cs", "src/Memoria.Core/Redaction/NotNamedAnywhere.cs"]), []);
  assert.deepEqual(names(["src/Memoria.Core/Sessions/PartGate.cs", "src/Memoria.Core/Redaction/Redactor.cs"]), ["security-core part gate"]);
  assert.deepEqual(names(["src/Memoria.Core/Sessions/ClaudeTranscript.cs"]), ["core sessions 2"]);
  // A file no shard names lands in its folder's rest shard, or the config's.
  assert.deepEqual(names(["src/Memoria.Core/Sessions/NotNamedAnywhere.cs"]), ["core sessions rest"]);
  assert.deepEqual(names(["src/Memoria.Core/Search/Anything.cs"]), ["core rest"]);
  // Memoria.Api: the api shard takes it all, and the security shard the files its config names.
  assert.deepEqual(names(["src/Memoria.Api/Program.cs"]), ["api"]);
  assert.deepEqual(names(["src/Memoria.Api/Auth/ApiKeyHandler.cs"]), ["api", "security"]);
  // Tests, configs and anything not C# source in a project select no shard.
  assert.deepEqual(names(["tests/Memoria.Core.Tests/RedactorTests.cs", "stryker/core.json", "src/Memoria.Core/Memoria.Core.csproj"]), []);
  // Every Core source file lands in exactly one Core shard.
  for (const file of ["Sessions/ClaudeTranscript.cs", "Sessions/PartGate.cs", "Sessions/Y.cs", "Z.cs"]) {
    assert.equal(names([`src/Memoria.Core/${file}`]).length, 1, file);
  }
});

test("a .NET report belongs to its config, whichever shard wrote it, and shards are scored together", () => {
  assert.equal(dotnetConfigOf("core"), "core");
  assert.equal(dotnetConfigOf("core-core-redaction-1"), "core");
  assert.equal(dotnetConfigOf("security-core"), "security-core");
  assert.equal(dotnetConfigOf("security-security"), "security");
  assert.equal(dotnetConfigOf("api-api"), "api");
  assert.equal(scoreOf(addTally(addTally(undefined, { detected: 9, valid: 10 }), { detected: 1, valid: 10 })), 50);
  assert.equal(scoreOf({ detected: 0, valid: 0 }), null);
  assert.deepEqual(strykerTally({ files: { "a.cs": { mutants: [{ status: "Killed" }, { status: "Survived" }, { status: "Ignored" }] } } }), { detected: 1, valid: 2 });
});

test("a report is named by its config", () => {
  assert.equal(reportName("sidecar/reports/mutation/mutation.json"), "mutation");
  assert.equal(reportName("sidecar/reports/mutation-security/mutation.json"), "mutation-security");
  assert.equal(reportName("StrykerOutput/core/reports/mutation-report.json"), "core");
  assert.equal(reportName("cli/reports/gremlins.json"), "mutation");
});

test("the status line puts failures first, and fits GitHub's 140 characters", () => {
  const results = [
    { package: "worker" as const, exitCode: 0, scores: { mutation: 76.19, "mutation-security": 100 } },
    { package: "sidecar" as const, exitCode: 1, scores: { "mutation-security": 93.1 } },
    { package: "cli" as const, exitCode: 0, scores: {} },
  ];
  assert.equal(describe(results, 3), "sidecar FAILED security 93.1%; worker 76.2% security 100.0%; cli no score (3 commits)");
  assert.equal(overall(results), "failure");
  assert.equal(overall(results.filter((r) => r.exitCode === 0)), "success");
  const long = describe(Array.from({ length: 12 }, () => results[0]!), 1);
  assert.equal(long.length, 140);
  assert.ok(long.endsWith("…"));
});

test("a diff's changed lines are the lines it adds or changes in each file, and a deletion adds none", () => {
  const diff = [
    "diff --git a/src/A.cs b/src/A.cs",
    "index 1..2 100644",
    "--- a/src/A.cs",
    "+++ b/src/A.cs",
    "@@ -289 +289 @@ class A",
    "-\told\r",
    "+\tnew\r",
    "@@ -300,2 +300,0 @@",
    "-gone",
    "-gone too",
    "@@ -310,0 +309,3 @@",
    // Body lines that look like headers stay body lines.
    "++++ b/not/a/file.ts",
    "+@@ -1 +1 @@",
    "+added",
    "diff --git a/old.ts b/old.ts",
    "deleted file mode 100644",
    "--- a/old.ts",
    "+++ /dev/null",
    "@@ -1,2 +0,0 @@",
    "-x",
    "-y",
    "diff --git a/sidecar/src/new.ts b/sidecar/src/new.ts",
    "--- /dev/null",
    "+++ b/sidecar/src/new.ts",
    "@@ -0,0 +1,2 @@",
    "+a",
    "+b",
    "",
  ].join("\n");
  assert.deepEqual(parseChangedLines(diff), new Map([
    ["src/A.cs", [289, 309, 310, 311]],
    ["sidecar/src/new.ts", [1, 2]],
  ]));
  assert.deepEqual(parseChangedLines(""), new Map());
});

test("a mutant counts on a changed-lines run only when its span overlaps a changed line", () => {
  assert.ok(touchesChange([10], 10, 10));
  assert.ok(touchesChange([12], 10, 14), "a block whose middle changed");
  assert.ok(!touchesChange([9, 15], 10, 14));
  assert.ok(!touchesChange(undefined, 1, 100), "a file with no changed line");
  // Report keys become repository paths: Stryker.NET's are absolute in the clone, Gremlins' are the module's.
  assert.equal(repoPath("/state/work/src/Memoria.Core/Sessions/SessionRenderer.cs", "/state/work"), "src/Memoria.Core/Sessions/SessionRenderer.cs");
  assert.equal(repoPath("C:\\w\\src\\A.cs", "C:\\w\\"), "src/A.cs");
  assert.equal(repoPath("sidecar/src/jobs.ts", "/state/work"), "sidecar/src/jobs.ts");
  assert.equal(repoPath("internal/api/x.go", "/state/work", "cli/"), "cli/internal/api/x.go");

  const changed = new Map([["src/A.cs", [5, 20]], ["src/B.cs", [1]]]);
  const at = (status: string, start: number, end = start) => ({ status, location: { start: { line: start, column: 1 }, end: { line: end, column: 9 } } });
  const report = {
    files: {
      "/w/src/A.cs": { mutants: [at("Killed", 5), at("Survived", 3, 7), at("NoCoverage", 20), at("Survived", 6), at("Ignored", 5), at("Killed", 21)] },
      "/w/src/B.cs": { mutants: [at("Timeout", 1), { status: "Survived" }] },
      "/w/src/C.cs": { mutants: [at("Survived", 1)] },
    },
  };
  // Killed@5, Survived 3-7 and NoCoverage@20 in A; Timeout@1 in B. Not the unchanged line 6, the line 21, C, or a mutant with no location.
  assert.deepEqual(strykerChangedTally(report, changed, (key) => repoPath(key, "/w")), { detected: 2, valid: 4 });
  assert.deepEqual(strykerChangedTally(null, changed, (key) => key), { detected: 0, valid: 0 });

  const gremlins = {
    test_efficacy: 50,
    files: [
      { file_name: "cmd/a.go", mutations: [{ status: "KILLED", line: 4 }, { status: "LIVED", line: 4 }, { status: "NOT COVERED", line: 4 }, { status: "TIMED OUT", line: 4 }, { status: "LIVED", line: 5 }] },
      { file_name: "cmd/b.go", mutations: [{ status: "LIVED", line: 4 }] },
    ],
  };
  // Killed over killed and lived, as Gremlins' own efficacy: not covered and timed out count for neither.
  assert.deepEqual(gremlinsChangedTally(gremlins, new Map([["cli/cmd/a.go", [4]]]), (key) => repoPath(key, "/w", "cli/")), { detected: 1, valid: 2 });
  assert.deepEqual(gremlinsChangedTally({}, new Map(), (key) => key), { detected: 0, valid: 0 });
});

test("a changed-lines score is judged against its config's break, and no score is no failure", () => {
  assert.deepEqual(belowBreak({ core: 89.9, api: 98, "security-core": 100 }, { core: 90, api: 98, "security-core": 90, security: 98 }), ["core"]);
  assert.deepEqual(belowBreak({}, { core: 90 }), [], "no mutant on a changed line");
  assert.deepEqual(belowBreak({ mutation: 10 }, {}), [], "a config with no break");
  assert.equal(gremlinsBreak("unleash:\n  threshold:\n    efficacy: 92\n    mutant-coverage: 88\n"), 92);
  assert.equal(gremlinsBreak(readFileSync(path.resolve(import.meta.dirname, "../../cli/.gremlins.yaml"), "utf8")) !== null, true, "the repository's own config");
  assert.equal(gremlinsBreak("unleash:\n  exclude-files: []\n"), null);

  // Each break is found under the name its config's score goes by, from the repository's own configs.
  const root = path.resolve(import.meta.dirname, "../..");
  const keys = (pkg: Parameters<typeof breaksFor>[0]) => Object.keys(breaksFor(pkg, root)).sort();
  assert.deepEqual(keys("dotnet"), ["api", "core", "security", "security-core"]);
  assert.deepEqual(keys("sidecar"), ["mutation", "mutation-integration", "mutation-security"]);
  assert.deepEqual(keys("worker"), ["mutation", "mutation-security"]);
  assert.deepEqual(keys("cli"), ["mutation"]);
  for (const pkg of ["dotnet", "sidecar", "worker", "cli"] as const) for (const value of Object.values(breaksFor(pkg, root))) assert.ok(value > 0 && value <= 100, `${pkg} ${value}`);

  const ran = { package: "dotnet" as const, exitCode: 0, scores: { core: 80 }, below: ["core"] };
  const fine = { package: "cli" as const, exitCode: 0, scores: {}, below: [] };
  const broke = { package: "worker" as const, exitCode: 1, scores: {}, below: [] };
  assert.ok(!passed(ran), "under its break");
  assert.ok(passed(fine));
  assert.ok(!passed(broke), "a tool that failed still fails the run");
  assert.equal(overall([fine]), "success");
  assert.equal(overall([fine, ran]), "failure");
  assert.equal(describe([fine, ran, broke], 1, true), "changed lines: dotnet FAILED core 80.0%; worker FAILED no score; cli no score");
});

test("the repository comes from the clone URL", () => {
  assert.equal(repositoryOf("https://github.com/mggarofalo/memoria.git"), "mggarofalo/memoria");
  assert.equal(repositoryOf("git@github.com:mggarofalo/memoria.git"), "mggarofalo/memoria");
  assert.equal(repositoryOf("https://github.com/mggarofalo/memoria"), "mggarofalo/memoria");
  assert.throws(() => repositoryOf("https://gitlab.com/a/b.git"), /GITHUB_REPOSITORY/);
});

test("a run marks its commit mutated, and a runner error leaves it queued until the third", () => {
  const run = (state: RunRecord["state"]): RunRecord => ({ target: "c3", base: "c0", ref: "main", commits: 3, packages: ["cli"], state, description: "d", finishedAt: "t" });
  const start: State = { mutated: { main: "c0" }, attempts: {}, history: [] };
  assert.deepEqual(recordRun(start, run("failure")).mutated, { main: "c3" });
  const once = recordRun(start, run("error"));
  assert.deepEqual([once.mutated, once.attempts], [{ main: "c0" }, { c3: 1 }]);
  const twice = recordRun(once, run("error"));
  assert.deepEqual([twice.mutated, twice.attempts], [{ main: "c0" }, { c3: 2 }]);
  assert.equal(MAX_ATTEMPTS, 3);
  const given = recordRun(twice, run("error"));
  assert.deepEqual([given.mutated, given.attempts], [{ main: "c3" }, {}], "the third error gives up and moves on");
  assert.equal(given.history[0]!.state, "failure");
  assert.match(given.history[0]!.description, /^gave up after 3 runner errors: d/);
  // A success after an error clears the count.
  assert.deepEqual(recordRun(once, run("success")).attempts, {});
  const many = Array.from({ length: 60 }, () => run("success"));
  assert.equal(many.reduce<State>((s, r) => recordRun(s, r), { mutated: {}, attempts: {}, history: [] }).history.length, 50, "history is bounded");
});

test("an exit code judges the commit only when the mutation finished", () => {
  for (const code of [0, 1, 2]) assert.ok(judged(code), String(code));
  // Killed by a signal, as by the OOM killer, or a command that is not there: the runner's error, not the commit's.
  for (const code of [127, 128, 137, 143]) assert.ok(!judged(code), String(code));
});

test("the lock keeps a second run out until the first releases it or goes quiet for ten minutes", async () => {
  const file = path.join(await tempDir(), "lock");
  let now = Date.now();
  const first = new Lock(file, { now: () => now });
  const second = new Lock(file, { now: () => now });
  assert.deepEqual(first.acquire(), { held: true });
  const refused = second.acquire();
  assert.equal(refused.held, false);
  assert.match(!refused.held ? refused.by : "", /pid \d+ since/);
  first.release();
  assert.ok(!existsSync(file));
  assert.deepEqual(second.acquire(), { held: true });
  // The holder died without releasing: once its heartbeat is ten minutes old, the lock is free.
  const old = new Date(now - 11 * 60_000);
  utimesSync(file, old, old);
  assert.deepEqual(first.acquire(), { held: true });
  // A heartbeat keeps a long run's lock fresh.
  now += 11 * 60_000;
  first.touch();
  assert.equal(second.acquire().held, false);
  first.release();
});
