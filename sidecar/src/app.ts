import Fastify, { type FastifyError, type FastifyInstance, type FastifyServerOptions } from "fastify";
import swagger from "@fastify/swagger";
import { bearerMatches } from "./auth.ts";
import type { SidecarConfig } from "./config.ts";
import { HttpError } from "./errors.ts";
import { JobQueue, type EmbedSummary, type JobResult, type ReindexRequest } from "./jobs.ts";
import { MetadataIndex } from "./metadata.ts";
import { registerRoutes } from "./routes.ts";
import { documentSchema, errorSchema, jobSchema, metadataSchema } from "./schemas.ts";
import type { QmdBackend } from "./store.ts";

export const SIDECAR_VERSION = "0.1.0";

/** Routes that never require the shared secret. */
export const PUBLIC_PATHS: ReadonlySet<string> = new Set(["/health", "/openapi.json"]);

export interface WarmupState {
  requested: boolean;
  done: boolean;
  error: string | null;
  durationMs: number | null;
}

export interface AppContext {
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

  const runJob = async (request: ReindexRequest): Promise<JobResult> => {
    const update = await backend.update(request.collections ? { collections: request.collections } : {});
    let embed: EmbedSummary | null = null;
    if (request.embed) {
      if (request.collections === null) {
        embed = await backend.embed({ force: request.force });
      } else {
        // The SDK embeds one collection per call; keep the job scoped to
        // exactly the collections that were updated.
        embed = { docsProcessed: 0, chunksEmbedded: 0, errors: 0, durationMs: 0 };
        for (const collection of request.collections) {
          const r = await backend.embed({ force: request.force, collection });
          embed.docsProcessed += r.docsProcessed;
          embed.chunksEmbedded += r.chunksEmbedded;
          embed.errors += r.errors;
          embed.durationMs += r.durationMs;
        }
      }
    }
    const meta = metadata.refresh();
    return { update, embed, metadata: meta };
  };

  const ctx: AppContext = {
    backend,
    config,
    metadata,
    jobs: new JobQueue(runJob),
    startedAt: Date.now(),
    warmup: { requested: config.warmup, done: false, error: null, durationMs: null },
  };

  const fastify = Fastify({
    logger: options.logger ?? false,
    bodyLimit: config.bodyLimitBytes,
    trustProxy: false,
    // Memoria.Api sends its W3C trace id here; adopting it as reqId lets one id
    // join the API's request log line and this process's pino lines.
    requestIdHeader: "request-id",
    ajv: { customOptions: { removeAdditional: false, useDefaults: true, coerceTypes: "array", allErrors: false } },
  });
  const app = Object.assign(fastify, { ctx }) as App;

  app.addSchema(errorSchema);
  app.addSchema(metadataSchema);
  app.addSchema(jobSchema);
  app.addSchema(documentSchema);

  await app.register(swagger, {
    openapi: {
      openapi: "3.1.0",
      info: {
        title: "memoria-qmd",
        description:
          "REST sidecar over the QMD SDK. Runs on the host that holds the models and index.sqlite; " +
          "Memoria.Api is its only intended caller. Every route except /health and /openapi.json requires " +
          "`Authorization: Bearer <shared secret>`.",
        version: SIDECAR_VERSION,
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
        { name: "documents", description: "Read documents and ingest new ones by value." },
        { name: "collections", description: "QMD collections and their contexts." },
        { name: "index", description: "Serialized reindex jobs." },
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
      return { schema, url };
    },
  });

  // --- auth -------------------------------------------------------------------
  app.addHook("onRequest", async (request, reply) => {
    if (config.secret === undefined) return;
    const pathname = request.url.split("?")[0] ?? request.url;
    if (PUBLIC_PATHS.has(pathname)) return;
    if (!bearerMatches(request.headers.authorization, config.secret)) {
      reply.header("WWW-Authenticate", 'Bearer realm="memoria-qmd"');
      await reply.code(401).send({ error: "unauthorized", message: "Missing or invalid bearer token" });
    }
  });

  // --- errors -----------------------------------------------------------------
  app.setNotFoundHandler(async (request, reply) => {
    await reply.code(404).send({ error: "no_route", message: `${request.method} ${request.url} is not a route` });
  });

  app.setErrorHandler(async (error: FastifyError | Error, request, reply) => {
    if (error instanceof HttpError) {
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
