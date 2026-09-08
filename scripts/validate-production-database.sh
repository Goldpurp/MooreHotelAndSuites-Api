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

    IF to_regclass('public.room_types') IS NULL THEN
        SELECT count(*) INTO invalid_count
        FROM bookings b
        LEFT JOIN guests g ON g."Id" = b."GuestId"
        LEFT JOIN rooms r ON r."Id" = b."RoomId"
        WHERE g."Id" IS NULL OR r."Id" IS NULL;
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % orphan booking relationships.', invalid_count;
        END IF;

        SELECT count(*) INTO invalid_count
        FROM bookings first_booking
        JOIN bookings second_booking
          ON second_booking."RoomId" = first_booking."RoomId"
         AND second_booking."Id" > first_booking."Id"
         AND second_booking."CheckIn" < first_booking."CheckOut"
         AND second_booking."CheckOut" > first_booking."CheckIn"
        WHERE first_booking."Status" NOT IN ('Cancelled', 'CheckedOut', 'NoShow')
          AND second_booking."Status" NOT IN ('Cancelled', 'CheckedOut', 'NoShow');
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % overlapping physical-room reservations.', invalid_count;
        END IF;
    ELSE
        SELECT count(*) INTO invalid_count
        FROM bookings b
        LEFT JOIN guests g ON g."Id" = b."GuestId"
        LEFT JOIN room_types rt ON rt."Id" = b."RoomTypeId"
        LEFT JOIN rooms r ON r."Id" = b."RoomId"
        WHERE g."Id" IS NULL
           OR rt."Id" IS NULL
           OR b."RoomQuantity" NOT BETWEEN 1 AND 10
           OR (b."RoomId" IS NOT NULL AND
               (r."Id" IS NULL OR r."RoomTypeId" <> b."RoomTypeId"));
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % invalid room-type booking relationships.', invalid_count;
        END IF;

        SELECT count(*) INTO invalid_count
        FROM bookings b
        WHERE (SELECT count(*) FROM reservation_rooms rr WHERE rr."BookingId" = b."Id")
              <> b."RoomQuantity";
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % bookings have an incorrect reservation-unit count.', invalid_count;
        END IF;

        SELECT count(*) INTO invalid_count
        FROM reservation_rooms rr
        JOIN bookings b ON b."Id" = rr."BookingId"
        LEFT JOIN room_types rt ON rt."Id" = rr."RoomTypeId"
        LEFT JOIN rooms r ON r."Id" = rr."AssignedRoomId"
        WHERE rt."Id" IS NULL
           OR rr."RoomTypeId" <> b."RoomTypeId"
           OR length(trim(rr."RoomTypeCode")) = 0
           OR length(trim(rr."RoomTypeName")) = 0
           OR (rr."AssignedRoomId" IS NOT NULL AND
               (r."Id" IS NULL OR r."RoomTypeId" <> rr."RoomTypeId"));
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % reservation units have invalid room-type or assignment data.', invalid_count;
        END IF;

        SELECT count(*) INTO invalid_count
        FROM reservation_rooms first_unit
        JOIN reservation_rooms second_unit
          ON second_unit."AssignedRoomId" = first_unit."AssignedRoomId"
         AND second_unit."Id" > first_unit."Id"
        JOIN bookings first_booking ON first_booking."Id" = first_unit."BookingId"
        JOIN bookings second_booking ON second_booking."Id" = second_unit."BookingId"
        WHERE first_unit."AssignedRoomId" IS NOT NULL
          AND first_booking."Status" NOT IN ('Cancelled', 'CheckedOut', 'NoShow')
          AND second_booking."Status" NOT IN ('Cancelled', 'CheckedOut', 'NoShow')
          AND second_booking."CheckIn" < first_booking."CheckOut"
          AND second_booking."CheckOut" > first_booking."CheckIn";
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % overlapping physical-room assignments.', invalid_count;
        END IF;
    END IF;

    IF to_regclass('public.folios') IS NOT NULL THEN
        SELECT count(*) INTO invalid_count
        FROM bookings b
        LEFT JOIN folios f ON f."BookingId" = b."Id"
        WHERE f."Id" IS NULL OR f."Currency" <> b."Currency";
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % bookings have a missing or mismatched folio.', invalid_count;
        END IF;

        SELECT count(*) INTO invalid_count
        FROM folio_entries entry
        JOIN folios folio ON folio."Id" = entry."FolioId"
        LEFT JOIN folio_entries original ON original."Id" = entry."ReversesEntryId"
        WHERE entry."Amount" <= 0
           OR entry."Currency" <> folio."Currency"
           OR (entry."Type" = 'Void' AND
               (original."Id" IS NULL OR original."FolioId" <> entry."FolioId"))
           OR (entry."Type" <> 'Void' AND entry."ReversesEntryId" IS NOT NULL);
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % folio entries violate ledger invariants.', invalid_count;
        END IF;
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

    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'bookings'
          AND column_name = 'Currency'
    ) THEN
        SELECT count(*) INTO invalid_count
        FROM bookings
        WHERE "Currency" !~ '^[A-Z]{3}$'
           OR "RoomSubtotal" < 0
           OR "DiscountAmount" < 0
           OR "IncludedTaxAmount" < 0
           OR "TaxAmount" < 0
           OR "FeeAmount" < 0;
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % bookings have invalid price snapshots.', invalid_count;
        END IF;
    END IF;

    IF to_regclass('public.rate_plans') IS NOT NULL THEN
        SELECT count(*) INTO invalid_count
        FROM rate_plans
        WHERE "IsActive" AND "IsDefault";
        IF invalid_count <> 1 THEN
            RAISE EXCEPTION 'Preflight failed: expected one active default rate plan, found %.', invalid_count;
        END IF;
    END IF;

    IF to_regclass('public.booking_quotes') IS NOT NULL THEN
        IF to_regclass('public.room_types') IS NULL THEN
            SELECT count(*) INTO invalid_count
            FROM booking_quotes q
            LEFT JOIN rate_plans rp ON rp."Id" = q."RatePlanId"
            LEFT JOIN rooms r ON r."Id" = q."RoomId"
            WHERE rp."Id" IS NULL
               OR r."Id" IS NULL
               OR q."TotalAmount" <> q."RoomSubtotal" - q."DiscountAmount" + q."TaxAmount" + q."FeeAmount"
               OR q."ExpiresAtUtc" <= q."CreatedAtUtc";
        ELSE
            SELECT count(*) INTO invalid_count
            FROM booking_quotes q
            LEFT JOIN rate_plans rp ON rp."Id" = q."RatePlanId"
            LEFT JOIN room_types rt ON rt."Id" = q."RoomTypeId"
            LEFT JOIN rooms r ON r."Id" = q."RoomId"
            WHERE rp."Id" IS NULL
               OR rt."Id" IS NULL
               OR q."RoomQuantity" NOT BETWEEN 1 AND 10
               OR (q."RoomId" IS NOT NULL AND
                   (r."Id" IS NULL OR r."RoomTypeId" <> q."RoomTypeId"))
               OR q."TotalAmount" <> q."RoomSubtotal" - q."DiscountAmount" + q."TaxAmount" + q."FeeAmount"
               OR q."ExpiresAtUtc" <= q."CreatedAtUtc";
        END IF;
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % pricing quotes are invalid.', invalid_count;
        END IF;

        -- This script runs both before and after migration. Older releases
        -- have quotes but do not yet have the amendment-binding column.
        IF EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'booking_quotes'
              AND column_name = 'AmendmentBookingId'
        ) THEN
            SELECT count(*) INTO invalid_count
            FROM bookings booking
            JOIN booking_quotes quote ON quote."Id" = booking."QuoteId"
            WHERE quote."AmendmentBookingId" IS NOT NULL
              AND quote."AmendmentBookingId" <> booking."Id";
            IF invalid_count > 0 THEN
                RAISE EXCEPTION 'Preflight failed: % bookings reference an amendment quote bound to another reservation.', invalid_count;
            END IF;
        END IF;
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'email_outbox'
          AND column_name = 'QuarantinedAtUtc'
    ) THEN
        SELECT count(*) INTO invalid_count
        FROM email_outbox
        WHERE "QuarantinedAtUtc" IS NOT NULL
          AND ("Recipient" <> 'privacy-migration@delivery-failure.invalid'
               AND "Recipient" <> 'redacted@delivery-failure.invalid'
               OR "ProtectedPayload" <> '{}');
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % quarantined email records retain delivery payload data.', invalid_count;
        END IF;
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'bookings'
          AND column_name = 'ReservationPolicyVersion'
    ) THEN
        SELECT count(*) INTO invalid_count
        FROM bookings
        WHERE length(btrim("ReservationPolicyVersion")) = 0
           OR "FreeCancellationHours" NOT BETWEEN 0 AND 720
           OR "CancellationPenaltyPercent" NOT BETWEEN 0 AND 100
           OR "DepositPercent" NOT BETWEEN 0 AND 100
           OR "NoShowPenaltyPercent" NOT BETWEEN 0 AND 100
           OR "CancellationPenaltyAmount" < 0
           OR "NoShowPenaltyAmount" < 0;
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % bookings have invalid reservation-policy snapshots.', invalid_count;
        END IF;
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'guests'
          AND column_name = 'NormalizedEmail'
    ) THEN
        SELECT count(*) INTO invalid_count
        FROM guests
        WHERE "PreferencesJson" IS NULL
           OR "NormalizedEmail" IS NULL
           OR "NormalizedPhone" IS NULL;
        IF invalid_count > 0 THEN
            RAISE EXCEPTION 'Preflight failed: % guest CRM identities are not normalized.', invalid_count;
        END IF;
    END IF;

    IF to_regclass('public.booking_amendments') IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'TR_booking_amendments_immutable' AND NOT tgisinternal
    ) THEN
        RAISE EXCEPTION 'Preflight failed: reservation amendment immutability trigger is missing.';
    END IF;
    IF to_regclass('public.guest_merges') IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'TR_guest_merges_immutable' AND NOT tgisinternal
    ) THEN
        RAISE EXCEPTION 'Preflight failed: guest merge immutability trigger is missing.';
    END IF;
    IF to_regclass('public.night_audits') IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'TR_night_audits_immutable' AND NOT tgisinternal
    ) THEN
        RAISE EXCEPTION 'Preflight failed: night-audit immutability trigger is missing.';
    END IF;
END
$$;

SELECT 'Production database preflight passed.' AS result;
SQL
