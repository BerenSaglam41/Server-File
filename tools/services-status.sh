#!/usr/bin/env bash
# Docker Compose servis durumunu OpsApi'nin okuyacağı status dosyasına yazar.
#
# OpsApi Docker socket'e erişmez; sadece bu script'in ürettiği JSON dosyasını okur.
#
# Kullanım:
#   bash tools/services-status.sh
#
# Env:
#   BACKUP_ROOT   — status dosyasının yazılacağı kök (default: /backup/platform-files)
#   COMPOSE_FILE  — compose dosyası (default: ./docker-compose.yml)
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
BACKUP_ROOT="${BACKUP_ROOT:-/backup/platform-files}"
COMPOSE_FILE="${COMPOSE_FILE:-$ROOT_DIR/docker-compose.yml}"
STATUS_FILE="$BACKUP_ROOT/.services-status.json"
STAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

mkdir -p "$BACKUP_ROOT"

raw="$(mktemp)"
tmp="$(mktemp)"
trap 'rm -f "$raw" "$tmp"' EXIT

if docker compose -f "$COMPOSE_FILE" ps --format json > "$raw" 2>/tmp/platform-services-status.err; then
  python3 - "$raw" "$tmp" "$STAMP" <<'PY'
import json
import sys

raw_path, out_path, stamp = sys.argv[1:4]
text = open(raw_path, encoding="utf-8").read().strip()

if not text:
    services = []
else:
    try:
        data = json.loads(text)
    except json.JSONDecodeError:
        # Bazı compose sürümleri JSON lines döndürebilir.
        data = [json.loads(line) for line in text.splitlines() if line.strip()]

    if isinstance(data, dict):
        data = [data]

    services = []
    for item in data:
        name = item.get("Name") or item.get("Service") or item.get("name") or "?"
        service = item.get("Service") or item.get("service") or name
        image = item.get("Image") or item.get("image") or ""
        state = item.get("State") or item.get("state") or ""
        status = item.get("Status") or item.get("status") or state
        created = item.get("Created") or item.get("created") or ""
        services.append({
            "name": name,
            "service": service,
            "image": image,
            "state": state,
            "status": status,
            "created": str(created),
        })

payload = {
    "status": "success",
    "timestamp": stamp,
    "count": len(services),
    "services": services,
}
with open(out_path, "w", encoding="utf-8") as f:
    json.dump(payload, f, ensure_ascii=False, indent=2)
    f.write("\n")
PY
  mv "$tmp" "$STATUS_FILE"
  echo "[OK] Services status yazıldı: $STATUS_FILE"
else
  reason="$(cat /tmp/platform-services-status.err 2>/dev/null || true)"
  python3 - "$STATUS_FILE" "$STAMP" "$reason" <<'PY'
import json
import sys

path, stamp, reason = sys.argv[1:4]
payload = {
    "status": "failed",
    "timestamp": stamp,
    "reason": reason[:500],
    "count": 0,
    "services": [],
}
with open(path, "w", encoding="utf-8") as f:
    json.dump(payload, f, ensure_ascii=False, indent=2)
    f.write("\n")
PY
  echo "[HATA] docker compose ps okunamadı: $reason" >&2
  exit 1
fi
