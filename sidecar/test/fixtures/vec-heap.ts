/**
 * Run as its own process, with a small heap. One large document with many chunks matches a
 * vector search. The SDK used to read the whole body once per matching chunk, so this ran
 * out of heap and killed the process. Uses no model: the vectors are written directly.
 *
 * argv: <dbPath>. Writes one JSON line to stdout.
 */
const qmd = await import("@tobilu/qmd");
const store = await qmd.createStore({ dbPath: process.argv[2]! });
const internal = store.internal as unknown as {
  db: { prepare(sql: string): { get(): { hash: string } } };
  ensureVecTable(dimensions: number): void;
  insertEmbedding(hash: string, seq: number, pos: number, embedding: Float32Array, model: string, at: string, total: number, fingerprint: string): void;
  searchVec(query: string, model: string, limit: number, collection: undefined, session: undefined, embedding: number[]): Promise<Array<{ displayPath: string; body?: string }>>;
};
const { hash } = internal.db.prepare("SELECT hash FROM documents LIMIT 1").get();
internal.ensureVecTable(8);
const vector = [1, 2, 3, 4, 5, 6, 7, 8];
for (let seq = 0; seq < 60; seq++) internal.insertEmbedding(hash, seq, seq * 100, new Float32Array(vector), "m", new Date().toISOString(), 60, "fp");
const hits = await internal.searchVec("q", "m", 20, undefined, undefined, vector);
console.log(JSON.stringify({ hits: hits.length, bodyLength: hits[0]?.body?.length ?? 0 }));
await store.close();
process.exit(0);
