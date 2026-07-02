#!/usr/bin/env bash
# Süresi dolmuş yonetim.download_tickets satırlarını temizler.
#
# Ticket satırları küçük (~200 byte) ve hacim düşük olduğu için kritik değil,
# ama sınırsız birikmesin diye periyodik olarak (systemd timer ile günlük)
# çalıştırılır. Sadece süresi üzerinden RETAIN_DAYS gün geçmiş satırları siler —
# henüz süresi dolmamış veya kısa süre önce dolmuş ticket'lara dokunmaz.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
COMPOSE_FILE="${COMPOSE_FILE:-$ROOT_DIR/docker-compose.yml}"
RETAIN_DAYS="${RETAIN_DAYS:-1}"

RESULT=$(docker compose -f "$COMPOSE_FILE" exec -T postgres \
  psql -U platform -d platformdb -c \
  "DELETE FROM yonetim.download_tickets WHERE expires_at < now() - interval '${RETAIN_DAYS} day';" 2>&1) || {
  echo "[HATA] Ticket cleanup sorgusu basarisiz oldu: $RESULT" >&2
  exit 1
}

# psql "DELETE n" komut etiketini yazdırır — kaç satır silindiğini oradan okuyoruz.
DELETED_COUNT=$(echo "$RESULT" | grep -oE '^DELETE [0-9]+' | grep -oE '[0-9]+' || echo "?")
echo "[OK] $DELETED_COUNT satir silindi (expires_at < now() - ${RETAIN_DAYS} gun)"
