#!/usr/bin/env bash
set -euo pipefail

IMAGE="${1:-deezspotag-web:local}"
APPLE_WRAPPER_IMAGE="${PARITY_APPLE_WRAPPER_IMAGE:-}"
PORT="${PARITY_TEST_PORT:-18668}"
EXPECTED_BUILD_VERSION="${PARITY_EXPECTED_BUILD_VERSION:-}"
PLATFORM="${PARITY_TEST_PLATFORM:-}"
REQUIRE_MP4DECRYPT="${PARITY_REQUIRE_MP4DECRYPT:-0}"
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

docker_run() {
  if [ -n "${PLATFORM}" ]; then
    docker run --platform "${PLATFORM}" "$@"
  else
    docker run "$@"
  fi
}

assert_image_file_hash_matches_source() {
  local source_file="$1"
  local image_file="$2"
  local source_hash
  local image_hash

  if [[ ! -f "$source_file" ]]; then
    echo "Parity audit source file missing: $source_file" >&2
    exit 1
  fi

  source_hash="$(sha256sum "$source_file" | awk '{print $1}')"
  image_hash="$(docker_run --rm --entrypoint /bin/sh "$IMAGE" -lc "sha256sum '$image_file' 2>/dev/null | awk '{print \$1}'" | tr -d '\r\n')"

  if [[ -z "$image_hash" ]]; then
    echo "Parity audit image file missing: $image_file" >&2
    exit 1
  fi

  if [[ "$source_hash" != "$image_hash" ]]; then
    echo "Parity audit hash mismatch for $source_file ($source_hash) vs $image_file ($image_hash)." >&2
    exit 1
  fi
}

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

echo "[1/6] Checking runtime dependencies in ${IMAGE}..."
docker_run --rm -e PARITY_REQUIRE_MP4DECRYPT="${REQUIRE_MP4DECRYPT}" --entrypoint /bin/sh "${IMAGE}" -c '
set -e
timeout 20s ffmpeg -version >/dev/null
timeout 20s mp4box -version >/dev/null
if [ "${PARITY_REQUIRE_MP4DECRYPT:-0}" = "1" ]; then
  command -v mp4decrypt >/dev/null
fi
test -x /opt/venv/bin/python3
/opt/venv/bin/python3 /app/Tools/vibe_analyzer.py --probe > /tmp/vibe-probe.json
python3 - <<'"'"'PY'"'"'
import json
with open("/tmp/vibe-probe.json", "r", encoding="utf-8") as handle:
    payload = json.load(handle)
if not isinstance(payload, dict):
    raise RuntimeError("vibe probe did not return a JSON object")
if payload.get("ok") is not True:
    missing_required = payload.get("missingRequired")
    raise RuntimeError(f"vibe probe failed: ok={payload.get('ok')} missingRequired={missing_required} message={payload.get('message')}")
print("vibe probe ok=", payload.get("ok"))
PY
test -f /app/Tools/vibe_analyzer.py
test -d /app/Tools/models
test -x /app/Tools/AppleMusicWrapper/runv2/apple-wrapper-runv2
'

echo "[2/6] Checking wrapper image runtime contract..."
if [ -z "${APPLE_WRAPPER_IMAGE}" ]; then
  echo "PARITY_APPLE_WRAPPER_IMAGE is required for parity smoke tests." >&2
  exit 1
fi
docker_run --rm --entrypoint /bin/sh "${APPLE_WRAPPER_IMAGE}" -c '
set -e
test -x /opt/apple-wrapper/wrapper
test -x /opt/apple-wrapper/rootfs/system/bin/main
test -x /opt/apple-wrapper/entrypoint.sh
'

echo "[3/6] Starting wrapper smoke container (shared-control contract)..."
docker_run -d \
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

echo "[4/6] Starting release image container..."
docker_run -d \
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

echo "[5/6] Probing HTTP readiness on 127.0.0.1:${PORT}..."
for attempt in $(seq 1 60); do
  if curl -fsS "http://127.0.0.1:${PORT}/" >/dev/null; then
    break
  fi

  sleep 1
done

if ! curl -fsS "http://127.0.0.1:${PORT}/" >/dev/null; then
  echo "Parity smoke test failed. Last app logs:"
  docker logs "${APP_CONTAINER_NAME}" || true
  echo "Last wrapper logs:"
  docker logs "${WRAPPER_CONTAINER_NAME}" || true
  exit 1
fi

echo "[6/6] Auditing source/image parity markers..."
assert_image_file_hash_matches_source "DeezSpoTag.Web/wwwroot/js/library.js" "/app/wwwroot/js/library.js"
assert_image_file_hash_matches_source "DeezSpoTag.Web/wwwroot/js/library-album-page.js" "/app/wwwroot/js/library-album-page.js"
assert_image_file_hash_matches_source "DeezSpoTag.Web/wwwroot/js/library-interactions.js" "/app/wwwroot/js/library-interactions.js"
assert_image_file_hash_matches_source "DeezSpoTag.Web/wwwroot/js/site.js" "/app/wwwroot/js/site.js"
assert_image_file_hash_matches_source "DeezSpoTag.Web/wwwroot/css/library.css" "/app/wwwroot/css/library.css"

build_version="$(docker image inspect "${IMAGE}" --format '{{range .Config.Env}}{{println .}}{{end}}' | grep '^DEEZSPOTAG_BUILD_VERSION=' | head -n 1 | cut -d= -f2-)"
if [[ -z "$build_version" ]]; then
  echo "Parity audit failed: DEEZSPOTAG_BUILD_VERSION is missing from image env." >&2
  exit 1
fi
if [[ "$build_version" == "dev" ]]; then
  echo "Parity audit failed: DEEZSPOTAG_BUILD_VERSION is still 'dev' in ${IMAGE}." >&2
  exit 1
fi
if [[ -n "$EXPECTED_BUILD_VERSION" && "$build_version" != "$EXPECTED_BUILD_VERSION" ]]; then
  echo "Parity audit failed: image build version '$build_version' != expected '$EXPECTED_BUILD_VERSION'." >&2
  exit 1
fi

echo "Parity smoke + audit passed (build version: ${build_version})."
exit 0
