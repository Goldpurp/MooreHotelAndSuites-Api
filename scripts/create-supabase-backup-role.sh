#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_directory/database-connection.sh"

if [[ -z "${MIGRATION_CONNECTION_STRING:-}" ||
      -z "${DATABASE_BACKUP_ROLE:-}" ||
      -z "${DATABASE_BACKUP_PASSWORD:-}" ]]; then
  echo "MIGRATION_CONNECTION_STRING, DATABASE_BACKUP_ROLE, and DATABASE_BACKUP_PASSWORD are required." >&2
  exit 2
fi
if [[ ! "$DATABASE_BACKUP_ROLE" =~ ^[a-z][a-z0-9_]{2,30}$ ]]; then
  echo "DATABASE_BACKUP_ROLE must be a lowercase PostgreSQL identifier (3-31 characters)." >&2
  exit 2
fi

configure_psql_connection "$MIGRATION_CONNECTION_STRING" "MIGRATION_CONNECTION_STRING"
backup_password_base64="$(printf '%s' "$DATABASE_BACKUP_PASSWORD" | base64 | tr -d '\n')"
{
  printf '\\set backup_password_base64 %s\n' "$backup_password_base64"
  cat <<'SQL'
SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'backup_role') AS backup_role_exists
\gset
\if :backup_role_exists
  SELECT format(
      'ALTER ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION BYPASSRLS',
      :'backup_role',
      convert_from(decode(:'backup_password_base64', 'base64'), 'UTF8'))
  \gexec
\else
  SELECT format(
      'CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION BYPASSRLS',
      :'backup_role',
      convert_from(decode(:'backup_password_base64', 'base64'), 'UTF8'))
  \gexec
\endif

SELECT format('REVOKE ALL ON DATABASE %I FROM %I', current_database(), :'backup_role') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), :'backup_role') \gexec
SELECT format('REVOKE ALL ON SCHEMA public FROM %I', :'backup_role') \gexec
SELECT format('GRANT USAGE ON SCHEMA public TO %I', :'backup_role') \gexec
SELECT format('REVOKE ALL ON ALL TABLES IN SCHEMA public FROM %I', :'backup_role') \gexec
SELECT format('GRANT SELECT ON ALL TABLES IN SCHEMA public TO %I', :'backup_role') \gexec
SELECT format('REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM %I', :'backup_role') \gexec
SELECT format('GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO %I', :'backup_role') \gexec
SELECT format('REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM %I', :'backup_role') \gexec

-- The migration owner runs this after every migration, so newly created objects
-- receive no backup access until this explicit provisioning pass succeeds.
SELECT format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM %I', :'backup_role') \gexec
SELECT format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON SEQUENCES FROM %I', :'backup_role') \gexec
SELECT 'Dedicated read-only backup role provisioned.' AS result;
SQL
} | run_psql --set "backup_role=$DATABASE_BACKUP_ROLE"
unset backup_password_base64
