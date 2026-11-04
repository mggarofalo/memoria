#!/bin/sh
# Real Go branch coverage, as a slow nightly REPORT. It never blocks anything:
# the blocking gate is statement coverage in scripts/coverage-go.sh.
#
# gobco can only take one package at a time, has no threshold, and cannot
# exclude generated files, so this runs it per package and tallies its listing
# without client.gen.go. Each condition is two branches, true and false.
# Writes cli/reports/gobco.txt.
set -eu

cd "$(dirname "$0")/../cli"
mkdir -p reports
gobco="go run github.com/rillig/gobco@v1.3.4"
out=reports/gobco.txt
: > "$out"

total_covered=0
total_branches=0
failed=""

note_failure() {
  failed="$failed $1"
  echo "$1: not counted, $2" | tee -a "$out"
}

for package in $(go list -f '{{.Dir}}' ./...); do
  relative=".${package#"$PWD"}"
  relative=$(echo "$relative" | tr '\\' '/')
  listing=$(mktemp)
  # shellcheck disable=SC2086 # word splitting of the command is intended
  if ! $gobco -branch -list-all -test -timeout=300s "$relative" > "$listing" 2>&1; then
    note_failure "$relative" "gobco or its tests failed:"
    tail -n 5 "$listing" | tee -a "$out"
    rm -f "$listing"
    continue
  fi
  # Prints: covered, all, and the same two with generated code, plus gobco's own
  # total. Every condition line must be understood, or the totals disagree.
  counts=$(awk '
    /^Branch coverage: / { split($3, own, "/") }
    / condition / {
      n = 0
      if (/was never evaluated$/) n = 0
      else if (/but never (true|false)$/) n = 1
      else if (/ (true|false) and .* (true|false)$/) n = 2
      else next
      every_hit += n; every_all += 2
      if (!/\.gen\.go:/) { hit += n; all += 2 }
    }
    END { printf "%d %d %d %d %d %d", hit, all, every_hit, every_all, own[1], own[2] }
  ' "$listing")
  rm -f "$listing"
  # shellcheck disable=SC2086 # the six numbers are meant to split
  set -- $counts
  if [ "$3" != "$5" ] || [ "$4" != "$6" ]; then
    note_failure "$relative" "parsed $3/$4 but gobco reported $5/$6"
    continue
  fi
  total_covered=$((total_covered + $1))
  total_branches=$((total_branches + $2))
  echo "$relative: $1/$2" | tee -a "$out"
done

if [ "$total_branches" -eq 0 ]; then
  pct="n/a"
else
  pct=$(awk -v c="$total_covered" -v t="$total_branches" 'BEGIN { printf "%.1f%%", 100 * c / t }')
fi
summary="Go branch coverage (gobco, generated code excluded): $pct ($total_covered/$total_branches)"
if [ -n "$failed" ]; then
  summary="$summary. Not counted:$failed"
fi
echo "$summary" | tee -a "$out"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  echo "$summary" >> "$GITHUB_STEP_SUMMARY"
fi
