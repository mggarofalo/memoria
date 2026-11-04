#!/bin/sh
# StrykerJS for one package: scripts/mutation-js.sh <sidecar|worker>
# With MUTATION_BASE set (pull requests), mutates only the changed files in src/.
# Otherwise (nightly) mutates everything. Never run this from a git hook.
set -eu

cd "$1"
stryker="node ../node_modules/@stryker-mutator/core/bin/stryker.js"
security=stryker.security.json

if [ -n "${MUTATION_BASE:-}" ]; then
  changed=$(git diff --relative --name-only --diff-filter=d "$MUTATION_BASE"...HEAD -- src | { grep '\.ts$' || true; })
  if [ -z "$changed" ]; then
    echo "No changed source files in $1."
    exit 0
  fi
  # shellcheck disable=SC2086 # word splitting of the command is intended
  $stryker run --mutate "$(echo "$changed" | paste -sd, -)"
else
  # shellcheck disable=SC2086
  $stryker run
fi

if [ -f "$security" ]; then
  # shellcheck disable=SC2086
  $stryker run "$security"
fi
