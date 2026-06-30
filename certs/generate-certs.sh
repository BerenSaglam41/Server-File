#!/usr/bin/env bash
# Platform servis-ici mTLS sertifikaları
# Kullanım: bash certs/generate-certs.sh
set -euo pipefail

DIR="$(cd "$(dirname "$0")" && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "=== Platform CA ==="
openssl genrsa -out "$DIR/ca.key" 4096
openssl req -new -x509 -key "$DIR/ca.key" \
  -out "$DIR/ca.crt" -days 3650 \
  -subj "/CN=platform-ca/O=Platform/OU=Internal"

sign_server() {
  local name="$1" subj="$2"
  echo "=== $name (server) ==="
  cat > "$TMP/$name.ext" <<EOF
[req]
distinguished_name = dn
[dn]
[SAN]
subjectAltName=DNS:fileservice,DNS:localhost
extendedKeyUsage=serverAuth
EOF
  openssl genrsa -out "$DIR/$name.key" 2048
  openssl req -new -key "$DIR/$name.key" -out "$TMP/$name.csr" -subj "$subj"
  openssl x509 -req -in "$TMP/$name.csr" \
    -CA "$DIR/ca.crt" -CAkey "$DIR/ca.key" -CAcreateserial \
    -out "$DIR/$name.crt" -days 825 \
    -extensions SAN -extfile "$TMP/$name.ext"
  echo "  → $name.crt + $name.key"
}

sign_client() {
  local name="$1" subj="$2"
  echo "=== $name (client) ==="
  cat > "$TMP/$name.ext" <<EOF
[req]
distinguished_name = dn
[dn]
[EXT]
extendedKeyUsage=clientAuth
EOF
  openssl genrsa -out "$DIR/$name.key" 2048
  openssl req -new -key "$DIR/$name.key" -out "$TMP/$name.csr" -subj "$subj"
  openssl x509 -req -in "$TMP/$name.csr" \
    -CA "$DIR/ca.crt" -CAkey "$DIR/ca.key" -CAcreateserial \
    -out "$DIR/$name.crt" -days 825 \
    -extensions EXT -extfile "$TMP/$name.ext"
  echo "  → $name.crt + $name.key"
}

sign_server "fileservice" "/CN=fileservice/O=Platform/OU=Internal"
sign_client "yonetimapi"  "/CN=yonetimapi/O=Platform/OU=Internal"
sign_client "filoapi"     "/CN=filoapi/O=Platform/OU=Internal"

echo ""
echo "=== Doğrulama ==="
openssl verify -CAfile "$DIR/ca.crt" "$DIR/fileservice.crt"
openssl verify -CAfile "$DIR/ca.crt" "$DIR/yonetimapi.crt"
openssl verify -CAfile "$DIR/ca.crt" "$DIR/filoapi.crt"

echo ""
echo "Oluşturulan dosyalar:"
ls -1 "$DIR"/*.crt "$DIR"/*.key
