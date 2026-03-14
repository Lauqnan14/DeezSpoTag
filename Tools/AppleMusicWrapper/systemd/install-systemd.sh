#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUNTIME_DIR="$SCRIPT_DIR/../runtime/app"
TARGET_DIR="/opt/apple-wrapper"
SERVICE_FILE="/etc/systemd/system/apple-wrapper.service"
ENV_FILE="/etc/default/apple-wrapper"

if [[ $EUID -ne 0 ]]; then
  echo "Run as root (sudo)." >&2
  exit 1
fi

if [[ ! -x "$RUNTIME_DIR/wrapper" ]]; then
  echo "Wrapper binary missing at $RUNTIME_DIR/wrapper" >&2
  exit 1
fi

if [[ ! -f "$RUNTIME_DIR/rootfs/system/bin/main" ]]; then
  echo "Rootfs main binary missing at $RUNTIME_DIR/rootfs/system/bin/main" >&2
  exit 1
fi

mkdir -p "$TARGET_DIR"
rsync -a --delete "$RUNTIME_DIR/" "$TARGET_DIR/"

chown -R root:root "$TARGET_DIR"
chmod 4755 "$TARGET_DIR/wrapper"

install -m 0644 "$SCRIPT_DIR/apple-wrapper.service" "$SERVICE_FILE"
install -m 0600 "$SCRIPT_DIR/apple-wrapper.env" "$ENV_FILE"
rm -f /run/apple-wrapper-login.env

systemctl daemon-reload
systemctl enable --now apple-wrapper.service

echo "Installed and started apple-wrapper.service"
