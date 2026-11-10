# Inference workers

The sidecar can send its model work to another machine. QMD still owns retrieval on the node that holds the index: full-text search, vector lookup, fusion and caching all stay in the sidecar's process. Only the three model operations move: embedding, reranking and query expansion.

**Status: run on one real GPU.** The worker and the parity benchmark have run on a Windows desktop with an RTX 5080, using real models and a real index. The Mac, the services and the sidecar's API on top have not; see [What has and has not been verified](#what-has-and-has-not-been-verified). `MEMORIA_QMD_LLM=local` remains the default, and local mode loads none of this code.

**The reranker transport is decided:** llama-server, given the exact tokens in-process QMD would score. [The decision](#the-decision) has the numbers.

```
Memoria.Api
    │
memoria-qmd sidecar ── index.sqlite, full-text and vector search, fusion, llm_cache
    │  RemoteLlm, assigned to store.internal.llm
    │  HTTP + bearer token, LAN only
    ▼
memoria-worker ── auth, validation, truncation, health
    │  HTTP, loopback only
    ▼
llama-server × 3 ── embeddinggemma-300M, Qwen3-Reranker-0.6B, qmd-query-expansion-1.7B
```

## Turning it on

On the sidecar:

| Variable | Default | Purpose |
|---|---|---|
| `MEMORIA_QMD_LLM` | `local` | `remote` sends model work to a worker. |
| `MEMORIA_QMD_CONFIG_FILE` | unset | A JSON file holding the pool and the queue's tuning. See [The configuration file](#the-configuration-file). Read only in remote mode. |
| `MEMORIA_QMD_WORKERS` | unset | The pool, as a JSON list: the same list as the file's `workers`. |
| `MEMORIA_QMD_WORKER_URL` | unset | Shorthand for a pool of one. Must not contain credentials. Set the pool in one place: the file, this list or this URL. |
| `MEMORIA_QMD_WORKER_TOKEN` | unset | The token for `MEMORIA_QMD_WORKER_URL`. Required unless the worker is on loopback. At least 16 characters. `MEMORIA_QMD_WORKER_TOKEN_FILE` reads it from a file instead, which is what a service should use. |
| `MEMORIA_QMD_WORKER_TOKEN_FILE` | unset | Reads `MEMORIA_QMD_WORKER_TOKEN` from this file. Setting both is an error. |
| `MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK` | `0` | Required for a plain-http worker URL that is not loopback, such as the example below. Set it only on an encrypted network such as a WireGuard tunnel. |
| `MEMORIA_QMD_WORKER_POLL_MS` | `10000` | How often each worker's `/health` and `/capabilities` are read. |
| `MEMORIA_QMD_LLM_FALLBACK` | `1` | Tokenize and embed queries on this host's CPU when no worker answers. Needs the embedding model in QMD's cache on this host, under the name QMD gives it (`hf_<owner>_<file>`). It never downloads one, and a call gives up after 30 s. |
| `MEMORIA_QMD_EMBED_DIMENSIONS` | `768` | Every embedding must have this length. `0` pins it to the first response. |
| `MEMORIA_QMD_EMBED_BATCH_BYTES` | `262144` | Most text one embedding batch carries. A single larger chunk still goes alone. The file's `queue.embedBatchBytes` sets the same thing; set one or the other. |
| `MEMORIA_QMD_RERANK_TIMEOUT_MS` | `20000` | Deadline for one search-time rerank call. Query vectors and expansion have 10 s each. |
| `MEMORIA_QMD_SEARCH_DEADLINE_MS` | `60000` | Deadline for all of one search's calls to workers, including retries, slot waits and the CPU fallback. It sits well under `Memoria.Api`'s 2-minute client timeout, so a hung worker yields a degraded answer rather than a timeout. |
| `MEMORIA_QMD_QUEUE_DB_PATH` | beside the index | The job queue's database. Applies in both LLM modes. |

The pool list names the environment variable holding each token, never the token, so the list itself can be kept in plain configuration. As with other secrets, setting `<name>_FILE` instead of `<name>` reads the token from a file, and `tokenEnv` always names `<name>`. A worker's token must not be the sidecar's own secret: the sidecar compares the values at startup and refuses to run if they match, whatever the variable is called.

### The configuration file

`MEMORIA_QMD_CONFIG_FILE` names one JSON file with two sections, both optional. It replaces hand-editing the pool and the queue's tuning into the environment of each service. The sidecar reads it once, at startup, so a change takes a restart. Every object refuses a setting it does not know, so a misspelt key stops startup rather than falling back to a default.

```json
{
  "workers": [
    { "name": "desktop", "url": "http://192.168.1.20:8383", "tokenEnv": "DESKTOP_WORKER_TOKEN", "priority": 1, "maxConcurrent": 8 },
    {
      "name": "macbook", "url": "http://192.168.1.30:8383", "tokenEnv": "MACBOOK_WORKER_TOKEN", "priority": 2,
      "capabilities": ["rerank", "generate"],
      "availability": { "failureThreshold": 5, "backoffMs": 10000, "maxBackoffMs": 120000 }
    }
  ],
  "queue": { "embedBatchBytes": 262144, "embedConcurrency": 0, "interactiveReserve": 1 }
}
```

**`workers`** is the list `MEMORIA_QMD_WORKERS` takes, entry for entry. Set the pool in the file, in `MEMORIA_QMD_WORKERS`, or with `MEMORIA_QMD_WORKER_URL`; setting it in two places stops startup.

| Worker key | Default | Purpose |
|---|---|---|
| `name` | required | Letters, digits, dots, dashes or underscores, at most 64. Unique. Drain and enable go by it. |
| `url` | required | The worker's address. No credentials, query or fragment. |
| `tokenEnv` | unset | The environment variable holding this worker's token. Required unless the worker is on loopback. An entry holding `token` itself is refused. |
| `capabilities` | all three | Some of `embed`, `rerank` and `generate`. |
| `priority` | `100` | Tiebreak between equally loaded workers. Lower is preferred. |
| `maxConcurrent` | `4` | Calls this sidecar sends the worker at once. |
| `availability.failureThreshold` | `3` | Failures in a row that open one of the worker's circuits. |
| `availability.backoffMs` | `5000` | Wait before the first trial call once a circuit opens. It doubles after each failed trial. |
| `availability.maxBackoffMs` | `60000` | Longest that wait grows to. Must not be shorter than `backoffMs`. |

**`queue`** tunes background work. Reindex jobs still run one at a time, whatever these say.

| Queue key | Default | Purpose |
|---|---|---|
| `embedBatchBytes` | `262144` | Most text one embedding batch carries, as `MEMORIA_QMD_EMBED_BATCH_BYTES`. |
| `embedConcurrency` | `0` | Most embedding batches one job sends at once. `0` uses every background slot the workers have. |
| `interactiveReserve` | `1` | Slots per worker that background work may not use, so a search does not queue behind embedding. Background work always keeps one slot, so a worker with `maxConcurrent: 1` still embeds. |

A file with only `queue` works beside `MEMORIA_QMD_WORKERS` or `MEMORIA_QMD_WORKER_URL`. In local mode the file is not read, as the worker variables are not. Setting up the worker itself is covered in [`worker/README.md`](../worker/README.md).

### Worker tokens

Each worker has its own bearer token. Nothing registers a worker: its token is issued by whoever runs it, and the sidecar holds a copy. Tokens never go in a file kept in the repository. The configuration file names the variable that holds each token, and the loader refuses an entry that holds one.

**Issuing a token for a new worker:**

1. Generate it on a trusted machine: `node -e "console.log(require('crypto').randomBytes(32).toString('base64url'))"`. A token under 16 characters, or one that looks like a placeholder, is refused by both sides.
2. On the worker, write it to the file `MEMORIA_WORKER_TOKEN_FILE` names, readable only by the worker's account. See [`worker/README.md`](../worker/README.md).
3. On the sidecar's host, write the same token to a file only the sidecar can read, and point `<tokenEnv>_FILE` at it, such as `DESKTOP_WORKER_TOKEN_FILE` for `"tokenEnv": "DESKTOP_WORKER_TOKEN"`.
4. Add the worker to `workers` and restart the sidecar.

Never reuse the sidecar's `MEMORIA_QMD_SECRET` for a worker; the sidecar refuses to start if one matches. Give every worker a different token, so replacing one does not touch the others.

**Rotating a token** replaces it on both sides. Each side reads its token at startup, so there is a window in which they disagree. Draining the worker first keeps searches off it during that window:

1. `memoria workers drain <name>`, and wait for `inFlight` to reach 0 in `memoria workers list`.
2. Generate a new token and write it to the worker's token file. Restart the worker.
3. Write it to the sidecar's file for that worker, and restart the sidecar. A restart puts every drained worker back, so there is no separate enable.
4. Check that `memoria workers list` shows the worker `up` with a closed circuit.

Rotate a token that may have leaked the same way, at once. Until the sidecar restarts, the worker refuses the old token with 401, which counts as a failure and opens the worker's circuits, so the other workers carry the load.

### Draining a worker and pausing background work

These are runtime controls, held in the sidecar's memory and never written anywhere. A restart of the sidecar puts every worker back in rotation and resumes the queue, which returns it to its configuration. To keep a worker out for good, remove it from `workers`. Through `Memoria.Api` they need an admin key that is not bound to particular collections; see [`docs/cli.md`](cli.md#operating-the-backend).

**Draining** a worker (`POST /workers/{name}:drain`) stops new calls to it. Calls in flight finish, and `inFlight` in `GET /workers` shows them leave. Heartbeats go on, so its state stays visible, but it gets no trial calls and no comparison probe. `POST /workers/{name}:enable` puts it back. A drained worker does not count toward `llm.workersUp` on `/ready`, so with every worker drained `/ready` reads `degraded` and searches run as in [Degraded mode](#degraded-mode). Draining does not change cache keys, so a worker that comes back unchanged keeps its cached answers.

**Pausing** the queue (`POST /queue:pause`) starts no more reindex or embedding jobs. A running job runs to its end; cancel it to stop it sooner. Writes still land on disk and queue their jobs, so nothing is lost, but new documents are not searchable until `POST /queue:resume`. `GET /queue` reports `paused`. On shutdown a paused queue is not waited on: its jobs persist and run after the restart.

## How calls are routed

`src/llm/pool.ts` decides which worker does each call.

**Choosing a worker.** A worker qualifies if it is configured and reports serving the role, has that model loaded, passed its last heartbeat, and its circuit for that role allows a call. Among those, the order is: not busy, lowest queue depth, best priority, then name. A busy worker is used only when nothing else qualifies. One batch is one call to one worker.

**When a call fails,** it is retried once on a different worker, and counts against the worker it failed on. Only a 400 or 413 says something about the request itself: those are not retried, since another worker would refuse too, and do not count. A 404, 410, 501 or similar is about the worker. Even a 400 stops being taken at its word after 5 in a row with no success, so a worker refusing everything cannot starve its neighbour.

**The circuit breaker.** Each worker has one circuit per role, so a reranker failing does not take embedding down with it, and an embedding success cannot reset the reranker's count. Embedding has two circuits, one for documents and one for query vectors, so a chunk that keeps failing cannot cost search its vectors. If only the document circuit is out, `/ready` lists `documents` in `llm.unavailable` and reads `degraded`. Three failures in a row open a role's circuit. While open, calls for that role skip the worker with no connection attempt. After a backoff of 5 s, doubling up to 1 min, one trial call decides: success closes the circuit, failure doubles the wait. The heartbeat makes that call itself, a small real one for that role, so a role recovers with no search needing to try it first. A search that arrives first can be the trial instead. A failed heartbeat takes the whole worker out at once. A good `/health` does not close a circuit, because it can be fine while every rerank fails. It only frees a trial that never reported back, and lets a worker returning from an outage try straight away.

**Priority.** Search-time work (rerank, expansion, query vectors) is interactive. Embedding and tokenizing documents is background. Interactive calls take any free slot ahead of waiting background calls, and background work can never use a worker's last slot unless `maxConcurrent` is 1. A call already running is not interrupted, so this is priority at the next free slot and not true preemption. A long embedding batch on a `maxConcurrent: 1` worker still makes a search wait for it.

**The embedding model, per worker.** A worker must name the index's model exactly, at every heartbeat and on every embed response, which catches a restart with a different model between heartbeats. A worker failing that does no embedding or tokenizing until a later check clears it, though it can still rerank and expand.

**Workers are compared, and the result is only reported.** Claiming the right name is not proof, so every minute each worker is asked to embed a fixed text and the answers are compared with each other by cosine in that one pass. If they differ, `GET /workers` and `/ready` name the workers and a warning is logged. It cannot say which one is wrong, and it stores nothing between passes. With one worker there is nothing to compare, so it does not run, and a worker's first vectors have nothing to be checked against.

It never takes a worker out of service. This is for honest mistakes, such as two workers on different builds of a model. The bearer token is the trust boundary: a token holder is trusted, and a careful liar would answer this public text honestly anyway. A lockout on this signal could take embedding down over a legitimate model update.

**If a worker did embed bad vectors,** drain it, take it out of the pool's configuration, and run a full force re-embed (`POST /reindex` with `force: true` and no collection). Old vectors stand until each document's new set is written. Jobs keep the names of the workers they used, for tracing, but vectors do not record which worker made them.

### Degraded mode

When no worker can serve a role, search skips the step that needs it rather than faking it. Search responses carry `degraded: true` and say what was missing:

| `degradedReasons` | Meaning |
|---|---|
| `no_rerank` | Results are in rank-fusion order. Scores are rank-derived, not reranker scores, so `minScore` is not applied. |
| `no_expansion` | The query ran as plain lexical and vector sub-queries. |
| `cpu_embedding` | Query vectors came from this host's CPU. |
| `no_vectors` | There were no query vectors, so vector search contributed nothing. |

`no_vectors` is also reported, in both LLM modes, whenever vector search was asked for and the index holds no vectors at all.

**Nothing placeholder reaches `llm_cache`.** The SDK caches any score or expansion it receives, so a made-up value would outlive the outage. Skipped steps never call the SDK's cached functions, and a rerank that fails mid-search throws before the SDK's cache write. A test pins that order to the SDK's source. Expansion output with nothing usable counts as a failure, so the SDK's placeholder sub-queries are never cached in remote mode. After recovery, the next search reranks for real.

**Cached answers do not outlive their worker.** Rerank and expansion cache keys include the identity of the workers serving them (name, address and model file), so replacing a worker cannot replay what it scored. Cached answers expire after 7 days, swept hourly in remote mode. For a worker that gave bad answers and came back unchanged, `POST /cache:purge` drops them all.

`/ready` reports `status: "degraded"` and an `llm` block listing the roles no worker can serve. Those follow recent outcomes, so a role whose circuit is open reads as down even while `/health` looks fine, and a role reads as down until a trial call has actually succeeded. `models.resident` and `warmup.done` follow the same state, not a stored flag, so neither reads as healthy with no worker up. `/ready?requireModels=true` returns 503 in that state.

`Memoria.Api` passes this on. Its search responses, REST and MCP, carry `degraded` and `degradedReasons`, and `memoria query` shows them. Its `/ready` answers 200 with `status: "degraded"`, so losing workers does not take the API out of rotation; `/ready?strict=true` answers 503 instead.

`llm.embedDisagreement` names any workers that embedded the same text differently. That is reported only, and does not make `status` degraded.

`GET /workers` lists each worker's state, circuits, whether it disagrees, whether it is drained, last heartbeat, in-flight calls, queue depth and error rate.

## Embedding jobs in remote mode

The job queue, how it persists, and its routes are in [`sidecar/README.md`](../sidecar/README.md#the-job-queue). This is what remote mode adds.

**With no worker,** a job still runs its scan, so new documents can be found lexically and deleted ones disappear. Embedding then waits: the job goes back to `queued` with `error` saying why, and retries after 30 s, doubling up to 5 min, for as long as it takes. `POST /reindex` for the same target releases it at once, and `:cancel` ends it. On shutdown, the drain does not wait for such a job, because it is persisted anyway.

**In remote mode, embedding is farmed out.** After the scan, chunks are grouped into batches by byte budget, and as many batches run at once as workers have background slots. Vectors are written through QMD's `insertEmbedding`, and the output matches the SDK's own path byte for byte, which a test checks. Progress is whatever the index holds, so a re-run asks what is still pending.

**`force` clears nothing first.** Old vectors stand until a document's new set is ready, so an outage partway through loses none. A job resuming redoes only what it has not redone since it was accepted. The one exception is a new vector length, which cannot share the table: a force job over every collection then drops everything, but only once it holds a real vector of that length. Any other job meeting a new length fails with an explanation and changes nothing. A force job over every collection also removes the vectors of documents no longer in the index, before it starts on the rest, so restoring such a document embeds it afresh rather than bringing old vectors back. Asking again for a force job that is waiting gives it a new accept time, so it redoes everything written before the request, and the job's `createdAt` shows that time. A row dated in the future, from a clock moved back since, counts as due, so `force` still redoes it.

**One bad chunk costs one document.** A failed batch is retried with a short backoff, which rides out a brief failure even on one worker. Then a known-good text decides what happened: if no worker can embed it, that is an outage and the job waits. If one can, the batch is split until what fails is alone.

**A document is set aside only when the worker is shown to be fine.** When a run ends with documents that failed, every worker is asked to embed a known-good text as long as the longest real chunk. A worker refusing that is too small or broken, so its refusals say nothing about the documents: the job waits, and sets nothing aside. If every worker accepts it:

- **A refusal is believed at once.** That means a 400 or 413 for that text. The worker returns 400 when llama-server rejects the input.
- **A 5xx or a timeout needs to repeat.** The document stays pending, and the job ends `failed` with `embed.docsPending` set, for the next job to try. After failing in 3 runs at least 10 minutes apart, it is set aside. A brief window of errors cannot span that. The count is kept in memory, so a restart starts it again.

**Failures that outnumber everything blame the worker.** If 5 or more documents are refused, or 5 or more fail, and they outnumber all the documents that have ever embedded, the job stops and waits. Documents that failed lately go last in the next run, so they cannot starve the rest, and if they were the problem the rest then embed and outnumber them.

**A waiting job is never quiet.** A job can wait for as long as it takes, and that includes a worker that accepts the test text and refuses every real one. While it waits, `/ready` reads `degraded` and shows `jobs.stuck` with the reason and how many times it has tried, and each retry logs a warning.

**A set-aside document is out of vector search, and comes back by itself.** It is recorded in `memoria_dead_letters`, a table in the index beside QMD's, and any vectors it had are dropped, so a force job cannot leave a bad worker's vector live. It can still be found lexically. The job that found it ends `failed` and counts it in `embed.docsDeadLettered`, and later jobs skip it and succeed. `/ready` shows the count in `llm.documentsSetAside`, and `/status` reports it as `documentsSetAside`, not in `needsEmbedding`.

It is tried again when any of these happens:

- Its content or path changes. The record is by both, because the path can reach the text through the title.
- The workers or the model change, because the refusal may have been theirs.
- 24 hours pass.
- A force job runs.
- `POST /queue/dead-letters:clear` is called.

The record goes when the document is deleted.

**Other processes may hold the index.** QMD sets a 120 s busy timeout, and SQLite waits on the event loop, so the farm's writes, the startup repair and the cache sweep all use a 250 ms one instead. A farm write that cannot get the index defers the job rather than failing it. The sweep and the repair log it, or skip it, and try again next time.

**The startup repair can catch another process mid-write.** It cannot tell a row left by a kill from one another process has just written and not yet given a vector, because QMD's write holds no lock between the two. That row is removed, and the document embeds again on the next job. No vector is lost, and nothing cheap can detect the writer.

The farm has to reach SDK internals that are not part of its public API, and copies the SQL of a private function. `test/llm-surface.test.ts` pins all of this to the exact SDK version.

**A job leaving documents pending does not report success.** It ends `failed`, with its `result` attached, `embed.docsPending` saying how many, and an `error` saying so. `:retry` or the next job for that collection picks them up. With no worker at all it waits instead, since that resolves itself. In-process embedding in local mode still ends `succeeded` with `embed.errors` set, as the SDK reports no pending count.

## The main risk: reranker scores

Search results are ordered by reranker score, so if a worker scores differently, results change. This is the gap as far as the code shows it.

### What in-process QMD actually does

QMD does not format the reranker's prompt or read the yes and no logits itself. It calls `rankAll` on a node-llama-cpp ranking context (`LlamaCpp.rerank` in the SDK's `dist/llm.js`). That library:

- takes the prompt from the model's own `tokenizer.chat_template.rerank` metadata;
- tokenizes the template's pieces, the query and the document separately, and joins the tokens;
- evaluates with llama.cpp's rank pooling, and takes the first value of the pooled output as the score;
- for the `qwen3` architecture, treats that value as a probability already, because llama.cpp applies the softmax. Other architectures get a sigmoid.

The yes and no logits are therefore read inside llama.cpp, not in QMD or node-llama-cpp.

### The decision

**The worker builds node-llama-cpp's tokens itself and sends them to llama-server's `/completion`** (`QmdTokensRerank`, reported as `llama-server-qmd-tokens`). It reads the probabilities of "yes" and "no" after the prompt, and their ratio is the score. This is the second of the three options the design left open, and it was chosen on these measurements, from llama.cpp build b12110 and `@tobilu/qmd` 2.8.3 on 24 fixed queries against the real index.

The comparison is always against in-process QMD on the same candidate chunks, 532 of them. Rank correlation is Spearman, mean over the queries.

| Against in-process QMD on Vulkan | Rank correlation | Top-10 overlap | Mean score difference | Largest difference |
|---|---|---|---|---|
| In-process, run again | 1.000 | 1.000 | 0 | 0 |
| In-process on CUDA: no transport involved, only the GPU backend | 0.991 | 0.979 | 0.0027 | 0.082 |
| llama-server `/rerank` on CUDA: the original design | 0.970 | 0.942 | 0.0092 | 0.185 |
| llama-server `/rerank` on Vulkan | 0.974 | 0.950 | 0.0096 | 0.221 |
| Exact tokens, `/embeddings` on CUDA: rejected, it crashes | 0.989 | 0.967 | 0.0031 | 0.072 |
| Exact tokens, `/embeddings` on Vulkan: rejected, it crashes | 0.999 | 0.988 | 0.0008 | 0.021 |
| **Exact tokens, `/completion` on CUDA: chosen, full benchmark** | **0.985** | **0.967** | **0.0028** | **0.060** |

Reading those rows:

- **Tokens were the gap.** node-llama-cpp tokenizes the template's pieces, the query and the document separately, so `<Query>: ` and the query become `:`, a space and `how`. `/rerank` tokenizes one string and gets `:` and ` how`. Rebuilding the input by the first method matched node-llama-cpp token for token on all 532 chunks.
- **The rest is the GPU backend.** Once the tokens match, the difference is the size of the difference between in-process QMD on Vulkan and on CUDA. With both sides on Vulkan and exact tokens it nearly vanishes, at 0.999. A node-llama-cpp wrapper would land on the same floor on CUDA, so its native dependency buys nothing here.
- **In-process QMD on this desktop runs on Vulkan, not CUDA.** node-llama-cpp's automatic choice needs the CUDA runtime on `PATH`. So the baseline a worker has to match depends on the machine.
- **The quirk suspected in node-llama-cpp does not fire.** Its `unshift` of an end token does not run for this model, whose metadata asks for no end token. The input starts with `<|im_start|>`.

**Why not `/embeddings`?** It takes token arrays on a rank-pooled server and gave the best numbers, but its output carries memory past the two scores, as values like `-1.4e-36`, and on real data the server died twice with an access violation (exit `0xC0000005`) in one benchmark run. llama.cpp's source shows the cause: `send_embedding` reads the model's full embedding width from a vector holding only the two scores. Across 97,156 synthetic documents the bad read showed in every response but the crash did not recur, so it depends on what sits next in memory.

**What is weaker about `/completion`.**

- It is slower than `/rerank`: about 1.2 s for 40 long documents, against about 0.7 s.
- "yes" can fall outside the 200 listed probabilities when "no" dominates. Such a document scores below the lowest listed probability, and order among documents that far down is lost. That is where the lower rank correlation comes from; those scores are far from the top 10.
- The template is written into the worker, so a different rerank model needs a change there.
- One 2-hour soak is the evidence for its stability, below. Its one error is unexplained.

### Full benchmark on the chosen code

One run of `bench/parity.ts` with the same flags as `npm run bench:parity`, with no errors and no worker restarts. Latency was measured with other builds running on the machine, at below-normal priority, so treat it as a rough guide.

| Measurement | Mean | Minimum | Median | Worst query |
|---|---|---|---|---|
| Rerank rank correlation | 0.985 | 0.922 | 0.992 | |
| Rerank top-10 overlap | 0.967 | 0.900 | 1.000 | |
| Rerank mean score difference | 0.0028 | | 0.0013 | 0.0115 |
| Rerank largest score difference | 0.019 | | 0.008 | 0.060 |
| End-to-end top-10 overlap | 0.983 | 0.800 | 1.000 | |
| End-to-end rank correlation | 0.983 | 0.879 | 0.988 | |
| Embedding cosine | 0.9998 | 0.9997 | 0.9998 | |

| Latency, ms | Mean | Median | 95th percentile |
|---|---|---|---|
| Rerank, in-process | 2058 | 1728 | 3348 |
| Rerank, worker | 1011 | 939 | 1656 |
| Search with rerank, in-process | 4330 | 2912 | 9448 |
| Search with rerank, worker | 3941 | 1937 | 10665 |

On the GPU, with no sidecar involved: one short embedding takes about 77 ms, a batch of 32 about 170 ms, reranking 10 long documents about 345 ms, and expansion a median of about 270 ms. All three models take about 4.4 GB of VRAM. During requests the card reached 80% utilization and about 200 W, from 12 W idle.

### Two-hour soak

Sustained rerank and embed load on the real GPU, using real document text from a copy of the index.

| Measurement | Result |
|---|---|
| Rerank | 5,398 of 5,399 calls succeeded, over 135,181 documents. Per-minute median 2.5 s for 10 to 40 documents, sharing the GPU. |
| Embedding | All 15,463 calls succeeded, over 494,816 texts. Per-minute median 416 ms for 32 texts. |
| Crashes and restarts | None. |
| The one failure | One rerank at minute 63 returned HTTP 502. The cause is unknown, since llama-server's output is not kept. |
| Committed memory, start to end | Rerank server 2,741 to 2,808 MiB, embedding server 1,669 to 1,674 MiB, generate server 2,234 to 2,247 MiB. |

The soak ran on the code before the hardening in this file's trust boundaries section. The hardened worker has since passed the smoke script and the live tests, and ran the re-embed below.

### Re-embedding the corpus

A full re-embed of a copy of the index through the pool: 1,568 documents and 46,347 chunks on an RTX 5080, with no errors.

| | Time | Chunks per second | `/v1/tokenize` calls | Tokenize time, summed |
|---|---|---|---|---|
| One tokenize call per chunk | 29 min 14 s | 26 | 50,831 | 787 s |
| `/v1/tokenize-batch` | 9 min 34 s | 81 | 9,626, plus 1,593 batches | 126 s |

Gaps of more than 5 seconds between embedding calls fell from 746 s to 65 s. The remaining single calls are for text the chunker has to re-split. Machine load differed between the two runs, so treat the ratio as approximate.

### Existing vectors do not match the worker's

**Most vectors in an index built in-process cover only the end of their chunk.** node-llama-cpp's embedding context evaluates 512 tokens at a time, and the pooled result reflects only the final batch. So a chunk over 512 tokens is represented by its tail. This is QMD's open upstream fix, tobi/qmd#993. The worker sends the whole chunk at once. On 120 chunks of the real index:

| Chunk length | Chunks | Stored vector against the whole chunk | Stored vector against the final batch alone |
|---|---|---|---|
| Up to 512 tokens | 34 | 0.9997 | 0.9997 |
| 513 to 1,024 tokens | 86 | 0.716 | 0.9996 |

Stored vectors match in-process QMD today at 0.99999, so nothing is corrupt. The re-embed above matched the stored chunk positions exactly on all 1,492 documents they had in common. Its 46,347 chunks are the stored index's 44,767 live ones plus the 76 documents that were pending; the other 13,662 stored vectors are orphans.

Both kinds of vector carry the same model name and fingerprint, so **QMD cannot tell them apart, and a worker embedding part of an index built in-process leaves it mixed.** Re-embed the whole index through the worker when switching, rather than letting one fill in pending documents. Whether whole-chunk vectors search better has not been measured.

### Seams

- **Sidecar:** `RerankTransport` in `sidecar/src/llm/rerank-transport.ts`. `RemoteLlm` takes any implementation and validates its output the same way.
- **Worker:** `RerankBackend` in `worker/src/inference.ts`.

## Other differences from in-process

**Query expansion.** Measured, and three real differences were found and fixed. In-process, `LlamaChatSession` adds node-llama-cpp's default system prompt, which the server's template does not. QMD's presence penalty never applies in-process, because with no `penalty` value node-llama-cpp leaves out the whole penalty stage. And llama-server's `min_p` defaults to 0.05 where in-process it is off. The worker now sends the system prompt with the penalty and `min_p` off. The rendered prompt matches node-llama-cpp's tokens on the three strings checked, and tests pin these facts to the installed libraries.

Sampling is random on both sides, so the number to read is how much runs agree across sides compared with within a side, as Jaccard overlap of sub-queries:

| Mean over 24 queries | In-process with itself | Across sides | Worker with itself | Exact match across sides |
|---|---|---|---|---|
| Before the fixes | 0.194 | 0.094 | 0.134 | 0.015 |
| After | 0.221 | 0.196 | 0.166 | 0.045 |

Across-sides agreement now sits between the two within-side figures, so the remote path no longer behaves differently. node-llama-cpp also leaves newlines out of penalties, which no longer matters with penalties off.

**Embeddings.** On short text, such as queries, cosine against in-process is 0.9998 on CUDA and 0.999997 on a Vulkan llama-server, so this is the GPU backend and too small to matter. Long chunks are a different matter, as [above](#existing-vectors-do-not-match-the-workers). The server's vectors are normalized and in-process ones are not, which cosine distance ignores.

**Control tokens in document text.** llama-server parses them in strings. On synthetic text, a document with `<|im_end|>` and a fake answer scored 0.64 through `/rerank`, against 0.49 with the same text kept as text, and 0.03 for the same words with the markers removed. `/v1/embeddings` parses them too, moving the vector to a cosine of 0.92. In-process QMD keeps them as text, and the worker now does as well for both, by sending tokens. A document can still argue for itself in plain words, in-process or not.

**Build and version.** node-llama-cpp bundles llama.cpp b10361; the worker ran b12110. Results above include any effect of that gap. Another llama-server release can change the routes this relies on, and the smoke script is the first place that would show.

## Memory

**A search can allocate over 4 GB of JavaScript heap.** QMD's vector lookup reads a document's whole body once per matching chunk, and this corpus has documents up to 21 MB. One query reached 4.1 GB and, at Node's default limit, the benchmark died of it. That is QMD's behaviour, in-process or remote. `bench:parity` now raises the limit to 6 GiB. The sidecar service (`npm start` and its image) runs with 4 GiB, which is under that 4.1 GB query.

**The benchmark cleans up its index copies.** They hold real transcripts. A normal exit and Ctrl-C remove them. A fatal crash runs no code at all, so the next run removes what a dead run left, recognising its own directories by a marker holding the owner's process ID. `--out` is rewritten after each query and marked `partial` until the run ends, so a crash keeps the results so far.

**llama-server's prompt cache is off.** It defaults to 8 GiB of system memory and reached that on the rerank server, starving the machine until allocations failed. It also held real documents, for no benefit, because these requests do not repeat.

## Design decisions

**The chunker's tokenizer also goes remote.** QMD's `chunkDocumentByTokens` ignores `store.llm` and asks for a process-wide default instance. Assigning `store.internal.llm` alone would load the embedding model on the sidecar's host just to count tokens. Remote mode therefore also calls the SDK's `setDefaultLlamaCpp`. The SDK's exports map hides that function, so `src/store.ts` reaches it by file URL. `test/llm-surface.test.ts` pins this for the exact SDK version.

**Worker results get their own cache keys.** QMD caches rerank scores and expansions in `llm_cache`, keyed by model name. Remote mode appends `#worker` to those two names. Otherwise cached in-process scores would be replayed as if a worker had produced them, worker scores would be served after a return to local mode, and a parity comparison would look perfect. The embedding model keeps its plain name, because stored vectors are keyed by it. Changing it would force a full re-embed.

**Document embedding has no CPU fallback.** Only query embedding does. A corpus embed on CPU could run for hours while looking healthy. Instead the job waits for a worker, and its documents stay pending.

## Trust boundaries

**Worker responses are untrusted.** A response that breaks a rule is rejected whole, never clamped. Rerank results and expansions would otherwise persist in `llm_cache`.

| Response | Rules |
|---|---|
| Embeddings | Count equals the request. Every vector has the expected length, finite numbers only, and is not all zeros. |
| Rerank | Count equals the request. Indexes are integers, in range and unique. Scores are finite and within 0 to 1. Applies to any transport. |
| Tokens | Non-negative integers, and no more than the text's bytes plus 16. |
| Text | A string under a size limit. |
| Any | Must be JSON, and is cut off while reading past 64 MiB. Redirects are refused, so the token is not resent. |

**The worker does not take llama-server's word for who it is.** Another local user could bind a child's port during a restart and be sent transcript text. So each start gets a random API key and a random nonce, passed in the environment because a command line is visible to other users. A server is ready only once an authenticated `/props` returns the nonce as its model alias, and that check repeats on every health poll. `llama-server` listed as an external URL cannot be checked this way, so it is the operator's responsibility; its address must be a literal loopback or private one, and `MEMORIA_WORKER_<ROLE>_API_KEY` supplies its key.

**Nothing from llama-server reaches a log.** Its output can hold prompt text. The worker keeps the last 2,000 characters in memory only to report a fixed `reason` when a server exits: `port_in_use`, `model_load_failed`, `out_of_memory` or `bad_arguments`. The sidecar's YAML parser also keeps its warnings, which quote frontmatter, off stderr.

**What the worker takes in is bounded.** A full queue refuses before reading a body. All bodies held at once are capped by `MEMORIA_WORKER_MAX_BUFFERED_BYTES`, because the old defaults allowed about 24 GiB. Responses from llama-server stop being read at 64 MiB, and calls refuse redirects, since one would carry transcript text elsewhere. Grammars are capped at 16 KiB. On the real server recursive grammars such as `root ::= root root` get an immediate 400, but a half-megabyte one held the generate slot for over 45 seconds; 11 KB took under 2 seconds.

**The bearer token stays in one header.** Error messages are built from fixed text and validated codes, never from a response body or a caught error's message, because they reach job records and `/ready`. Per-call log lines hold only worker host and port, operation, batch size, duration, status and an error code. On the worker, neither the token nor any other `MEMORIA_` or `LLAMA_` variable passes to llama-server's environment, whatever the case of its name.

## The parity benchmark

`sidecar/bench/` holds a fixed set of 24 queries across both collections, each with an intent. It needs a real worker and the models on the machine that runs it, and it works on temporary copies of the index:

```bash
cd sidecar
MEMORIA_QMD_WORKER_URL=http://<worker>:8383 MEMORIA_QMD_WORKER_TOKEN=... \
  npm run bench:parity -- --db /path/to/index.sqlite --out parity.json
```

With no worker URL it does nothing and exits 0.

| Measurement | How | Reading it |
|---|---|---|
| Rerank scores | The same candidate chunks go to both sides, bypassing the cache. Reports mean and largest score difference, rank correlation, and top-10 overlap. | The direct test of the main risk. Rank correlation and overlap matter most, because results are ordered by score. Large score differences with good rank agreement would still affect `minScore` users. |
| Embeddings | The same text through both sides. | Cosine should be very near 1. |
| End to end | Typed sub-queries through each side's own pipeline, caches cleared. Reports top-10 overlap, correlation and latency. | What a caller would see, without expansion's randomness. |
| Expansion | Five runs per side per query. Reports overlap of sub-queries within and across sides, and the exact-match rate. | Compare local-versus-remote with local-versus-local. Only a gap between those two means the remote path behaves differently. |

The benchmark reports numbers and sets no thresholds. [The decision](#the-decision) records the results it supported.

## What has and has not been verified

| Claim | Evidence |
|---|---|
| Local mode loads no adapter code and fetches nothing | Run: a child process watches module loading, with a worker URL set in its environment. |
| The store works with the adapter on every path | Run: real SDK and sqlite-vec with a mocked worker, on a temporary index. |
| The adapter assumes the right things about the SDK | Run: tests read the installed SDK's source. |
| Supervision, restart, auth and the smoke script work | Run on Windows and in CI: real child processes, with a fixture playing llama-server. |
| Failover, the circuit breaker and priority work | Run: real HTTP to mock workers, counting connection attempts. |
| Degraded search caches nothing | Run: real SDK with mock workers, reading `llm_cache`. |
| Farmed embedding writes vectors QMD can search | Run: real SDK and sqlite-vec with mock workers' made-up vectors. |
| Jobs survive a crash | Run: a child process killed outright, on a real queue file, with the fake backend. |
| A kill mid-write leaves vectors consistent | Run: the farm on the real SDK, killed outright six times at varied moments. A kill is a race, so a deterministic test covers the rollback, and another the startup repair on the exact state a kill leaves. |
| The farm matches the SDK's output | Run: both paths on a fake model, comparing rows and vector bytes. |
| An index from before the newer vector columns works | Run: a fixture rebuilt to the old shape, not a real old index. |
| Interactive latency under real embedding load | **Not measured.** The priority logic is tested; its effect on a GPU is not. |
| llama-server accepts these flags and routes | Run on Windows with llama.cpp b12110 on CUDA: smoke script and `npm run test:live`. |
| Scores match in-process QMD | Run: the benchmark above, on one machine. Close, not identical. |
| GPU loading on CUDA | Run: `nvidia-smi` shows VRAM, utilization and power rising during requests. |
| GPU loading on Metal | **Not run.** |
| A worker pool, or a worker on another machine | **Not run for real.** Everything here was one worker on loopback. |
| The NSSM and launchd scripts work | **Not run for real.** Dry-run plans and ACL helpers are tested; see [what is unconfirmed](deployment.md#not-confirmed). |
| Windows VRAM reading | Run: the parser's result matched real `nvidia-smi` output on one GPU. |
| macOS thermal reading | Parsing tested against sample output, not against the real tool. |
