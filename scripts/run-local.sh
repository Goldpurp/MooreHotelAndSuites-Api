#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/MooreHotels.WebAPI/MooreHotels.WebAPI.csproj"

# Single-node, isolated compilation avoids stale compiler-server state and
# makes repeated Local starts deterministic across IDE and terminal sessions.
dotnet build "$PROJECT" \
  --configuration Release \
  --disable-build-servers \
  -m:1 \
  -p:UseSharedCompilation=false

exec dotnet run \
  --project "$PROJECT" \
  --configuration Release \
  --no-build \
  --launch-profile MooreHotels.Local
