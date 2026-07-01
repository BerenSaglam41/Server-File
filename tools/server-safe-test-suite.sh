#!/usr/bin/env bash
# Production-like ortamda sistemi bozmayan ek doğrulamalar.
#
# Normal deploy smoke test'ten sonra çalıştır:
#   bash tools/server-safe-test-suite.sh
#
# Bu script dosya yüklemez, servis restart etmez, NFS/DB kesmez.
# Ama auth, ops dashboard veri bütünlüğü, correlation id, audit ve eşzamanlı login
# gibi güvenli senaryoları daha derin kontrol eder.
set -euo pipefail

BASE_URL="${BASE_URL:-https://localhost:5090}"
HR_USER="${HR_USER:-hr001}"
HR_PASS="${HR_PASS:-Demo1234!}"
OPS_USER="${OPS_USER:-opsadmin}"
OPS_PASS="${OPS_PASS:-ops123}"
OPS_READ_USER="${OPS_READ_USER:-opsuser01}"
OPS_READ_PASS="${OPS_READ_PASS:-ops456}"
PERSONNEL_ID="${PERSONNEL_ID:-P001}"
CONCURRENT_LOGINS="${CONCURRENT_LOGINS:-20}"
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.yml}"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

HR_COOKIE="$WORK_DIR/hr.cookies"
OPS_COOKIE="$WORK_DIR/ops.cookies"
OPS_READ_COOKIE="$WORK_DIR/ops-read.cookies"

log() { printf '[..] %s\n' "$*"; }
ok() { printf '[OK] %s\n' "$*"; }
fail() { printf '[HATA] %s\n' "$*" >&2; exit 1; }

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "$1 bulunamadı"
}

expect_status() {
  local expected="$1"
  local actual="$2"
  local label="$3"
  local body_file="${4:-}"
  if [ "$actual" != "$expected" ]; then
    printf '\n[HATA] %s beklenen=%s gelen=%s\n' "$label" "$expected" "$actual" >&2
    if [ -n "$body_file" ] && [ -f "$body_file" ]; then
      printf '[HATA] Response body:\n' >&2
      cat "$body_file" >&2 || true
    fi
    exit 1
  fi
  ok "$label -> HTTP $actual"
}

login() {
  local user="$1"
  local pass="$2"
  local cookie="$3"
  local label="$4"
  local body="$WORK_DIR/login-$label.body"
  local status
  status="$(curl -k -sS -o "$body" -w '%{http_code}' \
    -c "$cookie" \
    -H 'Content-Type: application/json' \
    -X POST "$BASE_URL/api/auth/login" \
    --data "{\"username\":\"$user\",\"password\":\"$pass\"}")"
  expect_status "200" "$status" "$label login" "$body"
}

require_command curl
require_command python3
require_command docker

echo "=== Safe test suite başlıyor ==="
echo "Base URL: $BASE_URL"

log "Servis status snapshot yenileniyor"
BACKUP_ROOT="${BACKUP_ROOT:-/backup/platform-files}" COMPOSE_FILE="$COMPOSE_FILE" bash tools/services-status.sh

log "Temel kullanıcılarla login"
login "$HR_USER" "$HR_PASS" "$HR_COOKIE" "hr"
login "$OPS_USER" "$OPS_PASS" "$OPS_COOKIE" "ops"
login "$OPS_READ_USER" "$OPS_READ_PASS" "$OPS_READ_COOKIE" "ops-read"

log "Refresh endpoint kontrolü"
status="$(curl -k -sS -o "$WORK_DIR/hr-refresh.body" -w '%{http_code}' \
  -b "$HR_COOKIE" \
  -c "$HR_COOKIE" \
  -X POST "$BASE_URL/api/auth/refresh")"
expect_status "200" "$status" "HR refresh" "$WORK_DIR/hr-refresh.body"

log "Ops dashboard JSON bütünlüğü kontrol ediliyor"
status="$(curl -k -sS -D "$WORK_DIR/dashboard.headers" -o "$WORK_DIR/dashboard.json" -w '%{http_code}' \
  -b "$OPS_COOKIE" \
  "$BASE_URL/ops/dashboard")"
expect_status "200" "$status" "Ops dashboard" "$WORK_DIR/dashboard.json"

python3 - "$WORK_DIR/dashboard.json" <<'PY'
import json
import sys

path = sys.argv[1]
with open(path, encoding="utf-8") as f:
    data = json.load(f)

required = ["timestamp", "health", "services", "disk", "alerts", "backups", "version"]
missing = [k for k in required if k not in data]
if missing:
    raise SystemExit(f"dashboard missing keys: {missing}")

service_names = {s.get("name") for s in data["health"].get("services", [])}
for expected in ["yonetimapi", "flotaapi", "keycloak", "gateway", "postgres", "fileservice", "opsapi"]:
    if expected not in service_names:
        raise SystemExit(f"health service missing: {expected}")

version = data["version"]
if version.get("commit_hash") in ("", None, "unknown"):
    raise SystemExit("commit_hash unknown")
if version.get("branch") in ("", None, "unknown"):
    raise SystemExit("branch unknown")
if not isinstance(version.get("uptime_seconds"), int):
    raise SystemExit("uptime_seconds missing/not int")

services = data["services"].get("services", [])
if len(services) < 7:
    raise SystemExit(f"service snapshot too small: {len(services)}")
for row in services:
    for key in ["name", "service", "state", "status", "restart_count"]:
        if key not in row:
            raise SystemExit(f"service row missing {key}: {row}")

backups = data["backups"]
if backups.get("count", 0) < 1:
    raise SystemExit("backup count is zero")
if backups.get("retention_limit", 0) < 1:
    raise SystemExit("retention limit invalid")

print("dashboard_json_ok")
PY
ok "Ops dashboard JSON alanları tutarlı"

log "Ops read-only kullanıcı yetkileri kontrol ediliyor"
status="$(curl -k -sS -o "$WORK_DIR/ops-read-me.json" -w '%{http_code}' \
  -b "$OPS_READ_COOKIE" \
  "$BASE_URL/ops/me")"
expect_status "200" "$status" "opsuser01 /ops/me" "$WORK_DIR/ops-read-me.json"

python3 - "$WORK_DIR/ops-read-me.json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as f:
    data = json.load(f)
permissions = data.get("permissions", {})
if permissions.get("read") is not True:
    raise SystemExit("opsuser01 read permission false")
if permissions.get("execute") is not False:
    raise SystemExit("opsuser01 execute permission must be false")
if permissions.get("admin") is not False:
    raise SystemExit("opsuser01 admin permission must be false")
roles = set(data.get("roles", []))
if "ops.read" not in roles or "ops.admin" in roles or "ops.execute" in roles:
    raise SystemExit(f"unexpected opsuser01 roles: {roles}")
print("ops_read_only_ok")
PY
ok "opsuser01 yalnız read yetkisine sahip"

status="$(curl -k -sS -o "$WORK_DIR/ops-read-dashboard.body" -w '%{http_code}' \
  -b "$OPS_READ_COOKIE" \
  "$BASE_URL/ops/dashboard")"
expect_status "200" "$status" "opsuser01 /ops/dashboard" "$WORK_DIR/ops-read-dashboard.body"

log "Correlation header kontrolü"
correlation_id="$(awk 'BEGIN{IGNORECASE=1} /^X-Correlation-Id:/ {gsub(/\r/,"",$2); print $2}' "$WORK_DIR/dashboard.headers" | tail -n 1)"
if [ -z "$correlation_id" ]; then
  fail "X-Correlation-Id header yok"
fi
ok "X-Correlation-Id var: $correlation_id"

log "$PERSONNEL_ID dosyaları içinde en büyük dosya seçiliyor"
status="$(curl -k -sS -o "$WORK_DIR/files.json" -w '%{http_code}' \
  -b "$HR_COOKIE" \
  "$BASE_URL/api/personnel/$PERSONNEL_ID/files")"
expect_status "200" "$status" "$PERSONNEL_ID files" "$WORK_DIR/files.json"

python3 - "$WORK_DIR/files.json" "$WORK_DIR/selected-file.env" <<'PY'
import json
import sys

files_path, env_path = sys.argv[1:3]
with open(files_path, encoding="utf-8") as f:
    files = json.load(f)
if not files:
    print("NO_FILE=1", file=open(env_path, "w", encoding="utf-8"))
    raise SystemExit(0)
best = max(files, key=lambda x: int(x.get("sizeBytes") or 0))
with open(env_path, "w", encoding="utf-8") as out:
    out.write(f"NO_FILE=0\n")
    out.write(f"FILE_ID={best.get('fileId','')}\n")
    out.write(f"EXPECTED_SIZE={int(best.get('sizeBytes') or 0)}\n")
PY
# shellcheck disable=SC1090
. "$WORK_DIR/selected-file.env"

if [ "${NO_FILE:-1}" = "1" ]; then
  printf '[UYARI] %s files boş; büyük download testi skip edildi.\n' "$PERSONNEL_ID"
else
  log "Seçilen dosya download ediliyor: $FILE_ID"
  status="$(curl -k -sS -o "$WORK_DIR/large-download.bin" -w '%{http_code}' \
    -b "$HR_COOKIE" \
    "$BASE_URL/api/personnel/$PERSONNEL_ID/files/$FILE_ID/content")"
  expect_status "200" "$status" "Seçilen dosya download" "$WORK_DIR/large-download.bin"
  actual_size="$(wc -c < "$WORK_DIR/large-download.bin" | tr -d ' ')"
  if [ "$EXPECTED_SIZE" -gt 0 ] && [ "$actual_size" -ne "$EXPECTED_SIZE" ]; then
    fail "Download boyutu beklenenle uyuşmuyor expected=$EXPECTED_SIZE actual=$actual_size"
  fi
  ok "Download boyutu doğrulandı: $actual_size bytes"
fi

log "$CONCURRENT_LOGINS eşzamanlı HR login testi"
for i in $(seq 1 "$CONCURRENT_LOGINS"); do
  (
    curl -k -sS -o "$WORK_DIR/concurrent-$i.body" -w '%{http_code}' \
      -H 'Content-Type: application/json' \
      -X POST "$BASE_URL/api/auth/login" \
      --data "{\"username\":\"$HR_USER\",\"password\":\"$HR_PASS\"}" \
      > "$WORK_DIR/concurrent-$i.status"
  ) &
done
wait

bad=0
for i in $(seq 1 "$CONCURRENT_LOGINS"); do
  code="$(cat "$WORK_DIR/concurrent-$i.status")"
  if [ "$code" != "200" ]; then
    printf '[HATA] concurrent login %s HTTP %s\n' "$i" "$code" >&2
    bad=1
  fi
done
if [ "$bad" -ne 0 ]; then
  fail "Eşzamanlı login testinde hata var"
fi
ok "$CONCURRENT_LOGINS eşzamanlı login başarılı"

log "Ops audit tablosunda son ops kayıtları kontrol ediliyor"
postgres_container="$(docker compose -f "$COMPOSE_FILE" ps -q postgres 2>/dev/null || true)"
if [ -n "$postgres_container" ]; then
  count="$(docker compose -f "$COMPOSE_FILE" exec -T postgres psql -U platform -d platformdb -tAc \
    "select count(*) from ops.audit_events where created_at > now() - interval '10 minutes';" | tr -d '[:space:]')"
  if [ "${count:-0}" -lt 1 ]; then
    fail "Son 10 dakikada ops audit kaydı yok"
  fi
  ok "Ops audit son 10 dakika kayıt sayısı: $count"

  denied_counts="$(docker compose -f "$COMPOSE_FILE" exec -T postgres psql -U platform -d platformdb -tAc \
    "select reason_code || '=' || count(*) from ops.audit_events where created_at > now() - interval '15 minutes' and result='denied' and reason_code in ('no_token','ops_role_missing') group by reason_code order by reason_code;" | tr -d '[:space:]')"
  printf '%s\n' "$denied_counts" > "$WORK_DIR/denied-counts.txt"
  grep -q 'no_token=' "$WORK_DIR/denied-counts.txt" || fail "ops audit içinde no_token denied kaydı yok"
  grep -q 'ops_role_missing=' "$WORK_DIR/denied-counts.txt" || fail "ops audit içinde ops_role_missing denied kaydı yok"
  ok "Ops denied audit kayıtları var: $(tr '\n' ' ' < "$WORK_DIR/denied-counts.txt")"
else
  printf '[UYARI] docker compose postgres bulunamadı; ops audit DB kontrolü skip edildi.\n'
fi

echo "=== Safe test suite tamamlandı ==="
