#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_directory/database-connection.sh"

if [[ -z "${RUNTIME_CONNECTION_STRING:-}" ]]; then
  echo "RUNTIME_CONNECTION_STRING is required." >&2
  exit 2
fi
if ! command -v psql >/dev/null 2>&1; then
  echo "psql is required for runtime-role validation." >&2
  exit 2
fi

configure_psql_connection "$RUNTIME_CONNECTION_STRING" "RUNTIME_CONNECTION_STRING"
run_psql <<'SQL'
DO $$
DECLARE
    role_record record;
    owns_application_objects boolean;
    table_name text;
BEGIN
    SELECT rolsuper, rolcreaterole, rolcreatedb, rolreplication, rolbypassrls
    INTO role_record
    FROM pg_roles
    WHERE rolname = current_user;

    SELECT EXISTS (
        SELECT 1
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public'
          AND c.relkind IN ('r', 'p', 'S')
          AND pg_get_userbyid(c.relowner) = current_user
    ) INTO owns_application_objects;

    IF role_record.rolsuper OR role_record.rolcreaterole OR role_record.rolcreatedb OR
       role_record.rolreplication OR role_record.rolbypassrls OR
       has_database_privilege(current_user, current_database(), 'CREATE') OR
       has_schema_privilege(current_user, 'public', 'CREATE') OR
       owns_application_objects THEN
        RAISE EXCEPTION 'Runtime database role % has DDL, ownership, or administrative privileges.', current_user;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_roles powerful
        WHERE powerful.rolname IN (
            'pg_read_all_data',
            'pg_write_all_data',
            'pg_read_server_files',
            'pg_write_server_files',
            'pg_execute_server_program',
            'pg_signal_backend'
        )
          AND pg_has_role(current_user, powerful.oid, 'MEMBER')
    ) THEN
        RAISE EXCEPTION 'Runtime database role % inherits a powerful predefined role.', current_user;
    END IF;

    IF to_regclass('public.bookings') IS NOT NULL AND
       (NOT has_table_privilege(current_user, 'public.bookings', 'SELECT,INSERT,UPDATE') OR
        has_table_privilege(current_user, 'public.bookings', 'DELETE')) THEN
        RAISE EXCEPTION 'Runtime database role % lacks required booking DML privileges.', current_user;
    END IF;

    IF to_regclass('public."__EFMigrationsHistory"') IS NOT NULL AND
       (has_table_privilege(current_user, 'public."__EFMigrationsHistory"', 'SELECT') OR
        has_table_privilege(current_user, 'public."__EFMigrationsHistory"', 'INSERT') OR
        has_table_privilege(current_user, 'public."__EFMigrationsHistory"', 'UPDATE') OR
        has_table_privilege(current_user, 'public."__EFMigrationsHistory"', 'DELETE') OR
        has_table_privilege(current_user, 'public."__EFMigrationsHistory"', 'TRUNCATE') OR
        has_table_privilege(current_user, 'public."__EFMigrationsHistory"', 'REFERENCES') OR
        has_table_privilege(current_user, 'public."__EFMigrationsHistory"', 'TRIGGER')) THEN
        RAISE EXCEPTION 'Runtime database role % can access migration history.', current_user;
    END IF;

    IF to_regclass('public.audit_logs') IS NOT NULL AND
       (NOT has_table_privilege(current_user, 'public.audit_logs', 'SELECT,INSERT') OR
        has_table_privilege(current_user, 'public.audit_logs', 'UPDATE') OR
        has_table_privilege(current_user, 'public.audit_logs', 'DELETE') OR
        has_table_privilege(current_user, 'public.audit_logs', 'TRUNCATE') OR
        has_table_privilege(current_user, 'public.audit_logs', 'REFERENCES') OR
        has_table_privilege(current_user, 'public.audit_logs', 'TRIGGER')) THEN
        RAISE EXCEPTION 'Runtime database role % does not have append-only audit access.', current_user;
    END IF;

    FOREACH table_name IN ARRAY ARRAY[
        'booking_code_allocations',
        'visit_records',
        'notifications'
    ]
    LOOP
        IF to_regclass(format('public.%I', table_name)) IS NOT NULL AND
           (NOT has_table_privilege(current_user, format('public.%I', table_name), 'SELECT,INSERT') OR
            has_table_privilege(current_user, format('public.%I', table_name), 'UPDATE') OR
            has_table_privilege(current_user, format('public.%I', table_name), 'DELETE') OR
            has_table_privilege(current_user, format('public.%I', table_name), 'TRUNCATE') OR
            has_table_privilege(current_user, format('public.%I', table_name), 'REFERENCES') OR
            has_table_privilege(current_user, format('public.%I', table_name), 'TRIGGER')) THEN
            RAISE EXCEPTION 'Runtime database role % does not have append-only access to %.', current_user, table_name;
        END IF;
    END LOOP;

    FOREACH table_name IN ARRAY ARRAY['bookings', 'guests', 'monnify_transactions']
    LOOP
        IF to_regclass(format('public.%I', table_name)) IS NOT NULL AND
           has_table_privilege(current_user, format('public.%I', table_name), 'DELETE') THEN
            RAISE EXCEPTION 'Runtime database role % can delete retained financial records from %.', current_user, table_name;
        END IF;
    END LOOP;
END
$$;

SELECT 'Runtime database role is least-privileged.' AS result;
SQL
