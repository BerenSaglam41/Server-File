#!/usr/bin/env bash
# Canlı sistem restore — belirtilen backup noktasına geri döner.
# Storage (export/) ve PostgreSQL birlikte restore edilir.
# Uyarı: backup'tan sonra eklenen tüm dosya ve DB kayıtları kalıcı olarak gider.
#
# Kullanım:
#   sudo bash tools/restore-live.sh                          # en son backup
#   sudo bash tools/restore-live.sh /backup/platform-files/20260701T085033Z
#   sudo FORCE=1 bash tools/restore-live.sh <backup-dir>    # onay sormadan
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
BACKUP_ROOT="${BACKUP_ROOT:-/backup/platform-files}"
STORAGE_ROOT="${STORAGE_ROOT:-/mnt/platform-files}"
COMPOSE_FILE="${COMPOSE_FILE:-$ROOT_DIR/docker-compose.yml}"
BACKUP_DIR="${1:-}"
FORCE="${FORCE:-0}"

# --- Backup dizinini bul ---
if [ -z "$BACKUP_DIR" ]; then
    BACKUP_DIR="$(find "$BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | sort | tail -1)"
fi

if [ -z "$BACKUP_DIR" ] || [ ! -d "$BACKUP_DIR" ]; then
    echo "[HATA] Backup dizini bulunamadı. Belirtin: $0 <backup-dir>" >&2
    exit 1
fi

for required in export.sha256 platformdb.dump export; do
    if [ ! -e "$BACKUP_DIR/$required" ]; then
        echo "[HATA] Backup eksik: $BACKUP_DIR/$required" >&2
        exit 1
    fi
done

# --- Bilgi göster ---
BACKUP_STAMP="$(basename "$BACKUP_DIR")"
LIVE_FILE_COUNT="$(find "$STORAGE_ROOT/export" -type f 2>/dev/null | wc -l | tr -d ' ')"
BACKUP_FILE_COUNT="$(find "$BACKUP_DIR/export" -type f 2>/dev/null | wc -l | tr -d ' ')"

echo "=============================================="
echo " CANLI SİSTEM RESTORE"
echo "=============================================="
echo " Backup    : $BACKUP_DIR"
echo " Backup stamp : $BACKUP_STAMP"
echo " Canlı dosya  : $LIVE_FILE_COUNT adet"
echo " Backup dosya : $BACKUP_FILE_COUNT adet"
echo ""
echo " DİKKAT: Bu backup'tan sonra eklenen tüm dosya"
echo " ve veritabanı kayıtları KALICI OLARAK SİLİNİR."
echo "=============================================="
echo ""

if [ "$FORCE" != "1" ]; then
    if [ ! -t 0 ]; then
        echo "[HATA] Onay için terminal gerekli. İnteraktif değilse FORCE=1 kullanın." >&2
        exit 1
    fi
    printf "Devam etmek için 'evet' yazın: "
    read -r answer
    if [ "$answer" != "evet" ]; then
        echo "İptal edildi."
        exit 0
    fi
fi

cd "$ROOT_DIR"

# 1. Uygulama containerlarını durdur (postgres + keycloak çalışmaya devam eder)
echo "[..] Uygulama servisleri durduruluyor..."
docker compose -f "$COMPOSE_FILE" stop fileservice yonetimapi flotaapi
echo "[OK] fileservice, yonetimapi, flotaapi durduruldu"

# 2. Storage restore
echo "[..] Storage restore ediliyor..."
echo "     $BACKUP_DIR/export/ → $STORAGE_ROOT/export/"
# --no-o --no-g: NFS all_squash ortamında chown yapılamaz, atla
rsync -rl --delete --no-o --no-g "$BACKUP_DIR/export/" "$STORAGE_ROOT/export/"
echo "[OK] Storage restore tamamlandı"

# 3. PostgreSQL restore
echo "[..] PostgreSQL dump kopyalanıyor..."
docker compose -f "$COMPOSE_FILE" cp "$BACKUP_DIR/platformdb.dump" postgres:/tmp/restore.dump

echo "[..] Aktif bağlantılar kapatılıyor..."
docker compose -f "$COMPOSE_FILE" exec -T postgres psql -U platform -d postgres -c \
    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='platformdb' AND pid <> pg_backend_pid();" \
    > /dev/null 2>&1 || true

echo "[..] pg_restore çalıştırılıyor..."
docker compose -f "$COMPOSE_FILE" exec -T postgres \
    pg_restore -U platform -d platformdb \
    --clean --if-exists --no-privileges --no-owner \
    /tmp/restore.dump
docker compose -f "$COMPOSE_FILE" exec -T postgres rm -f /tmp/restore.dump
echo "[OK] PostgreSQL restore tamamlandı"

# 4. Servisleri yeniden başlat
echo "[..] Servisler yeniden başlatılıyor..."
docker compose -f "$COMPOSE_FILE" start fileservice yonetimapi flotaapi
echo "[OK] Servisler başlatıldı"

# 5. Health check
echo "[..] Health check bekleniyor (5 sn)..."
sleep 5
health="$(curl -sk https://localhost:5090/health 2>/dev/null || echo '{}')"
status="$(printf '%s' "$health" | python3 -c "import json,sys; print(json.load(sys.stdin).get('status','unknown'))" 2>/dev/null || echo "unknown")"
if [ "$status" = "healthy" ]; then
    echo "[OK] Gateway sağlıklı"
else
    echo "[UYARI] Gateway health: $status — servisler hâlâ başlıyor olabilir"
fi

echo ""
echo "=============================================="
echo " RESTORE TAMAMLANDI: $BACKUP_STAMP"
echo " Canlı kontrol: bash tools/server-smoke-test.sh"
echo "=============================================="
