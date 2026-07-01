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
import datetime as dt
import json
import subprocess
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

    def run_json(args):
        try:
            proc = subprocess.run(args, text=True, capture_output=True, timeout=5)
            if proc.returncode != 0 or not proc.stdout.strip():
                return None
            return json.loads(proc.stdout)
        except Exception:
            return None

    def run_text(args):
        try:
            proc = subprocess.run(args, text=True, capture_output=True, timeout=5)
            if proc.returncode != 0:
                return ""
            return proc.stdout.strip()
        except Exception:
            return ""

    def parse_started_at(value):
        if not value:
            return "", None
        normalized = value.replace("Z", "+00:00")
        try:
            started = dt.datetime.fromisoformat(normalized)
            if started.tzinfo is None:
                started = started.replace(tzinfo=dt.timezone.utc)
            now = dt.datetime.fromisoformat(stamp.replace("Z", "+00:00"))
            return started.astimezone(dt.timezone.utc).isoformat().replace("+00:00", "Z"), max(0, int((now - started).total_seconds()))
        except Exception:
            return value, None

    services = []
    for item in data:
        container_id = item.get("ID") or item.get("Id") or item.get("ContainerID") or item.get("id") or ""
        name = item.get("Name") or item.get("Service") or item.get("name") or "?"
        service = item.get("Service") or item.get("service") or name
        image = item.get("Image") or item.get("image") or ""
        state = item.get("State") or item.get("state") or ""
        status = item.get("Status") or item.get("status") or state
        created = item.get("Created") or item.get("created") or ""
        inspect = run_json(["docker", "inspect", container_id or name])
        inspect0 = inspect[0] if isinstance(inspect, list) and inspect else {}
        inspect_state = inspect0.get("State", {}) if isinstance(inspect0, dict) else {}
        started_at, age_seconds = parse_started_at(inspect_state.get("StartedAt"))
        restart_count = inspect0.get("RestartCount") if isinstance(inspect0, dict) else None
        stats = run_json(["docker", "stats", "--no-stream", "--format", "{{json .}}", container_id or name]) or {}
        cpu = stats.get("CPUPerc") or stats.get("CPUPerc".lower()) or ""
        mem_usage = stats.get("MemUsage") or stats.get("MemUsage".lower()) or ""
        mem = mem_usage.split(" / ", 1)[0] if isinstance(mem_usage, str) else ""
        services.append({
            "id": container_id,
            "name": name,
            "service": service,
            "image": image,
            "state": state,
            "status": status,
            "created": str(created),
            "started_at": started_at,
            "age_seconds": age_seconds,
            "restart_count": restart_count,
            "cpu": cpu,
            "memory": mem,
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
