#!/usr/bin/env bash
set -euo pipefail

database_provider="$(printf '%s' "${Database__Provider:-PostgreSql}" | tr '[:upper:]' '[:lower:]')"
if [[ "$database_provider" != "supabase" ]]; then
  exit 0
fi

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_directory/database-connection.sh"

if [[ -z "${MIGRATION_CONNECTION_STRING:-}" ]]; then
  echo "MIGRATION_CONNECTION_STRING is required." >&2
  exit 2
fi

configure_psql_connection "$MIGRATION_CONNECTION_STRING" "MIGRATION_CONNECTION_STRING"
run_psql <<'SQL'
DO $$
DECLARE
    exposed_role text;
BEGIN
    FOREACH exposed_role IN ARRAY ARRAY['anon', 'authenticated', 'service_role', 'authenticator']
    LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = exposed_role) THEN
            EXECUTE format('REVOKE ALL ON SCHEMA public FROM %I', exposed_role);
            EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA public FROM %I', exposed_role);
            EXECUTE format('REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM %I', exposed_role);
            EXECUTE format('REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM %I', exposed_role);
            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM %I', exposed_role);
            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON SEQUENCES FROM %I', exposed_role);
            EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE EXECUTE ON FUNCTIONS FROM %I', exposed_role);
        END IF;
    END LOOP;
END
$$;

REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON SEQUENCES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

DO $$
DECLARE
    exposed_role text;
BEGIN
    FOREACH exposed_role IN ARRAY ARRAY['anon', 'authenticated', 'service_role', 'authenticator']
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = exposed_role) THEN
            CONTINUE;
        END IF;

        IF has_schema_privilege(exposed_role, 'public', 'USAGE') OR
           has_schema_privilege(exposed_role, 'public', 'CREATE') THEN
            RAISE EXCEPTION 'Supabase Data API role % retains access to schema public.', exposed_role;
        END IF;

        IF EXISTS (
            SELECT 1
            FROM pg_class application_object
            JOIN pg_namespace object_schema ON object_schema.oid = application_object.relnamespace
            WHERE object_schema.nspname = 'public'
              AND application_object.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND (
                  has_table_privilege(exposed_role, application_object.oid, 'SELECT') OR
                  has_table_privilege(exposed_role, application_object.oid, 'INSERT') OR
                  has_table_privilege(exposed_role, application_object.oid, 'UPDATE') OR
                  has_table_privilege(exposed_role, application_object.oid, 'DELETE') OR
                  has_table_privilege(exposed_role, application_object.oid, 'TRUNCATE') OR
                  has_table_privilege(exposed_role, application_object.oid, 'REFERENCES') OR
                  has_table_privilege(exposed_role, application_object.oid, 'TRIGGER')
              )
        ) THEN
            RAISE EXCEPTION 'Supabase Data API role % retains table access in schema public.', exposed_role;
        END IF;

        IF EXISTS (
            SELECT 1
            FROM pg_class application_sequence
            JOIN pg_namespace object_schema ON object_schema.oid = application_sequence.relnamespace
            WHERE object_schema.nspname = 'public'
              AND application_sequence.relkind = 'S'
              AND (
                  has_sequence_privilege(exposed_role, application_sequence.oid, 'USAGE') OR
                  has_sequence_privilege(exposed_role, application_sequence.oid, 'SELECT') OR
                  has_sequence_privilege(exposed_role, application_sequence.oid, 'UPDATE')
              )
        ) THEN
            RAISE EXCEPTION 'Supabase Data API role % retains sequence access in schema public.', exposed_role;
        END IF;

        IF EXISTS (
            SELECT 1
            FROM pg_proc application_function
            JOIN pg_namespace object_schema ON object_schema.oid = application_function.pronamespace
            WHERE object_schema.nspname = 'public'
              AND has_function_privilege(exposed_role, application_function.oid, 'EXECUTE')
        ) THEN
            RAISE EXCEPTION 'Supabase Data API role % retains function access in schema public.', exposed_role;
        END IF;
    END LOOP;
END
$$;

SELECT 'Supabase Data API roles cannot access the application schema.' AS result;
SQL
