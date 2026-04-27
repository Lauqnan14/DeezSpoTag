#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="/opt/apple-wrapper"
ROOTFS_DIR="$ROOT_DIR/rootfs"
DEV_DIR="$ROOTFS_DIR/dev"
WRAPPER_BINARY_DEFAULT="$ROOT_DIR/wrapper"
DEFAULT_WRAPPER_ARGS="-H 0.0.0.0 -D 10020 -M 20020 -A 30020"
LOGIN_FILE_DEFAULT="/opt/apple-wrapper/data/wrapper-login.txt"
LOGIN_ENV_MARKER_DEFAULT="/opt/apple-wrapper/data/.wrapper-login-env.sha256"
TWO_FACTOR_STATE_FILE_DEFAULT="/opt/apple-wrapper/data/wrapper-2fa-state.txt"
LOGOUT_FILE_DEFAULT="/opt/apple-wrapper/data/wrapper-logout.txt"
LOGOUT_STATE_FILE_DEFAULT="/opt/apple-wrapper/data/wrapper-logout-state.txt"
AUTH_ACTIVE_FILE_DEFAULT="/opt/apple-wrapper/data/wrapper-auth-active.txt"
STARTUP_LOGIN_CREDS=""

log() {
  printf '[entrypoint] %s\n' "$*" >&2
}

set_twofactor_state() {
  local state_file="$1"
  local state_value="$2"
  local parent_dir
  local tmp_file
  parent_dir="$(dirname "$state_file")"
  mkdir -p "$parent_dir"
  tmp_file="$(mktemp "$parent_dir/.wrapper-2fa-state.XXXXXX")"
  printf '%s\n' "$state_value" > "$tmp_file"
  mv -f "$tmp_file" "$state_file"
  chmod 644 "$state_file" || true
}

set_wrapper_runtime_flag() {
  local flag_file="$1"
  local value="$2"
  local parent_dir
  local tmp_file
  parent_dir="$(dirname "$flag_file")"
  mkdir -p "$parent_dir"
  tmp_file="$(mktemp "$parent_dir/.wrapper-flag.XXXXXX")"
  printf '%s\n' "$value" > "$tmp_file"
  mv -f "$tmp_file" "$flag_file"
  chmod 644 "$flag_file" || true
}

set_logout_state() {
  local state_file="$1"
  local request_id="$2"
  local parent_dir
  local tmp_file
  parent_dir="$(dirname "$state_file")"
  mkdir -p "$parent_dir"
  tmp_file="$(mktemp "$parent_dir/.wrapper-logout-state.XXXXXX")"
  printf '%s\n' "$request_id" > "$tmp_file"
  mv -f "$tmp_file" "$state_file"
  chmod 644 "$state_file" || true
}

clear_dir_contents() {
  local target_dir="$1"
  if [[ ! -d "$target_dir" ]]; then
    return 0
  fi

  find "$target_dir" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
}

process_shared_logout_request() {
  local logout_file="${WRAPPER_LOGOUT_FILE:-$LOGOUT_FILE_DEFAULT}"
  local logout_state_file="${WRAPPER_LOGOUT_STATE_FILE:-$LOGOUT_STATE_FILE_DEFAULT}"
  local login_file="${WRAPPER_LOGIN_FILE:-$LOGIN_FILE_DEFAULT}"
  local marker_file="${WRAPPER_LOGIN_ENV_MARKER_FILE:-$LOGIN_ENV_MARKER_DEFAULT}"
  local session_cache_dir="$ROOTFS_DIR/data/data/com.apple.android.music/files"
  local data_dir="$ROOT_DIR/data"
  local request_id=""

  if [[ ! -f "$logout_file" ]]; then
    return 1
  fi

  request_id="$(tr -d '\r\n' < "$logout_file" 2>/dev/null || true)"
  rm -f "$logout_file" || true

  clear_dir_contents "$session_cache_dir"
  clear_dir_contents "$data_dir"
  rm -f "$login_file" "$marker_file" || true

  if [[ -n "$request_id" ]]; then
    set_logout_state "$logout_state_file" "$request_id"
  fi

  log "processed shared logout request${request_id:+ ($request_id)}."
  return 0
}

require_file() {
  local path="$1"
  if [[ ! -f "$path" ]]; then
    log "required file missing: $path"
    exit 1
  fi
}

resolve_wrapper_binary() {
  local configured="${WRAPPER_EXECUTABLE:-${WRAPPER_BINARY:-}}"
  if [[ -n "$configured" ]]; then
    printf '%s' "$configured"
    return 0
  fi

  printf '%s' "$WRAPPER_BINARY_DEFAULT"
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

hash_credentials() {
  local credentials="$1"
  printf '%s' "$credentials" | sha256sum | awk '{print $1}'
}

consume_env_login_credentials() {
  local creds_env="$1"
  local marker_file="${WRAPPER_LOGIN_ENV_MARKER_FILE:-$LOGIN_ENV_MARKER_DEFAULT}"
  local marker_dir
  marker_dir="$(dirname "$marker_file")"
  mkdir -p "$marker_dir"

  local env_hash
  env_hash="$(hash_credentials "$creds_env")"

  if [[ -f "$marker_file" ]]; then
    local existing_hash=""
    existing_hash="$(tr -d '\r\n' < "$marker_file")"
    if [[ "$existing_hash" == "$env_hash" ]]; then
      return 1
    fi
  fi

  printf '%s' "$env_hash" > "$marker_file"
  chmod 600 "$marker_file" || true
  printf '%s' "$creds_env"
}

decode_base64_value() {
  local value="$1"
  printf '%s' "$value" | base64 -d 2>/dev/null
}

payload_uses_encoded_login_format() {
  local payload="$1"
  [[ "$payload" == *"email_b64="* ]] || [[ "$payload" == *"username_b64="* ]] || [[ "$payload" == *"password_b64="* ]]
}

parse_encoded_login_payload() {
  local payload="$1"
  local username_b64=""
  local password_b64=""

  while IFS='=' read -r raw_key raw_value; do
    local key="${raw_key//$'\r'/}"
    local value="${raw_value//$'\r'/}"
    case "$key" in
      email_b64|username_b64)
        username_b64="$value"
        ;;
      password_b64)
        password_b64="$value"
        ;;
    esac
  done <<< "$payload"

  if [[ -z "$username_b64" || -z "$password_b64" ]]; then
    return 1
  fi

  local username=""
  local password=""
  if ! username="$(decode_base64_value "$username_b64")"; then
    log "invalid wrapper login payload: unable to decode email/username."
    return 1
  fi
  if ! password="$(decode_base64_value "$password_b64")"; then
    log "invalid wrapper login payload: unable to decode password."
    return 1
  fi

  if [[ -z "$username" || -z "$password" ]]; then
    log "invalid wrapper login payload: decoded credentials are empty."
    return 1
  fi
  if [[ "$username" == *$'\n'* || "$username" == *$'\r'* || "$password" == *$'\n'* || "$password" == *$'\r'* ]]; then
    log "invalid wrapper login payload: newline characters are not allowed in credentials."
    return 1
  fi

  printf '%s:%s' "$username" "$password"
}

append_login_args_array() {
  local -n args_ref="$1"
  local existing_args_string="$2"
  local creds="$3"
  local use_code_file="${WRAPPER_CODE_FROM_FILE:-1}"

  args_ref+=("-L" "$creds")
  if [[ "$use_code_file" != "0" ]] && ! args_include_code_from_file "$existing_args_string"; then
    args_ref+=("-F")
  fi
}

read_login_credentials() {
  local creds_env="${WRAPPER_LOGIN:-}"
  if [[ -n "$creds_env" ]]; then
    local consumed_creds=""
    if consumed_creds="$(consume_env_login_credentials "$creds_env")"; then
      printf '%s' "$consumed_creds"
      return 0
    fi
  fi

  local login_file="${WRAPPER_LOGIN_FILE:-$LOGIN_FILE_DEFAULT}"
  if [[ -f "$login_file" ]]; then
    local payload=""
    payload="$(cat "$login_file" 2>/dev/null || true)"
    local creds_file=""
    if payload_uses_encoded_login_format "$payload"; then
      if ! creds_file="$(parse_encoded_login_payload "$payload")"; then
        rm -f "$login_file" || true
        return 1
      fi
    else
      creds_file="$(printf '%s' "$payload" | tr -d '\r\n')"
    fi

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
  local wait_seconds=0
  local last_notice=0
  local auth_active_file="${WRAPPER_AUTH_ACTIVE_FILE:-$AUTH_ACTIVE_FILE_DEFAULT}"
  STARTUP_LOGIN_CREDS=""
  set_wrapper_runtime_flag "$auth_active_file" "0"

  log "no Apple session cache found; wrapper will wait for login/session instead of exiting."
  log "provide WRAPPER_LOGIN=user:pass or write credentials to ${WRAPPER_LOGIN_FILE:-$LOGIN_FILE_DEFAULT}"

  while true; do
    process_shared_logout_request || true

    if has_cached_session; then
      log "session cache detected; starting wrapper in normal mode."
      return 0
    fi

    local login_creds=""
    if login_creds="$(read_login_credentials)"; then
      log "login credentials detected; starting wrapper login flow."
      STARTUP_LOGIN_CREDS="$login_creds"
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

ensure_timezone_payload() {
  local source_zoneinfo="/usr/share/zoneinfo"
  local rootfs_zoneinfo_dirs=(
    "$ROOTFS_DIR/usr/share/zoneinfo"
    "$ROOTFS_DIR/system/usr/share/zoneinfo"
  )
  local copied_any="0"

  if [[ ! -d "$source_zoneinfo" ]]; then
    return 0
  fi

  for zoneinfo_dir in "${rootfs_zoneinfo_dirs[@]}"; do
    if [[ -f "$zoneinfo_dir/UTC" ]]; then
      continue
    fi

    mkdir -p "$zoneinfo_dir"
    cp -a "$source_zoneinfo/." "$zoneinfo_dir/"
    copied_any="1"
  done

  mkdir -p "$ROOTFS_DIR/etc"
  if [[ ! -f "$ROOTFS_DIR/etc/localtime" && -f "$source_zoneinfo/UTC" ]]; then
    cp -f "$source_zoneinfo/UTC" "$ROOTFS_DIR/etc/localtime"
    copied_any="1"
  fi
  if [[ ! -f "$ROOTFS_DIR/etc/timezone" ]]; then
    printf 'UTC\n' > "$ROOTFS_DIR/etc/timezone"
    copied_any="1"
  fi

  if [[ "$copied_any" == "1" ]]; then
    log "hydrated timezone payload into wrapper rootfs."
  fi
}

run_wrapper_with_state_tracking() {
  local wrapper_binary="$1"
  local state_file="$2"
  local transient_flag_file="$3"
  local response_type_file="$4"
  local auth_active_file="$5"
  local login_attempt_active="$6"
  shift
  shift
  shift
  shift
  shift
  shift
  local exit_code=0

  set_twofactor_state "$state_file" "not_waiting_for_2fa"
  set_wrapper_runtime_flag "$transient_flag_file" "0"
  set_wrapper_runtime_flag "$response_type_file" ""
  set_wrapper_runtime_flag "$auth_active_file" "$login_attempt_active"

  "$wrapper_binary" "$@" 2> >(
    while IFS= read -r line; do
      printf '%s\n' "$line" >&2
      case "$line" in
        *"2FA: true"*|*"Enter your 2FA code"*|*"Waiting for input..."*)
          set_twofactor_state "$state_file" "waiting_for_2fa"
          ;;
        *"Code file detected"*|*"credentialHandler:"*"2FA: false"*|*"returning account info"*)
          set_twofactor_state "$state_file" "not_waiting_for_2fa"
          ;;
        *"Unable to perform this operation"*|*"network connection is unstable"*|*"system is busy"*|*"temporarily unavailable"*)
          set_wrapper_runtime_flag "$transient_flag_file" "1"
          ;;
        *"[.] response type "*)
          set_wrapper_runtime_flag "$response_type_file" "${line##*response type }"
          ;;
      esac
    done
  ) &
  local wrapper_pid=$!

  while kill -0 "$wrapper_pid" 2>/dev/null; do
    if process_shared_logout_request; then
      if kill -0 "$wrapper_pid" 2>/dev/null; then
        kill "$wrapper_pid" 2>/dev/null || true
      fi
      break
    fi
    sleep 0.2
  done

  if ! wait "$wrapper_pid"; then
    exit_code=$?
  fi

  set_twofactor_state "$state_file" "not_waiting_for_2fa"
  set_wrapper_runtime_flag "$auth_active_file" "0"
  return "$exit_code"
}

should_retry_login() {
  local transient_flag_file="$1"
  local response_type_file="$2"
  local transient_flag="0"
  local response_type=""

  if [[ -f "$transient_flag_file" ]]; then
    transient_flag="$(tr -d '\r\n' < "$transient_flag_file")"
  fi
  if [[ -f "$response_type_file" ]]; then
    response_type="$(tr -d '\r\n' < "$response_type_file")"
  fi

  # Apple occasionally returns transient "system busy/network unstable" dialogs,
  # often mapped to response type 4. These usually succeed on immediate retry.
  if [[ "$transient_flag" == "1" ]]; then
    return 0
  fi
  [[ "$response_type" == "4" ]]
}

main() {
  local wrapper_binary
  wrapper_binary="$(resolve_wrapper_binary)"

  require_file "$wrapper_binary"
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
  ensure_timezone_payload

  export ANDROID_DATA="${ANDROID_DATA:-/data}"
  export ANDROID_ROOT="${ANDROID_ROOT:-/system}"

  local two_factor_state_file
  two_factor_state_file="${WRAPPER_2FA_STATE_FILE:-$TWO_FACTOR_STATE_FILE_DEFAULT}"
  local transient_flag_file
  transient_flag_file="${WRAPPER_TRANSIENT_FLAG_FILE:-/tmp/wrapper-transient-state.txt}"
  local response_type_file
  response_type_file="${WRAPPER_RESPONSE_TYPE_FILE:-/tmp/wrapper-response-type.txt}"
  local retry_attempts
  retry_attempts="${WRAPPER_LOGIN_RETRY_ATTEMPTS:-2}"
  local retry_delay_seconds
  retry_delay_seconds="${WRAPPER_LOGIN_RETRY_DELAY_SECONDS:-4}"
  local exit_on_login_failure
  exit_on_login_failure="${WRAPPER_EXIT_ON_LOGIN_FAILURE:-0}"
  local auth_active_file
  auth_active_file="${WRAPPER_AUTH_ACTIVE_FILE:-$AUTH_ACTIVE_FILE_DEFAULT}"
  set_wrapper_runtime_flag "$auth_active_file" "0"

  while true; do
    local wrapper_args_string
    wrapper_args_string="$(resolve_wrapper_args)"

    local wrapper_has_login="0"
    if wrapper_args_include_login "$wrapper_args_string"; then
      wrapper_has_login="1"
    fi

    STARTUP_LOGIN_CREDS=""
    if [[ "$wrapper_has_login" != "1" ]] && ! has_cached_session; then
      wait_for_startup_inputs
      if [[ -n "$STARTUP_LOGIN_CREDS" ]]; then
        wrapper_has_login="1"
      fi
    fi

    IFS=' ' read -r -a wrapper_args <<< "$wrapper_args_string"
    if [[ -n "$STARTUP_LOGIN_CREDS" ]]; then
      append_login_args_array wrapper_args "$wrapper_args_string" "$STARTUP_LOGIN_CREDS"
    fi

    local attempt=0
    local exit_code=0
    while true; do
      set_wrapper_runtime_flag "$transient_flag_file" "0"
      set_wrapper_runtime_flag "$response_type_file" ""
      if run_wrapper_with_state_tracking "$wrapper_binary" "$two_factor_state_file" "$transient_flag_file" "$response_type_file" "$auth_active_file" "$wrapper_has_login" "${wrapper_args[@]}"; then
        exit_code=0
      else
        exit_code=$?
      fi

      if [[ "$exit_code" -eq 0 ]]; then
        return 0
      fi

      if [[ "$wrapper_has_login" != "1" ]]; then
        return "$exit_code"
      fi

      if (( attempt >= retry_attempts )); then
        break
      fi

      if should_retry_login "$transient_flag_file" "$response_type_file"; then
        attempt=$((attempt + 1))
        log "detected transient Apple login failure; retrying wrapper login (${attempt}/${retry_attempts}) in ${retry_delay_seconds}s..."
        sleep "$retry_delay_seconds"
        continue
      fi

      break
    done

    # In shared-login mode, return to waiting state after failed auth instead of
    # exiting and letting Docker restart-loop the container.
    if [[ "$wrapper_has_login" == "1" ]] && ! has_cached_session && [[ "$exit_on_login_failure" != "1" ]]; then
      log "wrapper login flow failed (exit=${exit_code}); waiting for fresh credentials/session instead of exiting."
      set_twofactor_state "$two_factor_state_file" "not_waiting_for_2fa"
      sleep 1
      continue
    fi

    return "$exit_code"
  done
}

main "$@"
