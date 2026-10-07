#!/usr/bin/env bash
# Smoke test: starts the orders service with valid env and checks its
# endpoints, then starts it with bad env and checks it refuses to start.
# Needs dotnet, curl and python3.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
pid=""
trap '[ -n "$pid" ] && kill "$pid" 2>/dev/null; rm -rf "$tmp"' EXIT
dotnet publish "$here" -c Release -o "$tmp/app" >/dev/null
app="$tmp/app/Orders.Api.dll"

secret='postgres://orders:s3cr3t-pw@localhost:5432/orders'
# A free port: other services may be listening on the usual ones.
port="${SMOKE_PORT:-$(python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])')}"

# 1. Valid env: the service serves /healthz and /config, without the secret.
ORDERS__PORT=$port ORDERS__DATABASEURL="$secret" ORDERS__ALLOWEDORIGINS__0=https://shop.example.com \
  dotnet "$app" >"$tmp/out.txt" 2>&1 &
pid=$!
for _ in $(seq 100); do
  kill -0 "$pid" 2>/dev/null || { echo "the service exited:" >&2; cat "$tmp/out.txt" >&2; exit 1; }
  curl -fsS "http://127.0.0.1:$port/healthz" >"$tmp/healthz" 2>/dev/null && break
  sleep 0.2
done
[ "$(cat "$tmp/healthz" 2>/dev/null)" = ok ] || { echo "GET /healthz did not return ok" >&2; cat "$tmp/out.txt" >&2; exit 1; }
curl -fsS "http://127.0.0.1:$port/config" >"$tmp/config.json"
if grep -q 's3cr3t-pw' "$tmp/config.json"; then
  echo "GET /config leaked the secret" >&2; exit 1
fi
grep -q '"databaseUrl":"\*\*\*"' "$tmp/config.json" || { echo "GET /config did not redact databaseUrl" >&2; exit 1; }
echo "valid env: /healthz ok, /config $(cat "$tmp/config.json")"
kill "$pid"; wait "$pid" 2>/dev/null || true; pid=""

# 2. ORDERS__PORT=0 and no ORDERS__DATABASEURL: the service exits 1 with one line per problem, no stack trace.
: >"$tmp/termination-log" # docuconf appends to it only when it exists, as /dev/termination-log does
set +e
env -u ORDERS__DATABASEURL ORDERS__PORT=0 ORDERS__WORKERCUONT=2 DOCUCONF_TERMINATION_LOG="$tmp/termination-log" \
  dotnet "$app" >"$tmp/bad.txt" 2>&1
code=$?
set -e
[ "$code" = 1 ] || { echo "expected exit status 1, got $code:" >&2; cat "$tmp/bad.txt" >&2; exit 1; }
expected='docuconf: ORDERS__WORKERCUONT is set but not declared; did you mean ORDERS__WORKERCOUNT?
docuconf: 2 configuration problems:
  [missing_required] ORDERS__DATABASEURL: is required (Orders:DatabaseUrl)
  [out_of_range] ORDERS__PORT: '"'"'0'"'"' is below the minimum 1'
if [ "$(cat "$tmp/bad.txt")" != "$expected" ]; then
  echo "unexpected startup output:" >&2; diff <(echo "$expected") "$tmp/bad.txt" >&2; exit 1
fi
grep -q 'out_of_range' "$tmp/termination-log" || { echo "termination log not written" >&2; exit 1; }
echo "bad env: exited 1 with:"
sed 's/^/  /' "$tmp/bad.txt"
echo "smoke: ok"
