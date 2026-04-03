#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DEV_DIR="$ROOT_DIR/EmailProvider/.dev/smtp"
CERT_PATH="$DEV_DIR/server.crt"
KEY_PATH="$DEV_DIR/server.key"
PFX_PATH="$DEV_DIR/server.pfx"
PASSWORD="symposia-dev-pass"

mkdir -p "$DEV_DIR"

openssl req \
  -x509 \
  -newkey rsa:2048 \
  -keyout "$KEY_PATH" \
  -out "$CERT_PATH" \
  -days 30 \
  -nodes \
  -subj "/CN=localhost"

openssl pkcs12 \
  -export \
  -out "$PFX_PATH" \
  -inkey "$KEY_PATH" \
  -in "$CERT_PATH" \
  -passout "pass:$PASSWORD"

echo "Development SMTP certificate written to $PFX_PATH"
