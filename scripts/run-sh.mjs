// Runs a repository shell script from any shell: node scripts/run-sh.mjs <script> [args]
// On Windows, `sh` is usually only on the path inside Git Bash. Git for Windows
// always ships one, so this finds it from git itself, along with the tools beside
// it (rm, awk, grep), and says what to do when there is none.
import { execFileSync, spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import path from "node:path";

function hasSh() {
  return spawnSync("sh", ["-c", "exit 0"], { stdio: "ignore" }).status === 0;
}

/** Git for Windows' own bin directory, worked out from where git's helpers live. */
function gitBashTools() {
  try {
    const execPath = execFileSync("git", ["--exec-path"], { encoding: "utf8" }).trim();
    // <git>/mingw64/libexec/git-core, so three levels up is <git>.
    const tools = path.resolve(execPath, "..", "..", "..", "usr", "bin");
    return existsSync(path.join(tools, "sh.exe")) ? tools : undefined;
  } catch {
    return undefined;
  }
}

const env = { ...process.env };
if (!hasSh()) {
  const tools = gitBashTools();
  if (tools === undefined) {
    console.error("This script needs sh. On Windows, install Git for Windows, or run it from Git Bash.");
    process.exit(127);
  }
  // Windows may spell the variable Path, so replace whichever spelling exists.
  const key = Object.keys(env).find((name) => name.toLowerCase() === "path") ?? "PATH";
  env[key] = `${tools}${path.delimiter}${env[key] ?? ""}`;
}

const result = spawnSync("sh", process.argv.slice(2), { stdio: "inherit", env });
process.exit(result.status ?? 1);
