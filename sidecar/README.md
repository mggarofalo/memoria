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
$env:MEMORIA_QMD_SECRET = node -e "console.log(require('crypto').randomBytes(32).toString('base64url'))"
npm start
```

The process logs JSON lines to stdout. The first reranked query after start pays the model load (about 11 s on the desktop); `MEMORIA_QMD_WARMUP=1` (default) pays it at boot instead, and `/ready` reports when the models are resident.

### Configuration

Everything is an environment variable. See [`.env.example`](.env.example) for the full list.

| Variable | Default | Purpose |
|---|---|---|
| `MEMORIA_QMD_HOST` | `127.0.0.1` | Bind address. Use the desktop's LAN address for `Memoria.Api`. |
| `MEMORIA_QMD_PORT` | `8282` | Port. |
| `MEMORIA_QMD_SECRET` | unset | Bearer secret. Required unless bound to loopback. For development: services use the file below. |
| `MEMORIA_QMD_SECRET_FILE` | unset | Read the secret from this file at startup: one line of UTF-8, with surrounding whitespace ignored. Setting both this and `MEMORIA_QMD_SECRET` is an error. On POSIX the sidecar refuses to start if group or others have any access to the file (`chmod 600`). `MEMORIA_QMD_WORKER_TOKEN_FILE` works the same way. |
| `MEMORIA_QMD_DB_PATH` | `~/.cache/qmd/index.sqlite` | Index to serve. Must exist. |
| `MEMORIA_QMD_KEEP_WARM` | `1` | Disable the SDK's 5-minute idle model unload. |
| `MEMORIA_QMD_WARMUP` | `1` | Load models at startup. |
| `MEMORIA_QMD_LOG_LEVEL` | `info` | pino level. |
| `MEMORIA_QMD_REINDEX_DEBOUNCE_MS` | `60000` | Quiet period after a document write before its index update runs. `0` turns the debounce off. |
| `MEMORIA_QMD_REINDEX_MAX_WAIT_MS` | `300000` | Longest an update can wait from its first write, so steady ingest cannot starve the index. |
| `MEMORIA_QMD_SHUTDOWN_DRAIN_MS` | `10000` | How long shutdown waits for queued jobs. Keep it under the service manager's stop timeout (15 s in `install-service.ps1`). |
| `MEMORIA_QMD_LLM` | `local` | `remote` sends model work to a worker. See [`docs/workers.md`](../docs/workers.md), which has the other worker settings and what is still unproven. |

### As a Windows service

[`scripts/install-service.ps1`](scripts/install-service.ps1) registers the sidecar with [NSSM](https://nssm.cc/) (`winget install NSSM.NSSM`) so it starts at boot and restarts on failure. **It has not been run on a real install yet.** Print the plan first, which needs no elevation and changes nothing, then run it from an elevated shell:

```powershell
.\scripts\install-service.ps1 -BindHost 192.168.1.20 -SecretFile .\secret.txt -CollectionRoot D:\corpus\sessions -WhatIf
.\scripts\install-service.ps1 -BindHost 192.168.1.20 -SecretFile .\secret.txt -CollectionRoot D:\corpus\sessions
```

It installs to `%ProgramFiles%\Memoria\memoria-qmd` and runs as `NT SERVICE\memoria-qmd`, with the secret in a file only that account and Administrators can read. Logs land in `%ProgramData%\memoria-qmd\logs` and rotate at 10 MiB. Pass every collection directory as `-CollectionRoot`, or document writes will fail. `scripts/uninstall-service.ps1` removes the service, its files and the secret, and leaves the logs. [`docs/deployment.md`](../docs/deployment.md#running-as-a-service) has the details, including what to do when upgrading an earlier install and what has yet to be confirmed.

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
| `GET /reindex/jobs`, `GET /reindex/jobs/{id}` | Job history, which persists across restarts. |
| `GET /queue`, `POST /queue/jobs/{id}:retry`, `POST /queue/jobs/{id}:cancel` | Queue depth, and operator control of jobs. |
| `GET /workers` | The worker pool's state, in remote LLM mode. |
| `GET /queue/dead-letters`, `POST /queue/dead-letters:clear` | Documents no worker could embed, and letting them try again. |
| `POST /cache:purge` | Drop every cached rerank score and expansion. |

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

A frontmatter block that does not close within the first 8192 characters has no metadata. `POST /documents` and a rescan read the same window, so they agree, and such a document does not match any filter.

### Document paths

The two routes follow different rules. A write has to produce a document QMD can index, under a name that means the same thing on every platform. A delete has to be able to remove whatever is there, including leaked content, so only safety applies to it. See [Deleting](#deleting).

`POST /documents` refuses these with 400 `invalid_path`:

- Empty, `.` or `..` segments, absolute paths, and any path that resolves outside the root.
- Segments that end in a dot, or start or end with whitespace. Windows strips some of these when it opens a file, so `.. /` would reach the filesystem as `../`.
- Windows reserved device names in any segment, in any case, with any extension: `CON`, `PRN`, `AUX`, `NUL`, `COM0`-`COM9`, `LPT0`-`LPT9`, the superscript forms, `CONIN$`, `CONOUT$`, `CLOCK$`.
- Any of `< > : " | ? *`, control characters, Unicode format characters (which include bidi overrides and zero-width characters), and lone surrogates.
- 8.3 short name shapes such as `hidden~1.md`, which on NTFS can be another name for `.hidden.md`. A `~` followed by anything other than digits and then a dot or the end of the segment is fine.
- Text not in Unicode normalization form NFC. NTFS keeps NFC and NFD spellings as two files, while APFS treats them as one.
- Segments over 255 bytes, and paths with more than 32 segments.
- Paths QMD will never index, because they would return 202 and then never appear: `node_modules`, `.git`, `.cache`, `vendor`, `dist` or `build` at any depth and in any case, and hidden names. A test pins this list to the installed SDK.
- A symlink or junction anywhere under the root, even one that points back inside it, since QMD's scan does not follow links.

A pattern miss returns 400 `pattern_mismatch`. Blank content returns 400 `empty_content`, because QMD skips a blank file and the previous text would stay searchable.

These return 409:

- `case_conflict`: the path differs only in case or normalization from an existing entry at some level. Use the existing name. Otherwise `KEEP.md` would overwrite `keep.md` on NTFS and APFS while the metadata index recorded the wrong key. This applies on case-sensitive hosts too.
- `hard_link`: overwriting a file with more than one hard link. Deleting one is allowed, because it removes that name only and leaves the file's other names alone.
- `not_a_directory`, `is_directory`, `exists`: what they say.
- `path_changed`: a directory on the path changed during the operation.

A write goes to a hidden temp file in the target's directory, is flushed, and is renamed over the target after containment has been checked again. A reader never sees half a document, and a hard link or symlink at the target is replaced rather than written through. An overwrite keeps the old file's POSIX permission bits. On Windows the new file takes its ACL from the directory, so an ACL set on the old file alone is lost. A delete renames the file aside and checks again before unlinking it.

#### Deleting

`DELETE /documents` does not apply the glob, the ignore patterns, the excluded directories, the segment limit or the name rules above, and an unsupported ignore pattern does not affect it. A document that stopped matching, sits under a rule added later, or has a name these rules would refuse today stays removable. This also means a delete can remove any file under the root that has no dot-prefixed segment, even one that was never a document, so the root should hold only the corpus.

Still refused or not reached:

| Path | Result | Why |
|---|---|---|
| Traversal, absolute paths, NUL, empty, `.` or `..` segments | 400 `invalid_path` | Safety, on every platform. Backslashes are separators everywhere, so `..\x` cannot be a way out. |
| A dot-prefixed segment at any depth, such as `.git/config` or `.hidden.md` | 400 `hidden_path` | QMD's scan drops these whatever the collection's pattern, so they cannot be indexed documents. A real-SDK test confirms it. Remove a leftover `.memoria-*.tmp` on the host. |
| A symlink or junction between the root and the file | 400 `invalid_path` | Containment. A link at the last segment is removed itself. |
| A case or normalization alias of an existing entry | 409 `case_conflict` | The exact name works, and is what keeps the metadata key right. |
| A name that is not a real directory entry, such as an 8.3 short name | 202, `deleted: false` | A directory listing does not show short names, so `hidden~1.md` cannot remove `.hidden.md`, but a file really called `notes~1.md` is removed. |

Names a delete cannot address, and what to do instead:

| Class | Why | Instead |
|---|---|---|
| On Windows: a name with `< > : " \| ? *` or a control character, ending in a dot or space, or a device name such as `NUL` or `CON.md`, returns 400 | Windows would read it as something else: `.. ` as `..`, a colon as a data stream, `NUL` as the device | Normally these cannot exist on NTFS. One made through a `\\?\` path can be removed the same way: `Remove-Item -LiteralPath "\\?\D:\corpus\NUL\x.md"` |
| On POSIX: a name with a literal backslash returns 202 with `deleted: false` | Backslashes are treated as separators, so `a\b.md` means `a/b.md` | Remove it with `rm`, then `POST /reindex` for the collection |
| A name that is not valid Unicode, on POSIX, returns 202 with `deleted: false` | Node cannot represent those bytes in a string | Remove it with `rm`, then `POST /reindex` |
| A path over 1024 characters returns 400 from the schema | The route's limit | Remove it on the host, then `POST /reindex` |

Remaining limits:

- Node has no `openat` or `renameat`, so each call resolves its path afresh. A local user with write access to the collection root can still win the gap inside the rename itself, between the kernel resolving the source and the destination. Swapping a directory at any earlier point makes the temp file's name unresolvable, and the write fails. A swap after the rename is reported as an error but cannot be undone. A swap at the moment the temp file is created can leave an empty file outside the root. A delete's rename aside can land on a file outside the root: it is put back when the swap is still in place, and otherwise stays under a `.memoria-*.tmp` name beside its old one, and a flip test reproduces this outcome. Restrict write access to collection roots to the service account.
- These are accepted limits: [`docs/deployment.md`](../docs/deployment.md#collection-root-permissions) states the boundary.

`overwrite: false` publishes with a hard link, which fails if the name is taken, so of several simultaneous creates exactly one succeeds and the rest get 409 `exists`. On a filesystem without hard links it returns 500 `exclusive_create_failed` rather than giving up exclusivity. `overwrite: true` does not need them.

Paths matching the collection's `ignore` patterns in QMD's configuration return 400 `ignored_path`, including a path below an ignored directory, as QMD's scan treats it. Patterns are read only from that configuration. Each compiles to a regex that runs on a request's path, so a pattern with an extglob, a leading `!`, or more than three wildcards makes every `POST` to that collection return 500 `unsupported_ignore_pattern` until it is simplified. It is neither run nor skipped. `DELETE` runs no pattern and is not affected.

A temp file is never indexed: QMD's scan skips hidden names whatever the collection's pattern, so a crash mid-write cannot put a partial document in search, `GET /docs` or multi-get. A write removes any `.memoria-*.tmp` over an hour old from its own directory, so a directory no write reaches keeps its leftovers. They are harmless and safe to delete. A crash in the moment between linking and unlinking leaves the document looking hard-linked, so overwriting it returns 409 `hard_link` until that sweep runs.

### Reindex scheduling

One worker runs one job at a time. Document writes do not start a scan each: the file lands on disk at once and the update waits until writes for that collection have been quiet for `MEMORIA_QMD_REINDEX_DEBOUNCE_MS`, so N documents cost one scan. The wait never exceeds `MEMORIA_QMD_REINDEX_MAX_WAIT_MS` from the first write.

- A waiting job's state is `queued`. Every write in the burst gets the same job back, and its `reason` counts them: `POST /documents claude-sessions/a.md (+24 more)`.
- A write that arrives while a job is running gets one follow-up job, because the running scan may already have passed the new file.
- If any write in a burst asks for embedding, the shared job embeds. An embed run sweeps every unembedded document in the collection, so a separate job would not have spared the others.
- `POST /reindex` does not wait. An identical job that is already waiting is returned and released to run.
- So a new document is not searchable, and a deleted one is not gone from search, until the job finishes. Call `POST /reindex` for the collection when that delay matters.
- On SIGINT or SIGTERM the sidecar releases held jobs and waits up to `MEMORIA_QMD_SHUTDOWN_DRAIN_MS` for them. Jobs persist in `index.sqlite.queue.sqlite` beside the index, so anything unfinished, including after a crash, runs after the next start. See [`docs/workers.md`](../docs/workers.md#the-job-queue).

## Develop

```bash
npm run typecheck         # tsc --noEmit
npm test                  # unit tests on an in-memory fake backend (no SDK load)
npm run openapi:check     # committed openapi.json matches the routes
MEMORIA_QMD_INTEGRATION=1 npm run test:integration   # real SDK on a temp fixture index, no models
MEMORIA_QMD_WORKER_URL=... npm run test:live         # sanity checks against a real worker
MEMORIA_QMD_WORKER_URL=... npm run bench:parity -- --db <index>   # in-process versus worker
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
| `src/queue-store.ts` | The queue's own SQLite file, beside the index. |
| `src/ingest.ts` | Path validation, file writes and deletes for `/documents`. |
| `src/secret-file.ts` | Reads a secret from `<NAME>_FILE`. Kept identical to the worker's copy by a test. |
| `src/path-rules.ts` | The names a document path may use, on every platform. |
| `src/multi-get.ts` | Splits a multi-get into the separate calls QMD can read, and merges the results. |
| `src/llm/` | Remote mode: `RemoteLlm`, the worker pool, the embedding farm, response validation and the rerank transport. Loaded only when `MEMORIA_QMD_LLM=remote`. |
| `bench/` | The parity benchmark and its query set. |
| `test/fake-backend.ts` | In-memory backend used by unit tests and OpenAPI generation. |
| `test/mock-worker.ts` | Scripted worker over real HTTP. |

## Security notes

- QMD has no authentication and the corpus contains credentials. The sidecar refuses to start on a non-loopback address without a secret, and compares bearer tokens in constant time.
- `POST /documents` and `DELETE /documents` change files. Redaction is the caller's job and must happen before the call. Paths must stay inside the collection root and match the collection's glob, and the name rules below apply on every platform, so a path valid on one host is valid on the others. See [Document paths](#document-paths).
- Write access to a collection root is trust in that account. The checks below shrink what a local user can do by racing the sidecar, but do not remove it (see [Document paths](#document-paths)).
- The sidecar is not a public surface. `Memoria.Api` is the only intended caller; expose nothing beyond the LAN.

## Dependency audit

`npm audit --omit=dev` reports no findings, and there are no accepted risks. Getting there takes three `overrides` in `package.json`, because QMD 2.8.3, the latest release, pins exact versions of vulnerable packages. npm's own suggestion of downgrading to QMD 2.5.3 fixes nothing: that release audits worse, with 7 findings including 2 critical and 4 high.

| Override | Replaces | Advisories | What QMD uses it for | Can the sidecar reach it? |
|---|---|---|---|---|
| `simple-git` 4.0.2, which brings `@simple-git/argv-parser` 2.0.1 | 3.36.0, 1.1.1 | GHSA-v5rq-49vh-5v5c | Cloning llama.cpp when `node-llama-cpp` has to build from source | The module loads with the local LLM runtime. The flaw is not reachable: it needs attacker-influenced values passed through simple-git's `.env()` and a git command that opens an editor, and `node-llama-cpp` does neither. |
| `@modelcontextprotocol/server` 2.4.0, which brings `core` 2.4.0 | 2.0.0 | GHSA-jm63-cq47-j5jp, GHSA-46jr-4wx8-v453, GHSA-rvq5-wwqv-78pq, GHSA-f897-62hf-6gxq | QMD's own MCP server | No. Only `dist/mcp/server.js` imports it, and only `qmd mcp` imports that file. The sidecar serves REST through Fastify. |
| `zod` 4.6.7 | 4.2.1 | GHSA-mrhx-c7xc-m3w5 | Tool schemas in the same MCP server | No, for the same reason. The sidecar validates requests with Fastify's JSON schemas. |

Three limits to keep in mind:

- **A clean audit does not harden `qmd mcp --http`.** The fix for GHSA-46jr-4wx8-v453 is an opt-in option, `maxToolInputElements`, that QMD does not set, so that memory-exhaustion flaw remains in QMD's HTTP transport even on the patched package. The sidecar never starts that transport. Do not start it either; see the invariants in `AGENTS.md`.
- **Overrides cover this package only.** A `qmd` CLI installed with `npm i -g @tobilu/qmd` keeps the vulnerable versions. `qmd update` and `qmd embed` load neither the MCP server nor zod, but never serve QMD's HTTP transport from that install.
- **Overrides outlive their reason.** They also hold back anything a later QMD asks for. On every QMD bump, remove all three, run `npm audit --omit=dev`, and put back only what the new release still needs. Then read what QMD does with each package again, since `simple-git` 4 is a major version above the `^3.36.0` that `node-llama-cpp` declares.
