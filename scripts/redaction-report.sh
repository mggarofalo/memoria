#!/bin/sh
# What gitleaks and trufflehog still find after redaction, as counts only. Use it for a migration's redaction
# report and as a release check. It reads real content, so run it locally and never in CI.
# Gitleaks's rules are also the ones the engine imports, so its 0 says mostly that the import is faithful;
# trufflehog, with its own detectors and verification off, is the independent look. Neither 0 is proof.
# Needs both on PATH, unless told --no-gitleaks or --no-trufflehog, and no network. No copy of the text is
# written to disk. Exits 0 when clean, 1 when a scanner found something or a document was refused, 2 on bad usage.
set -eu

if [ "$#" -lt 1 ]; then
  echo "usage: scripts/redaction-report.sh [--no-gitleaks] [--no-trufflehog] <directory of .md files> [more directories]" >&2
  exit 2
fi

needs() {
  for arg in "$@"; do
    [ "$arg" = "--no-$scanner" ] && return 1
  done
  return 0
}

for scanner in gitleaks trufflehog; do
  if needs "$@" && ! command -v "$scanner" >/dev/null 2>&1; then
    echo "$scanner is not on PATH; install it, or pass --no-$scanner for a weaker check." >&2
    exit 2
  fi
done

root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
exec dotnet run --project "$root/tools/RedactionCheck/RedactionCheck.csproj" -c Release --verbosity quiet -- "$@"
