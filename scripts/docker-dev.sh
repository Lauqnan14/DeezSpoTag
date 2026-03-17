#!/usr/bin/env bash
set -euo pipefail

IMAGE_NAME="${IMAGE_NAME:-deezspotag-web:local}"
BENTO4_URL_DEFAULT="https://www.bok.net/Bento4/binaries/Bento4-SDK-1-6-0-641.x86_64-unknown-linux.zip"
BENTO4_URL="${BENTO4_URL:-$BENTO4_URL_DEFAULT}"

get_image_id() {
    docker image inspect "$1" --format '{{.Id}}' 2>/dev/null || true
}

remove_replaced_image() {
    local previous_image_id="$1"
    local current_image_id="$2"

    if [ -z "$previous_image_id" ] || [ "$previous_image_id" = "$current_image_id" ]; then
        return
    fi

    docker image rm -f "$previous_image_id" >/dev/null 2>&1 || true
}

split_image_reference() {
    local image_ref="$1"
    local repository="$image_ref"
    local tag="latest"
    local last_segment

    if [[ "$image_ref" == *@* ]]; then
        repository="${image_ref%@*}"
        tag="${image_ref##*@}"
    else
        last_segment="${image_ref##*/}"
        if [[ "$last_segment" == *:* ]]; then
            repository="${image_ref%:*}"
            tag="${image_ref##*:}"
        fi
    fi

    printf '%s %s\n' "$repository" "$tag"
}

remove_old_local_tags_for_image() {
    local image_ref="$1"
    local repository
    local tag
    local current_ref
    local candidate_ref

    read -r repository tag < <(split_image_reference "$image_ref")
    current_ref="${repository}:${tag}"

    while IFS= read -r candidate_ref; do
        if [ -z "$candidate_ref" ] || [ "$candidate_ref" = "$current_ref" ]; then
            continue
        fi

        if [[ "$candidate_ref" == *":<none>" ]]; then
            continue
        fi

        docker image rm "$candidate_ref" >/dev/null 2>&1 || true
    done < <(docker image ls "$repository" --format '{{.Repository}}:{{.Tag}}')

    docker image prune -f >/dev/null 2>&1 || true
}

build_local_image() {
    local previous_image_id
    local current_image_id

    previous_image_id="$(get_image_id "$IMAGE_NAME")"
    docker build -t "$IMAGE_NAME" --build-arg "BENTO4_URL=$BENTO4_URL" .
    current_image_id="$(get_image_id "$IMAGE_NAME")"
    remove_replaced_image "$previous_image_id" "$current_image_id"
    remove_old_local_tags_for_image "$IMAGE_NAME"
}

case "${1:-up}" in
  build)
    build_local_image
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
    build_local_image
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
