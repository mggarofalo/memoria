// Says, before the hooks' tests run, when this Node is older than the one package.json's engines asks for.
// The hooks run under whatever Node is on the path, and npm only warns about engines, so a result can differ from
// CI's without anyone noticing. It warns and does not block: CI's worker and WireGuard jobs still run Node 22, below
// the engines floor, so refusing here would be stricter than CI.
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

/** True when `version` (1.2.3) is at least the floor in `range` (>=1.2.3). Any other range form is not judged. */
export function meetsFloor(version, range) {
  const floor = /^>=\s*(\d+)\.(\d+)\.(\d+)$/.exec(range.trim());
  const have = /^v?(\d+)\.(\d+)\.(\d+)/.exec(version);
  if (!floor || !have) return true;
  for (let i = 1; i <= 3; i++) {
    if (Number(have[i]) !== Number(floor[i])) return Number(have[i]) > Number(floor[i]);
  }
  return true;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const range = JSON.parse(readFileSync(path.join(root, "package.json"), "utf8")).engines?.node ?? "";
  if (!meetsFloor(process.versions.node, range)) {
    console.error(`warning: this is Node ${process.versions.node}, and package.json asks for ${range}. The hooks' results may differ from CI's; install a newer Node.`);
  }
}
