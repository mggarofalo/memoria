import path from "node:path";
import { homedir } from "node:os";
import { buildApp, warmup, type App } from "./app.ts";
import { isLoopbackHost, loadConfig, type SidecarConfig } from "./config.ts";
import { openJobStore, queueDbPathFor } from "./queue-store.ts";
import { openBackend, type LlmOptions } from "./store.ts";

/**
 * Process entry point. Opens the index once, builds the app, warms the
 * models, listens, and shuts down cleanly on SIGINT/SIGTERM.
 */
export function defaultDbPath(env: NodeJS.ProcessEnv = process.env): string {
  // Mirrors qmd's own default without calling getDefaultDbPath(), which the
  // SDK only permits after its CLI flips an unexported production flag.
  const cacheDir = env.XDG_CACHE_HOME || path.join(homedir(), ".cache");
  return path.join(cacheDir, "qmd", "index.sqlite");
}

function logExposure(app: App, config: SidecarConfig): void {
  if (config.secret === undefined) {
    app.log.warn({ host: config.host }, "MEMORIA_QMD_SECRET is not set; running unauthenticated on loopback only");
  }
  if (!isLoopbackHost(config.host)) {
    app.log.info({ host: config.host }, "binding a routable address; bearer auth is enforced on every route except /health and /openapi.json");
  }
}

function llmOptionsFrom(config: SidecarConfig): LlmOptions {
  if (config.llm.mode === "local") return { mode: "local" };
  return {
    mode: "remote",
    workers: config.llm.workers,
    embedDimensions: config.llm.embedDimensions,
    fallback: config.llm.fallback,
    statusPollMs: config.llm.pollMs,
    embedBatchBytes: config.llm.embedBatchBytes,
    rerankTimeoutMs: config.llm.rerankTimeoutMs,
    searchDeadlineMs: config.llm.searchDeadlineMs,
  };
}

async function main(): Promise<void> {
  const config = loadConfig();
  const dbPath = config.dbPath ?? defaultDbPath();

  // The logger arrives with the app, after the backend opens.
  let log: { info: (o: object, m: string) => void; warn: (o: object, m: string) => void } | undefined;
  // Beside the index but not in it: rebuilding index.sqlite leaves jobs alone. Only
  // job records live here, so nothing about this file may stop search from starting.
  const earlyWarnings: string[] = [];
  const { store: jobStore } = openJobStore(config.queueDbPath ?? queueDbPathFor(dbPath), (message) => earlyWarnings.push(message));
  const backend = await openBackend({
    dbPath,
    keepWarm: config.keepWarm,
    llm: llmOptionsFrom(config),
    onLlmCall: (entry) => log?.info(entry, "llm worker call"),
    onLlmDegraded: (entry) => log?.warn(entry, "llm worker call degraded"),
    onLlmWarning: (message) => (log ? log.warn({}, message) : earlyWarnings.push(message)),
    onVectorRepair: (rows) => earlyWarnings.push(`Removed ${rows} vector rows left with no vector by an interrupted write. Those documents will embed again.`),
  });
  const app = await buildApp({
    backend,
    config,
    ...(jobStore ? { jobStore } : {}),
    logger: {
      level: config.logLevel,
      redact: { paths: ["req.headers.authorization"], censor: "[redacted]" },
    },
  });
  log = app.log;
  for (const message of earlyWarnings) app.log.warn(message);
  if (jobStore && jobStore.quarantined > 0) app.log.warn({ rows: jobStore.quarantined }, "set aside unreadable job rows in jobs_quarantine");
  app.log.info({ llm: config.llm.mode, rerankTransport: backend.llm.rerankTransport }, "llm mode");
  logExposure(app, config);

  let closing = false;
  const shutdown = async (signal: string): Promise<void> => {
    if (closing) return;
    closing = true;
    app.log.info({ signal, pendingJobs: app.ctx.jobs.pending }, "shutting down");
    try {
      await app.close();
      // Index what was accepted in the debounce window. A timeout loses nothing:
      // the jobs are persisted and resume at the next start.
      if (!(await app.ctx.jobs.drain(config.shutdownDrainMs))) {
        app.log.warn({ pendingJobs: app.ctx.jobs.pending, timeoutMs: config.shutdownDrainMs }, "reindex jobs still pending at shutdown");
      }
      // A job still running is about to lose the index under it. That is not its
      // failure, so it stays queued in the store and resumes at the next start.
      app.ctx.jobs.suspend();
      await backend.close();
      jobStore?.close();
      process.exit(0);
    } catch (err) {
      app.log.error({ err }, "shutdown failed");
      process.exit(1);
    }
  };
  process.on("SIGINT", () => void shutdown("SIGINT"));
  process.on("SIGTERM", () => void shutdown("SIGTERM"));

  await app.listen({ host: config.host, port: config.port });
  app.log.info({ dbPath: backend.dbPath, metadataDocuments: app.ctx.metadata.size, keepWarm: config.keepWarm }, "memoria-qmd listening");

  if (config.warmup) {
    // Not awaited: the listener is up and /ready reports model residency.
    void warmup(app.ctx, app.log);
  }
}

main().catch((err: unknown) => {
  console.error(err instanceof Error ? err.stack ?? err.message : String(err));
  process.exit(1);
});
