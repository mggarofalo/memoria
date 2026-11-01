# Plane

Guidance for AI agents working with the Plane workspace for this project.

## Workspace

- **Project:** Memoria (identifier: `MEMORIA`)
- **CLI:** `plane` — use `plane --help` and `plane <command> --help` to discover commands. If you hit bugs or missing features in the CLI, file them at https://github.com/mggarofalo/plane-cli
- **Output:** `-o json` (default) for programmatic parsing, `-o table` for human-readable

The CLI supports name resolution — use state names, label names, and member names instead of UUIDs.

## States

`Backlog` → `Todo` → `In Progress` → `Done`, plus `Cancelled`.

## Labels

Every issue needs at least one **layer** label. Add **type** labels as appropriate.

| Layer | Type |
|-------|------|
| `api`, `cli`, `ingest`, `infra`, `docs` | `Feature`, `Improvement`, `Bug`, `security`, `cleanup`, `dx`, `testing`, `epic` |

Issues labeled `epic` are parent containers — skip and work their children.


## Modules

Phases are tracked as Plane **modules**. Use `plane module list --all -p MEMORIA` to discover them and their status.

**Every issue must be attached to a module.** `plane issue create` does NOT accept a `--module-id` flag — attach via a separate call after creation:

```bash
ID=$(plane issue create --name "..." --labels api --priority medium --id-only -p MEMORIA)
plane module add-work-items --module-id "Host" --issues "$ID" -p MEMORIA
```

Modules: **Host**, **API**, **Auth**, **Ingest**, **CLI**, **Sources**, **Workers**, and **Maintenance Backlog** — the default bucket for hardening and bug fixes that don't belong to a phase.

## Epics

Seven epics track the phases, each attached to its matching module. Work their children, not the epics themselves.

| Issue | Module | Priority |
|---|---|---|
| `MEMORIA-1` Host: provision the always-on QMD node | Host | Urgent — ready now |
| `MEMORIA-2` API: Memoria.Api front door over QMD | API | High |
| `MEMORIA-3` Auth: API keys and collection scoping | Auth | Medium |
| `MEMORIA-4` Ingest: redaction, envelope, and queue | Ingest | Medium |
| `MEMORIA-5` CLI: the memoria Go binary | CLI | Medium |
| `MEMORIA-6` Sources: sync workers beyond transcripts | Sources | Low |
| `MEMORIA-12` Workers: inference pool and control plane behind one memoria ingress | Workers | High |

`MEMORIA-1` is the only epic in Todo. Its `qmd query` benchmark gates the API's default search mode, so it blocks meaningful progress on `MEMORIA-2`.

`MEMORIA-12` decouples model inference from the index: the sidecar on the always-on node keeps the index and routes model calls to GPU workers (desktop, MacBook) through QMD's `llm` seam. Its children are `MEMORIA-13` through `MEMORIA-21`; `MEMORIA-13` (remote LLM adapter), `MEMORIA-14` (worker runtime) and `MEMORIA-15` (parity benchmark) are ready now. It changes what `MEMORIA-1` and `MEMORIA-8` mean once `MEMORIA-19` moves the index: the QMD node needs no GPU, and sleeping the desktop costs rerank quality rather than availability.

## Priority

Priority reflects **execution readiness**, not importance: Urgent = ready now, High = one step away, Medium = blocked by 2+, Low = far future.

## What's Next

1. List issues in Backlog/Todo state, exclude Done/Cancelled/Duplicate
2. Skip `epic`-labeled issues — work their children
3. Skip issues with unresolved blockers
4. Pick the highest-priority unblocked issue

## Issue Workflow

**Start:** `plane issue update <id> -p MEMORIA --state "In Progress"` — branch as `<type>/memoria-<id>-<slug>`

**Finish:** `plane issue update <id> -p MEMORIA --state Done` — check whether this unblocks downstream issues

**Create:** Always use `-p MEMORIA`, attach a module via `plane module add-work-items`, set priority by readiness, add at least one layer label

## CLI Quick Reference

```bash
# Fetch an issue (the CLI's own flag names; `--identifier` does not exist)
plane issue get-by-sequence-id --project-identifier MEMORIA --issue-identifier <n> -o json

# Update issue state (`--resource-id`, not `--work-item-id`; state names resolve)
plane issue update -p MEMORIA --resource-id <UUID> --state "In Progress"

# Create a new issue. `--labels` takes label UUIDs only (names do not resolve here);
# `--parent` accepts a UUID or a sequence ID such as MEMORIA-12
plane issue create -p MEMORIA --name "<title>" --description-html "<description>" --priority medium --labels <label-uuid> --id-only

# Add a comment
plane comment add -p MEMORIA --work-item-id <UUID> --comment-html "<html>"

# List states / labels / modules
plane state list -p MEMORIA -o json
plane label list -p MEMORIA -o json
plane module list --all -p MEMORIA -o json
```
