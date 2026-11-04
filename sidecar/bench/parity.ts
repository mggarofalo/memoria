/**
 * Compare in-process QMD with the remote adapter on a real index and real models.
 *
 *   MEMORIA_QMD_WORKER_URL=http://host:8383 MEMORIA_QMD_WORKER_TOKEN=... \
 *     npm run bench:parity -- --db /path/to/index.sqlite --out parity.json
 *
 * Needs the GGUF models on this host for the in-process side. Takes a copy of the
 * index and works only on copies, deleted afterward; the index named by --db is
 * only read. Exits 0 without doing anything when no worker URL is set, so it can
 * sit in CI without ever being required.
 *
 * It reports numbers and makes no decision. See docs/workers.md for reading them.
 */
import { copyFile, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { parseArgs } from "node:util";
import { loadConfig } from "../src/config.ts";
import { openBackend, type OpenedBackend } from "../src/store.ts";
import { runParity, validateQuerySet, type BenchQuery, type ParitySide } from "./run.ts";

function side(backend: OpenedBackend): ParitySide {
  return {
    candidates: async (query: BenchQuery, limit: number) => {
      const hits = await backend.search({ queries: query.searches, collections: [query.collection], limit, minScore: 0, rerank: false });
      return hits.map((h) => ({ file: h.displayPath, text: h.bestChunk }));
    },
    rerank: (query, documents) => backend.llm.rerank(query, documents),
    embed: async (text) => (await backend.llm.embedBatch([text]))[0]?.embedding ?? null,
    search: async (query, limit) => {
      const hits = await backend.search({ queries: query.searches, intent: query.intent, collections: [query.collection], limit, minScore: 0, rerank: true });
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
  if (!values.db) {
    console.error("--db is required. There is deliberately no default: name the index to compare against.");
    return 2;
  }
  if (!existsSync(values.db)) {
    console.error(`No index at ${values.db}.`);
    return 2;
  }
  const candidateLimit = Number(values["candidate-limit"]);
  const expansionRuns = Number(values["expansion-runs"]);
  if (!Number.isInteger(candidateLimit) || candidateLimit < 1 || !Number.isInteger(expansionRuns) || expansionRuns < 2) {
    console.error("--candidate-limit must be at least 1, and --expansion-runs at least 2 so a baseline exists.");
    return 2;
  }
  const config = loadConfig({ ...process.env, MEMORIA_QMD_LLM: "remote" });
  if (config.llm.mode !== "remote") return 2;

  const queryFile = values.queries ?? new URL("./queries.json", import.meta.url);
  const queries = validateQuerySet(JSON.parse(await readFile(queryFile, "utf8")));

  const workspace = await mkdtemp(path.join(tmpdir(), "memoria-parity-"));
  let local: OpenedBackend | undefined;
  let remote: OpenedBackend | undefined;
  try {
    // Opened local first: remote mode replaces the SDK's process-wide default,
    // which the local side's document path would otherwise share. These runs
    // embed queries only, through each store's own instance, so that is safe.
    local = await openBackend({ dbPath: await copyIndex(values.db, workspace, "local.sqlite"), keepWarm: true, llm: { mode: "local" } });
    remote = await openBackend({
      dbPath: await copyIndex(values.db, workspace, "remote.sqlite"),
      keepWarm: true,
      llm: {
        mode: "remote",
        worker: { url: config.llm.workerUrl, token: config.llm.workerToken, embedDimensions: config.llm.embedDimensions },
        // A silent fallback would make the remote side measure this machine.
        fallback: false,
      },
      onLlmDegraded: (entry) => console.error(JSON.stringify(entry)),
    });
    const report = await runParity(queries, side(local), side(remote), {
      candidateLimit,
      expansionRuns,
      onProgress: (done, total, id) => console.error(`[${done}/${total}] ${id}`),
    });
    const output = JSON.stringify({ transport: remote.llm.rerankTransport, candidateLimit, expansionRuns, ...report }, null, 2);
    if (values.out) await writeFile(values.out, `${output}\n`, "utf8");
    console.log(JSON.stringify({ transport: remote.llm.rerankTransport, summary: report.summary }, null, 2));
    const failed = report.queries.filter((q) => q.errors.length > 0).length;
    if (failed > 0) console.error(`${failed} of ${report.queries.length} queries recorded errors; see their entries in the report.`);
    return 0;
  } finally {
    await remote?.close();
    await local?.close();
    await rm(workspace, { recursive: true, force: true, maxRetries: 3 });
  }
}

process.exit(await main());
