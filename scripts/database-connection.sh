#!/usr/bin/env bash

configure_psql_connection() {
  local connection_string="$1"
  local variable_name="$2"

  unset PGHOST PGPORT PGDATABASE PGUSER PGPASSWORD PGSSLMODE PGSSLROOTCERT PGCONNECT_TIMEOUT PGOPTIONS
  if [[ "$connection_string" == postgresql://* || "$connection_string" == postgres://* ]]; then
    echo "$variable_name must use semicolon-separated key/value syntax; URI credentials are not accepted because psql would expose them in its process arguments." >&2
    return 2
  fi
  if [[ "$connection_string" == *"="* ]]; then
    local fields=()
    local field=""
    local quote=""
    local character key value normalized_key lower_value
    for ((index = 0; index < ${#connection_string}; index++)); do
      character="${connection_string:index:1}"
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
    [[ -z "$quote" ]] || { echo "Unterminated quote in $variable_name." >&2; return 2; }
    fields+=("$field")

    for field in "${fields[@]}"; do
      [[ -z "${field//[[:space:]]/}" ]] && continue
      [[ "$field" == *"="* ]] || { echo "Invalid database connection option in $variable_name." >&2; return 2; }
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
        maximumpoolsize|pooling|enlist|multiplexing|includeerrordetail) ;;
        *) echo "Unsupported database connection option in $variable_name: $key" >&2; return 2 ;;
      esac
    done
  else
    echo "$variable_name must use semicolon-separated key/value syntax." >&2
    return 2
  fi
}

run_psql() {
  psql --no-psqlrc --set ON_ERROR_STOP=1 "$@"
}
