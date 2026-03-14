#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE_DIR="$SCRIPT_DIR/source"
RUNTIME_DIR="$SCRIPT_DIR/runtime/app"
WRAPPER_SRC="$SOURCE_DIR/wrapper.c"
CMDLINE_SRC="$SOURCE_DIR/cmdline.c"
WRAPPER_OUT="$RUNTIME_DIR/wrapper"

if [[ ! -f "$WRAPPER_SRC" || ! -f "$CMDLINE_SRC" ]]; then
  echo "wrapper.c/cmdline.c not found in $SOURCE_DIR" >&2
  exit 1
fi

mkdir -p "$RUNTIME_DIR"

CC_BIN="${CC:-cc}"
if ! command -v "$CC_BIN" >/dev/null 2>&1; then
  echo "C compiler not found (cc/clang/gcc)." >&2
  exit 1
fi

"$CC_BIN" -O3 -Wall -o "$WRAPPER_OUT" "$WRAPPER_SRC" "$CMDLINE_SRC"
chmod +x "$WRAPPER_OUT"

echo "Built wrapper: $WRAPPER_OUT"

ROOTFS_MAIN="$RUNTIME_DIR/rootfs/system/bin/main"
if [[ ! -f "$ROOTFS_MAIN" ]]; then
  echo "Warning: rootfs main binary is missing at $ROOTFS_MAIN" >&2
  echo "You must supply a built Android main binary (from the wrapper source + NDK)." >&2
fi
