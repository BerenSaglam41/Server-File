#!/usr/bin/env python3
"""FilesPublisher — Files-01 üzerinde çalışan, mTLS korumalı minimal yazma servisi.

Amaç: FileServiceApi'nin NFS mount'unu salt-okunur (ro) yapabilmesi için, tek
canlı yazma ihtiyacını (dosya yükleme) karşılayan, files01-nfs-model.md'nin
"kontrollü operasyon kullanıcısı" dediği şeyi gerçekleştiren küçük bir servis.

Sadece iki işlem yapar:
  POST   /publish?relativePath=... — body'yi staging'e yazar, SHA256 hesaplar,
                                      export'a atomik taşır, {sha256,sizeBytes} döner.
  DELETE /publish?relativePath=... — export'taki dosyayı siler (rollback, best-effort).

Güvenlik:
  - mTLS zorunlu: istemci sertifikası CA tarafından imzalanmış olmalı ve CN
    ALLOWED_CLIENT_CNS içinde olmalı (varsayılan: sadece "fileservice").
  - relativePath path traversal'a karşı normalize edilip export/staging
    kökünün dışına çıkmadığı doğrulanır.
  - Sadece stdlib kullanır (http.server + ssl) — Files-01'e yeni bir runtime
    (dotnet, pip paketi vb.) kurulmasına gerek kalmaz.
"""
import hashlib
import json
import os
import ssl
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

STAGING_ROOT = os.environ.get("STAGING_ROOT", "/srv/files/staging")
EXPORT_ROOT = os.environ.get("EXPORT_ROOT", "/srv/files/export")
# B3 — public/private zone: public dosyalar TAMAMEN AYRI bir fiziksel kök dizine yazılır
# (savunma derinliği — bir path-traversal hatası bile iki ağacı birbirine karıştıramaz).
EXPORT_ROOT_PUBLIC = os.environ.get("EXPORT_ROOT_PUBLIC", "/srv/files/export-public")
LISTEN_HOST = os.environ.get("LISTEN_HOST", "0.0.0.0")
LISTEN_PORT = int(os.environ.get("LISTEN_PORT", "6060"))
SERVER_CERT = os.environ.get("SERVER_CERT", "/etc/platform-certs/filespublisher.crt")
SERVER_KEY = os.environ.get("SERVER_KEY", "/etc/platform-certs/filespublisher.key")
CA_CERT = os.environ.get("CA_CERT", "/etc/platform-certs/ca.crt")
ALLOWED_CLIENT_CNS = set(
    (os.environ.get("ALLOWED_CLIENT_CNS", "fileservice")).split(",")
)


def resolve_safe_path(root: str, relative_path: str) -> str | None:
    """relative_path'i root altında çözer; kök dışına çıkarsa None döner."""
    if not relative_path or relative_path.startswith("/") or ".." in relative_path.split("/"):
        return None
    root_real = os.path.realpath(root)
    full = os.path.realpath(os.path.join(root_real, relative_path))
    if full != root_real and not full.startswith(root_real + os.sep):
        return None
    return full


def get_peer_cn(ssl_socket) -> str | None:
    cert = ssl_socket.getpeercert()
    if not cert:
        return None
    for field in cert.get("subject", ()):
        for key, value in field:
            if key == "commonName":
                return value
    return None


class PublishHandler(BaseHTTPRequestHandler):
    server_version = "FilesPublisher/1.0"

    def _authorized_cn(self) -> str | None:
        cn = get_peer_cn(self.connection)
        if cn is None or cn not in ALLOWED_CLIENT_CNS:
            return None
        return cn

    def _json_response(self, status: int, payload: dict):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        cn = self._authorized_cn()
        if cn is None:
            self._json_response(403, {"error": "forbidden", "reason": "client_cert_not_allowed"})
            return

        parsed = urlparse(self.path)
        if parsed.path != "/publish":
            self._json_response(404, {"error": "not_found"})
            return

        query = parse_qs(parsed.query)
        relative_path = (query.get("relativePath") or [""])[0]
        zone = (query.get("zone") or ["private"])[0]
        export_root = EXPORT_ROOT_PUBLIC if zone == "public" else EXPORT_ROOT

        staging_full = resolve_safe_path(STAGING_ROOT, relative_path)
        export_full = resolve_safe_path(export_root, relative_path)
        if staging_full is None or export_full is None:
            self._json_response(400, {"error": "invalid_path"})
            return

        if os.path.exists(export_full):
            self._json_response(409, {"error": "already_exists"})
            return

        content_length = int(self.headers.get("Content-Length", "0"))
        if content_length <= 0:
            self._json_response(400, {"error": "empty_body"})
            return

        os.makedirs(os.path.dirname(staging_full), exist_ok=True)
        sha256 = hashlib.sha256()
        bytes_written = 0
        try:
            with open(staging_full, "wb") as f:
                remaining = content_length
                while remaining > 0:
                    chunk = self.rfile.read(min(65536, remaining))
                    if not chunk:
                        break
                    f.write(chunk)
                    sha256.update(chunk)
                    bytes_written += len(chunk)
                    remaining -= len(chunk)

            if bytes_written != content_length:
                raise IOError(f"beklenen {content_length} byte, yazılan {bytes_written} byte")

            os.makedirs(os.path.dirname(export_full), exist_ok=True)
            os.rename(staging_full, export_full)  # aynı dosya sistemi → atomik
        except Exception as exc:
            try:
                if os.path.exists(staging_full):
                    os.remove(staging_full)
            except OSError:
                pass
            self._json_response(503, {"error": "storage_write_failed", "detail": str(exc)})
            return

        self._json_response(200, {
            "sha256": sha256.hexdigest(),
            "sizeBytes": bytes_written,
            "relativePath": relative_path,
        })

    def do_DELETE(self):
        cn = self._authorized_cn()
        if cn is None:
            self._json_response(403, {"error": "forbidden", "reason": "client_cert_not_allowed"})
            return

        parsed = urlparse(self.path)
        if parsed.path != "/publish":
            self._json_response(404, {"error": "not_found"})
            return

        query = parse_qs(parsed.query)
        relative_path = (query.get("relativePath") or [""])[0]
        zone = (query.get("zone") or ["private"])[0]
        export_root = EXPORT_ROOT_PUBLIC if zone == "public" else EXPORT_ROOT
        export_full = resolve_safe_path(export_root, relative_path)
        if export_full is None:
            self._json_response(400, {"error": "invalid_path"})
            return

        try:
            if os.path.exists(export_full):
                os.remove(export_full)
            self._json_response(200, {"deleted": True})
        except OSError as exc:
            self._json_response(500, {"error": "delete_failed", "detail": str(exc)})

    def log_message(self, format, *args):
        sys.stderr.write("%s - %s\n" % (self.address_string(), format % args))


def main():
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(certfile=SERVER_CERT, keyfile=SERVER_KEY)
    context.load_verify_locations(cafile=CA_CERT)
    context.verify_mode = ssl.CERT_REQUIRED  # mTLS zorunlu

    httpd = ThreadingHTTPServer((LISTEN_HOST, LISTEN_PORT), PublishHandler)
    httpd.socket = context.wrap_socket(httpd.socket, server_side=True)
    print(f"[OK] FilesPublisher {LISTEN_HOST}:{LISTEN_PORT} dinliyor "
          f"(izinli client CN: {ALLOWED_CLIENT_CNS})", flush=True)
    httpd.serve_forever()


if __name__ == "__main__":
    main()
