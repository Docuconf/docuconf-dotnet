#!/usr/bin/env bash
# Smoke test: starts the orders service with valid env and checks its
# endpoints, then starts it with bad env and checks it refuses to start, and
# posts webhooks signed with each key of a key set that is mid-rotation.
# Needs dotnet, curl and python3.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
pid=""
trap '[ -n "$pid" ] && kill "$pid" 2>/dev/null; rm -rf "$tmp"' EXIT
dotnet publish "$here" -c Release -o "$tmp/app" >/dev/null
app="$tmp/app/Orders.Api.dll"

secret='postgres://orders:s3cr3t-pw@localhost:5432/orders'
# Two webhook keys: the old one and, mid-rotation, the new one.
old_key='old-webhook-key-0123456789abcdef0123'
new_key='new-webhook-key-0123456789abcdef0123'
# A free port: other services may be listening on the usual ones.
port="${SMOKE_PORT:-$(python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])')}"

wait_for_healthz() {
  for _ in $(seq 100); do
    kill -0 "$pid" 2>/dev/null || { echo "the service exited:" >&2; cat "$tmp/out.txt" >&2; exit 1; }
    curl -fsS "http://127.0.0.1:$port/healthz" >"$tmp/healthz" 2>/dev/null && break
    sleep 0.2
  done
  [ "$(cat "$tmp/healthz" 2>/dev/null)" = ok ] || { echo "GET /healthz did not return ok" >&2; cat "$tmp/out.txt" >&2; exit 1; }
}

# 1. Valid env: the service serves /healthz and /config, without the secrets,
# and turns away an unsigned webhook.
ORDERS__PORT=$port ORDERS__DATABASEURL="$secret" ORDERS__ALLOWEDORIGINS__0=https://shop.example.com \
  WEBHOOK_KEYS="$old_key,$new_key" dotnet "$app" >"$tmp/out.txt" 2>&1 &
pid=$!
wait_for_healthz
curl -fsS "http://127.0.0.1:$port/config" >"$tmp/config.json"
if grep -q -e 's3cr3t-pw' -e 'webhook-key' "$tmp/config.json" "$tmp/out.txt"; then
  echo "a secret leaked into /config or the log" >&2; exit 1
fi
grep -q '"databaseUrl":"\*\*\*"' "$tmp/config.json" || { echo "GET /config did not redact databaseUrl" >&2; exit 1; }
grep -q '"webhookKeys":"\*\*\*"' "$tmp/config.json" || { echo "GET /config did not redact webhookKeys" >&2; exit 1; }
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'X-Signature: 00' -d '{}' "http://127.0.0.1:$port/webhooks/payments")
[ "$code" = 401 ] || { echo "an unsigned webhook got $code, want 401" >&2; exit 1; }
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

# 3. A key set with an empty second key (a trailing comma): the item length
# constraint fails it at startup, without printing the key.
set +e
ORDERS__PORT=$port ORDERS__DATABASEURL="$secret" WEBHOOK_KEYS="$old_key," dotnet "$app" >"$tmp/bad.txt" 2>&1
code=$?
set -e
expected='docuconf: 1 configuration problem:
  [out_of_range] WEBHOOK_KEYS: item 1 is 0 characters, below itemMinLength 32 (value redacted)'
if [ "$code" != 1 ] || [ "$(cat "$tmp/bad.txt")" != "$expected" ] || grep -q webhook-key "$tmp/bad.txt"; then
  echo "want exit 1 for an empty webhook key, got $code:" >&2; diff <(echo "$expected") "$tmp/bad.txt" >&2; exit 1
fi
echo "empty webhook key: exited 1"

# 4. Mid-rotation, a webhook signed with either key is accepted, and one
# signed with any other key is not.
ORDERS__PORT=$port ORDERS__DATABASEURL="$secret" WEBHOOK_KEYS="$old_key,$new_key" dotnet "$app" >"$tmp/out.txt" 2>&1 &
pid=$!
wait_for_healthz
body='{"order":"42","status":"paid"}'
for key in "$old_key" "$new_key" "other-webhook-key-0123456789abcdef"; do
  sig=$(python3 -c 'import hashlib, hmac, sys; print(hmac.new(sys.argv[1].encode(), sys.argv[2].encode(), hashlib.sha256).hexdigest())' "$key" "$body")
  code=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "X-Signature: $sig" -d "$body" "http://127.0.0.1:$port/webhooks/payments")
  want=204; [ "${key#other}" != "$key" ] && want=401
  [ "$code" = "$want" ] || { echo "webhook signed with ${key%%-*} key: got $code, want $want" >&2; exit 1; }
done
echo "webhooks: old and new key accepted, any other rejected"
kill "$pid"; wait "$pid" 2>/dev/null || true; pid=""
echo "smoke: ok"
