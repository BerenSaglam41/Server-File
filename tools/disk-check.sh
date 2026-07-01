#!/usr/bin/env bash
# Disk doluluk kontrolü — API sunucusu ve Files-01 için.
#
# Eşikler aşılırsa .disk-status dosyasına yazar ve sıfır olmayan çıkış kodu döner
# (systemd servisi "failed" sayar, journalctl'de görünür).
#
# Kullanım:
#   sudo bash tools/disk-check.sh                    # varsayılan eşikler
#   sudo WARN_PCT=50 CRIT_PCT=70 bash tools/disk-check.sh   # test için düşük eşik
#
# Çıkış kodları:  0=temiz  1=uyarı  2=kritik
set -euo pipefail

BACKUP_ROOT="${BACKUP_ROOT:-/backup/platform-files}"
STORAGE_MOUNT="${STORAGE_MOUNT:-/mnt/platform-files}"
WARN_PCT="${WARN_PCT:-80}"
CRIT_PCT="${CRIT_PCT:-90}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
STATUS_FILE="$BACKUP_ROOT/.disk-status"

mkdir -p "$BACKUP_ROOT"

# Bir mount noktasının kullanım yüzdesini döner (sayı, % işareti yok)
disk_pct() {
    df "$1" 2>/dev/null | awk 'NR==2 {gsub(/%/,"",$5); print $5}'
}

API_PCT="$(disk_pct /)"
FILES01_PCT="$(disk_pct "$STORAGE_MOUNT" 2>/dev/null || echo "N/A")"

overall=ok
worst_pct=0
reason=""

check_threshold() {
    local label="$1" pct="$2"
    [ "$pct" = "N/A" ] && return
    if [ "$pct" -ge "$CRIT_PCT" ]; then
        echo "[KRİTİK] $label diski %$pct dolu (eşik: %$CRIT_PCT)"
        overall=critical
        reason="${reason}${label}_above_critical "
        [ "$pct" -gt "$worst_pct" ] && worst_pct="$pct"
    elif [ "$pct" -ge "$WARN_PCT" ]; then
        echo "[UYARI]  $label diski %$pct dolu (eşik: %$WARN_PCT)"
        [ "$overall" != "critical" ] && overall=warning
        reason="${reason}${label}_above_warning "
        [ "$pct" -gt "$worst_pct" ] && worst_pct="$pct"
    else
        echo "[OK]     $label diski %$pct dolu"
    fi
}

check_threshold "api_server" "$API_PCT"
check_threshold "files01"    "$FILES01_PCT"

reason="${reason% }"  # trailing space

printf "status=%s\ntimestamp=%s\napi_server_pct=%s\nfiles01_pct=%s\nreason=%s\n" \
    "$overall" "$STAMP" "$API_PCT" "$FILES01_PCT" "${reason:-none}" \
    > "$STATUS_FILE" 2>/dev/null || true

if [ "$overall" = "critical" ]; then
    echo ""
    echo "EYLEM GEREKLİ: Disk kritik seviyede dolmak üzere!"
    echo "  docker system prune --force   (build cache temizle)"
    echo "  ls -lh /backup/platform-files/ (eski backup kontrol)"
    exit 2
elif [ "$overall" = "warning" ]; then
    echo ""
    echo "Dikkat: Disk doluluk uyarısı. İzlemeye devam."
    exit 1
fi

echo "[OK] Disk doluluk seviyeleri normal."
exit 0
