#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${MOORE_LOCAL_API_URL:-http://127.0.0.1:5222}"
ENV_FILE="$ROOT_DIR/.env.local"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

for command in curl jq; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "Required command '$command' is not installed." >&2
    exit 1
  }
done

[[ -f "$ENV_FILE" ]] || {
  echo "Missing ignored local configuration: $ENV_FILE" >&2
  exit 1
}

env_value() {
  local value
  value="$(awk -v key="$1" 'index($0, key "=") == 1 { print substr($0, length(key) + 2) }' "$ENV_FILE" | tail -n 1)"
  value="${value%\"}"
  value="${value#\"}"
  printf '%s' "$value"
}

request() {
  local name="$1"
  shift
  curl --silent --show-error --dump-header "$TMP_DIR/$name.headers" \
    --output "$TMP_DIR/$name.body" --write-out '%{http_code}' "$@"
}

assert_status() {
  if [[ "$2" != "$1" ]]; then
    echo "FAIL: $3 returned HTTP $2 (expected $1)." >&2
    exit 1
  fi
  echo "PASS: $3"
}

status="$(request live "$BASE_URL/health/live")"
assert_status 200 "$status" "liveness"
jq -e '.status == "Healthy" and .environment == "local"' "$TMP_DIR/live.body" >/dev/null
grep -Eiq '^X-Moore-API-Environment:[[:space:]]*local' "$TMP_DIR/live.headers"

status="$(request ready "$BASE_URL/api/health")"
assert_status 200 "$status" "database readiness"
jq -e '.status == "Healthy" and .database == "Connected"' "$TMP_DIR/ready.body" >/dev/null

status="$(request swagger "$BASE_URL/swagger/index.html")"
assert_status 200 "$status" "Local Swagger UI"
status="$(request spec "$BASE_URL/swagger/v1/swagger.json")"
assert_status 200 "$status" "OpenAPI document"
jq -e '(.paths | length) > 0' "$TMP_DIR/spec.body" >/dev/null

status="$(request anonymous-admin "$BASE_URL/api/analytics/overview")"
assert_status 401 "$status" "anonymous admin boundary"

status="$(request allowed-cors -H 'Origin: http://localhost:3000' "$BASE_URL/health/live")"
assert_status 200 "$status" "allowed Local dashboard origin"
grep -Eiq '^Access-Control-Allow-Origin:[[:space:]]*http://localhost:3000' "$TMP_DIR/allowed-cors.headers"

status="$(request rejected-cors -H 'Origin: https://attacker.invalid' "$BASE_URL/health/live")"
assert_status 200 "$status" "untrusted origin response"
if grep -Eiq '^Access-Control-Allow-Origin:' "$TMP_DIR/rejected-cors.headers"; then
  echo "FAIL: untrusted origin received a CORS allow header." >&2
  exit 1
fi
echo "PASS: untrusted origin is not granted browser access"

status="$(request wrong-environment -H 'X-Moore-App-Environment: production' "$BASE_URL/health/live")"
assert_status 409 "$status" "cross-environment protection"

status="$(request bad-host -H 'Host: attacker.invalid' "$BASE_URL/health/live")"
assert_status 400 "$status" "Host header protection"

admin_email="$(env_value AdminSeed__Email)"
admin_password="$(env_value AdminSeed__Password)"
if [[ -z "$admin_email" || -z "$admin_password" ]]; then
  echo "Local administrator seed values are missing." >&2
  exit 1
fi

jq -n --arg email "$admin_email" --arg password "$admin_password" \
  '{email: $email, password: $password}' > "$TMP_DIR/login.json"
status="$(request login -H 'Content-Type: application/json' --data-binary "@$TMP_DIR/login.json" "$BASE_URL/api/Auth/login")"
assert_status 200 "$status" "Local super-administrator login"
token="$(jq -er '.token' "$TMP_DIR/login.body")"

status="$(request analytics -H "Authorization: Bearer $token" "$BASE_URL/api/analytics/overview")"
assert_status 200 "$status" "authorized analytics"
status="$(request profile -H "Authorization: Bearer $token" "$BASE_URL/api/Profile/me")"
assert_status 200 "$status" "authorized profile"

echo "All Moore Hotels Local API smoke checks passed."
