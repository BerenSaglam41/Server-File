#!/usr/bin/env bash
# Install platform-backup and platform-restore-test systemd timer units.
#
# Run on the API server (192.168.64.5) as root or with sudo:
#   sudo bash tools/install-backup-timers.sh
#
# Env overrides:
#   PROJECT_DIR   — repo path on this server (default: parent of this script)
#   STORAGE_ROOT  — NFS mount point (default: /mnt/platform-files)
#   BACKUP_ROOT   — backup destination (default: /backup/platform-files)
#   BACKUP_RETAIN — how many daily backups to keep (default: 14)
#   BACKUP_TIME   — backup timer calendar spec (default: *-*-* 02:00:00 UTC)
#   RESTORE_TIME  — restore-test timer calendar spec (default: Sun *-*-* 03:00:00 UTC)
#
# After install, check status:
#   systemctl list-timers 'platform-*'
#   journalctl -u platform-backup -f
set -euo pipefail

PROJECT_DIR="${PROJECT_DIR:-$(cd "$(dirname "$0")/.." && pwd)}"
STORAGE_ROOT="${STORAGE_ROOT:-/mnt/platform-files}"
BACKUP_ROOT="${BACKUP_ROOT:-/backup/platform-files}"
BACKUP_RETAIN="${BACKUP_RETAIN:-14}"
BACKUP_TIME="${BACKUP_TIME:-*-*-* 02:00:00 UTC}"
RESTORE_TIME="${RESTORE_TIME:-Sun *-*-* 03:00:00 UTC}"
SYSTEMD_DIR="/etc/systemd/system"

if [ "$(id -u)" -ne 0 ]; then
  echo "[HATA] Bu script root olarak çalıştırılmalı (sudo bash tools/install-backup-timers.sh)" >&2
  exit 1
fi

echo "=== Platform backup timer kurulumu ==="
echo "  PROJECT_DIR  : $PROJECT_DIR"
echo "  STORAGE_ROOT : $STORAGE_ROOT"
echo "  BACKUP_ROOT  : $BACKUP_ROOT"
echo "  BACKUP_RETAIN: $BACKUP_RETAIN gün"
echo "  BACKUP_TIME  : $BACKUP_TIME"
echo "  RESTORE_TIME : $RESTORE_TIME"
echo ""

# Gerekli scriptlerin varlığını doğrula
for script in tools/backup-files01.sh tools/restore-test.sh; do
  if [ ! -x "$PROJECT_DIR/$script" ]; then
    echo "[HATA] Script bulunamadı veya çalıştırılabilir değil: $PROJECT_DIR/$script" >&2
    echo "chmod +x $PROJECT_DIR/$script" >&2
    exit 1
  fi
done

mkdir -p "$BACKUP_ROOT"

# --- platform-backup.service ---
# ExecStartPost: backup başarılı olursa restore-test otomatik koşar.
# Herhangi biri başarısız olursa servis failed sayılır ve journalctl'de görünür.
cat > "$SYSTEMD_DIR/platform-backup.service" <<EOF
[Unit]
Description=Platform daily backup — Files-01 export and PostgreSQL dump
After=network.target docker.service
Requires=docker.service

[Service]
Type=oneshot
User=root
Environment=STORAGE_ROOT=$STORAGE_ROOT
Environment=BACKUP_ROOT=$BACKUP_ROOT
Environment=BACKUP_RETAIN=$BACKUP_RETAIN
ExecStart=$PROJECT_DIR/tools/backup-files01.sh
ExecStartPost=$PROJECT_DIR/tools/restore-test.sh
StandardOutput=journal
StandardError=journal
SyslogIdentifier=platform-backup
EOF
echo "[OK] $SYSTEMD_DIR/platform-backup.service yazıldı"

# --- platform-backup.timer ---
cat > "$SYSTEMD_DIR/platform-backup.timer" <<EOF
[Unit]
Description=Platform daily backup timer

[Timer]
OnCalendar=$BACKUP_TIME
RandomizedDelaySec=300
Persistent=true

[Install]
WantedBy=timers.target
EOF
echo "[OK] $SYSTEMD_DIR/platform-backup.timer yazıldı"

# --- platform-restore-test.service ---
cat > "$SYSTEMD_DIR/platform-restore-test.service" <<EOF
[Unit]
Description=Platform weekly restore test — SHA256 hash verification
After=network.target

[Service]
Type=oneshot
User=root
Environment=STORAGE_ROOT=$STORAGE_ROOT
Environment=BACKUP_ROOT=$BACKUP_ROOT
ExecStart=$PROJECT_DIR/tools/restore-test.sh
StandardOutput=journal
StandardError=journal
SyslogIdentifier=platform-restore-test
EOF
echo "[OK] $SYSTEMD_DIR/platform-restore-test.service yazıldı"

# --- platform-restore-test.timer ---
cat > "$SYSTEMD_DIR/platform-restore-test.timer" <<EOF
[Unit]
Description=Platform weekly restore test timer

[Timer]
OnCalendar=$RESTORE_TIME
RandomizedDelaySec=600
Persistent=true

[Install]
WantedBy=timers.target
EOF
echo "[OK] $SYSTEMD_DIR/platform-restore-test.timer yazıldı"

# Reload + enable + start
systemctl daemon-reload

systemctl enable --now platform-backup.timer
echo "[OK] platform-backup.timer aktif"

systemctl enable --now platform-restore-test.timer
echo "[OK] platform-restore-test.timer aktif"

echo ""
echo "=== Kurulum tamamlandı ==="
echo ""
systemctl list-timers 'platform-*' --no-pager
echo ""
echo "Logları izlemek için:"
echo "  journalctl -u platform-backup -f"
echo "  journalctl -u platform-restore-test -f"
echo ""
echo "Manuel test (şimdi çalıştır):"
echo "  systemctl start platform-backup.service"
echo "  journalctl -u platform-backup --no-pager -n 40"
echo "  systemctl start platform-restore-test.service"
echo "  journalctl -u platform-restore-test --no-pager -n 40"
