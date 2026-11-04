/**
 * Stamp a copy of the worker with this source's version. The service installer
 * copies src/ somewhere with no .git, so without this a worker installed from a
 * checkout could only report `unknown`. A package's stamp carries over unchanged.
 *
 * usage: node scripts/write-version.ts <directory holding the copied src files>
 */
import { writeFileSync } from "node:fs";
import path from "node:path";
import { resolveVersion, STAMP_FILE } from "../src/version.ts";

const target = process.argv[2];
if (target === undefined) throw new Error("usage: write-version.ts <directory>");
const version = resolveVersion();
writeFileSync(path.join(target, STAMP_FILE), `${version}\n`, "utf8");
console.log(`stamped ${path.join(target, STAMP_FILE)}: ${version}`);
