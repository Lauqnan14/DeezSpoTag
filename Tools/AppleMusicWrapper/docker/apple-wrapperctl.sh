#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
COMPOSE_FILE="$REPO_ROOT/docker-compose.yml"
if [[ -n "${APPLE_WRAPPER_ENV_FILE:-}" ]]; then
  if [[ "$APPLE_WRAPPER_ENV_FILE" = /* ]]; then
    ENV_FILE="$APPLE_WRAPPER_ENV_FILE"
  else
    ENV_FILE="$REPO_ROOT/$APPLE_WRAPPER_ENV_FILE"
  fi
else
  ENV_FILE="$SCRIPT_DIR/apple-wrapper.env"
fi
SERVICE="apple-wrapper"

cmd="${1:-}"
shift || true

compose() {
  if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    docker compose -f "$COMPOSE_FILE" "$@"
    return
  fi

  if command -v docker-compose >/dev/null 2>&1; then
    docker-compose -f "$COMPOSE_FILE" "$@"
    return
  fi

  echo "Docker Compose CLI is unavailable. Install docker compose plugin or docker-compose." >&2
  exit 127
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

write_temp_wrapper_env_file() {
  local args_value="$1"
  local temp_file
  local escaped
  temp_file="$(mktemp "${TMPDIR:-/tmp}/apple-wrapper-env.XXXXXX")"
  escaped="$(escape_env_value "$args_value")"
  chmod 600 "$temp_file"
  {
    printf 'WRAPPER_ARGS="%s"\n' "$escaped"
    printf 'args="%s"\n' "$escaped"
  } > "$temp_file"
  printf '%s' "$temp_file"
}

resolve_container_id() {
  compose ps -q "$SERVICE" 2>/dev/null || true
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
  logs="$(compose logs --no-color --tail 400 "$SERVICE" 2>&1 || true)"
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
      echo "Usage: apple-wrapperctl.sh login user:pass" >&2
      exit 1
    fi
    creds="$1"
    if [[ "$creds" != *:* ]]; then
      echo "Credentials must be in username:password format." >&2
      exit 1
    fi
    if [[ "$creds" =~ [[:space:]] ]]; then
      echo "Credentials must not contain whitespace." >&2
      exit 1
    fi
    cid="$(resolve_container_id)"
    session_volume="$(resolve_mount_volume "$cid" "/opt/apple-wrapper/rootfs/data/data/com.apple.android.music")"
    data_volume="$(resolve_mount_volume "$cid" "/opt/apple-wrapper/data")"

    if [[ -z "$session_volume" ]]; then
      session_volume="$(resolve_volume_by_suffix "apple_wrapper_session")"
    fi
    if [[ -z "$data_volume" ]]; then
      data_volume="$(resolve_volume_by_suffix "apple_wrapper_data")"
    fi

    # Force a clean auth baseline so stale wrapper tokens cannot mask invalid credentials.
    compose stop "$SERVICE" >/dev/null || true
    clear_volume_contents "$session_volume"
    clear_volume_contents "$data_volume"

    base_args="-H 0.0.0.0 -D 10020 -M 20020 -A 30020"
    login_args="$base_args -L $creds -F"
    write_wrapper_env_file "$base_args"
    temp_env="$(write_temp_wrapper_env_file "$login_args")"
    trap 'rm -f "$temp_env"' EXIT
    APPLE_WRAPPER_ENV_FILE="$temp_env" compose up -d --no-deps --force-recreate "$SERVICE"
    # Login attempts must be one-shot. If credentials are invalid, the wrapper exits quickly;
    # keeping restart=unless-stopped would replay -L/-F in a loop and spam Apple auth attempts.
    cid="$(resolve_container_id)"
    if [[ -n "$cid" ]]; then
      docker update --restart=no "$cid" >/dev/null 2>&1 || true
    fi
    rm -f "$temp_env"
    trap - EXIT
    write_wrapper_env_file "$base_args"
    ;;
  run)
    # If the container is already running, leave it alone to avoid re-authentication
    # which triggers Apple 2FA codes.
    if compose ps --status running "$SERVICE" 2>/dev/null | grep -q "$SERVICE"; then
      echo "Wrapper container already running."
      exit 0
    fi

    cid="$(resolve_container_id)"
    session_volume="$(resolve_mount_volume "$cid" "/opt/apple-wrapper/rootfs/data/data/com.apple.android.music")"
    if [[ -z "$session_volume" ]]; then
      session_volume="$(resolve_volume_by_suffix "apple_wrapper_session")"
    fi

    if ! volume_has_file "$session_volume" "files/IC-Info.sido"; then
      echo "No active Apple Music wrapper session cache found (missing IC-Info.sido). Run login first." >&2
      exit 1
    fi

    write_wrapper_env_file "-H 0.0.0.0 -D 10020 -M 20020 -A 30020"
    compose up -d --no-deps "$SERVICE"
    ;;
  twofactor|2fa)
    if [[ $# -lt 1 ]]; then
      echo "Usage: apple-wrapperctl.sh 2fa 123456" >&2
      exit 1
    fi
    code="$1"
    if ! compose ps --status running "$SERVICE" 2>/dev/null | grep -q "$SERVICE"; then
      echo "Wrapper container is not running." >&2
      exit 1
    fi

    probe_state="$(probe_twofactor_state || true)"
    if [[ "$probe_state" != "waiting_for_2fa" ]]; then
      echo "Wrapper is not currently waiting for a 2FA code." >&2
      exit 1
    fi

    if ! compose exec -T -e APPLE_2FA_CODE="$code" "$SERVICE" sh -lc \
      'set -e; \
       target="/opt/apple-wrapper/rootfs/data/data/com.apple.android.music/files/2fa.txt"; \
       mkdir -p "$(dirname "$target")"; \
       printf "%s" "$APPLE_2FA_CODE" > "$target"'; then
      echo "Failed to submit 2FA code to wrapper container." >&2
      exit 1
    fi
    ;;
  sanitize)
    # After successful login, strip credentials from env and recreate the container
    # so Docker restarts no longer replay -L/-F login flags.
    # Session data persists on the apple_wrapper_session volume.
    write_wrapper_env_file "-H 0.0.0.0 -D 10020 -M 20020 -A 30020"
    compose up -d --no-deps --force-recreate "$SERVICE"
    ;;
  logout)
    # Clear persisted Apple session data so status becomes logged out.
    cid="$(resolve_container_id)"
    session_volume="$(resolve_mount_volume "$cid" "/opt/apple-wrapper/rootfs/data/data/com.apple.android.music")"
    data_volume="$(resolve_mount_volume "$cid" "/opt/apple-wrapper/data")"
    if [[ -z "$session_volume" ]]; then
      session_volume="$(resolve_volume_by_suffix "apple_wrapper_session")"
    fi
    if [[ -z "$data_volume" ]]; then
      data_volume="$(resolve_volume_by_suffix "apple_wrapper_data")"
    fi

    write_wrapper_env_file "-H 0.0.0.0 -D 10020 -M 20020 -A 30020"

    compose stop "$SERVICE" >/dev/null || true
    clear_volume_contents "$session_volume"
    clear_volume_contents "$data_volume"
    ;;
  status)
    compose ps "$SERVICE"
    ;;
  probe-2fa|probe2fa)
    probe_state="$(probe_twofactor_state || true)"
    if [[ -z "$probe_state" ]]; then
      echo "probe_failed" >&2
      exit 1
    fi
    echo "$probe_state"
    exit 0
    ;;
  logs)
    compose logs -f "$SERVICE"
    ;;
  *)
    echo "Usage: apple-wrapperctl.sh {login user:pass|run|2fa code|sanitize|logout|status|probe-2fa|logs}" >&2
    exit 1
    ;;
esac
