#!/usr/bin/env bash
# Backup Files-01 export/manifests plus PostgreSQL catalog dump.
#
# Defaults are safe for local/UTM. Override for production:
#   STORAGE_ROOT=/mnt/platform-files BACKUP_ROOT=/backup/platform-files ./tools/backup-files01.sh
#
# Retention: keeps the last BACKUP_RETAIN backups, deletes older ones.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
STORAGE_ROOT="${STORAGE_ROOT:-${STORAGE_PATH:-/Volumes/platform-files}}"
BACKUP_ROOT="${BACKUP_ROOT:-$ROOT_DIR/backups}"
COMPOSE_FILE="${COMPOSE_FILE:-$ROOT_DIR/docker-compose.yml}"
SKIP_DB_DUMP="${SKIP_DB_DUMP:-0}"
BACKUP_RETAIN="${BACKUP_RETAIN:-14}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
DEST="$BACKUP_ROOT/$STAMP"
STATUS_FILE="$BACKUP_ROOT/.backup-status"

mkdir -p "$BACKUP_ROOT"

# Status dosyasını EXIT trap ile yaz — başarısız olsa bile durum kayıt altına alınır
_backup_result=failed
trap 'printf "status=%s\ntimestamp=%s\nbackup_dir=%s\n" "$_backup_result" "$STAMP" "$DEST" > "$STATUS_FILE"' EXIT

EXPORT_DIR="$STORAGE_ROOT/export"
MANIFESTS_DIR="$STORAGE_ROOT/manifests"

if command -v sha256sum >/dev/null 2>&1; then
  SHA256_CMD="sha256sum"
elif command -v shasum >/dev/null 2>&1; then
  SHA256_CMD="shasum -a 256"
else
  echo "[ERROR] sha256sum veya shasum bulunamadı" >&2
  exit 1
fi

if [ ! -d "$EXPORT_DIR" ]; then
  echo "[ERROR] export directory not found: $EXPORT_DIR" >&2
  exit 1
fi

mkdir -p "$DEST"

echo "[..] Backup target: $DEST"
echo "[..] Copying export/ (staging is intentionally excluded)"
rsync -a --delete "$EXPORT_DIR/" "$DEST/export/"

if [ -d "$MANIFESTS_DIR" ]; then
  echo "[..] Copying manifests/"
  rsync -a --delete "$MANIFESTS_DIR/" "$DEST/manifests/"
else
  mkdir -p "$DEST/manifests"
fi

echo "[..] Writing file manifest"
(
  cd "$DEST/export"
  find . -type f -print0 | sort -z | xargs -0 $SHA256_CMD
) > "$DEST/export.sha256"

if [ "$SKIP_DB_DUMP" = "1" ]; then
  echo "[!!] Skipping PostgreSQL dump because SKIP_DB_DUMP=1"
else
  echo "[..] Dumping PostgreSQL platformdb"
  docker compose -f "$COMPOSE_FILE" exec -T postgres \
    pg_dump -U platform -d platformdb --format=custom --file=/tmp/platformdb.dump
  docker compose -f "$COMPOSE_FILE" cp postgres:/tmp/platformdb.dump "$DEST/platformdb.dump" >/dev/null
  docker compose -f "$COMPOSE_FILE" exec -T postgres rm -f /tmp/platformdb.dump
  test -s "$DEST/platformdb.dump"
fi

cat > "$DEST/backup-info.txt" <<EOF
created_at_utc=$STAMP
storage_root=$STORAGE_ROOT
included=export,manifests,platformdb.dump
excluded=staging
skip_db_dump=$SKIP_DB_DUMP
EOF

echo "[OK] Backup completed: $DEST"

# Retention: keep last BACKUP_RETAIN backups, remove older ones
total="$(find "$BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | wc -l | tr -d ' ')"
to_remove=$(( total - BACKUP_RETAIN ))
if [ "$to_remove" -gt 0 ]; then
  echo "[..] Retention: $total backups found, removing $to_remove oldest (keeping $BACKUP_RETAIN)"
  find "$BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type d | sort | head -n "$to_remove" | while read -r old; do
    echo "[..] Removing old backup: $old"
    rm -rf "$old"
  done
  echo "[OK] Old backups removed"
else
  echo "[--] Retention: $total backups, no cleanup needed (limit $BACKUP_RETAIN)"
fi

_backup_result=success
