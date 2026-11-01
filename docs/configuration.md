# Configuring Memoria.Api

Everything the host needs lives under one `Memoria` section. It binds through
the standard ASP.NET Core configuration stack, so each key can come from
`appsettings.json`, `appsettings.{Environment}.json`, `appsettings.local.json`
(gitignored, loaded last, for machine-local values), an environment variable,
or a command-line switch. Environment variables use `__` as the separator:
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
| `RerankByDefault` | `true` | Full reranked hybrid search unless the caller passes `rerank=false`. Decided in MEMORIA-1 once QMD moved to the desktop. |
| `DefaultCollections` | unset | Collections searched when the caller names none. Unset defers to QMD's default-included set. |

## Host settings outside `Memoria`

| Key | Default | Purpose |
|---|---|---|
| `Urls` | `http://0.0.0.0:8080` | Kestrel bind address. `0.0.0.0` makes the API reachable on the LAN so the laptop can use it; nothing routes it beyond the LAN until MEMORIA-7 (homelab hardening) and MEMORIA-3 (API keys) land. Override with `ASPNETCORE_URLS` or `--urls`. |
| `Logging:Console:FormatterName` | `json` | One JSON object per line, UTC timestamps, scopes included. `appsettings.Development.json` switches to `simple` for a readable terminal. |

## Local development

```jsonc
// src/Memoria.Api/appsettings.local.json  (gitignored)
{
  "Memoria": {
    "Sidecar": {
      "BaseUrl": "http://127.0.0.1:8282",
      "Secret": "the value you exported as MEMORIA_QMD_SECRET"
    }
  }
}
```

```bash
dotnet run --project src/Memoria.Api
curl -s http://localhost:8080/ready | jq
curl -s "http://localhost:8080/v1/search?query=api+key+auth&rerank=false&limit=3" | jq
```

## Reserved sections

Later epics add siblings under `Memoria`. The shape is fixed now so
deployments do not have to be reorganised when they land:

| Section | Epic | Contents |
|---|---|---|
| `Memoria:Auth` | MEMORIA-3 | API keys, their collection scopes and the `read` / `ingest` / `admin` scopes endpoints already declare through `MemoriaScopeAttribute`. |
| `Memoria:Workers` | MEMORIA-12 / MEMORIA-18 | The inference pool: one entry per worker with `name`, `url`, a token *reference* (never the token), `capabilities` (`embed`, `rerank`, `generate`), `priority` and availability thresholds. |
| `Memoria:Queue` | MEMORIA-17 / MEMORIA-18 | Queue policy: concurrency per worker, embedding batch byte budget, priority classes (interactive before background). |
| `Memoria:Mcp` | MEMORIA-24 | The outward MCP server: enabled flag and path. |

The sidecar keeps its own environment-variable configuration (see
`sidecar/.env.example`). The control-plane issue (MEMORIA-18) decides how much
of `Workers` and `Queue` the API pushes to the sidecar versus the sidecar
reading directly; either way the keys above are where operators will find them.
