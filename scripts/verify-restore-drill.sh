#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_directory/database-connection.sh"

if [[ -z "${RESTORE_DRILL_CONNECTION_STRING:-}" ]]; then
  echo "RESTORE_DRILL_CONNECTION_STRING is required." >&2
  exit 2
fi
if ! command -v psql >/dev/null 2>&1; then
  echo "psql is required for restore-drill verification." >&2
  exit 2
fi

configure_psql_connection \
  "$RESTORE_DRILL_CONNECTION_STRING" \
  "RESTORE_DRILL_CONNECTION_STRING"

database_name="$(run_psql --tuples-only --no-align --command 'SELECT current_database()')"
if [[ ! "$database_name" =~ (restore|drill|rehearsal) ]]; then
  echo "Refusing to inspect '$database_name': the database name must contain restore, drill, or rehearsal." >&2
  exit 2
fi

run_psql <<'SQL'
DO $$
DECLARE
    required_table text;
    missing_tables text[] := ARRAY[]::text[];
    invalid_count bigint;
BEGIN
    FOREACH required_table IN ARRAY ARRAY[
        'bookings',
        'guests',
        'rooms',
        'audit_logs',
        'email_outbox',
        'rate_plans',
        'booking_quotes',
        'booking_quote_lines',
        '__EFMigrationsHistory'
    ]
    LOOP
        IF to_regclass('public.' || quote_ident(required_table)) IS NULL THEN
            missing_tables := array_append(missing_tables, required_table);
        END IF;
    END LOOP;

    IF cardinality(missing_tables) > 0 THEN
        RAISE EXCEPTION 'Restore verification failed; missing required tables: %', missing_tables;
    END IF;

    SELECT count(*) INTO invalid_count
    FROM bookings b
    LEFT JOIN guests g ON g."Id" = b."GuestId"
    LEFT JOIN rooms r ON r."Id" = b."RoomId"
    WHERE g."Id" IS NULL
       OR r."Id" IS NULL
       OR b."CheckOut" <= b."CheckIn";

    IF invalid_count > 0 THEN
        RAISE EXCEPTION 'Restore verification failed; % invalid booking rows.', invalid_count;
    END IF;
END
$$;

SELECT
    current_database() AS restored_database,
    (SELECT count(*) FROM rooms) AS rooms,
    (SELECT count(*) FROM guests) AS guests,
    (SELECT count(*) FROM bookings) AS bookings,
    (SELECT count(*) FROM booking_quotes) AS pricing_quotes,
    (SELECT count(*) FROM audit_logs) AS audit_entries,
    (SELECT max("CreatedAt") FROM bookings) AS newest_booking,
    (SELECT max("MigrationId") FROM "__EFMigrationsHistory") AS latest_migration;
SQL

echo "Restore-drill structural and relationship verification passed for '$database_name'."
