/**
 * Compare in-process QMD with the remote adapter on a real index and real models.
 *
 *   MEMORIA_QMD_WORKER_URL=http://host:8383 MEMORIA_QMD_WORKER_TOKEN=... \
 *     npm run bench:parity -- --db /path/to/index.sqlite --out parity.json
 *
 * Needs the GGUF models on this host for the in-process side. Takes a copy of the
 * index and works only on copies, deleted on any exit but a crash, and swept by
 * the next run after one; the index named by --db is only read. `--out` is updated
 * after every query, marked `partial` until the run ends. Exits 0 without doing anything when no worker URL is set, so it can
 * sit in CI without ever being required.
 *
 * It reports numbers and makes no decision. See docs/workers.md for reading them.
 */
import { copyFile, readFile, rename, writeFile } from "node:fs/promises";
import { existsSync, renameSync, writeFileSync } from "node:fs";
import path from "node:path";
import { parseArgs } from "node:util";
import { loadConfig } from "../src/config.ts";
import { openBackend, type OpenedBackend } from "../src/store.ts";
import { runParity, validateQuerySet, type BenchQuery, type ParitySide } from "./run.ts";
import { createWorkspace, onInterrupt, removeWorkspace, sweepDeadWorkspaces } from "./workspace.ts";

function side(backend: OpenedBackend): ParitySide {
  return {
    candidates: async (query: BenchQuery, limit: number) => {
      const hits = await backend.search({ queries: query.searches, collections: [query.collection], limit, minScore: 0, rerank: false });
      return hits.map((h) => ({ file: h.displayPath, text: h.bestChunk }));
    },
    rerank: (query, documents) => backend.llm.rerank(query, documents),
    embed: async (text) => (await backend.llm.embedBatch([text]))[0]?.embedding ?? null,
    search: async (query, limit) => {
      const { hits, degradedReasons } = await backend.searchDetailed({ queries: query.searches, intent: query.intent, collections: [query.collection], limit, minScore: 0, rerank: true });
      // A degraded result is not what this measures. Recorded as an error for the query.
      if (degradedReasons.length > 0) throw new Error(`search ran degraded: ${degradedReasons.join(", ")}`);
      return { files: hits.map((h) => h.displayPath), scores: hits.map((h) => h.score) };
    },
    expand: (query) => backend.llm.expandQuery(query),
    clearCache: () => backend.llm.clearCache(),
  };
}

async function copyIndex(source: string, directory: string, name: string): Promise<string> {
  const target = path.join(directory, name);
  await copyFile(source, target);
  // A live SQLite index may hold recent writes in its write-ahead log.
  if (existsSync(`${source}-wal`)) await copyFile(`${source}-wal`, `${target}-wal`);
  return target;
}

function argumentProblem(db: string | undefined, candidateLimit: number, expansionRuns: number): string | null {
  if (!db) return "--db is required. There is deliberately no default: name the index to compare against.";
  if (!existsSync(db)) return `No index at ${db}.`;
  const valid = Number.isInteger(candidateLimit) && candidateLimit >= 1 && Number.isInteger(expansionRuns) && expansionRuns >= 2;
  return valid ? null : "--candidate-limit must be at least 1, and --expansion-runs at least 2 so a baseline exists.";
}

async function main(): Promise<number> {
  if (!process.env.MEMORIA_QMD_WORKER_URL?.trim()) {
    console.log("bench:parity skipped: MEMORIA_QMD_WORKER_URL is not set.");
    return 0;
  }
  const { values } = parseArgs({
    options: {
      db: { type: "string" },
      out: { type: "string" },
      queries: { type: "string" },
      "candidate-limit": { type: "string", default: "40" },
      "expansion-runs": { type: "string", default: "5" },
    },
  });
  const candidateLimit = Number(values["candidate-limit"]);
  const expansionRuns = Number(values["expansion-runs"]);
  const problem = argumentProblem(values.db, candidateLimit, expansionRuns);
  if (problem) {
    console.error(problem);
    return 2;
  }
  const db = values.db!;
  const config = loadConfig({ ...process.env, MEMORIA_QMD_LLM: "remote" });
  if (config.llm.mode !== "remote") return 2;

  const queryFile = values.queries ?? new URL("./queries.json", import.meta.url);
  const queries = validateQuerySet(JSON.parse(await readFile(queryFile, "utf8")));

  const swept = sweepDeadWorkspaces();
  if (swept.length > 0) console.error(`Removed ${swept.length} index copies left by runs that crashed.`);
  const workspace = await createWorkspace();
  let local: OpenedBackend | undefined;
  let remote: OpenedBackend | undefined;
  const cleanup = async (): Promise<void> => {
    await remote?.close();
    await local?.close();
    removeWorkspace(workspace);
  };
  const removeHandlers = onInterrupt(cleanup);
  try {
    // Opened local first: remote mode replaces the SDK's process-wide default,
    // which the local side's document path would otherwise share. These runs
    // embed queries and candidate chunks through each store's own instance, so that is safe.
    local = await openBackend({ dbPath: await copyIndex(db, workspace, "local.sqlite"), keepWarm: true, llm: { mode: "local" } });
    remote = await openBackend({
      dbPath: await copyIndex(db, workspace, "remote.sqlite"),
      keepWarm: true,
      llm: {
        mode: "remote",
        workers: config.llm.workers,
        embedDimensions: config.llm.embedDimensions,
        // A silent fallback would make the remote side measure this machine.
        fallback: false,
      },
      onLlmDegraded: (entry) => console.error(JSON.stringify(entry)),
    });
    const report = await runParity(queries, side(local), side(remote), {
      candidateLimit,
      expansionRuns,
      onProgress: (done, total, id) => console.error(`[${done}/${total}] ${id}`),
      onQuery: (reports) => {
        if (!values.out) return;
        writeFileSync(`${values.out}.tmp`, `${JSON.stringify({ partial: true, candidateLimit, expansionRuns, queries: reports }, null, 2)}\n`, "utf8");
        renameSync(`${values.out}.tmp`, values.out);
      },
    });
    const output = JSON.stringify({ transport: remote.llm.rerankTransport, candidateLimit, expansionRuns, ...report }, null, 2);
    if (values.out) {
      await writeFile(`${values.out}.tmp`, `${output}\n`, "utf8");
      await rename(`${values.out}.tmp`, values.out);
    }
    console.log(JSON.stringify({ transport: remote.llm.rerankTransport, summary: report.summary }, null, 2));
    const failed = report.queries.filter((q) => q.errors.length > 0).length;
    if (failed > 0) console.error(`${failed} of ${report.queries.length} queries recorded errors; see their entries in the report.`);
    return 0;
  } finally {
    removeHandlers();
    await cleanup();
  }
}

process.exit(await main());
