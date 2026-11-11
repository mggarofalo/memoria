#!/bin/sh
# Stryker.NET for Memoria.Core, Memoria.Api, and the security-sensitive subset.
# With MUTATION_BASE set (pull requests), mutates only what changed since it.
# Otherwise (nightly) mutates everything. Never run this from a git hook.
# Every config runs, so one score under its threshold does not hide the others.
#
# With MUTATION_LIMIT_MINUTES set, running out of time is reported as inconclusive
# instead of failing. Stryker.NET writes its score at the end, so a config that
# runs out of time has no score to report; the nightly run has no such limit.
#
# A full run of Memoria.Core takes hours, so it can also run as the shards in
# stryker/shards.json, which together mutate every file exactly once and can run in
# parallel or be rerun one at a time. MUTATION_SHARD=<name> runs one of them under its
# config's thresholds, break included, with its report in StrykerOutput/<config>-<shard>.
# `node scripts/mutation-dotnet-shards.mjs --names` lists them.
#
# Lower level: MUTATION_CONFIGS names the configs to run, space-separated, and
# MUTATION_MUTATE narrows what they mutate, as space-separated globs passed as --mutate,
# where a leading ! excludes.
set -euf # -f: the globs are Stryker's, not the shell's

since=""
if [ -n "${MUTATION_BASE:-}" ]; then
  since="--since:$MUTATION_BASE"
fi

output_suffix=""
if [ -n "${MUTATION_SHARD:-}" ]; then
  shards="$(dirname "$0")/mutation-dotnet-shards.mjs"
  MUTATION_CONFIGS=$(node "$shards" --shard "$MUTATION_SHARD" --field configs)
  MUTATION_MUTATE=$(node "$shards" --shard "$MUTATION_SHARD" --field mutate)
  output_suffix="-$(node "$shards" --shard "$MUTATION_SHARD" --field slug)"
fi

mutate=""
for glob in ${MUTATION_MUTATE:-}; do
  mutate="$mutate --mutate $glob"
done

inconclusive() {
  message="Mutation (.NET) is inconclusive: the time limit ended before $1 finished. No score for it, and this is not a failure. The nightly run covers it."
  echo "$message"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    echo "$message" >> "$GITHUB_STEP_SUMMARY"
  fi
}

dotnet tool restore
# MUTATION_CONCURRENCY sets Stryker.NET's test runners; its default is half the cores.
# Raise it on a machine of its own, not one shared with other work: each runner holds
# a copy of the test suite.
concurrency=""
if [ -n "${MUTATION_CONCURRENCY:-}" ]; then
  concurrency="--concurrency $MUTATION_CONCURRENCY"
fi
started=$(date +%s)
status=0
for config in ${MUTATION_CONFIGS:-core api security security-core}; do
  limiter=""
  if [ -n "${MUTATION_LIMIT_MINUTES:-}" ]; then
    left=$((MUTATION_LIMIT_MINUTES * 60 - $(date +%s) + started))
    if [ "$left" -le 0 ]; then
      inconclusive "$config"
      continue
    fi
    limiter="timeout $left"
  fi
  code=0
  # shellcheck disable=SC2086 # $limiter, $concurrency, $since and $mutate are empty or meant to split
  $limiter dotnet tool run dotnet-stryker --config-file "stryker/$config.json" \
    --output "StrykerOutput/$config$output_suffix" $concurrency $since $mutate || code=$?
  if [ "$code" -eq 124 ] && [ -n "$limiter" ]; then
    inconclusive "$config"
  elif [ "$code" -ne 0 ]; then
    status=1
  fi
done
exit "$status"
