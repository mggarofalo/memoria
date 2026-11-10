/**
 * `memoria migrate rerender` against a real API and sidecar: an export over the cap goes again from its raw Codex transcript,
 * and `verify` then counts it held through its session. Fixtures only, in the driver's private directory.
 */
import { mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import type { CliResult, Harness } from "./harness.ts";
import { codexMessage, codexMeta, forceIndex } from "./ingest.ts";

export const RERENDER_TARGET = "migrated-rerender";

const SENT_ID = "0a1b2c3d-1111-4222-8333-444455556666";
const MISSING_ID = "0a1b2c3d-9999-4222-8333-444455556666";
const MAX_DOCUMENT_BYTES = 2 * 1024 * 1024;

interface RerenderRow {
  overCap: number;
  wouldSend?: number;
  sent: number;
  alreadySent: number;
  noTranscript: number;
}

interface VerifyRow {
  heldBySession: number;
  notHeld: number;
  countDiffers: boolean;
}

function file(root: string, name: string, text: string): string {
  const where = path.join(root, ...name.split("/"));
  mkdirSync(path.dirname(where), { recursive: true });
  writeFileSync(where, text);
  return where;
}

/** An export of a session over the cap, as the exporter writes one: its session in frontmatter, and the messages below. */
function export_(id: string): string {
  return `---\nsession_id: "${id}"\nmessage_count: 1\n---\n\n## User\n\n` + "padding words ".repeat(MAX_DOCUMENT_BYTES / 14 + 10);
}

function rowOf<T>(run: CliResult): T {
  return ((JSON.parse(run.out || "{}") as { collections?: T[] }).collections ?? [])[0] ?? ({} as T);
}

export async function migrateRerender(h: Harness, key: string): Promise<void> {
  const root = h.privatePath("rerender-exports");
  const codex = h.privatePath("rerender-codex");
  file(root, "2026-01-15_sent.md", export_(SENT_ID));
  file(root, "2026-01-15_missing.md", export_(MISSING_ID));
  file(codex, `2026/01/15/rollout-2026-01-15T10-00-00-${SENT_ID}.jsonl`, codexMeta(SENT_ID) + codexMessage("rerenderedword from the raw transcript"));
  const config = h.privatePath("rerender.yml");
  writeFileSync(config, `collections:\n  ${RERENDER_TARGET}:\n    path: ${JSON.stringify(root)}\n    pattern: "**/*.md"\n`);
  const run = (...extra: string[]): CliResult => h.memoria(["migrate", "rerender", "--qmd-config", config, "--codex-sessions", codex, "--api-url", h.apiUrl, "--format", "json", ...extra], { key });

  const pushed = h.memoria(["migrate", "push", "--qmd-config", config, "--api-url", h.apiUrl, "--apply", "--format", "json"], { key });
  h.check("rerender: push could store neither export, as both are over the cap", pushed.code === 1 && rowOf<{ overCap: number }>(pushed).overCap === 2, pushed.out + pushed.err);

  const dry = run();
  h.check("rerender: a dry run finds one transcript, says the other is missing, and sends nothing", dry.code === 1 && rowOf<RerenderRow>(dry).wouldSend === 1 && rowOf<RerenderRow>(dry).noTranscript === 1 && dry.err.includes("export is their only copy"), dry.out + dry.err);

  const sent = run("--apply");
  h.check("rerender: the transcript goes through the session route, and the missing one still fails the run", sent.code === 1 && rowOf<RerenderRow>(sent).sent === 1 && rowOf<RerenderRow>(sent).noTranscript === 1, sent.out + sent.err);
  h.check("rerender: no file name reaches its output", !(sent.out + sent.err).includes("2026-01-15_"), sent.out + sent.err);
  await afterRerender(h, key, config, run);
}

async function afterRerender(h: Harness, key: string, config: string, run: (...extra: string[]) => CliResult): Promise<void> {
  await forceIndex({ h });
  const hits = await fetch(`${h.apiUrl}/v1/search?query=rerenderedword&rerank=false&limit=5&collection=${RERENDER_TARGET}`, { headers: { authorization: `Bearer ${key}` } });
  h.check("rerender: the session's text is searchable", ((await hits.json()) as { results: unknown[] }).results.length >= 1);

  const again = run("--apply");
  h.check("rerender: a second run sends nothing the server holds", rowOf<RerenderRow>(again).alreadySent === 1 && rowOf<RerenderRow>(again).sent === 0, again.out + again.err);

  const verify = h.memoria(["migrate", "verify", "--qmd-config", config, "--api-url", h.apiUrl, "--format", "json"], { key });
  const row = rowOf<VerifyRow>(verify);
  h.check("rerender: verify counts the re-rendered export as held through its session, and only the missing one as not held", row.heldBySession === 1 && row.notHeld === 1 && !row.countDiffers && verify.code === 1, verify.out + verify.err);
}
