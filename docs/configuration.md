# Configuring Memoria.Api

Everything the host needs lives under one `Memoria` section. It binds through
the standard ASP.NET Core configuration stack, so each key can come from
`appsettings.json`, `appsettings.{Environment}.json`, `appsettings.local.json`
(gitignored, for machine-local values), an environment variable, or a
command-line switch, in that order of precedence: the local file overrides the
committed files, and environment variables and switches override everything. Environment variables use `__` as the separator:
`Memoria__Sidecar__Secret`.

Invalid configuration fails at startup with a message naming the key. The API
never starts half-configured.

## Routes

`/openapi/v1.json` is the contract; this lists its operations. The MCP endpoint is at `Memoria:Mcp:Path`. [Scopes](#scopes) says which key reaches which, and `DocsMatchCodeTests` keeps this list and the key tables below true to the code.

| Route | What it does |
|---|---|
| `GET /health` | Liveness. Anonymous. |
| `GET /ready` | Readiness. Anonymous; see [Anonymous routes](#anonymous-routes). |
| `GET /v1/search` | Search the corpus. |
| `GET /v1/docs/{id}` | Get one document. |
| `POST /v1/docs:multiGet` | Get several documents. See [Batch selector limits](#batch-selector-limits). |
| `GET /v1/collections` | List collections. |
| `GET /v1/status` | Index status. |
| `POST /v1/documents` | Store a document. |
| `DELETE /v1/documents` | Delete a document. See [Deleting](#deleting). |
| `POST /v1/ingest/session` | Store a raw session transcript. |
| `GET /v1/ingest/session` | How a session's upload ended. |
| `POST /v1/ingest/rendered` | Store an already rendered document. |
| `GET /v1/ingest/rendered` | What is recorded for a rendered document's path. |
| `POST /v1/admin/keys` | Issue an API key. See [Keys](#keys). |
| `GET /v1/admin/keys` | List API keys. |
| `DELETE /v1/admin/keys/{id}` | Revoke an API key. |
| `POST /v1/admin/keys/{id}:rotate` | Rotate an API key. |
| `GET /v1/admin/workers` | List inference workers. See [Operations](#operations). |
| `POST /v1/admin/workers/{name}:drain` | Take a worker out of rotation. |
| `POST /v1/admin/workers/{name}:enable` | Put a drained worker back. |
| `GET /v1/admin/queue` | Background queue status and recent jobs. |
| `POST /v1/admin/queue:pause` | Pause background work. |
| `POST /v1/admin/queue:resume` | Resume background work. |
| `POST /v1/admin/queue/jobs/{id}:retry` | Retry a failed job. |
| `POST /v1/admin/queue/jobs/{id}:cancel` | Cancel a job. |
| `POST /v1/admin/reindex` | Queue a reindex. |

## `Memoria:Sidecar` — reaching memoria-qmd

| Key | Default | Purpose |
|---|---|---|
| `BaseUrl` | `http://127.0.0.1:8282` | Where the sidecar listens. |
| `Secret` | unset | The sidecar's `MEMORIA_QMD_SECRET`, sent as `Authorization: Bearer`. **Required when `BaseUrl` is not loopback.** Set it through the environment or `appsettings.local.json`; never commit it. |
| `Timeout` | `00:02:00` | Per-request ceiling. A reranked search on a cold sidecar can take 15 s or more; leave headroom. |
| `ReadinessTimeout` | `00:00:05` | Ceiling for the `/ready` probe only. Probes must fail fast so an orchestrator notices. |
| `ReadinessCacheTime` | `00:00:02` | How long one answer from the sidecar's `/ready` serves every caller of the API's. `/ready` is anonymous, and each probe makes the sidecar scan the index, so callers in that time share one, and so do callers while one is in flight. A failure is shared too. A caller with an admin key probes on its own instead, so the sidecar logs the call under that request's trace id; its key's rate limit bounds it. `00:00:00` shares only a probe in flight. At most a minute. |

## `Memoria:Search` — defaults for `GET /v1/search`

| Key | Default | Purpose |
|---|---|---|
| `DefaultLimit` | `10` | Used when the caller omits `limit`. |
| `MaxLimit` | `100` | Largest `limit` accepted; the sidecar caps at 100 regardless. |
| `MaxMultiGetBytes` | `16777216` | Total bytes of document bodies in one `POST /v1/docs:multiGet` response (16 MiB). The sidecar stops reading at this budget and lists the rest under `skipped`. At most 256 MiB. |
| `RerankByDefault` | `true` | Full reranked hybrid search unless the caller passes `rerank=false`. Reranked queries were benchmarked as viable on a GPU host. |
| `DefaultCollections` | unset | Collections searched when the caller names none. Unset defers to QMD's default-included set. |

## `Memoria:Mcp` — the outward MCP server

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | `true` | Serve the MCP endpoint. `false` leaves only REST. |
| `Path` | `/mcp` | Route of the Streamable HTTP endpoint. Must start with `/` and must not sit under `/v1`. |

The server runs stateless: no session ids, no server-to-client requests, so any
number of Claude Code sessions on any machine share one endpoint without
affinity. Tools: `search`, `get_document`, `get_documents`, `list_collections`,
`status`. They go through the same request factory and collection scope as the
REST endpoints, so a tool call and a `curl` see identical defaults. The endpoint
requires an API key with the `read` scope, and each tool call sees that key's
collections.

Point Claude Code at it from any machine on the LAN:

```bash
claude mcp add --transport http memoria http://<api-host>:8080/mcp \
  --header "Authorization: Bearer <key>"
```

## `Memoria:Auth` — API keys, rate limits, audit

| Key | Default | Purpose |
|---|---|---|
| `Mode` | `ApiKey` | `ApiKey` requires a key on every `/v1` and MCP route. `Disabled` turns authentication off for development on a trusted LAN; it must be set explicitly and logs a loud warning at startup. The value must be exactly one of those two, case-sensitive. Anything else stops the host, including `1`, `disabled`, ` Disabled `, `ApiKey,Disabled`, and a key that is set but empty. |
| `DatabasePath` | unset | The SQLite control database holding keys and audit records. **Required in `ApiKey` mode**: without it the host refuses to start. It is durable state, separate from QMD's index, and belongs on a local volume (SQLite's WAL mode does not work over a network share). Not needed in `Disabled` mode. |
| `RateLimit:Enabled` | `true` | Enforce per-key limits. |
| `RateLimit:PermitLimit` | `120` | Requests one key may make per window, counting ones that end in `403`. Over the limit: `429` with `Retry-After`. |
| `RateLimit:Window` | `00:01:00` | Length of the fixed window. |
| `TrustedProxies` | empty | Proxies whose `X-Forwarded-For` is believed, as IPs or CIDR ranges. Empty means the socket address is the client and the header is ignored, and loopback is not trusted implicitly. A `/0` range or an unparseable entry stops the host. See [the reverse proxy section](deployment.md#reverse-proxy-and-tls): behind a proxy this must be set. |
| `FailedAuth:Enabled` | `true` | Limit failed authentication per client address. |
| `FailedAuth:PermitLimit` | `20` | Failed attempts one address may make per window. Past it, a credential from that address gets `429` and `Retry-After` with no database lookup until the window ends, unless its secret matches a key this process already knows. Requests that authenticate spend nothing. An IPv6 client is counted by its /64. |
| `FailedAuth:Window` | `00:01:00` | Length of the fixed window. |
| `FailedAuth:MaxTrackedAddresses` | `10000` | Addresses held in memory. A flood of distinct addresses can make the limiter forget an address early, never grow past this. |
| `Audit:IncludeQueries` | `false` | Also record search queries and document references in the audit log. Queries can be as sensitive as the corpus, so this is off unless you opt in. Document content is never recorded. |
| `Audit:RetentionDays` | `90` | Days to keep audit records in the database. Zero or less keeps them forever. |
| `Audit:MaxRowsPerKey` | `100000` | Records kept per key, newest first and within 10% or 1000 rows of the cap, whichever is less, so a burst from one key cannot grow the table without bound or evict another key's history. The evicted rows remain in the log stream. |

### Keys

A key is `mem_<id>_<secret>`, sent as `Authorization: Bearer <key>`. The id is
public and appears in logs and the audit trail. The secret is 256 random bits,
exists in plaintext only in the response that issued it, and is stored only as
a SHA-256 hash. Revocation and rotation take effect on the key's next request.

Create the first admin key with the [bootstrap command](deployment.md#the-first-admin-key),
then manage keys over REST with that key:

| Route | Does |
|---|---|
| `POST /v1/admin/keys` | Issue a key. Body: `label`, `scopes`, then `collections` or `allCollections: true`, and optionally `expiresAt`. |
| `GET /v1/admin/keys` | List keys, with no key material. |
| `DELETE /v1/admin/keys/{id}` | Revoke. |
| `POST /v1/admin/keys/{id}:rotate` | New secret, same id, scopes and expiry. The old token stops working at once. Revoked or expired keys get `409`. |

```bash
curl -s -X POST http://localhost:8080/v1/admin/keys \
  -H "Authorization: Bearer $ADMIN_KEY" -H "Content-Type: application/json" \
  -d '{"label":"laptop","scopes":["read"],"collections":["claude-sessions"]}' | jq -r .key
```

### Scopes

Scopes do not imply one another.

| Scope | Allows |
|---|---|
| `read` | Search, documents, collections, status, and the MCP endpoint. |
| `ingest` | The ingest routes: `/v1/documents`, `/v1/ingest/session` and `/v1/ingest/rendered`. |
| `admin` | Key management, [operations](#operations) on the workers and the queue, and backend detail: the full `/ready` report and how the backend failed. |

`admin` does **not** imply `read`. A key used to manage keys is not a key that
reads the corpus, so day-to-day clients never carry key-management power and a
bootstrapped key reads nothing. An admin can issue itself a reader, but that
leaves an audit record and a separate key to revoke. Give a key both scopes if
you want both.

### Operations

The routes under `/v1/admin` other than `/v1/admin/keys` operate the backend. Each one proxies a sidecar admin route over the shared-secret channel, so the sidecar holds the state and the API decides who may change it.

| Route | Does |
|---|---|
| `GET /v1/admin/workers` | Each worker's state, circuits, load, error rate and whether it is drained. Empty in local LLM mode. Never includes tokens. |
| `POST /v1/admin/workers/{name}:drain` | Take the worker out of rotation. Calls in flight finish, and it gets no new ones. `404` with code `unknown_worker` for a name not in the pool. |
| `POST /v1/admin/workers/{name}:enable` | Put it back. |
| `GET /v1/admin/queue` | Jobs by state, whether the queue is paused and persistent, documents set aside, model calls by priority class, and the jobs the sidecar remembers, newest first. |
| `POST /v1/admin/queue:pause` | Start no more background jobs. A running job runs to its end. Writes still queue, and until the queue resumes new documents are not searchable and deleted ones still are. |
| `POST /v1/admin/queue:resume` | Let waiting jobs start. |
| `POST /v1/admin/queue/jobs/{id}:retry` | Queue a failed job again under the same id. `409` unless it failed. |
| `POST /v1/admin/queue/jobs/{id}:cancel` | Stop a job that has not finished. `409` if it has. |
| `POST /v1/admin/reindex` | Rescan and, unless `embed` is `false`, embed. Body: optional `collections`, `embed` and `force`. Omitting `collections` means every collection; an empty list is refused. |

**Who may call them.** These act on every collection at once, so they need the `admin` scope and a key that is not bound to particular collections: an admin-only key, or an admin key with `allCollections: true`. A key that lists collections gets `403` with code `collection_scoped_key`, even with the `admin` scope, and a key without `admin` gets `403`. Both are decided before the request body is read, so a body that would not parse gets the same `403`, and neither reaches the sidecar. A body sent as anything but JSON selects no operation at all: routing answers `415` to a valid key and `401` to none, and the operation never runs. Like key management, they are unavailable when `Memoria:Auth:Mode` is `Disabled`. Every call is audited under its operation name, such as `drainWorker`.

**What lasts.** Drain and pause are held in the sidecar's memory, not in the API or on disk: a sidecar restart puts every worker back and resumes the queue. To keep a worker out, remove it from the sidecar's configuration ([docs/workers.md](workers.md#the-configuration-file)). A worker name or job id the sidecar could not hold is refused with `400` before the sidecar is called. The sidecar's own refusals (`400`, `404`, `409`) pass through with its `code`. A sidecar that does not have the route at all, because it is older than the API, gets `502` with code `sidecar_route_missing`.

### Collection scoping

Every `read` or `ingest` key is bound to a list of collections. `collections: []`
or omitting the list is refused. All collections requires the explicit
`allCollections: true`. A key with only the `admin` scope omits both and is bound
to none. A key may list up to 50 collections.

Names are case-sensitive, matching QMD, where `Work` and `work` are two
collections. List them exactly as `/v1/collections` shows them.

For a collection-scoped key:

- **Search** with no `collection` searches all of the key's collections,
  including ones QMD excludes from its default set. `Memoria:Search:DefaultCollections`
  does not apply. Naming a collection outside the list is `403`, whether or not it
  exists. Every hit is checked again on the way out.
- **`GET /v1/docs/{id}`** answers `404` for a reference that resolves outside the
  list, with the same body as a real miss. The list goes to the sidecar, which
  resolves the reference only inside it: nothing outside is matched, scanned or read.
  A docid shared with a duplicate document elsewhere finds the key's own copy.
- **`POST /v1/docs:multiGet`** matches and reads only inside the list, and filters
  `docs` and `skipped` per result again on the way out. `errors` is always empty, so
  a reference with no readable match is simply absent.
- **`/v1/collections` and `/v1/status`** cover only the key's collections.
  `totalDocuments` is their sum, and `needsEmbedding` is `null`, because QMD reports
  that backlog only for the whole index.
- **Sidecar errors** carry no sidecar detail about documents.

### Failed authentication and shared addresses

Each attempt reserves a failure before it may reach the database, and a valid key gets
it back, so the limit holds exactly under concurrency. A valid key is not locked out by
an attacker at the same address: the API keeps the hashes of known keys in memory, from
startup, key management and successful use, and a matching secret skips the budget. A
match still goes to the database, which decides revocation and expiry. A wrong secret
never matches, so a guesser gets no bypass.

The tradeoff: a key this process has not seen waits out the window if its address is
over budget. That only happens for a key created by another process since startup, such
as a second bootstrap, or past 10,000 known keys. Restarting the API loads it.

### Batch selector limits

`POST /v1/docs:multiGet` and the MCP `get_documents` tool refuse these with `400`,
for every key. The sidecar enforces the same rules. QMD's own glob matcher can be
made to run indefinitely by one crafted pattern, so globs are matched by Memoria in
bounded time and never reach QMD.

| Limit | Value |
|---|---|
| Glob syntax | `*`, `?` and whole-segment `**` over letters, digits, space, `.`, `_`, `-`, `/` and `:`. Braces are refused because expansion can be exponential, brackets because nothing needs them, and extglobs and parentheses because they are the attack. |
| Wildcards | At most 12 per glob, at most 2 `**`, and `*` may not touch `?`. `??` is allowed: it is fixed-width. |
| Glob length and count | 256 characters, 10 globs per request. An entry in `ids` holding a wildcard counts as a glob. |
| References | 200 per request, 1024 characters each, and no commas inside an `ids` entry. |
| `maxBytes` | At most 1048576 (1 MiB) per document. Read longer documents a range at a time with `GET /v1/docs/{id}`. |
| Response size | `Memoria:Search:MaxMultiGetBytes` for all bodies together. Documents past it are listed under `skipped` and are not read. |
| Documents from globs | 1000 per request. Past that, `truncated` is `true` in the response and the rest are not looked at. |

References resolve the same way for every key: docid, `qmd://` URI, `collection/path`,
bare path, or path-boundary suffix, but not an arbitrary fragment. A miss returns no
suggestions: producing them means comparing the reference with every path in the index,
which blocks the sidecar.

### Anonymous routes

| Route | Why |
|---|---|
| `/health` | Liveness; contacts nothing and reveals only the version. |
| `/ready` | Orchestrators need it without a credential. It answers 200 with `status` of `ready`, `warming` or `degraded` whenever the sidecar answers, because losing GPU workers makes search rank worse and must not take the API out of rotation. It answers 503 only when the sidecar cannot be reached (`status` of `not_ready` to these callers), or with `?strict=true` for anything but `ready`. Anonymous and non-admin callers get the status word alone, with `reason` and `sidecar` null: no index paths, counts or sidecar details. An admin key gets the full report, including `jobs.stuck` and `llm.documentsSetAside`. |
| `/openapi/v1.json` | The spec is public and carries no data. |

### Audit and logs

Each request a key makes to a scoped route, including ones ending in `403` or
`429`, is written to the `audit_log` table and to the log stream: a server-generated
audit id, key id, operation (the REST route's name, or `mcp.<tool>`), collections
touched, collections attempted when a request was denied, result count, status,
timestamp, and trace id. For key management it also records the target key's id,
scopes and collections, never its secret. Status is how the operation ended: an MCP
tool call denied for scope is recorded as `403` even though its HTTP status is `200`.
The trace id comes from the caller's `traceparent` when they send one, so it is for
correlation only; the audit id is what identifies a record. Requests with no valid
key are logged but not audited: they have no key to attribute, and recording them
would let anyone grow the table.

Every request also gets one line in the log stream with its method, route pattern, status, time, trace id, peer address and key id. Anonymous `/health` and `/ready` probes log at debug, so an orchestrator's polling does not fill the log. A probe that carries a key, such as `memoria ready`, logs at information like any other request, so the trace id it prints can be found.

**The audit log is not tamper-evident against the key it records.** `Audit:MaxRowsPerKey`
keeps one noisy key from growing the table or evicting another key's history, but it also
means a key can roll its own oldest rows out by making enough requests. The API logs one
warning per key per process when its rows are first trimmed, and the log stream keeps every
record the table drops. If you need a record the acting key cannot erase, ship the log stream
or the rows off the box.

The request log line carries the route template and the key id. Logs never carry
the query string, request headers, or a key's secret. With
`Memoria:Logging:RequestDetails` set to `Enabled`, the JSON console format's
scopes include ASP.NET Core's raw `RequestPath`, so a document path requested
through `GET /v1/docs/{id}` can appear in log output even with
`Audit:IncludeQueries` off. Rate limits and the audit trail
key on the API key, not the client address, so they work the same behind the
reverse proxy. The `RemoteIp` in the log line and the failed-authentication limit
use the client address: the socket peer, or the forwarded client when the peer is a
configured `TrustedProxies` entry.

## `Memoria:Ingest` — writing to the corpus

| Key | Default | Purpose |
|---|---|---|
| `MaxDocumentBytes` | `2097152` | The largest stored document, in UTF-8 bytes, at least 64 KiB. Set it to the sidecar's `MEMORIA_QMD_MAX_DOCUMENT_BYTES`. Over it, `POST /v1/documents` answers 413 and a session part fails on its own. The renderer splits a long message to fit. |
| `MaxSessionBytes` | `268435456` | The largest raw transcript one request may send. It is read as a stream, and past this the request gets 413 `payload_too_large`. Before answering, the server reads and throws away what is left of the body, so a client still sending gets to its end and reads the 413 instead of a broken connection. It reads up to twice this limit in all, for at most two minutes, and no slower than `MinUploadBytesPerSecond`. Past any of those, or with a declared length over twice the limit, the 413 goes at once, and a client still sending may see only a reset. Nothing past the limit is kept. The documents routes do the same with their own limits. A transcript under this limit whose text passes the renderer's 32 million character budget is stored as far as the budget goes and answered with 207: it stays `partial`, and `read.messagesOverLimit` says how much was left out. |
| `MaxConcurrentLargeDocuments` | `8` | Documents over 256 KiB handled at once. Past it one gets 503 `ingest_busy` with `Retry-After` and its connection closed, and smaller ones carry on. Their text waits in memory for a render slot, so without this a burst can run the process out of memory, which can kill it. A client should wait before retrying: one that does stores far more than one that does not. |
| `MaxConcurrentLargeUploads` | `2` | Transcripts over 8 MiB handled at once. Past it a large upload gets 503 `ingest_busy` with `Retry-After`, while documents and smaller transcripts carry on. A slow large upload holds its place until it ends, so two uploads dripping at the rate floor can keep other large uploads out for as long as the upload deadline allows. That is accepted on a private server whose keys all belong to one owner. |
| `MaxConcurrentRenders` | `2` | Redactions and renders run at once, for documents and transcripts together. Jobs of 1 MiB or less have a lane of 2 of their own and do not count against it. Reading a body does not take one. Only one job of 32 MiB or more runs at a time, whatever this says. Under a 512 MiB GC limit (`DOTNET_GCHeapHardLimit`) in the Linux image, one or two 200 MiB transcripts took the API to about 570 MiB of peak memory, all answered 207. Under 1 GiB the peak was about 1 GB. So allow for that much memory or set such a limit. If memory runs out anyway, the request gets 503 `ingest_busy` and can be repeated. |
| `MinUploadBytesPerSecond` | `16384` | The slowest a body may arrive, after 10 seconds' grace. A link slower than this gets 408, so a client on one needs to send less at a time. Above it, a link costs nothing but the time. |
| `SessionUploadDeadline` | `01:00:00` | The longest a transcript's body may take to arrive, however steadily. Past it the request gets 408 and nothing changes. |
| `DocumentUploadDeadline` | `00:05:00` | The same for a document. |
| `DeleteWriteWait` | `00:00:30` | How long a delete waits for a write of its path to answer. Past it the delete answers 503 `delete_unconfirmed`, having changed nothing, and repeating it is safe. |
| `DigestKey` | required with API keys | At least 32 random characters, held outside the database: the API will not start without it. It keys every digest made from content that the ledger stores. See below. |
| `PreviousDigestKeys` | empty | Keys that used to be `DigestKey`. Stored digests made with one still match, so a key can be changed without losing them. See below. |
| `MinReindexInterval` | `00:00:10` | The least time between index updates a delete starts in one collection. A delete with none recently starts one at once, so removing a leaked secret stays immediate. A burst shares one more when the interval ends, so no update starts later than this. This bounds when the update starts, not when it finishes: an update took about 1.7 s at 1,500 documents and about 118 s at 52,000, and a deleted document stays searchable until it ends. |
| `Collections:<name>:Enabled` | `false` | Opens a collection to ingest. See below. |
| `Collections:<name>:Redaction` | not set | Exactly `Disabled` turns redaction off for that collection. See below. |

Writing needs the `ingest` scope, the target collection in the key's allowlist, and the collection opened to ingest. It is not available when `Memoria:Auth:Mode` is `Disabled`.

### Collections belong to ingest or not at all

A collection is opened to ingest by setting `Memoria:Ingest:Collections:<name>:Enabled` to `true`. The first time ingest uses it, the API asks the sidecar whether the collection's directory is empty. If it is not, or the directory does not exist, the request gets 409 `collection_not_empty` and nothing changes. Otherwise the API records that in the control database and never asks again. A collection not enabled gets 409 `ingest_not_enabled`, for writes and deletes alike.

Everything inside an opened collection is Memoria's. So a write replaces whatever is at its path, and a retry after a lost answer simply overwrites with the same result. A delete removes whatever is at its path, and it is up to no one else to have put it there. Nothing else may write to such a collection, and losing the control database means restoring it, since a collection with files in it will not open again.

### One API and one sidecar, with nothing between them

This design assumes one API instance, talking directly to the sidecar on one host. **Two API instances, or anything between them that buffers or answers, break it.** See [deployment](deployment.md#nothing-may-sit-between-the-api-and-the-sidecar).

Each path has one lock, in the API process. A write holds it until the sidecar answers. A delete waits for it, leaves its tombstone, and has the file removed, so it cannot miss a write in flight.

If the API dies or gives up on a write, its connection closes. The sidecar writes to a temporary file and checks the connection immediately before the atomic rename, so that write does not land. On one Linux host, a killed process's close was seen within 5 ms, and the rename followed the check by under 1 ms. A write past its check when the API dies still lands, and if a delete has been answered by then, the file is back. A restart takes far longer than that window, so this needs the sidecar to stall in that exact place, and repeating the delete clears it.

### Moving an old corpus

`POST /v1/ingest/rendered?collection=&path=&updated=` takes markdown that was already rendered, as `text/markdown`, with its frontmatter, and stores it at `path`. All of it goes through the collection's redaction decision and is reported the same way. Otherwise it is kept as it came. It needs an opened collection, and it cannot delete.

- **Sending it again is safe.** The path is the identity, so an equal `updated` overwrites with the same result. Send the same `updated` each time, such as the file's modification time. An older one gets 409 `stale_version`, and something deleted stays out as usual. A path has one spelling: a backslash is refused, here and on `DELETE /v1/documents`.
- **Compare `updated` as well as `state`, and finish sending the newest copy.** The ledger records a version only when it hears the answer. So after a lost answer `complete` can name an older copy than the file holds, and sending that older copy again answers 202 and replaces the newer one.
- **`frontmatter` says what became of the metadata.** `read` means filters work. `past_window` means the block does not close within the sidecar's first 8192 characters, as with an old export's long `files_touched`. `missing` means there is none, and `unparsable` means it is not a YAML mapping. In all three the document is stored and searchable, but matches no date, project or tag filter. Nothing is trimmed to make it fit. Redaction can cause `unparsable`: a credential inside a flow map, like `m: {a: <token>}`, is replaced by text YAML cannot read there. The secret is gone and the answer says so.
- **Over `MaxDocumentBytes` gets 413,** for that document alone. It is not split: a part needs a new path, so it would stop matching by path, and a rendered document cannot be cut safely at a message. Re-render it from its raw transcript through `POST /v1/ingest/session`, which splits, or raise `MaxDocumentBytes` and the sidecar's `MEMORIA_QMD_MAX_DOCUMENT_BYTES` together for the migration.
- **A document over 256 KiB counts against `MaxConcurrentLargeDocuments`,** so use few workers and pause on 503.
- **What each answer means:**

| Answer | Meaning | What to do |
|---|---|---|
| 202 | Stored. | Sending it again is safe. |
| 400 | The path, body or date is not acceptable. | Report it. Retrying gets the same answer. |
| 403 | The key may not write to this collection. | Fix the key. |
| 409 `stale_version`, `path_owned`, `case_conflict` | A newer copy or a deleted one, a path belonging to another session, or a name differing only in case. | Report it. Retrying gets the same answer. |
| 409 `session_shrank` | The document says it holds fewer messages than the copy at its path. | If it really is shorter, delete the path and send it dated after the delete. |
| 409 `ingest_not_enabled`, `collection_not_empty` | The collection is not open to ingest. | An operator has to act first. |
| 409 `ingest_in_progress` | Another upload of this is running. | Retry after `Retry-After`. |
| 409 `deleted_during_upload` | A delete took it. | Report it. |
| 413 | Over the size limit. | Report it. Retrying gets the same answer. |
| 422 | Redaction could not finish, so it was not stored. | Report it. |
| 503 `ingest_busy`, `sidecar_unreachable` | Too much at once, or the sidecar is away. | Pause, then send it again. Safe. |
| 504, or the connection is lost | The answer did not arrive, and the write may have landed. | Send it again. Safe. |
- **`GET /v1/ingest/rendered?collection=&path=`** says what the ledger holds for a path: `state` is `complete` once a write of it landed, `taken_over` once its session owns it, and `none` if nothing is recorded.
- **A session takes over its migrated documents in its own folder.** A document whose frontmatter names a `session_id` belongs to that session, as long as it sits in the same folder as the session's files, which are always in the collection root, and the session shows it is the copy in its own project folder: `projectDirectory` is sent, and the transcript's first `cwd`, encoded the way Claude Code names its folders, matches it. A Codex session needs neither. The first `POST /v1/ingest/session` for it replaces them: its parts overwrite the same file names, and documents under names it no longer writes are removed once every part has arrived, so no message stays twice once it finishes. From then on the session's normal rules apply.
- **A copy in another folder stays where it is.** It is not replaced or removed, and it does not limit what the session may send, so searches can find the same text twice. This is deliberate: such a copy can be another fork of the session, and an export may be the only copy left. Two documents with one `session_id` in the root are not protected this way, so a session that takes over replaces both, and the one that is a fork goes too. A copy with no `session_id` stays beside the session as a double too.
- **A session resumed from another directory takes nothing over.** Its first `cwd` does not match its folder, so it stores under its own suffixed name beside the export, and later uploads stay on that name. The same text can then turn up twice in search. An upload that cannot show which copy it is, because it has no `projectDirectory` or no `cwd`, takes nothing over either: under a name an export holds it gets 409 `path_owned`, and otherwise it lands beside the export. A path someone deleted still stays deleted for both.
- **If the upload fails partway,** the old documents under other names stay, but a part that overwrites a document replaces it at once. When the part boundaries differ and the answer is lost, some old messages can be off disk until the upload is sent again, which restores them. Until then that text depends on the raw transcript.
- **What refuses a takeover.** A copy with fewer messages than the documents reach, by their `message_start` and `message_count`, gets 409 `session_shrank`. So does any copy when a document does not say how many it holds, until it is sent with `allowShrink=true`. A copy older than them gets 409 `stale_version`.
- **A different start at takeover replaces the migrated text.** A rendered document has no digest of its messages, so a copy whose start differs is caught only on uploads after the takeover.
- **A `session_id` that did not come from the exporter still counts.** The session it names can remove that document, or be blocked by it with `session_shrank` or `stale_version`. A document naming another session stays out of it, as does one with no `session_id`, and a session wanting its file name gets 409 `path_owned`.
- **A path someone deleted stays deleted.** The part answers `part_deleted`, and the rest of the old parts still go.
- **A taken-over path stays the session's.** A rendered copy sent to it gets 409 `path_owned`, even after the part was deleted or the session stopped writing that name. A rendered copy under a name that was never used is stored as usual, beside the session and so doubled, and the session's next upload takes it over, which removes it unless the session writes that name. This is a way for deleted text to come back, as a delete is not a content ban.

### What a session upload promises

- **`202` and `complete` mean all of it.** The transcript had messages and lost none, every part was written, and every old part was dealt with. Anything less answers `207`, stays `partial` in `GET /v1/ingest/session`, and `read` and `parts` say why. A live transcript whose last line is still being written reads as `partial` until it is sent whole.
- **An upload must start with the messages stored.** Fewer gets 409 `session_shrank`, and different ones get 409 `lost_content`, whatever the version, because a cut or damaged copy looks just like that. A torn last line, or a damaged line that held no stored message, does not trigger this: the session stays `partial` and keeps taking new messages. Rendering to fewer parts does not trigger it either. Send `allowShrink=true` if the history really did change. That needs no extra privilege: any key that can write the collection can send it. An upgrade that changes redaction rules can change stored text, and such a session then needs it too.
- **Sessions do not overwrite each other.** The ledger records which session each path belongs to, and a path belonging to another gets 409 `path_owned`. A session whose short id is already taken gets a digest in its file names, a longer one if that is taken too, and a name is free again once its owner has no files. A Codex transcript whose `session_meta.id` differs from `sessionId` gets 400.
- **Old parts are removed once they stop being parts.** When a session renders to fewer parts than it did, the ledger says which paths it had, and those go. An upload that lost content keeps them, and the session is not called complete.

### Deleting

A delete waits for any write of its path to answer, up to `DeleteWriteWait`. It then leaves its tombstone, which also stops the upload that was running, and has the file removed. That upload answers `deleted_during_upload`, or says the part was deleted, unless it finished first. No client hanging up can stop a delete that has started.

202 means the sidecar confirmed the file is gone. 503 `delete_unconfirmed` means it could not say so, and repeating the request is safe. If the sidecar cannot be reached the tombstone is already there, so a replay stays out in the meantime. A refusal on the merits, such as a directory at the path, is answered as what it is.

A name in another case is another file, so it answers `deleted: false`, or 409 `case_conflict` from the sidecar, and the document stays. A delete of a path holding nothing answers `deleted: false` and costs no index update.

A delete and an upload sent at about the same time land in one of three orders, and in each the answers agree with the file:

- **The delete comes before the upload is accepted.** It answers `deleted: false`, and the upload then lands.
- **The delete comes after the upload is accepted but before its write goes out.** Its tombstone stops the upload, which answers 409 `deleted_during_upload` and stores nothing. No file had landed, so the delete answers `deleted: false`.
- **The delete comes after the write.** It removes the file and answers `deleted: true`. The upload answers 202 if it had finished, and `deleted_during_upload` if it had not.

`deleted` says whether a file was removed. The file is there afterwards only when the upload answered 202 and the delete answered `deleted: false`. Sending them at the same moment does not say which order you get. Wait for a delete's answer before uploading, and the upload's file stays.

**A delete is not a content ban.** A deleted document can come back only as new content dated after the delete. The same content under a later date is refused, by comparing a keyed digest of its redacted text. Only the last version deleted is remembered, so an older version replayed under a later date does come back. A deleted session part stays deleted however often the session is sent. A date ahead of the clock is recorded as the clock, so it cannot hold honest updates back. A leaked credential must also be removed from the source, or it can return under a new identity.

To bring back what was deleted, send `restoreDeleted=true` with a key that has `admin` as well as `ingest`. The audit record says so. It guards a path, not the content. A collection with redaction off works the same way: its digests are of the unredacted text, keyed in the same way.

Audit rows name what was read or written, in the table only, never the log, so finding which document a request asked for does not need `RequestDetails`. A read that found nothing is recorded as `[unresolved]`, as the text someone asked for may be a credential pasted in by mistake, and nothing can tell that by its look. A read that found a document records the document's own path.

### Digests in the ledger

The ledger keeps digests so a stolen database cannot confirm a guess at what a document said: redaction cannot stop a plain hash confirming text no rule matches. Everything derived from content, which is the document digest and the chain of message digests with each message's role, is an HMAC under `DigestKey`.

To change the key, set the new one as `DigestKey` and put the old one in `PreviousDigestKeys`. Stored digests made with an old key keep matching, and each is replaced with one under the new key as its document or session is next written. Once an old key is dropped from the list, digests made with it stop matching: a session then needs `allowShrink=true` once, and a deleted document no longer refuses its same content, so only the date protects it.

Identity, lookup and path digests are not keyed. They are made from names, not content, and must stay the same however the key changes.

`message_sha256` in a Codex part's frontmatter says no more than the part's own text. When the last check replaced a message in that part, the digest is the only trace of what the message said, so it is replaced too.

### What redaction cannot catch

Besides its own rules, the engine runs gitleaks's default rules as data, after its own, on whatever they left. [The imported gitleaks rules](gitleaks-rules.md) lists every one, and every upstream rule left out, with the reason. Imported rules that know a token by its own format run before those that know a value by the name before it, so a name's rule cannot take the head of a longer token. A token whose alphabet has no `_` ends at one, as in `ENV_<token>_backup`, though upstream's `\b` finds nothing there, unless what came before the `_` reads as words, as in `sk_test_connection_timeout`. A token whose alphabet holds `_` runs on, so it stays, and so does a value known by the name before it. After a token at its format's exact length, or one that ends in `=` padding, text after a separator stays, plain or URL-encoded. After one of a variable length, or a value known by the name before it, random text after a separator goes too, since it may be the rest of a longer token, and so does more of the run after an encoded `%2B`, `%2F` or `%3D` when a piece of it reads as random. A path such as `%2Fhome%2Fuser` stays. A change to these rules changes what is stored on the next upload, so a session already stored may then need `allowShrink=true`, as any rule change can.

A value carrying a provider's own prefix at that provider's length is replaced unless it is recognizably a sample, such as `your-token-goes-here`, or a mask. So a name that happens to start with one of those prefixes is replaced too, which costs little. A value with no prefix, known only from what surrounds it, also has to read as generated: after `token:` or `?key=`, lower-case letters split by dashes cannot be told from a name, and they stay.

A credential in base64, hex or percent-encoded form, or with its characters written as literal `\uXXXX` escapes, is decoded once to check it, so it is replaced, up to 16 KiB of encoded text. Encoding inside encoding is looked through up to four layers, and a fifth is stored as it came. Base64 is read as little-endian UTF-16 when nearly every second byte is zero, as PowerShell's `-EncodedCommand` writes it. Base64 that decodes to text is also judged by Memoria's rules that go by a name, so `password=x` in a base64 `.env` file or Docker `config.json` is replaced as it would be written out. Percent-encoded text is judged only by tokens that speak for themselves, because across this repository every percent-encoded run that a name-based rule would take was prose about a password, not a password. Text there is UTF-8 or UTF-16 with few control characters, so binary data is not read as text, and one hit replaces the whole run. A Stripe publishable key, `pk_live_…`, is public by design and stays. A Facebook token is replaced from `EAAC` at 44 characters, or `EAA` and another capital at 100, the length of user and page tokens. Square's `EAAA` at 64 keeps its own label, and an `EAA` run of another length below 100 stays, as do upper-case hex hashes and base64 data that start the same way. Hex counts only as an even run of at least 32 digits with no other hex digit either side, so it is read after `0x`, PostgreSQL's `\x` or a JSON `\n`. Hex with spaces or colons between its bytes, `\x67\x68` escapes, hex of UTF-16 text, upper-case `\U` escapes, and a credential encrypted or reversed are stored as they came. Some values cannot be told from ordinary text without a name beside them, so they stay. A password on a `curl`, MySQL or Mongo command line is found at the start of a command, and also when the command is quoted after other words, as long as only flags, flag values without spaces and URLs lie between the command and the password. Quoted after other words, the password must read as generated, at least eight characters that are not words, since the command name could as well be a word of the sentence or another tool's argument, as in `apt install mysql -pkg`. A port or a Docker port mapping after `-p` is never a password. A quoted flag value, or a database name before the flags, ends the command, so the password after it stays. A password said in prose with no `:` or `=`, as in `the password is …`, stays. So does a bare word after a password's name and colon when a word of prose stands before the name, as in `admin password: letmein`. After a user, host or database name and its value, as in `user: app password: letmeinplease`, a bare word of ten characters or more counts. A shorter one, such as a log's `user=bob password: rejected`, stays. Words with spaces between them in braces, and a list of calls or a comprehension, are code or prose, so they stay. A value that holds quotes or spaces inside a `$(…)`, `${…}`, `[…]` or `{…}` is judged whole, so the literal `printf` or `echo` prints, a list item, or a literal a template joins with `+` is replaced. Three shapes beside a template span stay, so that code stays readable: an upper-case name with underscores, a remainder under four characters, and a version range such as `^2024.11`. **A bare 40- or 64-digit hex value stays,** because commit hashes and lockfile hashes look exactly the same. That includes the secret half of a Memoria key on its own, though a whole key is always replaced. **A 40-character value with no name stays** unless it comes soon after an AWS access key id. JSON escapes in the request itself, control characters and unusual spaces inside a token, and a token split by line breaks, content blocks or messages are all caught when the token starts with a recognized prefix. What follows a whole token is kept, so its last piece stays if it reads as an ordinary word or number, up to about 12 characters. Some rules take more than the value: a `Cookie` header goes to the end of its line, as any cookie in it may be the live one.

### Turning redaction off for a collection

Every collection is redacted unless an operator names it here:

```jsonc
{ "Memoria": { "Ingest": { "Collections": { "family-notes": { "Redaction": "Disabled" } } } } }
```

- **Only this setting can do it.** No request parameter, key attribute, header or envelope field has any effect, so a caller cannot turn redaction off.
- **The value and the name are exact.** `disabled`, `off`, `false` and ` Disabled ` all leave redaction on, and the server warns at startup about a value it did not recognize. A differently cased collection name is a different collection, and is redacted.
- **Search results from an unredacted collection can reach cloud models.** They go wherever the caller sends them, along with any credential the text holds. Treat every credential ingested into the collection as exposed to those models.
- **A switch changes future ingest only.** Turning redaction back on does not clean what is already stored. Older documents stay as they were written until they are ingested again or deleted, so re-ingest or delete them, and rotate any credential they held. Turning redaction off does not change older documents either.
- **It is visible.** Startup logs a warning naming each unredacted collection. The ingest response and the audit record say `redaction: "disabled"`, and `GET /v1/status` lists `unredactedCollections` for a key that has `admin` as well as `read`. The per-rule counts, refusals and the check on finished parts do not apply to such a collection, so they are empty.

## `Memoria:Logging` — what the log may hold

Query strings and request paths carry search text and document paths, so they are kept out of the log in code, at every log level, whatever `appsettings.json` or `Logging:*` says. The framework's request log is off, its other logs that hold paths or URIs are limited to warnings, and the MCP library's, which name tools, to critical failures. The sidecar likewise logs route patterns, never paths or query strings. So raising log levels to debug something cannot put a secret in the log this way.

| Key | Default | Purpose |
|---|---|---|
| `RequestDetails` | not set | Exactly `Enabled` lets the framework log request details again at the configured levels. Startup logs a loud warning when it is on. Only for debugging, on a host you can clean up afterwards. |

## Host settings outside `Memoria`

| Key | Default | Purpose |
|---|---|---|
| `Urls` | `http://0.0.0.0:8080` | Kestrel bind address. `0.0.0.0` makes the API reachable on the LAN so the laptop can use it; nothing routes it beyond the LAN until homelab hardening lands. Override with `ASPNETCORE_URLS` or `--urls`. |
| `Logging:Console:FormatterName` | `json` | One JSON object per line, UTC timestamps, scopes included. `appsettings.Development.json` switches to `simple` for a readable terminal. |

## Local development

```jsonc
// src/Memoria.Api/appsettings.local.json  (gitignored)
{
  "Memoria": {
    "Sidecar": {
      "BaseUrl": "http://127.0.0.1:8282",
      "Secret": "the value you exported as MEMORIA_QMD_SECRET"
    },
    // Anonymous, on a trusted LAN only. Or set Auth:DatabasePath to develop with keys.
    "Auth": { "Mode": "Disabled" }
  }
}
```

The file is gitignored, excluded from the Docker build context
(`.dockerignore`), and excluded from `dotnet publish` by the project file, so
it can never end up in the container image.

```bash
dotnet run --project src/Memoria.Api
curl -s http://localhost:8080/ready | jq
curl -s "http://localhost:8080/v1/search?query=api+key+auth&rerank=false&limit=3" | jq
```
