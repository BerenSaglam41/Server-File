#!/usr/bin/env bash
# Restore probe for a Files-01 backup.
#
# This does not restore into live export/. It copies backup export/ into
# restore-tests/<timestamp>/export and verifies SHA256 manifest there.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
STORAGE_ROOT="${STORAGE_ROOT:-${STORAGE_PATH:-/Volumes/platform-files}}"
BACKUP_ROOT="${BACKUP_ROOT:-$ROOT_DIR/backups}"
BACKUP_DIR="${1:-}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"

if command -v sha256sum >/dev/null 2>&1; then
  SHA256_CHECK_CMD="sha256sum -c"
elif command -v shasum >/dev/null 2>&1; then
  SHA256_CHECK_CMD="shasum -a 256 -c"
else
  echo "[ERROR] sha256sum veya shasum bulunamadı" >&2
  exit 1
fi

if [ -z "$BACKUP_DIR" ]; then
  BACKUP_DIR="$(find "$BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | sort | tail -1)"
fi

if [ -z "$BACKUP_DIR" ] || [ ! -d "$BACKUP_DIR/export" ]; then
  echo "[ERROR] backup directory not found or invalid. Usage: $0 <backup-dir>" >&2
  exit 1
fi

if [ ! -f "$BACKUP_DIR/export.sha256" ]; then
  echo "[ERROR] missing manifest: $BACKUP_DIR/export.sha256" >&2
  exit 1
fi

RESTORE_ROOT="$STORAGE_ROOT/restore-tests/$STAMP"
RESTORE_EXPORT="$RESTORE_ROOT/export"

mkdir -p "$RESTORE_EXPORT"

echo "[..] Restore test source: $BACKUP_DIR"
echo "[..] Restore test target: $RESTORE_ROOT"
rsync -a --delete "$BACKUP_DIR/export/" "$RESTORE_EXPORT/"

echo "[..] Verifying SHA256 manifest"
(
  cd "$RESTORE_EXPORT"
  $SHA256_CHECK_CMD "$BACKUP_DIR/export.sha256"
)

cat > "$RESTORE_ROOT/restore-test-info.txt" <<EOF
created_at_utc=$STAMP
backup_dir=$BACKUP_DIR
storage_root=$STORAGE_ROOT
result=success
EOF

echo "[OK] Restore test completed: $RESTORE_ROOT"
