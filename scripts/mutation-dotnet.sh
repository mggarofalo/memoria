#!/bin/sh
# Stryker.NET for Memoria.Core, Memoria.Api, and the security-sensitive subset.
# With MUTATION_BASE set (pull requests), mutates only what changed since it.
# Otherwise (nightly) mutates everything. Never run this from a git hook.
# Every config runs, so one score under its threshold does not hide the others.
set -eu

since=""
if [ -n "${MUTATION_BASE:-}" ]; then
  since="--since:$MUTATION_BASE"
fi

dotnet tool restore
status=0
for config in core api security security-core; do
  # shellcheck disable=SC2086 # $since is empty or one argument
  dotnet tool run dotnet-stryker --config-file "stryker/$config.json" \
    --output "StrykerOutput/$config" $since || status=1
done
exit "$status"
