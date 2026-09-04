#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/MooreHotels.WebAPI/MooreHotels.WebAPI.csproj"

"$ROOT_DIR/scripts/local-db.sh" start

exec dotnet watch \
  --project "$PROJECT" \
  run \
  --launch-profile MooreHotels.Local
