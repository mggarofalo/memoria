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
    acceptsFullChunk: async () => true,
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
    setAsideCount: () => w.deadLetters.length,
    documentCount: () => w.docs.size,
    recentFailures: new Map(),
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
  assert.deepEqual(w.deadLetters, [], "a 503 says nothing lasting about the document");
  assert.equal(out.docsDeadLettered, 0);
});

test("a 502 chunk is isolated by the split even though it would open the circuit, and stays pending", async () => {
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
  assert.deepEqual(w.deadLetters, []);
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
  assert.equal(out.docsPending, 1);
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

test("a force job over everything removes rows of documents gone from the index, however it ends", async () => {
  const sweeps = async (options: Partial<FarmOptions>, over: Partial<FarmDeps> = {}) => {
    const w = world({ a: ["a0"] }, over);
    await runEmbedFarm(w.deps, opts(options));
    return w.orphanSweeps;
  };
  // A job that waits for a worker has swept already, so a restored document starts clean.
  const waiting = world({ a: ["a0"] }, { canEmbed: () => false, confirmWorkers: async () => false });
  await assert.rejects(runEmbedFarm(waiting.deps, opts({ force: true })), JobDeferred);
  assert.equal(waiting.orphanSweeps, 1);
  assert.equal(await sweeps({ force: true }), 1);
  assert.equal(await sweeps({ force: true }, { pendingDocs: () => [] }), 1, "with nothing to redo as well");
  assert.equal(await sweeps({ force: false }), 0);
  assert.equal(await sweeps({ force: true, collection: "one" }), 0, "it has not seen the rest");
  assert.equal(await sweeps({ force: true }, { embed: async () => { throw refused(); } }), 1, "even when something stays unfinished");
  await assert.rejects(sweeps({ force: true }, { canEmbed: () => false, confirmWorkers: async () => false }), JobDeferred);
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

// --- what may set a document aside: the reviewer's scenarios --------------------------------

/** Many one-chunk documents and a clock that only moves when the farm spends time. */
function timed(count: number, failure: (now: number, texts: string[]) => WorkerError | null) {
  const docs: Record<string, string[]> = {};
  for (let i = 0; i < count; i++) docs[`d${String(i).padStart(4, "0")}`] = [`real text ${i}`];
  const clock = { now: 0, calls: 0 };
  const w = world(docs, {
    capacity: () => 2,
    sleep: async (ms) => void (clock.now += ms),
    embed: async (texts) => {
      clock.now += 50;
      clock.calls++;
      const err = failure(clock.now, texts);
      if (err) throw err;
      return vectors(texts);
    },
  });
  return { w, clock };
}

/** Run as the queue would: a deferred job tries again after a doubling wait, a finished one is done. */
async function untilSettled(w: World, clock: { now: number }, runs = 30): Promise<{ deferrals: number; docsPending: number | undefined }> {
  let deferrals = 0;
  for (let run = 0; run < runs; run++) {
    try {
      const out = await runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 }));
      return { deferrals, docsPending: out.docsPending };
    } catch (err) {
      if (!(err instanceof JobDeferred)) throw err;
      clock.now += Math.min(30_000 * 2 ** deferrals, 300_000);
      deferrals++;
    }
  }
  throw new Error("never settled");
}

const bad = (status: number) => new WorkerError(status === 400 ? "rejected" : "unavailable", "embed", "a", `HTTP ${status}`, status);

for (const windowMs of [5_000, 30_000]) {
  test(`a ${windowMs / 1000} s window of 502s on real text sets nothing aside, and every document embeds once it heals`, async () => {
    const { w, clock } = timed(100, (now) => (now < windowMs ? bad(502) : null));
    const out = await untilSettled(w, clock);
    assert.deepEqual(w.deadLetters, []);
    // A job that ends partial is picked up by the next one: nothing is lost either way.
    if (out.docsPending) assert.equal((await runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 }))).docsPending, 0);
    assert.equal(w.written.size, 100);
  });
}

test("502 on all real text: the job waits, stops early, and sets nothing aside", async () => {
  const { w, clock } = timed(200, () => bad(502));
  await assert.rejects(runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 })), (e) => e instanceof JobDeferred && /keep failing on real documents/.test(e.message));
  assert.deepEqual(w.deadLetters, []);
  assert.ok(clock.calls < 60, `${clock.calls} calls`);
});

test("400 on all real text, as from too small a context: the job waits and condemns nothing", async () => {
  const { w, clock } = timed(200, () => bad(400));
  await assert.rejects(runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 })), (e) => e instanceof JobDeferred && /refused too many/.test(e.message));
  assert.deepEqual(w.deadLetters, []);
  assert.equal(w.written.size, 0);
  assert.ok(clock.calls < 80, `stops once the verdict is in, after ${clock.calls} calls`);
});

test("a run cut short records no refusals: only a finished run can say how many there were", async () => {
  const abort = new AbortController();
  const w = world({ a: ["BAD"], b: ["b0"] }, {
    capacity: () => 1,
    embed: async (texts) => {
      if (texts.includes("BAD")) throw refused();
      abort.abort();
      return vectors(texts);
    },
  });
  await assert.rejects(runEmbedFarm(w.deps, opts({ signal: abort.signal, maxBatchChunks: 1 })), JobCancelled);
  assert.deepEqual(w.deadLetters, []);
});

test("one slow chunk stays pending, costs only itself, and is tried again", async () => {
  const timeout = new WorkerError("timeout", "embed", "a", "no response before the deadline");
  let slow = true;
  const { w } = timed(10, (_, texts) => (slow && texts.includes("real text 3") ? timeout : null));
  const first = await runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 }));
  assert.equal(first.docsPending, 1);
  assert.deepEqual(w.deadLetters, []);
  assert.equal(w.written.size, 9);
  slow = false;
  assert.equal((await runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 }))).docsPending, 0);
  assert.equal(w.written.size, 10);
});

test("documents that keep failing cannot starve the ones behind them", async () => {
  const failing = new Set(["real text 0", "real text 1", "real text 2", "real text 3", "real text 4", "real text 5"]);
  const { w, clock } = timed(20, (_, texts) => (texts.some((t) => failing.has(t)) ? bad(502) : null));
  w.deps.capacity = () => 1;
  await untilSettled(w, clock);
  assert.equal(w.written.size, 14, "everything else got its turn");
  assert.deepEqual(w.deadLetters, []);
});

test("a few real refusals are set aside, once the run is over", async () => {
  const seenWhenWritten: number[] = [];
  const { w } = timed(10, (_, texts) => (texts.includes("real text 3") || texts.includes("real text 7") ? bad(400) : null));
  const original = w.deps.deadLetter;
  w.deps.deadLetter = (...args) => {
    seenWhenWritten.push(w.written.size);
    original(...args);
  };
  const out = await runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 }));
  assert.deepEqual(w.deadLetters.sort(), ["d0003", "d0007"]);
  assert.equal(out.docsDeadLettered, 2);
  assert.deepEqual(seenWhenWritten, [8, 8], "after the run finished");
});

test("a worker refusing a full-size text is too small: its refusals condemn nothing, and the error says so", async () => {
  const { w } = timed(10, (_, texts) => (texts.includes("real text 3") ? bad(400) : null));
  w.deps.acceptsFullChunk = async () => false;
  await assert.rejects(runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 })), (e) => e instanceof JobDeferred && /full-size test text/.test(e.message));
  assert.deepEqual(w.deadLetters, []);
  assert.equal(w.written.size, 9, "the rest still embed");
});

test("the full-size check is made only when something failed", async () => {
  let asked = 0;
  const w = world({ a: ["a0"] }, {
    acceptsFullChunk: async () => {
      asked++;
      return true;
    },
  });
  await runEmbedFarm(w.deps, opts());
  assert.equal(asked, 0);
});

test("refusals are not capped when the worker takes a full-size text and most documents embed", async () => {
  const poison = new Set(["real text 3", "real text 4", "real text 5", "real text 6", "real text 70", "real text 71", "real text 72"]);
  const { w } = timed(100, (_, texts) => (texts.some((t) => poison.has(t)) ? bad(400) : null));
  const out = await runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 }));
  assert.equal(out.docsDeadLettered, 7);
  assert.equal(w.written.size, 93);
});

test("refusals that come first wait a run, then go last, and are believed once the rest embed", async () => {
  const { w, clock } = timed(20, (_, texts) => (texts.some((t) => /^real text [0-5]$/.test(t)) ? bad(400) : null));
  w.deps.capacity = () => 1;
  await untilSettled(w, clock);
  assert.equal(w.written.size, 14);
  assert.equal(w.deadLetters.length, 6);
});

test("what has embedded already counts: refusals in a run of only those documents are believed", async () => {
  const { w } = timed(5, () => bad(400));
  w.deps.documentCount = () => 100;
  assert.equal((await runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 }))).docsDeadLettered, 5);
});

test("documents already set aside do not count as having embedded", async () => {
  const { w } = timed(5, () => bad(400));
  w.deps.documentCount = () => 10;
  w.deps.setAsideCount = () => 5;
  await assert.rejects(runEmbedFarm(w.deps, opts({ maxBatchChunks: 1 })), /refused too many/);
});

const repeatedly = (over: Partial<FarmDeps> = {}) => {
  let now = 1_000_000_000;
  const w = world({ a: ["BAD"], b: ["b0"] }, { capacity: () => 1, embed: async (texts) => { if (texts.includes("BAD")) throw unavailable(); return vectors(texts); }, ...over });
  return { w, tick: (ms: number) => (now += ms), run: () => runEmbedFarm(w.deps, opts({ maxBatchChunks: 1, now: () => now })) };
};
const GAP = 10 * 60_000;

test("a 5xx document is set aside after three runs well apart, and not sooner", async () => {
  const { w, tick, run } = repeatedly();
  assert.equal((await run()).docsPending, 1);
  tick(GAP);
  await run();
  assert.deepEqual(w.deadLetters, [], "two runs");
  tick(GAP);
  const third = await run();
  assert.deepEqual(w.deadLetters, ["a"]);
  assert.equal(third.docsDeadLettered, 1);
});

test("runs close together count once, so a brief window cannot set a document aside", async () => {
  const { w, tick, run } = repeatedly();
  for (let i = 0; i < 6; i++) {
    await run();
    tick(GAP / 10);
  }
  assert.deepEqual(w.deadLetters, []);
});

test("a run where the worker fails the full-size text does not count", async () => {
  const { w, tick, run } = repeatedly({ acceptsFullChunk: async () => false });
  for (let i = 0; i < 5; i++) {
    await run();
    tick(GAP);
  }
  assert.deepEqual(w.deadLetters, []);
});

test("a success wipes the count", async () => {
  let failing = true;
  const { w, tick, run } = repeatedly({ embed: async (texts) => { if (failing && texts.includes("BAD")) throw unavailable(); return vectors(texts); } });
  await run();
  tick(GAP);
  await run();
  failing = false;
  await run();
  assert.equal(w.deps.recentFailures.size, 0);
});
