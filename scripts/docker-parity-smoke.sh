#!/usr/bin/env bash
set -euo pipefail

IMAGE="${1:-deezspotag-web:local}"
APPLE_WRAPPER_IMAGE="${PARITY_APPLE_WRAPPER_IMAGE:-}"
PORT="${PARITY_TEST_PORT:-18668}"
HOST_DATA_DIR="${PARITY_TEST_DATA_DIR:-}"
HOST_DOWNLOADS_DIR="${PARITY_TEST_DOWNLOADS_DIR:-}"
HOST_WRAPPER_DATA_DIR="${PARITY_TEST_WRAPPER_DATA_DIR:-}"
HOST_WRAPPER_SESSION_DIR="${PARITY_TEST_WRAPPER_SESSION_DIR:-}"
APP_CONTAINER_NAME="deezspotag-parity-smoke-$$"
WRAPPER_CONTAINER_NAME="deezspotag-apple-wrapper-parity-smoke-$$"
AUTO_DATA_DIR="0"
AUTO_DOWNLOADS_DIR="0"
AUTO_WRAPPER_DATA_DIR="0"
AUTO_WRAPPER_SESSION_DIR="0"

cleanup() {
  docker rm -f "${APP_CONTAINER_NAME}" >/dev/null 2>&1 || true
  docker rm -f "${WRAPPER_CONTAINER_NAME}" >/dev/null 2>&1 || true
  if [ "${AUTO_DATA_DIR}" = "1" ] && [ -n "${HOST_DATA_DIR}" ] && [ -d "${HOST_DATA_DIR}" ]; then
    rm -rf "${HOST_DATA_DIR}" >/dev/null 2>&1 || true
  fi
  if [ "${AUTO_DOWNLOADS_DIR}" = "1" ] && [ -n "${HOST_DOWNLOADS_DIR}" ] && [ -d "${HOST_DOWNLOADS_DIR}" ]; then
    rm -rf "${HOST_DOWNLOADS_DIR}" >/dev/null 2>&1 || true
  fi
  if [ "${AUTO_WRAPPER_DATA_DIR}" = "1" ] && [ -n "${HOST_WRAPPER_DATA_DIR}" ] && [ -d "${HOST_WRAPPER_DATA_DIR}" ]; then
    rm -rf "${HOST_WRAPPER_DATA_DIR}" >/dev/null 2>&1 || true
  fi
  if [ "${AUTO_WRAPPER_SESSION_DIR}" = "1" ] && [ -n "${HOST_WRAPPER_SESSION_DIR}" ] && [ -d "${HOST_WRAPPER_SESSION_DIR}" ]; then
    rm -rf "${HOST_WRAPPER_SESSION_DIR}" >/dev/null 2>&1 || true
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

if [ -z "${HOST_WRAPPER_DATA_DIR}" ]; then
  HOST_WRAPPER_DATA_DIR="$(mktemp -d -t deezspotag-parity-wrapper-data-XXXXXX)"
  AUTO_WRAPPER_DATA_DIR="1"
fi

if [ -z "${HOST_WRAPPER_SESSION_DIR}" ]; then
  HOST_WRAPPER_SESSION_DIR="$(mktemp -d -t deezspotag-parity-wrapper-session-XXXXXX)"
  AUTO_WRAPPER_SESSION_DIR="1"
fi

mkdir -p "${HOST_DATA_DIR}" "${HOST_DOWNLOADS_DIR}" "${HOST_WRAPPER_DATA_DIR}" "${HOST_WRAPPER_SESSION_DIR}"
chmod 0777 "${HOST_DATA_DIR}" "${HOST_DOWNLOADS_DIR}" "${HOST_WRAPPER_DATA_DIR}" "${HOST_WRAPPER_SESSION_DIR}"

echo "[1/4] Checking runtime dependencies in ${IMAGE}..."
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

echo "[2/4] Starting wrapper smoke container (shared-control contract)..."
if [ -z "${APPLE_WRAPPER_IMAGE}" ]; then
  echo "PARITY_APPLE_WRAPPER_IMAGE is required for parity smoke tests." >&2
  exit 1
fi

docker run -d \
  --name "${WRAPPER_CONTAINER_NAME}" \
  --network host \
  -v "${HOST_WRAPPER_DATA_DIR}:/opt/apple-wrapper/data" \
  -v "${HOST_WRAPPER_SESSION_DIR}:/opt/apple-wrapper/rootfs/data/data/com.apple.android.music" \
  -v /dev/urandom:/opt/apple-wrapper/rootfs/dev/urandom:ro \
  -v /dev/random:/opt/apple-wrapper/rootfs/dev/random:ro \
  "${APPLE_WRAPPER_IMAGE}" >/dev/null

sleep 5
if ! docker ps --format '{{.Names}}' | grep -Fxq "${WRAPPER_CONTAINER_NAME}"; then
  echo "Wrapper parity smoke failed. Last wrapper logs:" >&2
  docker logs "${WRAPPER_CONTAINER_NAME}" || true
  exit 1
fi

echo "[3/4] Starting release image container..."
docker run -d \
  --name "${APP_CONTAINER_NAME}" \
  --network host \
  -e ASPNETCORE_URLS="http://127.0.0.1:${PORT}" \
  -e DEEZSPOTAG_DATA_DIR=/data \
  -e DEEZSPOTAG_CONFIG_DIR=/data \
  -e DEEZSPOTAG_BOOTSTRAP_USER=admin \
  -e DEEZSPOTAG_BOOTSTRAP_PASS=ParitySmokePass123! \
  -e DEEZSPOTAG_APPLE_WRAPPER_MODE=external \
  -e DEEZSPOTAG_APPLE_WRAPPER_CONTROL_MODE=shared \
  -e DEEZSPOTAG_APPLE_WRAPPER_HOST=127.0.0.1 \
  -e DEEZSPOTAG_APPLE_WRAPPER_SHARED_DATA_DIR=/apple-wrapper/data \
  -e DEEZSPOTAG_APPLE_WRAPPER_SHARED_SESSION_DIR=/apple-wrapper/session \
  -v "${HOST_DATA_DIR}:/data" \
  -v "${HOST_DOWNLOADS_DIR}:/downloads" \
  -v "${HOST_WRAPPER_DATA_DIR}:/apple-wrapper/data" \
  -v "${HOST_WRAPPER_SESSION_DIR}:/apple-wrapper/session" \
  "${IMAGE}" >/dev/null

echo "[4/4] Probing HTTP readiness on 127.0.0.1:${PORT}..."
for attempt in $(seq 1 60); do
  if curl -fsS "http://127.0.0.1:${PORT}/" >/dev/null; then
    echo "Parity smoke test passed."
    exit 0
  fi

  sleep 1
done

echo "Parity smoke test failed. Last app logs:"
docker logs "${APP_CONTAINER_NAME}" || true
echo "Last wrapper logs:"
docker logs "${WRAPPER_CONTAINER_NAME}" || true
exit 1
