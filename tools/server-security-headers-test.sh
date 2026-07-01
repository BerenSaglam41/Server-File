#!/usr/bin/env bash
# Gateway security header ve CSP smoke testi.
#
# Kullanım:
#   bash tools/server-security-headers-test.sh
set -euo pipefail

BASE_URL="${BASE_URL:-https://localhost:5090}"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

fail() { printf '[HATA] %s\n' "$*" >&2; exit 1; }
ok() { printf '[OK] %s\n' "$*"; }
log() { printf '[..] %s\n' "$*"; }

command -v curl >/dev/null 2>&1 || fail "curl bulunamadı"

echo "=== Security headers test başlıyor ==="
echo "Base URL: $BASE_URL"

log "Ana sayfa header'ları okunuyor"
status="$(curl -k -sS -D "$WORK_DIR/headers.txt" -o "$WORK_DIR/index.html" -w '%{http_code}' "$BASE_URL/")"
if [ "$status" != "200" ]; then
  cat "$WORK_DIR/index.html" >&2 || true
  fail "Ana sayfa HTTP $status"
fi
ok "Ana sayfa -> HTTP 200"

require_header() {
  local name="$1"
  if ! awk -v n="$name" 'BEGIN{IGNORECASE=1} $0 ~ "^" n ":" {found=1} END{exit found ? 0 : 1}' "$WORK_DIR/headers.txt"; then
    cat "$WORK_DIR/headers.txt" >&2
    fail "$name header yok"
  fi
  ok "$name header var"
}

require_header "Content-Security-Policy"
require_header "X-Content-Type-Options"
require_header "X-Frame-Options"
require_header "Referrer-Policy"
require_header "Permissions-Policy"

if grep -Eiq '^Server:.*nginx/[0-9]' "$WORK_DIR/headers.txt"; then
  cat "$WORK_DIR/headers.txt" >&2
  fail "Server header nginx versiyonu sızdırıyor"
fi
ok "Server header versiyon sızdırmıyor"

log "React asset referansları kontrol ediliyor"
js_path="$(grep -oE 'src="/assets/[^"]+\.js"' "$WORK_DIR/index.html" | head -1 | sed -E 's/src="([^"]+)"/\1/')"
css_path="$(grep -oE 'href="/assets/[^"]+\.css"' "$WORK_DIR/index.html" | head -1 | sed -E 's/href="([^"]+)"/\1/')"
[ -n "$js_path" ] || fail "JS asset bulunamadı"
[ -n "$css_path" ] || fail "CSS asset bulunamadı"

status="$(curl -k -sS -o /dev/null -w '%{http_code}' "$BASE_URL$js_path")"
[ "$status" = "200" ] || fail "JS asset HTTP $status"
ok "JS asset erişilebilir"

status="$(curl -k -sS -o /dev/null -w '%{http_code}' "$BASE_URL$css_path")"
[ "$status" = "200" ] || fail "CSS asset HTTP $status"
ok "CSS asset erişilebilir"

echo "=== Security headers test tamamlandı ==="
