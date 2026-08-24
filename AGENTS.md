# AGENTS.md

Guidance for AI agents working in this repository.

## Quick Start

```bash
dotnet restore Memoria.slnx     # .NET packages
(cd cli && go mod download)     # Go CLI dependencies
```

Requires **.NET 10 SDK** and **Go 1.26+**. A running instance additionally needs **Node 22** and QMD (`npm i -g @tobilu/qmd`) on the host.

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

Conventional Commits: `<type>(<scope>): <description>`. Scopes track the layout: `api`, `core`, `cli`, `ingest`, `auth`, `docker`, `docs`, `ci`.

## Build and Test

```bash
dotnet build Memoria.slnx -p:TreatWarningsAsErrors=true   # build (CI parity)
dotnet test Memoria.slnx                                  # test
dotnet format Memoria.slnx --verify-no-changes            # format gate (CI parity)

cd cli
go build ./...                                            # build CLI
go test ./... -race                                       # test CLI
gofmt -l .                                                # must print nothing
go vet ./...
go mod tidy && git diff --exit-code go.mod go.sum         # tidy gate (CI parity)
```

All four gates run in CI as required status checks: `Build & Test`, `Code Formatting`, `CLI Build & Test`, `CLI Lint`.

## Architecture

Memoria is a **front door over QMD**, not a search engine. QMD owns retrieval — BM25 via FTS5, vectors via sqlite-vec, RRF fusion, and LLM reranking. Memoria owns everything QMD deliberately does not: network exposure, authentication, authorization, multi-source ingest, and client tooling.

```
clients (Claude Code / CLI / scripts)
        │  HTTPS + Bearer API key
        ▼
   Memoria.Api  ──── auth, scoping, rate limiting, audit
        │  loopback only
        ▼
   qmd mcp --http  (127.0.0.1:8181)
        │
        ▼
   index.sqlite  ← derived, rebuildable
        ▲
   ingest workers ── redaction → document envelope → markdown corpus
```

| Path | Contains |
|---|---|
| `src/Memoria.Api/` | ASP.NET Core host: MCP proxy, REST endpoints, auth handler, ingest |
| `src/Memoria.Core/` | Document envelope, search contracts, redaction rules |
| `tests/Memoria.Api.Tests/` | xUnit tests against `WebApplicationFactory<Program>` |
| `cli/` | Go module `github.com/mggarofalo/memoria/cli`, binary `memoria` |
| `docs/` | Plane workflow, deployment, API guidelines |

**Key invariants:**

- **QMD binds loopback and is never exposed directly.** All external traffic goes through `Memoria.Api`. QMD ships no authentication whatsoever — the API is the only thing standing between the corpus and the internet.
- **Redaction is a precondition of indexing, not a post-filter.** Transcripts carry live credentials. If a document reaches the index unredacted, the secret is already searchable.
- **API keys are scoped to collections.** Never issue an all-collections key by default; the corpus mixes sensitivity levels.
- **The markdown corpus is the durable artifact.** `index.sqlite` is derived and may be deleted and rebuilt at any time. Never treat it as a source of truth.
- **Reindexing is serialized.** Concurrent `qmd update`/`qmd embed` runs contend over the model and the database. Ingest enqueues; a single worker drains.

## Coding Standards

**C#:** File-scoped namespaces, `sealed` by default, nullable enabled, primary constructors where they read cleanly. Style is enforced by `.editorconfig` and gated by `dotnet format`.

**Go:** Mirror the conventions in `~/Source/plane-cli` — cobra command tree, `99designs/keyring` for credential storage, and a resolver precedence of flag → env → keyring → config. `gofmt` is authoritative.

**Line endings:** C# is CRLF, everything else is LF. Enforced in `.gitattributes` and mirrored in `.editorconfig`. After changing either, run `git add --renormalize .`.

## Agent Rules

**All new functionality must include tests.** Endpoints, handlers, auth logic, redaction rules, and CLI commands ship with tests in the same PR. Never defer tests to a follow-up.

Redaction rules specifically require tests with realistic positive **and** negative cases — a rule that over-matches silently destroys corpus content, and one that under-matches leaks credentials.

**Never modify CI configuration or branch protection** unless explicitly asked.
