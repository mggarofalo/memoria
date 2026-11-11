# AGENTS.md

Guidance for AI agents working in this repository.

## Quick Start

```bash
dotnet restore Memoria.slnx     # .NET packages
(cd cli && go mod download)     # Go CLI dependencies
```

Requires **.NET 10 SDK**, **Go 1.27.3+**, and **Node 26.13+** (for `sidecar/`). A running instance additionally needs an index on the sidecar host, and in local LLM mode QMD's models. For the `qmd` CLI, use the one the sidecar installs, patched as its SDK is: `node sidecar/node_modules/@tobilu/qmd/bin/qmd` from a checkout, or `node node_modules/@tobilu/qmd/bin/qmd` inside the image (`docker compose -f deploy/compose.yaml run --rm --no-deps memoria-qmd node_modules/@tobilu/qmd/bin/qmd status`: the image's entrypoint is already `node`). Never a global `npm i -g @tobilu/qmd`: it is stock, and a newer one migrates the index to a schema the sidecar refuses to serve.

```bash
(cd sidecar && npm ci)          # memoria-qmd sidecar dependencies
(cd worker && npm ci)           # memoria-worker dev dependencies
dotnet tool restore             # ReportGenerator and Stryker.NET
npm ci                          # dev tooling at the root; also installs the git hooks
```

`npm ci` at the root is the one command that installs the hooks. `npx lefthook uninstall` removes them. Nothing at the root ships.

## Workflow Rules

### Plane

All issue work is tracked in Plane. Project: "Memoria" (identifier: `MEMORIA`) — use the `plane` CLI for all operations. Issues labeled `epic` are parent containers — skip them and work their children. See **[docs/plane.md](docs/plane.md)** for full details.

### Branching

Single-trunk model: `main` is the only long-lived branch. Cut a short-lived
`<type>/<slug>` branch off `main`, open a PR, and **squash-merge to
`main`**. There is no `develop` branch and no module/parent branches.

**This repository is public.** Tracker URLs and work-item IDs never go in branch names, commit messages, PRs, code comments, or documentation here. Cross-reference from the tracker to the PR, never the reverse.

**Never close or merge a pull request you did not open in the current session.**
If a PR looks like a blocker, report it and stop — do not close it.

`main` is protected by the `main protection` ruleset: no direct pushes, no force
pushes, no deletion, and all seven required CI checks must pass. Those job names
must not change, and they must always report, so path filtering in `ci.yml`
happens per job and never under `on:`. `non_fast_forward` has no
API bypass — recovering from a bad merge means temporarily disabling "Block force
pushes" in Settings → Rules → Rulesets, then re-enabling it.

### Commits

Conventional Commits: `<type>(<scope>): <description>`. Scopes track the layout: `api`, `core`, `cli`, `sidecar`, `worker`, `ingest`, `auth`, `e2e`, `docker`, `docs`, `ci`.

## Build and Test

```bash
dotnet build Memoria.slnx -p:TreatWarningsAsErrors=true   # build (CI parity)
dotnet test Memoria.slnx                                  # test
dotnet format Memoria.slnx --verify-no-changes            # format gate (CI parity)
sh scripts/redaction-report.sh <corpus-dir>               # local release check: gitleaks and trufflehog over redacted output, counts only. Not for CI
dotnet test tests/Memoria.Core.Tests --filter "Category=Timing"   # the redaction time budgets, uninstrumented. The coverage and mutation runs leave them out; Build & Test and the pre-commit hook run them on their own
MEMORIA_REDACTION_REPORT=<dir> dotnet test tests/Memoria.Core.Tests --filter RedactionMeasurement   # opt-in: this repo's lines redaction changes, and sampled tokens caught; compare <dir>/redaction-report.txt before and after a rule change
MEMORIA_WRITE_OPENAPI=1 dotnet test Memoria.slnx --filter OpenApiContract   # refresh src/Memoria.Api/openapi.json after an endpoint change
npx --yes @stoplight/spectral-cli@6.17.0 lint --fail-severity warn src/Memoria.Api/openapi.json   # lint gate (CI parity)

cd cli
go build ./...                                            # build CLI
go test ./... -race                                       # test CLI (-race needs cgo)
gofmt -l .                                                # must print nothing
go vet ./...
go run github.com/golangci/golangci-lint/v2/cmd/golangci-lint@v2.15.0 run ./...   # complexity gate
go mod tidy && git diff --exit-code go.mod go.sum         # tidy gate (CI parity)
go generate ./... && git diff --exit-code internal/api    # regenerate the API client from src/Memoria.Api/openapi.json (CI parity)

cd sidecar
npm run typecheck                                         # tsc --noEmit
npm test                                                  # unit tests (fake backend, no SDK load)
npm run test:coverage                                     # unit tests with the branch and line coverage gates
npm run openapi:check                                     # committed openapi.json matches routes
npm run build                                             # emit dist/ for the service
MEMORIA_QMD_INTEGRATION=1 npm run test:integration        # real SDK on a temp fixture index, mock worker
MEMORIA_QMD_WORKER_URL=... npm run test:live              # opt-in, against a real worker
MEMORIA_QMD_WORKER_URL=... npm run bench:parity -- --db <index>   # opt-in, in-process versus worker
```

The sidecar's `npm test` and `npm run typecheck` also cover `worker/` and `scripts/release/`, and its typecheck covers `scripts/e2e/`. CI runs `npm run test:coverage` in `sidecar/` and in `scripts/release/`. The worker has its own `npm test`, `npm run test:coverage`, and `npm run typecheck`, and its own CI job.

```bash
docker build -t memoria-api:local .                       # the image CI publishes to ghcr.io/mggarofalo/memoria
```

Seven gates are required status checks on `main`: `Build & Test`, `Code Formatting`, `CLI Build & Test`, `CLI Lint`, `Sidecar Build & Test`, `Worker Build & Test` and `Docker Image`. `Sidecar Build & Test` and `Worker Build & Test` run the Node commands above, and `Docker Image` (`.github/workflows/docker.yml`) builds and verifies the image on every event, and a separate `Publish Image` job publishes it on pushes to `main` and release tags.

### Releases

The only version in this repository is a `vX.Y.Z` tag, computed from Conventional Commits by `node scripts/release/release.ts next`. Never write a version number into a csproj, `package.json`, or Go file. Use the `release` skill (`.claude/skills/release/SKILL.md`) to cut one. **[docs/releasing.md](docs/releasing.md)** has the rules, what each component stamps, and the CLI asset contract.

## Quality Gates

| Gate | C# | TypeScript (sidecar, worker, scripts/release) | Go (cli) |
|---|---|---|---|
| Cyclomatic complexity, per function | CA1502, a build error. Limit in `CodeMetricsConfig.txt` | ESLint `complexity`. Limit in `eslint.config.mjs` | golangci-lint `gocyclo`. Limit in `cli/.golangci.yml` |
| Coverage | **Branch**. `scripts/coverage-dotnet.sh` merges both test projects | **Branch and line**. `npm run test:coverage` in each package, limits in its `.c8rc.json` | **Statement**, not branch. `scripts/coverage-go.sh` |
| Mutation (the mutation runner, never a hook) | Stryker.NET, `stryker/*.json` | StrykerJS, `stryker*.json` in `sidecar/` and `worker/` | Gremlins, `cli/.gremlins.yaml` |

- **Limits:** complexity 10 per function, and coverage 90 percent or better. Never loosen a limit; tighten it when the code allows.
- **Generated code is excluded,** and nothing else: `cli/internal/api/client.gen.go`, and source-generated C# (`coverage.runsettings`). Do not add `CompilerGeneratedAttribute` to the coverage exclusions: async methods compile to state machines that carry it, so it silently removes real code.
- **Node coverage uses c8 with `all`,** so a source file no test loads counts as uncovered instead of vanishing (`sidecar/src/main.ts`). Only type-only files are left out (`sidecar/src/llm/types.ts`). V8 cannot see branches inside a function that never runs, so a file can read 100 percent of branches on 53 percent of lines (`sidecar/src/store.ts`, which only the integration tests reach). That is why line coverage is gated too.
- **Go's blocking gate is statement coverage, not branch coverage.** Go's tooling has no native branch metric. `gobco` measures real branches but handles one package at a time, cannot exclude generated files, and has no threshold, so `scripts/coverage-go-branch.sh` wraps it as a non-blocking report run by hand (`Go Branch Coverage (report)` in `mutation.yml`).
- **The CA1502 config file must be named exactly `CodeMetricsConfig.txt`.** With any other name the rule passes without checking anything.

### Git hooks

`lefthook.yml` runs on pre-commit, scoped to the language you staged: format, lint, complexity, and tests. The TypeScript and Go tests run with their coverage gates. The C# tests run without coverage collection, which made a commit take four minutes, and CI's Build & Test keeps the C# coverage gate. A commit that changes only docs, skills or a package README runs the tests that hold them to the code. The hook warns, without stopping the commit, when the local Node is older than `package.json`'s engines. Mutation testing never runs in a hook. `git commit --no-verify` exists for emergencies; CI runs the same gates.

Hooks work from PowerShell, cmd, and Git Bash: `scripts/run-sh.mjs` finds the `sh` that ships with Git for Windows and says what to do if there is none. Run a script by hand the same way: `node scripts/run-sh.mjs scripts/coverage-dotnet.sh`. `-race` is left to CI because it needs cgo.

### Mutation testing

Mutation testing never runs on a pull request or on hosted runners by schedule. Commits on `main` are mutated by the mutation runner, a compose project in `deploy/mutation-runner/` on a box of our own (**[docs/mutation-runner.md](docs/mutation-runner.md)**). It coalesces the commits that arrive together into one run on the newest, covering every package changed since the last mutated commit, and posts a commit status named `mutation`, which no ruleset requires. By default it mutates only the changed files: `--since` for Stryker.NET, and the changed files for StrykerJS and Gremlins (`MUTATION_BASE` in the scripts). `.github/workflows/mutation.yml` runs only by hand: it mutates everything and uploads the reports as artifacts, even from a run that fails. Its sidecar run is too big for one job's four hours, so `scripts/mutation-shard.mts` splits its files into eight shards, balanced by line count, and each shard is a job of its own under the sidecar's thresholds; the first also runs the security config. A full Memoria.Core run takes hours too, so it also runs as the shards in `stryker/shards.json`, which mutate every source file exactly once and can run in parallel or be rerun one at a time: `MUTATION_SHARD="core redaction 1" sh scripts/mutation-dotnet.sh`, with the names from `node scripts/mutation-dotnet-shards.mjs --names`. Each shard keeps its config's thresholds. Rebalance the file when a shard grows. The mutation runner runs, one after another, only the .NET shards that hold a changed file, or every shard when it mutates whole packages. The Stryker.NET configs ignore mutations inside `ConfigureAwait`: flipping it only changes which context a continuation resumes on, which no test of behaviour can see. StrykerJS concurrency is fixed in each package's config, the same in CI and by hand: 2 for the worker, whose tests each start several processes, and 4 for the sidecar. Authentication, scoping, and redaction code has its own stricter config (`stryker/security.json`, `stryker/security-core.json`, and `stryker.security.json` in `sidecar/` and `worker/`). StrykerJS runs from the repository root, because sidecar tests read files elsewhere in it; `sidecar/test/install-scripts.test.ts` is left out of its sandbox because it checks the repository's own scripts, and `sidecar/test/docs-match-code.test.ts` because it reads the docs and the source, which in the sandbox is instrumented. Run one by hand with `node scripts/run-sh.mjs scripts/mutation-js.sh sidecar`, and likewise `mutation-dotnet.sh` or `mutation-go.sh`. Two local caveats: `--since` reads the main checkout's HEAD when run from a git worktree, and Gremlins v0.6.0 misreads coverage on Windows, so `scripts/mutation-go.sh` refuses to run there. The mutation runner, which runs both on Linux in a clone of its own, has neither problem.

### Other CI

`.github/workflows/audit.yml` runs `npm audit`, `govulncheck`, and `dotnet list package --vulnerable` daily and whenever a dependency file changes. `.github/workflows/security.yml` runs CodeQL and gitleaks. The `End-to-end (real API and sidecar)` job in `ci.yml` runs `scripts/e2e`, and `End-to-end (real reranker on CPU)` runs its reranked searches against a real worker with cached, checksum-pinned models. None of these is required, so a new advisory cannot block an unrelated pull request. Actions are pinned by commit SHA; Dependabot keeps the pins, and the dependencies, up to date in weekly grouped pull requests.

## Architecture

Memoria is a **front door over QMD**, not a search engine. QMD owns retrieval — BM25 via FTS5, vectors via sqlite-vec, RRF fusion, and LLM reranking. Memoria owns everything QMD deliberately does not: network exposure, authentication, authorization, multi-source ingest, and client tooling.

```
clients (Claude Code / CLI / scripts)
        │  HTTPS + Bearer API key
        ▼
   Memoria.Api  (container on the API host) ──── auth, scoping, rate limiting, audit
        │  one bridge network, plain REST + shared-secret bearer
        ▼
   memoria-qmd sidecar  (container on the same host, sidecar/) ── imports the QMD SDK in-process
        │                                   │  WireGuard
        ▼                                   ▼
   index.sqlite  ← derived, rebuildable     memoria-worker (GPU hosts, worker/) ── runs the models
        ▲
   ingest workers ── redaction → document envelope → POST /documents → markdown corpus
```

| Path | Contains |
|---|---|
| `src/Memoria.Api/` | ASP.NET Core host: REST endpoints, outward MCP server, auth handler, ingest |
| `src/Memoria.Core/` | Document envelope, search contracts, session rendering, redaction rules |
| `skills/memoria/` | The agent skill for searching and reading the corpus. Installed at cutover, so it stays out of `.claude/skills`, where it would be live in this repository. `cli/cmd/skill_test.go` and `scripts/e2e/skill.ts` keep it true to the CLI and the MCP tools. These files are outside the Go module, so Go's test cache cannot see them change: run `go test -count=1 ./cmd -run Skill` |
| `skills/memoria-ops/` | The operator skill: issuing and rotating keys, draining workers, the queue, and readiness by trace id. It uses the CLI only, asks before every command that changes what others can do, and stays out of `.claude/skills` like the search skill. `cli/cmd/skill_ops_test.go` keeps it true to the CLI and free of private detail; `skills/evals/memoria-ops-triggers.json` holds its trigger prompts |
| `tools/RedactionCheck/` | Local report behind `scripts/redaction-report.sh` and `memoria migrate report`. Not part of the solution. The required build job builds it, and `scripts/e2e` runs it on fixtures |
| `tests/Memoria.Api.Tests/` | xUnit tests against `WebApplicationFactory<Program>` with a scripted `FakeSidecar` handler. `MemoriaApiFactory` opts in to disabled auth; `AuthApiFactory` enforces keys on a temp control database. |
| `tests/Memoria.Core.Tests/` | Client unit tests and `SidecarContractTests`, which pins the C# wire types to `sidecar/openapi.json` |
| `sidecar/` | `memoria-qmd`: TypeScript REST service over the QMD SDK; `openapi.json` is its contract |
| `worker/` | `memoria-worker`: supervises `llama-server` for QMD's models on a GPU host and serves them to the sidecar. No runtime npm dependencies; runs from source. See `docs/workers.md` |
| `cli/` | Go module `github.com/mggarofalo/memoria/cli`, binary `memoria`; `internal/api` is the client oapi-codegen generates from `src/Memoria.Api/openapi.json` |
| `scripts/release/` | Version computation, CLI asset builds, and release gates; no dependencies, runs on Node. See `docs/releasing.md` |
| `deploy/` | `compose.yaml` for the API host: the API and the sidecar, with no published sidecar port; `windows/MemoriaService.psm1`, used by the worker's Windows service scripts |
| `Dockerfile` | `Memoria.Api` only. `sidecar/Dockerfile` is the sidecar's image, with no llama.cpp binaries (docs/deployment.md) |
| `scripts/e2e/` | The real CLI against a real API and sidecar on a throwaway index. See its README. Extend it when a change crosses those boundaries |
| `install.sh`, `install.ps1` | CLI installers; they consume the release assets described in `docs/cli.md` |
| `docs/` | Plane workflow (`plane.md`), API configuration (`configuration.md`), deployment (`deployment.md`), inference workers (`workers.md`), releases (`releasing.md`), the CLI (`cli.md`), migrating a local QMD setup (`migration.md`) |

**Inside `Memoria.Api`:** `Configuration/` (the `Memoria` options root and its validator), `Sidecar/` (typed-client registration and trace propagation), `Auth/` (API-key handler and token format, the SQLite control database and key store, rate limiting, audit), `Http/` (request logging, problem-details mapping, `MemoriaScope` enforcement, `ICallerScope`, `SidecarDisclosure`), `Endpoints/` (`/health`, `/ready`, `/v1/*`, `/v1/admin/keys`, the operations under `/v1/admin` that proxy the sidecar's worker, queue and reindex routes, and the ingest routes `/v1/documents`, `/v1/ingest/session` and `/v1/ingest/rendered`), `Ingest/` (the document and session writers, `CollectionReindexer`, `IngestSupport`, `IngestLedger`, which orders versions and tells a finished session from a half-written one, and `IngestRedactionPolicy`, the only reader of the per-collection redaction setting), `Search/` (`SearchRequestFactory`, and `ScopedCorpus`, the only way REST and MCP read the sidecar, which applies scope and reports audit data), `Mcp/` (the outward MCP server), `OpenApi/` (document, operation and schema transformers that keep the committed spec generator-friendly), `Hosting/` (the container health-check and admin-key bootstrap switches). The sidecar client itself (`IMemoriaQmdClient`, its wire records) lives in `Memoria.Core/Sidecar/`; the public v1 contracts in `Memoria.Core/Contracts/`. When the sidecar contract changes, update the records in `Memoria.Core/Sidecar/SidecarContracts.cs` — `SidecarContractTests` fails until they match the committed `openapi.json`.

**Key invariants:**

- **QMD is never exposed directly.** The sidecar is the only process that opens the index. It runs beside `Memoria.Api` on one Linux host and publishes no port: only the API reaches it, on their own bridge network, and with a shared secret. Workers on other machines serve its models over WireGuard and never open the index. All external traffic goes through `Memoria.Api`. QMD ships no authentication whatsoever — the API is the only thing standing between the corpus and the internet. QMD's own MCP HTTP transport is not used: it binds `localhost` only, requires an `Mcp-Session-Id` handshake, and cannot filter on frontmatter.
- **Redaction is a precondition of indexing, not a post-filter, on by default.** Transcripts carry live credentials. If a document reaches the index unredacted, the secret is already searchable. The one exception is an explicit operator choice per collection, `Memoria:Ingest:Collections:<name>:Redaction = Disabled`, compared exactly; anything else is redacted. It is never a request parameter, key attribute, header or envelope field, so no caller can reach it. `SidecarIngestRequest.Content` is a `RedactedMarkdown`, which means "went through the redaction decision": only `Redactor`, the session renderer's final check (`PartGate`) and `RedactedMarkdown.PassThroughForDisabledCollection` can create it, the latter needs a `RedactionDisabledCollection` from `IngestRedactionPolicy` and the client refuses to send its text to any other collection, so skipping the decision fails to compile. `SessionRenderer` redacts every metadata string and all message text, joined so a key spread across messages is one key, before the title, digest, sizing, and split, then checks each finished part and replaces any field or message still holding a hit with a marker. It refuses the session only on a blown budget or an internal fault.
- **Ingest collections are opt-in and exclusive.** Ingest writes only to a collection with `Memoria:Ingest:Collections:<name>:Enabled = true`, and refuses unless its directory is empty when ingest first uses it. Everything inside then belongs to Memoria.
- **API keys are scoped to collections.** Never issue an all-collections key by default; the corpus mixes sensitivity levels. An all-collections key takes the explicit `allCollections: true`, never an omission.
- **Auth fails closed.** `Memoria:Auth:Mode` defaults to `ApiKey`, and a fallback authorization policy covers any endpoint without an explicit `AllowAnonymous`. New `/v1` routes get `MemoriaScopeAttribute` metadata from their group. Scopes do not imply each other: `admin` does not grant `read`.
- **Operations act on every collection.** The worker, queue and reindex routes under `/v1/admin` need the `admin` scope and a key that lists no collections (`MemoriaUnboundKeyAttribute`, an authorization requirement, so it is decided before the body is read). Drain and pause live only in the sidecar's memory: the sidecar keeps no durable state beyond its job queue, so a restart returns it to its configuration.
- **Never pass an empty collection list to the sidecar.** It reads `collections: []` as "the default collections", so it would widen the caller's scope. Empty scope means no results. Read the corpus through `ScopedCorpus`, not `IMemoriaQmdClient`, so results are re-checked against the scope.
- **Sidecar errors are not safe to forward.** `similarFiles`, multi-get `errors` and `excluded_by_ignore` quote paths from any collection; transport errors quote the sidecar's address. Pass them through `SidecarDisclosure`.
- **The client address comes from the socket unless the peer is a configured proxy.** `Memoria:Auth:TrustedProxies` is empty by default and the framework's implicit loopback trust is cleared. Failed authentication is reserved per address before the control-database lookup, and a valid key gets it back; `KnownGoodKeys` lets a matching secret past an exhausted budget but never decides acceptance. Key-based limits and audit run only after a key is valid.
- **Caller globs never reach QMD.** Its picomatch can be wedged by one pattern on the sidecar's single event loop. The grammar is enforced in both `Search/MultiGetPolicy.cs` and `sidecar/src/multi-get.ts` (`sidecar/test/glob-policy-cases.json` pins them together), and the sidecar matches with `matchGlob` and hands QMD exact URIs.
- **Reads go to the sidecar with their limits.** The SDK's miss path scans every path and its hit path reads the body, so a lookup resolved globally leaks existence and size through timing and can block the event loop. `ScopedCorpus` always sends `similarFiles: false`, the caller's allowlist, and the response byte budget; the sidecar resolves references itself, sizes documents first, and reads only what fits. Post-filtering in `ScopedCorpus` is defence in depth, not the control.
- **Scope comparison is ordinal.** QMD's collection names are case-sensitive even though its path lookup is not: check the canonical `collection` in the sidecar's response, never the caller's reference.
- **Key material is never logged.** Only the public key id goes in logs and the audit trail. The control database holds SHA-256 hashes; plaintext exists only in the response that issued it.
- **The markdown corpus is the durable artifact.** `index.sqlite` is derived and may be deleted and rebuilt at any time. Never treat it as a source of truth. The control database (`Memoria:Auth:DatabasePath`) is different: it is durable state and is not rebuildable.
- **Every request is traceable.** `Memoria.Api` returns `X-Trace-Id`, logs one structured line per request, and forwards the same id to the sidecar as `request-id` (its pino `reqId`) plus W3C `traceparent`. Problem-details responses carry `traceId`. Keep new endpoints inside the `/v1` group so they inherit this and the scope metadata.
- **Reindexing is serialized.** Concurrent `qmd update`/`qmd embed` runs contend over the model and the database. Ingest enqueues; a single worker drains (`POST /reindex`, `POST /documents` and `DELETE /documents` on the sidecar share one queue). Document writes are debounced so a burst costs one scan; the debounce clock is injected (`JobScheduler`), so tests never sleep.

## Coding Standards

**C#:** File-scoped namespaces, `sealed` by default, nullable enabled, primary constructors where they read cleanly. Style is enforced by `.editorconfig` and gated by `dotnet format`.

**TypeScript (sidecar/):** ESM, strict `tsconfig`, Node's native type stripping for tests (`erasableSyntaxOnly`: no enums, no parameter properties, `import type` for types, `.ts` extensions on relative imports). `@tobilu/qmd` is imported in exactly one file, `src/store.ts`; everything else depends on the `QmdBackend` interface so tests run on the in-memory fake. Route changes must regenerate `openapi.json` (`npm run openapi`). `MEMORIA_QMD_LLM=local` is the default, and `src/llm/` must stay unloaded in that mode. `test/llm-surface.test.ts` pins remote mode to the exact SDK version, so re-read the SDK when it fails rather than only updating the list. Worker responses are untrusted input, and the worker token must never reach a log or an error message (`docs/workers.md`).

**OpenAPI (Memoria.Api):** the served document is pinned to `src/Memoria.Api/openapi.json` by `OpenApiContractTests`. After changing an endpoint or contract record, refresh it with `MEMORIA_WRITE_OPENAPI=1 dotnet test Memoria.slnx --filter OpenApiContract`, then `cd cli && go generate ./...` so the Go client follows. Both drifts fail CI.

**Go:** Mirror the conventions in `~/Source/plane-cli` — cobra command tree and a resolver precedence of flag → env → keyring → file — as a default, not a reason to keep risk. The CLI deliberately diverges where a credential-holding binary needs it: `zalando/go-keyring` on Windows and Linux with `/usr/bin/security` on macOS instead of `99designs/keyring`, and an in-house updater instead of `go-selfupdate`. `gofmt` is authoritative. In `cli/`, `internal/transport` is the only code that puts an API key on the wire; the key never reaches stdout, stderr, logs, error messages, spawned process arguments or `config.json`, and tests assert this. See `docs/cli.md`.

**Line endings:** C# is CRLF, everything else is LF. Enforced in `.gitattributes` and mirrored in `.editorconfig`. After changing either, run `git add --renormalize .`.

## Agent Rules

**All new functionality must include tests.** Endpoints, handlers, auth logic, redaction rules, and CLI commands ship with tests in the same PR. Never defer tests to a follow-up.

Redaction rules specifically require tests with realistic positive **and** negative cases — a rule that over-matches silently destroys corpus content, and one that under-matches leaks credentials.

**Never modify CI configuration or branch protection** unless explicitly asked.
