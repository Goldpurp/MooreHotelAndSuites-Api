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

expected_migration="${RESTORE_DRILL_EXPECTED_MIGRATION:-}"
if [[ -z "$expected_migration" ]]; then
  migration_directory="$script_directory/../MooreHotels.Infrastructure/Migrations"
  if [[ -d "$migration_directory" ]]; then
    expected_migration="$(
      for migration in "$migration_directory"/[0-9]*.cs; do
        [[ "$migration" == *.Designer.cs ]] && continue
        basename "$migration" .cs
      done | sort | tail -n 1
    )"
  fi
fi
if [[ ! "$expected_migration" =~ ^[0-9]{14}_[A-Za-z0-9_]+$ ]]; then
  echo "RESTORE_DRILL_EXPECTED_MIGRATION must identify the release being restored when migration sources are unavailable." >&2
  exit 2
fi

run_psql --set "expected_migration=$expected_migration" <<'SQL'
DO $$
DECLARE
    required_table text;
    missing_tables text[] := ARRAY[]::text[];
    invalid_count bigint;
BEGIN
    FOREACH required_table IN ARRAY ARRAY[
        'environment_boundaries',
        'data_protection_keys',
        'users',
        'roles',
        'user_roles',
        'AspNetUserClaims',
        'AspNetUserLogins',
        'AspNetUserTokens',
        'AspNetRoleClaims',
        'bookings',
        'guests',
        'room_types',
        'rooms',
        'room_inventory_periods',
        'room_inventory_closures',
        'room_images',
        'media_assets',
        'media_deletion_outbox',
        'audit_logs',
        'visit_records',
        'notifications',
        'notification_receipts',
        'monnify_transactions',
        'email_outbox',
        'booking_email_verifications',
        'booking_code_allocations',
        'addon_services',
        'booking_addons',
        'privacy_requests',
        'rate_plans',
        'daily_room_rates',
        'pricing_rules',
        'promotions',
        'booking_quotes',
        'booking_quote_lines',
        'reservation_rooms',
        'folios',
        'folio_entries',
        'booking_amendments',
        'housekeeping_tasks',
        'maintenance_work_orders',
        'night_audits',
        'guest_notes',
        'guest_merges',
        'distribution_channels',
        'channel_events',
        'channel_reservation_mappings',
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

    IF (SELECT count(*) FROM environment_boundaries
        WHERE "Id" = 1 AND "EnvironmentName" = 'production') <> 1 THEN
        RAISE EXCEPTION 'Restore verification failed; production environment boundary is missing.';
    END IF;

    SELECT count(*) INTO invalid_count
    FROM bookings b
    LEFT JOIN guests g ON g."Id" = b."GuestId"
    LEFT JOIN room_types rt ON rt."Id" = b."RoomTypeId"
    LEFT JOIN rooms r ON r."Id" = b."RoomId"
    WHERE g."Id" IS NULL
       OR rt."Id" IS NULL
       OR (b."RoomId" IS NOT NULL AND
           (r."Id" IS NULL OR r."RoomTypeId" <> b."RoomTypeId"))
       OR b."CheckOut" <= b."CheckIn";

    IF invalid_count > 0 THEN
        RAISE EXCEPTION 'Restore verification failed; % invalid booking rows.', invalid_count;
    END IF;
END
$$;

SELECT COALESCE(max("MigrationId") = :'expected_migration', false) AS migration_matches
FROM "__EFMigrationsHistory"
\gset
\if :migration_matches
\else
  DO $$ BEGIN
    RAISE EXCEPTION 'Restore verification failed; migration history does not match the expected release.';
  END $$;
\endif

SELECT
    current_database() AS restored_database,
    (SELECT count(*) FROM rooms) AS rooms,
    (SELECT count(*) FROM guests) AS guests,
    (SELECT count(*) FROM bookings) AS bookings,
    (SELECT count(*) FROM booking_quotes) AS pricing_quotes,
    (SELECT count(*) FROM booking_amendments) AS reservation_amendments,
    (SELECT count(*) FROM night_audits) AS night_audits,
    (SELECT count(*) FROM channel_events) AS channel_events,
    (SELECT count(*) FROM audit_logs) AS audit_entries,
    (SELECT max("CreatedAt") FROM bookings) AS newest_booking,
    (SELECT max("MigrationId") FROM "__EFMigrationsHistory") AS latest_migration;
SQL

# Reuse the deployment invariant checks, including folios, unit counts, quotes,
# price snapshots, retained emails and append-only triggers. This is read-only.
MIGRATION_CONNECTION_STRING="$RESTORE_DRILL_CONNECTION_STRING" \
  "$script_directory/validate-production-database.sh"

echo "Restore-drill schema, release, environment and invariant verification passed for '$database_name'."
