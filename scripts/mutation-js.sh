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
set -eu

package=$1
stryker="node node_modules/@stryker-mutator/core/bin/stryker.js"
config="$package/stryker.config.json"
security="$package/stryker.security.json"
status=0

if [ -n "${MUTATION_BASE:-}" ]; then
  changed=$(git diff --name-only --diff-filter=d "$MUTATION_BASE"...HEAD -- "$package/src" | { grep '\.ts$' || true; } | { grep -v '^sidecar/src/secret-file\.ts$' || true; })
  if [ -z "$changed" ]; then
    echo "No changed source files in $package."
    exit 0
  fi
  # shellcheck disable=SC2086 # word splitting of the command is intended
  $stryker run "$config" --mutate "$(echo "$changed" | paste -sd, -)" || status=1
else
  # shellcheck disable=SC2086
  $stryker run "$config" || status=1
fi

if [ -f "$security" ]; then
  # shellcheck disable=SC2086
  $stryker run "$security" || status=1
fi
exit "$status"
