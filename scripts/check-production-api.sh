#!/usr/bin/env bash
set -euo pipefail

endpoint="${1:-https://api.moorehotelandsuites.com/health/operations}"
latency_limit_seconds="${2:-2.0}"
sample_count="${3:-5}"

if ! [[ "$latency_limit_seconds" =~ ^[0-9]+([.][0-9]+)?$ ]]; then
  echo "Latency limit must be a positive number." >&2
  exit 2
fi

if ! [[ "$sample_count" =~ ^[1-9][0-9]*$ ]]; then
  echo "Sample count must be a positive integer." >&2
  exit 2
fi

work_directory="$(mktemp -d)"
trap 'rm -rf "$work_directory"' EXIT

# The warm-up request is intentionally excluded. Render Free can cold-start after
# inactivity, while UptimeRobot's five-minute availability probe normally keeps
# the service warm for the latency samples that follow.
curl --fail --silent --show-error --location \
  --connect-timeout 10 --max-time 60 \
  --output /dev/null "$endpoint"

for sample in $(seq 1 "$sample_count"); do
  body_file="$work_directory/body-$sample.json"
  measurement="$(curl --silent --show-error --location \
    --connect-timeout 10 --max-time 15 \
    --output "$body_file" \
    --write-out '%{http_code} %{time_total}' \
    "$endpoint")"
  read -r status_code duration_seconds <<< "$measurement"

  if [[ "$status_code" != "200" ]]; then
    echo "Operations probe returned HTTP $status_code on sample $sample." >&2
    cat "$body_file" >&2
    exit 1
  fi

  python3 - "$body_file" <<'PY'
import json
import pathlib
import sys

payload = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
if payload.get("status") != "Operational":
    raise SystemExit(f"Operations probe reported {payload.get('status')!r}.")
checks = payload.get("checks") or {}
unhealthy = {key: value for key, value in checks.items() if value not in {"Operational", "Connected"}}
if unhealthy:
    raise SystemExit(f"Operations checks require attention: {unhealthy}")
PY

  printf '%s\n' "$duration_seconds" >> "$work_directory/durations.txt"
  printf 'sample=%s status=%s duration_seconds=%s\n' \
    "$sample" "$status_code" "$duration_seconds"
  sleep 1
done

p95_seconds="$(sort -n "$work_directory/durations.txt" | awk \
  -v count="$sample_count" 'NR == int((95 * count + 99) / 100) { print; exit }')"

printf 'p95_seconds=%s limit_seconds=%s\n' "$p95_seconds" "$latency_limit_seconds"

if ! awk -v observed="$p95_seconds" -v limit="$latency_limit_seconds" \
  'BEGIN { exit !(observed <= limit) }'; then
  echo "Production API p95 latency exceeded the ${latency_limit_seconds}s limit." >&2
  exit 1
fi
