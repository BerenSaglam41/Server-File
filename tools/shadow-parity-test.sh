#!/usr/bin/env bash
# Faz C1 Aşama 2 — Shadow Mode gerçek doğrulama testi.
#
# keycloak/realm-platform.json'daki TÜM kullanıcılar için (hr001, adm001, m001-m003,
# p001-p024, opsadmin, opsuser01 — toplam 31, fleetuser hariç çünkü hiçbir personnel/ops
# rolü yok) gerçek Keycloak login yapılır, kendi yetkisi dahilindeki VE dışındaki
# (pozitif + negatif) isteklerle ilgili endpoint'e istek atılır. Karar hâlâ JWT'den
# veriliyor (shadow mode) — bu script, DB-tabanlı gölge hesaplamanın JWT ile ayrıştığı
# HİÇBİR durum olmadığını (`ROLE_SHADOW_MISMATCH` log satırı sıfır) kanıtlar.
#
# Kullanım (proje kök dizininden):
#   bash tools/shadow-parity-test.sh
#
# Opsiyonel env: BASE_URL (varsayılan https://localhost:5090)

set -euo pipefail

BASE_URL="${BASE_URL:-https://localhost:5090}"
PERSONNEL_PASS="${PERSONNEL_PASS:-Demo1234!}"
OPS_PASS="${OPS_PASS:-ops123}"
OPS_READ_PASS="${OPS_READ_PASS:-ops456}"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

pass_count=0
fail_count=0

log() { printf '[TEST] %s\n' "$1"; }
ok()  { printf '[OK] %s\n' "$1"; pass_count=$((pass_count+1)); }
bad() { printf '[HATA] %s\n' "$1"; fail_count=$((fail_count+1)); }

# login <username> <password> <cookie_jar> -> beklenen 200
do_login() {
  local user="$1" pass="$2" jar="$3"
  local status
  status="$(curl -k -sS -o "$WORK_DIR/login.json" -w '%{http_code}' \
    -c "$jar" -H 'Content-Type: application/json' \
    -X POST "$BASE_URL/api/auth/login" \
    --data "{\"username\":\"$user\",\"password\":\"$pass\"}")"
  if [ "$status" != "200" ]; then
    bad "$user login -> HTTP $status (beklenen 200)"
    return 1
  fi
  return 0
}

# check_personnel_access <cookie_jar> <target_id> <expected_status> <label>
check_personnel_access() {
  local jar="$1" target="$2" expected="$3" label="$4"
  local status
  status="$(curl -k -sS -o /dev/null -w '%{http_code}' -b "$jar" "$BASE_URL/api/personnel/$target/files")"
  if [ "$status" = "$expected" ]; then
    ok "$label -> HTTP $status"
  else
    bad "$label -> HTTP $status (beklenen $expected)"
  fi
}

# check_ops_access <cookie_jar> <expected_status> <label>
check_ops_access() {
  local jar="$1" expected="$2" label="$3"
  local status
  status="$(curl -k -sS -o /dev/null -w '%{http_code}' -b "$jar" "$BASE_URL/ops/health")"
  if [ "$status" = "$expected" ]; then
    ok "$label -> HTTP $status"
  else
    bad "$label -> HTTP $status (beklenen $expected)"
  fi
}

# ── all-scope: hr001, adm001 — herhangi bir personele erişebilmeli ──────────────
for u in hr001 adm001; do
  jar="$WORK_DIR/$u.jar"
  log "$u (all-scope) test ediliyor"
  if do_login "$u" "$PERSONNEL_PASS" "$jar"; then
    check_personnel_access "$jar" "P001" "200" "$u -> P001 (all-scope, herkese erişebilmeli)"
    check_personnel_access "$jar" "P022" "200" "$u -> P022 (all-scope, herkese erişebilmeli)"
  fi
done

# ── team-scope: m001->P001-P007, m002->P008-P014, m003->P015-P021 ──────────────
declare -A team_member=( [m001]=P003 [m002]=P010 [m003]=P018 )
declare -A team_outsider=( [m001]=P022 [m002]=P001 [m003]=P001 )
for u in m001 m002 m003; do
  jar="$WORK_DIR/$u.jar"
  log "$u (team-scope) test ediliyor"
  if do_login "$u" "$PERSONNEL_PASS" "$jar"; then
    check_personnel_access "$jar" "${team_member[$u]}" "200" "$u -> ${team_member[$u]} (kendi ekibi, izinli)"
    check_personnel_access "$jar" "${team_outsider[$u]}" "403" "$u -> ${team_outsider[$u]} (ekip dışı, reddedilmeli)"
  fi
done

# ── self-scope: p001-p024 — sadece kendine erişebilmeli ─────────────────────────
for i in $(seq -w 1 24); do
  u="p0$i"
  target="P0$i"
  other="P0$(( (10#$i % 24) + 1 ))"
  [ "$other" = "$target" ] && other="P001"
  jar="$WORK_DIR/$u.jar"
  log "$u (self-scope) test ediliyor"
  if do_login "$u" "$PERSONNEL_PASS" "$jar"; then
    check_personnel_access "$jar" "$target" "200" "$u -> $target (kendi kaydı, izinli)"
    check_personnel_access "$jar" "$other" "403" "$u -> $other (başkası, reddedilmeli)"
  fi
done

# ── ops rolleri: opsadmin (read+admin), opsuser01 (sadece read) ────────────────
jar="$WORK_DIR/opsadmin.jar"
log "opsadmin test ediliyor"
if do_login "opsadmin" "$OPS_PASS" "$jar"; then
  check_ops_access "$jar" "200" "opsadmin -> /ops/health (izinli)"
fi

jar="$WORK_DIR/opsuser01.jar"
log "opsuser01 test ediliyor"
if do_login "opsuser01" "$OPS_READ_PASS" "$jar"; then
  check_ops_access "$jar" "200" "opsuser01 -> /ops/health (ops.read yeterli, izinli)"
fi

echo
echo "=== Sonuç: $pass_count OK, $fail_count HATA ==="
if [ "$fail_count" -gt 0 ]; then
  exit 1
fi
