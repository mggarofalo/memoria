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
# MUTATION_CONFIGS names the configs to run, space-separated; the default is all four.
# MUTATION_MUTATE narrows what they mutate, as space-separated globs, each passed as
# --mutate, where a leading ! excludes. The nightly run uses the two to split the work
# into shards that each fit the job's time limit, and each shard keeps its config's
# thresholds, break included.
set -euf # -f: the globs are Stryker's, not the shell's

since=""
if [ -n "${MUTATION_BASE:-}" ]; then
  since="--since:$MUTATION_BASE"
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
  # shellcheck disable=SC2086 # $limiter, $since and $mutate are empty or meant to split
  $limiter dotnet tool run dotnet-stryker --config-file "stryker/$config.json" \
    --output "StrykerOutput/$config" $since $mutate || code=$?
  if [ "$code" -eq 124 ] && [ -n "$limiter" ]; then
    inconclusive "$config"
  elif [ "$code" -ne 0 ]; then
    status=1
  fi
done
exit "$status"
