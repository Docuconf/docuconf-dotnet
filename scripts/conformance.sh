#!/usr/bin/env bash
# Runs only the docuconf-go-facing tests against a given docuconf-go checkout:
# the shared conformance suite (ConformanceTests), the shared export check
# (ConformanceExportTests, which runs `docuconf conformance export`), and the
# tests that `cue vet` exported contracts against its meta-schema
# (ExportTests, JsonVarTests, ItemBoundsTests). Not the full suite.
#
#   DOCUCONF_GO_DIR=/path/to/docuconf-go scripts/conformance.sh
#
# Needs the .NET 10 SDK and cue on PATH, and Go to build the docuconf CLI from
# the checkout (or DOCUCONF_CLI pointing at one). docuconf-go's downstream
# workflow and this repository's CI both call it. It fails if any case is
# skipped.
set -euo pipefail

: "${DOCUCONF_GO_DIR:?set DOCUCONF_GO_DIR to a docuconf-go checkout}"
DOCUCONF_GO_DIR="$(cd "$DOCUCONF_GO_DIR" && pwd)"
export DOCUCONF_GO_DIR
export DOCUCONF_CONFORMANCE="${DOCUCONF_CONFORMANCE:-$DOCUCONF_GO_DIR/conformance/cases.json}"
export DOCUCONF_SPEC_CUE="${DOCUCONF_SPEC_CUE:-$DOCUCONF_GO_DIR/spec/cue}"
export DOCUCONF_REQUIRE_CONFORMANCE=1
export DOCUCONF_REQUIRE_VET=1

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# The export check compares with the CLI of the same checkout as the golden contract.
if [ -z "${DOCUCONF_CLI:-}" ]; then
  (cd "$DOCUCONF_GO_DIR/cmd/docuconf" && go build -o "$tmp/docuconf" .)
  export DOCUCONF_CLI="$tmp/docuconf"
fi

export DOCUCONF_CONFORMANCE_SUMMARY="$tmp/summary"
cd "$(dirname "$0")/.."
# dotnet test restores and builds the test project and the SDK it references.
dotnet test --project tests/Docuconf.Tests -c Release \
  --filter-class Docuconf.Tests.ConformanceTests \
  --filter-class Docuconf.Tests.ConformanceExportTests \
  --filter-class Docuconf.Tests.ExportTests \
  --filter-class Docuconf.Tests.JsonVarTests \
  --filter-class Docuconf.Tests.ItemBoundsTests
# One line per target framework: "conformance: N passed, 0 skipped, 0 failed".
cat "$DOCUCONF_CONFORMANCE_SUMMARY"
