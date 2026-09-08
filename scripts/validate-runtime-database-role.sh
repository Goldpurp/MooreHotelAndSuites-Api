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
    inherits_owner_privileges boolean;
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

    SELECT EXISTS (
        SELECT 1
        FROM pg_roles inherited_role
        WHERE inherited_role.oid <> (SELECT oid FROM pg_roles WHERE rolname = current_user)
          AND pg_has_role(current_user, inherited_role.oid, 'USAGE')
          AND (
              inherited_role.rolsuper OR inherited_role.rolcreaterole OR
              inherited_role.rolcreatedb OR inherited_role.rolreplication OR
              inherited_role.rolbypassrls OR
              EXISTS (
                  SELECT 1
                  FROM pg_class owned_object
                  JOIN pg_namespace owned_schema ON owned_schema.oid = owned_object.relnamespace
                  WHERE owned_schema.nspname = 'public'
                    AND owned_object.relowner = inherited_role.oid
              ) OR
              EXISTS (
                  SELECT 1
                  FROM pg_proc owned_function
                  JOIN pg_namespace owned_schema ON owned_schema.oid = owned_function.pronamespace
                  WHERE owned_schema.nspname = 'public'
                    AND owned_function.proowner = inherited_role.oid
              )
          )
    ) INTO inherits_owner_privileges;

    IF role_record.rolsuper OR role_record.rolcreaterole OR role_record.rolcreatedb OR
       role_record.rolreplication OR role_record.rolbypassrls OR
       has_database_privilege(current_user, current_database(), 'CREATE') OR
       has_schema_privilege(current_user, 'public', 'CREATE') OR
       owns_application_objects OR inherits_owner_privileges THEN
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
        RAISE EXCEPTION 'Runtime database role % has invalid booking DML privileges.', current_user;
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

    IF to_regclass('public.environment_boundaries') IS NULL OR
       NOT EXISTS (
           SELECT 1 FROM public.environment_boundaries
           WHERE "Id" = 1 AND "EnvironmentName" = 'production'
       ) OR
       NOT has_table_privilege(current_user, 'public.environment_boundaries', 'SELECT') OR
       has_table_privilege(current_user, 'public.environment_boundaries', 'INSERT') OR
       has_table_privilege(current_user, 'public.environment_boundaries', 'UPDATE') OR
       has_table_privilege(current_user, 'public.environment_boundaries', 'DELETE') OR
       has_table_privilege(current_user, 'public.environment_boundaries', 'TRUNCATE') OR
       has_table_privilege(current_user, 'public.environment_boundaries', 'REFERENCES') OR
       has_table_privilege(current_user, 'public.environment_boundaries', 'TRIGGER') OR
       (pg_get_serial_sequence('public.environment_boundaries', 'Id') IS NOT NULL AND
        (has_sequence_privilege(
             current_user,
             pg_get_serial_sequence('public.environment_boundaries', 'Id'),
             'USAGE') OR
         has_sequence_privilege(
             current_user,
             pg_get_serial_sequence('public.environment_boundaries', 'Id'),
             'SELECT') OR
         has_sequence_privilege(
             current_user,
             pg_get_serial_sequence('public.environment_boundaries', 'Id'),
             'UPDATE'))) THEN
        RAISE EXCEPTION 'Runtime database role % has an invalid production environment boundary.', current_user;
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
        'booking_amendments',
        'folio_entries',
        'guest_merges',
        'night_audits'
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

    IF to_regclass('public.visit_records') IS NOT NULL AND
       (NOT has_table_privilege(current_user, 'public.visit_records', 'SELECT,INSERT') OR
        NOT has_column_privilege(current_user, 'public.visit_records', 'GuestName', 'UPDATE') OR
        has_table_privilege(current_user, 'public.visit_records', 'UPDATE') OR
        has_table_privilege(current_user, 'public.visit_records', 'DELETE') OR
        has_table_privilege(current_user, 'public.visit_records', 'TRUNCATE') OR
        has_table_privilege(current_user, 'public.visit_records', 'REFERENCES') OR
        has_table_privilege(current_user, 'public.visit_records', 'TRIGGER') OR
        EXISTS (
            SELECT 1 FROM information_schema.columns AS application_column
            WHERE application_column.table_schema = 'public'
              AND application_column.table_name = 'visit_records'
              AND application_column.column_name <> 'GuestName'
              AND has_column_privilege(
                  current_user,
                  'public.visit_records',
                  application_column.column_name,
                  'UPDATE')
        )) THEN
        RAISE EXCEPTION 'Runtime database role % has an invalid visit-record redaction grant.', current_user;
    END IF;

    IF to_regclass('public.notifications') IS NOT NULL AND
       (NOT has_table_privilege(current_user, 'public.notifications', 'SELECT,INSERT') OR
        NOT has_column_privilege(current_user, 'public.notifications', 'Title', 'UPDATE') OR
        NOT has_column_privilege(current_user, 'public.notifications', 'Message', 'UPDATE') OR
        has_table_privilege(current_user, 'public.notifications', 'UPDATE') OR
        has_table_privilege(current_user, 'public.notifications', 'DELETE') OR
        has_table_privilege(current_user, 'public.notifications', 'TRUNCATE') OR
        has_table_privilege(current_user, 'public.notifications', 'REFERENCES') OR
        has_table_privilege(current_user, 'public.notifications', 'TRIGGER') OR
        EXISTS (
            SELECT 1 FROM information_schema.columns AS application_column
            WHERE application_column.table_schema = 'public'
              AND application_column.table_name = 'notifications'
              AND application_column.column_name NOT IN ('Title', 'Message')
              AND has_column_privilege(
                  current_user,
                  'public.notifications',
                  application_column.column_name,
                  'UPDATE')
        )) THEN
        RAISE EXCEPTION 'Runtime database role % has an invalid notification redaction grant.', current_user;
    END IF;

    FOREACH table_name IN ARRAY ARRAY[
        'bookings',
        'guests',
        'monnify_transactions',
        'folios',
        'room_inventory_closures',
        'room_types',
        'booking_addons',
        'guest_notes',
        'housekeeping_tasks',
        'maintenance_work_orders',
        'distribution_channels',
        'channel_events',
        'channel_reservation_mappings'
    ]
    LOOP
        IF to_regclass(format('public.%I', table_name)) IS NOT NULL AND
           has_table_privilege(current_user, format('public.%I', table_name), 'DELETE') THEN
            RAISE EXCEPTION 'Runtime database role % can delete retained financial records from %.', current_user, table_name;
        END IF;
    END LOOP;

    IF to_regclass('public.reservation_rooms') IS NOT NULL AND
       NOT has_table_privilege(current_user, 'public.reservation_rooms', 'SELECT,INSERT,UPDATE,DELETE') THEN
        RAISE EXCEPTION 'Runtime database role % cannot perform atomic reservation-unit replacement.', current_user;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_class application_table
        JOIN pg_namespace object_schema ON object_schema.oid = application_table.relnamespace
        WHERE object_schema.nspname = 'public'
          AND application_table.relkind IN ('r', 'p')
          AND application_table.relname <> '__EFMigrationsHistory'
          AND (NOT has_table_privilege(current_user, application_table.oid, 'SELECT') OR
               (application_table.relname <> 'environment_boundaries' AND
                NOT has_table_privilege(current_user, application_table.oid, 'INSERT')))
    ) THEN
        RAISE EXCEPTION 'Runtime database role % lacks baseline read/insert access to an application table.', current_user;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_proc application_function
        JOIN pg_namespace object_schema ON object_schema.oid = application_function.pronamespace
        WHERE object_schema.nspname = 'public'
          AND has_function_privilege(current_user, application_function.oid, 'EXECUTE')
    ) THEN
        RAISE EXCEPTION 'Runtime database role % can execute application-schema functions directly.', current_user;
    END IF;
END
$$;

SELECT 'Runtime database role is least-privileged.' AS result;
SQL
