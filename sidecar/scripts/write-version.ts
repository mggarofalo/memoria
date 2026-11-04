/**
 * Pin the version of a build: `npm run build` writes dist/VERSION from the
 * checkout, so dist/ keeps reporting what it was built from even if the
 * checkout moves on before the service restarts.
 */
import { writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { describedVersion, STAMP_FILE } from "../src/version.ts";

const dist = fileURLToPath(new URL("../dist/", import.meta.url));
const version = describedVersion(dist);
writeFileSync(path.join(dist, STAMP_FILE), `${version}\n`, "utf8");
console.log(`stamped dist/${STAMP_FILE}: ${version}`);
