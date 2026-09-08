#!/usr/bin/env bash
set -euo pipefail

# Run against an isolated Local/test database only. The API's Local boundary
# rejects production hosts and database names before connecting.
: "${MOORE_CONTAINER_TEST_POSTGRES:?Set the disposable Local/test PostgreSQL connection string.}"
image="${1:-moore-hotels-api:ci}"
network="${MOORE_CONTAINER_TEST_NETWORK:-host}"
container="moore-runtime-check-$$"
trap 'docker rm --force "$container" >/dev/null 2>&1 || true' EXIT

docker run --detach --name "$container" --network "$network" \
  --env ASPNETCORE_ENVIRONMENT=Local \
  --env "ConnectionStrings__DefaultConnection=$MOORE_CONTAINER_TEST_POSTGRES" \
  --env Jwt__Key=disposable-container-check-key-0123456789abcdef0123456789abcdef \
  --env Jwt__Issuer=MooreHotels.Local \
  --env Jwt__Audience=MooreHotels.LocalClients \
  --env SeedAdmin=false \
  --env MIGRATION_CONNECTION_STRING=deployment-owner-sentinel \
  --env DATABASE_RUNTIME_PASSWORD=deployment-password-sentinel \
  "$image" >/dev/null

ready=false
for attempt in {1..60}; do
  if response="$(docker exec "$container" bash -c \
      'exec 3<>/dev/tcp/127.0.0.1/8080; printf "GET /health/ready HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n" >&3; cat <&3' 2>/dev/null)" &&
     [[ "$response" == *"HTTP/1.1 200 OK"* ]]; then
    ready=true
    break
  fi
  if [[ "$(docker inspect --format '{{.State.Running}}' "$container")" != true ]]; then
    break
  fi
  sleep 1
done
if [[ "$ready" != true ]]; then
  docker logs "$container" >&2
  echo "Container did not become ready against its Local test database." >&2
  exit 1
fi

if ! docker exec "$container" sh -c '
  test "$(cat /proc/1/comm)" = dotnet &&
  test "$(id -u)" != 0 &&
  test -r /proc/1/environ &&
  ! tr "\000" "\n" < /proc/1/environ | grep -qE "^(MIGRATION_CONNECTION_STRING|DATABASE_RUNTIME_PASSWORD)="
'; then
  echo "API PID 1 must be non-root dotnet and must not inherit deployment credentials." >&2
  exit 1
fi
echo "Container readiness, non-root PID 1 and deployment-secret isolation passed."
