#!/bin/sh
# StrykerJS for one package: scripts/mutation-js.sh <sidecar|worker>
# With MUTATION_BASE set (the mutation runner, deploy/mutation-runner), mutates
# only the files in src/ changed since that commit.
# With MUTATION_SHARD set to <index>/<count> (a dispatched sidecar run, too big for
# one job), mutates that shard of the package's files; see scripts/mutation-shard.mts.
# Each shard runs under the package's own config, so its thresholds apply, and only
# the first shard runs the security config.
# Otherwise mutates everything. Never run this from a git hook.
#
# Runs from the repository root, because some sidecar tests read files elsewhere
# in the repository, and those paths only resolve if the sandbox has the same shape.
#
# The two copies of secret-file.ts must stay identical and a sidecar test says so,
# which a mutant in the sidecar copy would always trip. The worker's run covers it.
set -eu

package=$1
config="$package/stryker.config.json"
security="$package/stryker.security.json"
status=0

# Usage: mutate <stryker run arguments>. A failed threshold fails the script, after every run.
mutate() {
  node node_modules/@stryker-mutator/core/bin/stryker.js run "$@" || status=1
}

if [ -n "${MUTATION_BASE:-}" ]; then
  changed=$(git diff --name-only --diff-filter=d "$MUTATION_BASE"...HEAD -- "$package/src" | { grep '\.ts$' || true; } | { grep -v '^sidecar/src/secret-file\.ts$' || true; })
  if [ -z "$changed" ]; then
    echo "No changed source files in $package."
    exit 0
  fi
  mutate "$config" --mutate "$(echo "$changed" | paste -sd, -)"
elif [ -n "${MUTATION_SHARD:-}" ]; then
  files=$(node scripts/mutation-shard.mts "$package" "$MUTATION_SHARD")
  echo "Shard $MUTATION_SHARD of $package: $files"
  mutate "$config" --mutate "$files"
else
  mutate "$config"
fi

if [ -f "$security" ] && { [ -z "${MUTATION_SHARD:-}" ] || [ "${MUTATION_SHARD%%/*}" = "1" ]; }; then
  mutate "$security"
fi
exit "$status"
