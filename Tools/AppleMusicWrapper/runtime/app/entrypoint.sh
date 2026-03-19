#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="/opt/apple-wrapper"
ROOTFS_DIR="$ROOT_DIR/rootfs"
DEV_DIR="$ROOTFS_DIR/dev"
DEFAULT_WRAPPER_ARGS="-H 0.0.0.0 -D 10020 -M 20020 -A 30020"
LOGIN_FILE_DEFAULT="/opt/apple-wrapper/data/wrapper-login.txt"

log() {
  printf '[entrypoint] %s\n' "$*" >&2
}

require_file() {
  local path="$1"
  if [[ ! -f "$path" ]]; then
    log "required file missing: $path"
    exit 1
  fi
}

resolve_wrapper_args() {
  local args_from_env="${WRAPPER_ARGS:-}"
  local args_legacy="${args:-}"
  local resolved="${args_from_env:-$args_legacy}"
  if [[ -z "$resolved" ]]; then
    resolved="$DEFAULT_WRAPPER_ARGS"
  fi
  printf '%s' "$resolved"
}

wrapper_args_include_login() {
  local args_string="$1"
  [[ "$args_string" =~ (^|[[:space:]])-L([[:space:]]|$) ]] \
    || [[ "$args_string" =~ (^|[[:space:]])--login= ]] \
    || [[ "$args_string" =~ (^|[[:space:]])--login([[:space:]]|$) ]]
}

args_include_code_from_file() {
  local args_string="$1"
  [[ "$args_string" =~ (^|[[:space:]])-F([[:space:]]|$) ]] \
    || [[ "$args_string" =~ (^|[[:space:]])--code-from-file([[:space:]]|$) ]]
}

has_cached_session() {
  local session_root="$ROOTFS_DIR/data/data/com.apple.android.music/files"
  [[ -s "$session_root/IC-Info.sido" ]] || [[ -s "$session_root/MUSIC_TOKEN" ]]
}

append_login_args() {
  local base_args="$1"
  local creds="$2"
  local use_code_file="${WRAPPER_CODE_FROM_FILE:-1}"
  local effective="$base_args"
  effective="$effective -L $creds"
  if [[ "$use_code_file" != "0" ]] && ! args_include_code_from_file "$effective"; then
    effective="$effective -F"
  fi
  printf '%s' "$effective"
}

read_login_credentials() {
  local creds_env="${WRAPPER_LOGIN:-}"
  if [[ -n "$creds_env" ]]; then
    printf '%s' "$creds_env"
    return 0
  fi

  local login_file="${WRAPPER_LOGIN_FILE:-$LOGIN_FILE_DEFAULT}"
  if [[ -f "$login_file" ]]; then
    local creds_file=""
    creds_file="$(tr -d '\r\n' < "$login_file")"
    if [[ -n "$creds_file" ]]; then
      # Consume credentials once to avoid login/2FA loops when the container restarts.
      # Set WRAPPER_LOGIN_FILE_PERSIST=1 only if you explicitly want persistent auto-login.
      if [[ "${WRAPPER_LOGIN_FILE_PERSIST:-0}" != "1" ]]; then
        rm -f "$login_file" || true
      fi
      printf '%s' "$creds_file"
      return 0
    fi
  fi

  return 1
}

wait_for_startup_inputs() {
  local base_args="$1"
  local wait_seconds=0
  local last_notice=0

  log "no Apple session cache found; wrapper will wait for login/session instead of exiting."
  log "provide WRAPPER_LOGIN=user:pass or write credentials to ${WRAPPER_LOGIN_FILE:-$LOGIN_FILE_DEFAULT}"

  while true; do
    if has_cached_session; then
      log "session cache detected; starting wrapper in normal mode."
      printf '%s' "$base_args"
      return 0
    fi

    local login_creds=""
    if login_creds="$(read_login_credentials)"; then
      log "login credentials detected; starting wrapper login flow."
      append_login_args "$base_args" "$login_creds"
      return 0
    fi

    if (( wait_seconds - last_notice >= 30 )); then
      log "still waiting for Apple session/login credentials..."
      last_notice=$wait_seconds
    fi
    sleep 2
    wait_seconds=$((wait_seconds + 2))
  done
}

ensure_runtime_layout() {
  mkdir -p \
    "$DEV_DIR" \
    "$ROOTFS_DIR/data/data/com.apple.android.music/files" \
    "$ROOT_DIR/data"
}

ensure_char_device() {
  local path="$1"
  local major="$2"
  local minor="$3"

  if [[ -e "$path" ]]; then
    return 0
  fi

  if ! mknod -m 666 "$path" c "$major" "$minor" 2>/dev/null; then
    log "unable to create character device: $path"
    log "mount host device (for example /dev/urandom) or run with sufficient capabilities."
    exit 70
  fi
}

verify_urandom_readable() {
  if ! dd if="$DEV_DIR/urandom" of=/dev/null bs=1 count=1 status=none 2>/dev/null; then
    log "unable to read $DEV_DIR/urandom"
    exit 71
  fi
}

main() {
  require_file "$ROOT_DIR/wrapper"
  require_file "$ROOTFS_DIR/system/bin/main"

  ensure_runtime_layout

  # Keep runtime deterministic across Linux hosts where mknod behavior differs.
  ensure_char_device "$DEV_DIR/null" 1 3
  ensure_char_device "$DEV_DIR/zero" 1 5
  ensure_char_device "$DEV_DIR/random" 1 8
  ensure_char_device "$DEV_DIR/urandom" 1 9
  ensure_char_device "$DEV_DIR/tty" 5 0
  ensure_char_device "$DEV_DIR/ptmx" 5 2
  verify_urandom_readable

  export ANDROID_DATA="${ANDROID_DATA:-/data}"
  export ANDROID_ROOT="${ANDROID_ROOT:-/system}"

  local wrapper_args_string
  wrapper_args_string="$(resolve_wrapper_args)"

  if ! wrapper_args_include_login "$wrapper_args_string" && ! has_cached_session; then
    wrapper_args_string="$(wait_for_startup_inputs "$wrapper_args_string")"
  fi

  IFS=' ' read -r -a wrapper_args <<< "$wrapper_args_string"

  exec "$ROOT_DIR/wrapper" "${wrapper_args[@]}"
}

main "$@"
