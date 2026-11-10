# End-to-end checks

This runs the real `memoria` CLI against a real `Memoria.Api` and a real `memoria-qmd` sidecar. It exists because fakes share the tests' assumptions. Its first run found that `memoria get collection/path.md` returned 404 from a real API, while every test against a fake server passed.

```sh
(cd sidecar && npm ci)
node scripts/e2e/run.ts              # builds the CLI, API and sidecar first
node scripts/e2e/run.ts --no-build   # reuses a built API and sidecar
node scripts/e2e/run.ts --no-build --only-ingest   # only the second server: ingest, sources and migrate, for running them many times
```

## The deployed layout

`node scripts/e2e/compose.ts` runs `deploy/compose.yaml` as it ships, on both images built from this checkout, with the sidecar tests' scripted worker in a container standing in for a remote host. It checks what the process-based suite cannot: that the sidecar publishes no port and shares its network with the API alone, holds no llama.cpp binaries, answers degraded search with every worker down, finishes a waiting embed once a worker answers, and keeps its data across a restart. The suite above runs a recording proxy between the API and the sidecar for its own checks, which is what this layout forbids, so it does not run on it. This needs Docker, and removes its containers, volumes and images when it ends.

### The suite on the compose layout

`node scripts/e2e/run.ts --layout compose` runs the ingest server's checks against `deploy/compose.yaml` as shipped, from images built from this checkout. A test-only override, written into the run's private directory and never shipped, puts the corpus, index, control database and stall file there for the checks to read, gives the API the run's port, and runs both containers as the current user. Containers and the project are named `sc-e2e`, and the run removes them, their volumes and both images.

Three groups say NOT RUN there, as they need what this layout forbids: the connection checks and sources sync steer or read the recording proxy, and one ingest check reads what crossed it. The first server's checks are of the API's own surface and connect from chosen loopback addresses, which a published container port rewrites, so they run in the process layout only.

## Running the suite

It needs Node 26.13+, Go and the .NET SDK. It exits non-zero if any check fails. It connects from loopback addresses such as `127.0.0.2`, which Linux and Windows answer without setup. macOS does not, so the auth checks fail there.

## What it touches

- **No real keys.** Every key comes from the throwaway server it starts, and printed output is scrubbed of them.
- **No real index, and no models.** The real QMD SDK builds a small index from two fixture collections. The sidecar runs with `MEMORIA_QMD_LLM=remote`, `MEMORIA_QMD_LLM_FALLBACK=0`, and a worker address nothing listens on. Do not run it in local mode: a plain query can reach QMD's expansion step, which starts the LLM runtime and tries to fetch a model, and under `CI=true` it fails instead. The run ends by checking that the sidecar logged remote mode and that no model file reached its private cache.
- **One private directory.** A new temp directory, mode 0700, holds the index, control database, logs and config. Home and config variables point inside it for every process, and it is deleted at the end, pass or fail.
- **Loopback only**, on free ports. The hosts run from their output directory, so they read the shipped `appsettings.json`.
- **One exception, the OS keyring.** A file-backend login records its server in the keyring when one is available. This uses the profile `memoria-e2e-test`, holds no key, and is removed by the logout that follows. On a host with no keyring, such as CI, nothing happens.

## What it does not cover

With no worker, there is no query expansion, reranking or vector search, so queries are the sidecar's degraded BM25 search and match one document. It runs plain http, not TLS, and does not exercise a prompt in a real terminal.

## The secret proof

`ingest.ts` is the most important check here. On a server of its own, a document and a session holding credentials go in through the real routes. Each token is built at run time from random bytes, so the source holds none and none can be printed. Then every route that can read returns only redacted text: REST search, `GET /v1/docs` by id and by path, multi-get, and MCP search and get. Searching for each token finds nothing, no file of the run holds one in its raw bytes, including the index, the control database, their write-ahead logs, and the logs, and no write reaching the sidecar holds one.

The same file then proves the operator's off switch against the collection next door, deletes against a 10-minute debounce, scope, shrinking, and who owns a file. Long-running proofs of limits and memory are not here, because they would take the run past its time limit.

## Adding checks

Add a function to `checks.ts` or to the file for its area (`auth-*.ts`, `ingest-*.ts`, `migrate*.ts`, `sources.ts`, `ops.ts` for workers, the queue, reindex and readiness), and call it from `run.ts`, before `rateLimits`. Only the final scans, `noModels` and `secretsAbsent`, may follow it, because it uses up the failed-auth limit for the address.

Keep checks deterministic. The rate limits are fixed and their windows are 30 minutes, so a 429 arrives on an exact request however slow the machine is, and `rateLimits` uses its own key. Other checks can make up to 100 requests with one key before hitting the limit.

To learn what the sidecar was actually asked, read `h.proxied()`, as `traversal` does. `proxy.ts` sits between the API and the sidecar, records each call's exact URL before forwarding it, and through `h.planProxy` can answer the next batch call as a sidecar that breaks its limits. Use `h.request` to send exact path bytes, since `fetch` and `URL` normalize them, and its `from` option to choose the peer address. Each peer has its own failed-auth budget, and only `TRUSTED_PEER` is believed about `X-Forwarded-For`. Pass any new key to `h.remember`, so that it cannot be printed, and it joins the final scan of logs, the audit table, the control database and its write-ahead log, and every response body.

A check that something did not happen must also see something that did. If a check says "no stray call" or "no row", send a marker after it, and require the marker in the same record, as `traversal` and `deniedAndAnonymous` do. Otherwise it passes when the record is empty, which has happened here: a renamed log field once left the traversal checks passing while seeing nothing.

`ingest-connection.ts` holds writes the API has lost control of: `stall-writes.mjs` is loaded into the sidecar to hold a write at a chosen moment, the proxy loses answers and passes on closed connections, and the API is really killed.
