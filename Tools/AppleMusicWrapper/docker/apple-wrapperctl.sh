#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
if [[ -n "${APPLE_WRAPPER_COMPOSE_FILE:-}" ]]; then
  if [[ "$APPLE_WRAPPER_COMPOSE_FILE" = /* ]]; then
    COMPOSE_FILE="$APPLE_WRAPPER_COMPOSE_FILE"
  else
    COMPOSE_FILE="$REPO_ROOT/$APPLE_WRAPPER_COMPOSE_FILE"
  fi
else
  COMPOSE_FILE="$REPO_ROOT/docker-compose.yml"
fi

if [[ -n "${APPLE_WRAPPER_ENV_FILE:-}" ]]; then
  if [[ "$APPLE_WRAPPER_ENV_FILE" = /* ]]; then
    ENV_FILE="$APPLE_WRAPPER_ENV_FILE"
  else
    ENV_FILE="$REPO_ROOT/$APPLE_WRAPPER_ENV_FILE"
  fi
else
  if [[ -w "$SCRIPT_DIR" ]]; then
    ENV_FILE="$SCRIPT_DIR/apple-wrapper.env"
  else
    ENV_FILE="${TMPDIR:-/tmp}/apple-wrapper.env"
  fi
fi

SERVICE="apple-wrapper"
CONTAINER_NAME="${APPLE_WRAPPER_CONTAINER_NAME:-$SERVICE}"

cmd="${1:-}"
shift || true

has_docker_compose_plugin() {
  command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1
}

has_docker_compose_v1() {
  command -v docker-compose >/dev/null 2>&1
}

is_running_in_container() {
  if [[ -f "/.dockerenv" ]]; then
    return 0
  fi

  grep -qaE '(docker|containerd|kubepods|podman)' /proc/1/cgroup 2>/dev/null
}

use_compose_mode() {
  if [[ "${APPLE_WRAPPER_DISABLE_COMPOSE:-0}" == "1" ]]; then
    return 1
  fi

  if is_running_in_container && [[ "${APPLE_WRAPPER_ALLOW_COMPOSE_IN_CONTAINER:-0}" != "1" ]]; then
    return 1
  fi

  [[ -f "$COMPOSE_FILE" ]] && (has_docker_compose_plugin || has_docker_compose_v1)
}

compose() {
  if ! use_compose_mode; then
    echo "docker-compose mode unavailable (compose file not found at $COMPOSE_FILE)." >&2
    return 1
  fi

  if has_docker_compose_plugin; then
    docker compose -f "$COMPOSE_FILE" "$@"
    return
  fi

  if has_docker_compose_v1; then
    docker-compose -f "$COMPOSE_FILE" "$@"
    return
  fi

  echo "Docker Compose CLI is unavailable. Install docker compose plugin or docker-compose." >&2
  return 127
}

service_logs_tail() {
  local tail_lines="$1"
  if use_compose_mode; then
    compose logs --no-color --tail "$tail_lines" "$SERVICE" 2>&1 || true
  else
    docker logs --tail "$tail_lines" "$CONTAINER_NAME" 2>&1 || true
  fi
}

resolve_container_id() {
  if use_compose_mode; then
    local compose_cid=""
    compose_cid="$(compose ps -q "$SERVICE" 2>/dev/null || true)"
    if [[ -n "$compose_cid" ]]; then
      printf '%s' "$compose_cid"
      return
    fi
  fi

  docker ps -aq --filter "name=^${CONTAINER_NAME}$" | head -n 1
}

ensure_container_present() {
  local cid
  cid="$(resolve_container_id)"
  if [[ -z "$cid" ]]; then
    echo "Wrapper container '$CONTAINER_NAME' not found. Start it once first." >&2
    exit 1
  fi
  printf '%s' "$cid"
}

is_container_running() {
  local cid="$1"
  if [[ -z "$cid" ]]; then
    return 1
  fi

  [[ "$(docker inspect --format '{{.State.Running}}' "$cid" 2>/dev/null || true)" == "true" ]]
}

service_running() {
  local cid
  cid="$(resolve_container_id)"
  is_container_running "$cid"
}

service_stop() {
  if use_compose_mode; then
    compose stop "$SERVICE" >/dev/null || true
    return
  fi

  docker stop "$CONTAINER_NAME" >/dev/null 2>&1 || true
}

service_start() {
  if use_compose_mode; then
    compose up -d --no-deps "$SERVICE" >/dev/null
    return
  fi

  ensure_container_present >/dev/null
  docker start "$CONTAINER_NAME" >/dev/null 2>&1 || true
}

service_restart() {
  service_stop
  service_start
}

escape_env_value() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  printf '%s' "$value"
}

write_wrapper_env_file() {
  local args_value="$1"
  local escaped
  local temp_file
  escaped="$(escape_env_value "$args_value")"
  mkdir -p "$(dirname "$ENV_FILE")"
  temp_file="$(mktemp "${ENV_FILE}.XXXXXX")"
  chmod 600 "$temp_file"
  {
    printf 'WRAPPER_ARGS="%s"\n' "$escaped"
    printf 'args="%s"\n' "$escaped"
  } > "$temp_file"
  mv "$temp_file" "$ENV_FILE"
  chmod 600 "$ENV_FILE"
}

resolve_mount_volume() {
  local cid="$1"
  local destination="$2"
  if [[ -z "$cid" ]]; then
    return 0
  fi
  docker inspect "$cid" --format "{{range .Mounts}}{{if eq .Destination \"$destination\"}}{{.Name}}{{end}}{{end}}" 2>/dev/null || true
}

resolve_volume_by_suffix() {
  local suffix="$1"
  local selected=""
  local count=0

  while IFS= read -r name; do
    if [[ -z "$name" ]]; then
      continue
    fi
    selected="$name"
    count=$((count + 1))
  done < <(docker volume ls --format '{{.Name}}' 2>/dev/null | grep -E "(^|_)${suffix}$" || true)

  if [[ "$count" -eq 1 ]]; then
    printf '%s' "$selected"
  fi
}

clear_volume_contents() {
  local volume_name="$1"
  if [[ -z "$volume_name" ]]; then
    return 0
  fi

  docker run --rm -v "${volume_name}:/target" alpine:3.20 sh -lc \
    'if [ -d /target ]; then find /target -mindepth 1 -maxdepth 1 -exec rm -rf {} +; fi' >/dev/null
}

clear_container_paths() {
  local cid="$1"
  docker run --rm --volumes-from "$cid" alpine:3.20 sh -lc '
    set -e
    for target in \
      /opt/apple-wrapper/rootfs/data/data/com.apple.android.music \
      /opt/apple-wrapper/data
    do
      if [ -d "$target" ]; then
        find "$target" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
      fi
    done
  ' >/dev/null
}

write_container_login_file() {
  local cid="$1"
  local username="$2"
  local password="$3"
  docker run --rm --volumes-from "$cid" \
    -e APPLE_WRAPPER_LOGIN_USER="$username" \
    -e APPLE_WRAPPER_LOGIN_PASS="$password" \
    alpine:3.20 sh -lc '
    set -e
    user_b64="$(printf "%s" "$APPLE_WRAPPER_LOGIN_USER" | base64 | tr -d "\n")"
    pass_b64="$(printf "%s" "$APPLE_WRAPPER_LOGIN_PASS" | base64 | tr -d "\n")"
    mkdir -p /opt/apple-wrapper/data
    {
      printf "email_b64=%s\n" "$user_b64"
      printf "password_b64=%s\n" "$pass_b64"
    } > /opt/apple-wrapper/data/wrapper-login.txt
  ' >/dev/null
}

remove_container_login_file() {
  local cid="$1"
  docker run --rm --volumes-from "$cid" alpine:3.20 sh -lc '
    rm -f /opt/apple-wrapper/data/wrapper-login.txt
  ' >/dev/null
}

container_has_file() {
  local cid="$1"
  local file_path="$2"
  docker run --rm --volumes-from "$cid" -e CHECK_PATH="$file_path" alpine:3.20 sh -lc \
    '[ -f "$CHECK_PATH" ]' >/dev/null 2>&1
}

volume_has_file() {
  local volume_name="$1"
  local relative_path="$2"
  if [[ -z "$volume_name" || -z "$relative_path" ]]; then
    return 1
  fi

  docker run --rm -v "${volume_name}:/target" alpine:3.20 sh -lc \
    "[ -f \"/target/${relative_path}\" ]" >/dev/null 2>&1
}

probe_twofactor_state() {
  local logs wait_idx settle_idx
  logs="$(service_logs_tail 400)"
  if [[ -z "$logs" ]]; then
    return 1
  fi

  wait_idx="$(printf '%s\n' "$logs" | nl -ba | grep -E "2FA: true|Enter your 2FA code|Waiting for input" | tail -n 1 | awk '{print $1}' || true)"
  settle_idx="$(printf '%s\n' "$logs" | nl -ba | grep -E "Code file detected|account info cached successfully|login failed|response type (0|1|2|3|5|6|7|8|9)" | tail -n 1 | awk '{print $1}' || true)"

  if [[ -n "$wait_idx" ]]; then
    if [[ -z "$settle_idx" ]] || (( wait_idx > settle_idx )); then
      echo "waiting_for_2fa"
      return 0
    fi
  fi

  echo "not_waiting_for_2fa"
  return 0
}

case "$cmd" in
  login)
    if [[ $# -lt 1 ]]; then
      echo "Usage: apple-wrapperctl.sh login user:pass | apple-wrapperctl.sh login user pass" >&2
      exit 1
    fi
    username=""
    password=""
    if [[ $# -eq 1 ]]; then
      creds="$1"
      if [[ "$creds" != *:* ]]; then
        echo "Credentials must be in username:password format when a single argument is used." >&2
        exit 1
      fi
      username="${creds%%:*}"
      password="${creds#*:}"
    else
      if [[ $# -ne 2 ]]; then
        echo "Usage: apple-wrapperctl.sh login user pass" >&2
        exit 1
      fi
      username="$1"
      password="$2"
    fi
    if [[ -z "$username" || -z "$password" ]]; then
      echo "Credentials must include both username and password." >&2
      exit 1
    fi

    base_args="-H 0.0.0.0 -D 10020 -M 20020 -A 30020"

    if use_compose_mode; then
      cid="$(resolve_container_id)"
      service_stop
      if [[ -n "$cid" ]]; then
        clear_container_paths "$cid"
      fi

      write_wrapper_env_file "$base_args"
      compose up -d --no-deps --force-recreate "$SERVICE"

      cid="$(resolve_container_id)"
      if [[ -z "$cid" ]]; then
        echo "Wrapper container '$CONTAINER_NAME' not found after compose recreate." >&2
        exit 1
      fi

      write_container_login_file "$cid" "$username" "$password"
      exit 0
    fi

    cid="$(ensure_container_present)"
    service_stop
    clear_container_paths "$cid"
    write_container_login_file "$cid" "$username" "$password"
    service_start
    ;;

  run)
    if service_running; then
      echo "Wrapper container already running."
      exit 0
    fi

    cid="$(ensure_container_present)"

    if use_compose_mode; then
      has_session_cache=0
      has_queued_login=0
      if container_has_file "$cid" "/opt/apple-wrapper/rootfs/data/data/com.apple.android.music/files/IC-Info.sido"; then
        has_session_cache=1
      fi
      if container_has_file "$cid" "/opt/apple-wrapper/data/wrapper-login.txt"; then
        has_queued_login=1
      fi

      if [[ "$has_session_cache" -eq 0 && "$has_queued_login" -eq 0 ]]; then
        echo "No active Apple Music wrapper session cache found (missing IC-Info.sido) and no queued login payload found (missing wrapper-login.txt). Run login first." >&2
        exit 1
      fi

      write_wrapper_env_file "-H 0.0.0.0 -D 10020 -M 20020 -A 30020"
      if [[ "$has_queued_login" -eq 0 ]]; then
        remove_container_login_file "$cid"
      fi
      compose up -d --no-deps "$SERVICE"
      exit 0
    fi

    has_session_cache=0
    has_queued_login=0
    if container_has_file "$cid" "/opt/apple-wrapper/rootfs/data/data/com.apple.android.music/files/IC-Info.sido"; then
      has_session_cache=1
    fi
    if container_has_file "$cid" "/opt/apple-wrapper/data/wrapper-login.txt"; then
      has_queued_login=1
    fi

    if [[ "$has_session_cache" -eq 0 && "$has_queued_login" -eq 0 ]]; then
      echo "No active Apple Music wrapper session cache found (missing IC-Info.sido) and no queued login payload found (missing wrapper-login.txt). Run login first." >&2
      exit 1
    fi

    if [[ "$has_queued_login" -eq 0 ]]; then
      remove_container_login_file "$cid"
    fi
    service_start
    ;;

  twofactor|2fa)
    if [[ $# -lt 1 ]]; then
      echo "Usage: apple-wrapperctl.sh 2fa 123456" >&2
      exit 1
    fi
    code="$1"
    if ! service_running; then
      echo "Wrapper container is not running." >&2
      exit 1
    fi

    probe_state="$(probe_twofactor_state || true)"
    if [[ "$probe_state" != "waiting_for_2fa" ]]; then
      echo "Wrapper is not currently waiting for a 2FA code." >&2
      exit 1
    fi

    if use_compose_mode; then
      if ! compose exec -T -e APPLE_2FA_CODE="$code" "$SERVICE" sh -lc \
        'set -e; target="/opt/apple-wrapper/rootfs/data/data/com.apple.android.music/files/2fa.txt"; mkdir -p "$(dirname "$target")"; printf "%s" "$APPLE_2FA_CODE" > "$target"'; then
        echo "Failed to submit 2FA code to wrapper container." >&2
        exit 1
      fi
      exit 0
    fi

    if ! docker exec -i -e APPLE_2FA_CODE="$code" "$CONTAINER_NAME" sh -lc \
      'set -e; target="/opt/apple-wrapper/rootfs/data/data/com.apple.android.music/files/2fa.txt"; mkdir -p "$(dirname "$target")"; printf "%s" "$APPLE_2FA_CODE" > "$target"'; then
      echo "Failed to submit 2FA code to wrapper container." >&2
      exit 1
    fi
    ;;

  sanitize)
    if use_compose_mode; then
      write_wrapper_env_file "-H 0.0.0.0 -D 10020 -M 20020 -A 30020"
      compose up -d --no-deps --force-recreate "$SERVICE"
      exit 0
    fi

    cid="$(ensure_container_present)"
    remove_container_login_file "$cid"
    if service_running; then
      service_restart
    fi
    ;;

  logout)
    cid="$(resolve_container_id)"
    if [[ -z "$cid" ]]; then
      echo "Wrapper container '$CONTAINER_NAME' not found." >&2
      exit 1
    fi

    if use_compose_mode; then
      write_wrapper_env_file "-H 0.0.0.0 -D 10020 -M 20020 -A 30020"
      service_stop
      clear_container_paths "$cid"
      remove_container_login_file "$cid"
      exit 0
    fi

    service_stop
    clear_container_paths "$cid"
    remove_container_login_file "$cid"
    ;;

  status)
    if use_compose_mode; then
      compose ps "$SERVICE"
    else
      docker ps -a --filter "name=^${CONTAINER_NAME}$"
    fi
    ;;

  probe-2fa|probe2fa)
    probe_state="$(probe_twofactor_state || true)"
    if [[ -z "$probe_state" ]]; then
      echo "probe_failed" >&2
      exit 1
    fi
    echo "$probe_state"
    ;;

  logs)
    if use_compose_mode; then
      compose logs -f "$SERVICE"
    else
      docker logs -f "$CONTAINER_NAME"
    fi
    ;;

  *)
    echo "Usage: apple-wrapperctl.sh {login user:pass|login user pass|run|2fa code|sanitize|logout|status|probe-2fa|logs}" >&2
    exit 1
    ;;
esac
