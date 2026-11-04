#!/bin/sh
# Branch coverage gate for C#. Runs both test projects, merges their reports
# (Memoria.Api.Tests also exercises Memoria.Core), and fails below the thresholds.
# Reads the existing build, so run `dotnet build` first.
set -eu

MIN_BRANCH=90
# Memoria.Core on its own, so Memoria.Api's gaps cannot hide a drop here.
MIN_BRANCH_CORE=95

out=TestResults/coverage
rm -rf "$out"
dotnet test Memoria.slnx --no-build --nologo --verbosity minimal \
  --settings coverage.runsettings \
  --collect:"XPlat Code Coverage" \
  --results-directory "$out"

# Usage: check <label> <minimum> <summary directory> [reportgenerator filter]
check() {
  label=$1
  min=$2
  dir=$3
  shift 3
  dotnet tool run reportgenerator \
    "-reports:$out/*/coverage.cobertura.xml" \
    "-targetdir:$dir" \
    "-reporttypes:TextSummary" \
    "-verbosity:Warning" "$@"
  awk -v min="$min" -v label="$label" '
    /^ *Branch coverage:/ {
      found = 1
      pct = $3; sub(/%/, "", pct)
      printf "%s branch coverage: %s%% (minimum %s%%)\n", label, pct, min
      if (pct + 0 < min + 0) { print "Below the minimum." > "/dev/stderr"; exit 1 }
      exit 0
    }
    END { if (!found) { print "No branch coverage figure in the report." > "/dev/stderr"; exit 1 } }
  ' "$dir/Summary.txt"
}

check "C#" "$MIN_BRANCH" "$out"
check "Memoria.Core" "$MIN_BRANCH_CORE" "$out/core" "-assemblyfilters:+Memoria.Core"
