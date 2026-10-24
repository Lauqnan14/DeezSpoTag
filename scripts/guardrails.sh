#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

MODE="${GUARDRAILS_MODE:-changed}" # changed|full
BASE_REF="${GUARDRAILS_BASE_REF:-origin/main}"
BUILD_CONFIG="${GUARDRAILS_BUILD_CONFIG:-Debug}"
HEAD_SHA="${GUARDRAILS_HEAD_SHA:-HEAD}"
INCLUDE_WORKTREE="${GUARDRAILS_INCLUDE_WORKTREE:-1}"

case "$MODE" in changed|full) ;; *) echo "Invalid guardrails mode: $MODE" >&2; exit 1 ;; esac

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
  committed_changes="$(git diff --name-only "$BASE_SHA" "$HEAD_SHA")"
  unstaged_changes=""
  staged_changes=""
  untracked_changes=""
  if [[ "$INCLUDE_WORKTREE" == "1" ]]; then
    unstaged_changes="$(git diff --name-only)"
    staged_changes="$(git diff --cached --name-only)"
    untracked_changes="$(git ls-files --others --exclude-standard)"
  fi
  CHANGED_FILES="$(
    printf '%s\n%s\n%s\n%s\n' \
      "$committed_changes" \
      "$unstaged_changes" \
      "$staged_changes" \
      "$untracked_changes" \
      | sed '/^$/d' \
      | sort -u
  )"
fi

log "Base: ${BASE_SHA}"
log "Mode: ${MODE}"

# Map affected areas to existing test classes. A changed test selects its own class;
# changing an unmapped source requires adding a mapping, never an unrelated smoke run.
mapfile -t test_files < <(git ls-files --cached --others --exclude-standard 'DeezSpoTag.Tests/*Test.cs' | sort -u)
selected=()
unmapped=()
add_tests_matching() {
  local pattern="$1" test_file name
  for test_file in "${test_files[@]}"; do
    name="${test_file##*/}"
    name="${name%.cs}"
    if [[ "$name" =~ $pattern ]]; then selected+=("$name"); fi
  done
}

if [[ "$MODE" == "changed" ]]; then
  while IFS= read -r file; do
    [[ -z "$file" ]] && continue
    before=${#selected[@]}
    # Browser security tests cover escaping, URL validation, safe SVGs and handlers.
    # Keep the existing feature mappings below as well.
    case "$file" in
      DeezSpoTag.Web/Services/WatchlistEngine.cs)
        add_tests_matching '^PlaylistSyncTargetAggregationTest$' ;;
    esac
    case "$file" in
      DeezSpoTag.Web/wwwroot/js/auto-playlists.js|DeezSpoTag.Web/wwwroot/js/home-index.js|DeezSpoTag.Web/wwwroot/js/home-view-helpers.js|DeezSpoTag.Web/wwwroot/js/library-artist-hero.js|DeezSpoTag.Web/wwwroot/js/library-watchlists.js|DeezSpoTag.Web/wwwroot/js/library.js|DeezSpoTag.Web/wwwroot/js/lrc-editor.js|DeezSpoTag.Web/wwwroot/js/personal-genre.js|DeezSpoTag.Web/wwwroot/js/site.js)
        add_tests_matching '^SecurityHardeningGuardrailTest$' ;;
    esac
    case "$file" in
      DeezSpoTag.Tests/*Test.cs)
        # Removed tests have nothing left to execute; CI still checks the full suite.
        if [[ -f "$file" ]]; then
          name="${file##*/}"
          selected+=("${name%.cs}")
        fi
        continue ;;
      *.md|.gitignore|LICENSE*|docs/*|*.png|*.jpg|*.jpeg|*.webp|*.gif|*.svg|*.ico) continue ;;
      scripts/guardrails.sh|.githooks/pre-push|.github/workflows/*|.gitea/workflows/*|Dockerfile*)
        add_tests_matching '^PublishingWorkflowGuardrailTest$' ;;
      DeezSpoTag.Integrations/TargetPlaylistLookup.cs)
        add_tests_matching '^(PlaylistSyncEngine|PlaylistSyncLinkService|PlaylistSyncSchedule|PlaylistSyncReadiness|ArtistMetadataUpdaterReliability)Test$' ;;
      DeezSpoTag.Web/Services/PlaylistCandidateContract.cs)
        add_tests_matching '^(WatchlistDurabilityRepository|WatchlistIdentityResolverGuardrail|WatchlistRunCoordinatorHardening|WatchlistSettingsBehavior|MonitoredPlaylistPresentationGuardrail)Test$' ;;
      DeezSpoTag.Services/Download/DownloadSourceOrder.cs)
        add_tests_matching '^(DownloadSourceOrderFallbackParity|DownloadIntentFallbackParity|DownloadQualityOrderMonotonicity|EngineFallbackCoordinator|AtmosPipelineGuardrail|SoundCloudIntentAndFallback)Test$' ;;
      scripts/setup-youtube-playback-runtime.sh|DeezSpoTag.Web/DeezSpoTag.Web.csproj)
        add_tests_matching '^(YouTubePlaybackTest|PublishingWorkflowGuardrailTest)$' ;;
      DeezSpoTag.Web/Views/Search/Index.cshtml)
        add_tests_matching '^(YouTubeBrowseTest|AppleTracklistNativePlaybackGuardrailTest)$' ;;
      DeezSpoTag.Web/Views/Tracklist/Index.cshtml)
        add_tests_matching '^(YouTubeBrowseTest|AppleTracklistNativePlaybackGuardrailTest|SpotifyIncrementalPlaybackGuardrailTest|MediaSessionGuardrailTest)$' ;;
      DeezSpoTag.Integrations/Plex/PlexApiClient.cs)
        add_tests_matching '^(Plex|PlaylistSyncReadiness|NavidromeIdentityReconciliation|LibraryScanTriggerGuardrail)' ;;
      DeezSpoTag.Integrations/Jellyfin/JellyfinApiClient.cs)
        add_tests_matching '^(Jellyfin|PlaylistSyncReadiness|NavidromeIdentityReconciliation|LibraryScanTriggerGuardrail)' ;;
      DeezSpoTag.Integrations/Navidrome/NavidromeApiClient.cs)
        add_tests_matching '^(Navidrome|PlaylistSyncReadiness|LibraryScanTriggerGuardrail)' ;;
      DeezSpoTag.Web/Services/MediaServerLibraryRefreshService.cs|DeezSpoTag.Web/Services/MediaServerRefreshOutboxService.cs)
        add_tests_matching '^(NavidromeIdentityReconciliation|LibraryScanTriggerGuardrail|LibraryRepositoryCoverage|PlaylistSyncReadiness)Test$' ;;
      DeezSpoTag.Web/wwwroot/js/library-apple-extras.js)
        add_tests_matching '^(LibraryScanTriggerGuardrail|SecurityHardeningGuardrail)Test$' ;;
      *Amazon*) add_tests_matching '^(Amazon|DownloadIntentFallbackParity|EngineFallbackCoordinator)' ;;
      *YouTube*) add_tests_matching '^(YouTube|FavoritesProvider|PublicDownloadProviderBoundary)' ;;
      *Apple*Browse*|*AppleMusicCatalog*|*AppleCatalogJson*) add_tests_matching '^(AppleBrowse|ApplePlaylistPagination|AppleTracklistNativePlayback)' ;;
      *Security*|*LogSanitizer*|*OutboundUrlGuard*) add_tests_matching '(Security|Secret|CodeQlCleanup)' ;;
      DeezSpoTag.Web/Services/PlaylistPlatformSnapshotCoordinator.cs)
        add_tests_matching '^(PlaylistPlatformSnapshotTest|WatchlistPlatformProgressTest)$' ;;
      *Recommendation*|*Pathfinder*) add_tests_matching '(Recommendation|WeeklyPathfinder)' ;;
      DeezSpoTag.Services/Genre/*) add_tests_matching '(Genre|PersonalGenre|ResearchedGenre)' ;;
      DeezSpoTag.Web/Views/Shared/_GenreIntelligence.cshtml)
        add_tests_matching '^(GenreIntelligenceAutoTagTest|GenreVocabularySurfaceTest|GenreRegionUiGuardrailTest)$' ;;
      *AutoTag*|*autotag*) add_tests_matching '(AutoTag|OneTagger|AlbumIdentity|ManualEnhancement|LastFmAutoTag)' ;;
      DeezSpoTag.Web/Services/ArtistMetadata*.cs)
        add_tests_matching '^ArtistMetadata' ;;
      DeezSpoTag.Web/Services/Audiomack/AudiomackArtistLocationService.cs)
        add_tests_matching '^(AudiomackArtistLocation|ArtistLocationResolver)' ;;
      DeezSpoTag.Web/Services/LocalLibraryScanner.cs)
        add_tests_matching '^(LibraryScanTriggerGuardrail|TrackAnalysisBackgroundServiceGuardrail|AudioFileQualityRanker|EmbeddedVibeMetadataReader|VibeOrchestration)Test$' ;;
      DeezSpoTag.Web/Services/TaggingProfileService.cs)
        add_tests_matching '^(TaggingProfile|EmbeddedVibeMetadataReader|VibeAnalyzerGenreEvidence)' ;;
      DeezSpoTag.Web/Services/Vibe/VibeSemanticEvidence.cs)
        add_tests_matching '^(VibeSemanticResolver|VibeAnalyzerGenreEvidence|VibeOrchestration|EmbeddedVibeMetadataReader)Test$' ;;
      DeezSpoTag.Web/Tools/vibe_analyzer.py)
        add_tests_matching '^(VibeAnalyzerWorker|VibeAnalyzerGenreEvidence|SonicEmbeddingModelContract|SonicEmbeddingPooling)Test$' ;;
      DeezSpoTag.Web/Views/Activities/Index.cshtml)
        add_tests_matching '^(ActivitiesControllerContract|SonicAnalysisSettingsAndPanel|ArtistMetadataAutomationResilience)Test$' ;;
      *ArtistAlias*) add_tests_matching '(ArtistAlias|AliasGroup|ArtistMetadataAlias)' ;;
      DeezSpoTag.Services/Library/*|DeezSpoTag.Web/Services/Library*|DeezSpoTag.Web/Controllers/Api/Library*)
        add_tests_matching '^Library' ;;
      *Playlist*Sync*|*Playlist*Write*|*Playlist*Target*) add_tests_matching '(PlaylistSync|PlaylistMembership|PlaylistRemoval|PlaylistProvisioning|PlaylistPlatform|PlaylistTarget|PlaylistMetadata|PlaylistBatching)' ;;
      *Soulseek*|*Slskd*) add_tests_matching '^Soulseek' ;;
      *Spotify*) add_tests_matching '^Spotify' ;;
      *Lyrics*|*lyrics*) add_tests_matching '(Lyrics|Lrc|Ttml)' ;;
      *Download/Queue/*|*VerificationRetry*|*PublicApiVerification*|*DownloadIntent*|*DownloadOrchestration*|*DownloadQueueEnqueue*|*DownloadApiController*)
        add_tests_matching '(DownloadQueue|DownloadIntent|VerificationRetry|EngineFallback|PublicApiDownload|PublicDownloadProvider)' ;;
      *Download/Fallback/*) add_tests_matching '(Fallback|DownloadIntent)' ;;
      *Download/Apple/*) add_tests_matching '^Apple' ;;
      *Download/Tidal/*|*TidalCatalog*) add_tests_matching '^Tidal' ;;
      *Download/Qobuz/*) add_tests_matching '^Qobuz' ;;
      *Download/Deezer/*) add_tests_matching '^Deezer' ;;
      *SoundCloud*) add_tests_matching '^SoundCloud' ;;
      *Download/Utils/*|*Download/Shared/*)
        add_tests_matching '(AudioTagger|ArtworkFallback|LyricsSettingsPolicy|TechnicalLyricsSettingsApplier|DownloadIntentFallbackParity)' ;;
      *Watchlist*|*watchlist*) add_tests_matching '(Watchlist|MonitoredPlaylist)' ;;
      DeezSpoTag.Web/wwwroot/js/home-*|DeezSpoTag.Web/wwwroot/css/home-*|DeezSpoTag.Web/Views/Categories/*|DeezSpoTag.Web/Views/Home/*)
        add_tests_matching '^(AppleBrowse|YouTubeBrowse|AssetUrlGuardrail)' ;;
      *manifest.webmanifest|DeezSpoTag.Web/wwwroot/js/site.js|*CommonFooterScripts*|*CommonHeadAssets*|*Views/Shared/_Layout*)
        add_tests_matching '(PwaInstall|TabPreference|AssetUrlGuardrail)' ;;
      *Views/Settings/*) add_tests_matching '(Settings|AuthenticationStatus|PublicApiProviderRendering)' ;;
      DeezSpoTag.Web/Program.cs) add_tests_matching '(StartupBackgroundWork|PwaInstall|LocalApiAccessSecurity|AssetUrlGuardrail)' ;;
    esac
    # Reuse conventional source-to-test names in addition to the area mappings.
    stem="${file##*/}"
    stem="${stem%.cs}"
    if [[ "$stem" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]]; then
      add_tests_matching "^${stem}(Test|.*GuardrailTest)$"
    fi
    if [[ ${#selected[@]} -eq "$before" ]]; then unmapped+=("$file"); fi
    # Supplement mapped areas without treating global security checks as feature coverage.
    case "$file" in
      DeezSpoTag.Web/*.cs|DeezSpoTag.Services/*.cs)
        add_tests_matching '^CodeQlCleanupRegressionGuardrailTest$' ;;
    esac
  done <<< "$CHANGED_FILES"

  if [[ ${#unmapped[@]} -gt 0 ]]; then
    log "Unmapped changes: add affected-test mappings in scripts/guardrails.sh before pushing."
    printf '  - %s\n' "${unmapped[@]}" >&2
    exit 1
  fi
fi

# Build the test project (including its web dependencies) once, then reuse outputs.
if [[ "$MODE" == "full" || ${#selected[@]} -gt 0 ]]; then
  log "Running build gate..."
  dotnet build DeezSpoTag.Tests/DeezSpoTag.Tests.csproj -c "$BUILD_CONFIG" -f "$TEST_FRAMEWORK" --nologo
  test_args=(--no-build --no-restore -c "$BUILD_CONFIG" -f "$TEST_FRAMEWORK" --nologo --verbosity minimal)
  if [[ "$MODE" == "changed" ]]; then
    mapfile -t selected < <(printf '%s\n' "${selected[@]}" | sort -u)
    filter=""
    for name in "${selected[@]}"; do
      filter+="${filter:+|}FullyQualifiedName~DeezSpoTag.Tests.${name}."
    done
    log "Selected ${#selected[@]} affected test classes: ${selected[*]}"
    test_args+=(--filter "$filter")
  else
    log "Running full test suite..."
  fi
  dotnet test DeezSpoTag.Tests/DeezSpoTag.Tests.csproj "${test_args[@]}"
else
  log "No affected tests selected; running build gate only."
  dotnet build DeezSpoTag.Web/DeezSpoTag.Web.csproj -c "$BUILD_CONFIG" --nologo
fi
log "Guardrails passed."
