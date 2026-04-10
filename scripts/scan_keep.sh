#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCAN_SCRIPT="${SCRIPT_DIR}/scan.sh"

if [[ ! -f "$SCAN_SCRIPT" ]]; then
  echo "scan.sh not found alongside scan_keep.sh." >&2
  exit 1
fi

SONAR_KEEP_LOCAL_SCAN_STATE=true "$SCAN_SCRIPT" "$@"
