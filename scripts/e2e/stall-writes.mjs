// Loaded into the sidecar with --import, for the end-to-end checks only. Holds a write at a chosen moment so a real socket can close during it. It finds
// writes by their temp file's name: `before` stalls ahead of the connection check, and `after` past it, just before the rename. The harness writes the file this reads.
import { readFileSync } from "node:fs";
import fs from "node:fs/promises";
import { syncBuiltinESMExports } from "node:module";

const stallFile = process.env.E2E_STALL_FILE;

function stallFor(where) {
  try {
    return JSON.parse(readFileSync(stallFile, "utf8"))[where] ?? 0;
  } catch {
    return 0;
  }
}

function wrap(name, where, isOurs) {
  const real = fs[name];
  fs[name] = async (...args) => {
    const ms = isOurs(args) ? stallFor(where) : 0;
    if (ms > 0) await new Promise((resolve) => setTimeout(resolve, ms));
    return real(...args);
  };
}

if (stallFile) {
  wrap("realpath", "before", (args) => String(args[0]).includes(".memoria-"));
  wrap("rename", "after", (args) => String(args[0]).includes(".memoria-") && !String(args[1]).includes(".memoria-"));
  syncBuiltinESMExports();
}
