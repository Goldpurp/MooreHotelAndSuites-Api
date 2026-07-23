#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

report="$(
  dotnet list "$ROOT_DIR/MooreHotels.sln" package \
    --vulnerable \
    --include-transitive \
    --format json
)"

printf '%s\n' "$report"

if grep -q '"severity"' <<<"$report"; then
  echo "NuGet vulnerability gate failed." >&2
  exit 1
fi

echo "NuGet vulnerability gate passed."
