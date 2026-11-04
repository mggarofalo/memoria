import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";

/**
 * The benchmark's temporary directory. It holds copies of a real index, and so
 * real transcripts, so it has to go away however the run ends. A clean exit and
 * Ctrl-C remove it. A fatal crash runs no code at all, so each run also clears
 * what a dead run left behind.
 */

export const WORKSPACE_PREFIX = "memoria-parity-";
const MARKER = ".memoria-parity-owner";

function alive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (err) {
    return (err as NodeJS.ErrnoException).code === "EPERM";
  }
}

/** Removes workspaces whose owner is gone. Only directories carrying the marker are ever touched. */
export function sweepDeadWorkspaces(root: string = tmpdir()): string[] {
  const removed: string[] = [];
  for (const name of readdirSync(root)) {
    if (!name.startsWith(WORKSPACE_PREFIX)) continue;
    const dir = path.join(root, name);
    const marker = path.join(dir, MARKER);
    if (!existsSync(marker)) continue;
    const owner = Number(readFileSync(marker, "utf8").trim());
    if (Number.isInteger(owner) && owner > 0 && alive(owner)) continue;
    rmSync(dir, { recursive: true, force: true, maxRetries: 3 });
    removed.push(dir);
  }
  return removed;
}

export async function createWorkspace(root: string = tmpdir()): Promise<string> {
  mkdirSync(root, { recursive: true });
  const dir = await mkdtemp(path.join(root, WORKSPACE_PREFIX));
  writeFileSync(path.join(dir, MARKER), String(process.pid), "utf8");
  return dir;
}

export function removeWorkspace(dir: string): void {
  rmSync(dir, { recursive: true, force: true, maxRetries: 3 });
}

/**
 * Runs `cleanup` once on Ctrl-C or a termination signal, then exits. An index
 * still open cannot be deleted on Windows, so `cleanup` closes it first, and a
 * deadline stops a stuck close from keeping the process alive. Returns a function
 * that removes the handlers.
 */
export function onInterrupt(cleanup: () => Promise<void>, deadlineMs = 15_000): () => void {
  let running = false;
  const handler = (signal: NodeJS.Signals): void => {
    if (running) return;
    running = true;
    const code = signal === "SIGINT" ? 130 : 143;
    const timer = setTimeout(() => process.exit(code), deadlineMs);
    timer.unref();
    void cleanup()
      .catch(() => {})
      .finally(() => process.exit(code));
  };
  process.on("SIGINT", handler);
  process.on("SIGTERM", handler);
  return () => {
    process.off("SIGINT", handler);
    process.off("SIGTERM", handler);
  };
}
