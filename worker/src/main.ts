import { loadConfig } from "./config.ts";
import { createLogger } from "./log.ts";
import { startWorker } from "./startup.ts";

/** Process entry: read the configuration, start, warm, and stop cleanly on a signal. */

async function main(): Promise<void> {
  const config = loadConfig();
  const log = createLogger(config.token);
  const worker = await startWorker(config, log);
  const stop = (signal: string): void => {
    void worker.shutdown(signal).then(() => process.exit(0));
  };
  process.on("SIGINT", () => stop("SIGINT"));
  process.on("SIGTERM", () => stop("SIGTERM"));
  await worker.warm();
}

main().catch((err: unknown) => {
  // Config errors are written to name the setting and never hold its value's secret.
  console.error(err instanceof Error ? err.message : "memoria-worker failed to start");
  process.exit(1);
});
