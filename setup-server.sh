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
NFS_MODE="${NFS_MODE:-production}" # production | test
COMPOSE_ARGS="${COMPOSE_ARGS:-}"
SHOWMOUNT_CHECK="${SHOWMOUNT_CHECK:-0}" # 1 yaparsan showmount ile '*' export kontrolü denenir.

if [ -z "$COMPOSE_ARGS" ]; then
    if [ "$NFS_MODE" = "production" ]; then
        COMPOSE_ARGS="-f docker-compose.yml"
    else
        COMPOSE_ARGS=""
    fi
fi

if [ "$NFS_MODE" = "production" ]; then
    if command -v nc >/dev/null 2>&1; then
        if ! nc -z -w 3 "$FILES_01_IP" 2049; then
            echo "[HATA] Files-01 NFS portuna erişilemiyor: $FILES_01_IP:2049"
            exit 1
        fi
        echo "[OK] Files-01 NFS portu erişilebilir ($FILES_01_IP:2049)"
    fi

    if [ "$SHOWMOUNT_CHECK" = "1" ] && command -v timeout >/dev/null 2>&1 && command -v showmount >/dev/null 2>&1; then
        EXPORTS_OUTPUT="$(timeout --kill-after=1s 3s showmount -e "$FILES_01_IP" 2>/dev/null || true)"
    else
        echo "[--] showmount kontrolü atlandı; NFSv4/firewall ortamında mount + probe kontrolü kullanılacak."
        EXPORTS_OUTPUT=""
    fi

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

# 3. mTLS/Gateway sertifikaları — .key dosyaları gitignore'da; yoksa veya eşleşmiyorsa üret
CERTS_NEED_REGEN=0
CERT_BACKUP_DIR="certs/backup-mismatch-$(date +%Y%m%d%H%M%S)"

backup_cert_file() {
    local path="$1"
    mkdir -p "$CERT_BACKUP_DIR"
    [ -e "$path" ] && mv "$path" "$CERT_BACKUP_DIR/$(basename "$path")" || true
}

backup_cert_pair() {
    local name="$1"
    backup_cert_file "certs/$name.crt"
    backup_cert_file "certs/$name.key"
    echo "[..] certs/$name.crt/key yedeğe alındı: $CERT_BACKUP_DIR"
}

backup_all_certs() {
    for name in ca fileservice gateway yonetimapi filoapi; do
        backup_cert_file "certs/$name.crt"
        backup_cert_file "certs/$name.key"
    done
    backup_cert_file "certs/ca.srl"
    echo "[..] Tüm sertifika zinciri yedeğe alındı: $CERT_BACKUP_DIR"
}

cert_key_match() {
    local name="$1"
    local cert="certs/$name.crt"
    local key="certs/$name.key"

    [ -f "$cert" ] && [ -f "$key" ] || return 1

    local cert_mod key_mod
    cert_mod="$(openssl x509 -noout -modulus -in "$cert" 2>/dev/null | openssl sha256 2>/dev/null || true)"
    key_mod="$(openssl rsa -noout -modulus -in "$key" 2>/dev/null | openssl sha256 2>/dev/null || true)"

    [ -n "$cert_mod" ] && [ "$cert_mod" = "$key_mod" ]
}

if [ ! -f "certs/ca.crt" ] || [ ! -f "certs/ca.key" ]; then
    echo "[..] certs/ca.crt/key eksik, sertifika üretimi gerekli"
    CERTS_NEED_REGEN=1
elif ! cert_key_match "ca"; then
    echo "[UYARI] certs/ca.crt ve certs/ca.key eşleşmiyor; tüm sertifika zinciri yeniden üretilecek"
    backup_all_certs
    CERTS_NEED_REGEN=1
fi

for CERT_NAME in fileservice gateway yonetimapi filoapi; do
    if [ ! -f "certs/$CERT_NAME.crt" ] || [ ! -f "certs/$CERT_NAME.key" ]; then
        echo "[..] certs/$CERT_NAME.crt/key eksik, sertifika üretimi gerekli"
        CERTS_NEED_REGEN=1
    elif ! cert_key_match "$CERT_NAME"; then
        echo "[UYARI] certs/$CERT_NAME.crt ve certs/$CERT_NAME.key eşleşmiyor"
        backup_cert_pair "$CERT_NAME"
        CERTS_NEED_REGEN=1
    fi
done

if [ "$CERTS_NEED_REGEN" = "1" ]; then
    echo "[..] Sertifikalar eksik/bozuk, yeniden üretiliyor..."
    bash certs/generate-certs.sh
fi
echo "[OK] Sertifikalar hazır"

# 4. Docker container'ları kaldır
echo "[..] Docker container'ları rebuild ediliyor..."
docker compose $COMPOSE_ARGS up --build -d
echo "[OK] Container'lar ayakta"

# 5. Fileservice NFS bağlantısını uygula (NFS container başladıktan sonra mount edildiyse)
echo "[..] Fileservice yeniden başlatılıyor (NFS storage aktif)..."
docker compose $COMPOSE_ARGS restart fileservice
echo "[OK] Fileservice NFS ile çalışıyor"

# 6. DB schema + seed (tablolar yoksa)
echo "[..] Veritabanı schema kontrol ediliyor..."
PG=$(docker compose $COMPOSE_ARGS ps -q postgres)
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
