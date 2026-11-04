#!/bin/sh
# Gremlins for cli/. Thresholds live in cli/.gremlins.yaml.
# With MUTATION_BASE set (pull requests), mutates only the changed files.
# Otherwise (nightly) mutates everything. Never run this from a git hook.
#
# Gremlins' own --diff is not used: with the module in a subdirectory its paths
# never match the diff's, so it skips every mutant and reports success. Its
# coverage detection also fails on Windows, where every mutant reads as not
# covered, so trust this script's results on Linux and macOS only.
set -eu

cd cli
gremlins="go run github.com/go-gremlins/gremlins/cmd/gremlins@v0.6.0"

if [ -n "${MUTATION_BASE:-}" ]; then
  changed=$(git diff --relative --name-only --diff-filter=d "$MUTATION_BASE"...HEAD -- . | { grep '\.go$' || true; })
  if [ -z "$changed" ]; then
    echo "No changed Go files."
    exit 0
  fi
  for file in $(git ls-files '*.go'); do
    if ! echo "$changed" | grep -qxF "$file"; then
      set -- "$@" --exclude-files "^$file\$"
    fi
  done
  # shellcheck disable=SC2086 # word splitting of the command is intended
  $gremlins unleash "$@"
else
  mkdir -p reports
  # shellcheck disable=SC2086
  $gremlins unleash --output reports/gremlins.json
fi
