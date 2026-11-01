import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { buildApp, type App } from "../src/app.ts";
import { loadConfig, type SidecarConfig } from "../src/config.ts";
import { FakeBackend, FRONTMATTER_DOC, type FakeDoc } from "./fake-backend.ts";

export const TEST_SECRET = "test-secret-0123456789abcdef";

export function testConfig(overrides: Partial<SidecarConfig> = {}): SidecarConfig {
  const base = loadConfig({ MEMORIA_QMD_SECRET: TEST_SECRET, MEMORIA_QMD_WARMUP: "0" });
  return { ...base, ...overrides };
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

export async function tempDir(prefix = "memoria-qmd-"): Promise<string> {
  return mkdtemp(path.join(tmpdir(), prefix));
}

export interface TestApp {
  app: App;
  backend: FakeBackend;
  roots: { claude: string; codex: string };
}

export async function makeApp(options: { docs?: FakeDoc[]; config?: Partial<SidecarConfig>; modelsResident?: boolean } = {}): Promise<TestApp> {
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
  const app = await buildApp({ backend, config: testConfig(options.config), logger: false });
  await app.ready();
  return { app, backend, roots: { claude, codex } };
}
