#!/usr/bin/env bash
set -euo pipefail

ENV_FILE="/etc/default/apple-wrapper"
LOGIN_ENV_FILE="/run/apple-wrapper-login.env"
SERVICE="apple-wrapper.service"
BASE_ARGS="-H 127.0.0.1 -D 10020 -M 20020 -A 30020"

if [[ $EUID -ne 0 ]]; then
  echo "Run as root (sudo)." >&2
  exit 1
fi

cmd="${1:-}"
shift || true

escape_env_value() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  printf '%s' "$value"
}

write_wrapper_env_file() {
  local target="$1"
  local args_value="$2"
  local escaped
  local temp_file
  escaped="$(escape_env_value "$args_value")"
  mkdir -p "$(dirname "$target")"
  temp_file="$(mktemp "${target}.XXXXXX")"
  chmod 600 "$temp_file"
  {
    printf 'WRAPPER_ARGS="%s"\n' "$escaped"
    printf 'args="%s"\n' "$escaped"
  } > "$temp_file"
  mv "$temp_file" "$target"
  chmod 600 "$target"
}

clear_login_env_file() {
  rm -f "$LOGIN_ENV_FILE"
}

case "$cmd" in
  login)
    if [[ $# -lt 1 ]]; then
      echo "Usage: sudo apple-wrapperctl.sh login user:pass" >&2
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
    write_wrapper_env_file "$ENV_FILE" "$BASE_ARGS"
    write_wrapper_env_file "$LOGIN_ENV_FILE" "$BASE_ARGS -L $creds -F"
    systemctl restart "$SERVICE"
    ;;
  run)
    write_wrapper_env_file "$ENV_FILE" "$BASE_ARGS"
    clear_login_env_file
    systemctl restart "$SERVICE"
    ;;
  twofactor|2fa)
    if [[ $# -lt 1 ]]; then
      echo "Usage: sudo apple-wrapperctl.sh 2fa 123456" >&2
      exit 1
    fi
    code="$1"
    umask 077
    mkdir -p /opt/apple-wrapper/rootfs/data /opt/apple-wrapper/rootfs/data/data/com.apple.android.music/files
    printf "%s" "$code" > /opt/apple-wrapper/rootfs/data/2fa.txt
    printf "%s" "$code" > /opt/apple-wrapper/rootfs/data/code.txt
    printf "%s" "$code" > /opt/apple-wrapper/rootfs/data/data/com.apple.android.music/files/2fa.txt
    ;;
  sanitize)
    write_wrapper_env_file "$ENV_FILE" "$BASE_ARGS"
    clear_login_env_file
    systemctl restart "$SERVICE"
    ;;
  logout)
    write_wrapper_env_file "$ENV_FILE" "$BASE_ARGS"
    clear_login_env_file
    systemctl stop "$SERVICE" || true
    session_dir="/opt/apple-wrapper/rootfs/data/data/com.apple.android.music"
    data_dir="/opt/apple-wrapper/data"
    mkdir -p "$session_dir"
    find "$session_dir" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
    if [[ -d "$data_dir" ]]; then
      find "$data_dir" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
    fi
    systemctl restart "$SERVICE"
    ;;
  status)
    systemctl status "$SERVICE" --no-pager
    ;;
  logs)
    journalctl -u "$SERVICE" -n 200 --no-pager
    ;;
  *)
    echo "Usage: sudo apple-wrapperctl.sh {login user:pass|run|2fa code|sanitize|logout|status|logs}" >&2
    exit 1
    ;;
esac
