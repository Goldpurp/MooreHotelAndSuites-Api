#!/usr/bin/env bash
set -euo pipefail
umask 077
script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_directory/database-connection.sh"
: "${BACKUP_CONNECTION_STRING:?Read-capable owner connection is required}"
: "${BACKUP_AGE_RECIPIENT:?An age public recipient is required}"
output="${1:?Usage: scripts/backup-production.sh NEW_OUTPUT.dump.age}"
[[ ! -e "$output" ]] || { echo 'Refusing to overwrite an existing backup.' >&2; exit 2; }
command -v age >/dev/null
command -v pg_dump >/dev/null
configure_psql_connection "$BACKUP_CONNECTION_STRING" BACKUP_CONNECTION_STRING
[[ "${PGSSLMODE:-}" == verify-full ]] || { echo 'Backup connections require VerifyFull TLS.' >&2; exit 2; }
# A sibling temporary file permits atomic publication only after both tools succeed.
temporary="$(mktemp "${output}.partial.XXXXXX")"
trap 'rm -f "$temporary"' EXIT
# public contains all API/Identity tables and encrypted keys; Supabase platform
# schemas and custom-role passwords are intentionally restored separately.
pg_dump --format=custom --schema=public --no-owner --no-privileges \
  | age --encrypt --recipient "$BACKUP_AGE_RECIPIENT" > "$temporary"
# Hard-link creation fails if another backup claimed this path while we ran.
ln "$temporary" "$output"
echo 'Encrypted application database backup created. Verify a restore before declaring recovery readiness.'
