#!/usr/bin/env bash
set -euo pipefail

IMAGE_NAME="${IMAGE_NAME:-deezspotag-dev}"
BENTO4_URL_DEFAULT="https://www.bok.net/Bento4/binaries/Bento4-SDK-1-6-0-641.x86_64-unknown-linux.zip"
BENTO4_URL="${BENTO4_URL:-$BENTO4_URL_DEFAULT}"

case "${1:-up}" in
  build)
    docker build -t "$IMAGE_NAME" --build-arg "BENTO4_URL=$BENTO4_URL" .
    ;;
  up)
    docker compose up -d deezspotag
    docker compose logs -f deezspotag
    ;;
  watch)
    docker compose up -d --build deezspotag-watch
    docker compose logs -f deezspotag-watch
    ;;
  hostnet)
    docker compose up -d deezspotag-hostnet
    docker compose logs -f deezspotag-hostnet
    ;;
  down)
    docker compose down
    ;;
  logs)
    docker compose logs -f "${2:-deezspotag}"
    ;;
  *)
    echo "Usage: $0 [build|up|watch|hostnet|down|logs [service]]" >&2
    exit 2
    ;;
esac
