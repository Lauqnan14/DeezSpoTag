#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

rootfs_candidates=(
  "$SCRIPT_DIR/runtime/app/rootfs"
)

created_any=0

create_node() {
  local dev_dir="$1" name="$2" type="$3" major="$4" minor="$5" mode="$6"
  local path="$dev_dir/$name"
  if [ -e "$path" ]; then
    return
  fi
  mknod -m "$mode" "$path" "$type" "$major" "$minor"
}

for ROOT_DIR in "${rootfs_candidates[@]}"; do
  if [[ ! -d "$ROOT_DIR" ]]; then
    continue
  fi

  DEV_DIR="$ROOT_DIR/dev"
  mkdir -p "$DEV_DIR"

  create_node "$DEV_DIR" null c 1 3 666
  create_node "$DEV_DIR" zero c 1 5 666
  create_node "$DEV_DIR" random c 1 8 666
  create_node "$DEV_DIR" urandom c 1 9 666
  create_node "$DEV_DIR" tty c 5 0 666
  create_node "$DEV_DIR" ptmx c 5 2 666

  printf 'Rootfs /dev nodes ready in %s\n' "$DEV_DIR"
  created_any=1
done

if [[ $created_any -eq 0 ]]; then
  printf 'No rootfs directory found under runtime.\n' >&2
  exit 1
fi
