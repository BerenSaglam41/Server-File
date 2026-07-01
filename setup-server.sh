#!/bin/bash
# Linux Docker sunucusunda git pull sonrası çalıştır.
# Kullanım: bash setup-server.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

echo "=== Sunucu kurulumu başlıyor ==="

# 1. .env dosyası
if [ ! -f .env ]; then
    cp .env.linux .env
    echo "[OK] .env oluşturuldu (.env.linux kopyalandı)"
else
    echo "[--] .env zaten var, dokunulmadı"
fi

# 2. NFS mount (files-01)
FILES_01_IP="${FILES_01_IP:-192.168.64.3}"
MOUNT_POINT="${MOUNT_POINT:-/mnt/platform-files}"
NFS_EXPORT="${NFS_EXPORT:-/srv/files}"
NFS_MOUNT_OPTIONS="${NFS_MOUNT_OPTIONS:-defaults,_netdev,nfsvers=4.2,proto=tcp}"
NFS_MODE="${NFS_MODE:-test}" # test | production

if [ "$NFS_MODE" = "production" ]; then
    if ! command -v showmount >/dev/null 2>&1; then
        echo "[HATA] NFS_MODE=production için showmount gerekli. nfs-common paketini kur."
        exit 1
    fi

    EXPORTS_OUTPUT="$(showmount -e "$FILES_01_IP" 2>/dev/null || true)"
    if echo "$EXPORTS_OUTPUT" | grep -E "^[[:space:]]*$NFS_EXPORT[[:space:]]+\\*" >/dev/null; then
        echo "[HATA] Files-01 production için güvenli değil: $NFS_EXPORT hala '*' olarak export ediliyor."
        echo "Önce Files-01 üzerinde çalıştır:"
        echo "  sudo NFS_MODE=production API_SERVER_IP=<BU_API_SUNUCUSU_IP> ./tools/configure-files01-nfs.sh"
        exit 1
    elif echo "$EXPORTS_OUTPUT" | grep -E "^[[:space:]]*$NFS_EXPORT[[:space:]]+" >/dev/null; then
        echo "[OK] NFS production kontrolü geçti ($NFS_EXPORT '*' olarak açık değil)"
    else
        echo "[UYARI] showmount export listesini doğrulayamadı; NFSv4/firewall nedeniyle normal olabilir."
        echo "[UYARI] Mount + probe kontrolüyle devam edilecek."
    fi
elif [ "$NFS_MODE" != "test" ]; then
    echo "[HATA] NFS_MODE test veya production olmalı. Gelen: $NFS_MODE"
    exit 1
fi

if mountpoint -q "$MOUNT_POINT" 2>/dev/null; then
    echo "[--] $MOUNT_POINT zaten mount edilmiş"
else
    echo "[..] NFS mount yapılıyor: $FILES_01_IP:$NFS_EXPORT → $MOUNT_POINT"
    sudo mkdir -p "$MOUNT_POINT"
    sudo mount -t nfs -o "$NFS_MOUNT_OPTIONS" "$FILES_01_IP:$NFS_EXPORT" "$MOUNT_POINT"
    echo "[OK] NFS mount tamam"

    # fstab'a yoksa ekle
    FSTAB_LINE="$FILES_01_IP:$NFS_EXPORT $MOUNT_POINT nfs $NFS_MOUNT_OPTIONS 0 0"
    if ! grep -qF "$FSTAB_LINE" /etc/fstab; then
        echo "$FSTAB_LINE" | sudo tee -a /etc/fstab > /dev/null
        echo "[OK] fstab'a eklendi (yeniden başlatmada otomatik mount)"
    fi
fi

# Mount doğrulama — yanlış klasör mount edilmişse yakala
if [ ! -f "$MOUNT_POINT/export/.probe" ]; then
    echo "[HATA] Probe bulunamadı: $MOUNT_POINT/export/.probe — files-01 mount edilemedi veya /srv/files yapısı eksik"
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

# 4. Docker container'ları kaldır
echo "[..] Docker container'ları rebuild ediliyor..."
docker compose up --build -d
echo "[OK] Container'lar ayakta"

# 5. Fileservice NFS bağlantısını uygula (NFS container başladıktan sonra mount edildiyse)
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
echo "Durum: docker compose ps"
