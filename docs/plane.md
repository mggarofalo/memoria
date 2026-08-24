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

> Labels are not yet created in the workspace. Create them before the first issue that needs one.

## Modules

Phases are tracked as Plane **modules**. Use `plane module list --all -p MEMORIA` to discover them and their status.

**Every issue must be attached to a module.** `plane issue create` does NOT accept a `--module-id` flag — attach via a separate call after creation:

```bash
ID=$(plane issue create --name "..." --labels api --priority medium --id-only -p MEMORIA)
plane module add-work-items --module-id "Host" --issues "$ID" -p MEMORIA
```

Planned modules: **Host**, **API**, **Auth**, **Ingest**, **CLI**, **Sources**, and **Maintenance Backlog** as the default bucket for hardening and bug fixes that don't belong to a phase.

> Modules are not yet created in the workspace.

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
# Fetch an issue
plane issue get-by-sequence-id --identifier MEMORIA-<n> --expand state,labels,assignees -o json

# Update issue state
plane issue update -p MEMORIA --work-item-id <UUID> --state "In Progress"

# Create a new issue
plane issue create -p MEMORIA --name "<title>" --description-html "<description>" --priority medium

# Add a comment
plane comment add -p MEMORIA --work-item-id <UUID> --comment-html "<html>"

# List states / labels / modules
plane state list -p MEMORIA -o json
plane label list -p MEMORIA -o json
plane module list --all -p MEMORIA -o json
```
