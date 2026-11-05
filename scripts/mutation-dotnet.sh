#!/bin/sh
# Stryker.NET: scripts/mutation-dotnet.sh <config>, a name from stryker/*.json.
# Never run this from a git hook.
#
# Nightly (no MUTATION_BASE) mutates everything and holds every threshold.
#
# On a pull request (MUTATION_BASE set) it mutates the changed files, and Stryker
# tests every mutant in a file it touched, including ones older than the change. So
# there the general configs only report: a one-line fix must not have to pay for a
# file's history, and the nightly still holds that bar. Security configs keep their
# threshold, because the nightly keeps that code at the bar, so falling below it
# means this change did it.
#
# With MUTATION_LIMIT_MINUTES set, running out of time is reported as inconclusive
# instead of failing. Stryker.NET writes its score at the end, so a run that runs
# out of time has no score to report.
set -eu

config=$1

inconclusive() {
  message="Mutation (.NET, $config) is inconclusive: the time limit ended before it finished. No score, and this is not a failure."
  echo "$message"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    echo "$message" >> "$GITHUB_STEP_SUMMARY"
  fi
}

set --
if [ -n "${MUTATION_BASE:-}" ]; then
  set -- "--since:$MUTATION_BASE"
  case "$config" in
    security*) ;;
    *) set -- "$@" --break-at 0 ;;
  esac
fi

# Stryker's analysis otherwise restores each project in parallel, which is where
# "simulated build failed" and "Failed to analyze project builds" have come from.
dotnet restore Memoria.slnx
dotnet tool restore

limiter=""
if [ -n "${MUTATION_LIMIT_MINUTES:-}" ]; then
  limiter="timeout $((MUTATION_LIMIT_MINUTES * 60))"
fi

code=0
# shellcheck disable=SC2086 # $limiter is empty or meant to split
$limiter dotnet tool run dotnet-stryker --config-file "stryker/$config.json" \
  --output "StrykerOutput/$config" "$@" || code=$?

if [ "$code" -eq 124 ] && [ -n "$limiter" ]; then
  inconclusive
  exit 0
fi
exit "$code"
