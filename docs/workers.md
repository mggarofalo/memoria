# Inference workers

The sidecar can send its model work to another machine. QMD still owns retrieval on the node that holds the index: full-text search, vector lookup, fusion and caching all stay in the sidecar's process. Only the three model operations move: embedding, reranking and query expansion.

**Status: delivered, not yet proven.** None of this has run on real hardware or with real models. Every test so far uses fakes, as listed under [What has and has not been verified](#what-has-and-has-not-been-verified). `MEMORIA_QMD_LLM=local` remains the default, and local mode loads none of this code.

**The choice of reranker transport is pending a real-hardware run** of the parity benchmark. No decision has been made.

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
| `MEMORIA_QMD_WORKERS` | unset | The pool, as a JSON list. See below. |
| `MEMORIA_QMD_WORKER_URL` | unset | Shorthand for a pool of one. Must not contain credentials. Set this or the list, not both. |
| `MEMORIA_QMD_WORKER_TOKEN` | unset | The token for `MEMORIA_QMD_WORKER_URL`. Required unless the worker is on loopback. At least 16 characters. `MEMORIA_QMD_WORKER_TOKEN_FILE` reads it from a file instead, which is what a service should use. |
| `MEMORIA_QMD_WORKER_POLL_MS` | `10000` | How often each worker's `/health` and `/capabilities` are read. |
| `MEMORIA_QMD_LLM_FALLBACK` | `1` | Tokenize and embed queries on this host's CPU when no worker answers. Needs the embedding model in QMD's cache on this host, under the name QMD gives it (`hf_<owner>_<file>`). It never downloads one, and a call gives up after 30 s. |
| `MEMORIA_QMD_EMBED_DIMENSIONS` | `768` | Every embedding must have this length. `0` pins it to the first response. |
| `MEMORIA_QMD_EMBED_BATCH_BYTES` | `262144` | Most text one embedding batch carries. A single larger chunk still goes alone. |
| `MEMORIA_QMD_RERANK_TIMEOUT_MS` | `20000` | Deadline for one search-time rerank call. Query vectors and expansion have 10 s each. |
| `MEMORIA_QMD_SEARCH_DEADLINE_MS` | `60000` | Deadline for all of one search's calls to workers, including retries, slot waits and the CPU fallback. It sits well under `Memoria.Api`'s 2-minute client timeout, so a hung worker yields a degraded answer rather than a timeout. |
| `MEMORIA_QMD_QUEUE_DB_PATH` | beside the index | The job queue's database. Applies in both LLM modes. |

The pool list names the environment variable holding each token, never the token, so the list itself can be kept in plain configuration. As with other secrets, setting `<name>_FILE` instead of `<name>` reads the token from a file, and `tokenEnv` always names `<name>`. A worker's token must not be the sidecar's own secret: the sidecar compares the values at startup and refuses to run if they match, whatever the variable is called.

Example:

```json
[
  { "name": "desktop", "url": "http://192.168.1.20:8383", "tokenEnv": "DESKTOP_WORKER_TOKEN", "priority": 1, "maxConcurrent": 8 },
  { "name": "macbook", "url": "http://192.168.1.30:8383", "tokenEnv": "MACBOOK_WORKER_TOKEN", "priority": 2, "capabilities": ["rerank", "generate"] }
]
```

`capabilities` defaults to all three of `embed`, `rerank` and `generate`, `priority` to 100 (lower is preferred), and `maxConcurrent` to 4. Setting up the worker itself is covered in [`worker/README.md`](../worker/README.md).

## How calls are routed

`src/llm/pool.ts` decides which worker does each call.

**Choosing a worker.** A worker qualifies if it is configured and reports serving the role, has that model loaded, passed its last heartbeat, and its circuit for that role allows a call. Among those, the order is: not busy, lowest queue depth, best priority, then name. A busy worker is used only when nothing else qualifies. One batch is one call to one worker.

**When a call fails,** it is retried once on a different worker, and counts against the worker it failed on. Only a 400 or 413 says something about the request itself: those are not retried, since another worker would refuse too, and do not count. A 404, 410, 501 or similar is about the worker. Even a 400 stops being taken at its word after 5 in a row with no success, so a worker refusing everything cannot starve its neighbour.

**The circuit breaker.** Each worker has one circuit per role, so a reranker failing does not take embedding down with it, and an embedding success cannot reset the reranker's count. Embedding has two circuits, one for documents and one for query vectors, so a chunk that keeps failing cannot cost search its vectors. Three failures in a row open a role's circuit. While open, calls for that role skip the worker with no connection attempt. After a backoff of 5 s, doubling up to 1 min, one trial call decides: success closes the circuit, failure doubles the wait. The heartbeat makes that call itself, a small real one for that role, so a role recovers with no search needing to try it first. A search that arrives first can be the trial instead. A failed heartbeat takes the whole worker out at once. A good `/health` does not close a circuit, because it can be fine while every rerank fails. It only frees a trial that never reported back, and lets a worker returning from an outage try straight away.

**Priority.** Search-time work (rerank, expansion, query vectors) is interactive. Embedding and tokenizing documents is background. Interactive calls take any free slot ahead of waiting background calls, and background work can never use a worker's last slot unless `maxConcurrent` is 1. A call already running is not interrupted, so this is priority at the next free slot and not true preemption. A long embedding batch on a `maxConcurrent: 1` worker still makes a search wait for it.

**The embedding model, per worker.** A worker must name the index's model exactly, at every heartbeat and on every embed response, which catches a restart with a different model between heartbeats. A worker failing that does no embedding or tokenizing until a later check clears it, though it can still rerank and expand.

**Workers are compared, and the result is only reported.** Claiming the right name is not proof, so every minute each worker is asked to embed a fixed text and the answers are compared with each other by cosine in that one pass. If they differ, `GET /workers` and `/ready` name the workers and a warning is logged. It cannot say which one is wrong, and it stores nothing between passes. With one worker there is nothing to compare, so it does not run, and a worker's first vectors have nothing to be checked against.

It never takes a worker out of service. This is for honest mistakes, such as two workers on different builds of a model. The bearer token is the trust boundary: a token holder is trusted, and a careful liar would answer this public text honestly anyway. A lockout on this signal could take embedding down over a legitimate model update.

**If a worker did embed bad vectors,** take it out of `MEMORIA_QMD_WORKERS` and run a full force re-embed (`POST /reindex` with `force: true` and no collection). Old vectors stand until each document's new set is written. Jobs keep the names of the workers they used, for tracing, but vectors do not record which worker made them.

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

`Memoria.Api` does not pass `degraded` through to its own callers yet. Its `/ready` already follows `models.resident`, but its search responses do not carry the flag.

`llm.embedDisagreement` names any workers that embedded the same text differently. That is reported only, and does not make `status` degraded.

`GET /workers` lists each worker's state, circuits, whether it disagrees, last heartbeat, in-flight calls, queue depth and error rate.

## The job queue

Reindex jobs persist in their own SQLite file beside the index (`index.sqlite.queue.sqlite`). It is not inside QMD's schema, so rebuilding the index leaves jobs alone. This uses Node's built-in `node:sqlite`, which adds no dependency but prints an experimental warning on Node 22.

**A job accepted before a crash runs after the restart,** under the same id. A held job keeps its due time. A job that was running goes back to queued and runs again. A job found running by 5 restarts in a row, with no progress between them, is marked failed, on the assumption that it is what keeps killing the process. Retries during an outage do not count, a document reaching the index clears the count, and so does `:retry`. A clean shutdown leaves a running job queued, which does not count either.

**Resuming is safe in remote mode because a document is written whole.** Its vectors build up in memory and go to the index in one transaction, replacing what it had.

**QMD's own write is not atomic.** Its `insertEmbedding` writes two tables with no transaction, so a kill between them leaves a row claiming a vector that does not exist, and the document reads as complete for good. The farm's transaction closes this for remote mode, but in-process embedding still goes through the SDK. So every start, in both LLM modes, removes such rows and logs how many, and those documents embed again. Between a kill and the next start the index may be short a vector, and only a restart repairs it.

**A damaged queue file does not stop search.** A row that cannot be read is moved to `jobs_quarantine`. Only a file SQLite reports as corrupt, or as not a database, is renamed to `*.corrupt-<time>`, and the last 3 of those are kept. Either way the jobs involved will not run, which a warning says. Any other trouble opening it leaves the file untouched and runs with jobs in memory, with a warning: that covers a file from a newer version and one held by another process.

**One sidecar owns a queue file.** It holds an exclusive SQLite lock for as long as it runs, and the operating system releases that if the process dies. A second sidecar on the same file runs with its jobs in memory and says so, and a `/ready` showing `jobs.persistent: false` is how to notice.

A later write failure, such as a full disk, is logged and the queue carries on in memory, writing what it missed if the store recovers. `/ready` shows `jobs.persistent` and `GET /queue` shows `persistence`, so this cannot go unnoticed. Writes never wait on a lock, because they run on the event loop.

**What is stored:** the request, timestamps, result counts, attempts, worker names, and error text. A reason names a file path at most. Document bodies and tokens are never stored, and a test reads the database's raw bytes to check.

**States are unchanged:** `queued`, `running`, `succeeded`, `failed`. So is the debounce. New fields on a job (`priority`, `attempts`, `worker`) are additions.

**With no worker,** a job still runs its scan, so new documents can be found lexically and deleted ones disappear. Embedding then waits: the job goes back to `queued` with `error` saying why, and retries after 30 s, doubling up to 5 min, for as long as it takes. `POST /reindex` for the same target releases it at once, and `:cancel` ends it. On shutdown, the drain does not wait for such a job, because it is persisted anyway.

**In remote mode, embedding is farmed out.** After the scan, chunks are grouped into batches by byte budget, and as many batches run at once as workers have background slots. Vectors are written through QMD's `insertEmbedding`, and the output matches the SDK's own path byte for byte, which a test checks. Progress is whatever the index holds, so a re-run asks what is still pending.

**`force` clears nothing first.** Old vectors stand until a document's new set is ready, so an outage partway through loses none. A job resuming redoes only what it has not redone since it was accepted. The one exception is a new vector length, which cannot share the table: a force job over every collection then drops everything, but only once it holds a real vector of that length. Any other job meeting a new length fails with an explanation and changes nothing. A full force job that finishes also removes the rows of documents no longer in the index. A row dated in the future, from a clock moved back since, counts as due, so `force` still redoes it.

**One bad chunk costs one document, once.** A failed batch is retried with a short backoff, which rides out a brief failure even on one worker. Then a known-good text decides what happened, because a batch's own failures cannot tell a bad batch from a dead worker. If no worker can embed it, that is an outage and the job waits, with nothing held against any document. If one can, the batch is split until what cannot be embedded is alone, and that document is set aside.

A set-aside document is recorded in `memoria_dead_letters`, a table in the index beside QMD's. The job that found it ends `failed` and counts it in `embed.docsDeadLettered`. Later jobs skip it and succeed. It is not in vector search but can still be found lexically, and `/status` still counts it as needing embedding. Changing the document retries it, because the record is by content. So does a force job, and `POST /queue/dead-letters:clear`, for after a worker or model has been fixed.

**Other processes may hold the index.** QMD sets a 120 s busy timeout, and SQLite waits on the event loop, so the farm's writes, the startup repair and the cache sweep all use a 250 ms one instead. A farm write that cannot get the index defers the job rather than failing it. The sweep and the repair log it, or skip it, and try again next time.

**The startup repair can catch another process mid-write.** It cannot tell a row left by a kill from one another process has just written and not yet given a vector, because QMD's write holds no lock between the two. That row is removed, and the document embeds again on the next job. No vector is lost, and nothing cheap can detect the writer.

The farm has to reach SDK internals that are not part of its public API, and copies the SQL of a private function. `test/llm-surface.test.ts` pins all of this to the exact SDK version.

**A job leaving documents pending does not report success.** It ends `failed`, with its `result` attached, `embed.docsPending` saying how many, and an `error` saying so. `:retry` or the next job for that collection picks them up. With no worker at all it waits instead, since that resolves itself. In-process embedding in local mode still ends `succeeded` with `embed.errors` set, as the SDK reports no pending count.

| Route | What it does |
|---|---|
| `GET /queue` | Jobs by state, model calls by priority class, and calls per worker. |
| `POST /queue/jobs/{id}:retry` | Queue a failed job again under the same id. 409 unless it has failed. |
| `POST /queue/jobs/{id}:cancel` | End a waiting job at once. A running job stops at its next safe point, between batches when farming, so a long scan or an in-process embed runs on until it returns. A cancelled job reports `failed` with the error `Cancelled`. 409 if already finished. |
| `GET /queue/dead-letters` | Documents set aside, and why. `GET /queue` has the count. |
| `POST /queue/dead-letters:clear` | Let them embed again, and queue a job to do it. |
| `POST /cache:purge` | Drop every cached rerank score and expansion. |

These are sidecar routes. `Memoria.Api` does not expose them.

## The main risk: reranker scores

Search results are ordered by reranker score, so if a worker scores differently, results change. This is the gap as far as the code shows it.

### What in-process QMD actually does

QMD does not format the reranker's prompt or read the yes and no logits itself. It calls `rankAll` on a node-llama-cpp ranking context (`LlamaCpp.rerank` in the SDK's `dist/llm.js`). That library:

- takes the prompt from the model's own `tokenizer.chat_template.rerank` metadata;
- tokenizes the template's pieces, the query and the document separately, and joins the tokens;
- evaluates with llama.cpp's rank pooling, and takes the first value of the pooled output as the score;
- for the `qwen3` architecture, treats that value as a probability already, because llama.cpp applies the softmax. Other architectures get a sigmoid.

The yes and no logits are therefore read inside llama.cpp, not in QMD or node-llama-cpp.

### What the worker does

The implemented transport is **`llama-server-rerank`**: the worker truncates documents to QMD's budget, then calls llama-server's `/rerank`. As documented upstream, that endpoint also formats pairs with the model's rerank template and uses rank pooling. So the two paths share a model and, in principle, a template and a pooling method. They are not the same code, and these differences could change scores:

| Difference | Why it could matter |
|---|---|
| **Tokenization.** node-llama-cpp tokenizes template pieces separately. llama-server is expected to tokenize one formatted string. | Token boundaries can differ around the joins, which changes model input. |
| **A quirk in node-llama-cpp.** When the input does not already end in the end token, `_getEvaluationInput` calls `unshift`, which puts that token at the *start* of the input. | If this runs for this model, the in-process input carries an extra leading token that llama-server will not add. Scores might shift, and in-process would be the odd one out. Not checked against the model's metadata. |
| **llama.cpp version.** node-llama-cpp bundles its own build. The worker runs whatever llama-server you install. | Pooling or template handling may differ between builds. |
| **Score scale.** llama-server's `relevance_score` is expected to be the pooled value unchanged. | If so it is already 0 to 1 for this model. This has not been seen on a real server, so the worker rejects anything outside 0 to 1 instead of rescaling it. |
| **Context handling.** QMD evaluates in 4096-token contexts. The worker sets `--ctx-size` to 4096 per slot and `--ubatch-size` to 4096. | Flags come from upstream documentation. They have not been run. |

The flags and routes in `worker/src/supervisor.ts` and `worker/src/inference.ts` come from upstream documentation as remembered, not from a running server. A llama-server release may differ, and the smoke script is the first place that would show.

### If the transport has to change

Two seams exist so the store never needs changing:

- **Sidecar:** `RerankTransport` in `sidecar/src/llm/rerank-transport.ts`. `RemoteLlm` takes any implementation and validates its output the same way.
- **Worker:** `RerankBackend` in `worker/src/inference.ts`.

The options the benchmark has to choose between:

1. **llama-server as it is.** Implemented. Simplest, if the numbers hold up.
2. **llama-server with QMD's prompt formatting.** Build the prompt ourselves to match node-llama-cpp's tokens. Only useful if the benchmark traces a gap to tokenization. Not implemented.
3. **A node-llama-cpp wrapper in the worker.** `rankAll` on the same library gives parity by construction, at the price of a native dependency in the worker. Not implemented.

## Other known differences from in-process

**Query expansion.** The worker sends QMD's exact prompt, grammar and sampling values to llama-server's chat route. A test pins those values to the installed SDK. Three things still differ:

- QMD formats the chat through node-llama-cpp's `LlamaChatSession`, and the server uses its own template engine, so the prompt the model sees could differ.
- QMD's penalty settings map onto the server's `presence_penalty` and `repeat_last_n` by name only. Whether they behave identically is untested.
- Sampling at temperature 0.7 is random in both, so identical output is not expected even in-process. The benchmark measures in-process against itself to give a baseline.

**Embeddings.** The server's `/v1/embeddings` route may normalize vectors where the in-process path may not. QMD's vector index uses cosine distance, which ignores length, so this should not change results. The benchmark reports the cosine between the two sides for the same text, which should be close to 1 apart from CPU and GPU rounding.

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

**The bearer token stays in one header.** Error messages are built from fixed text and validated codes, never from a response body or a caught error's message, because they reach job records and `/ready`. Per-call log lines hold only worker host and port, operation, batch size, duration, status and an error code. On the worker, the token is not passed to llama-server's environment.

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

The benchmark reports numbers and sets no thresholds. A run's results and the decision they support belong in this file.

CI does not run it yet, since CI configuration is outside this change. Whoever wires it in should gate the job on a worker URL being available and leave it out of the required checks.

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
| llama-server accepts these flags and routes | **Not run.** From upstream documentation; the fixture only reflects that understanding. |
| Scores match in-process QMD | **No evidence.** The in-process path was read, not measured. The benchmark has never run. |
| GPU loading, on CUDA or Metal | **Not run.** |
| The NSSM and launchd scripts work | **Not run for real.** Dry-run plans and ACL helpers are tested; see [what is unconfirmed](deployment.md#not-yet-confirmed). |
| Windows VRAM and macOS thermal readings | Parsing tested against sample output, not against real tools. |
