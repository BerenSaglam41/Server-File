#!/usr/bin/env bash
# Kontrollü servis restart testleri.
#
# Bu script servisleri restart eder. Integration/prod-like test ortamında bilinçli çalıştır.
#
# Kullanım:
#   bash tools/server-resilience-test.sh
set -euo pipefail

BASE_URL="${BASE_URL:-https://localhost:5090}"
OPS_USER="${OPS_USER:-opsadmin}"
OPS_PASS="${OPS_PASS:-ops123}"
HR_USER="${HR_USER:-hr001}"
HR_PASS="${HR_PASS:-Demo1234!}"
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.yml}"
SERVICES="${SERVICES:-opsapi gateway keycloak}"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

OPS_COOKIE="$WORK_DIR/ops.cookies"
HR_COOKIE="$WORK_DIR/hr.cookies"

log() { printf '[..] %s\n' "$*"; }
ok() { printf '[OK] %s\n' "$*"; }
fail() { printf '[HATA] %s\n' "$*" >&2; exit 1; }

expect_status() {
  local expected="$1" actual="$2" label="$3" body_file="${4:-}"
  if [ "$actual" != "$expected" ]; then
    printf '\n[HATA] %s beklenen=%s gelen=%s\n' "$label" "$expected" "$actual" >&2
    [ -n "$body_file" ] && [ -f "$body_file" ] && cat "$body_file" >&2 || true
    exit 1
  fi
  ok "$label -> HTTP $actual"
}

login() {
  local user="$1" pass="$2" cookie="$3" label="$4"
  local status
  status="$(curl -k -sS -o "$WORK_DIR/login-$label.body" -w '%{http_code}' \
    -c "$cookie" \
    -H 'Content-Type: application/json' \
    -X POST "$BASE_URL/api/auth/login" \
    --data "{\"username\":\"$user\",\"password\":\"$pass\"}")"
  expect_status "200" "$status" "$label login" "$WORK_DIR/login-$label.body"
}

wait_for_http() {
  local url="$1" expected="$2" label="$3" attempts="${4:-30}"
  local status="000"
  for _ in $(seq 1 "$attempts"); do
    status="$(curl -k -sS -o "$WORK_DIR/wait.body" -w '%{http_code}' "$url" 2>/dev/null || echo 000)"
    if [ "$status" = "$expected" ]; then
      ok "$label toparlandı -> HTTP $status"
      return 0
    fi
    sleep 2
  done
  cat "$WORK_DIR/wait.body" >&2 2>/dev/null || true
  fail "$label toparlanmadı; son HTTP $status"
}

command -v curl >/dev/null 2>&1 || fail "curl bulunamadı"
command -v docker >/dev/null 2>&1 || fail "docker bulunamadı"

echo "=== Resilience restart test başlıyor ==="
echo "Base URL: $BASE_URL"
echo "Services: $SERVICES"

log "Başlangıç login ve dashboard kontrolü"
login "$OPS_USER" "$OPS_PASS" "$OPS_COOKIE" ops
login "$HR_USER" "$HR_PASS" "$HR_COOKIE" hr
status="$(curl -k -sS -o "$WORK_DIR/dashboard-initial.body" -w '%{http_code}' -b "$OPS_COOKIE" "$BASE_URL/ops/dashboard")"
expect_status "200" "$status" "Başlangıç /ops/dashboard" "$WORK_DIR/dashboard-initial.body"

for service in $SERVICES; do
  log "$service restart ediliyor"
  docker compose -f "$COMPOSE_FILE" restart "$service"

  case "$service" in
    gateway)
      wait_for_http "$BASE_URL/health" 200 "gateway"
      ;;
    opsapi)
      wait_for_http "$BASE_URL/health" 200 "gateway health"
      login "$OPS_USER" "$OPS_PASS" "$OPS_COOKIE" "ops-after-opsapi"
      status="$(curl -k -sS -o "$WORK_DIR/dashboard-opsapi.body" -w '%{http_code}' -b "$OPS_COOKIE" "$BASE_URL/ops/dashboard")"
      expect_status "200" "$status" "opsapi restart sonrası /ops/dashboard" "$WORK_DIR/dashboard-opsapi.body"
      ;;
    keycloak)
      wait_for_http "$BASE_URL/health" 200 "gateway health"
      sleep 5
      login "$OPS_USER" "$OPS_PASS" "$OPS_COOKIE" "ops-after-keycloak"
      login "$HR_USER" "$HR_PASS" "$HR_COOKIE" "hr-after-keycloak"
      ;;
    *)
      wait_for_http "$BASE_URL/health" 200 "gateway health"
      ;;
  esac

  status="$(curl -k -sS -o "$WORK_DIR/personnel-$service.body" -w '%{http_code}' -b "$HR_COOKIE" "$BASE_URL/api/personnel?search=")"
  expect_status "200" "$status" "$service restart sonrası personnel list" "$WORK_DIR/personnel-$service.body"

  status="$(curl -k -sS -o "$WORK_DIR/dashboard-$service.body" -w '%{http_code}' -b "$OPS_COOKIE" "$BASE_URL/ops/dashboard")"
  expect_status "200" "$status" "$service restart sonrası /ops/dashboard" "$WORK_DIR/dashboard-$service.body"
done

log "Servis status snapshot yenileniyor"
BACKUP_ROOT="${BACKUP_ROOT:-/backup/platform-files}" COMPOSE_FILE="$COMPOSE_FILE" bash tools/services-status.sh

echo "=== Resilience restart test tamamlandı ==="
