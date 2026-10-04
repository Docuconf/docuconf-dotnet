#!/usr/bin/env bash
# Copies the CUE meta-schema from a checkout of the docuconf spec into the test project.
set -euo pipefail
src="${1:?usage: sync-spec.sh <path-to-spec/cue>}"
dst="$(dirname "$0")/../tests/Docuconf.Tests/spec"
rm -rf "$dst/cue.mod" "$dst/contract"
cp -r "$src/cue.mod" "$src/contract" "$dst/"
echo "Synced meta-schema from $src"
