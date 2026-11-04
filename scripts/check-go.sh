#!/bin/sh
# The Go pre-commit check: format, vet, complexity, then tests with coverage.
set -eu

cd "$(dirname "$0")/../cli"

unformatted=$(gofmt -l .)
if [ -n "$unformatted" ]; then
  echo "These files are not gofmt'd:"
  echo "$unformatted"
  exit 1
fi

go vet ./...
# Same version as the golangci-lint step in .github/workflows/ci.yml.
go run github.com/golangci/golangci-lint/v2/cmd/golangci-lint@v2.15.0 run ./...
sh ../scripts/coverage-go.sh
