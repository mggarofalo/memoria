/**
 * Entry point: `node scripts/release/release.ts <command>`. Needs Node 26.13+,
 * git, and for some commands gh or go. See docs/releasing.md.
 */
import { execFileSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { run, type Tools } from "./cli.ts";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");

function capture(command: string, args: string[], input?: string): string {
  return execFileSync(command, args, { cwd: root, encoding: "utf8", stdio: [input === undefined ? "ignore" : "pipe", "pipe", "pipe"], input, maxBuffer: 256 * 1024 * 1024 });
}

const tools: Tools = {
  root,
  repo: process.env["GITHUB_REPOSITORY"] ?? "mggarofalo/memoria",
  git: (args) => capture("git", args),
  gh: (args) => capture("gh", args),
  go: (args, env) => {
    execFileSync("go", args, { cwd: path.join(root, "cli"), env: { ...process.env, ...env }, stdio: "inherit" });
  },
  exec: (command, args, cwd) => {
    // npm is a .cmd on Windows, which needs a shell; these arguments are all constants.
    execFileSync(command, args, { cwd, stdio: "inherit", shell: command === "npm" && process.platform === "win32" });
  },
  docker: (args) => capture("docker", args),
  httpGet: (url, bearer) => {
    try {
      // `-H @-` reads the header from stdin, so the token is in no process argument.
      return bearer === undefined ? capture("curl", ["-fsS", url]) : capture("curl", ["-fsS", "-H", "@-", url], `Authorization: Bearer ${bearer}\n`);
    } catch {
      return undefined;
    }
  },
  sleep: (ms) => {
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
  },
  print: (line) => console.log(line),
};

process.exitCode = run(process.argv.slice(2), tools, (line) => console.error(line));
