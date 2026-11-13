import { SearchBusy, SearchGate } from "./search-gate.ts";
import { randomUUID } from "node:crypto";
import { createServer } from "node:http";
import { limitConnectionsPerSource, loggablePath, refuseAtBoundary, validRequestId } from "./boundary.ts";
import Fastify, { type FastifyError, type FastifyInstance, type FastifyServerOptions, LogController } from "fastify";
import swagger from "@fastify/swagger";
import { bearerMatches } from "./auth.ts";
import type { SidecarConfig } from "./config.ts";
import { HttpError } from "./errors.ts";
import { JobCancelled, JobPartial, JobQueue, type EmbedSummary, type JobResult, type JobRunContext, type JobScheduler, type JobStore, type ReindexRequest, type Job } from "./jobs.ts";
import { MetadataIndex } from "./metadata.ts";
import { registerRoutes } from "./routes.ts";
import { documentSchema, errorSchema, jobSchema, metadataSchema, workerSchema } from "./schemas.ts";
import type { QmdBackend } from "./store.ts";
import { resolveVersion } from "./version.ts";

export const SIDECAR_VERSION = resolveVersion();

/** The contract's own version, as in Memoria.Api's document. It is committed in openapi.json, so it is not the build's. */
const CONTRACT_VERSION = "v1";

/** Routes that never require the shared secret. */
export const PUBLIC_PATHS: ReadonlySet<string> = new Set(["/health", "/openapi.json"]);

export interface WarmupState {
  requested: boolean;
  done: boolean;
  error: string | null;
  durationMs: number | null;
}

export interface AppContext {
  searchGate: SearchGate;
  backend: QmdBackend;
  config: SidecarConfig;
  metadata: MetadataIndex;
  jobs: JobQueue;
  startedAt: number;
  warmup: WarmupState;
}

export interface BuildAppOptions {
  backend: QmdBackend;
  config: SidecarConfig;
  logger?: FastifyServerOptions["logger"];
  /** Clock and timers for the reindex debounce. Defaults to real ones. */
  scheduler?: JobScheduler;
  /** Where jobs persist. Without one they live in memory. */
  jobStore?: JobStore;
}

export type App = FastifyInstance & { ctx: AppContext };

/**
 * Assemble the Fastify app. Pure with respect to the process: no listening,
 * no env reads, no model loading — `main.ts` does those. Tests call this
 * with a fake backend and use `app.inject()`.
 */
export async function buildApp(options: BuildAppOptions): Promise<App> {
  const { backend, config } = options;

  const metadata = new MetadataIndex(backend);
  metadata.refresh();

  const embedRequested = async (request: ReindexRequest, context: JobRunContext): Promise<EmbedSummary> => {
    const run = { force: request.force, ...(context.firstAttempt ? {} : { forceSince: context.jobCreatedAt }), signal: context.signal, onWorker: context.noteWorker, onProgress: context.noteProgress };
    if (request.collections === null) return backend.embed(run);
    // The SDK embeds one collection per call; keep the job scoped to
    // exactly the collections that were updated.
    const total: EmbedSummary = { docsProcessed: 0, chunksEmbedded: 0, errors: 0, durationMs: 0 };
    for (const collection of request.collections) {
      if (context.signal.aborted) throw new JobCancelled();
      const r = await backend.embed({ ...run, collection });
      total.docsProcessed += r.docsProcessed;
      total.chunksEmbedded += r.chunksEmbedded;
      total.errors += r.errors;
      total.durationMs += r.durationMs;
      if (r.docsPending) total.docsPending = (total.docsPending ?? 0) + r.docsPending;
      if (r.docsDeadLettered) total.docsDeadLettered = (total.docsDeadLettered ?? 0) + r.docsDeadLettered;
    }
    return total;
  };

  const runJob = async (request: ReindexRequest, context: JobRunContext): Promise<JobResult> => {
    const update = await backend.update(request.collections ? { collections: request.collections } : {});
    // Scanned documents are lexically searchable from here, even if embedding
    // has to wait for a worker, so refresh before anything can defer the job.
    if (request.embed) metadata.refresh();
    if (context.signal.aborted) throw new JobCancelled();
    const embed = request.embed ? await embedRequested(request, context) : null;
    const result = { update, embed, metadata: metadata.refresh() };
    // Documents left behind must not read as success. See JobPartial.
    if (embed?.docsPending) throw new JobPartial(`${embed.docsPending} of ${embed.docsProcessed} documents could not be embedded, of which ${embed.docsDeadLettered ?? 0} were set aside (GET /queue/dead-letters). The rest stay pending.`, result);
    return result;
  };

  // The queue never lets a store failure reach the caller; the logger arrives below.
  let logStoreError = (_message: string): void => {};
  let logDeferred = (_job: Job, _reason: string): void => {};

  const ctx: AppContext = {
    backend,
    config,
    metadata,
    jobs: new JobQueue(runJob, {
      debounceMs: config.reindexDebounceMs,
      maxWaitMs: config.reindexMaxWaitMs,
      ...(options.scheduler ? { scheduler: options.scheduler } : {}),
      ...(options.jobStore ? { store: options.jobStore } : {}),
      onStoreError: (message) => logStoreError(message),
      onDeferred: (job, reason) => logDeferred(job, reason),
    }),
    searchGate: new SearchGate({ running: config.searchConcurrency, queued: config.searchQueue, maxWaitMs: config.searchMaxWaitMs }),
    startedAt: Date.now(),
    warmup: { requested: config.warmup, done: false, error: null, durationMs: null },
  };

  const fastify = Fastify({
    logger: options.logger ?? false,
    bodyLimit: config.bodyLimitBytes,
    trustProxy: false,
    // Logged by the hook below instead: after the auth check, bounded, with no query string.
    logController: new LogController({ disableRequestLogging: true }),
    // Memoria.Api sends its W3C trace id here; adopting it as reqId lets one id
    // join the API's request log line and this process's pino lines.
    requestIdHeader: false,
    genReqId: (request) => validRequestId(request.headers["request-id"]) ?? randomUUID(),
    requestTimeout: config.requestTimeoutMs,
    serverFactory: (handler) => {
      // Fastify's own 72 s keep-alive: Node's 5 s would close sockets a pooled client is about to reuse.
      const server = createServer({ headersTimeout: config.headersTimeoutMs, requestTimeout: config.requestTimeoutMs, keepAliveTimeout: 72_000, connectionsCheckingInterval: Math.min(config.headersTimeoutMs, 5_000) }, handler);
      server.maxConnections = config.maxConnections;
      limitConnectionsPerSource(server, config.maxConnectionsPerSource, { addresses: config.apiAddresses, limit: config.maxConnectionsPerApiAddress });
      return server;
    },
    ajv: { customOptions: { removeAdditional: false, useDefaults: true, coerceTypes: "array", allErrors: false } },
  });
  const app = Object.assign(fastify, { ctx }) as App;
  logStoreError = (message) => app.log.warn({ error: message }, "job queue could not write to its database; carrying on in memory");
  logDeferred = (job, reason) => app.log.warn({ jobId: job.id, attempts: job.attempts, reason }, "reindex job is waiting to try again");

  app.addSchema(errorSchema);
  app.addSchema(metadataSchema);
  app.addSchema(jobSchema);
  app.addSchema(documentSchema);
  app.addSchema(workerSchema);

  await app.register(swagger, {
    openapi: {
      openapi: "3.1.0",
      info: {
        title: "memoria-qmd",
        description:
          "REST sidecar over the QMD SDK. Runs on the host that holds the models and index.sqlite; " +
          "Memoria.Api is its only intended caller. Every route except /health and /openapi.json requires " +
          "`Authorization: Bearer <shared secret>`.",
        version: CONTRACT_VERSION,
      },
      components: {
        securitySchemes: {
          bearer: { type: "http", scheme: "bearer", description: "The MEMORIA_QMD_SECRET shared with Memoria.Api." },
        },
      },
      security: [{ bearer: [] }],
      tags: [
        { name: "system", description: "Liveness, readiness, index status." },
        { name: "search", description: "Hybrid retrieval with optional frontmatter filters." },
        { name: "documents", description: "Read documents, and ingest or delete them by value." },
        { name: "collections", description: "QMD collections and their contexts." },
        { name: "index", description: "Serialized, coalesced, persistent reindex jobs." },
        { name: "workers", description: "The inference worker pool, in remote LLM mode." },
      ],
    },
    refResolver: { buildLocalReference: (json) => String(json.$id) },
    transform: ({ schema, url }) => {
      // Fastify's wildcard is `*`, which no client generator accepts as a
      // parameter name. Publish it as `{reference}`.
      if (url === "/docs/*" && schema?.params) {
        const params = schema.params as { properties?: Record<string, unknown> };
        const star = params.properties?.["*"];
        return {
          schema: { ...schema, params: { type: "object", required: ["reference"], properties: { reference: star } } },
          url: "/docs/{reference}",
        };
      }
      // The router needs a regex to tell actions apart; clients do not. The double
      // colon is swagger's escape for a literal one.
      const action = /^\/(queue\/jobs|workers)\/:(\w+)\(.*\)::([\w-]+)$/.exec(url);
      if (action) return { schema, url: `/${action[1]}/{${action[2]}}::${action[3]}` };
      return { schema, url };
    },
  });

  // --- auth -------------------------------------------------------------------
  app.addHook("onRequest", async (request, reply) => {
    const refusal = refuseAtBoundary({ host: request.headers.host, origin: request.headers.origin }, config.allowedHosts);
    if (refusal) {
      await reply.code(refusal.statusCode).send({ error: refusal.error, message: refusal.message });
      return reply;
    }
    const pathname = request.url.split("?")[0] ?? request.url;
    if (PUBLIC_PATHS.has(pathname)) return;
    if (!bearerMatches(request.headers.authorization, config.secret)) {
      reply.header("WWW-Authenticate", 'Bearer realm="memoria-qmd"');
      await reply.code(401).send({ error: "unauthorized", message: "Missing or invalid bearer token" });
    }
  });

  // --- shutdown ---------------------------------------------------------------
  // `app.close()` resolves only once every connection has closed, and it closes just those
  // idle when it starts. A connection that answers a request in flight then sits idle under
  // the 72 s keep-alive, so shutdown would wait out its whole drain for nothing. Once closing
  // has begun, each response is its connection's last, and the connection closes with it.
  let closing = false;
  app.addHook("preClose", async () => {
    closing = true;
  });
  app.addHook("onSend", async (_request, reply) => {
    if (closing) reply.header("connection", "close");
  });

  app.addHook("onResponse", async (request, reply) => {
    request.log.info({ method: request.method, route: request.routeOptions.url ?? "(no route)", status: reply.statusCode, durationMs: Math.round(reply.elapsedTime) }, "request");
    // A response whose headers left before closing began kept its connection alive. Close it now it is idle.
    if (closing) app.server.closeIdleConnections();
  });

  // --- errors -----------------------------------------------------------------
  app.setNotFoundHandler(async (request, reply) => {
    await reply.code(404).send({ error: "no_route", message: `${request.method} ${loggablePath(request.url)} is not a route` });
  });

  app.setErrorHandler(async (error: FastifyError | Error, request, reply) => {
    if (error instanceof HttpError) {
      if (error instanceof SearchBusy) reply.header("Retry-After", "2");
      await reply.code(error.statusCode).send({ error: error.code, message: error.message });
      return;
    }
    const fe = error as FastifyError;
    if (fe.validation) {
      await reply.code(400).send({ error: "validation", message: fe.message, details: fe.validation });
      return;
    }
    if (fe.statusCode === 413) {
      await reply.code(413).send({ error: "payload_too_large", message: fe.message });
      return;
    }
    if (fe.statusCode === 415 || fe.code === "FST_ERR_CTP_INVALID_MEDIA_TYPE") {
      await reply.code(415).send({ error: "unsupported_media_type", message: fe.message });
      return;
    }
    if (fe.statusCode === 400) {
      await reply.code(400).send({ error: "bad_request", message: fe.message });
      return;
    }
    request.log.error({ err: error }, "unhandled error");
    await reply.code(500).send({ error: "internal", message: "Internal error; see sidecar logs" });
  });

  await registerRoutes(app, ctx);
  return app;
}

/**
 * Pay the model cold start once at boot. One reranked plain-text query walks
 * the whole pipeline — expansion, embedding, reranking — so all three models
 * are resident before the first real request.
 */
export async function warmup(ctx: AppContext, log: { info: (o: object, m: string) => void; warn: (o: object, m: string) => void }): Promise<void> {
  // Workers warm themselves. `/ready` reports their state, so nothing here could
  // make it read healthier than it is.
  if (ctx.backend.llmHealth().mode === "remote") return;
  const started = Date.now();
  try {
    await ctx.backend.search({
      query: "warm up the search models",
      limit: 1,
      minScore: 0,
      rerank: true,
    });
    ctx.warmup.done = true;
    ctx.warmup.durationMs = Date.now() - started;
    log.info({ durationMs: ctx.warmup.durationMs, models: ctx.backend.models() }, "warmup complete");
  } catch (err) {
    ctx.warmup.error = err instanceof Error ? err.message : String(err);
    ctx.warmup.durationMs = Date.now() - started;
    log.warn({ err, durationMs: ctx.warmup.durationMs }, "warmup failed; models load lazily on first request");
  }
}
