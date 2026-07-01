#!/usr/bin/env bash
# Ops alert kaynaklarını gerçek backup verisini bozmadan simüle eder.
#
# Status dosyalarını geçici değiştirir, /ops/alerts üzerinden doğrular ve çıkarken eski
# dosyaları geri yükler.
#
# Kullanım:
#   bash tools/server-alert-simulation-test.sh
set -euo pipefail

BASE_URL="${BASE_URL:-https://localhost:5090}"
OPS_USER="${OPS_USER:-opsadmin}"
OPS_PASS="${OPS_PASS:-ops123}"
BACKUP_ROOT="${BACKUP_ROOT:-/backup/platform-files}"
STORAGE_MOUNT="${STORAGE_MOUNT:-/mnt/platform-files}"

WORK_DIR="$(mktemp -d)"
trap 'restore_status; rm -rf "$WORK_DIR"' EXIT

OPS_COOKIE="$WORK_DIR/ops.cookies"
DISK_STATUS="$BACKUP_ROOT/.disk-status"
BACKUP_STATUS="$BACKUP_ROOT/.backup-status"

log() { printf '[..] %s\n' "$*"; }
ok() { printf '[OK] %s\n' "$*"; }
fail() { printf '[HATA] %s\n' "$*" >&2; exit 1; }

restore_status() {
  for name in disk backup; do
    local src="$WORK_DIR/$name-status.bak"
    local target
    if [ "$name" = "disk" ]; then target="$DISK_STATUS"; else target="$BACKUP_STATUS"; fi
    if [ -f "$src" ]; then
      cp "$src" "$target" 2>/dev/null || true
    elif [ -f "$WORK_DIR/$name-status.was-missing" ]; then
      rm -f "$target" 2>/dev/null || true
    fi
  done
}

backup_status_file() {
  local path="$1"
  local name="$2"
  mkdir -p "$BACKUP_ROOT"
  if [ -f "$path" ]; then
    cp "$path" "$WORK_DIR/$name-status.bak"
  else
    : > "$WORK_DIR/$name-status.was-missing"
  fi
}

expect_status() {
  local expected="$1" actual="$2" label="$3" body_file="${4:-}"
  if [ "$actual" != "$expected" ]; then
    printf '\n[HATA] %s beklenen=%s gelen=%s\n' "$label" "$expected" "$actual" >&2
    [ -n "$body_file" ] && [ -f "$body_file" ] && cat "$body_file" >&2 || true
    exit 1
  fi
  ok "$label -> HTTP $actual"
}

command -v curl >/dev/null 2>&1 || fail "curl bulunamadı"
command -v python3 >/dev/null 2>&1 || fail "python3 bulunamadı"

echo "=== Alert simulation test başlıyor ==="
echo "Base URL: $BASE_URL"

log "$OPS_USER ile login"
status="$(curl -k -sS -o "$WORK_DIR/login.body" -w '%{http_code}' \
  -c "$OPS_COOKIE" \
  -H 'Content-Type: application/json' \
  -X POST "$BASE_URL/api/auth/login" \
  --data "{\"username\":\"$OPS_USER\",\"password\":\"$OPS_PASS\"}")"
expect_status "200" "$status" "Ops login" "$WORK_DIR/login.body"

backup_status_file "$DISK_STATUS" disk
backup_status_file "$BACKUP_STATUS" backup

log "Disk warning simülasyonu: WARN_PCT=1 CRIT_PCT=99"
set +e
BACKUP_ROOT="$BACKUP_ROOT" STORAGE_MOUNT="$STORAGE_MOUNT" WARN_PCT=1 CRIT_PCT=99 bash tools/disk-check.sh > "$WORK_DIR/disk-check.out" 2>&1
disk_code="$?"
set -e
if [ "$disk_code" -ne 1 ]; then
  cat "$WORK_DIR/disk-check.out" >&2
  fail "disk-check warning exit code 1 bekleniyordu, gelen=$disk_code"
fi

status="$(curl -k -sS -o "$WORK_DIR/alerts-disk.json" -w '%{http_code}' \
  -b "$OPS_COOKIE" \
  "$BASE_URL/ops/alerts")"
expect_status "200" "$status" "/ops/alerts disk warning" "$WORK_DIR/alerts-disk.json"

python3 - "$WORK_DIR/alerts-disk.json" <<'PY'
import json, sys
with open(sys.argv[1], encoding="utf-8") as f:
    data = json.load(f)
alerts = data.get("alerts", [])
if not any(a.get("source") == "disk" and a.get("severity") == "warning" for a in alerts):
    raise SystemExit(f"disk warning alert missing: {alerts}")
print("disk_warning_alert_ok")
PY
ok "Disk warning alert görünüyor"

log "Backup failed alert simülasyonu"
cat > "$BACKUP_STATUS" <<EOF
status=failed
timestamp=$(date -u +%Y%m%dT%H%M%SZ)
backup_dir=/tmp/simulated-broken-backup
reason=simulated_backup_failure
EOF

status="$(curl -k -sS -o "$WORK_DIR/alerts-backup.json" -w '%{http_code}' \
  -b "$OPS_COOKIE" \
  "$BASE_URL/ops/alerts")"
expect_status "200" "$status" "/ops/alerts backup failed" "$WORK_DIR/alerts-backup.json"

python3 - "$WORK_DIR/alerts-backup.json" <<'PY'
import json, sys
with open(sys.argv[1], encoding="utf-8") as f:
    data = json.load(f)
alerts = data.get("alerts", [])
if not any(a.get("source") == "backup" and a.get("severity") == "critical" for a in alerts):
    raise SystemExit(f"backup failed alert missing: {alerts}")
print("backup_failed_alert_ok")
PY
ok "Backup failed alert görünüyor"

echo "=== Alert simulation test tamamlandı; status dosyaları geri yüklendi ==="
