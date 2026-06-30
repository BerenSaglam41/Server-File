#!/usr/bin/env bash
# Platform servis-ici mTLS sertifikaları.
#
# Varsayılan davranış mevcut CA ve sertifikaları ezmez. Yeniden üretmek için:
#   FORCE_REGENERATE_CERTS=1 bash certs/generate-certs.sh
#
# Gateway SAN değerleri ortama göre verilebilir:
#   GATEWAY_DNS="gateway,localhost,platform.example.com" \
#   GATEWAY_IPS="127.0.0.1,192.168.64.5" \
#   bash certs/generate-certs.sh
set -euo pipefail

DIR="$(cd "$(dirname "$0")" && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

FORCE_REGENERATE_CERTS="${FORCE_REGENERATE_CERTS:-0}"
CA_DAYS="${CA_DAYS:-3650}"
CERT_DAYS="${CERT_DAYS:-825}"
FILESERVICE_DNS="${FILESERVICE_DNS:-fileservice,localhost}"
GATEWAY_DNS="${GATEWAY_DNS:-gateway,localhost}"
GATEWAY_IPS="${GATEWAY_IPS:-127.0.0.1,192.168.64.5}"

should_create() {
  local crt="$1"
  local key="$2"
  [ "$FORCE_REGENERATE_CERTS" = "1" ] || [ ! -f "$crt" ] || [ ! -f "$key" ]
}

prepare_output_path() {
  local path="$1"
  if [ -d "$path" ]; then
    local backup="${path}.bak.$(date +%Y%m%d%H%M%S)"
    echo "  ! $path dizin; $backup olarak taşınıyor"
    mv "$path" "$backup"
  fi
}

san_line() {
  local dns_csv="$1"
  local ip_csv="${2:-}"
  local result=""
  local item

  IFS=',' read -ra dns_items <<< "$dns_csv"
  for item in "${dns_items[@]}"; do
    item="$(echo "$item" | xargs)"
    [ -z "$item" ] && continue
    result="${result:+$result,}DNS:$item"
  done

  IFS=',' read -ra ip_items <<< "$ip_csv"
  for item in "${ip_items[@]}"; do
    item="$(echo "$item" | xargs)"
    [ -z "$item" ] && continue
    result="${result:+$result,}IP:$item"
  done

  echo "$result"
}

echo "=== Platform CA ==="
prepare_output_path "$DIR/ca.crt"
prepare_output_path "$DIR/ca.key"
if should_create "$DIR/ca.crt" "$DIR/ca.key"; then
  openssl genrsa -out "$DIR/ca.key" 4096
  openssl req -new -x509 -key "$DIR/ca.key" \
    -out "$DIR/ca.crt" -days "$CA_DAYS" \
    -subj "/CN=platform-ca/O=Platform/OU=Internal"
  echo "  → ca.crt + ca.key üretildi"
else
  echo "  → mevcut CA korunuyor"
fi

sign_server() {
  local name="$1" subj="$2" dns_csv="$3" ip_csv="${4:-}"
  local crt="$DIR/$name.crt"
  local key="$DIR/$name.key"

  prepare_output_path "$crt"
  prepare_output_path "$key"

  if ! should_create "$crt" "$key"; then
    echo "=== $name (server) mevcut, atlandı ==="
    return
  fi

  echo "=== $name (server) ==="
  cat > "$TMP/$name.ext" <<EOF
[req]
distinguished_name = dn
[dn]
[SAN]
subjectAltName=$(san_line "$dns_csv" "$ip_csv")
extendedKeyUsage=serverAuth
EOF
  openssl genrsa -out "$key" 2048
  openssl req -new -key "$key" -out "$TMP/$name.csr" -subj "$subj"
  openssl x509 -req -in "$TMP/$name.csr" \
    -CA "$DIR/ca.crt" -CAkey "$DIR/ca.key" -CAcreateserial \
    -out "$crt" -days "$CERT_DAYS" \
    -extensions SAN -extfile "$TMP/$name.ext"
  echo "  → $name.crt + $name.key"
}

sign_client() {
  local name="$1" subj="$2"
  local crt="$DIR/$name.crt"
  local key="$DIR/$name.key"

  prepare_output_path "$crt"
  prepare_output_path "$key"

  if ! should_create "$crt" "$key"; then
    echo "=== $name (client) mevcut, atlandı ==="
    return
  fi

  echo "=== $name (client) ==="
  cat > "$TMP/$name.ext" <<EOF
[req]
distinguished_name = dn
[dn]
[EXT]
extendedKeyUsage=clientAuth
EOF
  openssl genrsa -out "$key" 2048
  openssl req -new -key "$key" -out "$TMP/$name.csr" -subj "$subj"
  openssl x509 -req -in "$TMP/$name.csr" \
    -CA "$DIR/ca.crt" -CAkey "$DIR/ca.key" -CAcreateserial \
    -out "$crt" -days "$CERT_DAYS" \
    -extensions EXT -extfile "$TMP/$name.ext"
  echo "  → $name.crt + $name.key"
}

sign_server "fileservice" "/CN=fileservice/O=Platform/OU=Internal" "$FILESERVICE_DNS"
sign_server "gateway"     "/CN=gateway/O=Platform/OU=Internal" "$GATEWAY_DNS" "$GATEWAY_IPS"
sign_client "yonetimapi"  "/CN=yonetimapi/O=Platform/OU=Internal"
sign_client "filoapi"     "/CN=filoapi/O=Platform/OU=Internal"

echo ""
echo "=== Doğrulama ==="
openssl verify -CAfile "$DIR/ca.crt" "$DIR/fileservice.crt"
openssl verify -CAfile "$DIR/ca.crt" "$DIR/gateway.crt"
openssl verify -CAfile "$DIR/ca.crt" "$DIR/yonetimapi.crt"
openssl verify -CAfile "$DIR/ca.crt" "$DIR/filoapi.crt"

echo ""
echo "Oluşturulan / korunan dosyalar:"
ls -1 "$DIR"/*.crt "$DIR"/*.key
