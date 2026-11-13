#!/bin/bash
set -euo pipefail
umask 077
cd /srv/torvello/app
mkdir -p /srv/torvello/backups
exec 9>/srv/torvello/backups/.backup.lock
flock -n 9 || exit 0
target="/srv/torvello/backups/torvello-$(date -u +%Y%m%dT%H%M%SZ).dump"
trap 'rm -f "$target.partial"' EXIT
docker compose exec -T postgres pg_dump -U postgres -d torvello -Fc > "$target.partial"
chmod 600 "$target.partial"
mv "$target.partial" "$target"
# Retain exactly the newest 14 successful dumps; failed dumps never count.
find /srv/torvello/backups -maxdepth 1 -name 'torvello-*.dump' -printf '%f\n' | sort -r | tail -n +15 | while IFS= read -r old; do
    rm -- "/srv/torvello/backups/$old"
done
