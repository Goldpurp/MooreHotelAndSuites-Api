#!/usr/bin/env bash
set -euo pipefail

PORT="${MOORE_LOCAL_DB_PORT:-5433}"
DB_USER="${MOORE_LOCAL_DB_USER:-postgres}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STATE_DIR="$ROOT_DIR/.local/postgresql"
LOG_FILE="$STATE_DIR/server.log"

server_ready() {
  pg_isready --host=127.0.0.1 --port="$PORT" --username="$DB_USER" \
    >/dev/null 2>&1
}

find_data_directory() {
  if [[ -n "${MOORE_LOCAL_DB_DATA_DIR:-}" ]]; then
    if [[ -f "$MOORE_LOCAL_DB_DATA_DIR/PG_VERSION" ]]; then
      printf '%s\n' "$MOORE_LOCAL_DB_DATA_DIR"
      return 0
    fi

    echo "MOORE_LOCAL_DB_DATA_DIR does not contain a PostgreSQL cluster: $MOORE_LOCAL_DB_DATA_DIR" >&2
    return 1
  fi

  local brew_prefix=""
  if command -v brew >/dev/null 2>&1; then
    brew_prefix="$(brew --prefix 2>/dev/null || true)"
  fi

  local candidates=(
    "$brew_prefix/var/postgresql@17"
    "$brew_prefix/var/postgresql@16"
    "$brew_prefix/var/postgresql@15"
    "$brew_prefix/var/postgresql@14"
    "$brew_prefix/var/postgres"
    "/usr/local/var/postgresql@17"
    "/usr/local/var/postgresql@16"
    "/usr/local/var/postgresql@15"
    "/usr/local/var/postgresql@14"
    "/usr/local/var/postgres"
    "/opt/homebrew/var/postgresql@17"
    "/opt/homebrew/var/postgresql@16"
    "/opt/homebrew/var/postgresql@15"
    "/opt/homebrew/var/postgresql@14"
    "/opt/homebrew/var/postgres"
  )

  local candidate
  for candidate in "${candidates[@]}"; do
    if [[ -n "$candidate" && -f "$candidate/PG_VERSION" ]]; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done

  return 1
}

wait_until_ready() {
  local attempt
  for attempt in {1..40}; do
    if server_ready; then
      return 0
    fi
    sleep 0.25
  done
  return 1
}

connection_password() {
  local env_file="$ROOT_DIR/.env.local"
  if [[ ! -f "$env_file" ]]; then
    return 1
  fi

  local connection_line
  connection_line="$(awk '/^ConnectionStrings__DefaultConnection=/{print; exit}' "$env_file")"
  if [[ -z "$connection_line" ]]; then
    return 1
  fi

  local connection_string="${connection_line#*=}"
  connection_string="${connection_string#\"}"
  connection_string="${connection_string%\"}"

  local part key value
  IFS=';' read -r -a connection_parts <<< "$connection_string"
  for part in "${connection_parts[@]}"; do
    key="${part%%=*}"
    value="${part#*=}"
    key="$(printf '%s' "$key" | tr '[:upper:]' '[:lower:]' | tr -d '[:space:]')"
    if [[ "$key" == "password" ]]; then
      printf '%s\n' "$value"
      return 0
    fi
  done

  return 1
}

ensure_login_role() {
  if [[ ! "$DB_USER" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]]; then
    echo "MOORE_LOCAL_DB_USER is not a valid PostgreSQL role name." >&2
    exit 1
  fi

  if ! command -v psql >/dev/null 2>&1 || ! command -v createuser >/dev/null 2>&1; then
    echo "Required PostgreSQL client commands 'psql' and 'createuser' are unavailable." >&2
    exit 1
  fi

  local password
  if ! password="$(connection_password)" || [[ -z "$password" || "$password" == *"<"* ]]; then
    echo "A real Password value is required in ConnectionStrings__DefaultConnection inside .env.local." >&2
    exit 1
  fi

  local role_exists
  if ! role_exists="$(psql --port="$PORT" --dbname=postgres --tuples-only --no-align \
      --command="SELECT 1 FROM pg_roles WHERE rolname = '$DB_USER';" 2>/dev/null)"; then
    echo "The local PostgreSQL administrator connection failed." >&2
    echo "Set MOORE_LOCAL_DB_DATA_DIR to the cluster owned by your macOS account." >&2
    exit 1
  fi

  if [[ "$role_exists" != "1" ]]; then
    echo "Creating the missing local PostgreSQL role '$DB_USER'..."
    createuser --port="$PORT" --login --superuser "$DB_USER"
  fi

  if PGPASSWORD="$password" psql --host=127.0.0.1 --port="$PORT" \
      --username="$DB_USER" --dbname=postgres --command='SELECT 1;' \
      >/dev/null 2>&1; then
    return
  fi

  # Keep the dedicated Local login aligned with the ignored Local connection
  # string. The password is neither printed nor passed as a command argument.
  local escaped_password="${password//\'/\'\'}"
  psql --port="$PORT" --dbname=postgres --set=ON_ERROR_STOP=1 \
    >/dev/null <<< "ALTER ROLE \"$DB_USER\" WITH LOGIN SUPERUSER PASSWORD '$escaped_password';"

  if ! PGPASSWORD="$password" psql --host=127.0.0.1 --port="$PORT" \
      --username="$DB_USER" --dbname=postgres --command='SELECT 1;' \
      >/dev/null 2>&1; then
    echo "The local PostgreSQL role '$DB_USER' could not authenticate with .env.local." >&2
    exit 1
  fi
}

start_server() {
  if ! command -v pg_isready >/dev/null 2>&1 || ! command -v pg_ctl >/dev/null 2>&1; then
    echo "PostgreSQL commands are unavailable. Install PostgreSQL with Homebrew first." >&2
    echo "Example: brew install postgresql@14" >&2
    exit 1
  fi

  if server_ready; then
    ensure_login_role
    echo "PostgreSQL is already ready on 127.0.0.1:$PORT (user: $DB_USER)."
    return
  fi

  local data_directory
  if ! data_directory="$(find_data_directory)"; then
    echo "No initialized local PostgreSQL data directory was found." >&2
    echo "Set MOORE_LOCAL_DB_DATA_DIR to the directory containing PG_VERSION." >&2
    exit 1
  fi

  if pg_ctl -D "$data_directory" status >/dev/null 2>&1; then
    echo "The PostgreSQL cluster is running, but not on the required port $PORT." >&2
    echo "Stop the conflicting server or align ConnectionStrings__DefaultConnection and MOORE_LOCAL_DB_PORT." >&2
    exit 1
  fi

  mkdir -p "$STATE_DIR"
  echo "Starting local PostgreSQL on 127.0.0.1:$PORT..."
  if ! pg_ctl -D "$data_directory" -l "$LOG_FILE" \
      -o "-h 127.0.0.1 -p $PORT" start >/dev/null; then
    echo "PostgreSQL could not start. Recent server output:" >&2
    tail -n 20 "$LOG_FILE" >&2 || true
    exit 1
  fi

  if ! wait_until_ready; then
    echo "PostgreSQL started but did not become ready on port $PORT." >&2
    tail -n 20 "$LOG_FILE" >&2 || true
    exit 1
  fi

  ensure_login_role
  echo "PostgreSQL is ready on 127.0.0.1:$PORT (user: $DB_USER)."
}

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
  start) start_server ;;
  status) check_server ;;
  stop)
    echo "This project does not stop the shared PostgreSQL service." >&2
    exit 2
    ;;
  *) echo "Usage: $0 {start|status}" >&2; exit 2 ;;
esac
