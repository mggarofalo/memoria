import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFile, stat, writeFile } from "node:fs/promises";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { tempDir } from "./helpers.ts";

/**
 * What can be checked without installing anything: scripts parse, the plan a
 * dry run prints is right, and the ACL helpers work on a temp directory. No test
 * here registers a service, creates an account, or runs NSSM.
 */

const repo = fileURLToPath(new URL("../../", import.meta.url));
const at = (...parts: string[]) => path.join(repo, ...parts);
const SECRET_CANARY = "canary-secret-value-0123456789";

function findPowerShell(): string | undefined {
  for (const exe of ["pwsh", "powershell"]) {
    if (spawnSync(exe, ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], { stdio: "ignore" }).status === 0) return exe;
  }
  return undefined;
}

const powershell = findPowerShell();
const needsPowerShell = powershell ? false : "PowerShell is not installed";
// Git Bash and WSL disagree with Node about paths on Windows; this runs on POSIX hosts, and by hand on Windows.
const needsPosixBash = process.platform === "win32" || spawnSync("bash", ["--version"], { stdio: "ignore" }).status !== 0 ? "needs bash on a POSIX host" : false;

function runPowerShell(args: string[], env: NodeJS.ProcessEnv = {}) {
  return spawnSync(powershell!, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", ...args], { encoding: "utf8", env: { ...process.env, ...env }, timeout: 120_000 });
}

const scripts = [
  at("sidecar", "scripts", "install-service.ps1"),
  at("sidecar", "scripts", "uninstall-service.ps1"),
  at("worker", "scripts", "install-service.ps1"),
  at("worker", "scripts", "uninstall-service.ps1"),
  at("deploy", "windows", "MemoriaService.psm1"),
  at("sidecar", "test", "fixtures", "memoria-service-checks.ps1"),
];

test("every PowerShell script parses", { skip: needsPowerShell }, () => {
  for (const script of scripts) {
    const command = `$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('${script.replace(/'/g, "''")}', [ref]$null, [ref]$errors); if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Output $_.Message }; exit 1 }`;
    const run = runPowerShell(["-Command", command]);
    assert.equal(run.status, 0, `${script}\n${run.stdout}${run.stderr}`);
  }
});

test("the service module's checks pass, including real ACLs on Windows", { skip: needsPowerShell }, () => {
  const run = runPowerShell(["-File", at("sidecar", "test", "fixtures", "memoria-service-checks.ps1"), "-ModulePath", at("deploy", "windows", "MemoriaService.psm1")]);
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.match(run.stdout, /all checks passed/);
});

const dirs = ["-InstallDir", "/pf/svc", "-StateDir", "/pd/svc"];

function sidecarDryRun(extra: string[] = [], env: NodeJS.ProcessEnv = {}) {
  return runPowerShell(["-File", at("sidecar", "scripts", "install-service.ps1"), "-WhatIf", ...dirs, "-CacheHome", "/home/u/.cache", "-ConfigHome", "/home/u/.config", "-CollectionRoot", "/corpus", ...extra], { MEMORIA_QMD_SECRET: "", MEMORIA_QMD_DB_PATH: "", ...env });
}

function workerDryRun(extra: string[] = [], env: NodeJS.ProcessEnv = {}) {
  return runPowerShell(["-File", at("worker", "scripts", "install-service.ps1"), "-WhatIf", ...dirs, "-LlamaServer", "/pf/llama/llama-server", "-ModelDir", "/models", ...extra], { MEMORIA_WORKER_TOKEN: "", ...env });
}

test("the sidecar's dry run keeps the secret out of the plan and out of the registry environment", { skip: needsPowerShell }, () => {
  const run = sidecarDryRun(["-BindHost", "192.168.1.20", "-AllowHttpOnPrivateNetwork"], { MEMORIA_QMD_SECRET: SECRET_CANARY });
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.ok(!(run.stdout + run.stderr).includes(SECRET_CANARY), "the secret must not appear anywhere in the output");
  assert.match(run.stdout, /AppEnvironmentExtra .*MEMORIA_QMD_SECRET_FILE=/);
  assert.doesNotMatch(run.stdout, /MEMORIA_QMD_SECRET=/);
  assert.match(run.stdout, /sc\.exe config memoria-qmd obj= "?NT SERVICE\\memoria-qmd/);
  assert.match(run.stdout, /Verify nobody else can read/);
  assert.match(run.stdout, /AppRotateOnline 1/);
  assert.match(run.stdout, /\/corpus \/grant .*NT SERVICE\\memoria-qmd/);
  assert.match(run.stdout, /npm ci --omit=dev/);
  assert.doesNotMatch(run.stdout, /\b(?:USERPROFILE|HOME)=/);
  assert.doesNotMatch(run.stdout, /LocalSystem/i);
});

test("the worker's dry run keeps the token out, and grants read on models and llama-server", { skip: needsPowerShell }, () => {
  const refused = workerDryRun(["-BindHost", "192.168.1.20"], { MEMORIA_WORKER_TOKEN: SECRET_CANARY });
  assert.notEqual(refused.status, 0);
  assert.match(refused.stdout + refused.stderr, /AllowHttpOnPrivateNetwork/);
  const run = workerDryRun(["-BindHost", "192.168.1.20", "-AllowHttpOnPrivateNetwork"], { MEMORIA_WORKER_TOKEN: SECRET_CANARY });
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.ok(!(run.stdout + run.stderr).includes(SECRET_CANARY));
  assert.match(run.stdout, /MEMORIA_WORKER_ALLOW_HTTP_ON_PRIVATE_NETWORK=1/);
  assert.match(run.stdout, /MEMORIA_WORKER_TOKEN_FILE=/);
  assert.doesNotMatch(run.stdout, /MEMORIA_WORKER_TOKEN=/);
  assert.match(run.stdout, /NT SERVICE\\memoria-worker/);
  // The install has no .git, so the copy must be stamped or it could only report `unknown`.
  assert.match(run.stdout, /write-version\.ts/);
  assert.match(run.stdout, /AppKillProcessTree 1/);
  assert.match(run.stdout, /\/models \/grant .*RX/);
  assert.match(run.stdout, /[\\/]pf[\\/]llama \/grant .*RX/);
});

test("the sidecar's service runs with the heap limit and the SDK patch's script, and says so in its plan", { skip: needsPowerShell }, () => {
  const run = sidecarDryRun();
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.match(run.stdout, /AppParameters .*--max-old-space-size=4096/);
  assert.match(run.stdout, /scripts/);
  assert.doesNotMatch(run.stdout, /MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK/, "on loopback, no opt-in");
  const worker = workerDryRun();
  assert.equal(worker.status, 0, worker.stdout + worker.stderr);
  assert.doesNotMatch(worker.stdout, /--max-old-space-size/);
});

test("a LAN bind needs the plaintext opt-in, and the service gets it when given", { skip: needsPowerShell }, () => {
  const refused = sidecarDryRun(["-BindHost", "192.168.1.20"], { MEMORIA_QMD_SECRET: SECRET_CANARY });
  assert.notEqual(refused.status, 0);
  assert.match(refused.stdout + refused.stderr, /AllowHttpOnPrivateNetwork/);
  const allowed = sidecarDryRun(["-BindHost", "192.168.1.20", "-AllowHttpOnPrivateNetwork"], { MEMORIA_QMD_SECRET: SECRET_CANARY });
  assert.equal(allowed.status, 0, allowed.stdout + allowed.stderr);
  assert.match(allowed.stdout, /MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK=1/);
});

test("no install script offers a secret as a parameter, and no comment claims the old behaviour", async () => {
  for (const file of [at("sidecar", "scripts", "install-service.ps1"), at("worker", "scripts", "install-service.ps1")]) {
    const text = await readFile(file, "utf8");
    assert.doesNotMatch(text, /\[string\]\$(Secret|Token)\b/);
    assert.doesNotMatch(text, /environment block/);
    assert.doesNotMatch(text, /LocalSystem/i);
  }
});

test("a dry run refuses a LAN bind with no secret, and takes one from a file", { skip: needsPowerShell }, async () => {
  const sidecar = sidecarDryRun(["-BindHost", "192.168.1.20"]);
  assert.notEqual(sidecar.status, 0);
  assert.match(sidecar.stdout + sidecar.stderr, /shared secret is required/);
  const worker = workerDryRun(["-BindHost", "192.168.1.20"]);
  assert.notEqual(worker.status, 0);
  assert.match(worker.stdout + worker.stderr, /token is required/);

  const file = path.join(await tempDir("memoria-install-"), "s.txt");
  await writeFile(file, `${SECRET_CANARY}\n`);
  const fromFile = sidecarDryRun(["-BindHost", "192.168.1.20", "-AllowHttpOnPrivateNetwork", "-SecretFile", file]);
  assert.equal(fromFile.status, 0, fromFile.stdout + fromFile.stderr);
  assert.ok(!(fromFile.stdout + fromFile.stderr).includes(SECRET_CANARY));
  assert.match(fromFile.stdout, /MEMORIA_QMD_SECRET_FILE=/);

  const loopback = sidecarDryRun();
  assert.equal(loopback.status, 0, loopback.stdout + loopback.stderr);
  assert.doesNotMatch(loopback.stdout, /MEMORIA_QMD_SECRET_FILE=/);
});

test("uninstall dry runs remove the secret and keep the logs", { skip: needsPowerShell }, () => {
  for (const [component, service] of [["sidecar", "memoria-qmd"], ["worker", "memoria-worker"]] as const) {
    const run = runPowerShell(["-File", at(component, "scripts", "uninstall-service.ps1"), "-WhatIf", ...dirs, "-DataPath", "/data"]);
    assert.equal(run.status, 0, run.stdout + run.stderr);
    assert.match(run.stdout, new RegExp(`icacls\\.exe /data /remove "?NT SERVICE\\\\${service}`));
    assert.match(run.stdout, /Remove the secret/);
    assert.doesNotMatch(run.stdout, /logs/);
  }
});

// --- launchd ---------------------------------------------------------------------

const launchd = at("worker", "scripts", "install-launchd.sh");

function launchdDryRun(root: string, llama: string, env: NodeJS.ProcessEnv, extra: string[] = []) {
  return spawnSync("bash", [launchd, "--dry-run", "--root", root, "--llama-server", llama, ...extra], { encoding: "utf8", env: { ...process.env, MEMORIA_WORKER_TOKEN: "", ...env } });
}

async function launchdFixture(): Promise<{ root: string; llama: string; plist: string; tokenFile: string }> {
  const root = await tempDir("memoria-launchd-");
  const llama = path.join(root, "llama-server");
  await writeFile(llama, "#!/bin/sh\n", { mode: 0o755 });
  return {
    root,
    llama,
    plist: path.join(root, "Library", "LaunchAgents", "com.memoria.worker.plist"),
    tokenFile: path.join(root, "Library", "Application Support", "memoria-worker", "token"),
  };
}

const modeOf = async (p: string) => (await stat(p)).mode & 0o777;

test("launchd: the plist holds a path, not the token, and everything is private", { skip: needsPosixBash }, async () => {
  const f = await launchdFixture();
  const run = launchdDryRun(f.root, f.llama, { MEMORIA_WORKER_TOKEN: SECRET_CANARY }, ["--host", "192.168.1.30"]);
  assert.equal(run.status, 0, run.stderr);
  assert.ok(!(run.stdout + run.stderr).includes(SECRET_CANARY));
  const plist = await readFile(f.plist, "utf8");
  assert.ok(!plist.includes(SECRET_CANARY), "the plist must not hold the token");
  assert.match(plist, /<key>MEMORIA_WORKER_TOKEN_FILE<\/key>/);
  assert.doesNotMatch(plist, /<key>MEMORIA_WORKER_TOKEN<\/key>/);
  assert.match(plist, /<key>Umask<\/key><integer>63<\/integer>/);
  assert.equal(await readFile(f.tokenFile, "utf8"), `${SECRET_CANARY}\n`);
  assert.equal(await modeOf(f.plist), 0o600);
  assert.equal(await modeOf(f.tokenFile), 0o600);
  assert.equal(await modeOf(path.dirname(f.tokenFile)), 0o700);
  assert.equal(await modeOf(path.join(f.root, "Library", "Logs", "memoria-worker")), 0o700);

  // What the worker will accept is exactly what the script wrote.
  const { readSecretFile } = await import("../../worker/src/secret-file.ts");
  assert.equal(readSecretFile(f.tokenFile, "MEMORIA_WORKER_TOKEN_FILE"), SECRET_CANARY);
});

test("launchd: a reinstall narrows a file an earlier install left wide open", { skip: needsPosixBash }, async () => {
  const f = await launchdFixture();
  assert.equal(launchdDryRun(f.root, f.llama, { MEMORIA_WORKER_TOKEN: SECRET_CANARY }).status, 0);
  const { chmod } = await import("node:fs/promises");
  await chmod(f.plist, 0o644);
  await chmod(f.tokenFile, 0o644);
  assert.equal(launchdDryRun(f.root, f.llama, { MEMORIA_WORKER_TOKEN: SECRET_CANARY }).status, 0);
  assert.equal(await modeOf(f.plist), 0o600);
  assert.equal(await modeOf(f.tokenFile), 0o600);
});

test("launchd: token rules, and removing the token removes its file", { skip: needsPosixBash }, async () => {
  const f = await launchdFixture();
  const noToken = launchdDryRun(f.root, f.llama, {}, ["--host", "192.168.1.30"]);
  assert.notEqual(noToken.status, 0);
  assert.match(noToken.stderr, /token is required/);
  const short = launchdDryRun(f.root, f.llama, { MEMORIA_WORKER_TOKEN: "short" });
  assert.notEqual(short.status, 0);
  assert.match(short.stderr, /at least 16/);

  const source = path.join(f.root, "source-token");
  await writeFile(source, `${SECRET_CANARY}\n\n`);
  assert.equal(launchdDryRun(f.root, f.llama, {}, ["--token-file", source]).status, 0);
  assert.equal(await readFile(f.tokenFile, "utf8"), `${SECRET_CANARY}\n`);

  assert.equal(launchdDryRun(f.root, f.llama, {}).status, 0);
  await assert.rejects(stat(f.tokenFile), /ENOENT/);
  assert.doesNotMatch(await readFile(f.plist, "utf8"), /MEMORIA_WORKER_TOKEN_FILE/);
});
