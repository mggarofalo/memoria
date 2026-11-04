#!/bin/sh
# Gremlins for cli/. Thresholds live in cli/.gremlins.yaml.
# With MUTATION_BASE set (pull requests), mutates only what changed since it.
# Otherwise (nightly) mutates everything. Never run this from a git hook.
set -eu

cd cli
gremlins="go run github.com/go-gremlins/gremlins/cmd/gremlins@v0.6.0"

if [ -n "${MUTATION_BASE:-}" ]; then
  # shellcheck disable=SC2086 # word splitting of the command is intended
  $gremlins unleash --diff "$MUTATION_BASE"
else
  mkdir -p reports
  # shellcheck disable=SC2086
  $gremlins unleash --output reports/gremlins.json
fi
