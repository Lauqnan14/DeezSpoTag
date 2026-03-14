#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

rootfs_candidates=(
  "$SCRIPT_DIR/runtime/app/rootfs"
)

mounted_any=0

for ROOT_DIR in "${rootfs_candidates[@]}"; do
  if [[ ! -d "$ROOT_DIR" ]]; then
    continue
  fi

  mkdir -p "$ROOT_DIR/proc" "$ROOT_DIR/sys" "$ROOT_DIR/dev/pts"

  if ! grep -qs " $ROOT_DIR/proc " /proc/mounts; then
    mount --bind /proc "$ROOT_DIR/proc"
  fi
  if ! grep -qs " $ROOT_DIR/sys " /proc/mounts; then
    mount --bind /sys "$ROOT_DIR/sys"
  fi
  if ! grep -qs " $ROOT_DIR/dev/pts " /proc/mounts; then
    mount --bind /dev/pts "$ROOT_DIR/dev/pts"
  fi

  printf 'Mounted /proc, /sys, /dev/pts into %s\n' "$ROOT_DIR"
  mounted_any=1
done

if [[ $mounted_any -eq 0 ]]; then
  printf 'No rootfs directory found under runtime.\n' >&2
  exit 1
fi
