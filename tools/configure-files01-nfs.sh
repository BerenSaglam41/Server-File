#!/usr/bin/env bash
# Configure Files-01 NFS export for test or minimum-production mode.
#
# Files-01 üzerinde çalıştır:
#   sudo NFS_MODE=production API_SERVER_IP=192.168.64.5 ./tools/configure-files01-nfs.sh
#
# Test/UTM kolaylığı için:
#   sudo NFS_MODE=test ./tools/configure-files01-nfs.sh
set -euo pipefail

NFS_MODE="${NFS_MODE:-production}"
FILES_ROOT="${FILES_ROOT:-/srv/files}"
API_SERVER_IP="${API_SERVER_IP:-}"
FIREWALL_BACKEND="${FIREWALL_BACKEND:-auto}" # auto | ufw | iptables | none

if [ "$(id -u)" -ne 0 ]; then
  echo "[ERROR] Bu script Files-01 üzerinde root/sudo ile çalıştırılmalıdır." >&2
  exit 1
fi

case "$NFS_MODE" in
  production|test) ;;
  *)
    echo "[ERROR] NFS_MODE production veya test olmalı. Gelen: $NFS_MODE" >&2
    exit 1
    ;;
esac

if [ "$NFS_MODE" = "production" ] && [ -z "$API_SERVER_IP" ]; then
  echo "[ERROR] Production modunda API_SERVER_IP zorunludur." >&2
  echo "Örnek: sudo NFS_MODE=production API_SERVER_IP=192.168.64.5 $0" >&2
  exit 1
fi

echo "=== Files-01 NFS yapılandırması ==="
echo "Mode: $NFS_MODE"
echo "Files root: $FILES_ROOT"
[ -n "$API_SERVER_IP" ] && echo "API server IP: $API_SERVER_IP"

echo "[..] Dizin yapısı hazırlanıyor"
mkdir -p "$FILES_ROOT/export/personnel" \
         "$FILES_ROOT/export/fleet" \
         "$FILES_ROOT/staging/personnel" \
         "$FILES_ROOT/staging/fleet" \
         "$FILES_ROOT/manifests/personnel" \
         "$FILES_ROOT/restore-tests/personnel"

if [ ! -f "$FILES_ROOT/export/.probe" ]; then
  echo "probe" > "$FILES_ROOT/export/.probe"
fi

timestamp="$(date +%Y%m%d%H%M%S)"
if [ -f /etc/exports ]; then
  cp /etc/exports "/etc/exports.bak.$timestamp"
  echo "[OK] /etc/exports yedeği: /etc/exports.bak.$timestamp"
fi

if [ "$NFS_MODE" = "production" ]; then
  export_line="$FILES_ROOT ${API_SERVER_IP}(rw,sync,no_subtree_check,root_squash)"
else
  export_line="$FILES_ROOT *(rw,sync,no_subtree_check)"
fi

echo "[..] /etc/exports yazılıyor"
printf '%s\n' "$export_line" > /etc/exports
exportfs -ra
systemctl enable --now nfs-server >/dev/null 2>&1 || systemctl enable --now nfs-kernel-server >/dev/null 2>&1 || true

configure_ufw() {
  ufw allow OpenSSH >/dev/null 2>&1 || true
  if [ "$NFS_MODE" = "production" ]; then
    ufw delete allow 2049/tcp >/dev/null 2>&1 || true
    ufw allow from "$API_SERVER_IP" to any port 2049 proto tcp
  else
    ufw allow 2049/tcp
  fi
  ufw --force enable
  ufw status verbose
}

configure_iptables() {
  iptables -C INPUT -p tcp --dport 2049 -j DROP 2>/dev/null && iptables -D INPUT -p tcp --dport 2049 -j DROP || true
  while iptables -C INPUT -p tcp --dport 2049 -j ACCEPT 2>/dev/null; do
    iptables -D INPUT -p tcp --dport 2049 -j ACCEPT
  done
  if [ "$NFS_MODE" = "production" ]; then
    iptables -C INPUT -p tcp -s "$API_SERVER_IP" --dport 2049 -j ACCEPT 2>/dev/null || \
      iptables -A INPUT -p tcp -s "$API_SERVER_IP" --dport 2049 -j ACCEPT
    iptables -A INPUT -p tcp --dport 2049 -j DROP
  else
    iptables -C INPUT -p tcp --dport 2049 -j ACCEPT 2>/dev/null || \
      iptables -A INPUT -p tcp --dport 2049 -j ACCEPT
  fi
  iptables -S INPUT | grep -- '--dport 2049' || true
  echo "[UYARI] iptables kuralları reboot sonrası kalıcı olmayabilir; dağıtıma göre iptables-persistent/nftables ile kalıcılaştır."
}

echo "[..] Firewall yapılandırılıyor ($FIREWALL_BACKEND)"
case "$FIREWALL_BACKEND" in
  auto)
    if command -v ufw >/dev/null 2>&1; then
      configure_ufw
    elif command -v iptables >/dev/null 2>&1; then
      configure_iptables
    else
      echo "[UYARI] ufw/iptables bulunamadı; firewall elle yapılandırılmalı." >&2
    fi
    ;;
  ufw) configure_ufw ;;
  iptables) configure_iptables ;;
  none) echo "[--] Firewall yapılandırması atlandı." ;;
  *)
    echo "[ERROR] FIREWALL_BACKEND auto, ufw, iptables veya none olmalı." >&2
    exit 1
    ;;
esac

echo ""
echo "=== Doğrulama ==="
echo "[1] cat /etc/exports"
cat /etc/exports
echo ""
echo "[2] exportfs -v"
exportfs -v
echo ""
echo "[OK] Files-01 NFS yapılandırması tamamlandı."
