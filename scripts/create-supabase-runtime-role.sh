#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_directory/database-connection.sh"

if [[ -z "${MIGRATION_CONNECTION_STRING:-}" ||
      -z "${DATABASE_RUNTIME_ROLE:-}" ||
      -z "${DATABASE_RUNTIME_PASSWORD:-}" ]]; then
  echo "MIGRATION_CONNECTION_STRING, DATABASE_RUNTIME_ROLE, and DATABASE_RUNTIME_PASSWORD are required." >&2
  exit 2
fi
if [[ ! "$DATABASE_RUNTIME_ROLE" =~ ^[a-z][a-z0-9_]{2,30}$ ]]; then
  echo "DATABASE_RUNTIME_ROLE must be a lowercase PostgreSQL identifier (3-31 characters)." >&2
  exit 2
fi

configure_psql_connection "$MIGRATION_CONNECTION_STRING" "MIGRATION_CONNECTION_STRING"
runtime_password_base64="$(printf '%s' "$DATABASE_RUNTIME_PASSWORD" | base64 | tr -d '\n')"
export PGOPTIONS="${PGOPTIONS:+$PGOPTIONS }-c moore.runtime_password_base64=$runtime_password_base64"
run_psql --set "runtime_role=$DATABASE_RUNTIME_ROLE" <<'SQL'
SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'runtime_role') AS runtime_role_exists
\gset
\if :runtime_role_exists
  SELECT format(
      'ALTER ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS',
      :'runtime_role',
      convert_from(decode(current_setting('moore.runtime_password_base64'), 'base64'), 'UTF8'))
  \gexec
  SELECT 'Rotated the dedicated Supabase runtime role password.' AS result;
\else
  SELECT format(
      'CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS',
      :'runtime_role',
      convert_from(decode(current_setting('moore.runtime_password_base64'), 'base64'), 'UTF8'))
  \gexec
  SELECT 'Created the dedicated Supabase runtime role.' AS result;
\endif
SQL
unset runtime_password_base64 PGOPTIONS
