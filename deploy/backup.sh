#!/bin/sh
# Run manually from the deployed project root. No cron/service is installed.
set -eu
cd "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
stamp=$(date -u +%Y%m%dT%H%M%SZ)-$$
container=$(docker compose ps -q library)
[ -n "$container" ] || { printf 'Existing library container is required.\n' >&2; exit 1; }
image=$(docker inspect --format '{{.Image}}' "$container")
owner=$(stat -c '%u:%g' data/library.db)
# Run as the database owner: opening WAL/SHM as an administrator changes ownership.
# Failure is actionable; never repair shared permissions recursively.
docker compose exec -T --user "$owner" library dotnet /app/admin/vndb-admin.dll library snapshot "/data/backups/$stamp" \
    --data-dir /data --image-id "$image" --owner "$owner" --key-file /run/secrets/library_write_key --include-dataset-inputs --json
printf 'Verified snapshot: %s\nOriginal key must be protected separately.\n' "data/backups/$stamp"
