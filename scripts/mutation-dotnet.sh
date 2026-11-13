#!/bin/sh
# Stryker.NET for Memoria.Core, Memoria.Api, and the API's authentication and authorization subset.
# With MUTATION_BASE set (the mutation runner, deploy/mutation-runner), mutates
# only what changed since that commit. Otherwise mutates everything. Never run
# this from a git hook. Every config runs, so one score under its threshold does
# not hide the others.
#
# A full run of Memoria.Core takes hours, so it can also run as the shards in
# stryker/shards.json, which together mutate every file exactly once and can run in
# parallel or be rerun one at a time. MUTATION_SHARD=<name> runs one of them under its
# config's thresholds, break included, with its report in StrykerOutput/<config>-<shard>.
# `node scripts/mutation-dotnet-shards.mjs --names` lists them. With MUTATION_BASE
# too, a shard mutates only its own files that changed.
#
# With MUTATION_NO_BREAK set (the mutation runner, on a changed-files run), no
# config's break threshold fails the run, and the caller judges the reports itself.
#
# Lower level: MUTATION_CONFIGS names the configs to run, space-separated, and
# MUTATION_MUTATE narrows what they mutate, as space-separated globs passed as --mutate,
# where a leading ! excludes.
set -euf # -f: the globs are Stryker's, not the shell's

since=""
if [ -n "${MUTATION_BASE:-}" ]; then
  since="--since:$MUTATION_BASE"
fi

break_at=""
if [ -n "${MUTATION_NO_BREAK:-}" ]; then
  break_at="--break-at 0"
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

dotnet tool restore
# MUTATION_CONCURRENCY sets Stryker.NET's test runners; its default is half the cores.
# Raise it on a machine of its own, not one shared with other work: each runner holds
# a copy of the test suite.
concurrency=""
if [ -n "${MUTATION_CONCURRENCY:-}" ]; then
  concurrency="--concurrency $MUTATION_CONCURRENCY"
fi
status=0
for config in ${MUTATION_CONFIGS:-core api security security-core}; do
  # shellcheck disable=SC2086 # $concurrency, $since, $break_at and $mutate are empty or meant to split
  dotnet tool run dotnet-stryker --config-file "stryker/$config.json" \
    --output "StrykerOutput/$config$output_suffix" $concurrency $since $break_at $mutate || status=1
done
exit "$status"
