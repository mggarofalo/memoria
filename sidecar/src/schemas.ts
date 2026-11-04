/**
 * JSON Schemas shared by routes. Fastify validates requests and serialises
 * responses from these, and @fastify/swagger turns them into openapi.json.
 * Keep them complete: fast-json-stringify drops properties it does not know.
 */

export const errorSchema = {
  $id: "Error",
  type: "object",
  required: ["error", "message"],
  properties: {
    error: { type: "string", description: "Stable machine-readable code." },
    message: { type: "string" },
    details: { description: "Code-specific extra data.", nullable: true },
  },
} as const;

export const metadataSchema = {
  $id: "DocumentMetadata",
  type: "object",
  description: "Fields lifted from the document's YAML frontmatter. Null when the document has none.",
  required: ["tags"],
  properties: {
    project: { type: "string" },
    branch: { type: "string" },
    date: { type: "string", description: "YYYY-MM-DD" },
    startTime: { type: "string" },
    endTime: { type: "string" },
    tags: { type: "array", items: { type: "string" } },
    sessionId: { type: "string" },
    source: { type: "string" },
  },
} as const;

export const metadataOrNull = { anyOf: [{ $ref: "DocumentMetadata#" }, { type: "null" }] } as const;

export const jobSchema = {
  $id: "Job",
  type: "object",
  required: ["id", "state", "request", "createdAt", "startedAt", "finishedAt", "result", "error"],
  properties: {
    id: { type: "string" },
    state: { type: "string", enum: ["queued", "running", "succeeded", "failed"] },
    request: {
      type: "object",
      required: ["collections", "embed", "force", "reason"],
      properties: {
        collections: { type: "array", items: { type: "string" }, nullable: true },
        embed: { type: "boolean" },
        force: { type: "boolean" },
        reason: { type: "string" },
      },
    },
    createdAt: { type: "string" },
    startedAt: { type: "string", nullable: true },
    finishedAt: { type: "string", nullable: true },
    result: {
      type: "object",
      nullable: true,
      required: ["update", "embed", "metadata"],
      properties: {
        update: {
          type: "object",
          required: ["collections", "indexed", "updated", "unchanged", "removed", "skipped", "needsEmbedding"],
          properties: {
            collections: { type: "integer" },
            indexed: { type: "integer" },
            updated: { type: "integer" },
            unchanged: { type: "integer" },
            removed: { type: "integer" },
            skipped: { type: "integer" },
            needsEmbedding: { type: "integer" },
          },
        },
        embed: {
          type: "object",
          nullable: true,
          required: ["docsProcessed", "chunksEmbedded", "errors", "durationMs"],
          properties: {
            docsProcessed: { type: "integer" },
            chunksEmbedded: { type: "integer" },
            errors: { type: "integer" },
            durationMs: { type: "number" },
            docsPending: { type: "integer", description: "Documents this run could not finish, including any it set aside. The job does not report success." },
            docsDeadLettered: { type: "integer", description: "Of those, how many were set aside for good. Later jobs skip them until a retry is due: see `GET /queue/dead-letters`." },
          },
        },
        metadata: {
          type: "object",
          required: ["documents", "withMetadata"],
          properties: { documents: { type: "integer" }, withMetadata: { type: "integer" } },
        },
      },
    },
    error: { type: "string", nullable: true },
    priority: { type: "string", enum: ["background"], description: "Reindex jobs are background work." },
    attempts: { type: "integer", description: "How many times this job has started." },
    worker: { type: "string", nullable: true, description: "Workers that embedded for the latest attempt, comma-separated." },
  },
} as const;

export const documentSchema = {
  $id: "Document",
  type: "object",
  required: ["docid", "file", "collection", "path", "title", "context", "modifiedAt", "bodyLength", "metadata"],
  properties: {
    docid: { type: "string", description: "Six-character content id. Accepted anywhere a document reference is." },
    file: { type: "string", description: "`collection/path`, QMD's display path." },
    collection: { type: "string" },
    path: { type: "string", description: "Path relative to the collection root." },
    title: { type: "string" },
    context: { type: "string", nullable: true },
    modifiedAt: { type: "string" },
    bodyLength: { type: "integer" },
    fromLine: { type: "integer", nullable: true, description: "First line included in `body` (1-indexed) when a range was requested." },
    maxLines: { type: "integer", nullable: true },
    body: { type: "string", nullable: true },
    metadata: metadataOrNull,
  },
} as const;

export const errorResponses = {
  400: { $ref: "Error#" },
  401: { $ref: "Error#" },
  404: { $ref: "Error#" },
  409: { $ref: "Error#" },
  500: { $ref: "Error#" },
} as const;
