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
| `Mode` | `ApiKey` | `ApiKey` requires a key on every `/v1` and MCP route. `Disabled` turns authentication off for development on a trusted LAN; it must be set explicitly and logs a loud warning at startup. Any other value stops the host. |
| `DatabasePath` | unset | The SQLite control database holding keys and audit records. **Required in `ApiKey` mode**: without it the host refuses to start. It is durable state, separate from QMD's index, and belongs on a local volume (SQLite's WAL mode does not work over a network share). Not needed in `Disabled` mode. |
| `RateLimit:Enabled` | `true` | Enforce per-key limits. |
| `RateLimit:PermitLimit` | `120` | Requests one key may make per window. Over the limit: `429` with `Retry-After`. |
| `RateLimit:Window` | `00:01:00` | Length of the fixed window. |
| `Audit:IncludeQueries` | `false` | Also record search queries and document references in the audit log. Queries can be as sensitive as the corpus, so this is off unless you opt in. Document content is never recorded. |
| `Audit:RetentionDays` | `90` | Days to keep audit records in the database. Zero or less keeps them forever. |

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
  list, with the same body as a real miss.
- **`POST /v1/docs:multiGet`** filters `docs` and `skipped` per result. `errors` is
  always empty, because QMD's errors quote "did you mean" paths from any
  collection, and their presence would reveal whether a reference matched
  something out of scope. A reference with no readable match is simply absent.
- **`/v1/collections` and `/v1/status`** cover only the key's collections.
  `totalDocuments` is their sum, and `needsEmbedding` is `null`, because QMD reports
  that backlog only for the whole index.
- **Sidecar errors** carry no sidecar detail about documents.

### Anonymous routes

| Route | Why |
|---|---|
| `/health` | Liveness; contacts nothing and reveals only the version. |
| `/ready` | Orchestrators need it without a credential. Anonymous and non-admin callers get `status` of `ready` or `not_ready`, with `reason` and `sidecar` null: no index paths, counts or sidecar details. An admin key gets the full report. |
| `/openapi/v1.json` | The spec is public and carries no data. |

### Audit and logs

Each request a key makes to a scoped route, including ones ending in `403` or
`429`, is written to the `audit_log` table and to the log stream: key id,
operation (the REST route's name, or `mcp.<tool>`), collections touched, result
count, status, timestamp, and trace id. Requests with no valid key are logged but
not audited: they have no key to attribute, and recording them would let anyone
grow the table.

The request log line carries the route template and the key id. Logs never carry
the query string, request headers, or a key's secret. The JSON console format's
scopes do include ASP.NET Core's raw `RequestPath`, so a document path requested
through `GET /v1/docs/{id}` can appear in log output even with
`Audit:IncludeQueries` off. Rate limits and the audit trail
key on the API key, not the client address, so they work the same behind the
reverse proxy, where every caller shares one address. The `RemoteIp` in the log
line is the proxy's.

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
