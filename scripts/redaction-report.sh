#!/bin/sh
# What gitleaks still finds after redaction, as counts only. Use it for a migration's redaction report and as a
# release check. It reads real content, so run it locally and never in CI.
# Gitleaks's rules are also the ones the engine imports, so its 0 says mostly that the import is faithful, and is not
# proof. It is a sanity check: glance at the counts.
# Needs gitleaks on PATH, unless told --no-gitleaks, and no network. No copy of the text is written to disk.
# Exits 0 when clean, 1 when gitleaks found something or a document was refused, 2 on bad usage.
set -eu

if [ "$#" -lt 1 ]; then
  echo "usage: scripts/redaction-report.sh [--no-gitleaks] <directory of .md files> [more directories]" >&2
  exit 2
fi

needs_gitleaks() {
  for arg in "$@"; do
    [ "$arg" = "--no-gitleaks" ] && return 1
  done
  return 0
}

if needs_gitleaks "$@" && ! command -v gitleaks >/dev/null 2>&1; then
  echo "gitleaks is not on PATH; install it, or pass --no-gitleaks for a weaker check." >&2
  exit 2
fi

root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
exec dotnet run --project "$root/tools/RedactionCheck/RedactionCheck.csproj" -c Release --verbosity quiet -- "$@"
