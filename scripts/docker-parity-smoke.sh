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
APP_BASE_URL="http://127.0.0.1:${PORT}"
PARITY_USER="admin"
PARITY_PASSWORD="ParitySmokePass123!"

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

start_app_container() {
  docker_run -d \
    --name "${APP_CONTAINER_NAME}" \
    --network host \
    -e ASPNETCORE_URLS="http://127.0.0.1:${PORT}" \
    -e DEEZSPOTAG_DATA_DIR=/data \
    -e DEEZSPOTAG_CONFIG_DIR=/data \
    -e DEEZSPOTAG_BOOTSTRAP_USER="${PARITY_USER}" \
    -e DEEZSPOTAG_BOOTSTRAP_PASS="${PARITY_PASSWORD}" \
    -e LoginConfiguration__RequirePasswordChange=false \
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
}

wait_for_app() {
  for attempt in $(seq 1 60); do
    if curl -fsS "${APP_BASE_URL}/" >/dev/null; then
      return 0
    fi
    sleep 1
  done
  return 1
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
MODELS_DIR=/app/Tools/models /tmp/fetch-vibe-models.sh
echo "Vibe model manifest SHA-256: $(sha256sum /tmp/fetch-vibe-models.sh | cut -d " " -f1)"
/opt/venv/bin/python3 /app/Tools/vibe_analyzer.py --probe --models /app/Tools/models > /tmp/vibe-probe.json
python3 - <<'"'"'PY'"'"'
import json
with open("/tmp/vibe-probe.json", "r", encoding="utf-8") as handle:
    payload = json.load(handle)
if not isinstance(payload, dict):
    raise RuntimeError("vibe probe did not return a JSON object")
if payload.get("ok") is not True:
    missing_required = payload.get("missingRequired")
    raise RuntimeError(f"vibe probe failed: ok={payload.get('ok')} missingRequired={missing_required} message={payload.get('message')}")
if payload.get("enhancedMode") is not True:
    raise RuntimeError(f"vibe enhanced mode unavailable: {payload}")
print("vibe probe ok=", payload.get("ok"))
PY

ffmpeg -hide_banner -loglevel error -nostdin -y \
  -f lavfi -i "sine=frequency=440:sample_rate=44100:duration=8" \
  -c:a flac /tmp/vibe-worker-smoke.flac
/opt/venv/bin/python3 - <<'"'"'PY'"'"'
import json
import shlex
import subprocess

command = "/opt/venv/bin/python3 /app/Tools/vibe_analyzer.py --worker --models /app/Tools/models"
requests = [
    {"requestId": "parity-1", "filePath": "/tmp/vibe-worker-smoke.flac"},
    {"requestId": "parity-2", "filePath": "/tmp/vibe-worker-smoke.flac"},
]
request_body = "".join(json.dumps(item) + "\n" for item in requests)
completed = subprocess.run(
    shlex.split(command),
    input=request_body,
    text=True,
    capture_output=True,
    timeout=600,
    check=False,
)
if completed.returncode != 0:
    raise RuntimeError(f"persistent vibe worker failed ({completed.returncode}): {completed.stderr[-2000:]}")
responses = [json.loads(line) for line in completed.stdout.splitlines() if line.strip()]
if len(responses) != 2:
    raise RuntimeError(f"persistent vibe worker returned {len(responses)} responses: {completed.stdout[-2000:]}")
for index, response in enumerate(responses, start=1):
    if response.get("requestId") != f"parity-{index}":
        raise RuntimeError(f"persistent vibe worker response order mismatch: {responses}")
    if response.get("ok") is not True or response.get("AnalysisMode") != "enhanced":
        raise RuntimeError(f"persistent vibe worker did not return enhanced output: {response}")
    if response.get("AnalysisVersion") != "musicnn-1":
        raise RuntimeError(f"persistent vibe worker returned the wrong analysis version: {response}")
    if response.get("GenreModel") != "discogs519-maest-30s-pw-519l":
        raise RuntimeError(f"persistent vibe worker did not initialize Discogs519/MAEST: {response}")
    if not isinstance(response.get("EssentiaGenreEvidence"), list):
        raise RuntimeError(f"persistent vibe worker omitted Essentia genre evidence: {response}")
    for field in ("MoodTags", "Genres", "DanceabilityMl", "ValenceMl", "ArousalMl"):
        if field not in response:
            raise RuntimeError(f"persistent vibe response missing {field}: {response}")
print("persistent vibe worker responses=", len(responses))
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

echo "[4/7] Initializing the application database..."
mkdir -p "${HOST_DATA_DIR}/analysis"
cat > "${HOST_DATA_DIR}/analysis/settings.json" <<'JSON'
{"enabled":false,"batchSize":10,"intervalMinutes":5,"useLibraryOrder":false,"libraryOrder":[]}
JSON
start_app_container
if ! wait_for_app; then
  echo "Parity smoke test failed. Last app logs:"
  docker logs "${APP_CONTAINER_NAME}" || true
  echo "Last wrapper logs:"
  docker logs "${WRAPPER_CONTAINER_NAME}" || true
  exit 1
fi
docker stop --time 30 "${APP_CONTAINER_NAME}" >/dev/null
docker rm "${APP_CONTAINER_NAME}" >/dev/null
docker_run --rm --entrypoint /bin/sh -v "${HOST_DATA_DIR}:/data" "${IMAGE}" -c \
  'chmod 0777 /data/db/library /data/db/library/deezspotag.db'

echo "[5/7] Seeding two Application-level enhanced Vibe analysis candidates..."
ffmpeg -hide_banner -loglevel error -nostdin -y \
  -f lavfi -i "sine=frequency=440:sample_rate=44100:duration=8" \
  -c:a flac "${HOST_DOWNLOADS_DIR}/vibe-app-1.flac"
ffmpeg -hide_banner -loglevel error -nostdin -y \
  -f lavfi -i "sine=frequency=880:sample_rate=44100:duration=8" \
  -c:a flac "${HOST_DOWNLOADS_DIR}/vibe-app-2.flac"
fixture_one_size="$(stat -c %s "${HOST_DOWNLOADS_DIR}/vibe-app-1.flac")"
fixture_two_size="$(stat -c %s "${HOST_DOWNLOADS_DIR}/vibe-app-2.flac")"
library_db="${HOST_DATA_DIR}/db/library/deezspotag.db"
test -f "${library_db}"
sqlite3 "${library_db}" <<SQL
PRAGMA foreign_keys = ON;
INSERT INTO library (id, name) VALUES (900001, 'Parity Vibe Library');
INSERT INTO folder (id, root_path, display_name, enabled, library_id, desired_quality_value)
VALUES (900001, '/downloads', 'Parity Vibe Folder', 1, 900001, 'FLAC');
INSERT INTO artist (id, name) VALUES (900001, 'Parity Artist');
INSERT INTO album (id, artist_id, title) VALUES (900001, 900001, 'Parity Album');
INSERT INTO track (id, album_id, title, duration_ms, track_no, tag_artist, tag_album, tag_track_no)
VALUES (900001, 900001, 'Parity Tone 440', 8000, 1, 'Parity Artist', 'Parity Album', 1),
       (900002, 900001, 'Parity Tone 880', 8000, 2, 'Parity Artist', 'Parity Album', 2);
INSERT INTO audio_file (id, path, relative_path, folder_id, size, duration_ms, codec, extension, sample_rate_hz, bits_per_sample, channels, quality_rank)
VALUES (900001, '/downloads/vibe-app-1.flac', 'vibe-app-1.flac', 900001, ${fixture_one_size}, 8000, 'flac', '.flac', 44100, 16, 1, 100),
       (900002, '/downloads/vibe-app-2.flac', 'vibe-app-2.flac', 900001, ${fixture_two_size}, 8000, 'flac', '.flac', 44100, 16, 1, 100);
INSERT INTO track_local (track_id, audio_file_id) VALUES (900001, 900001), (900002, 900002);
SQL
cat > "${HOST_DATA_DIR}/analysis/settings.json" <<'JSON'
{"enabled":true,"batchSize":10,"intervalMinutes":5,"useLibraryOrder":false,"libraryOrder":[]}
JSON

echo "[6/7] Running application-level enhanced Vibe analysis..."
start_app_container
if ! wait_for_app; then
  echo "Parity smoke test failed. Last app logs:"
  docker logs "${APP_CONTAINER_NAME}" || true
  exit 1
fi

analysis_deadline=$((SECONDS + 600))
while (( SECONDS < analysis_deadline )); do
  completed_count="$(sqlite3 "${library_db}" "SELECT count(*) FROM track_analysis WHERE track_id IN (900001,900002) AND status IN ('complete','completed');")"
  if [ "${completed_count}" = "2" ]; then
    break
  fi
  sleep 2
done
if [ "${completed_count:-0}" != "2" ]; then
  echo "Application-level Vibe analysis did not complete two tracks before the deadline." >&2
  sqlite3 -header -column "${library_db}" "SELECT track_id,status,error,analysis_mode,analysis_version FROM track_analysis WHERE track_id IN (900001,900002);" >&2 || true
  docker logs "${APP_CONTAINER_NAME}" >&2 || true
  exit 1
fi

sqlite3 -separator '|' "${library_db}" \
  "SELECT track_id,status,analysis_mode,analysis_version,json_array_length(essentia_genres),json_array_length(mood_tags),mood_happy,mood_sad,mood_relaxed,mood_aggressive,mood_party,mood_acoustic,mood_electronic,json_array_length(json_extract(metadata_json,'$.semanticEvidence')),json_extract(metadata_json,'$.genreModel') FROM track_analysis WHERE track_id IN (900001,900002) ORDER BY track_id;" \
  > "${HOST_DATA_DIR}/application-vibe-results.txt"
python3 - "${HOST_DATA_DIR}/application-vibe-results.txt" <<'PY'
import pathlib
import sys

rows = [line.split("|") for line in pathlib.Path(sys.argv[1]).read_text(encoding="utf-8").splitlines() if line]
if len(rows) != 2:
    raise RuntimeError(f"expected two persisted application analyses, received {rows}")
for row in rows:
    track_id, status, mode, version, genre_count, mood_count, *outputs = row
    mood_scores = outputs[:7]
    evidence_count, genre_model = outputs[7:]
    if status not in {"complete", "completed"} or mode != "enhanced" or version != "musicnn-1":
        raise RuntimeError(f"track {track_id} did not persist enhanced musicnn-1 analysis: {row}")
    if int(evidence_count or "0") < 1 or genre_model != "discogs519-maest-30s-pw-519l":
        raise RuntimeError(f"track {track_id} omitted Discogs519 Essentia genre evidence: {row}")
    if mood_count == "" or len(mood_scores) != 7 or any(value == "" for value in mood_scores):
        raise RuntimeError(f"track {track_id} omitted mood outputs: {row}")
print("application enhanced results=", len(rows))
PY

cookie_jar="${HOST_DATA_DIR}/parity-cookies.txt"
login_page="${HOST_DATA_DIR}/parity-login.html"
curl -fsS -c "${cookie_jar}" "${APP_BASE_URL}/Identity/Account/Login" > "${login_page}"
request_token="$(python3 - "${login_page}" <<'PY'
import html
import pathlib
import re
import sys

page = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8")
match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', page)
if not match:
    raise RuntimeError("login antiforgery token was not found")
print(html.unescape(match.group(1)))
PY
)"
curl -fsS -L -b "${cookie_jar}" -c "${cookie_jar}" \
  --data-urlencode "__RequestVerificationToken=${request_token}" \
  --data-urlencode "Input.Username=${PARITY_USER}" \
  --data-urlencode "Input.Password=${PARITY_PASSWORD}" \
  "${APP_BASE_URL}/Identity/Account/Login" >/dev/null
curl -fsS -b "${cookie_jar}" "${APP_BASE_URL}/api/library/analysis/runtime" > "${HOST_DATA_DIR}/application-vibe-runtime.json"
python3 - "${HOST_DATA_DIR}/application-vibe-runtime.json" <<'PY'
import json
import pathlib
import sys

runtime = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
worker = runtime.get("analyzer") or {}
if worker.get("state") != "ready":
    raise RuntimeError(f"Vibe worker state is not ready: {worker}")
if worker.get("startCount") != 1:
    raise RuntimeError(f"Vibe worker start count is not 1: {worker}")
print("application worker state=ready; worker start count=1")
PY

if docker logs "${APP_CONTAINER_NAME}" 2>&1 | grep -Fq "Essentia analysis failed; standard analysis was used"; then
  echo "Application emitted an analyzer-fallback activity warning." >&2
  docker logs "${APP_CONTAINER_NAME}" >&2 || true
  exit 1
fi

echo "[7/7] Auditing source/image parity markers..."
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
