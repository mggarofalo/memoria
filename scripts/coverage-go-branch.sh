#!/bin/sh
# Real Go branch coverage, as a slow nightly REPORT. It never blocks anything:
# the blocking gate is statement coverage in scripts/coverage-go.sh.
#
# gobco can only take one package at a time, has no threshold, and cannot
# exclude generated files, so this runs it per package and tallies its listing
# without client.gen.go. Each condition is two branches, true and false.
# Writes cli/reports/gobco.txt. Exits 0 unless the tool itself could not run.
set -eu

cd "$(dirname "$0")/../cli"
mkdir -p reports
gobco="go run github.com/rillig/gobco@v1.3.4"
out=reports/gobco.txt
: > "$out"

total_covered=0
total_branches=0
failed=""

for package in $(go list -f '{{.Dir}}' ./...); do
  relative=".${package#"$PWD"}"
  relative=$(echo "$relative" | tr '\\' '/')
  listing=$(mktemp)
  # shellcheck disable=SC2086 # word splitting of the command is intended
  if ! $gobco -branch -list-all -test -timeout=300s "$relative" > "$listing" 2>&1; then
    failed="$failed $relative"
    echo "$relative: gobco failed; not counted" | tee -a "$out"
    rm -f "$listing"
    continue
  fi
  # The two figures are "covered branches" and "all branches" in this package.
  counts=$(awk '
    / condition / && /\.gen\.go:/ { next }
    / was never evaluated$/ { all += 2; next }
    / but never (true|false)$/ { all += 2; hit += 1; next }
    / times (true|false) and [0-9]+ times (true|false)$/ { all += 2; hit += 2; next }
    END { printf "%d %d", hit, all }
  ' "$listing")
  covered=${counts% *}
  branches=${counts#* }
  total_covered=$((total_covered + covered))
  total_branches=$((total_branches + branches))
  echo "$relative: $covered/$branches" | tee -a "$out"
  rm -f "$listing"
done

if [ "$total_branches" -eq 0 ]; then
  pct="n/a"
else
  pct=$(awk -v c="$total_covered" -v t="$total_branches" 'BEGIN { printf "%.1f%%", 100 * c / t }')
fi
summary="Go branch coverage (gobco, generated code excluded): $pct ($total_covered/$total_branches)"
if [ -n "$failed" ]; then
  summary="$summary. Not counted, gobco failed:$failed"
fi
echo "$summary" | tee -a "$out"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  echo "$summary" >> "$GITHUB_STEP_SUMMARY"
fi
