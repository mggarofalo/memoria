# memoria-qmd

A thin REST service over the [QMD](https://github.com/tobi/qmd) SDK. It runs on the machine that holds the GGUF models and `index.sqlite`, loads the index once, keeps the models warm, and exposes the retrieval, document, collection, ingest and reindex operations that `Memoria.Api` needs over plain HTTP.

It replaces the earlier plan of proxying QMD's MCP HTTP transport. That transport binds `localhost` only, needs an `Mcp-Session-Id` handshake, and offers no frontmatter filtering. Importing the SDK as a library removes all three problems.

Retrieval itself is untouched: BM25 and vector search, RRF fusion, AST chunking and LLM reranking all stay inside QMD.

## Run it

Requirements: Node 22.18 or newer, an existing QMD index (`qmd update` has been run on this host), and the three models in QMD's cache.

```bash
cd sidecar
npm ci
npm run build
MEMORIA_QMD_SECRET=$(node -e "console.log(require('crypto').randomBytes(32).toString('base64url'))") npm start
```

On Windows PowerShell:

```powershell
cd sidecar
npm ci
npm run build
$env:MEMORIA_QMD_SECRET = "a-long-random-string-at-least-16-chars"
npm start
```

The process logs JSON lines to stdout. The first reranked query after start pays the model load (about 11 s on the desktop); `MEMORIA_QMD_WARMUP=1` (default) pays it at boot instead, and `/ready` reports when the models are resident.

### Configuration

Everything is an environment variable. See [`.env.example`](.env.example) for the full list.

| Variable | Default | Purpose |
|---|---|---|
| `MEMORIA_QMD_HOST` | `127.0.0.1` | Bind address. Use the desktop's LAN address for `Memoria.Api`. |
| `MEMORIA_QMD_PORT` | `8282` | Port. |
| `MEMORIA_QMD_SECRET` | unset | Bearer secret. Required unless bound to loopback. |
| `MEMORIA_QMD_DB_PATH` | `~/.cache/qmd/index.sqlite` | Index to serve. Must exist. |
| `MEMORIA_QMD_KEEP_WARM` | `1` | Disable the SDK's 5-minute idle model unload. |
| `MEMORIA_QMD_WARMUP` | `1` | Load models at startup. |
| `MEMORIA_QMD_LOG_LEVEL` | `info` | pino level. |
| `MEMORIA_QMD_REINDEX_DEBOUNCE_MS` | `60000` | Quiet period after a document write before its index update runs. `0` turns the debounce off. |
| `MEMORIA_QMD_REINDEX_MAX_WAIT_MS` | `300000` | Longest an update can wait from its first write, so steady ingest cannot starve the index. |
| `MEMORIA_QMD_SHUTDOWN_DRAIN_MS` | `10000` | How long shutdown waits for queued jobs. Keep it under the service manager's stop timeout (15 s in `install-service.ps1`). |

### As a Windows service

[`scripts/install-service.ps1`](scripts/install-service.ps1) registers the sidecar with [NSSM](https://nssm.cc/) (`winget install NSSM.NSSM`) so it starts at boot and restarts on failure. Run it from an elevated shell:

```powershell
.\scripts\install-service.ps1 -Host 192.168.1.20 -Secret $env:MEMORIA_QMD_SECRET
```

Logs land in `%ProgramData%\memoria-qmd\logs`. `scripts/uninstall-service.ps1` removes it.

## The contract

The full OpenAPI 3.1 document is committed at [`openapi.json`](openapi.json) and served at `GET /openapi.json`. `Memoria.Api` should generate its client from the committed file. Regenerate it with `npm run openapi`; CI fails if it drifts from the routes.

Every route except `/health` and `/openapi.json` requires `Authorization: Bearer <MEMORIA_QMD_SECRET>`. Errors are `{ "error": "<code>", "message": "...", "details"?: ... }`.

| Route | What it does |
|---|---|
| `GET /health` | Liveness. No auth, no index access. |
| `GET /ready` | Index open, model residency, warmup state, pending jobs. `?requireModels=true` returns 503 until the models are loaded. |
| `GET /status` | QMD's index status. |
| `POST /search` | Hybrid search. Either `query` (auto-expanded) or `searches` (typed `lex`/`vec`/`hyde` sub-queries), plus `intent`, `collections`, `limit`, `minScore`, `candidateLimit`, `rerank`, and frontmatter `filters`. |
| `GET /docs/{reference}` | One document by `collection/path`, `qmd://` URI or docid, with `:from` / `:from:count` line-range suffixes. |
| `POST /docs:multiGet` | Several documents by glob and/or a list of references. Each selector resolves separately; results merge, one entry per file. |
| `GET /collections` | Name, root, pattern, document counts, root context. |
| `POST /documents` | Ingest by value: writes markdown into the collection directory and queues a debounced update (and embed) job. Returns 202. |
| `DELETE /documents?collection=&path=` | Delete by value: removes the file and queues a debounced update. Idempotent; returns 202 with `deleted: false` when no file existed. |
| `POST /reindex` | Queue `update` (+ `embed`) for some or all collections, without waiting on the debounce. One worker runs one job at a time. |
| `GET /reindex/jobs`, `GET /reindex/jobs/{id}` | Job history for this process. |

### Search example

```json
POST /search
{
  "searches": [
    { "type": "lex", "query": "AuthenticationHandler" },
    { "type": "vec", "query": "how should API key auth work" }
  ],
  "intent": "asp.net core api key auth",
  "collections": ["claude-sessions"],
  "filters": { "project": "Receipts", "since": "2026-01-01" },
  "limit": 10,
  "rerank": true
}
```

```json
{
  "results": [
    {
      "docid": "f6feba",
      "file": "claude-sessions/2026-02-11_2e39c784.md",
      "collection": "claude-sessions",
      "path": "2026-02-11_2e39c784.md",
      "title": "...",
      "score": 0.91,
      "context": null,
      "line": 42,
      "snippet": "...",
      "metadata": { "project": "Receipts", "branch": "master", "date": "2026-02-11", "startTime": "...", "tags": ["project/Receipts", "branch/master"] }
    }
  ],
  "total": 1,
  "tookMs": 3700,
  "query": { "collections": ["claude-sessions"], "rerank": true, "filtered": true, "fetched": 40, "limit": 10 }
}
```

### Metadata filters

QMD has no notion of frontmatter, so the sidecar keeps a small in-memory index of each document's YAML header (`project`, `git_branch`, `date`, `start_time`, `end_time`, `tags`, `session_id`, `source`), built from the SQLite index at startup and refreshed after every reindex job. Filters are applied **after** retrieval: a filtered search asks QMD for `limit × MEMORIA_QMD_FILTER_OVERFETCH` candidates (default 4×, capped at 200), drops the ones that fail, and returns up to `limit`. A filtered result set can therefore be shorter than `limit` even when more matches exist deeper in the ranking. Documents with no frontmatter never match an active filter.

### Reindex scheduling

One worker runs one job at a time. Document writes do not start a scan each: the file lands on disk at once and the update waits until writes for that collection have been quiet for `MEMORIA_QMD_REINDEX_DEBOUNCE_MS`, so N documents cost one scan. The wait never exceeds `MEMORIA_QMD_REINDEX_MAX_WAIT_MS` from the first write.

- A waiting job's state is `queued`. Every write in the burst gets the same job back, and its `reason` counts them: `POST /documents claude-sessions/a.md (+24 more)`.
- A write that arrives while a job is running gets one follow-up job, because the running scan may already have passed the new file.
- If any write in a burst asks for embedding, the shared job embeds. An embed run sweeps every unembedded document in the collection, so a separate job would not have spared the others.
- `POST /reindex` does not wait. An identical job that is already waiting is returned and released to run.
- So a new document is not searchable, and a deleted one is not gone from search, until the job finishes. Call `POST /reindex` for the collection when that delay matters.
- On SIGINT or SIGTERM the sidecar releases held jobs and waits up to `MEMORIA_QMD_SHUTDOWN_DRAIN_MS` for them. Jobs are not persisted: after a crash the files are safe on disk but stay unindexed until the next scan of that collection.

## Develop

```bash
npm run typecheck         # tsc --noEmit
npm test                  # unit tests on an in-memory fake backend (no SDK load)
npm run openapi:check     # committed openapi.json matches the routes
MEMORIA_QMD_INTEGRATION=1 npm run test:integration   # real SDK on a temp fixture index, no models
npm run dev               # node src/main.ts against the local index
```

Tests run on Node's built-in runner with native TypeScript type stripping, so there is no transpile step for `npm test`. `npm run build` emits `dist/` for the service.

Layout:

| File | Role |
|---|---|
| `src/main.ts` | Process entry: config, open index, warm, listen, shutdown. |
| `src/app.ts` | Builds the Fastify app: auth hook, error envelope, swagger, job runner. |
| `src/routes.ts` | Every route and its JSON schema. |
| `src/store.ts` | The `QmdBackend` interface and the only import of `@tobilu/qmd`. |
| `src/metadata.ts` | Frontmatter parsing, filter matching, the in-memory metadata index. |
| `src/jobs.ts` | Single-worker reindex queue with an injectable debounce clock. |
| `src/ingest.ts` | Path validation, file writes and deletes for `/documents`. |
| `src/multi-get.ts` | Splits a multi-get into the separate calls QMD can read, and merges the results. |
| `test/fake-backend.ts` | In-memory backend used by unit tests and OpenAPI generation. |

## Security notes

- QMD has no authentication and the corpus contains credentials. The sidecar refuses to start on a non-loopback address without a secret, and compares bearer tokens in constant time.
- `POST /documents` and `DELETE /documents` change files. Paths are normalised, must stay inside the collection root, and must match the collection's glob. Symlinks and junctions cannot be followed out of the root, and segments that Windows would silently rewrite (trailing dots and spaces, colons) are refused on every platform. Redaction is the caller's job and must happen before the call.
- The sidecar is not a public surface. `Memoria.Api` is the only intended caller; expose nothing beyond the LAN.
