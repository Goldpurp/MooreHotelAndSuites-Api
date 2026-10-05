#!/usr/bin/env bash
set -euo pipefail
umask 077
script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_directory/database-connection.sh"
archive="${1:?Usage: restore-encrypted-backup.sh ARCHIVE.dump.age}"
: "${RESTORE_AGE_IDENTITY_FILE:?Path to the age identity is required}"
: "${RESTORE_DRILL_CONNECTION_STRING:?Isolated target connection is required}"
: "${RESTORE_DRILL_EXPECTED_DATABASE:?Explicit isolated database name is required}"
[[ "$RESTORE_DRILL_EXPECTED_DATABASE" =~ ^[a-zA-Z0-9_]*(restore|drill|rehearsal)[a-zA-Z0-9_]*$ ]] || { echo 'Use an explicitly named restore, drill or rehearsal database.' >&2; exit 2; }
[[ -f "$archive" && -s "$archive" ]] || { echo 'Encrypted archive is missing or empty.' >&2; exit 2; }
# Validate permissions without reading or printing the key.
python3 - "$RESTORE_AGE_IDENTITY_FILE" <<'PY'
import os, pathlib, stat, sys
path = pathlib.Path(sys.argv[1])
if path.is_symlink() or not path.is_file():
    raise SystemExit('Identity must be a regular non-symlink file.')
info = path.stat()
if info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) & 0o077:
    raise SystemExit('Identity must be owned by the current user with mode 0600 or stricter.')
PY
for tool in age pg_restore psql; do command -v "$tool" >/dev/null; done
configure_psql_connection "$RESTORE_DRILL_CONNECTION_STRING" RESTORE_DRILL_CONNECTION_STRING
[[ "${PGDATABASE:-}" == "$RESTORE_DRILL_EXPECTED_DATABASE" ]] || { echo 'Target database does not match the approved isolated name.' >&2; exit 2; }
case "${PGHOST:-}" in
  localhost|127.0.0.1|::1) ;;
  *) [[ "${PGSSLMODE:-}" == verify-full ]] || { echo 'Remote restore requires VerifyFull TLS.' >&2; exit 2; } ;;
esac
actual="$(run_psql --tuples-only --no-align --command 'SELECT current_database()')"
[[ "$actual" == "$RESTORE_DRILL_EXPECTED_DATABASE" ]] || { echo 'Connected database identity does not match.' >&2; exit 2; }
count="$(run_psql --tuples-only --no-align --command "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%' AND c.relkind IN ('r','p','v','m','S','f')")"
[[ "$count" == 0 ]] || { echo 'Refusing to restore into a database containing application objects.' >&2; exit 2; }
# Authenticate the entire age stream before invoking pg_restore. Only this private
# temporary file is removed; the supplied archive and identity are never changed.
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
age --decrypt --identity "$RESTORE_AGE_IDENTITY_FILE" --output "$work/verified.dump" "$archive"
pg_restore --list "$work/verified.dump" >/dev/null
# No --clean or --create. One transaction prevents a partial SQL restore.
pg_restore --exit-on-error --single-transaction --no-owner --no-privileges --dbname "$PGDATABASE" "$work/verified.dump"
"$script_directory/verify-restore-drill.sh"
echo 'Database archive restored and invariants verified. Application key decryption and media recovery still require separate acceptance.'
