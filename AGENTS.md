# AGENTS.md

Guidance for AI agents working in this repository.

## Quick Start

```bash
dotnet restore Memoria.slnx     # .NET packages
(cd cli && go mod download)     # Go CLI dependencies
```

Requires **.NET 10 SDK**, **Go 1.26+**, and **Node 22.18+** (for `sidecar/`). A running instance additionally needs QMD (`npm i -g @tobilu/qmd`), its models, and an index on the sidecar host.

```bash
(cd sidecar && npm ci)          # memoria-qmd sidecar dependencies
```

## Workflow Rules

### Plane

All issue work is tracked in Plane. Project: "Memoria" (identifier: `MEMORIA`) — use the `plane` CLI for all operations. Issues labeled `epic` are parent containers — skip them and work their children. See **[docs/plane.md](docs/plane.md)** for full details.

### Branching

Single-trunk model: `main` is the only long-lived branch. Cut a short-lived
`<type>/memoria-<id>-<slug>` branch off `main`, open a PR, and **squash-merge to
`main`**. There is no `develop` branch and no module/parent branches.

**Never close or merge a pull request you did not open in the current session.**
If a PR looks like a blocker, report it and stop — do not close it.

`main` is protected by the `main protection` ruleset: no direct pushes, no force
pushes, no deletion, and all four CI checks must pass. `non_fast_forward` has no
API bypass — recovering from a bad merge means temporarily disabling "Block force
pushes" in Settings → Rules → Rulesets, then re-enabling it.

### Commits

Conventional Commits: `<type>(<scope>): <description>`. Scopes track the layout: `api`, `core`, `cli`, `sidecar`, `ingest`, `auth`, `docker`, `docs`, `ci`.

## Build and Test

```bash
dotnet build Memoria.slnx -p:TreatWarningsAsErrors=true   # build (CI parity)
dotnet test Memoria.slnx                                  # test
dotnet format Memoria.slnx --verify-no-changes            # format gate (CI parity)
MEMORIA_WRITE_OPENAPI=1 dotnet test Memoria.slnx --filter OpenApiContract   # refresh src/Memoria.Api/openapi.json after an endpoint change
npx --yes @stoplight/spectral-cli@6.17.0 lint --fail-severity warn src/Memoria.Api/openapi.json   # lint gate (CI parity)

cd cli
go build ./...                                            # build CLI
go test ./... -race                                       # test CLI
gofmt -l .                                                # must print nothing
go vet ./...
go mod tidy && git diff --exit-code go.mod go.sum         # tidy gate (CI parity)
go generate ./... && git diff --exit-code internal/api    # regenerate the API client from src/Memoria.Api/openapi.json (CI parity)

cd sidecar
npm run typecheck                                         # tsc --noEmit
npm test                                                  # unit tests (fake backend, no SDK load)
npm run openapi:check                                     # committed openapi.json matches routes
npm run build                                             # emit dist/ for the service
MEMORIA_QMD_INTEGRATION=1 npm run test:integration        # real SDK on a temp fixture index
```

```bash
docker build -t memoria-api:local .                       # the image CI publishes to ghcr.io/mggarofalo/memoria
```

Four gates are required status checks on `main`: `Build & Test`, `Code Formatting`, `CLI Build & Test`, `CLI Lint`. A fifth job, `Sidecar Build & Test`, runs the sidecar commands above, and `Docker Image` (`.github/workflows/docker.yml`) builds the image on every pull request and publishes it on pushes to `main` and `v*` tags; neither is in the ruleset's required list yet.

## Architecture

Memoria is a **front door over QMD**, not a search engine. QMD owns retrieval — BM25 via FTS5, vectors via sqlite-vec, RRF fusion, and LLM reranking. Memoria owns everything QMD deliberately does not: network exposure, authentication, authorization, multi-source ingest, and client tooling.

```
clients (Claude Code / CLI / scripts)
        │  HTTPS + Bearer API key
        ▼
   Memoria.Api  (Docker on homelab) ──── auth, scoping, rate limiting, audit
        │  LAN, plain REST + shared-secret bearer
        ▼
   memoria-qmd sidecar  (desktop, sidecar/) ── imports the QMD SDK in-process
        │
        ▼
   index.sqlite + GGUF models  ← derived, rebuildable
        ▲
   ingest workers ── redaction → document envelope → POST /documents → markdown corpus
```

| Path | Contains |
|---|---|
| `src/Memoria.Api/` | ASP.NET Core host: REST endpoints, outward MCP server, auth handler, ingest |
| `src/Memoria.Core/` | Document envelope, search contracts, redaction rules |
| `tests/Memoria.Api.Tests/` | xUnit tests against `WebApplicationFactory<Program>` with a scripted `FakeSidecar` handler |
| `tests/Memoria.Core.Tests/` | Client unit tests and `SidecarContractTests`, which pins the C# wire types to `sidecar/openapi.json` |
| `sidecar/` | `memoria-qmd`: TypeScript REST service over the QMD SDK; `openapi.json` is its contract |
| `cli/` | Go module `github.com/mggarofalo/memoria/cli`, binary `memoria`; `internal/api` is the client oapi-codegen generates from `src/Memoria.Api/openapi.json` |
| `deploy/` | `compose.yaml` for the always-on host |
| `Dockerfile` | `Memoria.Api` only; the sidecar runs as its own service beside the models (docs/deployment.md) |
| `docs/` | Plane workflow (`plane.md`), API configuration (`configuration.md`), deployment (`deployment.md`) |

**Inside `Memoria.Api`:** `Configuration/` (the `Memoria` options root and its validator), `Sidecar/` (typed-client registration and trace propagation), `Http/` (request logging, problem-details mapping, the `MemoriaScope` and `ICallerScope` seams for MEMORIA-3), `Endpoints/` (`/health`, `/ready`, `/v1/*`), `Mcp/` (the outward MCP server; tools translate to the sidecar client through the same `Search/SearchRequestFactory` the REST endpoints use), `OpenApi/` (document and schema transformers that keep the committed spec generator-friendly), `Hosting/` (the container health-check switch). The sidecar client itself (`IMemoriaQmdClient`, its wire records) lives in `Memoria.Core/Sidecar/`; the public v1 contracts in `Memoria.Core/Contracts/`. When the sidecar contract changes, update the records in `Memoria.Core/Sidecar/SidecarContracts.cs` — `SidecarContractTests` fails until they match the committed `openapi.json`.

**Key invariants:**

- **QMD is never exposed directly.** The sidecar is the only process that opens the index, and it is reachable only on the LAN behind a shared secret; all external traffic goes through `Memoria.Api`. QMD ships no authentication whatsoever — the API is the only thing standing between the corpus and the internet. QMD's own MCP HTTP transport is not used: it binds `localhost` only, requires an `Mcp-Session-Id` handshake, and cannot filter on frontmatter.
- **Redaction is a precondition of indexing, not a post-filter.** Transcripts carry live credentials. If a document reaches the index unredacted, the secret is already searchable.
- **API keys are scoped to collections.** Never issue an all-collections key by default; the corpus mixes sensitivity levels.
- **The markdown corpus is the durable artifact.** `index.sqlite` is derived and may be deleted and rebuilt at any time. Never treat it as a source of truth.
- **Every request is traceable.** `Memoria.Api` returns `X-Trace-Id`, logs one structured line per request, and forwards the same id to the sidecar as `request-id` (its pino `reqId`) plus W3C `traceparent`. Problem-details responses carry `traceId`. Keep new endpoints inside the `/v1` group so they inherit this and the scope metadata.
- **Reindexing is serialized.** Concurrent `qmd update`/`qmd embed` runs contend over the model and the database. Ingest enqueues; a single worker drains (`POST /reindex` and `POST /documents` on the sidecar share one queue).

## Coding Standards

**C#:** File-scoped namespaces, `sealed` by default, nullable enabled, primary constructors where they read cleanly. Style is enforced by `.editorconfig` and gated by `dotnet format`.

**TypeScript (sidecar/):** ESM, strict `tsconfig`, Node's native type stripping for tests (`erasableSyntaxOnly`: no enums, no parameter properties, `import type` for types, `.ts` extensions on relative imports). `@tobilu/qmd` is imported in exactly one file, `src/store.ts`; everything else depends on the `QmdBackend` interface so tests run on the in-memory fake. Route changes must regenerate `openapi.json` (`npm run openapi`).

**OpenAPI (Memoria.Api):** the served document is pinned to `src/Memoria.Api/openapi.json` by `OpenApiContractTests`. After changing an endpoint or contract record, refresh it with `MEMORIA_WRITE_OPENAPI=1 dotnet test Memoria.slnx --filter OpenApiContract`, then `cd cli && go generate ./...` so the Go client follows. Both drifts fail CI.

**Go:** Mirror the conventions in `~/Source/plane-cli` — cobra command tree, `99designs/keyring` for credential storage, and a resolver precedence of flag → env → keyring → config. `gofmt` is authoritative.

**Line endings:** C# is CRLF, everything else is LF. Enforced in `.gitattributes` and mirrored in `.editorconfig`. After changing either, run `git add --renormalize .`.

## Agent Rules

**All new functionality must include tests.** Endpoints, handlers, auth logic, redaction rules, and CLI commands ship with tests in the same PR. Never defer tests to a follow-up.

Redaction rules specifically require tests with realistic positive **and** negative cases — a rule that over-matches silently destroys corpus content, and one that under-matches leaks credentials.

**Never modify CI configuration or branch protection** unless explicitly asked.
