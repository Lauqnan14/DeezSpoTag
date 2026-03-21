#!/usr/bin/env bash
set -euo pipefail

IMAGE="${1:-deezspotag-apple-wrapper:local-amd64}"
PLATFORM="${PARITY_TEST_PLATFORM:-linux/amd64}"
WAIT_CONTAINER_NAME="apple-wrapper-smoke-wait-$$"
LOGIN_CONTAINER_NAME="apple-wrapper-smoke-login-$$"
RETRY_CONTAINER_NAME="apple-wrapper-smoke-retry-$$"
HOST_WRAPPER_DATA_DIR="${PARITY_TEST_WRAPPER_DATA_DIR:-}"
HOST_WRAPPER_SESSION_DIR="${PARITY_TEST_WRAPPER_SESSION_DIR:-}"
HOST_WRAPPER_TEST_DIR="${PARITY_TEST_WRAPPER_TEST_DIR:-}"
AUTO_WRAPPER_DATA_DIR="0"
AUTO_WRAPPER_SESSION_DIR="0"
AUTO_WRAPPER_TEST_DIR="0"

cleanup() {
  docker rm -f "${WAIT_CONTAINER_NAME}" >/dev/null 2>&1 || true
  docker rm -f "${LOGIN_CONTAINER_NAME}" >/dev/null 2>&1 || true
  docker rm -f "${RETRY_CONTAINER_NAME}" >/dev/null 2>&1 || true
  if [[ "${AUTO_WRAPPER_DATA_DIR}" == "1" && -n "${HOST_WRAPPER_DATA_DIR}" && -d "${HOST_WRAPPER_DATA_DIR}" ]]; then
    rm -rf "${HOST_WRAPPER_DATA_DIR}" >/dev/null 2>&1 || true
  fi
  if [[ "${AUTO_WRAPPER_SESSION_DIR}" == "1" && -n "${HOST_WRAPPER_SESSION_DIR}" && -d "${HOST_WRAPPER_SESSION_DIR}" ]]; then
    rm -rf "${HOST_WRAPPER_SESSION_DIR}" >/dev/null 2>&1 || true
  fi
  if [[ "${AUTO_WRAPPER_TEST_DIR}" == "1" && -n "${HOST_WRAPPER_TEST_DIR}" && -d "${HOST_WRAPPER_TEST_DIR}" ]]; then
    rm -rf "${HOST_WRAPPER_TEST_DIR}" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

docker_run() {
  docker run --platform "${PLATFORM}" "$@"
}

if [[ -z "${HOST_WRAPPER_DATA_DIR}" ]]; then
  HOST_WRAPPER_DATA_DIR="$(mktemp -d -t apple-wrapper-data-XXXXXX)"
  AUTO_WRAPPER_DATA_DIR="1"
fi

if [[ -z "${HOST_WRAPPER_SESSION_DIR}" ]]; then
  HOST_WRAPPER_SESSION_DIR="$(mktemp -d -t apple-wrapper-session-XXXXXX)"
  AUTO_WRAPPER_SESSION_DIR="1"
fi

if [[ -z "${HOST_WRAPPER_TEST_DIR}" ]]; then
  HOST_WRAPPER_TEST_DIR="$(mktemp -d -t apple-wrapper-test-XXXXXX)"
  AUTO_WRAPPER_TEST_DIR="1"
fi

mkdir -p "${HOST_WRAPPER_DATA_DIR}" "${HOST_WRAPPER_SESSION_DIR}"
chmod 0777 "${HOST_WRAPPER_DATA_DIR}" "${HOST_WRAPPER_SESSION_DIR}"
chmod 0777 "${HOST_WRAPPER_TEST_DIR}"

echo "[1/5] Verifying wrapper image filesystem contract..."
docker_run --rm --entrypoint /bin/sh "${IMAGE}" -c '
set -e
test -x /opt/apple-wrapper/wrapper
test -x /opt/apple-wrapper/rootfs/system/bin/main
test -x /opt/apple-wrapper/entrypoint.sh
test -c /opt/apple-wrapper/rootfs/dev/null
test -c /opt/apple-wrapper/rootfs/dev/zero
test -c /opt/apple-wrapper/rootfs/dev/random
test -c /opt/apple-wrapper/rootfs/dev/urandom
test -c /opt/apple-wrapper/rootfs/dev/tty
test -c /opt/apple-wrapper/rootfs/dev/ptmx
test -s /opt/apple-wrapper/rootfs/system/usr/share/zoneinfo/tzdata
test -s /opt/apple-wrapper/rootfs/apex/com.android.tzdata/etc/tz/tzdata
test -f /opt/apple-wrapper/rootfs/system/usr/share/zoneinfo/UTC
test -f /opt/apple-wrapper/rootfs/etc/localtime
'

echo "[2/5] Verifying idle startup without login/session..."
docker_run -d \
  --name "${WAIT_CONTAINER_NAME}" \
  --network host \
  -v "${HOST_WRAPPER_DATA_DIR}:/opt/apple-wrapper/data" \
  -v "${HOST_WRAPPER_SESSION_DIR}:/opt/apple-wrapper/rootfs/data/data/com.apple.android.music" \
  "${IMAGE}" >/dev/null

sleep 5
if ! docker ps --format '{{.Names}}' | grep -Fxq "${WAIT_CONTAINER_NAME}"; then
  echo "Wrapper idle smoke failed. Logs:" >&2
  docker logs "${WAIT_CONTAINER_NAME}" || true
  exit 1
fi

idle_logs="$(docker logs "${WAIT_CONTAINER_NAME}" 2>&1 || true)"
if ! grep -Eq 'no Apple session cache found|provide WRAPPER_LOGIN|still waiting for Apple session/login credentials' <<< "${idle_logs}"; then
  echo "Wrapper idle smoke failed: waiting-state log not found." >&2
  printf '%s\n' "${idle_logs}" >&2
  exit 1
fi

docker rm -f "${WAIT_CONTAINER_NAME}" >/dev/null 2>&1 || true

echo "[3/5] Verifying shared-file login handoff and wrapper launch contract..."
docker_run -d \
  --name "${LOGIN_CONTAINER_NAME}" \
  --network host \
  -e WRAPPER_LOGIN_RETRY_ATTEMPTS=0 \
  -v "${HOST_WRAPPER_DATA_DIR}:/opt/apple-wrapper/data" \
  -v "${HOST_WRAPPER_SESSION_DIR}:/opt/apple-wrapper/rootfs/data/data/com.apple.android.music" \
  -v /dev/urandom:/opt/apple-wrapper/rootfs/dev/urandom:ro \
  -v /dev/random:/opt/apple-wrapper/rootfs/dev/random:ro \
  "${IMAGE}" >/dev/null

sleep 5
printf '%s' 'parity@example.com:not-a-real-password' > "${HOST_WRAPPER_DATA_DIR}/wrapper-login.txt"

sleep 15
login_logs="$(docker logs "${LOGIN_CONTAINER_NAME}" 2>&1 || true)"
if ! grep -Eq 'login credentials detected; starting wrapper login flow.' <<< "${login_logs}"; then
  echo "Wrapper launch smoke failed: shared-file login handoff was not detected." >&2
  printf '%s\n' "${login_logs}" >&2
  exit 1
fi
if ! grep -Eq '\[\+\] starting|\[\+\] initializing ctx|\[\+\] logging in' <<< "${login_logs}"; then
  echo "Wrapper launch smoke failed: wrapper did not reach startup/login path after shared-file handoff." >&2
  printf '%s\n' "${login_logs}" >&2
  exit 1
fi
if grep -Eq 'required file missing|unable to create character device|unable to read .*/urandom|__bionic_open_tzdata' <<< "${login_logs}"; then
  echo "Wrapper launch smoke failed: runtime portability error detected." >&2
  printf '%s\n' "${login_logs}" >&2
  exit 1
fi

docker rm -f "${LOGIN_CONTAINER_NAME}" >/dev/null 2>&1 || true
rm -f "${HOST_WRAPPER_DATA_DIR}/wrapper-login.txt"

cat > "${HOST_WRAPPER_TEST_DIR}/fake-wrapper.sh" <<'EOF'
#!/usr/bin/env sh
set -eu
state_file="/tmp/fake-wrapper-attempt"
attempt="0"
if [ -f "$state_file" ]; then
  attempt="$(cat "$state_file")"
fi
attempt=$((attempt + 1))
printf '%s\n' "$attempt" > "$state_file"
echo "[+] starting..." >&2
echo "[+] initializing ctx..." >&2
echo "[+] logging in..." >&2
if [ "$attempt" -eq 1 ]; then
  echo "[.] response type 4" >&2
  echo "[!] login failed" >&2
  exit 1
fi
echo "[.] returning account info" >&2
exit 0
EOF
chmod 755 "${HOST_WRAPPER_TEST_DIR}/fake-wrapper.sh"

echo "[4/5] Verifying transient login retry coverage..."
retry_logs="$(
  docker_run --rm \
    --name "${RETRY_CONTAINER_NAME}" \
    --network host \
    -e WRAPPER_LOGIN="parity@example.com:not-a-real-password" \
    -e WRAPPER_LOGIN_RETRY_ATTEMPTS=1 \
    -e WRAPPER_LOGIN_RETRY_DELAY_SECONDS=1 \
    -e WRAPPER_EXECUTABLE="/tmp/fake-wrapper.sh" \
    -v "${HOST_WRAPPER_DATA_DIR}:/opt/apple-wrapper/data" \
    -v "${HOST_WRAPPER_SESSION_DIR}:/opt/apple-wrapper/rootfs/data/data/com.apple.android.music" \
    -v "${HOST_WRAPPER_TEST_DIR}/fake-wrapper.sh:/tmp/fake-wrapper.sh:ro" \
    -v /dev/urandom:/opt/apple-wrapper/rootfs/dev/urandom:ro \
    -v /dev/random:/opt/apple-wrapper/rootfs/dev/random:ro \
    "${IMAGE}" 2>&1
)"

if ! grep -Eq 'detected transient Apple login failure; retrying wrapper login \(1/1\) in 1s' <<< "${retry_logs}"; then
  echo "Wrapper retry smoke failed: transient retry log not found." >&2
  printf '%s\n' "${retry_logs}" >&2
  exit 1
fi
if [[ "$(grep -F -c '[+] logging in...' <<< "${retry_logs}")" -lt 2 ]]; then
  echo "Wrapper retry smoke failed: wrapper did not launch a second login attempt." >&2
  printf '%s\n' "${retry_logs}" >&2
  exit 1
fi
if ! grep -Fq '[.] response type 4' <<< "${retry_logs}"; then
  echo "Wrapper retry smoke failed: simulated transient failure was not observed." >&2
  printf '%s\n' "${retry_logs}" >&2
  exit 1
fi
if ! grep -Fq '[.] returning account info' <<< "${retry_logs}"; then
  echo "Wrapper retry smoke failed: retry path did not recover." >&2
  printf '%s\n' "${retry_logs}" >&2
  exit 1
fi

echo "[5/5] Wrapper smoke audit passed."
