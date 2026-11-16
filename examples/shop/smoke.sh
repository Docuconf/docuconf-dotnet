#!/usr/bin/env bash
# Smoke test: runs the published shop app as each role with valid env and
# checks the line it prints, then with LOG_FORMAT=json and no LOG_SCHEMA_URL
# and checks it refuses to start without printing the database password.
# These are cases 1, 2 and 6 of the matrix in README.md. Needs dotnet.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
dotnet publish "$here" -c Release -o "$tmp/app" >/dev/null
app="$tmp/app/Shop.dll"

secret='s3cr3t-pw'
mkdir "$tmp/root" # a fresh, empty file root: no webhook signing key
export DATABASE_URL="postgres://shop:$secret@db:5432/shop" DOCUCONF_FILE_ROOT="$tmp/root"

# run NAME WANT_CODE WANT_STDOUT [VAR=value ...]: runs the app with the extra env and checks its exit status and stdout.
run() {
  local name="$1" want_code="$2" want_out="$3" code
  shift 3
  set +e
  env "$@" dotnet "$app" >"$tmp/out.txt" 2>"$tmp/err.txt"
  code=$?
  set -e
  if [ "$code" != "$want_code" ] || [ "$(cat "$tmp/out.txt")" != "$want_out" ]; then
    echo "$name: want exit $want_code and stdout '$want_out', got exit $code:" >&2
    cat "$tmp/out.txt" "$tmp/err.txt" >&2
    exit 1
  fi
  if grep -q "$secret" "$tmp/out.txt" "$tmp/err.txt"; then
    echo "$name: the database password leaked" >&2; exit 1
  fi
}

# 1. web: needs ALLOWED_ORIGINS; PORT has a default.
run "1 web" 0 'shop web: port 8080, origins https://shop.example' DOCUCONF_ROLE=web ALLOWED_ORIGINS=https://shop.example
echo "1 web: $(cat "$tmp/out.txt")"

# 2. worker: needs QUEUES, and not ALLOWED_ORIGINS.
run "2 worker" 0 'shop worker: queues default,mail, webhooks off' DOCUCONF_ROLE=worker QUEUES=default,mail
echo "2 worker: $(cat "$tmp/out.txt")"

# 6. LOG_FORMAT=json requires LOG_SCHEMA_URL: exit 1, one problem, nothing on stdout.
: >"$tmp/termination-log" # docuconf appends to it only when it exists, as /dev/termination-log does
run "6 json logs" 1 '' DOCUCONF_ROLE=worker QUEUES=default LOG_FORMAT=json DOCUCONF_TERMINATION_LOG="$tmp/termination-log"
expected='docuconf: 1 configuration problem:
  [missing_required] LOG_SCHEMA_URL: is required when LOG_FORMAT is "json" (Shop:LogSchemaUrl)'
if [ "$(cat "$tmp/err.txt")" != "$expected" ]; then
  echo "6 json logs: unexpected startup output:" >&2; diff <(echo "$expected") "$tmp/err.txt" >&2; exit 1
fi
grep -q 'missing_required' "$tmp/termination-log" || { echo "termination log not written" >&2; exit 1; }
echo "6 json logs: exited 1 with:"
sed 's/^/  /' "$tmp/err.txt"
echo "smoke: ok"
