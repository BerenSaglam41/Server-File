#!/bin/bash
# Mac'te git pull sonrası çalıştır.
# Kullanım: bash setup-mac.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

echo "=== Mac kurulumu başlıyor ==="

# 1. .env dosyası
if [ ! -f .env ]; then
    cp .env.mac .env
    echo "[OK] .env oluşturuldu (.env.mac kopyalandı)"
else
    echo "[--] .env zaten var, dokunulmadı"
fi

# 2. NFS mount (files-01)
FILES_01_IP="${FILES_01_IP:-192.168.64.3}"
MOUNT_POINT="${MOUNT_POINT:-/Volumes/platform-files}"
NFS_EXPORT="${NFS_EXPORT:-/srv/files}"
NFS_MOUNT_OPTIONS="${NFS_MOUNT_OPTIONS:-resvport,nfsvers=4.2,proto=tcp}"

if mount | grep -q "$MOUNT_POINT"; then
    echo "[--] $MOUNT_POINT zaten mount edilmiş"
else
    echo "[..] NFS mount yapılıyor: $FILES_01_IP:$NFS_EXPORT → $MOUNT_POINT"
    sudo mkdir -p "$MOUNT_POINT"
    sudo mount -t nfs -o "$NFS_MOUNT_OPTIONS" "$FILES_01_IP:$NFS_EXPORT" "$MOUNT_POINT"
    echo "[OK] NFS mount tamam"
fi

# probe dosyası kontrol
if [ ! -f "$MOUNT_POINT/export/.probe" ]; then
    echo "[!!] UYARI: $MOUNT_POINT/export/.probe bulunamadı — files-01 düzgün mount edilmedi"
    exit 1
fi
echo "[OK] Storage erişilebilir (probe dosyası var)"

# 3. mTLS sertifikaları — .key dosyaları gitignore'da; yoksa veya klasörse üret
for KEY in certs/fileservice.key certs/yonetimapi.key certs/filoapi.key; do
    if [ ! -f "$KEY" ]; then
        echo "[..] Sertifikalar eksik/bozuk, yeniden üretiliyor..."
        bash certs/generate-certs.sh
        break
    fi
done
echo "[OK] Sertifikalar hazır"

# 4. Docker container'ları rebuild
echo "[..] Docker container'ları rebuild ediliyor..."
docker compose up --build -d
echo "[OK] Container'lar ayakta"

# 5. Fileservice NFS bağlantısını uygula
echo "[..] Fileservice yeniden başlatılıyor (NFS storage aktif)..."
docker compose restart fileservice
echo "[OK] Fileservice NFS ile çalışıyor"

# 6. DB schema + seed (tablolar yoksa)
echo "[..] Veritabanı schema kontrol ediliyor..."
PG=$(docker compose ps -q postgres)
TABLE_COUNT=$(docker exec "$PG" psql -U platform -d platformdb -tAc \
    "SELECT COUNT(*) FROM pg_tables WHERE schemaname='yonetim';" 2>/dev/null || echo "0")
if [ "$TABLE_COUNT" = "0" ]; then
    echo "[..] Schema oluşturuluyor..."
    docker exec -i "$PG" psql -U platform -d platformdb < db/docker-init/01-schema.sql
    docker exec -i "$PG" psql -U platform -d platformdb < db/docker-init/02-seed.sql
    echo "[OK] DB schema + seed tamamlandı"
else
    echo "[--] DB tablolar zaten var"
fi

echo ""
echo "=== Kurulum tamamlandı ==="
echo "Tarayıcı: http://localhost:5090"
echo ""
echo "NOT: Mac yeniden başlatılırsa NFS mount kaybolur."
echo "     Yeniden mount için: sudo mount -t nfs -o $NFS_MOUNT_OPTIONS $FILES_01_IP:$NFS_EXPORT $MOUNT_POINT"
echo "     Ardından: docker compose restart fileservice"
