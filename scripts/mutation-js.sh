#!/bin/sh
# StrykerJS for one package: scripts/mutation-js.sh <sidecar|worker>
# With MUTATION_BASE set (pull requests), mutates only the changed files in src/.
# Otherwise (nightly) mutates everything. Never run this from a git hook.
#
# Runs from the repository root, because some sidecar tests read files elsewhere
# in the repository, and those paths only resolve if the sandbox has the same shape.
#
# The two copies of secret-file.ts must stay identical and a sidecar test says so,
# which a mutant in the sidecar copy would always trip. The worker's run covers it.
#
# With MUTATION_LIMIT_MINUTES set, running out of time is reported as inconclusive
# instead of failing, as in mutation-dotnet.sh.
set -eu

package=$1
config="$package/stryker.config.json"
security="$package/stryker.security.json"
started=$(date +%s)
status=0

# Usage: mutate <what> <stryker run arguments>
mutate() {
  what=$1
  shift
  limiter=""
  if [ -n "${MUTATION_LIMIT_MINUTES:-}" ]; then
    left=$((MUTATION_LIMIT_MINUTES * 60 - $(date +%s) + started))
    if [ "$left" -le 0 ]; then
      inconclusive "$what"
      return
    fi
    limiter="timeout $left"
  fi
  code=0
  # shellcheck disable=SC2086 # $limiter is empty or meant to split
  $limiter node node_modules/@stryker-mutator/core/bin/stryker.js run "$@" || code=$?
  if [ "$code" -eq 124 ] && [ -n "$limiter" ]; then
    inconclusive "$what"
  elif [ "$code" -ne 0 ]; then
    status=1
  fi
}

inconclusive() {
  message="Mutation ($package) is inconclusive: the time limit ended before $1 finished. No score for it, and this is not a failure. The nightly run covers it."
  echo "$message"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    echo "$message" >> "$GITHUB_STEP_SUMMARY"
  fi
}

if [ -n "${MUTATION_BASE:-}" ]; then
  changed=$(git diff --name-only --diff-filter=d "$MUTATION_BASE"...HEAD -- "$package/src" | { grep '\.ts$' || true; } | { grep -v '^sidecar/src/secret-file\.ts$' || true; })
  if [ -z "$changed" ]; then
    echo "No changed source files in $package."
    exit 0
  fi
  mutate "the changed files" "$config" --mutate "$(echo "$changed" | paste -sd, -)"
else
  mutate "the full run" "$config"
fi

if [ -f "$security" ]; then
  mutate "the security run" "$security"
fi
exit "$status"
