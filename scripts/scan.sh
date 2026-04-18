#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  ./scan.sh [options]
  scripts/scan.sh [options]

Options:
  -k, --project-key <key>   SonarQube project key (default: DeezSpoTag)
  -u, --host-url <url>      SonarQube host URL (default: http://localhost:9000)
  -t, --token <token>       SonarQube token (default: SONAR_TOKEN env var)
  -s, --solution <path>     Solution/project path (default: ./DeezSpoTag.Web/DeezSpoTag.Web.csproj)
  -c, --config <cfg>        Build config (default: Debug)
      --lightweight         Exclude JS/TS/HTML/CSS web assets from scan
      --begin-arg <arg>     Extra begin arg (repeatable)
      --build-arg <arg>     Extra dotnet build arg (repeatable)
  -h, --help                Show this help

Environment fallbacks:
  SONAR_PROJECT_KEY
  SONAR_HOST_URL
  SONAR_TOKEN
  SONAR_PYTHON_VERSION
  BUILD_CONFIG
  SONAR_EXCLUSIONS
  SONAR_CPD_EXCLUSIONS
  SONAR_LIGHTWEIGHT
  SONAR_INCLUDE_TESTS
  SONAR_INCLUDE_COVERAGE
  SONAR_COVERAGE_EXCLUSIONS
  SONAR_KEEP_LOCAL_SCAN_STATE
  SONAR_ALLOW_HIGH_MEMORY_PRESSURE

Example:
  SONAR_TOKEN=xxxx ./scan.sh
  SONAR_TOKEN=xxxx ./scan.sh --lightweight
  ./scan.sh -t xxxx -k DeezSpoTag -u http://localhost:9000
EOF
}

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

project_key="${SONAR_PROJECT_KEY:-DeezSpoTag}"
host_url="${SONAR_HOST_URL:-http://localhost:9000}"
token="${SONAR_TOKEN:-}"
solution_path="${ROOT_DIR}/DeezSpoTag.Web/DeezSpoTag.Web.csproj"
build_config="${BUILD_CONFIG:-Debug}"
sonar_python_version="${SONAR_PYTHON_VERSION:-}"
sonar_exclusions="${SONAR_EXCLUSIONS:-}"
sonar_cpd_exclusions="${SONAR_CPD_EXCLUSIONS:-}"
sonar_scan_all="${SONAR_SCAN_ALL:-false}"
sonar_lightweight="${SONAR_LIGHTWEIGHT:-false}"
sonar_include_tests="${SONAR_INCLUDE_TESTS:-false}"
sonar_include_coverage="${SONAR_INCLUDE_COVERAGE:-true}"
sonar_coverage_exclusions="${SONAR_COVERAGE_EXCLUSIONS:-**/DeezSpoTag.Tests/**,**/DeezSpoTag.CoverPortTests/**,**/Tools/**,**/References/**,**/bin/**,**/obj/**}"
sonar_keep_local_scan_state="${SONAR_KEEP_LOCAL_SCAN_STATE:-false}"
sonar_allow_high_memory_pressure="${SONAR_ALLOW_HIGH_MEMORY_PRESSURE:-false}"
coverage_dir="${ROOT_DIR}/.sonar-coverage"
coverage_opencover_reports_path="${coverage_dir}/**/coverage.opencover.xml"
scan_lock_file="${ROOT_DIR}/.scan.lock"

cleanup_local_scan_state() {
  rm -rf "${ROOT_DIR}/.sonarqube" "$coverage_dir"
}

if [[ "$sonar_keep_local_scan_state" != "true" ]]; then
  trap cleanup_local_scan_state EXIT
fi

acquire_scan_lock() {
  if ! command -v flock >/dev/null 2>&1; then
    echo "Warning: flock not found; skipping scan lock." >&2
    return
  fi

  exec 9>"$scan_lock_file"
  if ! flock -n 9; then
    echo "Another Sonar scan is already running for this workspace." >&2
    echo "Wait for it to finish, then rerun ./scan.sh." >&2
    exit 1
  fi
}

declare -a coverage_projects=()
if [[ "$sonar_include_coverage" == "true" ]]; then
  coverage_projects=(
    "${ROOT_DIR}/DeezSpoTag.Tests/DeezSpoTag.Tests.csproj"
    "${ROOT_DIR}/DeezSpoTag.CoverPortTests/DeezSpoTag.CoverPortTests.csproj"
  )
fi

declare -a extra_begin_args=()
declare -a extra_build_args=()
declare -a default_sonar_exclusions=(
  "**/.sonarqube/**"
  "**/.gocache/**"
  "**/.venv/**"
  "**/venv/**"
  "**/__pycache__/**"
  "**/*.pyc"
  "**/site-packages/**"
  "**/lib/python*/site-packages/**"
  "**/lib64/python*/site-packages/**"
  "**/build/**"
  "**/.playwright/**"
  "**/node_modules/**"
  "**/bin/**"
  "**/obj/**"
  "**/TestResults/**"
  "**/test-results/**"
  "**/coverage-report/**"
  "**/reports/**"
  "**/*Tests*/**"
  "**/DeezSpoTag.Tests/**"
  "**/DeezSpoTag.CoverPortTests/**"
  "**/meloday-main/**"
  "**/scripts/spotify/**"
  "**/smb_/**"
  "**/Music Video Downloads/**"
  "**/Data/analysis/**"
  "**/Data/apple-music/**"
  "**/Data/apple-music-test/**"
  "**/Data/apple-wrapper/**"
  "**/Data/autotag/**"
  "**/Data/deezspotag/**"
  "**/Data/downloads/**"
  "**/Data/library/**"
  "**/Data/library-artist-images/**"
  "**/Data/library-thumbs/**"
  "**/Data/logs/**"
  "**/Data/meloday/**"
  "**/Data/playlist-covers/**"
  "**/Data/playlist-visuals/**"
  "**/Data/runtime/**"
  "**/Data/security/**"
  "**/Data/spotify/**"
  "**/Data/*.db"
  "**/Data/*.json"
)
declare -a default_sonar_cpd_generated_exclusions=(
  "**/*_pb2.py"
  "**/*_pb2.pyi"
)
declare -a lightweight_sonar_exclusions=(
  "**/*.js"
  "**/*.jsx"
  "**/*.ts"
  "**/*.tsx"
  "**/*.html"
  "**/*.css"
  "**/*.cshtml"
)

while [[ $# -gt 0 ]]; do
  case "$1" in
    -k|--project-key)
      [[ $# -ge 2 ]] || { echo "Missing value for $1" >&2; exit 2; }
      project_key="$2"
      shift 2
      ;;
    -u|--host-url)
      [[ $# -ge 2 ]] || { echo "Missing value for $1" >&2; exit 2; }
      host_url="$2"
      shift 2
      ;;
    -t|--token)
      [[ $# -ge 2 ]] || { echo "Missing value for $1" >&2; exit 2; }
      token="$2"
      shift 2
      ;;
    -s|--solution)
      [[ $# -ge 2 ]] || { echo "Missing value for $1" >&2; exit 2; }
      solution_path="$2"
      shift 2
      ;;
    -c|--config)
      [[ $# -ge 2 ]] || { echo "Missing value for $1" >&2; exit 2; }
      build_config="$2"
      shift 2
      ;;
    --lightweight)
      sonar_lightweight="true"
      shift
      ;;
    --begin-arg)
      [[ $# -ge 2 ]] || { echo "Missing value for $1" >&2; exit 2; }
      extra_begin_args+=("$2")
      shift 2
      ;;
    --build-arg)
      [[ $# -ge 2 ]] || { echo "Missing value for $1" >&2; exit 2; }
      extra_build_args+=("$2")
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage
      exit 2
      ;;
  esac
done

default_sonar_exclusions_csv="$(IFS=,; echo "${default_sonar_exclusions[*]}")"
default_sonar_cpd_generated_exclusions_csv="$(IFS=,; echo "${default_sonar_cpd_generated_exclusions[*]}")"
lightweight_sonar_exclusions_csv="$(IFS=,; echo "${lightweight_sonar_exclusions[*]}")"
if [[ -z "$sonar_exclusions" ]]; then
  sonar_exclusions="$default_sonar_exclusions_csv"
else
  sonar_exclusions="${default_sonar_exclusions_csv},${sonar_exclusions}"
fi

if [[ "$sonar_lightweight" == "true" ]]; then
  sonar_exclusions="${sonar_exclusions},${lightweight_sonar_exclusions_csv}"
fi

if [[ -z "$sonar_cpd_exclusions" ]]; then
  sonar_cpd_exclusions="$sonar_exclusions"
  if [[ -n "$default_sonar_cpd_generated_exclusions_csv" ]]; then
    sonar_cpd_exclusions="${sonar_cpd_exclusions},${default_sonar_cpd_generated_exclusions_csv}"
  fi
fi

if [[ -z "$sonar_python_version" ]] && command -v python3 >/dev/null 2>&1; then
  sonar_python_version="$(python3 -c 'import sys; print(f"{sys.version_info.major}.{sys.version_info.minor}")')"
fi

if [[ -z "$token" ]]; then
  echo "SONAR_TOKEN is required. Set SONAR_TOKEN or pass --token." >&2
  exit 1
fi

if [[ ! -f "$solution_path" ]]; then
  echo "Solution file not found: $solution_path" >&2
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is required but not found in PATH." >&2
  exit 1
fi

run_scanner() {
  if command -v dotnet-sonarscanner >/dev/null 2>&1; then
    dotnet-sonarscanner "$@"
    return
  fi

  if dotnet sonarscanner --help >/dev/null 2>&1; then
    dotnet sonarscanner "$@"
    return
  fi

  echo "SonarScanner for .NET not found." >&2
  echo "Install with: dotnet tool install --global dotnet-sonarscanner" >&2
  exit 1
}

check_sonar_server() {
  if ! command -v curl >/dev/null 2>&1; then
    echo "curl not found; skipping SonarQube reachability check." >&2
    return
  fi

  local status_url="${host_url%/}/api/system/status"
  if ! curl --silent --show-error --fail --max-time 10 "$status_url" >/dev/null; then
    echo "SonarQube is not reachable at $host_url" >&2
    echo "Start the server first, then rerun the scan." >&2
    exit 1
  fi
}

extract_ce_task_id_from_log() {
  local log_file="$1"
  grep -Eo 'api/ce/task\?id=[A-Za-z0-9-]+' "$log_file" | tail -n1 | sed -E 's/.*id=//' || true
}

fetch_current_ce_task_id() {
  if ! command -v curl >/dev/null 2>&1; then
    return 1
  fi

  local ce_url="${host_url%/}/api/ce/component"
  local response
  if ! response="$(
    curl --silent --show-error --fail --max-time 20 \
      -u "${token}:" \
      --get "$ce_url" \
      --data-urlencode "component=${project_key}"
  )"; then
    return 1
  fi

  printf '%s' "$response" | sed -n 's/.*"current":{[^}]*"id":"\([^"]*\)".*/\1/p' | head -n1
}

wait_for_ce_task_completion() {
  local task_id="$1"

  if ! command -v curl >/dev/null 2>&1; then
    echo "curl not found; cannot wait for SonarQube processing." >&2
    return 1
  fi

  local task_url="${host_url%/}/api/ce/task"
  local attempt response status
  echo "Waiting for SonarQube report processing (task: $task_id)..."

  for ((attempt = 1; attempt <= 120; attempt++)); do
    if ! response="$(
      curl --silent --show-error --fail --max-time 20 \
        -u "${token}:" \
        --get "$task_url" \
        --data-urlencode "id=${task_id}"
    )"; then
      echo "Failed to query CE task status (attempt $attempt/120)." >&2
      sleep 2
      continue
    fi

    status="$(printf '%s' "$response" | sed -n 's/.*"task":{[^}]*"status":"\([A-Z_]*\)".*/\1/p' | head -n1)"

    case "$status" in
      SUCCESS)
        echo "SonarQube report processing complete."
        return 0
        ;;
      FAILED|CANCELED)
        echo "SonarQube report processing ended with status: $status." >&2
        return 1
        ;;
      PENDING|IN_PROGRESS|"")
        ;;
      *)
        echo "Unexpected CE task status: $status" >&2
        ;;
    esac

    sleep 2
  done

  echo "Timed out waiting for SonarQube report processing (task: $task_id)." >&2
  return 1
}

check_quality_gate_status() {
  if ! command -v curl >/dev/null 2>&1; then
    echo "curl not found; cannot verify quality gate status." >&2
    return 1
  fi

  local gate_url="${host_url%/}/api/qualitygates/project_status"
  local response gate_status failing_conditions condition metric threshold actual
  if ! response="$(
    curl --silent --show-error --fail --max-time 20 \
      -u "${token}:" \
      --get "$gate_url" \
      --data-urlencode "projectKey=${project_key}"
  )"; then
    echo "Failed to query SonarQube quality gate status." >&2
    return 1
  fi

  gate_status="$(printf '%s' "$response" | sed -n 's/.*"projectStatus":{"status":"\([^"]*\)".*/\1/p' | head -n1)"
  if [[ "$gate_status" == "OK" ]]; then
    echo "Quality Gate: PASSED"
    return 0
  fi

  echo "Quality Gate: FAILED (status: ${gate_status:-unknown})." >&2
  failing_conditions="$(printf '%s' "$response" | grep -Eo '\{"status":"ERROR","metricKey":"[^"]+","comparator":"[^"]+","errorThreshold":"[^"]+","actualValue":"[^"]*"\}' || true)"

  if [[ -n "$failing_conditions" ]]; then
    while IFS= read -r condition; do
      metric="$(printf '%s' "$condition" | sed -n 's/.*"metricKey":"\([^"]*\)".*/\1/p')"
      threshold="$(printf '%s' "$condition" | sed -n 's/.*"errorThreshold":"\([^"]*\)".*/\1/p')"
      actual="$(printf '%s' "$condition" | sed -n 's/.*"actualValue":"\([^"]*\)".*/\1/p')"
      echo "  - ${metric}: actual=${actual}, threshold=${threshold}" >&2
    done <<< "$failing_conditions"
  else
    echo "  Unable to parse failing conditions from SonarQube response." >&2
  fi

  return 1
}

check_system_resources() {
  if [[ ! -r /proc/meminfo ]]; then
    return
  fi

  local mem_available_kb mem_total_kb swap_total_kb swap_free_kb swap_used_pct
  mem_available_kb="$(awk '/MemAvailable:/ { print $2 }' /proc/meminfo)"
  mem_total_kb="$(awk '/MemTotal:/ { print $2 }' /proc/meminfo)"
  swap_total_kb="$(awk '/SwapTotal:/ { print $2 }' /proc/meminfo)"
  swap_free_kb="$(awk '/SwapFree:/ { print $2 }' /proc/meminfo)"

  if [[ -z "$mem_available_kb" || -z "$mem_total_kb" ]]; then
    return
  fi

  if [[ "${swap_total_kb:-0}" -gt 0 ]]; then
    swap_used_pct="$(( (100 * (swap_total_kb - swap_free_kb)) / swap_total_kb ))"
  else
    swap_used_pct=0
  fi

  local min_mem_available_kb=$((4 * 1024 * 1024))
  local severe_mem_available_kb=$((2 * 1024 * 1024))

  if [[ "$mem_available_kb" -lt "$severe_mem_available_kb" || ( "$swap_used_pct" -ge 95 && "$mem_available_kb" -lt "$min_mem_available_kb" ) ]]; then
    if [[ "$sonar_lightweight" == "true" || "$sonar_allow_high_memory_pressure" == "true" ]]; then
      echo "Local system is under heavy memory pressure." >&2
      echo "MemAvailable: $((mem_available_kb / 1024)) MiB" >&2
      echo "Swap used   : ${swap_used_pct}%" >&2
      if [[ "$sonar_lightweight" == "true" ]]; then
        echo "Proceeding because lightweight mode is enabled." >&2
      else
        echo "Proceeding because SONAR_ALLOW_HIGH_MEMORY_PRESSURE=true." >&2
      fi
      return
    fi

    echo "Local system is under heavy memory pressure." >&2
    echo "MemAvailable: $((mem_available_kb / 1024)) MiB" >&2
    echo "Swap used   : ${swap_used_pct}%" >&2
    echo "Abort full scan. Free memory or rerun with ./scan.sh --lightweight." >&2
    exit 1
  fi

  if [[ "$mem_available_kb" -lt "$min_mem_available_kb" || "$swap_used_pct" -ge 65 ]]; then
    echo "Warning: local memory pressure is elevated." >&2
    echo "MemAvailable: $((mem_available_kb / 1024)) MiB" >&2
    echo "Swap used   : ${swap_used_pct}%" >&2
    if [[ "$sonar_lightweight" != "true" ]]; then
      echo "Consider rerunning with ./scan.sh --lightweight if Sonar JS analysis is unstable." >&2
    fi
  fi
}

restore_legacy_platform_auth_placeholder() {
  local workers_data_dir="${DEEZSPOTAG_DATA_DIR:-${ROOT_DIR}/DeezSpoTag.Workers/Data}"
  local legacy_auth_dir="${workers_data_dir}/deezspotag/autotag"
  local legacy_auth_file="${legacy_auth_dir}/platform-auth.json"

  mkdir -p "$legacy_auth_dir"
  if [[ ! -f "$legacy_auth_file" ]]; then
    printf '{}\n' > "$legacy_auth_file"
  fi
}

echo "Starting SonarQube scan"
echo "Project key : $project_key"
echo "Host URL    : $host_url"
echo "Solution    : $solution_path"
echo "Build config: $build_config"
echo "Python version: ${sonar_python_version:-unset}"
echo "Scan all    : $sonar_scan_all"
echo "Lightweight : $sonar_lightweight"
echo "Include tests: $sonar_include_tests"
echo "Include coverage: $sonar_include_coverage"
echo "Coverage exclusions: $sonar_coverage_exclusions"
echo "Keep local scan state: $sonar_keep_local_scan_state"
echo "Allow high memory pressure: $sonar_allow_high_memory_pressure"
echo "Entry point : ./scan.sh"

check_sonar_server
check_system_resources
acquire_scan_lock

cleanup_local_scan_state

declare -a begin_args=(
  /k:"$project_key"
  /d:sonar.host.url="$host_url"
  /d:sonar.token="$token"
  /d:sonar.projectBaseDir="$ROOT_DIR"
  /d:sonar.scanner.scanAll="$sonar_scan_all"
  /d:sonar.exclusions="$sonar_exclusions"
  /d:sonar.cpd.exclusions="$sonar_cpd_exclusions"
  /d:sonar.coverage.exclusions="$sonar_coverage_exclusions"
)

if [[ -n "$sonar_python_version" ]]; then
  begin_args+=(/d:sonar.python.version="$sonar_python_version")
fi

if [[ "$sonar_include_coverage" == "true" ]]; then
  begin_args+=(/d:sonar.cs.opencover.reportsPaths="$coverage_opencover_reports_path")
fi

run_scanner begin "${begin_args[@]}" "${extra_begin_args[@]}"

if ! dotnet build "$solution_path" -c "$build_config" "${extra_build_args[@]}"; then
  echo "Build failed; finalizing scan." >&2
  run_scanner end /d:sonar.token="$token" || true
  exit 1
fi

rm -rf "$coverage_dir"

if [[ "$sonar_include_coverage" == "true" ]]; then
  mkdir -p "$coverage_dir"
  collected_reports=0
  for coverage_project in "${coverage_projects[@]}"; do
    if [[ ! -f "$coverage_project" ]]; then
      echo "Coverage project not found, skipping: $coverage_project" >&2
      continue
    fi

    test_name="$(basename "$coverage_project" .csproj)"
    test_output_dir="${coverage_dir}/${test_name}"
    mkdir -p "$test_output_dir"

    # Prefer OpenCover from coverlet.collector because Sonar C# ingests it more reliably.
    if ! dotnet test "$coverage_project" -c "$build_config" \
      --results-directory "$test_output_dir" \
      --collect:"XPlat Code Coverage" \
      -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover; then
      echo "OpenCover collection failed for $coverage_project, skipping." >&2
      continue
    fi

    opencover_count="$(find "$test_output_dir" -type f -name 'coverage.opencover.xml' | wc -l | tr -d ' ')"
    if [[ "${opencover_count:-0}" -eq 0 ]]; then
      echo "No OpenCover report produced for $coverage_project; skipping." >&2
      continue
    fi

    collected_reports=$((collected_reports + opencover_count))
  done

  if [[ "${collected_reports:-0}" -eq 0 ]]; then
    echo "No OpenCover coverage reports were collected from any configured test project." >&2
    run_scanner end /d:sonar.token="$token" || true
    exit 1
  fi
else
  echo "Skipping coverage collection (SONAR_INCLUDE_COVERAGE=false)."
fi

restore_legacy_platform_auth_placeholder

end_log_file="$(mktemp)"
if ! run_scanner end /d:sonar.token="$token" 2>&1 | tee "$end_log_file"; then
  rm -f "$end_log_file"
  echo "Sonar scanner end step failed." >&2
  exit 1
fi

ce_task_id="$(extract_ce_task_id_from_log "$end_log_file")"
rm -f "$end_log_file"

if [[ -z "$ce_task_id" ]]; then
  ce_task_id="$(fetch_current_ce_task_id || true)"
fi

if [[ -z "$ce_task_id" ]]; then
  echo "Unable to determine SonarQube report task id; cannot verify quality gate." >&2
  exit 1
fi

wait_for_ce_task_completion "$ce_task_id"
check_quality_gate_status

echo "SonarQube scan complete."
