#!/bin/sh
# STATEMENT coverage gate for cli/, not branch coverage: Go's tooling has no
# native branch metric. See "Quality gates" in AGENTS.md for why gobco is not
# used. Extra arguments go to `go test`; CI passes -race.
set -eu

# Phase 1 ratchet: the value measured on main. The target is 90.
MIN_STATEMENT=55

cd "$(dirname "$0")/../cli"

profile=coverage.out
go test ./... -count=1 "$@" -coverprofile="$profile"

# oapi-codegen wrote internal/api/client.gen.go, so it does not count.
filtered=coverage.filtered.out
grep -v '\.gen\.go:' "$profile" > "$filtered"

go tool cover -func="$filtered" | awk -v min="$MIN_STATEMENT" '
  $1 == "total:" {
    found = 1
    pct = $NF; sub(/%/, "", pct)
    printf "Go statement coverage: %s%% (minimum %s%%)\n", pct, min
    if (pct + 0 < min + 0) { print "Below the minimum." > "/dev/stderr"; exit 1 }
    exit 0
  }
  END { if (!found) { print "No coverage total in the report." > "/dev/stderr"; exit 1 } }
'
