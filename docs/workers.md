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
| `MEMORIA_QMD_WORKER_URL` | unset | Required in remote mode. Must not contain credentials. |
| `MEMORIA_QMD_WORKER_TOKEN` | unset | Required unless the worker is on loopback. At least 16 characters. `MEMORIA_QMD_WORKER_TOKEN_FILE` reads it from a file instead, which is what a service should use. |
| `MEMORIA_QMD_LLM_FALLBACK` | `1` | Tokenize and embed queries on this host's CPU when no worker answers. Needs the embedding model on this host. |
| `MEMORIA_QMD_EMBED_DIMENSIONS` | `768` | Every embedding must have this length. `0` pins it to the first response. |

Setting up the worker itself is covered in [`worker/README.md`](../worker/README.md).

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

**The embedding model is checked.** Before embedding, the sidecar asks the worker which file it serves and refuses a mismatch, because mixed vectors would corrupt search with no visible error.

**Rerank has no fallback.** With no worker, a reranked search fails with a 500. The SDK caches whatever score it receives, so a placeholder would be replayed after the worker recovers. Callers can pass `rerank: false`, and lexical search needs no worker at all.

**Document embedding has no CPU fallback.** Only query embedding does. A corpus embed on CPU could run for hours while looking healthy. Failed chunks stay pending, and the SDK's failure accounting reports them.

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
| llama-server accepts these flags and routes | **Not run.** From upstream documentation; the fixture only reflects that understanding. |
| Scores match in-process QMD | **No evidence.** The in-process path was read, not measured. The benchmark has never run. |
| GPU loading, on CUDA or Metal | **Not run.** |
| The NSSM and launchd scripts work | **Not run for real.** Dry-run plans and ACL helpers are tested; see [what is unconfirmed](deployment.md#not-yet-confirmed). |
| Windows VRAM and macOS thermal readings | Parsing tested against sample output, not against real tools. |
