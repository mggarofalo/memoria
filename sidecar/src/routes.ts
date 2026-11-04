import type { FastifyInstance } from "fastify";
import type { AppContext } from "./app.ts";
import { SIDECAR_VERSION } from "./app.ts";
import { HttpError } from "./errors.ts";
import { removeDocument, resolveDeleteTarget, resolveTarget, writeDocument } from "./ingest.ts";
import { parseLineRange } from "./line-range.ts";
import { hasFilters, matchesFilters, parseBound, type DocumentMetadata, type SearchFilters } from "./metadata.ts";
import { resolveMultiGet } from "./multi-get.ts";
import { errorResponses, metadataOrNull } from "./schemas.ts";
import type { DocumentInfo, DocumentLookupError, QmdBackend, SearchQuery } from "./store.ts";

// --- helpers -----------------------------------------------------------------

function splitDisplayPath(displayPath: string): { collection: string; path: string } {
  const i = displayPath.indexOf("/");
  if (i < 0) return { collection: displayPath, path: "" };
  return { collection: displayPath.slice(0, i), path: displayPath.slice(i + 1) };
}

function documentView(doc: DocumentInfo, ctx: AppContext, extra: { fromLine?: number; maxLines?: number; body?: string | null } = {}) {
  const { collection, path } = splitDisplayPath(doc.displayPath);
  return {
    docid: doc.docid,
    file: doc.displayPath,
    collection: doc.collectionName || collection,
    path,
    title: doc.title,
    context: doc.context,
    modifiedAt: doc.modifiedAt,
    bodyLength: doc.bodyLength,
    fromLine: extra.fromLine ?? null,
    maxLines: extra.maxLines ?? null,
    body: extra.body === undefined ? (doc.body ?? null) : extra.body,
    metadata: ctx.metadata.get(doc.displayPath) ?? null,
  };
}

function lookupErrorBody(err: DocumentLookupError) {
  if (err.error === "excluded_by_ignore") {
    return {
      error: "excluded_by_ignore",
      message: `Document is excluded by an ignore rule: ${err.query}`,
      details: { collection: err.collection, path: err.path, rule: err.rule },
    };
  }
  return { error: "not_found", message: `Document not found: ${err.query}`, details: { similarFiles: err.similarFiles } };
}

async function resolveCollections(backend: QmdBackend, requested: string[] | undefined): Promise<string[]> {
  const known = await backend.listCollections();
  const names = new Set(known.map((c) => c.name));
  if (requested === undefined || requested.length === 0) {
    const defaults = await backend.getDefaultCollectionNames();
    return defaults.length > 0 ? defaults : [...names];
  }
  const unknown = requested.filter((c) => !names.has(c));
  if (unknown.length > 0) {
    throw new HttpError(400, "unknown_collection", `Unknown collection(s): ${unknown.join(", ")}`);
  }
  return [...new Set(requested)];
}

// --- schemas local to routes -------------------------------------------------

const stringOrList = { anyOf: [{ type: "string", minLength: 1 }, { type: "array", items: { type: "string", minLength: 1 }, minItems: 1, maxItems: 50 }] } as const;

const filtersSchema = {
  type: "object",
  additionalProperties: false,
  description: "Frontmatter filters, applied after retrieval. Documents without frontmatter never match an active filter.",
  properties: {
    project: { ...stringOrList, description: "`project` frontmatter key; any-of when a list. Case-insensitive." },
    branch: { ...stringOrList, description: "`git_branch` frontmatter key; any-of when a list. Case-insensitive." },
    since: { type: "string", description: "Inclusive lower bound on start_time (or date): YYYY-MM-DD or ISO timestamp." },
    until: { type: "string", description: "Inclusive upper bound; a bare date covers the whole day." },
    tags: { type: "array", items: { type: "string", minLength: 1 }, maxItems: 50, description: "Every listed tag must be present. Case-insensitive." },
  },
} as const;

const searchResultSchema = {
  type: "object",
  required: ["docid", "file", "collection", "path", "title", "score", "context", "line", "snippet", "metadata"],
  properties: {
    docid: { type: "string" },
    file: { type: "string", description: "`collection/path`" },
    collection: { type: "string" },
    path: { type: "string" },
    title: { type: "string" },
    score: { type: "number", description: "0–1 after reranking; RRF-derived when rerank=false." },
    context: { type: "string", nullable: true },
    line: { type: "integer", description: "1-indexed line of the best-matching chunk; pass to /docs/{id}:line to read around it." },
    snippet: { type: "string" },
    metadata: metadataOrNull,
  },
} as const;

interface SearchBody {
  query?: string;
  searches?: SearchQuery[];
  intent?: string;
  collections?: string[];
  filters?: SearchFilters;
  limit: number;
  minScore: number;
  candidateLimit?: number;
  rerank: boolean;
}

interface DocsQuery {
  fromLine?: number;
  maxLines?: number;
  lineNumbers: boolean;
  body: boolean;
}

interface MultiGetBody {
  pattern?: string;
  ids?: string[];
  maxBytes: number;
  maxLines?: number;
  lineNumbers: boolean;
  includeBody: boolean;
}

interface IngestBody {
  collection: string;
  path: string;
  content: string;
  embed: boolean;
  overwrite: boolean;
}

interface DeleteQuery {
  collection: string;
  path: string;
}

interface ReindexBody {
  collections?: string[];
  embed: boolean;
  force: boolean;
}

// --- routes ------------------------------------------------------------------

export async function registerRoutes(app: FastifyInstance, ctx: AppContext): Promise<void> {
  const { backend, config } = ctx;

  // ---- system ----------------------------------------------------------------

  app.get(
    "/health",
    {
      schema: {
        tags: ["system"],
        summary: "Liveness",
        description: "The process is up. Unauthenticated; does not touch the index or the models.",
        security: [],
        response: {
          200: {
            type: "object",
            required: ["status", "version", "uptimeSeconds"],
            properties: { status: { type: "string", enum: ["ok"] }, version: { type: "string" }, uptimeSeconds: { type: "integer" } },
          },
        },
      },
    },
    async () => ({ status: "ok" as const, version: SIDECAR_VERSION, uptimeSeconds: Math.floor((Date.now() - ctx.startedAt) / 1000) }),
  );

  const readySchema = {
    type: "object",
    required: ["status", "version", "uptimeSeconds", "index", "models", "warmup", "metadata", "jobs"],
    properties: {
      status: { type: "string", enum: ["ready", "warming"] },
      version: { type: "string" },
      uptimeSeconds: { type: "integer" },
      index: {
        type: "object",
        required: ["dbPath", "totalDocuments", "needsEmbedding", "hasVectorIndex"],
        properties: {
          dbPath: { type: "string" },
          totalDocuments: { type: "integer" },
          needsEmbedding: { type: "integer" },
          hasVectorIndex: { type: "boolean" },
        },
      },
      models: {
        type: "object",
        required: ["embed", "rerank", "generate", "loaded", "resident"],
        properties: {
          embed: { type: "string" },
          rerank: { type: "string" },
          generate: { type: "string" },
          loaded: {
            type: "object",
            required: ["embed", "rerank", "generate"],
            properties: { embed: { type: "boolean" }, rerank: { type: "boolean" }, generate: { type: "boolean" } },
          },
          resident: { type: "boolean", description: "True once every model is loaded in memory." },
        },
      },
      warmup: {
        type: "object",
        required: ["requested", "done", "error", "durationMs"],
        properties: {
          requested: { type: "boolean" },
          done: { type: "boolean" },
          error: { type: "string", nullable: true },
          durationMs: { type: "number", nullable: true },
        },
      },
      metadata: {
        type: "object",
        required: ["documents", "refreshedAt"],
        properties: { documents: { type: "integer" }, refreshedAt: { type: "string", nullable: true } },
      },
      jobs: { type: "object", required: ["pending"], properties: { pending: { type: "integer" } } },
    },
  } as const;

  app.get<{ Querystring: { requireModels: boolean } }>(
    "/ready",
    {
      schema: {
        tags: ["system"],
        summary: "Readiness",
        description:
          "The index is open and the sidecar can serve. `models.resident` reports whether the embedding, reranking and " +
          "expansion models are loaded; with `requireModels=true` the route returns 503 until they are.",
        querystring: {
          type: "object",
          properties: { requireModels: { type: "boolean", default: false } },
        },
        response: { 200: readySchema, 503: readySchema, ...errorResponses },
      },
    },
    async (request, reply) => {
      const status = await backend.getStatus();
      const models = backend.models();
      const body = {
        status: models.resident ? ("ready" as const) : ("warming" as const),
        version: SIDECAR_VERSION,
        uptimeSeconds: Math.floor((Date.now() - ctx.startedAt) / 1000),
        index: {
          dbPath: backend.dbPath,
          totalDocuments: status.totalDocuments,
          needsEmbedding: status.needsEmbedding,
          hasVectorIndex: status.hasVectorIndex,
        },
        models,
        warmup: ctx.warmup,
        metadata: { documents: ctx.metadata.size, refreshedAt: ctx.metadata.refreshedAt?.toISOString() ?? null },
        jobs: { pending: ctx.jobs.pending },
      };
      if (request.query.requireModels && !models.resident) {
        return reply.code(503).send(body);
      }
      return body;
    },
  );

  app.get(
    "/status",
    {
      schema: {
        tags: ["system"],
        summary: "Index status",
        description: "QMD's own index status: document counts, embedding backlog, and per-collection stats.",
        response: {
          200: {
            type: "object",
            required: ["totalDocuments", "needsEmbedding", "hasVectorIndex", "collections"],
            properties: {
              totalDocuments: { type: "integer" },
              needsEmbedding: { type: "integer" },
              hasVectorIndex: { type: "boolean" },
              collections: {
                type: "array",
                items: {
                  type: "object",
                  required: ["name", "path", "pattern", "documents", "lastUpdated"],
                  properties: {
                    name: { type: "string" },
                    path: { type: "string", nullable: true },
                    pattern: { type: "string", nullable: true },
                    documents: { type: "integer" },
                    lastUpdated: { type: "string" },
                  },
                },
              },
            },
          },
          ...errorResponses,
        },
      },
    },
    async () => backend.getStatus(),
  );

  // ---- search ----------------------------------------------------------------

  app.post<{ Body: SearchBody }>(
    "/search",
    {
      schema: {
        tags: ["search"],
        summary: "Hybrid search",
        description:
          "Run QMD's hybrid pipeline (BM25 + vectors, RRF fusion, optional LLM rerank). Provide either `query` " +
          "(auto-expanded into lex/vec/hyde variants by the local expansion model) or `searches` (typed sub-queries " +
          "you compose yourself; the first gets 2x weight). `filters` are applied after retrieval on frontmatter, so a " +
          "filtered call fetches extra candidates and may return fewer than `limit`.",
        body: {
          type: "object",
          additionalProperties: false,
          properties: {
            query: { type: "string", minLength: 1, maxLength: 4000, description: "Plain-text query. Mutually exclusive with `searches`." },
            searches: {
              type: "array",
              minItems: 1,
              maxItems: 10,
              description: "Typed sub-queries. lex → BM25, vec → embedding similarity, hyde → hypothetical document embedded.",
              items: {
                type: "object",
                required: ["type", "query"],
                additionalProperties: false,
                properties: {
                  type: { type: "string", enum: ["lex", "vec", "hyde"] },
                  query: { type: "string", minLength: 1, maxLength: 4000 },
                },
              },
            },
            intent: { type: "string", maxLength: 4000, description: "Background context that steers reranking and snippet selection. Does not search on its own." },
            collections: { type: "array", items: { type: "string", minLength: 1 }, maxItems: 50, description: "Defaults to QMD's default-included collections." },
            filters: filtersSchema,
            limit: { type: "integer", minimum: 1, maximum: config.searchMaxLimit, default: 10 },
            minScore: { type: "number", minimum: 0, maximum: 1, default: 0 },
            candidateLimit: { type: "integer", minimum: 1, maximum: 1000, description: "Candidates passed to the reranker (QMD default 40)." },
            rerank: { type: "boolean", default: true, description: "false skips the LLM reranker for sub-second lex+vec fusion." },
          },
        },
        response: {
          200: {
            type: "object",
            required: ["results", "total", "tookMs", "query"],
            properties: {
              results: { type: "array", items: searchResultSchema },
              total: { type: "integer" },
              tookMs: { type: "integer" },
              query: {
                type: "object",
                required: ["collections", "rerank", "filtered", "fetched", "limit"],
                properties: {
                  collections: { type: "array", items: { type: "string" } },
                  rerank: { type: "boolean" },
                  filtered: { type: "boolean", description: "True when frontmatter filters were applied." },
                  fetched: { type: "integer", description: "Candidates retrieved from QMD before filtering." },
                  limit: { type: "integer" },
                },
              },
            },
          },
          ...errorResponses,
        },
      },
    },
    async (request) => {
      const body = request.body;
      const hasQuery = typeof body.query === "string" && body.query.trim() !== "";
      const hasSearches = Array.isArray(body.searches) && body.searches.length > 0;
      if (hasQuery === hasSearches) {
        throw new HttpError(400, "validation", "Provide exactly one of `query` or `searches`");
      }
      const filters: SearchFilters | undefined = hasFilters(body.filters) ? body.filters : undefined;
      const filtered = filters !== undefined;
      if (filters) {
        // Validate bounds up front so a bad date is a 400, not an empty result.
        try {
          if (filters.since?.trim()) parseBound(filters.since, "since");
          if (filters.until?.trim()) parseBound(filters.until, "until");
        } catch (err) {
          throw new HttpError(400, "validation", err instanceof Error ? err.message : String(err));
        }
      }
      const collections = await resolveCollections(backend, body.collections);
      const fetchLimit = filtered ? Math.min(body.limit * config.filterOverfetch, config.filterMaxFetch) : body.limit;
      const candidateLimit = body.candidateLimit ?? (filtered && fetchLimit > 40 ? fetchLimit : undefined);

      const started = Date.now();
      const hits = await backend.search({
        ...(hasQuery ? { query: body.query!.trim() } : {}),
        ...(hasSearches ? { queries: body.searches!.map((s) => ({ type: s.type, query: s.query })) } : {}),
        ...(body.intent ? { intent: body.intent } : {}),
        ...(candidateLimit !== undefined ? { candidateLimit } : {}),
        collections,
        limit: fetchLimit,
        minScore: body.minScore,
        rerank: body.rerank,
      });

      const primaryQuery = hasQuery
        ? body.query!.trim()
        : (body.searches!.find((s) => s.type === "lex")?.query ?? body.searches!.find((s) => s.type === "vec")?.query ?? body.searches![0]!.query);

      const results = [];
      for (const hit of hits) {
        const meta: DocumentMetadata | undefined = ctx.metadata.get(hit.displayPath);
        if (filters && !matchesFilters(meta, filters)) continue;
        const { line, snippet } = backend.snippet(hit.body, primaryQuery, 300, hit.bestChunkPos, hit.bestChunk.length, body.intent);
        const { collection, path } = splitDisplayPath(hit.displayPath);
        results.push({
          docid: hit.docid,
          file: hit.displayPath,
          collection,
          path,
          title: hit.title,
          score: Math.round(hit.score * 10000) / 10000,
          context: hit.context,
          line,
          snippet,
          metadata: meta ?? null,
        });
        if (results.length >= body.limit) break;
      }

      return {
        results,
        total: results.length,
        tookMs: Date.now() - started,
        query: { collections, rerank: body.rerank, filtered, fetched: hits.length, limit: body.limit },
      };
    },
  );

  // ---- documents -------------------------------------------------------------

  app.get<{ Params: { "*": string }; Querystring: DocsQuery }>(
    "/docs/*",
    {
      schema: {
        tags: ["documents"],
        summary: "Get a document",
        description:
          "Fetch one document by display path (`collection/path.md`), `qmd://` URI, or docid (`abc123` or `%23abc123`). " +
          "A line-range suffix narrows the body: `path.md:100` from line 100, `path.md:100:40` for 40 lines from line 100. " +
          "Explicit `fromLine`/`maxLines` query parameters take precedence over the suffix.",
        params: { type: "object", properties: { "*": { type: "string", description: "Document reference, optionally with `:from` or `:from:count`." } } },
        querystring: {
          type: "object",
          properties: {
            fromLine: { type: "integer", minimum: 1 },
            maxLines: { type: "integer", minimum: 1 },
            lineNumbers: { type: "boolean", default: false, description: "Prefix each body line with `N: `." },
            body: { type: "boolean", default: true, description: "false returns metadata only." },
          },
        },
        response: { 200: { $ref: "Document#" }, ...errorResponses },
      },
    },
    async (request, reply) => {
      const raw = request.params["*"] ?? "";
      const parsed = parseLineRange(raw);
      if (parsed.lookup.trim() === "") throw new HttpError(400, "validation", "Document reference is required");
      const fromLine = request.query.fromLine ?? parsed.fromLine;
      const maxLines = request.query.maxLines ?? parsed.maxLines;

      const found = await backend.get(parsed.lookup);
      if ("error" in found) return reply.code(404).send(lookupErrorBody(found));

      let body: string | null = null;
      if (request.query.body) {
        body = (await backend.getDocumentBody(found.filepath, { ...(fromLine !== undefined ? { fromLine } : {}), ...(maxLines !== undefined ? { maxLines } : {}) })) ?? "";
        if (request.query.lineNumbers) body = backend.addLineNumbers(body, fromLine ?? 1);
      }
      return documentView(found, ctx, { ...(fromLine !== undefined ? { fromLine } : {}), ...(maxLines !== undefined ? { maxLines } : {}), body });
    },
  );

  app.post<{ Body: MultiGetBody }>(
    "/docs::multiGet",
    {
      schema: {
        tags: ["documents"],
        summary: "Get several documents",
        description:
          "Batch fetch by glob (`claude-sessions/2026-05*.md`), by a list of references (paths or docids), or both. " +
          "Each selector resolves on its own and the results merge, one entry per file; a selector that matches nothing " +
          "adds a line to `errors` without failing the others. Documents larger than `maxBytes` are listed under " +
          "`skipped` rather than returned.",
        body: {
          type: "object",
          additionalProperties: false,
          properties: {
            pattern: { type: "string", minLength: 1, description: "Glob, comma-separated references, or a comma-separated mix of both." },
            ids: { type: "array", items: { type: "string", minLength: 1 }, minItems: 1, maxItems: 200, description: "Paths or docids." },
            maxBytes: { type: "integer", minimum: 1, default: 65536 },
            maxLines: { type: "integer", minimum: 1, description: "Truncate each body to this many lines." },
            lineNumbers: { type: "boolean", default: false },
            includeBody: { type: "boolean", default: true },
          },
        },
        response: {
          200: {
            type: "object",
            required: ["docs", "skipped", "errors"],
            properties: {
              docs: { type: "array", items: { $ref: "Document#" } },
              skipped: {
                type: "array",
                items: { type: "object", required: ["file", "reason"], properties: { file: { type: "string" }, reason: { type: "string" } } },
              },
              errors: { type: "array", items: { type: "string" } },
            },
          },
          ...errorResponses,
        },
      },
    },
    async (request) => {
      const { pattern, ids } = request.body;
      if (!pattern && (!ids || ids.length === 0)) {
        throw new HttpError(400, "validation", "Provide `pattern`, `ids`, or both");
      }
      const { docs, errors } = await resolveMultiGet(backend, pattern, ids, { includeBody: request.body.includeBody, maxBytes: request.body.maxBytes });
      const out = [];
      const skipped = [];
      for (const entry of docs) {
        if (entry.skipped) {
          skipped.push({ file: entry.doc.displayPath, reason: entry.skipReason });
          continue;
        }
        let body: string | null = request.body.includeBody ? (entry.doc.body ?? "") : null;
        let truncatedTo: number | undefined;
        if (body !== null && request.body.maxLines !== undefined) {
          const lines = body.split("\n");
          if (lines.length > request.body.maxLines) {
            body = lines.slice(0, request.body.maxLines).join("\n");
            truncatedTo = request.body.maxLines;
          }
        }
        if (body !== null && request.body.lineNumbers) body = backend.addLineNumbers(body, 1);
        out.push(documentView(entry.doc, ctx, { body, ...(truncatedTo !== undefined ? { maxLines: truncatedTo } : {}) }));
      }
      return { docs: out, skipped, errors };
    },
  );

  app.post<{ Body: IngestBody }>(
    "/documents",
    {
      schema: {
        tags: ["documents"],
        summary: "Ingest a document by value",
        description:
          "Write markdown into a collection's directory on the sidecar host and queue an index update for that " +
          "collection. The file on disk is the durable artifact; the index is rebuilt from it. Returns 202 with the " +
          "job that will make the document searchable. The job is debounced: a burst of documents for one collection " +
          "shares a single job, which stays `queued` until writes go quiet. Redaction is the caller's responsibility " +
          "and must happen before this call.",
        body: {
          type: "object",
          required: ["collection", "path", "content"],
          additionalProperties: false,
          properties: {
            collection: { type: "string", minLength: 1 },
            path: { type: "string", minLength: 1, maxLength: 1024, description: "Relative to the collection root; must match the collection's glob." },
            content: { type: "string", description: "Full markdown, frontmatter included." },
            embed: { type: "boolean", default: true, description: "Generate embeddings after indexing." },
            overwrite: { type: "boolean", default: true, description: "false returns 409 when the path already exists." },
          },
        },
        response: {
          202: {
            type: "object",
            required: ["collection", "path", "file", "bytes", "created", "metadata", "job"],
            properties: {
              collection: { type: "string" },
              path: { type: "string" },
              file: { type: "string" },
              bytes: { type: "integer" },
              created: { type: "boolean" },
              metadata: metadataOrNull,
              job: { $ref: "Job#" },
            },
          },
          ...errorResponses,
        },
      },
    },
    async (request, reply) => {
      const { collection, path, content, embed, overwrite } = request.body;
      const row = (await backend.listCollections()).find((c) => c.name === collection);
      if (!row) throw new HttpError(404, "unknown_collection", `Unknown collection: ${collection}`);
      const target = resolveTarget(row.pwd, row.glob_pattern, path, backend.ignorePatterns(collection));
      const written = await writeDocument(target, content, overwrite);
      const file = `${collection}/${target.relative}`;
      const metadata = ctx.metadata.set(file, content) ?? null;
      const job = ctx.jobs.enqueue({ collections: [collection], embed, force: false, reason: `POST /documents ${file}` }, { debounce: true });
      request.log.info({ file, bytes: written.bytes, created: written.created, jobId: job.id }, "document ingested");
      return reply.code(202).send({ collection, path: target.relative, file, bytes: written.bytes, created: written.created, metadata, job });
    },
  );

  app.delete<{ Querystring: DeleteQuery }>(
    "/documents",
    {
      schema: {
        tags: ["documents"],
        summary: "Delete a document",
        description:
          "Remove a document's file from its collection's directory and queue an index update. Only safety checks " +
          "apply, unlike `POST /documents`: a file outside the collection's glob or under an ignore rule can still be " +
          "removed. The path must stay inside the root, cross no link, and match the entry's exact name, so a case " +
          "alias returns 409. Idempotent: a path with no file returns 202 with " +
          "`deleted: false` and still queues the update, so a delete that was interrupted before reindexing can be " +
          "retried. The document stays searchable until the index is updated. The returned job is debounced like an " +
          "ingest; when that wait matters, `POST /reindex` for the collection updates the index without it.",
        querystring: {
          type: "object",
          required: ["collection", "path"],
          properties: {
            collection: { type: "string", minLength: 1 },
            path: { type: "string", minLength: 1, maxLength: 1024, description: "Relative to the collection root. Need not match the collection's glob." },
          },
        },
        response: {
          202: {
            type: "object",
            required: ["collection", "path", "file", "deleted", "job"],
            properties: {
              collection: { type: "string" },
              path: { type: "string" },
              file: { type: "string" },
              deleted: { type: "boolean", description: "False when no file existed at the path." },
              job: { $ref: "Job#" },
            },
          },
          ...errorResponses,
        },
      },
    },
    async (request, reply) => {
      const { collection, path } = request.query;
      const row = (await backend.listCollections()).find((c) => c.name === collection);
      if (!row) throw new HttpError(404, "unknown_collection", `Unknown collection: ${collection}`);
      const target = resolveDeleteTarget(row.pwd, path);
      const deleted = await removeDocument(target);
      const file = `${collection}/${target.relative}`;
      ctx.metadata.delete(file);
      const job = ctx.jobs.enqueue({ collections: [collection], embed: false, force: false, reason: `DELETE /documents ${file}` }, { debounce: true });
      request.log.info({ file, deleted, jobId: job.id }, "document deleted");
      return reply.code(202).send({ collection, path: target.relative, file, deleted, job });
    },
  );

  // ---- collections -----------------------------------------------------------

  app.get(
    "/collections",
    {
      schema: {
        tags: ["collections"],
        summary: "List collections",
        description: "Every collection in the index with its document count and root context.",
        response: {
          200: {
            type: "object",
            required: ["collections"],
            properties: {
              collections: {
                type: "array",
                items: {
                  type: "object",
                  required: ["name", "path", "pattern", "documents", "totalDocuments", "lastModified", "includeByDefault", "context"],
                  properties: {
                    name: { type: "string" },
                    path: { type: "string", description: "Collection root on the sidecar host." },
                    pattern: { type: "string" },
                    documents: { type: "integer", description: "Active (indexed, not removed) documents." },
                    totalDocuments: { type: "integer", description: "Including inactive rows kept for history." },
                    lastModified: { type: "string", nullable: true },
                    includeByDefault: { type: "boolean" },
                    context: { type: "string", nullable: true, description: "Root-level context text, if configured." },
                  },
                },
              },
            },
          },
          ...errorResponses,
        },
      },
    },
    async () => {
      const [rows, contexts] = await Promise.all([backend.listCollections(), backend.listContexts()]);
      const rootContext = new Map<string, string>();
      for (const c of contexts) {
        if (c.path === "" || c.path === "/" || c.path === ".") rootContext.set(c.collection, c.context);
      }
      return {
        collections: rows.map((r) => ({
          name: r.name,
          path: r.pwd,
          pattern: r.glob_pattern,
          documents: r.active_count,
          totalDocuments: r.doc_count,
          lastModified: r.last_modified,
          includeByDefault: r.includeByDefault,
          context: rootContext.get(r.name) ?? null,
        })),
      };
    },
  );

  // ---- index -----------------------------------------------------------------

  app.post<{ Body: ReindexBody }>(
    "/reindex",
    {
      schema: {
        tags: ["index"],
        summary: "Queue a reindex",
        description:
          "Rescan the collections' directories (`qmd update`) and, by default, embed what changed (`qmd embed`). " +
          "Jobs run one at a time behind a single worker; an identical job already waiting is returned instead of duplicated, " +
          "and starts as soon as the worker is free even if it was waiting on the document debounce.",
        body: {
          type: "object",
          additionalProperties: false,
          properties: {
            collections: { type: "array", items: { type: "string", minLength: 1 }, minItems: 1, maxItems: 50, description: "Defaults to every collection." },
            embed: { type: "boolean", default: true },
            force: { type: "boolean", default: false, description: "Re-embed every document, not only changed ones. Expensive." },
          },
        },
        response: { 202: { type: "object", required: ["job"], properties: { job: { $ref: "Job#" } } }, ...errorResponses },
      },
    },
    async (request, reply) => {
      const requested = request.body.collections;
      if (requested) await resolveCollections(backend, requested);
      const job = ctx.jobs.enqueue({
        collections: requested ? [...new Set(requested)] : null,
        embed: request.body.embed,
        force: request.body.force,
        reason: "POST /reindex",
      });
      return reply.code(202).send({ job });
    },
  );

  app.get(
    "/reindex/jobs",
    {
      schema: {
        tags: ["index"],
        summary: "List recent jobs",
        description: "Most recent first. The sidecar keeps the last 100 in memory; history does not survive a restart.",
        response: { 200: { type: "object", required: ["jobs"], properties: { jobs: { type: "array", items: { $ref: "Job#" } } } }, ...errorResponses },
      },
    },
    async () => ({ jobs: ctx.jobs.list() }),
  );

  app.get<{ Params: { id: string } }>(
    "/reindex/jobs/:id",
    {
      schema: {
        tags: ["index"],
        summary: "Get a job",
        params: { type: "object", required: ["id"], properties: { id: { type: "string" } } },
        response: { 200: { type: "object", required: ["job"], properties: { job: { $ref: "Job#" } } }, ...errorResponses },
      },
    },
    async (request, reply) => {
      const job = ctx.jobs.get(request.params.id);
      if (!job) return reply.code(404).send({ error: "not_found", message: `No job ${request.params.id}` });
      return { job };
    },
  );

  // ---- contract --------------------------------------------------------------

  app.get(
    "/openapi.json",
    {
      schema: {
        tags: ["system"],
        summary: "OpenAPI document",
        description: "The contract this process serves. Unauthenticated. The same document is committed as sidecar/openapi.json.",
        security: [],
        response: { 200: { type: "object", additionalProperties: true } },
      },
    },
    async () => app.swagger(),
  );
}
