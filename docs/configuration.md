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

## `Memoria:Sidecar` — reaching memoria-qmd

| Key | Default | Purpose |
|---|---|---|
| `BaseUrl` | `http://127.0.0.1:8282` | Where the sidecar listens. The sidecar host moves (desktop today, mini PC later); this value is the only thing that changes. |
| `Secret` | unset | The sidecar's `MEMORIA_QMD_SECRET`, sent as `Authorization: Bearer`. **Required when `BaseUrl` is not loopback.** Set it through the environment or `appsettings.local.json`; never commit it. |
| `Timeout` | `00:02:00` | Per-request ceiling. A reranked search on a cold sidecar can take 15 s or more; leave headroom. |
| `ReadinessTimeout` | `00:00:05` | Ceiling for the `/ready` probe only. Probes must fail fast so an orchestrator notices. |
| `ReadyRequiresModels` | `true` | `/ready` reports 503 until the sidecar's GGUF models are resident. Set `false` on a host that deliberately runs unreranked. |

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
| `ingest` | Reserved for the ingest routes. |
| `admin` | Key management and backend detail: the full `/ready` report and how the backend failed. |

`admin` does **not** imply `read`. A key used to manage keys is not a key that
reads the corpus, so day-to-day clients never carry key-management power and a
bootstrapped key reads nothing. An admin can issue itself a reader, but that
leaves an audit record and a separate key to revoke. Give a key both scopes if
you want both.

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
| `/ready` | Orchestrators need it without a credential. Anonymous and non-admin callers get `status` of `ready` or `not_ready`, with `reason` and `sidecar` null: no index paths, counts or sidecar details. An admin key gets the full report. |
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

The request log line carries the route template and the key id. Logs never carry
the query string, request headers, or a key's secret. The JSON console format's
scopes do include ASP.NET Core's raw `RequestPath`, so a document path requested
through `GET /v1/docs/{id}` can appear in log output even with
`Audit:IncludeQueries` off. Rate limits and the audit trail
key on the API key, not the client address, so they work the same behind the
reverse proxy. The `RemoteIp` in the log line and the failed-authentication limit
use the client address: the socket peer, or the forwarded client when the peer is a
configured `TrustedProxies` entry.

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

## Reserved sections

Later work adds siblings under `Memoria`. The shape is fixed now so
deployments do not have to be reorganised when they land:

| Section | Contents |
|---|---|
| `Memoria:Workers` | The inference pool: one entry per worker with `name`, `url`, a token *reference* (never the token), `capabilities` (`embed`, `rerank`, `generate`), `priority` and availability thresholds. |
| `Memoria:Queue` | Queue policy: concurrency per worker, embedding batch byte budget, priority classes (interactive before background). |

The sidecar keeps its own environment-variable configuration (see
`sidecar/.env.example`). The control-plane work decides how much
of `Workers` and `Queue` the API pushes to the sidecar versus the sidecar
reading directly; either way the keys above are where operators will find them.
