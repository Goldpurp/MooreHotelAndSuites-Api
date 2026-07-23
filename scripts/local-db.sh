#!/usr/bin/env bash
set -euo pipefail

PORT="${MOORE_LOCAL_DB_PORT:-5433}"
DB_USER="${MOORE_LOCAL_DB_USER:-postgres}"

check_server() {
  if ! command -v pg_isready >/dev/null 2>&1; then
    echo "Required command 'pg_isready' is not installed. Install PostgreSQL first." >&2
    exit 1
  fi

  if ! pg_isready --host=127.0.0.1 --port="$PORT" --username="$DB_USER" >/dev/null 2>&1; then
    echo "PostgreSQL is not accepting connections on 127.0.0.1:$PORT." >&2
    echo "Start the installed PostgreSQL service, then run scripts/run-local.sh." >&2
    exit 1
  fi

  echo "PostgreSQL is ready on 127.0.0.1:$PORT (user: $DB_USER)."
  echo "The Local API creates and migrates its configured database during startup."
}

case "${1:-status}" in
  start|status) check_server ;;
  stop)
    echo "This project does not stop the shared PostgreSQL service." >&2
    exit 2
    ;;
  *) echo "Usage: $0 {start|status}" >&2; exit 2 ;;
esac
