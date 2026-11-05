/**
 * Entry point: `node scripts/e2e/run.ts [--no-build]`. Needs Node 22.18+, Go, the
 * .NET SDK, and `npm ci` in sidecar/. See README.md.
 */
import { auditTrail, authModes, finishExpiry, knownGood, revocation, rotation, startExpiry } from "./auth-keys.ts";
import { forwardedHeaders, limits } from "./auth-limits.ts";
import { issueScopeKeys, mcpScope, multiGetLimits, scopeMatrix, secretsAbsent, sendCanaries } from "./auth-reads.ts";
import { bootstrap, keyManagement, login, mcp, noModels, rateLimits, scopedReads, scopeErrors, traversal, type Keys } from "./checks.ts";
import { Harness } from "./harness.ts";
import { skillMatchesTheServer } from "./skill.ts";
import { ingest, INGEST_SERVER } from "./ingest.ts";
import { sourcesSync } from "./sources.ts";

async function runAll(h: Harness): Promise<void> {
  h.seedIndex();
  await h.choosePorts();
  await h.startSidecar();
  await h.startProxy();
  const admin = bootstrap(h);
  await h.startApi();
  const keys = keyManagement(h, admin);
  scopeErrors(h, keys);
  scopedReads(h, keys);
  await traversal(h, keys);
  mcp(h, keys);
  skillMatchesTheServer(h, keys.reader);
  login(h, keys);
  await authChecks(h, keys);
  await rateLimits(h, keys);
  noModels(h);
  secretsAbsent(h, keys.reader);
}

async function authChecks(h: Harness, { admin, reader }: Keys): Promise<void> {
  const expiring = await startExpiry(h, admin);
  await authModes(h, admin);
  await rotation(h, admin);
  await revocation(h, admin);
  await knownGood(h, admin);
  await auditTrail(h, admin, reader);
  await limits(h, admin);
  await forwardedHeaders(h, admin);
  const scopeKeys = await issueScopeKeys(h, admin);
  await multiGetLimits(h, { wide: scopeKeys.wide.token, reader });
  await scopeMatrix(h, scopeKeys);
  await mcpScope(h, scopeKeys);
  await sendCanaries(h, reader);
  await finishExpiry(h, admin, expiring);
}

/** On a server of its own, because it needs settings of its own and uses a different rate budget. */
async function runIngest(h: Harness): Promise<void> {
  h.buildCli();
  h.seedIndex();
  await h.choosePorts();
  await h.startSidecar();
  await h.startProxy();
  const admin = bootstrap(h);
  await h.startApi();
  await ingest(h, admin);
  await sourcesSync(h, admin);
  noModels(h);
}

async function guarded(h: Harness, body: (h: Harness) => Promise<void>): Promise<boolean> {
  try {
    await body(h);
    return false;
  } catch (error) {
    console.error(h.scrub(String(error instanceof Error ? error.stack : error)));
    return true;
  } finally {
    await h.cleanUp();
  }
}

const started = Date.now();
const h = new Harness();
const ingestServer = new Harness(INGEST_SERVER);
// `--only-ingest` is for running the redaction proof many times over.
let crashed = await guarded(h, async (first) => {
  if (!process.argv.includes("--no-build")) first.buildServers();
  if (!process.argv.includes("--only-ingest")) {
    first.buildCli();
    await runAll(first);
  }
});
crashed = (await guarded(ingestServer, runIngest)) || crashed;

const results = [...h.results, ...ingestServer.results];
const failed = results.filter((result) => !result.ok);
console.log(`\n${results.length - failed.length} passed, ${failed.length} failed, in ${Math.round((Date.now() - started) / 1000)}s`);
for (const result of failed) console.log(`  FAILED: ${result.name}`);
process.exitCode = failed.length > 0 || crashed ? 1 : 0;
