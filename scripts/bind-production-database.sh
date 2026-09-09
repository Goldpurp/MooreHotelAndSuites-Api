#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_directory/database-connection.sh"

mode="${1:-check}"
if [[ "$mode" != "check" && "$mode" != "bind" ]]; then
  echo "Usage: $0 [check|bind]" >&2
  exit 2
fi
if [[ -z "${MIGRATION_CONNECTION_STRING:-}" ]]; then
  echo "MIGRATION_CONNECTION_STRING is required." >&2
  exit 2
fi
if ! command -v psql >/dev/null 2>&1; then
  echo "psql is required to enforce the production database boundary." >&2
  exit 2
fi

configure_psql_connection "$MIGRATION_CONNECTION_STRING" "MIGRATION_CONNECTION_STRING"

is_rehearsal="${MOORE_PRODUCTION_REHEARSAL:-false}"
database_name="$(printf '%s' "${PGDATABASE:-}" | tr '[:upper:]' '[:lower:]')"
database_host="$(printf '%s' "${PGHOST:-}" | tr '[:upper:]' '[:lower:]')"
database_provider="$(printf '%s' "${Database__Provider:-PostgreSql}" | tr '[:upper:]' '[:lower:]')"
if [[ "$is_rehearsal" == "true" ]]; then
  if [[ ! "$database_name" =~ (^|[_-])(test|tests|rehearsal)([_-]|$) ]]; then
    echo "MOORE_PRODUCTION_REHEARSAL requires an explicitly named test/rehearsal database." >&2
    exit 2
  fi
else
  if [[ "$database_name" =~ (^|[_-])(local|dev|development|test|tests|restore|drill|rehearsal)([_-]|$) ]]; then
    echo "Production pre-deploy refuses a Local, test, restore, or rehearsal database." >&2
    exit 2
  fi
  if [[ "$database_host" == "localhost" || "$database_host" == "127.0.0.1" || "$database_host" == "::1" ]]; then
    echo "Production pre-deploy refuses a loopback database host." >&2
    exit 2
  fi
  if [[ "$database_provider" == "supabase" ]]; then
    if [[ "$database_host" != *.pooler.supabase.com || "${PGPORT:-5432}" != "5432" || "${PGSSLMODE:-}" != "verify-full" ]]; then
      echo "Supabase production migration must use its port-5432 session pooler with verify-full TLS." >&2
      exit 2
    fi
  fi
fi

bind_environment=0
[[ "$mode" == "bind" ]] && bind_environment=1

run_psql --set "bind_environment=$bind_environment" <<'SQL'
DO $$
DECLARE
    actual_environment text;
BEGIN
    IF to_regclass('public.environment_boundaries') IS NULL THEN
        RETURN;
    END IF;

    SELECT "EnvironmentName" INTO actual_environment
    FROM public.environment_boundaries
    WHERE "Id" = 1;

    IF actual_environment IS NOT NULL AND actual_environment <> 'production' THEN
        RAISE EXCEPTION 'Database is already bound to environment %, not production.', actual_environment;
    END IF;
END
$$;

\if :bind_environment
INSERT INTO public.environment_boundaries ("Id", "EnvironmentName", "BoundAtUtc")
VALUES (1, 'production', CURRENT_TIMESTAMP)
ON CONFLICT ("Id") DO NOTHING;

DO $$
DECLARE
    boundary_count integer;
BEGIN
    SELECT count(*) INTO boundary_count
    FROM public.environment_boundaries
    WHERE "Id" = 1 AND "EnvironmentName" = 'production';
    IF boundary_count <> 1 THEN
        RAISE EXCEPTION 'Production database environment binding failed.';
    END IF;
END
$$;
\endif

SELECT CASE
    WHEN :bind_environment::integer = 1 THEN 'Production database boundary bound.'
    ELSE 'Production database boundary precheck passed.'
END AS result;
SQL
