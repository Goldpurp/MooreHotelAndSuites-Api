#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_directory/database-connection.sh"

if [[ -z "${BACKUP_CONNECTION_STRING:-}" ]]; then
  echo "BACKUP_CONNECTION_STRING is required." >&2
  exit 2
fi
command -v psql >/dev/null 2>&1 || { echo "psql is required for backup-role validation." >&2; exit 2; }

configure_psql_connection "$BACKUP_CONNECTION_STRING" "BACKUP_CONNECTION_STRING"
run_psql <<'SQL'
DO $$
DECLARE
    role_record record;
BEGIN
    SELECT rolsuper, rolcreaterole, rolcreatedb, rolreplication, rolbypassrls
    INTO STRICT role_record
    FROM pg_roles
    WHERE rolname = current_user;

    IF role_record.rolsuper OR role_record.rolcreaterole OR role_record.rolcreatedb OR
       role_record.rolreplication OR NOT role_record.rolbypassrls OR
       has_database_privilege(current_user, current_database(), 'CREATE') OR
       has_schema_privilege(current_user, 'public', 'CREATE') THEN
        RAISE EXCEPTION 'Backup role % has invalid administrative, DDL, or RLS attributes.', current_user;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_roles powerful
        WHERE powerful.rolname IN (
            'pg_read_all_data', 'pg_write_all_data', 'pg_read_server_files',
            'pg_write_server_files', 'pg_execute_server_program', 'pg_signal_backend'
        )
          AND pg_has_role(current_user, powerful.oid, 'MEMBER')
    ) THEN
        RAISE EXCEPTION 'Backup role % inherits a powerful predefined role.', current_user;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_auth_members membership
        JOIN pg_roles member_role ON member_role.oid = membership.member
        WHERE member_role.rolname = current_user
    ) THEN
        RAISE EXCEPTION 'Backup role % inherits another database role.', current_user;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_class application_object
        JOIN pg_namespace object_schema ON object_schema.oid = application_object.relnamespace
        WHERE object_schema.nspname = 'public'
          AND application_object.relkind IN ('r', 'p', 'S')
          AND (
              pg_get_userbyid(application_object.relowner) = current_user OR
              (application_object.relkind IN ('r', 'p') AND
               (NOT has_table_privilege(current_user, application_object.oid, 'SELECT') OR
                has_table_privilege(current_user, application_object.oid, 'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER'))) OR
              (application_object.relkind = 'S' AND
               (NOT has_sequence_privilege(current_user, application_object.oid, 'SELECT') OR
                has_sequence_privilege(current_user, application_object.oid, 'USAGE,UPDATE')))
          )
    ) THEN
        RAISE EXCEPTION 'Backup role % has incomplete reads, writes, or ownership in public.', current_user;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_proc application_function
        JOIN pg_namespace object_schema ON object_schema.oid = application_function.pronamespace
        WHERE object_schema.nspname = 'public'
          AND has_function_privilege(current_user, application_function.oid, 'EXECUTE')
    ) THEN
        RAISE EXCEPTION 'Backup role % can execute application-schema functions.', current_user;
    END IF;
END
$$;

SELECT 'Backup database role is complete and read-only.' AS result;
SQL
