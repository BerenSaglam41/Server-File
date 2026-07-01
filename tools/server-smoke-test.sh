#!/usr/bin/env bash
# Production-like FileAPI deploy sonrası kısa doğrulama.
# Kullanım:
#   bash tools/server-smoke-test.sh
#
# Opsiyonel env:
#   BASE_URL=https://localhost:5090
#   HR_USER=hr001 HR_PASS='Demo1234!'
#   SELF_USER=p001 SELF_PASS='Demo1234!'
#   OPS_USER=opsadmin OPS_PASS='ops123'
#   PERSONNEL_ID=P001 OTHER_PERSONNEL_ID=P002

set -euo pipefail

BASE_URL="${BASE_URL:-https://localhost:5090}"
HR_USER="${HR_USER:-hr001}"
HR_PASS="${HR_PASS:-Demo1234!}"
SELF_USER="${SELF_USER:-p001}"
SELF_PASS="${SELF_PASS:-Demo1234!}"
OPS_USER="${OPS_USER:-opsadmin}"
OPS_PASS="${OPS_PASS:-ops123}"
PERSONNEL_ID="${PERSONNEL_ID:-P001}"
OTHER_PERSONNEL_ID="${OTHER_PERSONNEL_ID:-P002}"
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.yml}"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

HR_COOKIE="$WORK_DIR/hr.cookies"
SELF_COOKIE="$WORK_DIR/self.cookies"
OPS_COOKIE="$WORK_DIR/ops.cookies"

log() {
  printf '[..] %s\n' "$*"
}

ok() {
  printf '[OK] %s\n' "$*"
}

fail() {
  printf '[HATA] %s\n' "$*" >&2
  exit 1
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

json_count() {
  python3 - "$1" <<'PY'
import json, sys
with open(sys.argv[1], "r", encoding="utf-8") as f:
    data = json.load(f)
print(len(data) if isinstance(data, list) else 0)
PY
}

first_file_id() {
  python3 - "$1" <<'PY'
import json, sys
with open(sys.argv[1], "r", encoding="utf-8") as f:
    data = json.load(f)
if isinstance(data, list) and data:
    print(data[0].get("fileId", ""))
PY
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "$1 bulunamadı"
}

require_command curl
require_command python3
require_command docker

echo "=== Server smoke test başlıyor ==="
echo "Base URL: $BASE_URL"

log "Gateway health kontrol ediliyor"
status="$(curl -k -sS -o "$WORK_DIR/health.body" -w '%{http_code}' "$BASE_URL/health")"
if [ "$status" != "200" ]; then
  cat "$WORK_DIR/health.body" >&2 || true
  fail "Gateway health başarısız: HTTP $status"
fi
ok "Gateway health -> HTTP 200"

log "$HR_USER ile login deneniyor"
status="$(curl -k -sS -o "$WORK_DIR/login.body" -w '%{http_code}' \
  -c "$HR_COOKIE" \
  -H 'Content-Type: application/json' \
  -X POST "$BASE_URL/api/auth/login" \
  --data "{\"username\":\"$HR_USER\",\"password\":\"$HR_PASS\"}")"
expect_status "200" "$status" "HR/Admin login" "$WORK_DIR/login.body"

log "Personel listesi kontrol ediliyor"
status="$(curl -k -sS -o "$WORK_DIR/personnel.body" -w '%{http_code}' \
  -b "$HR_COOKIE" \
  "$BASE_URL/api/personnel?search=")"
expect_status "200" "$status" "Personnel list" "$WORK_DIR/personnel.body"
count="$(json_count "$WORK_DIR/personnel.body")"
if [ "$count" -lt 1 ]; then
  fail "Personnel list boş döndü"
fi
ok "Personnel list kayıt sayısı: $count"

log "$PERSONNEL_ID dosya listesi kontrol ediliyor"
status="$(curl -k -sS -o "$WORK_DIR/files.body" -w '%{http_code}' \
  -b "$HR_COOKIE" \
  "$BASE_URL/api/personnel/$PERSONNEL_ID/files")"
expect_status "200" "$status" "$PERSONNEL_ID files" "$WORK_DIR/files.body"
file_count="$(json_count "$WORK_DIR/files.body")"
ok "$PERSONNEL_ID files kayıt sayısı: $file_count"

file_id="$(first_file_id "$WORK_DIR/files.body")"
if [ -n "$file_id" ]; then
  log "$PERSONNEL_ID ilk dosya download kontrolü: $file_id"
  status="$(curl -k -sS -o "$WORK_DIR/download.bin" -w '%{http_code}' \
    -b "$HR_COOKIE" \
    "$BASE_URL/api/personnel/$PERSONNEL_ID/files/$file_id/content")"
  expect_status "200" "$status" "Download" "$WORK_DIR/download.bin"
  if [ ! -s "$WORK_DIR/download.bin" ]; then
    fail "Download body boş"
  fi
  ok "Download body dolu ($(wc -c < "$WORK_DIR/download.bin" | tr -d ' ') bytes)"
else
  printf '[UYARI] %s files boş; download testi skip edildi.\n' "$PERSONNEL_ID"
fi

log "$SELF_USER ile login deneniyor"
status="$(curl -k -sS -o "$WORK_DIR/self-login.body" -w '%{http_code}' \
  -c "$SELF_COOKIE" \
  -H 'Content-Type: application/json' \
  -X POST "$BASE_URL/api/auth/login" \
  --data "{\"username\":\"$SELF_USER\",\"password\":\"$SELF_PASS\"}")"
expect_status "200" "$status" "Self login" "$WORK_DIR/self-login.body"

log "$SELF_USER -> $OTHER_PERSONNEL_ID erişim reddi kontrol ediliyor"
status="$(curl -k -sS -o "$WORK_DIR/forbidden.body" -w '%{http_code}' \
  -b "$SELF_COOKIE" \
  "$BASE_URL/api/personnel/$OTHER_PERSONNEL_ID/files")"
expect_status "403" "$status" "$SELF_USER başka personele erişemez" "$WORK_DIR/forbidden.body"

log "$OPS_USER ile ops login deneniyor"
status="$(curl -k -sS -o "$WORK_DIR/ops-login.body" -w '%{http_code}' \
  -c "$OPS_COOKIE" \
  -H 'Content-Type: application/json' \
  -X POST "$BASE_URL/api/auth/login" \
  --data "{\"username\":\"$OPS_USER\",\"password\":\"$OPS_PASS\"}")"
expect_status "200" "$status" "Ops login" "$WORK_DIR/ops-login.body"

log "/ops/me no-token erişim reddi kontrol ediliyor"
status="$(curl -k -sS -o "$WORK_DIR/ops-no-token.body" -w '%{http_code}' \
  "$BASE_URL/ops/me")"
expect_status "401" "$status" "/ops/me no-token" "$WORK_DIR/ops-no-token.body"

log "Ops rolü olmayan HR kullanıcının /ops/dashboard erişimi gizleniyor"
status="$(curl -k -sS -o "$WORK_DIR/ops-hr.body" -w '%{http_code}' \
  -b "$HR_COOKIE" \
  "$BASE_URL/ops/dashboard")"
expect_status "404" "$status" "HR ops rolü yok -> 404" "$WORK_DIR/ops-hr.body"

for endpoint in me health services disk backups version dashboard; do
  log "/ops/$endpoint kontrol ediliyor"
  status="$(curl -k -sS -o "$WORK_DIR/ops-$endpoint.body" -w '%{http_code}' \
    -b "$OPS_COOKIE" \
    "$BASE_URL/ops/$endpoint")"
  expect_status "200" "$status" "/ops/$endpoint" "$WORK_DIR/ops-$endpoint.body"
done

log "Ops logout sonrası oturum kapanışı kontrol ediliyor"
status="$(curl -k -sS -o "$WORK_DIR/ops-logout.body" -w '%{http_code}' \
  -b "$OPS_COOKIE" \
  -c "$OPS_COOKIE" \
  -X POST "$BASE_URL/api/auth/logout")"
expect_status "200" "$status" "Ops logout" "$WORK_DIR/ops-logout.body"

status="$(curl -k -sS -o "$WORK_DIR/ops-after-logout.body" -w '%{http_code}' \
  -b "$OPS_COOKIE" \
  "$BASE_URL/ops/me")"
expect_status "401" "$status" "Ops logout sonrası /ops/me" "$WORK_DIR/ops-after-logout.body"

log "Audit son kayıtlar okunuyor"
postgres_container="$(docker compose -f "$COMPOSE_FILE" ps -q postgres 2>/dev/null || true)"
if [ -n "$postgres_container" ]; then
  docker compose -f "$COMPOSE_FILE" exec -T postgres psql -U platform -d platformdb -c "
select created_at, app_code, actor, action, result, reason_code, correlation_id
from files.audit_events
order by created_at desc
limit 10;"
  ok "Audit son kayıtlar listelendi"
else
  printf '[UYARI] docker compose postgres bulunamadı; audit DB kontrolü skip edildi.\n'
fi

echo "=== Server smoke test tamamlandı ==="
