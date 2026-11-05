# memoria-worker

Runs on a GPU machine and serves QMD's three models to the Memoria sidecar. It launches one `llama-server` per model, restarts them if they crash or hang, and puts a small authenticated HTTP API in front of them.

This has run on a Windows desktop with an RTX 5080 and llama.cpp b12110. [`docs/workers.md`](../docs/workers.md) has the measurements, the reranker decision, and what has not been verified. Start with the smoke script below.

## Requirements

- Node 22.18 or newer. The worker has no runtime npm dependencies and runs from source, so there is no install or build step.
- `llama-server` from [llama.cpp](https://github.com/ggml-org/llama.cpp), built for your GPU: CUDA on the desktop, Metal on a Mac.
- The three GGUF models, in QMD's cache (`~/.cache/qmd/models`) or at paths you set.

## Run it

```bash
cd worker
MEMORIA_WORKER_HOST=192.168.1.20 \
MEMORIA_WORKER_TOKEN=$(node -e "console.log(require('crypto').randomBytes(32).toString('base64url'))") \
MEMORIA_WORKER_LLAMA_SERVER=/path/to/llama-server \
npm start
```

Then check every endpoint against real models:

```bash
MEMORIA_WORKER_URL=http://192.168.1.20:8383 MEMORIA_WORKER_TOKEN=... npm run smoke
```

From the sidecar, `MEMORIA_QMD_WORKER_URL=... npm run test:live` runs further sanity checks through the adapter.

### As a service

The scripts are provided but have not been run on a real install yet.

**Windows desktop (NSSM).** The service restarts on failure and takes its `llama-server` children down with it. `-WhatIf` prints the plan, needs no elevation and changes nothing. The real run needs an elevated PowerShell.

```powershell
.\scripts\install-service.ps1 -BindHost 192.168.1.20 -LlamaServer "C:\Program Files\llama\llama-server.exe" -TokenFile .\token.txt -WhatIf
.\scripts\install-service.ps1 -BindHost 192.168.1.20 -LlamaServer "C:\Program Files\llama\llama-server.exe" -TokenFile .\token.txt
.\scripts\uninstall-service.ps1
```

It installs to `%ProgramFiles%\Memoria\memoria-worker` and runs as `NT SERVICE\memoria-worker`, with the token in a file only that account and Administrators can read. Logs land in `%ProgramData%\memoria-worker\logs` and rotate at 10 MiB. The script refuses a `llama-server` that anyone but administrators can change, because the service runs it. **GPU access under this account has not been confirmed.** [`docs/deployment.md`](../docs/deployment.md#running-as-a-service) has the details.

**Mac (launchd).** Installs a user agent, which runs while you are logged in. The token goes to a mode 600 file in `~/Library/Application Support/memoria-worker`, and the plist holds only its path. launchd does not rotate logs, so prune `~/Library/Logs/memoria-worker` yourself. The script has not been run on a Mac; its files and modes are checked by tests with `--dry-run`, which loads nothing.

```bash
MEMORIA_WORKER_TOKEN=... ./scripts/install-launchd.sh --host 192.168.1.30 --llama-server /opt/homebrew/bin/llama-server
./scripts/uninstall-launchd.sh
```

## Configuration

| Variable | Default | Purpose |
|---|---|---|
| `MEMORIA_WORKER_HOST` | `127.0.0.1` | Must be a literal loopback or private address. Wildcards, public addresses and host names are refused. |
| `MEMORIA_WORKER_PORT` | `8383` | Port. |
| `MEMORIA_WORKER_TOKEN` | unset | Bearer token. Required off loopback. At least 16 characters. For development: services use the file below. |
| `MEMORIA_WORKER_TOKEN_FILE` | unset | Read the token from this file at startup: one line of UTF-8, with surrounding whitespace ignored. Setting both is an error. On POSIX the worker refuses to start if group or others have any access to the file (`chmod 600`). |
| `MEMORIA_WORKER_LLAMA_SERVER` | `llama-server` | The binary. |
| `MEMORIA_WORKER_MODEL_DIR` | `~/.cache/qmd/models` | Where to find models. Honors `XDG_CACHE_HOME`. |
| `MEMORIA_WORKER_ROLES` | `embed,rerank,generate` | Which models this machine serves. |
| `MEMORIA_WORKER_<ROLE>_MODEL` | unset | Explicit model path for `EMBED`, `RERANK` or `GENERATE`. The sidecar rejects an embedding file with a different name from its index's. |
| `MEMORIA_WORKER_<ROLE>_URL` | unset | Use an already-running `llama-server` instead of launching one. |
| `MEMORIA_WORKER_<ROLE>_PARALLEL` | `4`, `2`, `1` | Slots for embed, rerank and generate. Also the worker's concurrency limit for that role. |
| `MEMORIA_WORKER_LLAMA_BASE_PORT` | `8384` | First of three loopback ports for the children. |
| `MEMORIA_WORKER_GPU_LAYERS` | `999` | `--n-gpu-layers`. `0` runs on CPU. |
| `MEMORIA_WORKER_WARMUP` | `1` | One small request per model after they load. |
| `MEMORIA_WORKER_MAX_QUEUE` | `256` | Requests that may wait per role before new ones get a 503. |
| `MEMORIA_WORKER_BODY_LIMIT_BYTES` | `33554432` | Largest request body. |
| `MEMORIA_WORKER_REQUEST_TIMEOUT_MS` | `120000` | Deadline for one call to `llama-server`. |
| `MEMORIA_WORKER_BUSY_LOAD_PER_CORE` | `0.9` | Report `busy` above this one-minute load per core. `0` disables. Windows has no load average, so this does nothing there. |
| `MEMORIA_WORKER_BUSY_VRAM_FREE_MB` | `0` | Report `busy` below this much free VRAM. `0` disables. |
| `MEMORIA_WORKER_BUSY_ON_THERMAL` | `1` | Report `busy` when macOS says the CPU is thermally limited. |

## The API

Every route needs `Authorization: Bearer <token>` when a token is set. Errors are `{ "error": "<code>", "message": "..." }`, and never repeat request content or `llama-server` output.

| Route | Body | Returns |
|---|---|---|
| `GET /health` | | `status` (`ok`, `busy`, `starting`), `busyReasons`, per-model `loaded`, `state` and `restarts`, `inFlight`, `queueDepth`, `vram`, `loadPerCore`, `cpuSpeedLimit` |
| `GET /capabilities` | | `operations`, model file names, `rerankTransport` |
| `POST /v1/embed` | `{ texts }`, up to 256 | `{ embeddings, model }`. The sidecar refuses vectors whose `model` is not its index's. |
| `POST /v1/rerank` | `{ query, documents }`, up to 1000 | `{ results: [{ index, score }], transport }` |
| `POST /v1/complete` | `{ prompt, grammar?, maxTokens?, temperature?, topK?, topP?, repeatLastN?, presencePenalty? }` | `{ text }` |
| `POST /v1/tokenize` | `{ text }` | `{ tokens }` |
| `POST /v1/detokenize` | `{ tokens }` | `{ text }` |

`busy` is advice for a dispatcher: the worker keeps serving requests. A role the machine does not serve returns 501, and a model still loading returns 503.

Text that would not fit the model's context is shortened by QMD's rules before it reaches `llama-server`. Rerank documents are cut to 4096 tokens, less 512 for the template, less the query. Embedding text over 2048 tokens is cut to 2044.

Models stay loaded for as long as the worker runs. Nothing unloads them when idle.

## Develop

`npm test` here runs the worker's tests. The sidecar's `npm test` and `npm run typecheck` also cover this directory, which is how CI reaches it. Run `npm install` here only if you want the standalone `npm run typecheck`.

Tests launch `test/fixtures/fake-llama-server.ts` as a real child process in place of `llama-server`. That proves supervision, routing and auth, and says nothing about real models.

| File | Role |
|---|---|
| `src/main.ts` | Process entry: resolve models, supervise, listen, warm up, stop. |
| `src/config.ts` | Environment variables. |
| `src/secret-file.ts` | Reads a secret from `<NAME>_FILE`. Kept identical to the sidecar's copy by a test. |
| `src/supervisor.ts` | Launches and restarts `llama-server`, with its flags. |
| `src/inference.ts` | The operations, against `llama-server`'s API. `RerankBackend` is the rerank seam. |
| `src/server.ts` | HTTP routes, auth, validation, concurrency limits. |
| `src/probes.ts` | Load, thermal and VRAM readings, and the `busy` policy. |
| `scripts/smoke.ts` | Exercises every endpoint of a running worker. |

## Requests

`POST` bodies must be sent as `application/json`, or the worker answers 415. A browser can send `text/plain` to any address with no preflight, and cannot send this. Any `Origin` gets a 403, and a host name not in `MEMORIA_WORKER_ALLOWED_HOSTS` gets a 421, though an IP address and `localhost` always work. Between hosts the worker runs on a WireGuard tunnel, speaking plain http with the tunnel doing the encrypting, so binding a tunnel address needs `MEMORIA_WORKER_ALLOW_HTTP_ON_PRIVATE_NETWORK=1`, or `-AllowHttpOnPrivateNetwork` or `--allow-http-on-private-network` on the installers: see [`docs/deployment.md`](../docs/deployment.md).
