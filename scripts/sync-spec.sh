#!/usr/bin/env bash
# Copies the CUE meta-schema from a checkout of the docuconf spec into the test project, and the well-known fragments
# (spec/fragments.json, next to spec/cue) into the SDK, which embeds them.
set -euo pipefail
src="${1:?usage: sync-spec.sh <path-to-spec/cue>}"
dst="$(dirname "$0")/../tests/Docuconf.Tests/spec"
rm -rf "$dst/cue.mod" "$dst/contract"
cp -r "$src/cue.mod" "$src/contract" "$dst/"
cp "$src/../fragments.json" "$(dirname "$0")/../src/Docuconf/Contract/fragments.json"
echo "Synced meta-schema from $src"
