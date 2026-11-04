#!/bin/sh
# Branch coverage gate for C#. Runs both test projects, merges their reports
# (Memoria.Api.Tests also exercises Memoria.Core), and fails below the threshold.
# Reads the existing build, so run `dotnet build` first.
set -eu

# Phase 1 ratchet: the value measured on main. The target is 90.
MIN_BRANCH=89

out=TestResults/coverage
rm -rf "$out"
dotnet test Memoria.slnx --no-build --nologo --verbosity minimal \
  --settings coverage.runsettings \
  --collect:"XPlat Code Coverage" \
  --results-directory "$out"

dotnet tool run reportgenerator \
  "-reports:$out/*/coverage.cobertura.xml" \
  "-targetdir:$out" \
  "-reporttypes:TextSummary" \
  "-verbosity:Warning"

awk -v min="$MIN_BRANCH" '
  /^ *Branch coverage:/ {
    found = 1
    pct = $3; sub(/%/, "", pct)
    printf "C# branch coverage: %s%% (minimum %s%%)\n", pct, min
    if (pct + 0 < min + 0) { print "Below the minimum." > "/dev/stderr"; exit 1 }
    exit 0
  }
  END { if (!found) { print "No branch coverage figure in the report." > "/dev/stderr"; exit 1 } }
' "$out/Summary.txt"
