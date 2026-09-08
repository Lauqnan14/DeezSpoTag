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
        Assert.Contains("var title = MelodayScheduleSlots.PlaylistName(context.Library.Name, context.SlotName, mode, context.WeekdayId)", source, StringComparison.Ordinal);
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
        Assert.Contains("var success = !string.IsNullOrWhiteSpace(playlistId) && artworkSynced", generatedResultBody, StringComparison.Ordinal);
        Assert.Contains("Playlist artwork did not update.", generatedResultBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplacePlaylistWatchTargetMembershipAsync", generatedSyncBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistTargetPlaylistBindingAsync", generatedSyncBody, StringComparison.Ordinal);
        Assert.Contains("ExistingPlaylistId: ResolveExistingGeneratedPlaylistId(request, PlexService)", playlistSync, StringComparison.Ordinal);
        Assert.Contains("GetMixSyncPlaylistIdsAsync", source, StringComparison.Ordinal);
        Assert.Contains("UpsertMixSyncAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncMelodayToPlexAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncMelodayToJellyfinAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncMelodayToNavidromeAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Schedule_Uses_User_Configured_Slots_Instead_Of_Fixed_Hour_Periods()
    {
        var source = ReadMelodayService();
        var schedule = ReadMelodaySchedule();

        Assert.DoesNotContain("DawnHours", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultPeriods", source, StringComparison.Ordinal);
        Assert.Contains("ResolvePlaylistInstances(effective)", source, StringComparison.Ordinal);
        Assert.Contains("RunSlotAsync", source, StringComparison.Ordinal);
        Assert.Contains("MelodayScheduleMath.DaypartHours(effective.Slots, instance.Slot.Id)", source, StringComparison.Ordinal);
        Assert.Contains("MelodayScheduleMath.IsDue(slot, today, nowTime, state, effective.MissedRunGraceMinutes)", ReadMelodayHostedService(), StringComparison.Ordinal);
        Assert.Contains("public const string EarlyMorningId = \"early-morning\"", schedule, StringComparison.Ordinal);
        Assert.Contains("public const string LateEveningId = \"late-evening\"", schedule, StringComparison.Ordinal);
        Assert.DoesNotContain("Dawn", schedule, StringComparison.Ordinal);
        Assert.DoesNotContain("Late Night", schedule, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Uses_Rule_Based_Naming_And_Library_Slot_Mode_Mix_Identity()
    {
        var source = ReadMelodayService();
        var schedule = ReadMelodaySchedule();

        Assert.Contains("MelodayAppUserId", source, StringComparison.Ordinal);
        Assert.Contains("EnsureMelodayAppUserAsync", source, StringComparison.Ordinal);
        Assert.Contains("BuildMelodayMixId(context.Library.Id, context.SlotId, mode, context.WeekdayId)", source, StringComparison.Ordinal);
        Assert.Contains("private static string BuildMelodayMixId(long libraryId, string slotId, string mode, string weekdayId)", source, StringComparison.Ordinal);
        Assert.Contains("public static string SlotIdForMix(long libraryId, string slotId, string mode, string? weekday = null)", schedule, StringComparison.Ordinal);
        Assert.Contains("MixIdsForScheduledPlaylist", schedule, StringComparison.Ordinal);
        Assert.Contains(".AddDays(7)", ReadMelodayService(), StringComparison.Ordinal);
        Assert.DoesNotContain("PlaylistPrefix", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaylistPrefix", schedule, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildMelodayMixId(mode, context.Library.Id)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildMelodayMixId(mode),", source, StringComparison.Ordinal);
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
        Assert.Contains("if (!isDeezerMatchedExternalSource(normalizedSource))", externalMatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("'mix'", externalMatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("'meloday'", externalMatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("'plex'", externalMatcher, StringComparison.Ordinal);
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
        var activities = ReadActivitiesView();
        var script = ReadMelodayScript();
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "MelodaySettingsApiController.cs");
        var settingsStore = ReadSource("DeezSpoTag.Web", "Services", "MelodaySettingsStore.cs");

        Assert.Contains("Meloday Playlists", activities, StringComparison.Ordinal);
        Assert.Contains("id=\"meloday-config\"", activities, StringComparison.Ordinal);
        Assert.Contains("id=\"meloday-schedule-slots\"", activities, StringComparison.Ordinal);
        Assert.Contains("id=\"meloday-library-schedules\"", activities, StringComparison.Ordinal);
        Assert.Contains("id=\"meloday-grace-minutes\"", activities, StringComparison.Ordinal);
        Assert.Contains("id=\"meloday-schedule-reset\"", activities, StringComparison.Ordinal);
        Assert.Contains("data-meloday-view=\"grid\"", activities, StringComparison.Ordinal);
        Assert.Contains("data-meloday-view=\"list\"", activities, StringComparison.Ordinal);
        Assert.Contains("data-meloday-target-server=\"plex\"", activities, StringComparison.Ordinal);
        Assert.Contains("data-meloday-target-server=\"jellyfin\"", activities, StringComparison.Ordinal);
        Assert.Contains("data-meloday-target-server=\"navidrome\"", activities, StringComparison.Ordinal);
        Assert.Contains("Reset to default", activities, StringComparison.Ordinal);
        Assert.Contains("Names are generated automatically", script, StringComparison.Ordinal);
        Assert.Contains("melodayWeekdayName", script, StringComparison.Ordinal);
        Assert.Contains("['noon', 'Noon', '12:00', 'flare']", script, StringComparison.Ordinal);
        Assert.DoesNotContain("['midday'", script, StringComparison.Ordinal);
        Assert.Contains("melodayCanonicalSlotId", script, StringComparison.Ordinal);
        // The config lives inside the media operations tab; there is no standalone page.
        Assert.DoesNotContain("Open Meloday settings", activities, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/Meloday\"", activities, StringComparison.Ordinal);
        Assert.DoesNotContain("meloday-playlist-prefix", activities, StringComparison.Ordinal);
        Assert.DoesNotContain("meloday-target-library-grid", activities, StringComparison.Ordinal);
        Assert.DoesNotContain("meloday-update-minutes", activities, StringComparison.Ordinal);
        // No hidden or expandable sections.
        Assert.DoesNotContain("meloday-advanced-toggle", activities, StringComparison.Ordinal);
        Assert.DoesNotContain("meloday-advanced-panel", activities, StringComparison.Ordinal);
        // Artwork management is not part of the UI.
        Assert.DoesNotContain("meloday-artwork", activities, StringComparison.Ordinal);
        Assert.DoesNotContain("meloday-artwork", script, StringComparison.Ordinal);
        Assert.Contains("repeat(auto-fill, minmax(148px, 1fr))", activities, StringComparison.Ordinal);
        Assert.Contains("meloday-target-option", activities, StringComparison.Ordinal);
        Assert.Contains("melodayGetTargetServers", script, StringComparison.Ordinal);
        Assert.Contains("buildMelodayLibrariesFromDom", script, StringComparison.Ordinal);
        Assert.Contains("melodayCountPlaylists", script, StringComparison.Ordinal);
        Assert.Contains("/api/meloday/settings/libraries", script, StringComparison.Ordinal);
        Assert.Contains("targetServers", script, StringComparison.Ordinal);
        Assert.Contains("maxActivePlaylists", script, StringComparison.Ordinal);
        Assert.Contains("data-meloday-library-enabled", script, StringComparison.Ordinal);
        Assert.Contains("missedRunGraceMinutes", script, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"libraries\")]", controller, StringComparison.Ordinal);
        Assert.Contains("TargetServers = targetServers", controller, StringComparison.Ordinal);
        Assert.Contains("of {library.MaxActivePlaylists} playlist slots selected", controller, StringComparison.Ordinal);
        Assert.Contains("TargetServers = MelodayTargetServers.Normalize", settingsStore, StringComparison.Ordinal);
        Assert.Contains("TargetLibraryIds = MelodayService.NormalizeTargetLibraryIds", settingsStore, StringComparison.Ordinal);
        Assert.DoesNotContain("meloday-library-name", activities, StringComparison.Ordinal);
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
        Assert.Contains("DeezSpoTag.ui.confirm", script, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!confirm(", script, StringComparison.Ordinal);
        Assert.Contains("TryResolveExistingCoverWebPath", controller, StringComparison.Ordinal);
        Assert.Contains("AttachMelodayCover", controller, StringComparison.Ordinal);
        Assert.Contains("headers.set(\"X-CSRF-TOKEN\", csrfToken)", script, StringComparison.Ordinal);
        Assert.Contains("credentials: \"same-origin\"", script, StringComparison.Ordinal);
        Assert.Contains("method: \"DELETE\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Generates_A_Distinct_Mix_For_Every_Scheduled_Library_Slot()
    {
        var source = ReadMelodayService();
        var resolverBody = ExtractMethodBody(source, "private static List<MelodayPlaylistInstance> ResolvePlaylistInstances");
        var runBody = ExtractMethodBody(source, "private async Task<MelodayRunResult> RunInstancesAsync");

        Assert.Contains("foreach (var library in effective.Libraries)", resolverBody, StringComparison.Ordinal);
        Assert.Contains("library.IsTargeted", resolverBody, StringComparison.Ordinal);
        Assert.Contains("foreach (var slotId in library.SlotIds)", resolverBody, StringComparison.Ordinal);
        Assert.Contains("ResolveRunModes(library.Mode)", resolverBody, StringComparison.Ordinal);
        Assert.Contains("GetConfiguredEnabledMusicFoldersAsync", runBody, StringComparison.Ordinal);
        Assert.Contains("DeleteInactiveMelodayMixesAsync(", runBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PlexSectionId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JellyfinLibraryId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NavidromeLibraryId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderSupportsAnyTarget", runBody, StringComparison.Ordinal);
        Assert.Contains("folder.Id", runBody, StringComparison.Ordinal);
        Assert.Contains("foreach (var libraryGroup in instances.GroupBy", runBody, StringComparison.Ordinal);
        Assert.Contains("foreach (var instance in libraryGroup)", runBody, StringComparison.Ordinal);
        Assert.Contains("BuildMelodayMixId(context.Library.Id, context.SlotId, mode, context.WeekdayId)", source, StringComparison.Ordinal);
        Assert.Contains("var title = MelodayScheduleSlots.PlaylistName(context.Library.Name, context.SlotName, mode, context.WeekdayId)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectLibraryAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Selected_Targets_Filter_History_Sources_Sync_Servers_And_Libraries()
    {
        var source = ReadMelodayService();
        var runBody = ExtractMethodBody(source, "private async Task<MelodayRunResult> RunInstancesAsync");
        var targetResolver = ExtractMethodBody(source, "private static IReadOnlyList<MediaServerTarget> ResolveTargetServers");

        Assert.Contains("selectedServers.Contains(MelodayTargetServers.Plex", runBody, StringComparison.Ordinal);
        Assert.Contains("selectedServers.Contains(MelodayTargetServers.Jellyfin", runBody, StringComparison.Ordinal);
        Assert.Contains("selectedServers.Contains(MelodayTargetServers.Navidrome", runBody, StringComparison.Ordinal);
        Assert.Contains("ResolveTargetServers(auth, selectedServers)", runBody, StringComparison.Ordinal);
        Assert.Contains("selected.Contains(MelodayTargetServers.Plex", targetResolver, StringComparison.Ordinal);
        Assert.Contains("selected.Contains(MelodayTargetServers.Jellyfin", targetResolver, StringComparison.Ordinal);
        Assert.Contains("selected.Contains(MelodayTargetServers.Navidrome", targetResolver, StringComparison.Ordinal);
        Assert.Contains("scheduledLibraryIds.Contains(folder.LibraryId.Value)", runBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Artwork_Comes_From_A_Persistent_User_Image_Pool()
    {
        var source = ReadMelodayService();
        var pool = ReadMelodayArtworkPool();
        var composer = ReadMelodayCoverComposer();
        var artworkController = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "MelodayArtworkApiController.cs");
        var playlistSync = ReadPlaylistSyncService();

        Assert.Contains("GeneratedMelodayCover", source, StringComparison.Ordinal);
        Assert.Contains("_artworkAssignments.AssignAsync(libraryId, slotId, mode, weekdayId, cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("_coverComposer.Compose(", source, StringComparison.Ordinal);
        Assert.Contains("/images/meloday/generated/", source, StringComparison.Ordinal);
        Assert.Contains("\"images\", \"meloday\", \"source\"", pool, StringComparison.Ordinal);
        Assert.Contains("images\", \"meloday\", \"generated\"", composer, StringComparison.Ordinal);
        Assert.Contains("PixelAlphaCompositionMode.SrcOver", composer, StringComparison.Ordinal);
        Assert.DoesNotContain("TryResolveStaticCoverPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetArtworkIndex", source, StringComparison.Ordinal);
        Assert.Contains("[HttpPost]", artworkController, StringComparison.Ordinal);
        Assert.Contains("UpdatePlaylistPosterFromFileAsync", playlistSync, StringComparison.Ordinal);
        Assert.Contains("UpdateItemPrimaryImageFromFileAsync", playlistSync, StringComparison.Ordinal);
        Assert.Contains("SyncGeneratedNavidromeArtworkAsync", playlistSync, StringComparison.Ordinal);
        Assert.Contains("UpdatePlaylistImageFromFileAsync", playlistSync, StringComparison.Ordinal);
        Assert.Contains("var artworkSynced = await SyncGeneratedPlexArtworkAsync", playlistSync, StringComparison.Ordinal);
        Assert.Contains("var artworkSynced = await SyncGeneratedJellyfinArtworkAsync", playlistSync, StringComparison.Ordinal);
        Assert.Contains("var artworkSynced = await SyncGeneratedNavidromeArtworkAsync", playlistSync, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderCoverAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CoversPath", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Meloday_Artwork_Assignments_Are_Stable_Content_Addressed_And_Library_Unique()
    {
        var pool = ReadMelodayArtworkPool();
        var composer = ReadMelodayCoverComposer();
        var tracklistView = ReadTracklistView();
        var autoPlaylists = ReadAutoPlaylistsScript();

        Assert.Contains("internal static IReadOnlyList<string> BuildDeck", pool, StringComparison.Ordinal);
        Assert.Contains("MelodayArtworkAllocator.Allocate(deck, assignments, libraryId, slotId, mode, weekday)", pool, StringComparison.Ordinal);
        Assert.Contains("unusedGlobal.Count > 0", pool, StringComparison.Ordinal);
        Assert.Contains("unusedWithinLibrary.Count > 0", pool, StringComparison.Ordinal);
        Assert.Contains("RemoveAll(assignment => !deck.Contains(assignment.ImageId", pool, StringComparison.Ordinal);
        Assert.Contains("ResolveOutputFileName", composer, StringComparison.Ordinal);
        Assert.Contains("PruneStaleGenerations", composer, StringComparison.Ordinal);
        Assert.DoesNotContain("% 18", tracklistView, StringComparison.Ordinal);
        Assert.DoesNotContain("MELODAY_COVER_COUNT", autoPlaylists, StringComparison.Ordinal);
        Assert.DoesNotContain("/images/meloday/${", tracklistView, StringComparison.Ordinal);
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
        Assert.Contains("context.SlotName", service, StringComparison.Ordinal);
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

    private static string ReadMelodaySchedule()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "MelodaySchedule.cs"));
    }

    private static string ReadMelodayArtworkPool()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "MelodayArtworkPool.cs"));
    }

    private static string ReadMelodayCoverComposer()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "MelodayCoverComposer.cs"));
    }

    private static string ReadMelodayHostedService()
    {
        var repoRoot = ResolveRepoRoot();
        return File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "MelodayHostedService.cs"));
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
