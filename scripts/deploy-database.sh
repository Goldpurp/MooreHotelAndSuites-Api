#!/usr/bin/env bash
set -euo pipefail

# Run on the operator's machine, never as the Render web process.
# Build and test this exact image first; deploy its commit only after success.
image="${1:?Usage: scripts/deploy-database.sh TESTED_IMAGE}"
: "${MIGRATION_CONNECTION_STRING:?Owner session-pooler connection is required}"
: "${DATABASE_RUNTIME_ROLE:?Runtime role is required}"
: "${RUNTIME_CONNECTION_STRING:?Runtime connection is required for verification}"
export Database__Provider=Supabase

docker run --rm \
  --env MIGRATION_CONNECTION_STRING \
  --env DATABASE_RUNTIME_ROLE \
  --env DATABASE_RUNTIME_PASSWORD \
  --env RUNTIME_CONNECTION_STRING \
  --env Database__Provider \
  --entrypoint /bin/bash "$image" -c '
    set -euo pipefail
    if [[ -n "${DATABASE_RUNTIME_PASSWORD:-}" ]]; then
      ./scripts/create-supabase-runtime-role.sh
    fi
    ./scripts/predeploy-production.sh
    ./scripts/validate-runtime-database-role.sh
  '
