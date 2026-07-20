using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class MelodayGuardrailTests
{
    [Fact]
    public void Meloday_Service_Syncs_All_Configured_Targets_Including_Navidrome()
    {
        var source = ReadMelodayService();
        var playlistSync = ReadPlaylistSyncService();

        Assert.Contains("ResolveTargetServers", source, StringComparison.Ordinal);
        Assert.Contains("ResolveTargetServers(auth, selectedServers)", source, StringComparison.Ordinal);
        Assert.Contains("MelodayTargetServers.Normalize(effective.TargetServers", source, StringComparison.Ordinal);
        Assert.Contains("auth.Navidrome", source, StringComparison.Ordinal);
        Assert.Contains("SyncGeneratedLocalPlaylistAsync", source, StringComparison.Ordinal);
        Assert.Contains("context.TargetServers.Select(static target => target.Service)", source, StringComparison.Ordinal);
        Assert.Contains("BuildStableMelodayPlaylistPrefix(optionsForTitle.PlaylistPrefix, context.Library.Name, mode)", source, StringComparison.Ordinal);
        Assert.Contains("private static string BuildStableMelodayPlaylistPrefix", source, StringComparison.Ordinal);
        Assert.Contains("SyncGeneratedLocalPlaylistToTargetAsync", playlistSync, StringComparison.Ordinal);
        Assert.Contains("SyncGeneratedLocalPlaylistToPlexAsync", playlistSync, StringComparison.Ordinal);
        Assert.Contains("SyncGeneratedLocalPlaylistToJellyfinAsync", playlistSync, StringComparison.Ordinal);
        Assert.Contains("SyncGeneratedLocalPlaylistToNavidromeAsync", playlistSync, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncMelodayToNavidromeAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HasCompleteTargetResolution", source, StringComparison.Ordinal);
        Assert.DoesNotContain("canonical library tracks resolved on that server", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Plex or Jellyfin auth missing.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Generated_Playlist_Sync_Uses_Single_PlaylistSyncService_Path_Without_Watchlist_Membership()
    {
        var source = ReadMelodayService();
        var playlistSync = ReadPlaylistSyncService();
        var generatedSyncBody = ExtractMethodBody(playlistSync, "public async Task<GeneratedLocalPlaylistSyncResult> SyncGeneratedLocalPlaylistAsync");
        var generatedTargetBody = ExtractMethodBody(playlistSync, "private async Task<GeneratedLocalPlaylistTargetResult> SyncGeneratedLocalPlaylistToTargetAsync");
        var generatedResultBody = ExtractMethodBody(playlistSync, "private static GeneratedLocalPlaylistTargetResult BuildGeneratedTargetResult");

        Assert.Contains("GeneratedLocalPlaylistSyncRequest", playlistSync, StringComparison.Ordinal);
        Assert.Contains("GeneratedLocalPlaylistTargetResult", playlistSync, StringComparison.Ordinal);
        Assert.Contains("GeneratedLocalPlaylistSyncResult", playlistSync, StringComparison.Ordinal);
        Assert.Contains("PlexService => await SyncGeneratedLocalPlaylistToPlexAsync", generatedTargetBody, StringComparison.Ordinal);
        Assert.Contains("JellyfinService => await SyncGeneratedLocalPlaylistToJellyfinAsync", generatedTargetBody, StringComparison.Ordinal);
        Assert.Contains("NavidromeService => await SyncGeneratedLocalPlaylistToNavidromeAsync", generatedTargetBody, StringComparison.Ordinal);
        Assert.Contains("successful.Count > 0", generatedSyncBody, StringComparison.Ordinal);
        Assert.Contains("var success = !string.IsNullOrWhiteSpace(playlistId)", generatedResultBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplacePlaylistWatchTargetMembershipAsync", generatedSyncBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistTargetPlaylistBindingAsync", generatedSyncBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncMelodayToPlexAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncMelodayToJellyfinAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncMelodayToNavidromeAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Schedule_Still_Uses_Original_Dayparts()
    {
        var source = ReadMelodayService();

        Assert.Contains("DawnPeriodName", source, StringComparison.Ordinal);
        Assert.Contains("EarlyMorningPeriodName", source, StringComparison.Ordinal);
        Assert.Contains("MorningPeriodName", source, StringComparison.Ordinal);
        Assert.Contains("AfternoonPeriodName", source, StringComparison.Ordinal);
        Assert.Contains("EveningPeriodName", source, StringComparison.Ordinal);
        Assert.Contains("NightPeriodName", source, StringComparison.Ordinal);
        Assert.Contains("LateNightPeriodName", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Uses_Stable_Library_Specific_App_Mix_Identity()
    {
        var source = ReadMelodayService();

        Assert.Contains("MelodayAppUserId", source, StringComparison.Ordinal);
        Assert.Contains("EnsureMelodayAppUserAsync", source, StringComparison.Ordinal);
        Assert.Contains("BuildMelodayMixId(mode, context.Library.Id)", source, StringComparison.Ordinal);
        Assert.Contains("BuildMelodayMixId(string mode, long libraryId)", source, StringComparison.Ordinal);
        Assert.Contains("=> $\"meloday-{MelodayModes.Normalize(mode)}-{libraryId}\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildMelodayMixId(mode),", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildMelodayMixId(mode, context.HistoryTarget.Service)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("meloday-{targetService}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Tracklist_Rows_Are_Local_And_Not_Deezer_Matched_External_Rows()
    {
        var view = ReadTracklistView();
        var models = ReadSource("DeezSpoTag.Services", "Library", "Models.cs");
        var repository = ReadLibraryRepository();

        Assert.Contains("tracklistSource === 'mix' || tracklistSource === 'meloday'", view, StringComparison.Ordinal);
        Assert.Contains("function isLocalTracklistSource(source)", view, StringComparison.Ordinal);
        Assert.Contains("normalized === 'meloday'", view, StringComparison.Ordinal);
        Assert.Contains("source: 'meloday'", view, StringComparison.Ordinal);
        Assert.Contains("long? AudioFileId = null", models, StringComparison.Ordinal);
        Assert.Contains("string? FilePath = null", models, StringComparison.Ordinal);
        Assert.Contains("string? VariantKey = null", models, StringComparison.Ordinal);
        Assert.Contains("selected_audio.audio_file_id", repository, StringComparison.Ordinal);
        var localMapper = ExtractMethodBody(view, "function mapLibraryStyleTracks");
        var localCoverNormalizer = ExtractMethodBody(view, "function normalizeLocalLibraryCoverUrl");
        var externalMatcher = ExtractMethodBody(view, "function scheduleExternalTracklistMatches");
        var renderer = ExtractMethodBody(view, "function renderTracklist");
        Assert.Contains("function buildLocalTrackPlaybackUrl(trackId, audioFileId, filePath)", view, StringComparison.Ordinal);
        Assert.Contains("/api/library/analysis/track/${encodeURIComponent(trackId)}/audio", view, StringComparison.Ordinal);
        Assert.Contains("data-local-track-id", view, StringComparison.Ordinal);
        Assert.Contains("library:${localTrackId}", view, StringComparison.Ordinal);
        Assert.Contains("preview: localPlaybackUrl", localMapper, StringComparison.Ordinal);
        Assert.Contains("normalizeLocalLibraryCoverUrl(track.coverPath || '')", localMapper, StringComparison.Ordinal);
        Assert.Contains("/api/library/image?path=${encodeURIComponent(normalized)}&size=240", localCoverNormalizer, StringComparison.Ordinal);
        Assert.Contains("normalized.startsWith('/api/library/image')", localCoverNormalizer, StringComparison.Ordinal);
        Assert.DoesNotContain("preview: '',", localMapper, StringComparison.Ordinal);
        Assert.Contains("isLocalTracklistSource(normalizedSource)", externalMatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("externalSource !== 'deezer'", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Status_Exposes_ReadOnly_Source_Diagnostics()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "MelodayApiController.cs");
        var resultModel = ReadSource("DeezSpoTag.Web", "Services", "MelodayHistoryImportResult.cs");

        Assert.Contains("[HttpGet(\"diagnostics\")]", controller, StringComparison.Ordinal);
        Assert.Contains("GetStatusAsync()", controller, StringComparison.Ordinal);
        Assert.Contains("EndpointStatus", controller, StringComparison.Ordinal);
        Assert.Contains("MappingStatus", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("RunAsync(refreshHistory: true", ExtractMethodBody(controller, "public async Task<IActionResult> Diagnostics"), StringComparison.Ordinal);
        Assert.Contains("public string EndpointStatus", resultModel, StringComparison.Ordinal);
        Assert.Contains("public string MappingStatus", resultModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Settings_Render_Selected_Target_Server_And_Library_Controls_Without_Manual_Mappings()
    {
        var view = ReadActivitiesView();
        var script = ReadMelodayScript();
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "MelodaySettingsApiController.cs");
        var settingsStore = ReadSource("DeezSpoTag.Web", "Services", "MelodaySettingsStore.cs");

        Assert.Contains("data-meloday-target-server=\"plex\"", view, StringComparison.Ordinal);
        Assert.Contains("data-meloday-target-server=\"jellyfin\"", view, StringComparison.Ordinal);
        Assert.Contains("data-meloday-target-server=\"navidrome\"", view, StringComparison.Ordinal);
        Assert.Contains("id=\"meloday-target-libraries\"", view, StringComparison.Ordinal);
        Assert.Contains("metadata-updater-option-group", view, StringComparison.Ordinal);
        Assert.Contains("metadata-updater-checkbox-grid meloday-target-server-grid", view, StringComparison.Ordinal);
        Assert.Contains("metadata-updater-checkbox-grid meloday-target-library-grid", view, StringComparison.Ordinal);
        Assert.Contains("class=\"metadata-updater-option\"", view, StringComparison.Ordinal);
        Assert.Contains("meloday-target-server-grid", view, StringComparison.Ordinal);
        Assert.Contains("meloday-target-library-grid", view, StringComparison.Ordinal);
        Assert.Contains("repeat(3, minmax(0, 1fr))", view, StringComparison.Ordinal);
        Assert.Contains("repeat(auto-fit, minmax(220px, 1fr))", view, StringComparison.Ordinal);
        Assert.DoesNotContain("meloday-target-option", view, StringComparison.Ordinal);
        Assert.DoesNotContain("meloday-target-option", script, StringComparison.Ordinal);
        Assert.Contains("melodayGetTargetServers", script, StringComparison.Ordinal);
        Assert.Contains("melodayGetTargetLibraryIds", script, StringComparison.Ordinal);
        Assert.Contains("/api/meloday/settings/libraries", script, StringComparison.Ordinal);
        Assert.Contains("targetServers", script, StringComparison.Ordinal);
        Assert.Contains("targetLibraryIds", script, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"libraries\")]", controller, StringComparison.Ordinal);
        Assert.Contains("TargetServers = targetServers", controller, StringComparison.Ordinal);
        Assert.Contains("TargetLibraryIds = targetLibraryIds", controller, StringComparison.Ordinal);
        Assert.Contains("TargetServers = MelodayTargetServers.Normalize", settingsStore, StringComparison.Ordinal);
        Assert.Contains("TargetLibraryIds = MelodayService.NormalizeTargetLibraryIds", settingsStore, StringComparison.Ordinal);
        Assert.DoesNotContain("meloday-library-name", view, StringComparison.Ordinal);
        Assert.DoesNotContain("melodayGetSyncTargets", script, StringComparison.Ordinal);
        Assert.DoesNotContain("syncTargets:", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Playlists_Can_Be_Listed_And_Deleted_As_App_Owned_Mixes()
    {
        var controller = ReadMixesController();
        var repository = ReadLibraryRepository();
        var script = ReadAutoPlaylistsScript();

        Assert.Contains("EnsureMelodayAppUserAsync", controller, StringComparison.Ordinal);
        Assert.Contains("mixes.AddRange(requestedLibraryId > 0", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpDelete(\"{id}\")]", controller, StringComparison.Ordinal);
        Assert.Contains("DeleteGeneratedMixCacheAsync", repository, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM mix_item WHERE mix_cache_id", repository, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM mix_cache WHERE id", repository, StringComparison.Ordinal);
        Assert.Contains("className = \"meloday-playlist-delete\"", script, StringComparison.Ordinal);
        Assert.Contains("headers.set(\"X-CSRF-TOKEN\", csrfToken)", script, StringComparison.Ordinal);
        Assert.Contains("credentials: \"same-origin\"", script, StringComparison.Ordinal);
        Assert.Contains("method: \"DELETE\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Generates_A_Distinct_Mix_For_Every_Nonempty_Library()
    {
        var source = ReadMelodayService();
        var resolverBody = ExtractMethodBody(source, "private static IReadOnlyList<LibraryDto> ResolveMelodayLibraries");
        var runBody = ExtractMethodBody(source, "public async Task<MelodayRunResult> RunAsync");

        Assert.Contains("folder.LibraryId.HasValue", resolverBody, StringComparison.Ordinal);
        Assert.Contains("folder.LibraryName", resolverBody, StringComparison.Ordinal);
        Assert.Contains("GroupBy(folder => folder.LibraryId", resolverBody, StringComparison.Ordinal);
        Assert.Contains("GetConfiguredEnabledMusicFoldersAsync", runBody, StringComparison.Ordinal);
        Assert.Contains("var configuredLibraries = ResolveMelodayLibraries(configuredFolders)", runBody, StringComparison.Ordinal);
        Assert.Contains("ResolveMelodayLibraries(configuredFolders, effective.TargetLibraryIds)", runBody, StringComparison.Ordinal);
        Assert.Contains("DeleteInactiveMelodayMixesAsync(\n            configuredLibraries.Select", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PlexSectionId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JellyfinLibraryId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NavidromeLibraryId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderSupportsAnyTarget", runBody, StringComparison.Ordinal);
        Assert.Contains("folder.Id", runBody, StringComparison.Ordinal);
        Assert.Contains("foreach (var library in libraries)", runBody, StringComparison.Ordinal);
        Assert.Contains("BuildMelodayMixId(mode, context.Library.Id)", source, StringComparison.Ordinal);
        Assert.Contains("context.Library.Name} {GetModeLabel(mode)}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectLibraryAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Selected_Targets_Filter_History_Sources_Sync_Servers_And_Libraries()
    {
        var source = ReadMelodayService();
        var runBody = ExtractMethodBody(source, "public async Task<MelodayRunResult> RunAsync");
        var targetResolver = ExtractMethodBody(source, "private static IReadOnlyList<MediaServerTarget> ResolveTargetServers");
        var libraryResolver = ExtractMethodBody(source, "private static IReadOnlyList<LibraryDto> ResolveMelodayLibraries");

        Assert.Contains("selectedServers.Contains(MelodayTargetServers.Plex", runBody, StringComparison.Ordinal);
        Assert.Contains("selectedServers.Contains(MelodayTargetServers.Jellyfin", runBody, StringComparison.Ordinal);
        Assert.Contains("selectedServers.Contains(MelodayTargetServers.Navidrome", runBody, StringComparison.Ordinal);
        Assert.Contains("ResolveTargetServers(auth, selectedServers)", runBody, StringComparison.Ordinal);
        Assert.Contains("selected.Contains(MelodayTargetServers.Plex", targetResolver, StringComparison.Ordinal);
        Assert.Contains("selected.Contains(MelodayTargetServers.Jellyfin", targetResolver, StringComparison.Ordinal);
        Assert.Contains("selected.Contains(MelodayTargetServers.Navidrome", targetResolver, StringComparison.Ordinal);
        Assert.Contains("NormalizeTargetLibraryIds(selectedLibraryIds)", libraryResolver, StringComparison.Ordinal);
        Assert.Contains("selected.Count == 0 || selected.Contains(folder.LibraryId!.Value)", libraryResolver, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Artwork_Can_Be_Uploaded_From_Local_File_When_BaseUrl_Is_Not_Configured()
    {
        var source = ReadMelodayService();
        var playlistSync = ReadPlaylistSyncService();

        Assert.Contains("GeneratedMelodayCover", source, StringComparison.Ordinal);
        Assert.Contains("TryResolveStaticCoverPath", source, StringComparison.Ordinal);
        Assert.Contains("UpdatePlaylistPosterFromFileAsync", playlistSync, StringComparison.Ordinal);
        Assert.Contains("UpdateItemPrimaryImageFromFileAsync", playlistSync, StringComparison.Ordinal);
        Assert.Contains("images\", \"meloday", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (string.IsNullOrWhiteSpace(options.BaseUrl))\n        {\n            return null;\n        }", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderCoverAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CoversPath", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Artwork_Is_Deterministic_Per_Library_Period_And_Mode()
    {
        var source = ReadMelodayService();

        Assert.Contains("GetArtworkIndex(periodName, libraryId, mode", source, StringComparison.Ordinal);
        Assert.Contains("context.Library.Id", source, StringComparison.Ordinal);
        Assert.Contains("libraryId * 7L", source, StringComparison.Ordinal);
        Assert.Contains("MelodayModes.Sonic", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveStaticCoverFile", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Direct_And_Sonic_Modes_Use_FolderScoped_Vibe_Results()
    {
        var service = ReadMelodayService();
        var selector = ReadMelodayVibeSelector();
        var directBody = ExtractMethodBody(service, "private async Task<List<long>> BuildDirectTrackSelectionAsync");
        var sonicBody = ExtractMethodBody(service, "private async Task<List<long>> BuildSonicTrackSelectionAsync");
        var vibeBody = ExtractMethodBody(service, "private async Task<List<long>> BuildVibeDrivenTrackSelectionAsync");

        Assert.Contains("BuildVibeDrivenTrackSelectionAsync", directBody, StringComparison.Ordinal);
        Assert.Contains("BuildVibeDrivenTrackSelectionAsync", sonicBody, StringComparison.Ordinal);
        Assert.Contains("GetTrackAnalysisByTrackIdsAsync", vibeBody, StringComparison.Ordinal);
        Assert.Contains("context.AllowedTrackIds", vibeBody, StringComparison.Ordinal);
        Assert.Contains("context.Options.SonicSimilarityDistance", vibeBody, StringComparison.Ordinal);
        Assert.Contains("historicalTrackIds", vibeBody, StringComparison.Ordinal);
        Assert.Contains("outputExclusions.UnionWith(historyTrackIds)", vibeBody, StringComparison.Ordinal);
        Assert.Contains("allowedTrackIds.Contains", selector, StringComparison.Ordinal);
        Assert.Contains("excludedTrackIds.Contains", selector, StringComparison.Ordinal);
        Assert.DoesNotContain("GetTrackAnalysisCandidatesAsync", vibeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRandomTrackIdsAsync", vibeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("FillWithRandomTracksAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveFallbackVibeSeedIndex", service, StringComparison.Ordinal);
        Assert.Contains("ResolveAnalyzedHistoryTrackIds", vibeBody, StringComparison.Ordinal);
        Assert.Contains("ApplyPlexRatingFiltersAsync(candidatePool", vibeBody, StringComparison.Ordinal);
        Assert.Contains("HasMeaningfulFeatureCoverage", selector, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Folders_And_History_Are_Automatically_Scoped_To_Local_Libraries()
    {
        var service = ReadMelodayService();

        Assert.Contains("GetConfiguredEnabledMusicFoldersAsync", service, StringComparison.Ordinal);
        Assert.Contains("GetTrackIdsForLibraryScopeAsync", service, StringComparison.Ordinal);
        Assert.Contains("GetPlayHistoryEntriesAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderSupportsAnyTarget", service, StringComparison.Ordinal);
        Assert.Contains("AllDayHours", service, StringComparison.Ordinal);
        Assert.Contains("exact-folder all-day fallback", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Uses_Local_Daypart_And_Vibe_Tags_For_Evolving_Metadata()
    {
        var service = ReadMelodayService();

        Assert.Contains("cancellationToken, folder.Id, now.Offset", service, StringComparison.Ordinal);
        Assert.Contains("context.TrackAnalysesByTrackId", service, StringComparison.Ordinal);
        Assert.Contains("analysis.MoodTags", service, StringComparison.Ordinal);
        Assert.Contains("analysis.EssentiaGenres", service, StringComparison.Ordinal);
        Assert.Contains("context.PeriodName", service, StringComparison.Ordinal);
        Assert.Contains("ToDisplayLabel(mostCommonMood)", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Vibe_Results_Are_Not_Truncated_By_A_Final_Genre_Quota()
    {
        var source = ReadMelodayService();
        var filterBody = ExtractMethodBody(source, "private static bool TryIncludeTrack");

        Assert.DoesNotContain("GenreCountByName", filterBody, StringComparison.Ordinal);
        Assert.DoesNotContain("GenreLimit", filterBody, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPrimaryGenre", source, StringComparison.Ordinal);
    }

    private static string ReadMelodayService()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "MelodayService.cs"));
    }

    private static string ReadMelodayVibeSelector()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "MelodayVibeSelector.cs"));
    }

    private static string ReadPlaylistSyncService()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "PlaylistSyncService.cs"));
    }

    private static string ReadTracklistView()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml"));
    }

    private static string ReadActivitiesView()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Activities", "Index.cshtml"));
    }

    private static string ReadMelodayScript()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "meloday.js"));
    }

    private static string ReadMixesController()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "MixesApiController.cs"));
    }

    private static string ReadLibraryRepository()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));
    }

    private static string ReadAutoPlaylistsScript()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "auto-playlists.js"));
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(new[] { repoRoot }.Concat(relativeParts).ToArray()));
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from test output path.");
    }

    private static string ExtractMethodBody(string source, string methodMarker)
    {
        var methodIndex = source.IndexOf(methodMarker, StringComparison.Ordinal);
        Assert.True(methodIndex >= 0, $"Missing method marker: {methodMarker}");

        var bodyStart = source.IndexOf('{', methodIndex);
        Assert.True(bodyStart >= 0, $"Missing method body start for: {methodMarker}");

        var depth = 0;
        for (var i = bodyStart; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(bodyStart, i - bodyStart + 1);
                }
            }
        }

        throw new InvalidOperationException($"Unable to extract method body for {methodMarker}.");
    }
}
