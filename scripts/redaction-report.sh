#!/bin/sh
# What gitleaks still finds after redaction, as counts only. Use it for a migration's redaction
# report and as a release check. It reads real content, so run it locally and never in CI.
# Needs gitleaks on PATH and no network. The temporary files are deleted on every exit path.
# Exits 0 when clean, 1 when gitleaks found something or a document was refused, 2 on bad usage.
set -eu

if [ "$#" -lt 1 ]; then
  echo "usage: scripts/redaction-report.sh <directory of .md files> [more directories]" >&2
  exit 2
fi

if ! command -v gitleaks >/dev/null 2>&1; then
  echo "gitleaks is not on PATH." >&2
  exit 2
fi

root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
exec dotnet run --project "$root/tools/RedactionCheck/RedactionCheck.csproj" -c Release --verbosity quiet -- "$@"
