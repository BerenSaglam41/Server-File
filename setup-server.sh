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
FILES_01_IP="192.168.64.3"
MOUNT_POINT="/mnt/platform-files"
NFS_EXPORT="/srv/files"

if mountpoint -q "$MOUNT_POINT" 2>/dev/null; then
    echo "[--] $MOUNT_POINT zaten mount edilmiş"
else
    echo "[..] NFS mount yapılıyor: $FILES_01_IP:$NFS_EXPORT → $MOUNT_POINT"
    sudo mkdir -p "$MOUNT_POINT"
    sudo mount -t nfs "$FILES_01_IP:$NFS_EXPORT" "$MOUNT_POINT"
    echo "[OK] NFS mount tamam"

    # fstab'a yoksa ekle
    FSTAB_LINE="$FILES_01_IP:$NFS_EXPORT $MOUNT_POINT nfs defaults 0 0"
    if ! grep -qF "$FSTAB_LINE" /etc/fstab; then
        echo "$FSTAB_LINE" | sudo tee -a /etc/fstab > /dev/null
        echo "[OK] fstab'a eklendi (yeniden başlatmada otomatik mount)"
    fi
fi

# 3. Docker container'ları kaldır
echo "[..] Docker container'ları rebuild ediliyor..."
docker compose up --build -d
echo "[OK] Container'lar ayakta"

# 4. Fileservice NFS bağlantısını uygula (NFS container başladıktan sonra mount edildiyse)
echo "[..] Fileservice yeniden başlatılıyor (NFS storage aktif)..."
docker compose restart fileservice
echo "[OK] Fileservice NFS ile çalışıyor"

echo ""
echo "=== Kurulum tamamlandı ==="
echo "Durum: docker compose ps"
