#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_directory/database-connection.sh"

if [[ -z "${MIGRATION_CONNECTION_STRING:-}" ]]; then
  echo "MIGRATION_CONNECTION_STRING is required." >&2
  exit 2
fi
if [[ -z "${DATABASE_RUNTIME_ROLE:-}" ]]; then
  echo "DATABASE_RUNTIME_ROLE is required and must name an existing NOLOGIN/LOGIN runtime role." >&2
  exit 2
fi
if ! command -v psql >/dev/null 2>&1; then
  echo "psql is required to provision runtime database privileges." >&2
  exit 2
fi

configure_psql_connection "$MIGRATION_CONNECTION_STRING" "MIGRATION_CONNECTION_STRING"
run_psql --set "runtime_role=$DATABASE_RUNTIME_ROLE" <<'SQL'
SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'runtime_role') AS runtime_role_exists
\gset
\if :runtime_role_exists
\else
  DO $$
  BEGIN
    RAISE EXCEPTION 'DATABASE_RUNTIME_ROLE does not name an existing PostgreSQL role.';
  END
  $$;
\endif

SELECT format('REVOKE CREATE ON DATABASE %I FROM %I', current_database(), :'runtime_role') \gexec
SELECT format('REVOKE CREATE ON DATABASE %I FROM PUBLIC', current_database()) \gexec
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SELECT format('REVOKE CREATE ON SCHEMA public FROM %I', :'runtime_role') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), :'runtime_role') \gexec
SELECT format('GRANT USAGE ON SCHEMA public TO %I', :'runtime_role') \gexec
SELECT format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO %I', :'runtime_role') \gexec
SELECT format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO %I', :'runtime_role') \gexec
SELECT format('ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', :'runtime_role') \gexec
SELECT format('ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO %I', :'runtime_role') \gexec

SELECT format('REVOKE ALL ON TABLE public.%I FROM %I', '__EFMigrationsHistory', :'runtime_role')
WHERE to_regclass('public."__EFMigrationsHistory"') IS NOT NULL
\gexec
SELECT format('REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON TABLE public.audit_logs FROM %I', :'runtime_role')
WHERE to_regclass('public.audit_logs') IS NOT NULL
\gexec
SELECT format('GRANT SELECT, INSERT ON TABLE public.audit_logs TO %I', :'runtime_role')
WHERE to_regclass('public.audit_logs') IS NOT NULL
\gexec
SELECT format('REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON TABLE public.%I FROM %I', table_name, :'runtime_role')
FROM (VALUES
    ('booking_code_allocations'),
    ('visit_records'),
    ('notifications')
) AS append_only(table_name)
WHERE to_regclass(format('public.%I', table_name)) IS NOT NULL
\gexec
SELECT format('GRANT SELECT, INSERT ON TABLE public.%I TO %I', table_name, :'runtime_role')
FROM (VALUES
    ('booking_code_allocations'),
    ('visit_records'),
    ('notifications')
) AS append_only(table_name)
WHERE to_regclass(format('public.%I', table_name)) IS NOT NULL
\gexec
SELECT format('REVOKE DELETE, TRUNCATE, REFERENCES, TRIGGER ON TABLE public.%I FROM %I', table_name, :'runtime_role')
FROM (VALUES
    ('bookings'),
    ('guests'),
    ('monnify_transactions')
) AS retained_records(table_name)
WHERE to_regclass(format('public.%I', table_name)) IS NOT NULL
\gexec

SELECT has_schema_privilege(:'runtime_role', 'public', 'CREATE') AS runtime_has_schema_create
\gset
\if :runtime_has_schema_create
  DO $$
  BEGIN
    RAISE EXCEPTION 'The migration credential cannot remove public-schema CREATE from the runtime role. Make the migration role the public schema owner, then rerun.';
  END
  $$;
\endif

SELECT 'Runtime database privileges provisioned.' AS result;
SQL
