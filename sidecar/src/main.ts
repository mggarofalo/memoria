import path from "node:path";
import { homedir } from "node:os";
import { buildApp, warmup } from "./app.ts";
import { isLoopbackHost, loadConfig } from "./config.ts";
import { openBackend } from "./store.ts";

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

async function main(): Promise<void> {
  const config = loadConfig();
  const dbPath = config.dbPath ?? defaultDbPath();

  const backend = await openBackend({ dbPath, keepWarm: config.keepWarm });
  const app = await buildApp({
    backend,
    config,
    logger: {
      level: config.logLevel,
      redact: { paths: ["req.headers.authorization"], censor: "[redacted]" },
    },
  });

  if (config.secret === undefined) {
    app.log.warn({ host: config.host }, "MEMORIA_QMD_SECRET is not set; running unauthenticated on loopback only");
  }
  if (!isLoopbackHost(config.host)) {
    app.log.info({ host: config.host }, "binding a routable address; bearer auth is enforced on every route except /health and /openapi.json");
  }

  let closing = false;
  const shutdown = async (signal: string): Promise<void> => {
    if (closing) return;
    closing = true;
    app.log.info({ signal, pendingJobs: app.ctx.jobs.pending }, "shutting down");
    try {
      await app.close();
      // Index what was accepted in the debounce window. A timeout loses nothing
      // durable: the files are on disk and the next scan picks them up.
      if (!(await app.ctx.jobs.drain(config.shutdownDrainMs))) {
        app.log.warn({ pendingJobs: app.ctx.jobs.pending, timeoutMs: config.shutdownDrainMs }, "reindex jobs still pending at shutdown");
      }
      await backend.close();
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
