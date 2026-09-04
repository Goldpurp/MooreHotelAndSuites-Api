#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_directory/database-connection.sh"

if [[ -z "${MIGRATION_CONNECTION_STRING:-}" ]]; then
  echo "MIGRATION_CONNECTION_STRING is required." >&2
  exit 2
fi

if ! command -v psql >/dev/null 2>&1; then
  echo "psql is required for the production database preflight." >&2
  exit 2
fi

configure_psql_connection "$MIGRATION_CONNECTION_STRING" "MIGRATION_CONNECTION_STRING"

run_psql <<'SQL'
DO $$
DECLARE
    invalid_count bigint;
BEGIN
    IF to_regclass('public.bookings') IS NULL THEN
        RAISE NOTICE 'No existing bookings table; this is an empty-database deployment.';
        RETURN;
    END IF;

    SELECT count(*) INTO invalid_count
    FROM bookings
    WHERE "CheckOut" <= "CheckIn"
       OR length("BookingCode") > 30;
    IF invalid_count > 0 THEN
        RAISE EXCEPTION 'Preflight failed: % invalid booking rows.', invalid_count;
    END IF;

    SELECT count(*) INTO invalid_count
    FROM (
        SELECT "RefundReference"
        FROM bookings
        WHERE "RefundReference" IS NOT NULL
        GROUP BY "RefundReference"
        HAVING count(*) > 1
    ) duplicate_refunds;
    IF invalid_count > 0 THEN
        RAISE EXCEPTION 'Preflight failed: % duplicate refund references.', invalid_count;
    END IF;

    SELECT count(*) INTO invalid_count
    FROM (
        SELECT "BookingCode" FROM bookings GROUP BY "BookingCode" HAVING count(*) > 1
    ) duplicates;
    IF invalid_count > 0 THEN
        RAISE EXCEPTION 'Preflight failed: % duplicate booking codes.', invalid_count;
    END IF;

    SELECT count(*) INTO invalid_count
    FROM bookings b
    LEFT JOIN guests g ON g."Id" = b."GuestId"
    LEFT JOIN rooms r ON r."Id" = b."RoomId"
    WHERE g."Id" IS NULL OR r."Id" IS NULL;
    IF invalid_count > 0 THEN
        RAISE EXCEPTION 'Preflight failed: % orphan booking relationships.', invalid_count;
    END IF;

    IF to_regclass('public.room_images') IS NOT NULL THEN
        SELECT count(*) INTO invalid_count
        FROM (
            SELECT "PublicId" FROM room_images GROUP BY "PublicId" HAVING count(*) > 1
        ) duplicates;
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % duplicate room image public IDs.', invalid_count;
        END IF;
    END IF;

    IF to_regclass('public.addon_services') IS NOT NULL THEN
        SELECT count(*) INTO invalid_count
        FROM addon_services
        WHERE "Price" <= 0;
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % add-on services have invalid prices.', invalid_count;
        END IF;
    END IF;

    IF to_regclass('public.booking_addons') IS NOT NULL THEN
        SELECT count(*) INTO invalid_count
        FROM booking_addons
        WHERE "Quantity" <= 0
           OR "UnitPrice" <= 0
           OR "TotalPrice" <> "UnitPrice" * "Quantity";
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % booking add-ons have invalid totals.', invalid_count;
        END IF;
    END IF;

    IF to_regclass('public.media_assets') IS NOT NULL THEN
        SELECT count(*) INTO invalid_count
        FROM (
            SELECT "PublicId"
            FROM (
                SELECT "PublicId" FROM room_images
                UNION ALL
                SELECT "PublicId" FROM media_assets
                UNION ALL
                SELECT "AvatarPublicId" AS "PublicId" FROM users WHERE "AvatarPublicId" IS NOT NULL
            ) assets
            GROUP BY "PublicId"
            HAVING count(*) > 1
        ) duplicates;
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % image public IDs are referenced more than once.', invalid_count;
        END IF;
    END IF;

    IF to_regclass('public.users') IS NOT NULL AND to_regclass('public.guests') IS NOT NULL THEN
        SELECT count(*) INTO invalid_count
        FROM users u
        LEFT JOIN guests g ON g."Id" = u."GuestId"
        WHERE u."Role" = 'Client'
          AND (u."GuestId" IS NULL OR g."Id" IS NULL);
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % client accounts require guest-profile reconciliation.', invalid_count;
        END IF;
    END IF;
END
$$;

SELECT 'Production database preflight passed.' AS result;
SQL
