#!/usr/bin/env bash
# Install the service packages the way a host would, outside any checkout, and
# check what they report. usage: verify-services.sh <asset-dir> <version> <empty-work-dir>
#
# The sidecar runs for real, with its native dependencies installed from the
# lockfile. The worker's server module is imported instead, since running it
# needs llama-server and models.
set -euo pipefail

assets="$(cd "$1" && pwd)"
version="$2"
work="$(cd "$3" && pwd)"
port=18282

mkdir "$work/sidecar" "$work/worker"
tar -xzf "$assets/memoria-sidecar_${version}.tar.gz" -C "$work/sidecar"
tar -xzf "$assets/memoria-worker_${version}.tar.gz" -C "$work/worker"

test "$(cat "$work/sidecar/dist/VERSION")" = "$version"
test "$(cat "$work/worker/src/VERSION")" = "$version"
for package in sidecar worker; do
  if git -C "$work/$package" rev-parse --git-dir >/dev/null 2>&1; then
    echo "$work/$package is inside a git checkout, which would not prove anything" >&2
    exit 1
  fi
done

# Each packaged installer must find its module and build a complete plan. -WhatIf touches nothing.
pwsh -NoProfile -NonInteractive -File "$work/worker/scripts/install-service.ps1" -WhatIf \
  -InstallDir /pf/svc -StateDir /pd/svc -LlamaServer /pf/llama/llama-server -ModelDir /models >/dev/null
pwsh -NoProfile -NonInteractive -File "$work/sidecar/scripts/install-service.ps1" -WhatIf -NoBuild \
  -InstallDir /pf/svc -StateDir /pd/svc -CacheHome /home/u/.cache -ConfigHome /home/u/.config -CollectionRoot /corpus >/dev/null
echo "packaged installers planned"

worker_url="$(node -p "require('node:url').pathToFileURL(process.argv[1]).href" "$work/worker/src/server.ts")"
reported="$(cd "$work" && node --input-type=module -e "console.log((await import('$worker_url')).WORKER_VERSION)")"
echo "worker reports: $reported"
test "$reported" = "$version"

(cd "$work/sidecar" && npm ci --omit=dev --no-audit --no-fund)
cd "$work/sidecar"
# The sidecar opens an existing index and never creates one. This also loads the
# native dependencies that were just installed, and it refuses to start if installing
# did not apply the SDK patch.
db="$work/verify.sqlite"
node --input-type=module -e "import { createStore } from '@tobilu/qmd'; const store = await createStore({ dbPath: process.argv[1], config: { collections: {} } }); await store.close();" "$db"
MEMORIA_QMD_DB_PATH="$db" MEMORIA_QMD_WARMUP=0 MEMORIA_QMD_PORT="$port" node dist/main.js &
sidecar=$!
trap 'kill "$sidecar" 2>/dev/null || true' EXIT

health=""
for _ in $(seq 1 60); do
  health="$(curl -fsS "http://127.0.0.1:$port/health" 2>/dev/null)" && break
  kill -0 "$sidecar" 2>/dev/null || { echo "the sidecar exited before answering" >&2; exit 1; }
  sleep 1
done
reported="$(node -p "JSON.parse(process.argv[1]).version" "$health")"
echo "sidecar reports: $reported"
test "$reported" = "$version"
