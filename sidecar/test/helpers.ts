import { rmSync } from "node:fs";
import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { buildApp, type App } from "../src/app.ts";
import { loadConfig, type SidecarConfig } from "../src/config.ts";
import type { JobScheduler } from "../src/jobs.ts";
import { FakeBackend, FRONTMATTER_DOC, type FakeDoc } from "./fake-backend.ts";

export const TEST_SECRET = "test-secret-0123456789abcdef";

/** Debounce off unless a test asks for it, so `jobs.idle()` never waits on a timer. */
export function testConfig(overrides: Partial<SidecarConfig> = {}): SidecarConfig {
  const base = loadConfig({ MEMORIA_QMD_SECRET: TEST_SECRET, MEMORIA_QMD_WARMUP: "0", MEMORIA_QMD_REINDEX_DEBOUNCE_MS: "0" });
  return { ...base, ...overrides };
}

/** A clock that moves only when told to, so debounce tests never sleep. */
export class ManualScheduler implements JobScheduler {
  #now = 0;
  #nextId = 1;
  readonly #timers = new Map<number, { at: number; callback: () => void }>();

  now(): number {
    return this.#now;
  }

  setTimeout(callback: () => void, delayMs: number): unknown {
    const id = this.#nextId++;
    this.#timers.set(id, { at: this.#now + delayMs, callback });
    return id;
  }

  clearTimeout(handle: unknown): void {
    this.#timers.delete(handle as number);
  }

  /** Move the clock, firing whatever falls due, then let the worker start. */
  async advance(ms: number): Promise<void> {
    this.#now += ms;
    for (const [id, timer] of [...this.#timers].sort((a, b) => a[1].at - b[1].at)) {
      if (timer.at <= this.#now && this.#timers.delete(id)) timer.callback();
    }
    await new Promise((resolve) => setImmediate(resolve));
  }

  get pendingTimers(): number {
    return this.#timers.size;
  }
}

export const authHeaders = { authorization: `Bearer ${TEST_SECRET}` };

export function sampleDocs(): FakeDoc[] {
  return [
    {
      collection: "claude-sessions",
      path: "2026-02-11_alpha.md",
      body: FRONTMATTER_DOC(
        {
          session_id: "alpha",
          date: "2026-02-11",
          start_time: "2026-02-11T04:47:57.868000+00:00",
          project: "Receipts",
          git_branch: "master",
          tags: ["project/Receipts", "branch/master"],
        },
        "# Alpha\n\n## User\n\nHow should the AuthenticationHandler validate API keys?\n\n## Assistant\n\nCompare the bearer token in constant time.\n",
      ),
    },
    {
      collection: "claude-sessions",
      path: "2026-03-05_beta.md",
      body: FRONTMATTER_DOC(
        {
          session_id: "beta",
          date: "2026-03-05",
          start_time: "2026-03-05T10:00:00+00:00",
          project: "Memoria",
          git_branch: "main",
          tags: ["project/Memoria", "branch/main"],
        },
        "# Beta\n\n## User\n\nDesign the AuthenticationHandler for Memoria.Api.\n\n## Assistant\n\nUse a scoped API key per collection.\n",
      ),
    },
    {
      collection: "codex-sessions",
      path: "2026-08-31_gamma.md",
      body: FRONTMATTER_DOC(
        {
          session_id: "gamma",
          date: "2026-08-31",
          start_time: "2026-08-31T00:56:29+00:00",
          project: "receipts",
          git_branch: "main",
          source: "cli",
          tags: ["agent/codex", "project/receipts", "branch/main"],
        },
        "# Gamma\n\n## User\n\nWhere does the receipts importer handle API keys?\n",
      ),
    },
    {
      collection: "claude-sessions",
      path: "no-frontmatter.md",
      body: "# Plain\n\nThis transcript has no frontmatter but mentions API keys.\n",
    },
  ];
}

/**
 * Node arguments and the environment for a process a test starts, so that it ends with the test's process. A mutant
 * that hangs a test gets the test's process killed by the mutation runner, and the child would otherwise run on.
 * The preload is the worker's, which says more (worker/test/fixtures/die-with-parent.ts).
 */
export const dieWithParent = ["--import", new URL("../../worker/test/fixtures/die-with-parent.ts", import.meta.url).href];

export function dieWithParentEnv(): NodeJS.ProcessEnv {
  return { ...process.env, DIE_WITH_PARENT_PID: String(process.pid) };
}

const created: string[] = [];

/** Each test file is its own process, so its directories all go when it ends. */
process.once("exit", () => {
  for (const dir of created) {
    try {
      rmSync(dir, { recursive: true, force: true });
    } catch {
      // Something still has a file open. A leftover in the temporary directory fails nothing.
    }
  }
});

export async function tempDir(prefix = "memoria-qmd-"): Promise<string> {
  const dir = await mkdtemp(path.join(tmpdir(), prefix));
  created.push(dir);
  return dir;
}

export interface TestApp {
  app: App;
  backend: FakeBackend;
  roots: { claude: string; codex: string };
}

export async function makeApp(options: { docs?: FakeDoc[]; config?: Partial<SidecarConfig>; modelsResident?: boolean; scheduler?: JobScheduler } = {}): Promise<TestApp> {
  const claude = await tempDir("memoria-claude-");
  const codex = await tempDir("memoria-codex-");
  const backend = new FakeBackend({
    collections: [
      { name: "claude-sessions", root: claude },
      { name: "codex-sessions", root: codex, context: "Local Codex chat transcripts." },
    ],
    docs: options.docs ?? sampleDocs(),
    ...(options.modelsResident !== undefined ? { modelsResident: options.modelsResident } : {}),
  });
  const app = await buildApp({ backend, config: testConfig(options.config), logger: false, ...(options.scheduler ? { scheduler: options.scheduler } : {}) });
  await app.ready();
  return { app, backend, roots: { claude, codex } };
}
