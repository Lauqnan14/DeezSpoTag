#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
IMAGE_TAG="${1:-deezspotag-apple-wrapper:local-amd64}"
PLATFORM="${PLATFORM:-linux/amd64}"

docker buildx build \
  --platform "$PLATFORM" \
  --load \
  -f "$SCRIPT_DIR/Dockerfile" \
  -t "$IMAGE_TAG" \
  "$REPO_ROOT"

if [[ "${SKIP_SMOKE:-0}" != "1" ]]; then
  "$REPO_ROOT/scripts/apple-wrapper-runtime-smoke.sh" "$IMAGE_TAG"
fi

printf 'Built wrapper image: %s (%s)\n' "$IMAGE_TAG" "$PLATFORM"
