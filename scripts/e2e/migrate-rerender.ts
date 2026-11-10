/**
 * `memoria migrate rerender` against a real API and sidecar: an export over the cap goes again from its raw Codex transcript,
 * and `verify` then counts it held through its session. Fixtures only, in the driver's private directory.
 */
import { mkdirSync, utimesSync, writeFileSync } from "node:fs";
import path from "node:path";
import type { CliResult, Harness } from "./harness.ts";
import { codexMessage, codexMeta, forceIndex } from "./ingest.ts";

export const RERENDER_TARGET = "migrated-rerender";
export const CHUNKS_TARGET = "migrated-rerender-chunks";

const OLDER_ID = "0a1b2c3d-2222-4222-8333-444455556666";
const NEWER_ID = "0a1b2c3d-3333-4222-8333-444455556666";
const EXPORT_TIME = new Date("2025-06-01T12:00:00Z");

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
  takenOver: number;
  countDiffers: boolean;
}

function file(root: string, name: string, text: string): string {
  const where = path.join(root, ...name.split("/"));
  mkdirSync(path.dirname(where), { recursive: true });
  writeFileSync(where, text);
  return where;
}

/** An export of a session over the cap, as the exporter writes one: its session in frontmatter, and the messages below. */
function export_(id: string, start = 0): string {
  return `---\nsession_id: "${id}"\nmessage_start: ${start}\nmessage_count: 1\n---\n\n## User\n\n` + "padding words ".repeat(MAX_DOCUMENT_BYTES / 14 + 10);
}

/** A session's first chunk, small enough to store, and its second, too large, both written after the transcript or before it. */
function chunks(root: string, codex: string, id: string, transcriptTime: Date): void {
  const first = file(root, `2026-01-15_${id.slice(9, 13)}_1.md`, `---\nsession_id: "${id}"\nmessage_start: 0\nmessage_count: 1\n---\n\n## User\n\nfirst\n`);
  const second = file(root, `2026-01-15_${id.slice(9, 13)}_2.md`, export_(id, 1));
  const transcript = file(codex, `rollout-2026-01-15T10-00-00-${id}.jsonl`, codexMeta(id) + codexMessage("first") + codexMessage(`chunkword${id.slice(9, 13)} second`));
  for (const export__ of [first, second]) utimesSync(export__, EXPORT_TIME, EXPORT_TIME);
  utimesSync(transcript, transcriptTime, transcriptTime);
}

/**
 * A stored chunk carries its export's time, and exports are written after the transcript, so the version sent must be the later of
 * the two or the server refuses it as stale. Both orders are held here.
 */
export async function rerenderWithAStoredChunk(h: Harness, key: string): Promise<void> {
  const root = h.privatePath("rerender-chunks");
  const codex = h.privatePath("rerender-chunks-codex");
  chunks(root, codex, OLDER_ID, new Date("2025-01-01T00:00:00Z"));
  chunks(root, codex, NEWER_ID, new Date("2026-01-01T00:00:00Z"));
  const config = h.privatePath("rerender-chunks.yml");
  writeFileSync(config, `collections:\n  ${CHUNKS_TARGET}:\n    path: ${JSON.stringify(root)}\n    pattern: "**/*.md"\n`);
  const pushed = h.memoria(["migrate", "push", "--qmd-config", config, "--api-url", h.apiUrl, "--apply", "--format", "json"], { key });
  h.check("rerender: each session's first chunk is stored and its second is over the cap", rowOf<{ sent: number; overCap: number }>(pushed).sent === 2 && rowOf<{ overCap: number }>(pushed).overCap === 2, pushed.out + pushed.err);
  const sent = h.memoria(["migrate", "rerender", "--qmd-config", config, "--codex-sessions", codex, "--api-url", h.apiUrl, "--apply", "--format", "json"], { key });
  h.check("rerender: a session with a stored chunk goes, whether its transcript is older or newer than its exports", sent.code === 0 && rowOf<RerenderRow>(sent).sent === 2, sent.out + sent.err);
  await forceIndex({ h });
  const verify = h.memoria(["migrate", "verify", "--qmd-config", config, "--api-url", h.apiUrl, "--format", "json"], { key });
  const row = rowOf<VerifyRow>(verify);
  h.check("rerender: verify then finds the stored chunks taken over and the others held through their session", verify.code === 0 && row.takenOver === 2 && row.heldBySession === 2 && row.notHeld === 0, verify.out + verify.err);
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
