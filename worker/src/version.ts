import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

/**
 * This process's version, derived from the release tag and never written by
 * hand. Keep this file byte-identical in sidecar/src and worker/src; they share
 * no package, and scripts/release/test/runtime-version.test.ts fails if they differ.
 *
 *   on tag v1.2.3            1.2.3
 *   5 commits after v1.2.3   1.2.4-dev.5+g<sha>   (sorts after 1.2.3, before 1.2.4)
 *   no tag reachable         0.0.0-dev+g<sha>
 *   uncommitted changes      any dev form, with .dirty; a tagged commit becomes 1.2.4-dev.0
 *   no git and no stamp      0.0.0-dev+unknown
 */

export const UNKNOWN_VERSION = "0.0.0-dev+unknown";

/** A build step writes this file beside its output, pinning the version to that output. */
export const STAMP_FILE = "VERSION";

const MODULE_DIR = path.dirname(fileURLToPath(import.meta.url));

const DESCRIBE_ARGS = [
  // A service account rarely owns the checkout. The checkout's own code is what
  // this process runs, so trusting it for a read-only describe adds no exposure.
  "-c",
  "safe.directory=*",
  "describe",
  "--tags",
  "--long",
  "--always",
  "--dirty",
  "--abbrev=12",
  "--match",
  "v[0-9]*.[0-9]*.[0-9]*",
  "--exclude",
  "*-*",
];

/** Map `git describe` output to a semver version. */
export function describeToVersion(described: string): string {
  const tagged = /^v(\d+)\.(\d+)\.(\d+)-(\d+)-g([0-9a-f]+)(-dirty)?$/.exec(described);
  if (tagged) {
    const [, major, minor, patch, ahead, sha, dirty] = tagged;
    if (ahead === "0" && !dirty) return `${major}.${minor}.${patch}`;
    return `${major}.${minor}.${Number(patch) + 1}-dev.${ahead}+g${sha}${dirty ? ".dirty" : ""}`;
  }
  const untagged = /^([0-9a-f]+)(-dirty)?$/.exec(described);
  return untagged ? `0.0.0-dev+g${untagged[1]}${untagged[2] ? ".dirty" : ""}` : UNKNOWN_VERSION;
}

/** The version the checkout at `dir` describes, or UNKNOWN_VERSION when git cannot say. */
export function describedVersion(dir: string = MODULE_DIR): string {
  try {
    const described = execFileSync("git", DESCRIBE_ARGS, { cwd: dir, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], timeout: 5000 });
    return describeToVersion(described.trim());
  } catch {
    return UNKNOWN_VERSION;
  }
}

/** The stamp beside the running code wins: built output keeps its version after the checkout moves on. */
export function resolveVersion(dir: string = MODULE_DIR): string {
  try {
    const stamped = readFileSync(path.join(dir, STAMP_FILE), "utf8").trim();
    if (stamped) return stamped;
  } catch {
    // No stamp: running from source, so the checkout is the truth.
  }
  return describedVersion(dir);
}
