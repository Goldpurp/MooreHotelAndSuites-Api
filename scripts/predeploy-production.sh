#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
"$script_directory/validate-production-database.sh"
"$script_directory/../migrate" --connection "$MIGRATION_CONNECTION_STRING"
"$script_directory/provision-runtime-database-role.sh"
