import assert from "node:assert/strict";
import { test } from "node:test";
import { JobCancelled, JobDeferred } from "../src/jobs.ts";
import { WorkerError } from "../src/llm/client.ts";
import { IndexBusy, runEmbedFarm, type FarmDeps, type FarmOptions } from "../src/llm/embed-farm.ts";

/** Documents here are lists of chunk texts. The vector store is a map, so results can be inspected. */
interface World {
  docs: Map<string, string[]>;
  /** hash -> the chunks last written for it, and when. */
  written: Map<string, { chunks: number[] }>;
  deadLetters: string[];
  orphanSweeps: number;
  batches: string[][];
  deps: FarmDeps;
  resets: number;
  sleeps: number[];
  inFlight: { now: number; max: number };
}

function world(docs: Record<string, string[]>, over: Partial<FarmDeps> = {}): World {
  const w: World = { docs: new Map(Object.entries(docs)), written: new Map(), deadLetters: [], orphanSweeps: 0, batches: [], resets: 0, sleeps: [], inFlight: { now: 0, max: 0 }, deps: undefined as never };
  w.deps = {
    canEmbed: () => true,
    confirmWorkers: async () => true,
    capacity: () => 2,
    // Pending means it has no vectors yet, or a force job has not redone it.
    pendingDocs: (_c, redoBefore) => [...w.docs.keys()].filter((hash) => redoBefore !== undefined || !w.written.has(hash)).map((hash) => ({ hash, path: `${hash}.md`, bytes: 1 })),
    readBody: (hash) => w.docs.get(hash)!.join("\n"),
    chunk: async (body) => body.split("\n").map((text, i) => ({ text, pos: i })),
    format: (text) => text,
    vectorDimensions: () => null,
    resetVectors: () => {
      w.resets++;
      w.written.clear();
    },
    replaceDocument: (hash, chunks) => void w.written.set(hash, { chunks: chunks.map((c) => c.seq).sort((a, b) => a - b) }),
    deadLetter: (hash) => void w.deadLetters.push(hash),
    removeOrphans: () => void w.orphanSweeps++,
    embed: async (texts) => {
      w.batches.push(texts);
      w.inFlight.now++;
      w.inFlight.max = Math.max(w.inFlight.max, w.inFlight.now);
      await new Promise((r) => setImmediate(r));
      w.inFlight.now--;
      return { vectors: texts.map(() => [1, 2]), worker: "a" };
    },
    sleep: async (ms) => void w.sleeps.push(ms),
    ...over,
  };
  return w;
}

const opts = (over: Partial<FarmOptions> = {}): FarmOptions => ({ force: false, collection: undefined, signal: new AbortController().signal, onWorker: () => {}, maxBatchBytes: 1024, maxBatchChunks: 32, ...over });

const unavailable = () => new WorkerError("unavailable", "embed", "a", "HTTP 503", 503);
const refused = () => new WorkerError("rejected", "embed", "a", "HTTP 400", 400);
const vectors = (texts: string[], worker = "a") => ({ vectors: texts.map(() => [1, 2]), worker });

// --- the normal path -------------------------------------------------------------

test("every chunk of every pending document is embedded and written", async () => {
  const w = world({ a: ["a0", "a1"], b: ["b0"] });
  const workers: string[] = [];
  const out = await runEmbedFarm(w.deps, opts({ onWorker: (n) => workers.push(n) }));
  assert.equal(out.docsProcessed, 2);
  assert.equal(out.chunksEmbedded, 3);
  assert.equal(out.errors, 0);
  assert.equal(out.docsPending, 0);
  assert.deepEqual(w.written.get("a")?.chunks, [0, 1]);
  assert.ok(workers.includes("a"));
});

test("nothing pending does nothing, and does not defer even with no worker", async () => {
  const w = world({}, { canEmbed: () => false });
  assert.deepEqual(await runEmbedFarm(w.deps, opts()), { docsProcessed: 0, chunksEmbedded: 0, errors: 0, durationMs: 0, docsPending: 0 });
  assert.equal(w.batches.length, 0);
});

test("chunks group by byte budget, and one oversized chunk still goes alone", async () => {
  const w = world({ a: ["x".repeat(60), "x".repeat(60), "x".repeat(300), "y"] });
  await runEmbedFarm(w.deps, opts({ maxBatchBytes: 100 }));
  assert.deepEqual(w.batches.map((b) => b.map((t) => t.length)), [[60], [60], [300], [1]]);
});

test("chunks also group by count", async () => {
  const w = world({ a: ["1", "2", "3", "4", "5"] });
  await runEmbedFarm(w.deps, opts({ maxBatchChunks: 2 }));
  assert.deepEqual(w.batches.map((b) => b.length), [2, 2, 1]);
});

test("batches run concurrently up to capacity and no further", async () => {
  const docs: Record<string, string[]> = {};
  for (let i = 0; i < 12; i++) docs[`d${i}`] = [`t${i}`];
  const w = world(docs, { capacity: () => 3 });
  await runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 }));
  assert.equal(w.inFlight.max, 3);
  assert.equal(w.written.size, 12);
});

test("empty documents are skipped, as QMD skips them, and are not left over", async () => {
  const w = world({ a: ["   "], b: ["b0"] });
  const out = await runEmbedFarm(w.deps, opts());
  assert.equal(out.chunksEmbedded, 1);
  assert.equal(out.docsPending, 0);
  assert.deepEqual(w.batches, [["b0"]]);
});

test("a document reports progress when it reaches the index, and names every worker it used", async () => {
  let seen = 0;
  const workers: string[] = [];
  const w = world({ a: ["a0", "a1"] }, { capacity: () => 1, embed: async (texts) => vectors(texts, texts[0] === "a0" ? "first" : "second") });
  await runEmbedFarm(w.deps, opts({ maxBatchChunks: 1, onProgress: () => seen++, onWorker: (n) => workers.push(n) }));
  assert.equal(seen, 1);
  assert.deepEqual(workers, ["first", "second"]);
});

// --- one bad chunk (a batch must not stay stuck) -------------------------------------

test("one chunk the worker refuses costs its own document, not the whole batch", async () => {
  const docs: Record<string, string[]> = {};
  for (let i = 0; i < 12; i++) docs[`d${i}`] = [`text ${i}`];
  docs.d5 = ["POISON"];
  const w = world(docs, {
    embed: async (texts) => {
      if (texts.includes("POISON")) throw refused();
      return vectors(texts);
    },
  });
  const out = await runEmbedFarm(w.deps, opts());
  assert.equal(w.written.size, 11, "the other eleven are embedded");
  assert.ok(!w.written.has("d5"));
  assert.equal(out.docsPending, 1);
  assert.equal(out.errors, 1);
  assert.equal(out.chunksEmbedded, 11);
});

test("a refusal is split at once, without waiting on a backoff", async () => {
  const w = world({ a: ["ok", "POISON"] }, {
    embed: async (texts) => {
      if (texts.includes("POISON")) throw refused();
      return vectors(texts);
    },
  });
  await runEmbedFarm(w.deps, opts());
  assert.deepEqual(w.sleeps, []);
});

test("a document with one refused chunk writes nothing, and its other chunks are not kept", async () => {
  const w = world({ a: ["a0", "POISON", "a2"] }, {
    capacity: () => 1,
    embed: async (texts) => {
      if (texts.includes("POISON")) throw refused();
      return vectors(texts);
    },
  });
  const out = await runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 }));
  assert.equal(w.written.size, 0);
  assert.equal(out.chunksEmbedded, 0);
  assert.equal(out.docsPending, 1);
});

test("a brief failure recovers within the run, on a single worker", async () => {
  let failures = 2;
  const w = world({ a: ["a0"], b: ["b0"] }, {
    capacity: () => 1,
    embed: async (texts) => {
      if (failures-- > 0) throw unavailable();
      return vectors(texts);
    },
  });
  const out = await runEmbedFarm(w.deps, opts());
  assert.equal(out.docsPending, 0);
  assert.equal(out.errors, 0);
  assert.equal(w.written.size, 2);
  assert.deepEqual(w.sleeps, [500, 1000], "backing off");
});

test("a chunk that keeps failing with workers up ends up isolated and counted", async () => {
  const w = world({ a: ["a0"], b: ["BAD"] }, {
    capacity: () => 1,
    embed: async (texts) => {
      if (texts.includes("BAD")) throw unavailable();
      return vectors(texts);
    },
  });
  const out = await runEmbedFarm(w.deps, opts());
  assert.deepEqual([...w.written.keys()], ["a"]);
  assert.equal(out.docsPending, 1);
  assert.equal(out.errors, 1);
  assert.deepEqual(w.deadLetters, ["b"]);
  assert.equal(out.docsDeadLettered, 1);
});

test("a 502 chunk is isolated by the split even though it would open the circuit", async () => {
  // The real pool stops answering once the circuit opens, until a known-good call clears it.
  let open = false;
  let failures = 0;
  const w = world({ a: ["a0", "BAD", "a2"], b: ["b0"], c: ["c0"] }, {
    capacity: () => 1,
    confirmWorkers: async () => {
      open = false;
      failures = 0;
      return true;
    },
    embed: async (texts) => {
      if (open) throw new WorkerError("no_worker", "embed", "pool", "circuits open");
      if (texts.includes("BAD")) {
        if (++failures >= 3) open = true;
        throw unavailable();
      }
      return vectors(texts);
    },
  });
  const out = await runEmbedFarm(w.deps, opts());
  assert.deepEqual([...w.written.keys()].sort(), ["b", "c"]);
  assert.deepEqual(w.deadLetters, ["a"], "recorded, and only the document at fault");
  assert.equal(out.docsPending, 1);
});

test("one batch's failures are not an outage until a known-good call says so", async () => {
  let confirmed = 0;
  const w = world({ a: ["a0"], b: ["b0"] }, {
    capacity: () => 1,
    canEmbed: () => confirmed === 0,
    confirmWorkers: async () => {
      confirmed++;
      return true;
    },
    embed: async (texts) => {
      if (texts.includes("a0")) throw unavailable();
      return vectors(texts);
    },
  });
  const out = await runEmbedFarm(w.deps, opts());
  assert.ok(confirmed > 0);
  assert.deepEqual([...w.written.keys()], ["b"], "the circuit reading closed or open made no difference");
  assert.equal(out.docsDeadLettered, 1);
});

test("an open circuit left by the last job does not stop the next one, if workers answer", async () => {
  const w = world({ a: ["a0"] }, { canEmbed: () => false, confirmWorkers: async () => true });
  assert.equal((await runEmbedFarm(w.deps, opts())).docsPending, 0);
  assert.equal(w.written.size, 1);
});

test("a call no worker saw never condemns a document: it stays pending", async () => {
  const w = world({ a: ["a0"] }, { embed: async () => { throw new WorkerError("no_worker", "embed", "pool", "circuits open"); } });
  const out = await runEmbedFarm(w.deps, opts());
  assert.deepEqual(w.deadLetters, []);
  assert.equal(out.docsPending, 1);
  assert.equal(out.docsDeadLettered, 0);
});

test("a lone chunk gets its brief-failure retries again, so a blip after a split costs nothing", async () => {
  let failures = 0;
  const w = world({ a: ["a0", "a1"] }, {
    capacity: () => 1,
    embed: async (texts) => {
      // The batch fails three times, then the first lone chunk blips once.
      if (texts.length === 2 || (texts[0] === "a0" && failures++ === 0)) throw unavailable();
      return vectors(texts);
    },
  });
  const out = await runEmbedFarm(w.deps, opts());
  assert.deepEqual(w.deadLetters, []);
  assert.equal(out.docsPending, 0);
});

test("a document with several failing chunks is recorded once", async () => {
  const w = world({ a: ["BAD1", "BAD2"] }, { embed: async () => { throw refused(); } });
  const out = await runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 }));
  assert.deepEqual(w.deadLetters, ["a"]);
  assert.equal(out.docsDeadLettered, 1);
});

test("a tokenizer refusal, with workers up, sets the document aside too", async () => {
  const w = world({ a: ["a0"] }, { chunk: async () => { throw refused(); } });
  assert.equal((await runEmbedFarm(w.deps, opts())).docsDeadLettered, 1);
  assert.deepEqual(w.deadLetters, ["a"]);
});

// --- the index held by someone else ---------------------------------------------------

for (const [where, over] of [
  ["a document write", { replaceDocument: () => { throw new IndexBusy(); } }],
  ["a dead letter", { deadLetter: () => { throw new IndexBusy(); }, embed: async () => { throw refused(); } }],
  ["listing what is pending", { pendingDocs: () => { throw new IndexBusy(); } }],
  ["removing old rows", { removeOrphans: () => { throw new IndexBusy(); } }],
] as Array<[string, Partial<FarmDeps>]>) {
  test(`a busy index during ${where} defers the job rather than failing it`, async () => {
    const w = world({ a: ["a0"], b: ["b0"] }, { capacity: () => 1, ...over });
    await assert.rejects(runEmbedFarm(w.deps, opts({ force: true, maxBatchChunks: 1 })), (e) => e instanceof JobDeferred && /busy/.test(e.message));
  });
}

test("a busy index stops the run: nothing more is asked of the workers", async () => {
  const w = world({ a: ["a0"], b: ["b0"], c: ["c0"] }, { capacity: () => 1, replaceDocument: () => { throw new IndexBusy(); } });
  await assert.rejects(runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 })), JobDeferred);
  assert.ok(w.batches.length <= 2, String(w.batches.length));
});

// --- old rows after a force --------------------------------------------------------------

test("only a finished force job over everything removes rows of documents gone from the index", async () => {
  const sweeps = async (options: Partial<FarmOptions>, over: Partial<FarmDeps> = {}) => {
    const w = world({ a: ["a0"] }, over);
    await runEmbedFarm(w.deps, opts(options));
    return w.orphanSweeps;
  };
  assert.equal(await sweeps({ force: true }), 1);
  assert.equal(await sweeps({ force: true }, { pendingDocs: () => [] }), 1, "with nothing to redo as well");
  assert.equal(await sweeps({ force: false }), 0);
  assert.equal(await sweeps({ force: true, collection: "one" }), 0, "it has not seen the rest");
  assert.equal(await sweeps({ force: true }, { embed: async () => { throw refused(); } }), 0, "something is unfinished");
});

// --- atomic documents ---------------------------------------------------------------

test("a re-run finishes only what is left", async () => {
  let fail = true;
  const w = world({ a: ["a0", "a1"], b: ["b0"] }, {
    capacity: () => 1,
    embed: async (texts) => {
      if (fail && texts.includes("a1")) throw refused();
      return vectors(texts);
    },
  });
  const first = await runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 }));
  assert.equal(first.docsPending, 1);
  fail = false;
  const second = await runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 }));
  assert.equal(second.docsProcessed, 1, "only what was left");
  assert.equal(second.docsPending, 0);
  assert.equal(w.written.size, 2);
});

// --- outages ------------------------------------------------------------------------

test("with work to do and no worker it defers before touching anything", async () => {
  const w = world({ a: ["a0"] }, { canEmbed: () => false, confirmWorkers: async () => false });
  await assert.rejects(runEmbedFarm(w.deps, opts({ force: true })), JobDeferred);
  assert.equal(w.resets, 0);
  assert.equal(w.batches.length, 0);
});

test("losing every worker mid-run defers the job, and what finished stands", async () => {
  let up = true;
  const w = world({ a: ["a0"], b: ["b0"], c: ["c0"] }, {
    capacity: () => 1,
    confirmWorkers: async () => up,
    embed: async (texts) => {
      if (texts.includes("b0")) {
        up = false;
        throw unavailable();
      }
      return vectors(texts);
    },
  });
  await assert.rejects(runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 })), JobDeferred);
  assert.deepEqual([...w.written.keys()], ["a"]);
  assert.deepEqual(w.deadLetters, [], "an outage condemns nothing");
});

test("a tokenizer outage during chunking also defers", async () => {
  let up = true;
  const w = world({ a: ["a0"] }, {
    confirmWorkers: async () => up,
    chunk: async () => {
      up = false;
      throw unavailable();
    },
  });
  await assert.rejects(runEmbedFarm(w.deps, opts()), JobDeferred);
});

test("one document's tokenizer failure does not stop the rest while workers remain", async () => {
  const w = world({ a: ["a0"], b: ["b0"] });
  const inner = w.deps.chunk;
  const deps: FarmDeps = { ...w.deps, chunk: async (body, path, signal) => (path === "a.md" ? Promise.reject(unavailable()) : inner(body, path, signal)) };
  const out = await runEmbedFarm(deps, opts());
  assert.equal(out.errors, 1);
  assert.equal(out.docsPending, 1);
  assert.deepEqual([...w.written.keys()], ["b"]);
});

test("cancelling stops new batches", async () => {
  const abort = new AbortController();
  const w = world({ a: ["a0"], b: ["b0"], c: ["c0"] }, {
    capacity: () => 1,
    embed: async (texts) => {
      abort.abort();
      return vectors(texts);
    },
  });
  await assert.rejects(runEmbedFarm(w.deps, opts({ signal: abort.signal, maxBatchChunks: 1 })), JobCancelled);
  assert.ok(w.written.size <= 1);
});

test("a local error is not mistaken for a worker failure", async () => {
  const w = world({ a: ["a0"] }, { replaceDocument: () => { throw new Error("disk full"); } });
  await assert.rejects(runEmbedFarm(w.deps, opts()), /disk full/);
});

test("no lane writes after the job has ended, even when another lane fails", async () => {
  let openSlow = () => {};
  const slowGate = new Promise<void>((r) => (openSlow = r));
  let writesAfterEnd = 0;
  let ended = false;
  const w = world({ bad: ["BAD"], slow: ["SLOW"] }, {
    capacity: () => 2,
    embed: async (texts) => {
      if (texts.includes("SLOW")) await slowGate;
      return vectors(texts);
    },
    replaceDocument: (hash) => {
      if (ended) writesAfterEnd++;
      if (hash === "bad") {
        setImmediate(openSlow);
        throw new Error("disk full");
      }
    },
  });
  await assert.rejects(runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 })), /disk full/);
  ended = true;
  await new Promise((r) => setTimeout(r, 20));
  assert.equal(writesAfterEnd, 0);
});

// --- force --------------------------------------------------------------------------

test("force clears nothing: old vectors stand until each document's new set is ready", async () => {
  const w = world({ a: ["a0"], b: ["b0"] }, { vectorDimensions: () => 2 });
  w.written.set("a", { chunks: [99] });
  w.written.set("b", { chunks: [99] });
  let up = true;
  const deps: FarmDeps = {
    ...w.deps,
    capacity: () => 1,
    confirmWorkers: async () => up,
    embed: async (texts) => {
      if (texts.includes("b0")) {
        up = false;
        throw unavailable();
      }
      return vectors(texts);
    },
  };
  await assert.rejects(runEmbedFarm(deps, opts({ force: true, maxBatchChunks: 1 })), JobDeferred);
  assert.equal(w.resets, 0);
  assert.deepEqual(w.written.get("a")?.chunks, [0], "redone");
  assert.deepEqual(w.written.get("b")?.chunks, [99], "untouched by the outage");
});

test("a force job resumes from when it was accepted", async () => {
  const seen: Array<string | undefined> = [];
  const w = world({ a: ["a0"] });
  const deps: FarmDeps = {
    ...w.deps,
    pendingDocs: (collection, redoBefore) => {
      seen.push(redoBefore);
      return w.deps.pendingDocs(collection, redoBefore);
    },
  };
  await runEmbedFarm(deps, opts({ force: true, forceSince: "2026-11-04T00:00:00.000Z" }));
  await runEmbedFarm(deps, opts());
  assert.equal(seen[0], "2026-11-04T00:00:00.000Z");
  assert.equal(seen[1], undefined);
});

test("a new vector length replaces the old ones only for a force job, and only with a real vector in hand", async () => {
  const forced = world({ a: ["a0"], b: ["b0"] }, { vectorDimensions: () => 768 });
  const out = await runEmbedFarm(forced.deps, opts({ force: true, maxBatchChunks: 1 }));
  assert.equal(forced.resets, 1, "once");
  assert.equal(out.docsPending, 0);

  const plain = world({ a: ["a0"] }, { vectorDimensions: () => 768 });
  await assert.rejects(runEmbedFarm(plain.deps, opts()), /length 2 .* holds 768.*force embed with no collection/);
  assert.equal(plain.resets, 0);
  assert.equal(plain.written.size, 0);

  // One collection's force job would cost every other collection its vectors.
  const scoped = world({ a: ["a0"] }, { vectorDimensions: () => 768 });
  await assert.rejects(runEmbedFarm(scoped.deps, opts({ force: true, collection: "one" })), /Nothing was changed/);
  assert.equal(scoped.resets, 0);
  assert.equal(scoped.written.size, 0);

  const down = world({ a: ["a0"] }, { vectorDimensions: () => 768, embed: async () => { throw refused(); } });
  await runEmbedFarm(down.deps, opts({ force: true }));
  assert.equal(down.resets, 0, "no vector in hand, so nothing is dropped");
});
