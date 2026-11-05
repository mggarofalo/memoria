/**
 * Entry point: `node scripts/e2e/run.ts [--no-build]`. Needs Node 22.18+, Go, the
 * .NET SDK, and `npm ci` in sidecar/. See README.md.
 */
import { bootstrap, keyManagement, login, mcp, noLeaks, noModels, rateLimits, scopedReads, scopeErrors, traversal } from "./checks.ts";
import { Harness } from "./harness.ts";

async function runAll(h: Harness): Promise<void> {
  h.seedIndex();
  await h.choosePorts();
  await h.startSidecar();
  const admin = bootstrap(h);
  await h.startApi();
  const keys = keyManagement(h, admin);
  scopeErrors(h, keys);
  scopedReads(h, keys);
  await traversal(h, keys);
  mcp(h, keys);
  login(h, keys);
  await rateLimits(h, keys);
  noModels(h);
  noLeaks(h);
}

const started = Date.now();
const h = new Harness();
let crashed = false;
try {
  h.buildCli();
  if (!process.argv.includes("--no-build")) h.buildServers();
  await runAll(h);
} catch (error) {
  crashed = true;
  console.error(h.scrub(String(error instanceof Error ? error.stack : error)));
} finally {
  await h.cleanUp();
}

const failed = h.results.filter((result) => !result.ok);
console.log(`\n${h.results.length - failed.length} passed, ${failed.length} failed, in ${Math.round((Date.now() - started) / 1000)}s`);
for (const result of failed) console.log(`  FAILED: ${result.name}`);
process.exitCode = failed.length > 0 || crashed ? 1 : 0;
