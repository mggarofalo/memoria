#!/bin/sh
# StrykerJS for one package: scripts/mutation-js.sh <sidecar|worker>
# With MUTATION_BASE set (the mutation runner, deploy/mutation-runner), mutates
# only the files in src/ changed since that commit.
# With MUTATION_SHARD set to <index>/<count> (a dispatched sidecar run, too big for
# one job), mutates that shard of the package's files; see scripts/mutation-shard.mts.
# Each shard runs under the package's own config, so its thresholds apply, and only
# the first shard runs the security config. MUTATION_SHARD=integration instead runs
# the sidecar's stryker.integration.json with MEMORIA_QMD_INTEGRATION=1: the store
# and the SDK wire code, which only the integration tests reach.
# Otherwise mutates everything, that code included. Never run this from a git hook.
# With MUTATION_NO_BREAK set (the mutation runner, on a changed-files run), every
# config runs without its break threshold, and the caller judges the reports itself.
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
integration="$package/stryker.integration.json"
status=0

# Usage: mutate <config> <stryker run arguments>. A failed threshold fails the script, after every run.
mutate() {
  config_file=$1
  shift
  if [ -z "${MUTATION_NO_BREAK:-}" ]; then
    node node_modules/@stryker-mutator/core/bin/stryker.js run "$config_file" "$@" || status=1
    return
  fi
  # StrykerJS takes no threshold on its command line, so it runs a copy of the config without the break. Its paths
  # are the working directory's, not the config file's, so the copy can live anywhere.
  unbroken=$(mktemp -d)
  node -e 'const fs = require("node:fs"); const c = JSON.parse(fs.readFileSync(process.argv[1], "utf8")); c.thresholds = { ...c.thresholds, break: null }; fs.writeFileSync(process.argv[2], JSON.stringify(c));' "$config_file" "$unbroken/stryker.json"
  node node_modules/@stryker-mutator/core/bin/stryker.js run "$unbroken/stryker.json" "$@" || status=1
  rm -r "$unbroken"
}

# Usage: mutate_integrated <stryker run arguments>. As mutate, with the integration tests running.
mutate_integrated() {
  MEMORIA_QMD_INTEGRATION=1
  export MEMORIA_QMD_INTEGRATION
  mutate "$@"
  # The security run is measured without them, as in every other mode.
  unset MEMORIA_QMD_INTEGRATION
}

if [ -n "${MUTATION_BASE:-}" ]; then
  changed=$(git diff --name-only --diff-filter=d "$MUTATION_BASE"...HEAD -- "$package/src" | { grep '\.ts$' || true; } | { grep -v '^sidecar/src/secret-file\.ts$' || true; })
  if [ -z "$changed" ]; then
    echo "No changed source files in $package."
    exit 0
  fi
  # Changed files that only the integration tests reach are mutated with them running.
  reached=""
  if [ -f "$integration" ]; then
    list=$(mktemp)
    node -e 'console.log(JSON.parse(require("node:fs").readFileSync(process.argv[1], "utf8")).mutate.join("\n"))' "$integration" > "$list"
    reached=$(echo "$changed" | { grep -Fxf "$list" || true; })
    changed=$(echo "$changed" | { grep -vFxf "$list" || true; })
    rm -f "$list"
  fi
  if [ -n "$changed" ]; then
    mutate "$config" --mutate "$(echo "$changed" | paste -sd, -)"
  fi
  if [ -n "$reached" ]; then
    mutate_integrated "$integration" --mutate "$(echo "$reached" | paste -sd, -)"
  fi
elif [ "${MUTATION_SHARD:-}" = "integration" ]; then
  mutate_integrated "$integration"
elif [ -n "${MUTATION_SHARD:-}" ]; then
  files=$(node scripts/mutation-shard.mts "$package" "$MUTATION_SHARD")
  echo "Shard $MUTATION_SHARD of $package: $files"
  mutate "$config" --mutate "$files"
else
  mutate "$config"
  if [ -f "$integration" ]; then
    mutate_integrated "$integration"
  fi
fi

if [ -f "$security" ] && { [ -z "${MUTATION_SHARD:-}" ] || [ "${MUTATION_SHARD%%/*}" = "1" ]; }; then
  mutate "$security"
fi
exit "$status"
