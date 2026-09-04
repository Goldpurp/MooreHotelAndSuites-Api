#!/usr/bin/env bash
set -euo pipefail

if [[ -z "${MIGRATION_CONNECTION_STRING:-}" ]]; then
  echo "MIGRATION_CONNECTION_STRING is required." >&2
  exit 2
fi

if ! command -v psql >/dev/null 2>&1; then
  echo "psql is required for the production database preflight." >&2
  exit 2
fi

use_connection_argument=false
if [[ "$MIGRATION_CONNECTION_STRING" == *"="* &&
      "$MIGRATION_CONNECTION_STRING" != postgresql://* &&
      "$MIGRATION_CONNECTION_STRING" != postgres://* ]]; then
  # Convert the Npgsql key/value connection string used by the migration
  # bundle into libpq environment variables without printing credentials.
  fields=()
  field=""
  quote=""
  for ((index = 0; index < ${#MIGRATION_CONNECTION_STRING}; index++)); do
    character="${MIGRATION_CONNECTION_STRING:index:1}"
    if [[ -n "$quote" ]]; then
      if [[ "$character" == "$quote" ]]; then
        quote=""
      else
        field+="$character"
      fi
    elif [[ "$character" == "'" || "$character" == '"' ]]; then
      quote="$character"
    elif [[ "$character" == ";" ]]; then
      fields+=("$field")
      field=""
    else
      field+="$character"
    fi
  done
  [[ -z "$quote" ]] || { echo "Unterminated quote in MIGRATION_CONNECTION_STRING." >&2; exit 2; }
  fields+=("$field")

  for field in "${fields[@]}"; do
    [[ -z "${field//[[:space:]]/}" ]] && continue
    [[ "$field" == *"="* ]] || { echo "Invalid database connection option." >&2; exit 2; }
    key="${field%%=*}"
    value="${field#*=}"
    key="${key#"${key%%[![:space:]]*}"}"
    key="${key%"${key##*[![:space:]]}"}"
    value="${value#"${value%%[![:space:]]*}"}"
    value="${value%"${value##*[![:space:]]}"}"
    normalized_key="$(printf '%s' "$key" | tr '[:upper:]' '[:lower:]')"
    normalized_key="${normalized_key// /}"
    case "$normalized_key" in
      host) export PGHOST="$value" ;;
      port) export PGPORT="$value" ;;
      database) export PGDATABASE="$value" ;;
      username|userid|user) export PGUSER="$value" ;;
      password) export PGPASSWORD="$value" ;;
      sslmode)
        lower_value="$(printf '%s' "$value" | tr '[:upper:]' '[:lower:]')"
        case "$lower_value" in
          verifyfull) export PGSSLMODE="verify-full" ;;
          verifyca) export PGSSLMODE="verify-ca" ;;
          *) export PGSSLMODE="$lower_value" ;;
        esac
        ;;
      rootcertificate) export PGSSLROOTCERT="$value" ;;
      timeout) export PGCONNECT_TIMEOUT="$value" ;;
      commandtimeout) export PGOPTIONS="-c statement_timeout=${value}s" ;;
      maximumpoolsize|pooling|enlist|multiplexing) ;;
      *) echo "Unsupported database connection option: $key" >&2; exit 2 ;;
    esac
  done
else
  use_connection_argument=true
fi

run_psql() {
  if [[ "$use_connection_argument" == true ]]; then
    psql "$MIGRATION_CONNECTION_STRING" --no-psqlrc --set ON_ERROR_STOP=1
  else
    psql --no-psqlrc --set ON_ERROR_STOP=1
  fi
}

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
END
$$;

SELECT 'Production database preflight passed.' AS result;
SQL
