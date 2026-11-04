/**
 * Worker responses are untrusted input to the store: vectors go into
 * sqlite-vec, and rerank scores and expansions are cached in `llm_cache`, so a
 * bad answer outlives the request. A response that breaks a rule is rejected
 * whole, never clamped or padded.
 *
 * `ResponseViolation` messages are fixed text. They never quote the offending
 * values, which came from the worker.
 */
export class ResponseViolation extends Error {
  constructor(detail: string) {
    super(detail);
    this.name = "ResponseViolation";
  }
}

function fail(detail: string): never {
  throw new ResponseViolation(detail);
}

function asObject(raw: unknown): Record<string, unknown> {
  if (typeof raw !== "object" || raw === null || Array.isArray(raw)) fail("response is not an object");
  return raw as Record<string, unknown>;
}

/** Validate `{ embeddings: number[][] }`. `dimensions` of 0 only requires the batch to agree. */
export function parseEmbeddings(raw: unknown, expectedCount: number, dimensions: number): number[][] {
  const embeddings = asObject(raw).embeddings;
  if (!Array.isArray(embeddings)) fail("embeddings is not an array");
  if (embeddings.length !== expectedCount) fail("embedding count does not match the request");
  let length = dimensions;
  const out: number[][] = [];
  for (const vector of embeddings as unknown[]) {
    if (!Array.isArray(vector) || vector.length === 0) fail("an embedding is not a non-empty array");
    if (length === 0) length = vector.length;
    if (vector.length !== length) fail("an embedding has the wrong number of dimensions");
    checkStorable(vector);
    out.push(vector as number[]);
  }
  return out;
}

/**
 * Vectors are stored as float32, and judged by what survives that cast: `1e308`
 * becomes infinity and `1e-320` becomes zero, either of which breaks cosine
 * distance for every search that touches the vector.
 */
function checkStorable(vector: unknown[]): void {
  let norm = 0;
  for (const value of vector) {
    if (typeof value !== "number" || !Number.isFinite(value)) fail("an embedding holds a non-finite value");
    const stored = Math.fround(value);
    if (!Number.isFinite(stored) || (value !== 0 && stored === 0)) fail("an embedding holds a value float32 cannot store");
    norm = Math.fround(norm + Math.fround(stored * stored));
  }
  // Cosine distance is undefined for a zero vector, and for one whose length overflows.
  if (norm === 0) fail("an embedding is all zeros");
  if (!Number.isFinite(norm)) fail("an embedding is too long for float32");
}

/** An embed response also says which model produced it, so the caller can refuse a mismatch. */
export function parseEmbedResponse(raw: unknown, expectedCount: number, dimensions: number): { vectors: number[][]; model: unknown } {
  return { vectors: parseEmbeddings(raw, expectedCount, dimensions), model: asObject(raw).model };
}

/** Validate `{ results: [{ index, score }] }` and return scores aligned to the request order. */
export function parseRerankResults(raw: unknown, expectedCount: number): number[] {
  const results = asObject(raw).results;
  if (!Array.isArray(results)) fail("results is not an array");
  if (results.length !== expectedCount) fail("result count does not match the request");
  const scores = new Array<number | undefined>(expectedCount).fill(undefined);
  for (const item of results as unknown[]) {
    const { index, score } = asObject(item);
    if (typeof index !== "number" || !Number.isInteger(index) || index < 0 || index >= expectedCount) fail("a result index is out of range");
    if (scores[index] !== undefined) fail("a result index is repeated");
    scores[index] = checkedScore(score);
  }
  return scores as number[];
}

/** A score is a probability. Applied to every transport's output, not just the worker's. */
export function checkedScore(score: unknown): number {
  if (typeof score !== "number" || !Number.isFinite(score) || score < 0 || score > 1) fail("a score is outside 0 to 1");
  return score;
}

/** One token per input byte is the most any tokenizer can produce, plus specials. */
export function parseTokens(raw: unknown, text: string): number[] {
  const tokens = asObject(raw).tokens;
  if (!Array.isArray(tokens)) fail("tokens is not an array");
  if (tokens.length > Buffer.byteLength(text, "utf8") + 16) fail("more tokens than the text can produce");
  for (const token of tokens as unknown[]) {
    if (typeof token !== "number" || !Number.isSafeInteger(token) || token < 0) fail("a token is not a non-negative integer");
  }
  return tokens as number[];
}

export function parseText(raw: unknown, maxLength: number): string {
  const text = asObject(raw).text;
  if (typeof text !== "string") fail("text is not a string");
  if (text.length > maxLength) fail("text is longer than the limit");
  return text;
}

export interface WorkerCapabilities {
  operations: string[];
  /** Model file names by role. */
  models: { embed: string | null; rerank: string | null; generate: string | null };
  rerankTransport: string | null;
}

export function parseCapabilities(raw: unknown): WorkerCapabilities {
  const body = asObject(raw);
  if (!Array.isArray(body.operations) || !body.operations.every((o) => typeof o === "string")) fail("operations is not a list of strings");
  const models = asObject(body.models);
  const name = (v: unknown): string | null => (typeof v === "string" ? v : null);
  return {
    operations: body.operations as string[],
    models: { embed: name(models.embed), rerank: name(models.rerank), generate: name(models.generate) },
    rerankTransport: name(body.rerankTransport),
  };
}

export interface WorkerHealth {
  status: "ok" | "busy" | "starting";
  models: { embed: boolean; rerank: boolean; generate: boolean };
  queueDepth: number;
}

/** Routing reads these, so each is coerced to something safe. Anything else in the body is ignored. */
export function parseHealth(raw: unknown): WorkerHealth {
  const body = asObject(raw);
  const models = asObject(body.models);
  const loaded = (v: unknown): boolean => typeof v === "object" && v !== null && (v as { loaded?: unknown }).loaded === true;
  const depth = body.queueDepth;
  return {
    status: body.status === "busy" || body.status === "starting" ? body.status : "ok",
    models: { embed: loaded(models.embed), rerank: loaded(models.rerank), generate: loaded(models.generate) },
    // A worker cannot attract traffic by reporting a negative or absurd depth.
    queueDepth: typeof depth === "number" && Number.isFinite(depth) && depth > 0 ? depth : 0,
  };
}
