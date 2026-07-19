using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Controllers.Api;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

[Collection("Settings Config Isolation")]
public sealed class WatchlistSettingsBehaviorTests : IDisposable
{
    private const string WatchMaxItemsPerRunName = "WatchMaxItemsPerRun";
    private const string WatchMaxReleasesPerArtistName = "WatchMaxReleasesPerArtist";
    private const string WatchMaxReleasesPerArtistJsonName = "watchMaxReleasesPerArtist";
    private const string WatchMaxTracksPerPlaylistCheckJsonName = "watchMaxTracksPerPlaylistCheck";
    private const string WatchArtistTopSongsEnabledJsonName = "watchArtistTopSongsEnabled";
    private const string WatchArtistLatestReleasesOnlyJsonName = "watchArtistLatestReleasesOnly";
    private const string AlbumGroup = "album";
    private const string SingleGroup = "single";
    private const string AppearsOnGroup = "appears_on";
    private const string CompilationGroup = "compilation";

    private readonly string _tempRoot;
    private readonly TestConfigRootScope _configScope;
    private readonly DeezSpoTagSettingsService _settingsService;

    public WatchlistSettingsBehaviorTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-watchlist-settings-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);
        _configScope = new TestConfigRootScope(_tempRoot);
        _settingsService = new DeezSpoTagSettingsService(NullLogger<DeezSpoTagSettingsService>.Instance);
    }

    [Fact]
    public void SaveSettings_EmptyWatchlistAlbumGroups_RestoresDefaultGroups()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchedArtistAlbumGroup = new List<string>();

        _settingsService.SaveSettings(settings);

        var persisted = _settingsService.LoadSettings();
        Assert.Equal(new[] { AlbumGroup, SingleGroup }, persisted.WatchedArtistAlbumGroup);
    }

    [Fact]
    public void SaveSettings_WatchlistAlbumGroups_AreCanonicalAndDropUnknownValues()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchedArtistAlbumGroup = new List<string>
        {
            " Single ",
            "appearson",
            "compilations",
            "unsupported",
            SingleGroup
        };

        _settingsService.SaveSettings(settings);

        var persisted = _settingsService.LoadSettings();
        Assert.Equal(new[] { AppearsOnGroup, CompilationGroup, SingleGroup }, persisted.WatchedArtistAlbumGroup);
    }

    [Fact]
    public void SaveSettings_WatchMaxItemsPerRunAboveFifty_RestoresDefault()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchMaxItemsPerRun = 100;
        settings.WatchMaxReleasesPerArtist = 100;
        settings.WatchMaxTracksPerPlaylistCheck = 500;

        _settingsService.SaveSettings(settings);

        var persisted = _settingsService.LoadSettings();
        Assert.Equal(50, persisted.WatchMaxItemsPerRun);
        Assert.Equal(100, persisted.WatchMaxReleasesPerArtist);
        Assert.Equal(500, persisted.WatchMaxTracksPerPlaylistCheck);
    }

    [Fact]
    public void SaveSettings_InvalidNestedWatchLimits_NormalizeIndependently()
    {
        var settings = _settingsService.LoadSettings();
        settings.WatchMaxItemsPerRun = 25;
        settings.WatchMaxReleasesPerArtist = 101;
        settings.WatchMaxTracksPerPlaylistCheck = 501;

        _settingsService.SaveSettings(settings);

        var persisted = _settingsService.LoadSettings();
        Assert.Equal(25, persisted.WatchMaxItemsPerRun);
        Assert.Equal(50, persisted.WatchMaxReleasesPerArtist);
        Assert.Equal(50, persisted.WatchMaxTracksPerPlaylistCheck);
    }

    [Fact]
    public void ArtistWatch_NormalizesProviderAlbumGroups_ConsistentlyForSpotifyAppleAndDeezer()
    {
        var normalized = ArtistWatchService.NormalizeAlbumGroups(new[]
        {
            " Single ",
            "appears-on",
            "compile",
            "unsupported",
            SingleGroup
        });

        Assert.Equal(new[] { AppearsOnGroup, CompilationGroup, SingleGroup }, normalized);
    }

    [Fact]
    public void SettingsView_DoesNotExposeGlobalArtistAlbumGroupPreferences()
    {
        var repoRoot = ResolveRepoRoot();
        var viewPath = Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Settings", "Index.cshtml");
        Assert.True(File.Exists(viewPath), $"Missing settings view: {viewPath}");

        var source = File.ReadAllText(viewPath);
        Assert.DoesNotContain("Artist Album Groups", source, StringComparison.Ordinal);
        Assert.DoesNotContain("watchAlbumGroupAlbum", source, StringComparison.Ordinal);
        Assert.DoesNotContain("watchAlbumGroupSingle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("watchAlbumGroupCompilation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("watchAlbumGroupAppearsOn", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Select at least one artist album group.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.watchedArtistAlbumGroup.length === 0", source, StringComparison.Ordinal);
        Assert.Contains("Max Monitored Items Per Run", source, StringComparison.Ordinal);
        Assert.Contains(WatchMaxReleasesPerArtistJsonName, source, StringComparison.Ordinal);
        Assert.Contains(WatchMaxTracksPerPlaylistCheckJsonName, source, StringComparison.Ordinal);
        Assert.Contains(WatchArtistTopSongsEnabledJsonName, source, StringComparison.Ordinal);
        Assert.Contains(WatchArtistLatestReleasesOnlyJsonName, source, StringComparison.Ordinal);
        Assert.Contains("watchedArtistAlbumGroup: Array.isArray(baseSettings.watchedArtistAlbumGroup) ? [...baseSettings.watchedArtistAlbumGroup] : []", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsView_DoesNotExposeWatchlistAutomaticDownloadSource()
    {
        var repoRoot = ResolveRepoRoot();
        var settingsSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Settings", "Index.cshtml"));

        Assert.DoesNotContain("id=\"automaticDownloadSource\"", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("automaticDownloadSource:", settingsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Automatic source", settingsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistSettings_UseIndependentSyncTargetCheckboxes()
    {
        var repoRoot = ResolveRepoRoot();
        var watchlistScriptSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "library-watchlists.js"));

        Assert.Contains("createPlaylistSyncTargetsSection", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("playlistSyncTargetOptions", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("data-playlist-sync-target", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("syncTargets:", watchlistScriptSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ps-service-select", watchlistScriptSource, StringComparison.Ordinal);
        Assert.DoesNotContain("data-playlist-service", watchlistScriptSource, StringComparison.Ordinal);
        Assert.DoesNotContain("playlistServerOptions", watchlistScriptSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsView_CustomDownloadSourceOrderIsCollapsible()
    {
        var repoRoot = ResolveRepoRoot();
        var settingsSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Views", "Settings", "Index.cshtml"));

        Assert.Contains("id=\"downloadEngineOrderToggle\"", settingsSource, StringComparison.Ordinal);
        Assert.Contains("id=\"downloadEngineOrderSummary\"", settingsSource, StringComparison.Ordinal);
        Assert.Contains("id=\"downloadEngineOrderEditor\" hidden", settingsSource, StringComparison.Ordinal);
        Assert.Contains("setDownloadEngineOrderEditorExpanded", settingsSource, StringComparison.Ordinal);
        Assert.Contains("updateDownloadEngineOrderSummary", settingsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchCustomDownloadSource_IsPerPlaylistAndQueuedWithIntentOrder()
    {
        var repoRoot = ResolveRepoRoot();
        var catalogSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Download", "DownloadSourceCatalog.cs"));
        var controllerSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "LibraryPlaylistWatchlistApiController.cs"));
        var repositorySource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));
        var playlistWatchSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));
        var downloadIntentSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "DownloadIntentService.cs"));
        var intentModelSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Download", "Shared", "Models", "DownloadIntent.cs"));
        var watchlistScriptSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "library-watchlists.js"));

        Assert.Equal("custom", WatchlistPreferenceNormalizer.PreferredEngine(" Custom "));
        Assert.Contains("new(Custom, \"Custom\")", catalogSource, StringComparison.Ordinal);
        Assert.Contains("DownloadEngineOrderSettings? DownloadEngineOrder", controllerSource, StringComparison.Ordinal);
        Assert.Contains("NormalizePlaylistDownloadEngineOrder", controllerSource, StringComparison.Ordinal);
        Assert.Contains("download_engine_order_json", repositorySource, StringComparison.Ordinal);
        Assert.Contains("DownloadEngineOrder = options.DownloadEngineOrder ?? DownloadEngineOrderSettings.CreateDefault()", playlistWatchSource, StringComparison.Ordinal);
        Assert.Contains("ApplyIntentDownloadEngineOrder", downloadIntentSource, StringComparison.Ordinal);
        Assert.Contains("public DownloadEngineOrderSettings? DownloadEngineOrder { get; set; }", intentModelSource, StringComparison.Ordinal);
        Assert.Contains("createWatchlistDownloadEngineOrderSection", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("downloadEngineOrder: values.downloadEngineOrder", watchlistScriptSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchMaxItemsPerRun_IsOnlyUsedBySchedulerAndSettingsContract()
    {
        var repoRoot = ResolveRepoRoot();
        var serviceRoot = Path.Join(repoRoot, "DeezSpoTag.Web", "Services");

        var artistWatchSource = File.ReadAllText(Path.Join(serviceRoot, "ArtistWatchService.cs"));
        var playlistWatchSource = File.ReadAllText(Path.Join(serviceRoot, "WatchlistEngine.cs"));
        var hostedSource = File.ReadAllText(Path.Join(serviceRoot, "WatchlistRunCoordinator.cs"));
        var repoSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));
        var artistControllerSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "LibraryWatchlistApiController.cs"));
        var watchlistScriptSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "library-watchlists.js"));

        Assert.DoesNotContain(WatchMaxItemsPerRunName, artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("BeginRunIfInactive(watchSettings.WatchMaxItemsPerRun)", playlistWatchSource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            WatchMaxItemsPerRunName,
            playlistWatchSource.Replace("BeginRunIfInactive(watchSettings.WatchMaxItemsPerRun)", string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(WatchMaxItemsPerRunName, hostedSource, StringComparison.Ordinal);
        Assert.Contains(WatchMaxReleasesPerArtistName, artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("ResolveArtistAlbumGroups(artist)", artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("DefaultArtistAlbumGroups = new[] { AlbumGroup, SingleGroup }", artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("artist.TopSongsEnabled ?? false", artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("artist.LatestReleasesOnly ?? false", artistWatchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("artist.WatchedAlbumGroups ?? settings.WatchedArtistAlbumGroup", artistWatchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.WatchArtistTopSongsEnabled", artistWatchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.WatchArtistLatestReleasesOnly", artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("top-track:", artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("artist-top:", artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("UpdateWatchlistPreferencesAsync", repoSource, StringComparison.Ordinal);
        Assert.Contains("{artistId:long}/preferences", artistControllerSource, StringComparison.Ordinal);
        Assert.Contains("/api/library/watchlist/${encodeURIComponent(artistId)}/preferences", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("watchedArtistAlbumGroup", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("watchArtistTopSongsEnabled", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("watchArtistLatestReleasesOnly", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("const selectedGroups = Array.isArray(currentGroups) && currentGroups.length > 0", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains(": ['album', 'single'];", watchlistScriptSource, StringComparison.Ordinal);
        Assert.DoesNotContain("globalSettings.watchedArtistAlbumGroup", watchlistScriptSource, StringComparison.Ordinal);
        Assert.DoesNotContain("globalSettings.watchArtistTopSongsEnabled", watchlistScriptSource, StringComparison.Ordinal);
        Assert.DoesNotContain("globalSettings.watchArtistLatestReleasesOnly", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("preferredEngine", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("routingRules", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("Apply globally", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("/routing-rules/apply-globally", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("data-artist-engine", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("data-artist-routing-rules", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("fetchJson('/api/library/playlists')", watchlistScriptSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/library/playlists?refreshFromSource=true", watchlistScriptSource, StringComparison.Ordinal);
        Assert.Contains("PreferredEngine", artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("RoutingRules", artistWatchSource, StringComparison.Ordinal);
        Assert.Contains("preferred_engine", repoSource, StringComparison.Ordinal);
        Assert.Contains("routing_rules_json", repoSource, StringComparison.Ordinal);
        Assert.Contains("WatchlistPreferenceNormalizer.PreferredEngine", artistControllerSource, StringComparison.Ordinal);
        Assert.Contains("WatchlistPreferenceNormalizer.RoutingRules", artistControllerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatch_RefreshesFullSnapshotAndUsesSnapshotCache()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));

        Assert.Contains("WatchUseSnapshotIdChecking", source, StringComparison.Ordinal);
        Assert.Contains("var maxCandidates = CompletePlaylistCandidateFetchCount;", source, StringComparison.Ordinal);
        Assert.Contains("FetchLivePlaylistSnapshotAsync(source, sourceId, maxCandidates, cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("UpdatePlaylistWatchlistMetadataAsync", source, StringComparison.Ordinal);
        Assert.Contains("RemovePlaylistWatchTracksNotInAsync", source, StringComparison.Ordinal);
        Assert.Contains("FetchPlaylistTrackPageAsync(", source, StringComparison.Ordinal);
        Assert.Contains("Math.Min(100, maxCandidates)", source, StringComparison.Ordinal);
        Assert.Contains("while (candidates.Count < maxCandidates)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxPlaylistCandidateFetchCount = 1000", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LimitLivePlaylistSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("GetBoomplayPlaylistWatchDataAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetPlaylistAsync(playlistId, cancellationToken)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPlaylistAsync(playlistId, includeTracks:", source, StringComparison.Ordinal);
        Assert.Contains("BuildCurrentPlaylistDto(playlist, source, sourceId, liveSnapshot, liveTrackCount)", source, StringComparison.Ordinal);
        Assert.Contains("HasPlaylistSourceChanged(existingCandidateCache, liveSnapshot, candidatesJson)", source, StringComparison.Ordinal);
        Assert.Contains("EnqueueWatchlistPlaylistSyncJobsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncPlaylistAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildMediaSyncNotReadyStatus", source, StringComparison.Ordinal);
        Assert.DoesNotContain("media_sync_not_ready_", source, StringComparison.Ordinal);
        Assert.Contains("ShouldKeepSharedQueueClaimPending(result)", source, StringComparison.Ordinal);
        Assert.Contains("UpsertPlaylistWatchDownloadClaimsAsync", source, StringComparison.Ordinal);
        Assert.Contains("WatchlistHistoryStatus.DuplicateSharedTrackLinked", source, StringComparison.Ordinal);
        Assert.Contains("WatchlistHistoryStatus.MetadataRefreshed", source, StringComparison.Ordinal);
        Assert.DoesNotContain("source_unchanged", source, StringComparison.Ordinal);
        Assert.DoesNotContain("pre_sync_run", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryNotifyDuplicateWatchClaimAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatch_UsesSingleQueuePlannerBeforeQueueAdmission()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));
        var dedupeSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Services", "Download", "DownloadDedupeService.cs"));

        Assert.Contains("selection = await SelectMissingPlaylistTracksAsync(", source, StringComparison.Ordinal);
        Assert.Contains("queueOptions,", source, StringComparison.Ordinal);
        Assert.Contains("GetRequiredService<DownloadDedupeService>()", source, StringComparison.Ordinal);
        Assert.Contains("DownloadDedupeService.FromDownloadIntent", source, StringComparison.Ordinal);
        Assert.Contains("preparedIntent", source, StringComparison.Ordinal);
        Assert.Contains("HandlePreQueueDedupeDecisionAsync", source, StringComparison.Ordinal);
        Assert.Contains("RecoverInvalidPendingWatchClaimsAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetPlaylistWatchDownloadClaimsForPlaylistAsync", source, StringComparison.Ordinal);
        Assert.Contains("TryHandleQueueDuplicateForWatchlistAsync", source, StringComparison.Ordinal);
        Assert.Contains("IsPendingWatchClaimStillOwnedByQueue", source, StringComparison.Ordinal);
        Assert.Contains("queue_duplicate", source, StringComparison.Ordinal);
        Assert.Contains("library_duplicate", source, StringComparison.Ordinal);
        Assert.Contains("blocklist_match", source, StringComparison.Ordinal);
        Assert.Contains("TryRecordWatchDownloadClaimsAsync", source, StringComparison.Ordinal);
        Assert.Contains("TryMarkWatchTrackCompletedAsync", source, StringComparison.Ordinal);
        Assert.Contains("CheckLibraryPresenceAsync", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("selection = await SelectMissingPlaylistTracksAsync(", StringComparison.Ordinal)
            < source.IndexOf("EnqueueWatchlistPlaylistSyncJobsAsync", StringComparison.Ordinal),
            "Missing-track selection and queueing must precede durable remote target refresh work.");
        Assert.DoesNotContain("ShouldBlockTrack(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HandleBlockedWatchIntentAsync", source, StringComparison.Ordinal);
        Assert.Contains("public static DownloadDedupeRequest FromDownloadIntent(", dedupeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatch_DoesNotTreatPartialCachedSnapshotsAsComplete()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));

        Assert.Contains("PlaylistCandidateContract.IsReusableCache", source, StringComparison.Ordinal);
        Assert.Contains("existingCandidateCache?.IsComplete == true", source, StringComparison.Ordinal);
        Assert.Contains("if (cachedCandidatesComplete)", source, StringComparison.Ordinal);
        Assert.Contains("cached candidates are incomplete. Refreshing candidates.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatch_FollowGlobalRemainsDistinctFromExplicitAuto()
    {
        var repoRoot = ResolveRepoRoot();
        var serviceSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));
        var uiSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "library-watchlists.js"));

        Assert.Contains("{ value: '', label: 'Follow global download source' }", uiSource, StringComparison.Ordinal);
        Assert.Contains("preferredEngine: value?.preferredEngine || null", uiSource, StringComparison.Ordinal);
        Assert.Contains("var globalSettings = _settingsService.LoadSettings();", serviceSource, StringComparison.Ordinal);
        Assert.Contains("ManualDownloadPreferenceResolver.ResolvePreferredEngine(globalSettings)", serviceSource, StringComparison.Ordinal);
        Assert.Contains("itemDownloadEngineOrder ?? globalSettings.DownloadEngineOrder", serviceSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatch_ActivePlaylistRetentionCannotOverridePriorityOrder()
    {
        var repoRoot = ResolveRepoRoot();
        var serviceSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));
        var hostedSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "WatchlistRunCoordinator.cs"));

        Assert.DoesNotContain("ShouldKeepPlaylistActive", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("KeepActivePlaylist", serviceSource, StringComparison.Ordinal);
        Assert.Contains("RunBudget", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolutionBudget", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("if (result.QueuedTracks <= 0)", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("if (result.KeepActivePlaylist)", hostedSource, StringComparison.Ordinal);
        Assert.Contains("IsBlockingPlaylistStopReason(result.QueueStopReason)", hostedSource, StringComparison.Ordinal);
        Assert.Contains("return PlaylistAdvanceDecision.StopRunClearActive;", hostedSource, StringComparison.Ordinal);
        Assert.Contains("WatchQueueStopReason.DownloadGate.ToString()", hostedSource, StringComparison.Ordinal);
        Assert.Contains("WatchQueueStopReason.TrackDeferred.ToString()", hostedSource, StringComparison.Ordinal);
        Assert.Contains("ResolveInitialPlaylistItem", hostedSource, StringComparison.Ordinal);
        Assert.Contains("IsRecentExplicitPlaylistFocus", hostedSource, StringComparison.Ordinal);
        Assert.Contains("state.LastProgressUtc.HasValue", hostedSource, StringComparison.Ordinal);
        Assert.Contains("return ResolveNextPlaylistItem(playlistItems);", hostedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatch_StatusesRemainSpecificInsteadOfGenericQueueFailures()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));

        Assert.Contains("ResolveQueueFailureMessage", source, StringComparison.Ordinal);
        Assert.Contains("ResolveQueueStopStatus", source, StringComparison.Ordinal);
        Assert.Contains("queue_budget_reached", source, StringComparison.Ordinal);
        Assert.DoesNotContain("resolution_budget_reached", source, StringComparison.Ordinal);
        Assert.Contains("track_queue_deferred", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Playlist reconciled with queue failures.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistTrackStatus_PreservesUpgradedLiveQueuePrecedence()
    {
        var synced = CreatePlaylistTrackStatus(
            status: "completed",
            localTrackId: 42,
            identityStatus: "verified",
            syncStatus: "playlist_synced");
        var local = CreatePlaylistTrackStatus(
            status: "completed",
            localTrackId: 42,
            identityStatus: "verified");

        Assert.Equal(
            "downloading",
            WatchlistApiController.ResolvePlaylistTrackLocationStatus(false, synced, "running").Status);
        Assert.Equal(
            "synced",
            WatchlistApiController.ResolvePlaylistTrackLocationStatus(false, synced, "failed").Status);
        Assert.Equal(
            "failed",
            WatchlistApiController.ResolvePlaylistTrackLocationStatus(false, null, "failed").Status);
        Assert.Equal(
            "library",
            WatchlistApiController.ResolvePlaylistTrackLocationStatus(false, local, "cancelled").Status);
        Assert.Equal(
            "missing",
            WatchlistApiController.ResolvePlaylistTrackLocationStatus(false, null, null).Status);
        Assert.Equal(
            "blocked",
            WatchlistApiController.ResolvePlaylistTrackLocationStatus(true, synced, "running").Status);
    }

    [Fact]
    public void PlaylistTrackStatus_UsesOnlyExplicitPlaylistQueueClaims()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryPlaylistWatchlistApiController.cs"));

        Assert.Contains("GetPlaylistWatchDownloadClaimsForPlaylistAsync", source, StringComparison.Ordinal);
        Assert.Contains("return (claimedTasks ?? [])", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueTaskMatchesCandidate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadQueuePayloadId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistTrackStatus_DoesNotTreatEngineCompletionAsLibraryPresence()
    {
        var repoRoot = ResolveRepoRoot();
        var controllerSource = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryPlaylistWatchlistApiController.cs"));
        var appSource = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "DeezSpoTagApp.cs"));
        var helperSource = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Services",
            "Download",
            "Shared",
            "EngineAudioPostDownloadHelper.cs"));

        Assert.Contains("InLocalLibrary = persistedStatus?.LocalTrackId.HasValue == true", controllerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("NormalizeStatusText(persistedStatus?.Status) is \"completed\" or \"complete\"", controllerSource, StringComparison.Ordinal);
        Assert.Contains("private const string DownloadedStatus = \"downloaded\";", appSource, StringComparison.Ordinal);
        Assert.Contains("private const string DownloadedStatus = \"downloaded\";", helperSource, StringComparison.Ordinal);
        Assert.Contains("MarkSharedWatchDownloadClaimsDownloadedAsync", appSource, StringComparison.Ordinal);
        Assert.Contains("MarkSharedWatchDownloadClaimsDownloadedAsync", helperSource, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdatePlaylistWatchDownloadClaimStatusAsync(\n                queueUuid,\n                CompletedStatus", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdatePlaylistWatchDownloadClaimStatusAsync(\n                resolvedQueueUuid,\n                CompletedStatus", helperSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchClaims_CompleteOnlyAfterFinalizationVerification()
    {
        var repoRoot = ResolveRepoRoot();
        var repositorySource = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Services",
            "Library",
            "LibraryRepository.cs"));
        var finalizationSource = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Web",
            "Services",
            "WatchlistFinalizationService.cs"));
        var watchSource = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Web",
            "Services",
            "WatchlistEngine.cs"));
        var hostedSource = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Web",
            "Services",
            "WatchlistRunCoordinator.cs"));
        var recoveryPolicySource = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Services",
            "Download",
            "Queue",
            "DownloadQueueRecoveryPolicy.cs"));

        Assert.Contains("SET status = CASE WHEN @identityStatus = 'review' THEN status ELSE 'completed' END", repositorySource, StringComparison.Ordinal);
        Assert.Contains("UpdatePlaylistWatchDownloadClaimStatusAsync(\n                item.QueueUuid,\n                notification.Source,", finalizationSource, StringComparison.Ordinal);
        Assert.Contains("DownloadQueueRecoveryPolicy.IsWatchlistClaimOwnedByQueue", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadQueueRecoveryPolicy.IsWatchlistClaimOwnedByQueue", hostedSource, StringComparison.Ordinal);
        Assert.Contains("PostDownloadPendingLease", recoveryPolicySource, StringComparison.Ordinal);
        Assert.Contains("enrichmentStatus == \"running\" || finalizationStatus == \"running\"", recoveryPolicySource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatch_MirrorSyncDoesNotBlockMissingTrackQueueing()
    {
        var repoRoot = ResolveRepoRoot();
        var watchSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));
        var syncSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "PlaylistSyncService.cs"));
        var postDownloadSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "WatchlistPostDownloadSyncService.cs"));

        Assert.DoesNotContain("ShouldBlockUnsafeMirrorSync", syncSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Mirror sync blocked because", syncSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sync_incomplete\"", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sync_failed\"", watchSource, StringComparison.Ordinal);
        Assert.Contains("var success = queueResult.FailedCount == 0", watchSource, StringComparison.Ordinal);
        Assert.Contains("EnqueueWatchlistPlaylistSyncJobsAsync", watchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IsTerminalPlaylistSyncFailure", watchSource, StringComparison.Ordinal);
        Assert.Contains("SyncAvailablePlaylistTracksAsync", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IsFinalizedTrackSyncedAsync", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IsPlaylistWatchTrackSyncedToTargetAsync", postDownloadSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistSync_AppliesCurrentArtworkAccordingToPreference()
    {
        var repoRoot = ResolveRepoRoot();
        var syncSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "PlaylistSyncService.cs"));
        var postDownloadSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "WatchlistPostDownloadSyncService.cs"));
        var controllerSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "LibraryPlaylistWatchlistApiController.cs"));
        var visualSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "PlaylistVisualService.cs"));

        Assert.Contains("preference?.ReuseSavedArtwork == true", syncSource, StringComparison.Ordinal);
        Assert.Contains("UpdatePlaylistPosterFromUrlAsync", syncSource, StringComparison.Ordinal);
        Assert.Contains("UpdateItemPrimaryImageFromUrlAsync", syncSource, StringComparison.Ordinal);
        Assert.DoesNotContain("VerifyJellyfinPrimaryImageChangedAsync", syncSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetJellyfinPrimaryImageTagAsync", syncSource, StringComparison.Ordinal);
        Assert.Contains("ResolveJellyfinVisualFromPlaylistImage", syncSource, StringComparison.Ordinal);
        Assert.Contains("ResolveJellyfinVisualFromManagedImage", syncSource, StringComparison.Ordinal);
        Assert.Contains("SyncPlaylistArtworkOnlyAsync", syncSource, StringComparison.Ordinal);
        Assert.Contains("SyncPlaylistArtworkOnlyAsync", controllerSource, StringComparison.Ordinal);
        Assert.Contains("ResolveStoredVisualForArtworkSync", syncSource, StringComparison.Ordinal);
        Assert.Contains("PlaylistVisualService.IsManagedVisualUrl(managedImageUrl)", syncSource, StringComparison.Ordinal);
        Assert.Contains("ResolveUnmaterializedVisualUrl(remoteUrl, reuseSavedArtwork, existingUrl)", visualSource, StringComparison.Ordinal);
        Assert.Contains("return remoteUrl;", visualSource, StringComparison.Ordinal);
        Assert.Contains("ResolveActiveFileName", visualSource, StringComparison.Ordinal);
        Assert.Contains("GetCachedPlaylistTrackCandidatesAsync", postDownloadSource, StringComparison.Ordinal);
        Assert.Contains("SyncAvailablePlaylistTracksAsync", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncAvailablePlaylistTracksToTargetAsync", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RunChangedFilesAndWaitForIngestionAsync", postDownloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RunChangedFoldersAsync", postDownloadSource, StringComparison.Ordinal);

        var artworkOnlyBody = ExtractMethodBody(syncSource, "public async Task<PlaylistSyncResult> SyncPlaylistArtworkOnlyAsync(");
        Assert.Contains("ResolveTargetServicesAsync(preference, cancellationToken)", artworkOnlyBody, StringComparison.Ordinal);
        Assert.Contains("CombinePlaylistSyncTargetResults(results)", artworkOnlyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveTargetServiceAsync(preference, cancellationToken)", artworkOnlyBody, StringComparison.Ordinal);

        var jellyfinArtworkBody = ExtractMethodBody(syncSource, "private async Task<bool> SyncJellyfinPlaylistArtworkAsync(");
        Assert.Contains("ResolveJellyfinVisualFromPlaylistImage(playlist)", jellyfinArtworkBody, StringComparison.Ordinal);
        Assert.Contains("ResolveJellyfinVisualFromManagedImage(", jellyfinArtworkBody, StringComparison.Ordinal);
        Assert.Contains("UpdateItemPrimaryImageFromFileAsync", jellyfinArtworkBody, StringComparison.Ordinal);

        var jellyfinArtworkOnlyBody = ExtractMethodBody(syncSource, "private async Task<PlaylistSyncResult> SyncJellyfinPlaylistArtworkOnlyAsync(");
        Assert.Contains("ResolveExistingTargetPlaylistId(preference, JellyfinService)", jellyfinArtworkOnlyBody, StringComparison.Ordinal);
        Assert.Contains("FindPlaylistIdByNameAsync", jellyfinArtworkOnlyBody, StringComparison.Ordinal);
        Assert.True(
            jellyfinArtworkOnlyBody.IndexOf("ResolveExistingTargetPlaylistId(preference, JellyfinService)", StringComparison.Ordinal)
            < jellyfinArtworkOnlyBody.IndexOf("FindPlaylistIdByNameAsync", StringComparison.Ordinal),
            "Jellyfin artwork-only sync must use the persisted target playlist id before falling back to a name lookup.");

        var plexArtworkBody = ExtractMethodBody(syncSource, "private async Task SyncPlexPlaylistArtworkAsync(");
        var navidromeArtworkBody = ExtractMethodBody(syncSource, "private async Task<bool> SyncNavidromePlaylistArtworkAsync(");
        Assert.DoesNotContain("GetStoredVisualFromManagedUrl", plexArtworkBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveJellyfinVisual", plexArtworkBody, StringComparison.Ordinal);
        Assert.DoesNotContain("GetStoredVisualFromManagedUrl", navidromeArtworkBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveJellyfinVisual", navidromeArtworkBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaylistVisualService_ResolvesExactManagedVisualFileBeforeActiveFallback()
    {
        var service = new PlaylistVisualService(
            new StubHttpClientFactory(),
            new StubWebHostEnvironment(_tempRoot),
            NullLogger<PlaylistVisualService>.Instance);
        const string source = "spotify";
        const string sourceId = "playlist-1";

        await service.StoreUploadedVisualAsync(
            source,
            sourceId,
            new byte[] { 0xFF, 0xD8, 0x01, 0xFF, 0xD9 },
            "image/jpeg",
            CancellationToken.None);
        var oldVisual = service.GetStoredVisuals(source, sourceId).Single();

        await service.StoreUploadedVisualAsync(
            source,
            sourceId,
            new byte[] { 0xFF, 0xD8, 0x02, 0xFF, 0xD9 },
            "image/jpeg",
            CancellationToken.None);
        var newVisual = service.GetStoredVisuals(source, sourceId)
            .Single(visual => !string.Equals(visual.FilePath, oldVisual.FilePath, StringComparison.OrdinalIgnoreCase));

        Assert.True(service.SetActiveVisual(source, sourceId, Path.GetFileName(oldVisual.FilePath)));

        var resolved = service.GetStoredVisualFromManagedUrl(source, sourceId, newVisual.Url);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFileName(newVisual.FilePath), Path.GetFileName(resolved!.FilePath));
    }

    [Fact]
    public async Task PlaylistVisualService_NeutralizesLineBreaksInLoggedIdentifiers()
    {
        var logger = new CaptureLogger<PlaylistVisualService>();
        var service = new PlaylistVisualService(
            new StubHttpClientFactory(new FailingHttpMessageHandler()),
            new StubWebHostEnvironment(_tempRoot),
            logger);

        var result = await service.ResolveManagedVisualUrlAsync(
            "spotify\r\nFORGED-SOURCE",
            "playlist-1\r\nFORGED-ID",
            "Playlist",
            "https://example.com/cover.jpg",
            reuseSavedArtwork: false,
            CancellationToken.None,
            forceRefresh: true);

        Assert.Equal("https://example.com/cover.jpg", result);
        var message = Assert.Single(logger.Messages);
        Assert.DoesNotContain('\r', message);
        Assert.DoesNotContain('\n', message);
        Assert.Contains("FORGED-SOURCE", message, StringComparison.Ordinal);
        Assert.Contains("FORGED-ID", message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistPreferenceApi_NormalizesAndValidatesIncomingSettings()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryPlaylistWatchlistApiController.cs"));

        Assert.Contains("GetValidFolderIdsAsync", source, StringComparison.Ordinal);
        Assert.Contains("ValidatePlaylistPreferenceRequest", source, StringComparison.Ordinal);
        Assert.Contains("WatchlistPreferenceNormalizer.PreferredEngine", source, StringComparison.Ordinal);
        Assert.Contains("WatchlistPreferenceNormalizer.DownloadVariantMode", source, StringComparison.Ordinal);
        Assert.Contains("WatchlistPreferenceNormalizer.SyncMode", source, StringComparison.Ordinal);
        Assert.Contains("WatchlistPreferenceNormalizer.RoutingRules", source, StringComparison.Ordinal);
        Assert.Contains("WatchlistPreferenceNormalizer.BlockRules", source, StringComparison.Ordinal);
        Assert.Contains("ApplyRoutingRulesGlobally", source, StringComparison.Ordinal);
        Assert.Contains("routing-rules/apply-globally", source, StringComparison.Ordinal);
        Assert.Contains("SaveGlobalRoutingTemplateAsync", source, StringComparison.Ordinal);
        Assert.Contains("ApplyGlobalRoutingTemplateToPlaylistAsync", source, StringComparison.Ordinal);
        Assert.Contains("GlobalRoutingTemplateSource", source, StringComparison.Ordinal);
        Assert.Contains("GlobalRoutingTemplateSourceId", source, StringComparison.Ordinal);
        Assert.Contains("Routing destination folder was not found or is disabled.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtistPreferenceApi_RejectsNullRequestBody()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryWatchlistApiController.cs"));

        Assert.Contains("if (request is null)", source, StringComparison.Ordinal);
        Assert.Contains("Artist watchlist preference request is required.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectWatchlistTriggers_UseHostedSchedulerInsteadOfDirectQueueing()
    {
        var repoRoot = ResolveRepoRoot();
        var playlistController = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryPlaylistWatchlistApiController.cs"));
        var artistController = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryWatchlistApiController.cs"));

        Assert.DoesNotContain("CheckPlaylistWatchItemAsync", playlistController, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckArtistWatchItemAsync", artistController, StringComparison.Ordinal);
        Assert.Contains("_watchlistCoordinator.TriggerRunOnceAsync", playlistController, StringComparison.Ordinal);
        Assert.Contains("_watchlistCoordinator.TriggerRunOnceAsync", artistController, StringComparison.Ordinal);
        Assert.Contains("RefreshPlaylistMetadataOnlyAsync", playlistController, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistSettingsModal_UsesReliableMobileActionsAndWaitsForSave()
    {
        var repoRoot = ResolveRepoRoot();
        var siteScript = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "site.js"));
        var watchlistScript = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "library-watchlists.js"));
        var siteCss = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "css", "site.css"));

        Assert.Contains("matchMedia?.('(max-width: 768px)')", siteScript, StringComparison.Ordinal);
        Assert.Contains("if (!enabled || mobileViewport)", siteScript, StringComparison.Ordinal);
        Assert.Contains("typeof button.onClick === 'function'", siteScript, StringComparison.Ordinal);
        Assert.Contains("modal.toggleAttribute('aria-busy', busy)", siteScript, StringComparison.Ordinal);
        Assert.Contains("busyLabel: 'Saving...'", watchlistScript, StringComparison.Ordinal);
        Assert.Contains("onClick: () => savePlaylistSettingsFromPanel", watchlistScript, StringComparison.Ordinal);
        Assert.Contains("const settingsResult = await globalThis.DeezSpoTag.ui.showModal", watchlistScript, StringComparison.Ordinal);
        Assert.Contains("openPlaylistSettingsPanel(source, sourceId, playlistName, playlistPrefsPromise)", watchlistScript, StringComparison.Ordinal);
        var openSettingsFunction = ExtractMethodBody(watchlistScript, "async function openPlaylistSettingsPanel");
        Assert.Contains("const trackCandidatesPromise = fetchJson(", openSettingsFunction, StringComparison.Ordinal);
        Assert.DoesNotContain("trackCandidatesResponse] = await Promise.all", openSettingsFunction, StringComparison.Ordinal);
        Assert.True(
            openSettingsFunction.IndexOf("const settingsResult = await globalThis.DeezSpoTag.ui.showModal", StringComparison.Ordinal)
            < openSettingsFunction.IndexOf("if (settingsResult?.value === 'save')", StringComparison.Ordinal));
        Assert.Contains("if (settingsResult?.value === 'save')", watchlistScript, StringComparison.Ordinal);
        Assert.Contains("void refreshPlaylistSettingsViewsAfterSave();", watchlistScript, StringComparison.Ordinal);
        Assert.Contains("return true;", watchlistScript, StringComparison.Ordinal);
        Assert.Contains("return false;", watchlistScript, StringComparison.Ordinal);
        var saveFunction = ExtractMethodBody(watchlistScript, "async function savePlaylistSettingsFromPanel");
        Assert.DoesNotContain("loadPlaylistBlockedRules()", saveFunction, StringComparison.Ordinal);
        Assert.DoesNotContain("loadPlaylistWatchlist()", saveFunction, StringComparison.Ordinal);
        var refreshFunction = ExtractMethodBody(watchlistScript, "async function refreshPlaylistSettingsViewsAfterSave");
        Assert.Contains("watchlist-blocked-content", refreshFunction, StringComparison.Ordinal);
        Assert.Contains("await loadPlaylistBlockedRules();", refreshFunction, StringComparison.Ordinal);
        Assert.DoesNotContain("loadPlaylistWatchlist()", refreshFunction, StringComparison.Ordinal);
        Assert.Contains(".app-modal-dialog.playlist-settings-modal.is-resizable", siteCss, StringComparison.Ordinal);
        Assert.Contains(".app-modal-resize-handle", siteCss, StringComparison.Ordinal);
        Assert.DoesNotContain(".app-modal-action {\n        flex: 1 1 0;", siteCss, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistSettingsModal_DoesNotRenderTrackStateSection()
    {
        var repoRoot = ResolveRepoRoot();
        var watchlistScript = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "library-watchlists.js"));
        var libraryCss = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "css", "library.css"));

        Assert.DoesNotContain("Track state", watchlistScript, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist-track-status-section", watchlistScript, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist-track-status-section", libraryCss, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistWatchlistLoading_DoesNotEagerLoadHiddenBlockedTabOrBlindRefreshOnLibraryUpdate()
    {
        var repoRoot = ResolveRepoRoot();
        var libraryScript = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "library.js"));
        var watchlistScript = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "library-watchlists.js"));
        var playlistController = File.ReadAllText(Path.Join(
            repoRoot,
            "DeezSpoTag.Web",
            "Controllers",
            "Api",
            "LibraryPlaylistWatchlistApiController.cs"));

        var initialLoadQueue = ExtractMethodBody(libraryScript, "function queueStandardInitialLoadTasks");
        Assert.Contains("targets.shouldLoadPlaylistWatchlist && isMediaManagementPlaylistWatchlistActive()", initialLoadQueue, StringComparison.Ordinal);
        Assert.Contains("targets.shouldLoadPlaylistBlockedRules && isMediaManagementBlockedWatchlistActive()", initialLoadQueue, StringComparison.Ordinal);

        var libraryUpdateRefresh = ExtractMethodBody(libraryScript, "async function refreshLibraryViewsAfterLibraryUpdate");
        Assert.Contains("playlistWatchlistContainer && isMediaManagementPlaylistWatchlistActive()", libraryUpdateRefresh, StringComparison.Ordinal);
        Assert.Contains("playlistWatchlistContainer.dataset.stale = 'true';", libraryUpdateRefresh, StringComparison.Ordinal);
        Assert.Contains("blockedWatchlistContainer && isMediaManagementBlockedWatchlistActive()", libraryUpdateRefresh, StringComparison.Ordinal);
        Assert.Contains("blockedWatchlistContainer.dataset.stale = 'true';", libraryUpdateRefresh, StringComparison.Ordinal);

        var tabHydration = ExtractMethodBody(watchlistScript, "function bindPlaylistWatchlistTabHydration");
        Assert.Contains("watchlist-blocked-tab", tabHydration, StringComparison.Ordinal);
        Assert.Contains("watchlist-blocked-content", tabHydration, StringComparison.Ordinal);
        Assert.Contains("const ensureActiveWatchlistSubTabLoaded", tabHydration, StringComparison.Ordinal);
        Assert.Contains("watchlistTab?.addEventListener('shown.bs.tab', ensureActiveWatchlistSubTabLoaded)", tabHydration, StringComparison.Ordinal);
        Assert.Contains("blockedSubTab?.addEventListener('shown.bs.tab', ensureBlockedWatchlistLoaded)", tabHydration, StringComparison.Ordinal);
        Assert.Contains("void loadPlaylistBlockedRules();", tabHydration, StringComparison.Ordinal);
        Assert.Contains("!isWatchlistParentActive()", tabHydration, StringComparison.Ordinal);

        var getAll = ExtractMethodBody(playlistController, "public async Task<IActionResult> GetAll");
        Assert.DoesNotContain("BuildPlaylistPresentationSummaryAsync", getAll, StringComparison.Ordinal);
        Assert.DoesNotContain("[HttpGet(\"presentation-summaries\")]", playlistController, StringComparison.Ordinal);
        Assert.DoesNotContain("fetchJson('/api/library/playlists/presentation-summaries')", watchlistScript, StringComparison.Ordinal);
        Assert.Contains("renderPlaylistWatchlistPresentationBadges(item)", watchlistScript, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistSyncCompletion_RefreshesMonitoredPlaylistBadgesWithoutWaitingForTabReload()
    {
        var repoRoot = ResolveRepoRoot();
        var syncSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "PlaylistSyncService.cs"));
        var crossDeviceSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "CrossDeviceSyncService.cs"));
        var siteScript = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "site.js"));
        var watchlistScript = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "library-watchlists.js"));

        Assert.Contains("PublishWatchlistSyncUpdatedAsync(playlist, result, cancellationToken)", syncSource, StringComparison.Ordinal);
        Assert.Contains("PublishWatchlistUpdatedAsync(", syncSource, StringComparison.Ordinal);
        Assert.Contains("playlist_sync_completed", syncSource, StringComparison.Ordinal);
        Assert.Contains("SendAsync(\"watchlistUpdated\"", crossDeviceSource, StringComparison.Ordinal);
        Assert.Contains("connection.on('watchlistUpdated'", siteScript, StringComparison.Ordinal);
        Assert.Contains("deezspotag:watchlist-updated", siteScript, StringComparison.Ordinal);
        Assert.Contains("bindPlaylistWatchlistRealtimeRefresh", watchlistScript, StringComparison.Ordinal);
        Assert.Contains("deezspotag:watchlist-updated", watchlistScript, StringComparison.Ordinal);
        Assert.Contains("void loadPlaylistWatchlist();", watchlistScript, StringComparison.Ordinal);
        Assert.Contains("container.dataset.stale = 'true';", watchlistScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Watchlist_HasOneControllerType_AndPreservesExistingUiRoutes()
    {
        var controllerTypes = typeof(WatchlistApiController).Assembly.GetTypes()
            .Where(type => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(type))
            .Where(type => type.Name.Contains("Watchlist", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(typeof(WatchlistApiController), Assert.Single(controllerTypes));

        var repoRoot = ResolveRepoRoot();
        var artistSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "LibraryWatchlistApiController.cs"));
        var playlistSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "LibraryPlaylistWatchlistApiController.cs"));
        var historySource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "WatchlistHistoryApiController.cs"));
        Assert.Contains("~/api/library/watchlist", artistSource, StringComparison.Ordinal);
        Assert.Contains("[Route(\"api/library/playlists\")]", playlistSource, StringComparison.Ordinal);
        Assert.Contains("~/api/history/watchlist", historySource, StringComparison.Ordinal);
    }

    [Fact]
    public void Watchlist_UiSettingIsTheOnlyEnablementAuthority()
    {
        var repoRoot = ResolveRepoRoot();
        var hostedSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "WatchlistRunCoordinator.cs"));
        var settingsControllerSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "SettingsApiController.cs"));
        var appSettingsSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "appsettings.json"));
        Assert.DoesNotContain("BackgroundAutomationPolicy.IsEnabled(_configuration, \"Watchlist\")", hostedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Watchlist\": { \"Enabled\"", appSettingsSource, StringComparison.Ordinal);
        Assert.Contains("!persisted.WatchEnabled && settings.WatchEnabled", settingsControllerSource, StringComparison.Ordinal);
        Assert.Contains("TriggerRunOnceAsync", settingsControllerSource, StringComparison.Ordinal);
        Assert.Contains("ResumePendingJobsAsync", settingsControllerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Watchlist_FinalizationPersistsWhileUiAutomationIsDisabled()
    {
        var repoRoot = ResolveRepoRoot();
        var syncSource = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "WatchlistPostDownloadSyncService.cs"));
        var notifyBody = ExtractMethodBody(syncSource, "public async ValueTask RequestAllPlaylistSyncAsync(");
        Assert.DoesNotContain("if (!IsWatchlistEnabled())", notifyBody, StringComparison.Ordinal);
        Assert.Contains("EnqueueWatchlistAllPlaylistSyncJobsAsync", notifyBody, StringComparison.Ordinal);
        Assert.Contains("if (IsWatchlistEnabled())", syncSource, StringComparison.Ordinal);
        Assert.Contains("ClaimDueWatchlistSyncJobsAsync", syncSource, StringComparison.Ordinal);
        Assert.Contains("while (!stoppingToken.IsCancellationRequested)", syncSource, StringComparison.Ordinal);
        Assert.Contains("the worker will remain active and retry", syncSource, StringComparison.Ordinal);
        Assert.Contains("RenewWatchlistSyncJobLeaseAsync", syncSource, StringComparison.Ordinal);
        Assert.Contains("HasWatchlistReconciliationRequestAsync", syncSource, StringComparison.Ordinal);
        Assert.Contains("SyncAvailablePlaylistTracksAsync", syncSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncAvailablePlaylistTracksToTargetAsync", syncSource, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _configScope.Dispose();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void WatchlistConsolidation_HasSingleCoordinatorStateHistoryAndAdmissionOwners()
    {
        var repoRoot = ResolveRepoRoot();
        var serviceRoot = Path.Join(repoRoot, "DeezSpoTag.Web", "Services");
        var coordinator = File.ReadAllText(Path.Join(serviceRoot, "WatchlistRunCoordinator.cs"));
        var admission = File.ReadAllText(Path.Join(serviceRoot, "WatchlistQueueAdmissionService.cs"));
        var playlist = File.ReadAllText(Path.Join(serviceRoot, "WatchlistEngine.cs"));
        var postSync = File.ReadAllText(Path.Join(serviceRoot, "WatchlistPostDownloadSyncService.cs"));

        Assert.False(File.Exists(Path.Join(serviceRoot, "PlaylistWatchHostedService.cs")));
        Assert.False(File.Exists(Path.Join(serviceRoot, "WatchlistRunQueueBudgetService.cs")));
        Assert.Contains("WatchlistTriggerRequest", coordinator, StringComparison.Ordinal);
        Assert.Contains("TriggerPlaylistOnceAsync", coordinator, StringComparison.Ordinal);
        Assert.Contains("EvaluateBatchAsync", admission, StringComparison.Ordinal);
        Assert.Contains("WatchlistStateService", admission, StringComparison.Ordinal);
        Assert.Contains("WatchlistHistoryService", postSync, StringComparison.Ordinal);
        Assert.Contains("PlaylistWatchReconciler", playlist, StringComparison.Ordinal);
        Assert.Contains("WatchlistQueueService", playlist, StringComparison.Ordinal);
        Assert.Contains("IPlaylistSourceAdapter", playlist, StringComparison.Ordinal);
        Assert.Contains("WatchlistSelectionPolicy", playlist, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchlistHistory_UsesStableItemIdentityAndOneWriter()
    {
        var repoRoot = ResolveRepoRoot();
        var serviceRoot = Path.Join(repoRoot, "DeezSpoTag.Web", "Services");
        var historyOwner = File.ReadAllText(Path.Join(serviceRoot, "WatchlistPostDownloadSyncService.cs"));
        var artist = File.ReadAllText(Path.Join(serviceRoot, "ArtistWatchService.cs"));
        var playlist = File.ReadAllText(Path.Join(serviceRoot, "WatchlistEngine.cs"));
        var ui = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "wwwroot", "js", "library-watchlists.js"));

        Assert.Contains("ArtistItemKey(long artistId)", historyOwner, StringComparison.Ordinal);
        Assert.Contains("PlaylistItemKey(string source, string sourceId)", historyOwner, StringComparison.Ordinal);
        Assert.Contains("_watchlistHistory.RecordAsync", artist, StringComparison.Ordinal);
        Assert.Contains("_watchlistHistory.RecordAsync", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain("AddWatchlistHistoryAsync", artist, StringComparison.Ordinal);
        Assert.DoesNotContain("AddWatchlistHistoryAsync", playlist, StringComparison.Ordinal);
        Assert.Contains("detectedByItemKey", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("detectedByName", ui, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualUnavailableApi_IsOutsideWatchlistControllerWithRoutesPreserved()
    {
        var repoRoot = ResolveRepoRoot();
        var watchlistController = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "Api", "LibraryPlaylistWatchlistApiController.cs"));
        var activitiesController = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Controllers", "ActivitiesController.cs"));

        Assert.DoesNotContain("manual-unavailable", watchlistController, StringComparison.Ordinal);
        Assert.Contains("~/api/library/playlists/manual-unavailable", activitiesController, StringComparison.Ordinal);
        Assert.Contains("~/api/library/playlists/manual-unavailable/tracklist", activitiesController, StringComparison.Ordinal);
        Assert.Contains("~/api/library/playlists/manual-unavailable/{id:long}", activitiesController, StringComparison.Ordinal);
    }

    private static PlaylistWatchTrackStatusDto CreatePlaylistTrackStatus(
        string status,
        long? localTrackId = null,
        string? identityStatus = null,
        string? syncStatus = null)
        => new(
            TrackSourceId: "track-1",
            Isrc: null,
            Status: status,
            UpdatedAt: DateTimeOffset.UtcNow,
            UnavailableReason: null,
            UnavailableSinceUtc: null,
            UnavailableLastCheckedUtc: null,
            UnavailableNextRecheckUtc: null,
            UnavailableSettingsFingerprint: null,
            LocalTrackId: localTrackId,
            IdentityStatus: identityStatus,
            SyncStatus: syncStatus,
            TargetService: "plex");

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
        if (bodyStart + 1 < source.Length
            && source[bodyStart + 1] == '}'
            && source.IndexOf('{', bodyStart + 2) is var nextBodyStart
            && nextBodyStart >= 0)
        {
            bodyStart = nextBodyStart;
        }

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

        throw new InvalidOperationException($"Missing method body end for: {methodMarker}");
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public StubWebHostEnvironment(string rootPath)
        {
            ContentRootPath = rootPath;
            ContentRootFileProvider = new PhysicalFileProvider(rootPath);
            WebRootPath = rootPath;
            WebRootFileProvider = new PhysicalFileProvider(rootPath);
        }

        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "DeezSpoTag.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler? _handler;

        public StubHttpClientFactory(HttpMessageHandler? handler = null)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => _handler is null
            ? new HttpClient()
            : new HttpClient(_handler, disposeHandler: false);
    }

    private sealed class FailingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("Simulated playlist visual failure.");
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
