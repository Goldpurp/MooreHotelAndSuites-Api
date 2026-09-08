#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
"$script_directory/bind-production-database.sh" check
"$script_directory/harden-supabase-data-api.sh"
"$script_directory/validate-production-database.sh"
ConnectionStrings__DefaultConnection="$MIGRATION_CONNECTION_STRING" \
  "$script_directory/../migrate"
"$script_directory/bind-production-database.sh" bind
"$script_directory/validate-production-database.sh"
"$script_directory/harden-supabase-data-api.sh"
"$script_directory/provision-runtime-database-role.sh"
