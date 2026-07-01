#!/usr/bin/env bash
# Files-01 storage sunucusunda çalıştırılır.
# Kullanım:
#   sudo NFS_MODE=production API_SERVER_IP=192.168.64.5 bash setup-files01.sh
#   sudo NFS_MODE=test bash setup-files01.sh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

NFS_MODE="${NFS_MODE:-production}"
API_SERVER_IP="${API_SERVER_IP:-}"
FILES_ROOT="${FILES_ROOT:-/srv/files}"

echo "=== Files-01 kurulumu başlıyor ==="
echo "Mode: $NFS_MODE"
echo "Files root: $FILES_ROOT"
[ -n "$API_SERVER_IP" ] && echo "API server IP: $API_SERVER_IP"

if [ "$(id -u)" -ne 0 ]; then
  echo "[HATA] Bu script Files-01 üzerinde sudo/root ile çalıştırılmalı."
  exit 1
fi

if [ "$NFS_MODE" = "production" ] && [ -z "$API_SERVER_IP" ]; then
  echo "[HATA] Production modunda API_SERVER_IP zorunlu."
  echo "Örnek: sudo NFS_MODE=production API_SERVER_IP=192.168.64.5 bash setup-files01.sh"
  exit 1
fi

if ! command -v exportfs >/dev/null 2>&1; then
  echo "[..] NFS server paketi kuruluyor"
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update
    apt-get install -y nfs-kernel-server
  else
    echo "[HATA] exportfs bulunamadı ve apt-get yok. NFS server paketini elle kur."
    exit 1
  fi
fi

if ! command -v ufw >/dev/null 2>&1; then
  echo "[..] ufw kuruluyor"
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update
    apt-get install -y ufw
  else
    echo "[UYARI] ufw bulunamadı; tools/configure-files01-nfs.sh iptables fallback deneyebilir."
  fi
fi

bash tools/configure-files01-nfs.sh

echo ""
echo "=== Files-01 doğrulama komutları ==="
echo "cat /etc/exports"
echo "sudo exportfs -v"
echo ""
echo "API sunucusunda:"
echo "mount | grep platform-files"
echo "nc -vz <FILES_01_IP> 2049"
echo ""
echo "Mac/izinsiz makinede production beklenen:"
echo "sudo mount -t nfs -o resvport <FILES_01_IP>:/srv/files /tmp/files01-test"
echo "# access denied veya timeout"
