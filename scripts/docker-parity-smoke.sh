#!/usr/bin/env bash
set -euo pipefail

IMAGE="${1:-deezspotag-web:local}"
PORT="${PARITY_TEST_PORT:-18668}"
HOST_DATA_DIR="${PARITY_TEST_DATA_DIR:-}"
HOST_DOWNLOADS_DIR="${PARITY_TEST_DOWNLOADS_DIR:-}"
CONTAINER_NAME="deezspotag-parity-smoke-$$"
AUTO_DATA_DIR="0"
AUTO_DOWNLOADS_DIR="0"

cleanup() {
  docker rm -f "${CONTAINER_NAME}" >/dev/null 2>&1 || true
  if [ "${AUTO_DATA_DIR}" = "1" ] && [ -n "${HOST_DATA_DIR}" ] && [ -d "${HOST_DATA_DIR}" ]; then
    rm -rf "${HOST_DATA_DIR}" >/dev/null 2>&1 || true
  fi
  if [ "${AUTO_DOWNLOADS_DIR}" = "1" ] && [ -n "${HOST_DOWNLOADS_DIR}" ] && [ -d "${HOST_DOWNLOADS_DIR}" ]; then
    rm -rf "${HOST_DOWNLOADS_DIR}" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

if [ -z "${HOST_DATA_DIR}" ]; then
  HOST_DATA_DIR="$(mktemp -d -t deezspotag-parity-data-XXXXXX)"
  AUTO_DATA_DIR="1"
fi

if [ -z "${HOST_DOWNLOADS_DIR}" ]; then
  HOST_DOWNLOADS_DIR="$(mktemp -d -t deezspotag-parity-downloads-XXXXXX)"
  AUTO_DOWNLOADS_DIR="1"
fi

mkdir -p "${HOST_DATA_DIR}"
mkdir -p "${HOST_DOWNLOADS_DIR}"
chmod 0777 "${HOST_DATA_DIR}"
chmod 0777 "${HOST_DOWNLOADS_DIR}"

echo "[1/3] Checking runtime dependencies in ${IMAGE}..."
docker run --rm --entrypoint /bin/sh "${IMAGE}" -c '
set -e
timeout 20s ffmpeg -version >/dev/null
timeout 20s mp4box -version >/dev/null
command -v mp4decrypt >/dev/null
python3 - <<'"'"'PY'"'"'
import essentia.standard as es
print("essentia import ok")
PY
test -f /app/Tools/vibe_analyzer.py
test -d /app/Tools/models
test -x /app/Tools/AppleMusicWrapper/runv2/apple-wrapper-runv2
'

echo "[2/3] Starting release image container..."
docker run -d \
  --name "${CONTAINER_NAME}" \
  --network host \
  -e ASPNETCORE_URLS="http://127.0.0.1:${PORT}" \
  -e DEEZSPOTAG_DATA_DIR=/data \
  -e DEEZSPOTAG_CONFIG_DIR=/data \
  -e DEEZSPOTAG_BOOTSTRAP_USER=admin \
  -e DEEZSPOTAG_BOOTSTRAP_PASS=ParitySmokePass123! \
  -e DEEZSPOTAG_APPLE_WRAPPER_MODE=external \
  -e DEEZSPOTAG_APPLE_WRAPPER_HOST=127.0.0.1 \
  -v "${HOST_DATA_DIR}:/data" \
  -v "${HOST_DOWNLOADS_DIR}:/downloads" \
  "${IMAGE}" >/dev/null

echo "[3/3] Probing HTTP readiness on 127.0.0.1:${PORT}..."
for attempt in $(seq 1 60); do
  if curl -fsS "http://127.0.0.1:${PORT}/" >/dev/null; then
    echo "Parity smoke test passed."
    exit 0
  fi

  sleep 1
done

echo "Parity smoke test failed. Last container logs:"
docker logs "${CONTAINER_NAME}" || true
exit 1
