#!/usr/bin/env bash
set -euo pipefail

IMAGE_NAME="${IMAGE_NAME:-deezspotag-web:local}"
BENTO4_URL_DEFAULT="https://www.bok.net/Bento4/binaries/Bento4-SDK-1-6-0-641.x86_64-unknown-linux.zip"
BENTO4_URL="${BENTO4_URL:-$BENTO4_URL_DEFAULT}"

case "${1:-up}" in
  build)
    docker build -t "$IMAGE_NAME" --build-arg "BENTO4_URL=$BENTO4_URL" .
    ;;
  up)
    docker compose up -d --build deezspotag
    docker compose logs -f deezspotag
    ;;
  wrapper)
    # Local parity mode: run Apple wrapper in Docker while running the web app via dotnet on host.
    docker compose stop deezspotag >/dev/null 2>&1 || true
    docker compose up -d --build apple-wrapper
    docker compose logs -f apple-wrapper
    ;;
  watch)
    docker compose up -d --build deezspotag-watch
    docker compose logs -f deezspotag-watch
    ;;
  hostnet)
    docker compose up -d deezspotag-hostnet
    docker compose logs -f deezspotag-hostnet
    ;;
  parity)
    docker compose build deezspotag
    ./scripts/docker-parity-smoke.sh "$IMAGE_NAME"
    ;;
  down)
    docker compose down
    ;;
  logs)
    docker compose logs -f "${2:-deezspotag}"
    ;;
  *)
    echo "Usage: $0 [build|up|wrapper|watch|hostnet|parity|down|logs [service]]" >&2
    exit 2
    ;;
esac
