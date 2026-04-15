#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

MODE="${GUARDRAILS_MODE:-changed}" # changed|full
BASE_REF="${GUARDRAILS_BASE_REF:-origin/main}"
BUILD_CONFIG="${GUARDRAILS_BUILD_CONFIG:-Debug}"

resolve_test_framework() {
  if [[ -n "${GUARDRAILS_TEST_TFM:-}" ]]; then
    printf '%s' "$GUARDRAILS_TEST_TFM"
    return
  fi

  local tests_csproj="DeezSpoTag.Tests/DeezSpoTag.Tests.csproj"
  if [[ -f "$tests_csproj" ]]; then
    local target_framework
    target_framework="$(sed -n 's:.*<TargetFramework>\(.*\)</TargetFramework>.*:\1:p' "$tests_csproj" | head -n1)"
    if [[ -z "$target_framework" ]]; then
      local target_frameworks
      target_frameworks="$(sed -n 's:.*<TargetFrameworks>\(.*\)</TargetFrameworks>.*:\1:p' "$tests_csproj" | head -n1)"
      target_framework="${target_frameworks%%;*}"
    fi

    if [[ -n "$target_framework" ]]; then
      printf '%s' "$target_framework"
      return
    fi
  fi

  printf 'net10.0'
}

TEST_FRAMEWORK="$(resolve_test_framework)"

log() {
  printf '[guardrails] %s\n' "$*"
}

have_changes_matching() {
  local pattern="$1"
  printf '%s\n' "$CHANGED_FILES" | grep -Eq "$pattern"
}

run_test_filter() {
  local label="$1"
  local filter="$2"
  log "Running ${label}..."
  dotnet test DeezSpoTag.Tests/DeezSpoTag.Tests.csproj \
    -c "$BUILD_CONFIG" \
    -f "$TEST_FRAMEWORK" \
    --filter "$filter" \
    --nologo \
    --verbosity minimal
}

resolve_diff_base() {
  if [[ -n "${GUARDRAILS_BASE_SHA:-}" ]]; then
    printf '%s' "$GUARDRAILS_BASE_SHA"
    return
  fi

  if git rev-parse --verify "$BASE_REF" >/dev/null 2>&1; then
    git merge-base HEAD "$BASE_REF"
    return
  fi

  if git rev-parse --verify HEAD~1 >/dev/null 2>&1; then
    git rev-parse HEAD~1
    return
  fi

  git rev-parse HEAD
}

BASE_SHA="$(resolve_diff_base)"
if [[ "$MODE" == "full" ]]; then
  CHANGED_FILES="$(git ls-files)"
else
  CHANGED_FILES="$(git diff --name-only "$BASE_SHA"...HEAD)"
fi

if [[ -z "$CHANGED_FILES" ]]; then
  log "No changed files detected; running lightweight build check."
  dotnet build DeezSpoTag.Web/DeezSpoTag.Web.csproj -c "$BUILD_CONFIG" --nologo
  log "Guardrails passed."
  exit 0
fi

log "Base: ${BASE_SHA}"
log "Mode: ${MODE}"
changed_count="$(printf '%s\n' "$CHANGED_FILES" | sed '/^$/d' | wc -l | tr -d ' ')"
log "Changed files count: ${changed_count}"
if [[ "$changed_count" -le 200 ]]; then
  printf '%s\n' "$CHANGED_FILES" | sed 's/^/  - /'
else
  log "Changed file list omitted (too large); first 40 entries:"
  printf '%s\n' "$CHANGED_FILES" | awk 'NR <= 40 { print "  - " $0 }'
fi

log "Running build gate..."
dotnet build DeezSpoTag.Web/DeezSpoTag.Web.csproj -c "$BUILD_CONFIG" --nologo

run_all_tests=0
run_download_suite=0
run_library_suite=0
run_spotify_suite=0

if [[ "$MODE" == "full" ]]; then
  run_all_tests=1
else
  if have_changes_matching '^DeezSpoTag\.Tests/'; then
    run_all_tests=1
  fi

  if have_changes_matching '^DeezSpoTag\.Services/Download/' \
    || have_changes_matching '^DeezSpoTag\.Web/Services/Download' \
    || have_changes_matching '^DeezSpoTag\.Web/Services/.*(Qobuz|Tidal|Apple|Deezer|Spotify).*(Download|Queue)' \
    || have_changes_matching '^DeezSpoTag\.Web/Services/DownloadIntentService\.cs' ; then
    run_download_suite=1
  fi

  if have_changes_matching '^DeezSpoTag\.Services/Library/' \
    || have_changes_matching '^DeezSpoTag\.Web/Services/Library' \
    || have_changes_matching '^DeezSpoTag\.Web/Controllers/Api/Library' \
    || have_changes_matching '^DeezSpoTag\.Web/Services/LibrarySpotifyArtistQueueService\.cs' ; then
    run_library_suite=1
  fi

  if have_changes_matching '^DeezSpoTag\.Web/Services/SpotifyArtistService\.cs' \
    || have_changes_matching '^DeezSpoTag\.Web/Services/Spotify.*' \
    || have_changes_matching '^DeezSpoTag\.Web/Controllers/Api/Spotify.*' ; then
    run_spotify_suite=1
  fi
fi

if [[ "$run_all_tests" -eq 1 ]]; then
  log "Running full test suite..."
  dotnet test DeezSpoTag.Tests/DeezSpoTag.Tests.csproj -c "$BUILD_CONFIG" -f "$TEST_FRAMEWORK" --nologo --verbosity minimal
else
  if [[ "$run_download_suite" -eq 1 ]]; then
    run_test_filter "download regression suite" \
      "FullyQualifiedName~QobuzQueueEndToEndTests|FullyQualifiedName~LyricsSettingsPolicyTests|FullyQualifiedName~TechnicalLyricsSettingsApplierTests|FullyQualifiedName~AppleAacVariantSelectionTests|FullyQualifiedName~AppleHlsManifestParserTests|FullyQualifiedName~ArtworkFallbackHelperTests"
  fi

  if [[ "$run_library_suite" -eq 1 ]]; then
    run_test_filter "library regression suite" \
      "FullyQualifiedName~LibraryRepositoryCoverageTests"
  fi

  if [[ "$run_spotify_suite" -eq 1 ]]; then
    run_test_filter "spotify matching regression suite" \
      "FullyQualifiedName~SpotifyArtistNameMatchingGuardrailTests"
  fi

  if [[ "$run_download_suite" -eq 0 && "$run_library_suite" -eq 0 && "$run_spotify_suite" -eq 0 ]]; then
    log "No mapped impact area; running smoke subset."
    run_test_filter "smoke suite" \
      "FullyQualifiedName~QobuzQueueEndToEndTests|FullyQualifiedName~LibraryRepositoryCoverageTests|FullyQualifiedName~SpotifyArtistNameMatchingGuardrailTests"
  fi
fi

log "Guardrails passed."
