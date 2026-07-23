#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

dotnet tool restore
ASPNETCORE_ENVIRONMENT=Local dotnet tool run dotnet-ef database update \
  --project "$ROOT_DIR/MooreHotels.Infrastructure/MooreHotels.Infrastructure.csproj" \
  --startup-project "$ROOT_DIR/MooreHotels.WebAPI/MooreHotels.WebAPI.csproj"
