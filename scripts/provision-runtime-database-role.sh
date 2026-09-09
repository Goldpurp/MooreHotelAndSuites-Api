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
-- Fail closed for objects created by future migrations. The post-migration
-- provisioning pass below grants access only after the complete schema exists.
SELECT format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM %I', :'runtime_role') \gexec
SELECT format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON SEQUENCES FROM %I', :'runtime_role') \gexec

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
    ('data_protection_keys'),
    ('booking_code_allocations'),
    ('booking_amendments'),
    ('folio_entries'),
    ('guest_merges'),
    ('night_audits')
) AS append_only(table_name)
WHERE to_regclass(format('public.%I', table_name)) IS NOT NULL
\gexec
SELECT format('GRANT SELECT, INSERT ON TABLE public.%I TO %I', table_name, :'runtime_role')
FROM (VALUES
    ('data_protection_keys'),
    ('booking_code_allocations'),
    ('booking_amendments'),
    ('folio_entries'),
    ('guest_merges'),
    ('night_audits')
) AS append_only(table_name)
WHERE to_regclass(format('public.%I', table_name)) IS NOT NULL
\gexec

-- Key XML is certificate-encrypted, append-only, and inaccessible to Data API roles.
DROP POLICY IF EXISTS runtime_key_access ON public.data_protection_keys;
SELECT format('CREATE POLICY runtime_key_access ON public.data_protection_keys TO %I USING (true) WITH CHECK (true)', :'runtime_role') \gexec

-- The environment marker is owner-managed and immutable to the API role.
SELECT format('REVOKE ALL ON TABLE public.environment_boundaries FROM %I', :'runtime_role')
WHERE to_regclass('public.environment_boundaries') IS NOT NULL
\gexec
SELECT format('GRANT SELECT ON TABLE public.environment_boundaries TO %I', :'runtime_role')
WHERE to_regclass('public.environment_boundaries') IS NOT NULL
\gexec
SELECT format('REVOKE ALL ON SEQUENCE %s FROM %I', sequence_name, :'runtime_role')
FROM (
    SELECT pg_get_serial_sequence('public.environment_boundaries', 'Id') AS sequence_name
) AS boundary_sequence
WHERE sequence_name IS NOT NULL
\gexec

-- Operational activity stays append-only, but privacy fulfillment is allowed
-- to redact only the columns that can contain guest-facing text.
SELECT format('REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON TABLE public.visit_records FROM %I', :'runtime_role')
WHERE to_regclass('public.visit_records') IS NOT NULL
\gexec
SELECT format('GRANT UPDATE ("GuestName") ON TABLE public.visit_records TO %I', :'runtime_role')
WHERE to_regclass('public.visit_records') IS NOT NULL
\gexec
SELECT format('REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON TABLE public.notifications FROM %I', :'runtime_role')
WHERE to_regclass('public.notifications') IS NOT NULL
\gexec
SELECT format('GRANT UPDATE ("Title", "Message") ON TABLE public.notifications TO %I', :'runtime_role')
WHERE to_regclass('public.notifications') IS NOT NULL
\gexec
SELECT format('REVOKE DELETE, TRUNCATE, REFERENCES, TRIGGER ON TABLE public.%I FROM %I', table_name, :'runtime_role')
FROM (VALUES
    ('bookings'),
    ('guests'),
    ('monnify_transactions'),
    ('folios'),
    ('room_inventory_closures'),
    ('room_types'),
    ('booking_addons'),
    ('guest_notes'),
    ('housekeeping_tasks'),
    ('maintenance_work_orders'),
    ('distribution_channels'),
    ('channel_events'),
    ('channel_reservation_mappings')
) AS retained_records(table_name)
WHERE to_regclass(format('public.%I', table_name)) IS NOT NULL
\gexec

-- Reservation amendments must be able to atomically reduce or replace the
-- per-booking unit rows. Deletes remain constrained by foreign keys, booking
-- locks, service authorization and the append-only amendment/audit records.
SELECT format('GRANT DELETE ON TABLE public.reservation_rooms TO %I', :'runtime_role')
WHERE to_regclass('public.reservation_rooms') IS NOT NULL
\gexec

SELECT has_schema_privilege(:'runtime_role', 'public', 'CREATE') AS runtime_has_schema_create
\gset
\if :runtime_has_schema_create
  DO $$
  BEGIN
    RAISE EXCEPTION 'Public-schema CREATE remains granted to the runtime role. Make the migration role the public schema owner, then rerun.';
  END
  $$;
\endif

SELECT 'Runtime database privileges provisioned.' AS result;
SQL
