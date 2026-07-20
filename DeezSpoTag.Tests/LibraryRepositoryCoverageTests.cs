using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LibraryRepositoryCoverageTests : IAsyncLifetime
{
    private static readonly string[] SoundtrackGenres = ["Soundtrack"];
    private static readonly string[] SpotifyTrackSourceIds = ["sp-song-1", "sp-song-2", "sp-missing"];
    private static readonly string[] PlexMetadataGenres = ["Score"];
    private static readonly string[] PlexMetadataMoods = ["Epic"];
    private static readonly string[] PlexRatingKeys = ["rk-1"];
    private static readonly string[] HappyMoodTags = ["happy"];
    private static readonly string[] MixCoverUrls = ["https://example.com/mix.jpg"];
    private static readonly string[] EssentiaGenreTags = ["soundtrack"];
    private static readonly string[] LastfmGenreTags = ["score"];
    private static readonly string[] SharedQueueUuids = ["queue-shared-1"];
    private static readonly string[] UppercaseSpotifyTrackSourceIds = ["SP-SONG-1"];

    private sealed record SeededLibrary(
        long LibraryId,
        FolderDto Folder,
        long ArtistId,
        long AlbumId,
        IReadOnlyDictionary<string, long> TrackIdsByTitle,
        IReadOnlyDictionary<string, string> TrackPathsByTitle);

    private string _tempRoot = string.Empty;
    private string _dbPath = string.Empty;
    private IConfiguration _configuration = default!;
    private LibraryRepository _repository = default!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "deezspotag-library-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempRoot);

        _dbPath = Path.Join(_tempRoot, "library.db");
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Library"] = $"Data Source={_dbPath}"
            })
            .Build();

        var dbService = new LibraryDbService(_configuration, NullLogger<LibraryDbService>.Instance);
        await dbService.EnsureSchemaAsync();

        _repository = new LibraryRepository(_configuration, NullLogger<LibraryRepository>.Instance);
    }

    public Task DisposeAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_tempRoot) && Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ScanInfo_Settings_And_AutomationState_RoundTrip()
    {
        var initialScan = await _repository.GetScanInfoAsync();
        Assert.Null(initialScan.LastRunUtc);
        Assert.Equal(0, initialScan.ArtistCount);
        Assert.Equal(0, initialScan.AlbumCount);
        Assert.Equal(0, initialScan.TrackCount);

        var scanWrite = new LibraryScanInfo(DateTimeOffset.UtcNow, 14, 31, 220);
        await _repository.SaveScanInfoAsync(scanWrite);

        var scanRead = await _repository.GetScanInfoAsync();
        Assert.NotNull(scanRead.LastRunUtc);
        Assert.Equal(14, scanRead.ArtistCount);
        Assert.Equal(31, scanRead.AlbumCount);
        Assert.Equal(220, scanRead.TrackCount);

        var defaultSettings = await _repository.GetSettingsAsync();
        Assert.False(defaultSettings.LivePreviewIngest);
        Assert.False(defaultSettings.EnableSignalAnalysis);

        var updatedSettings = await _repository.UpdateSettingsAsync(new LibrarySettingsDto(
            LivePreviewIngest: true,
            EnableSignalAnalysis: true));
        Assert.True(updatedSettings.LivePreviewIngest);
        Assert.True(updatedSettings.EnableSignalAnalysis);

        var automationDefault = await _repository.GetQualityScannerAutomationSettingsAsync();
        Assert.False(automationDefault.Enabled);
        Assert.Equal("watchlist", automationDefault.Scope);

        var automationUpdated = await _repository.UpdateQualityScannerAutomationSettingsAsync(
            new QualityScannerAutomationSettingsDto(
                Enabled: true,
                IntervalMinutes: 120,
                Scope: "all",
                FolderId: null,
                QueueAtmosAlternatives: true,
                CooldownMinutes: 240,
                LastStartedUtc: null,
                LastFinishedUtc: null));
        Assert.True(automationUpdated.Enabled);
        Assert.Equal("all", automationUpdated.Scope);
        Assert.Equal(120, automationUpdated.IntervalMinutes);
        Assert.Equal(240, automationUpdated.CooldownMinutes);

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        var finishedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _repository.MarkQualityScannerAutomationStartedAsync(startedAt);
        await _repository.MarkQualityScannerAutomationFinishedAsync(finishedAt);

        var automationFinal = await _repository.GetQualityScannerAutomationSettingsAsync();
        Assert.NotNull(automationFinal.LastStartedUtc);
        Assert.NotNull(automationFinal.LastFinishedUtc);
    }

    [Fact]
    public async Task Logs_And_QualityScannerRun_Workflow_Completes()
    {
        await _repository.AddLogAsync(new LibraryLogEntry(DateTimeOffset.UtcNow.AddMinutes(-2), "info", "first"));
        await _repository.AddLogAsync(new LibraryLogEntry(DateTimeOffset.UtcNow.AddMinutes(-1), "warn", "second"));

        var allLogs = await _repository.GetLogsAsync();
        Assert.True(allLogs.Count >= 2);

        var latestLog = await _repository.GetLogsAsync(limit: 1);
        Assert.Single(latestLog);

        await _repository.ClearLogsAsync();
        var afterClear = await _repository.GetLogsAsync();
        Assert.Empty(afterClear);

        var runId = await _repository.StartQualityScannerRunAsync(
            trigger: "manual",
            scope: "watchlist",
            folderId: null,
            queueAtmosAlternatives: true);
        Assert.True(runId > 0);

        await _repository.UpdateQualityScannerRunProgressAsync(
            runId,
            new QualityScannerRunProgressDto(
                TotalTracks: 50,
                ProcessedTracks: 12,
                QualityMet: 5,
                LowQuality: 7,
                UpgradesQueued: 2,
                AtmosQueued: 1,
                DuplicateSkipped: 3,
                MatchMissed: 4),
            phase: "scan");

        await _repository.CompleteQualityScannerRunAsync(runId, "finished", null);
    }

    [Fact]
    public async Task FolderLifecycle_And_AliasLifecycle_Work()
    {
        var added = await _repository.AddFolderAsync(
            new LibraryRepository.FolderUpsertInput(
                RootPath: "/music/library-a",
                DisplayName: "Library A",
                Enabled: true,
                LibraryName: "Music",
                DesiredQuality: "27",
                ConvertEnabled: true,
                ConvertFormat: "flac",
                ConvertBitrate: "320",
                AutoTagProfileId: "profile-default"));

        Assert.True(added.Id > 0);
        Assert.Equal("Library A", added.DisplayName);
        Assert.Equal("Music", added.LibraryName);
        Assert.True(added.ConvertEnabled);
        Assert.Equal("flac", added.ConvertFormat);
        Assert.Equal("320", added.ConvertBitrate);
        Assert.Equal("profile-default", added.AutoTagProfileId);
        Assert.True(added.AutoTagEnabled);

        var libraries = await _repository.GetLibrariesAsync();
        Assert.Contains(libraries, lib => lib.Name == "Music");

        var folders = await _repository.GetFoldersAsync();
        Assert.Contains(folders, folder => folder.Id == added.Id);

        var updated = await _repository.UpdateFolderAsync(
            added.Id,
            new LibraryRepository.FolderUpsertInput(
                RootPath: "/music/library-a-renamed",
                DisplayName: "Library A+",
                Enabled: true,
                LibraryName: "Music",
                DesiredQuality: "atmos",
                ConvertEnabled: true,
                ConvertFormat: "ALAC",
                ConvertBitrate: "AUTO"));
        Assert.NotNull(updated);
        Assert.Equal("Library A+", updated!.DisplayName);
        Assert.Equal("atmos", updated.DesiredQuality);
        Assert.Equal("alac", updated.ConvertFormat);
        Assert.Equal("AUTO", updated.ConvertBitrate);
        Assert.Equal("profile-default", updated.AutoTagProfileId);

        var withProfile = await _repository.UpdateFolderProfileAsync(added.Id, "profile-1");
        Assert.NotNull(withProfile);
        Assert.Equal("profile-1", withProfile!.AutoTagProfileId);

        var withAutoTagDisabled = await _repository.UpdateFolderAutoTagEnabledAsync(added.Id, false);
        Assert.NotNull(withAutoTagDisabled);
        Assert.False(withAutoTagDisabled!.AutoTagEnabled);

        var resolved = await _repository.ResolveFolderForPathAsync("/music/library-a-renamed/Artist/Album/track.flac");
        Assert.NotNull(resolved);
        Assert.Equal(added.Id, resolved!.Id);

        var alias = await _repository.AddFolderAliasAsync(added.Id, "Alias A");
        Assert.True(alias.Id > 0);

        var aliases = await _repository.GetFolderAliasesAsync(added.Id);
        Assert.Contains(aliases, entry => entry.Id == alias.Id && entry.AliasName == "Alias A");

        var aliasDeleted = await _repository.DeleteFolderAliasAsync(alias.Id);
        Assert.True(aliasDeleted);

        await _repository.DisableFolderAsync(added.Id);
        var folderDeleted = await _repository.DeleteFolderAsync(added.Id);
        Assert.True(folderDeleted);
    }

    [Fact]
    public async Task Stats_And_Cleanup_OnEmptyDatabase_AreStable()
    {
        var hasLocalData = await _repository.HasLocalLibraryDataAsync();
        Assert.False(hasLocalData);

        var missingCleaned = await _repository.CleanupMissingFilesAsync();
        Assert.Equal(0, missingCleaned);

        var stats = await _repository.GetLibraryStatsAsync();
        Assert.Equal(0, stats.TotalArtists);
        Assert.Equal(0, stats.TotalAlbums);
        Assert.Equal(0, stats.TotalTracks);

        var clearResult = await _repository.ClearLibraryDataAsync();
        Assert.Equal(0, clearResult.ArtistsRemoved);
        Assert.Equal(0, clearResult.AlbumsRemoved);
        Assert.Equal(0, clearResult.TracksRemoved);
    }

    [Fact]
    public async Task LocalScanIngest_PopulatesSearch_Links_AndLookups()
    {
        var seeded = await SeedLibraryAsync(
            ("Song One", "dz-song-1", "sp-song-1", "am-song-1"));

        var stats = await _repository.GetLibraryStatsAsync();
        Assert.Equal(1, stats.TotalArtists);
        Assert.Equal(1, stats.TotalAlbums);
        Assert.Equal(1, stats.TotalTracks);

        var hasLocalData = await _repository.HasLocalLibraryDataAsync();
        Assert.True(hasLocalData);

        var trackId = seeded.TrackIdsByTitle["Song One"];
        var filePath = seeded.TrackPathsByTitle["Song One"];
        var resolvedTrackId = await _repository.GetTrackIdForFilePathAsync(filePath);
        Assert.Equal(trackId, resolvedTrackId);

        var primaryPath = await _repository.GetTrackPrimaryFilePathAsync(trackId);
        Assert.Equal(filePath, primaryPath);

        var trackLinks = await _repository.GetTrackSourceLinksAsync(trackId);
        Assert.NotNull(trackLinks);
        Assert.Equal("dz-song-1", trackLinks!.DeezerTrackId);
        Assert.Equal("sp-song-1", trackLinks.SpotifyTrackId);
        Assert.Equal("am-song-1", trackLinks.AppleTrackId);

        var albumTracks = await _repository.GetAlbumTracksAsync(seeded.AlbumId);
        Assert.Single(albumTracks);
        Assert.Equal(trackId, albumTracks[0].Id);

        var albumTrackLinks = await _repository.GetAlbumTrackSourceLinksAsync(seeded.AlbumId);
        Assert.True(albumTrackLinks.TryGetValue(trackId, out var albumTrackLink));
        Assert.Equal("dz-song-1", albumTrackLink.DeezerTrackId);

        var offlineTracks = await _repository.SearchTracksAsync("%song%");
        Assert.Contains(offlineTracks, item => item.DeezerId == "dz-song-1");

        var trackResults = await _repository.SearchTracksWithIdsAsync("%song%");
        Assert.Contains(trackResults, item => item.TrackId == trackId);

        var albums = await _repository.SearchAlbumsAsync("%album%");
        Assert.Contains(albums, item => item.Title == "Album One" && item.ArtistName == "Artist One");

        var artists = await _repository.SearchArtistsAsync("%artist%");
        Assert.Contains(artists, item => item.Name == "Artist One");

        var trackAudioInfo = await _repository.GetTrackAudioInfoAsync(trackId);
        Assert.NotNull(trackAudioInfo);
        Assert.Equal("Song One", trackAudioInfo!.Title);

        var existsTrackSource = await _repository.ExistsTrackSourceAsync("deezer", "dz-song-1");
        Assert.True(existsTrackSource);

        var libraryTrackIds = await _repository.GetTrackIdsForLibraryAsync(seeded.LibraryId);
        Assert.Contains(trackId, libraryTrackIds);
    }

    [Fact]
    public async Task PlaylistWatchlist_And_Blocklist_RoundTrip_Works()
    {
        var seeded = await SeedLibraryAsync(
            ("Song One", "dz-song-1", "sp-song-1", "am-song-1"));

        var added = await _repository.AddPlaylistWatchlistAsync(
            source: "  SpOtIfY  ",
            sourceId: "  pl-123  ",
            metadata: new PlaylistWatchlistMetadataInput(
                "Road Mix",
                "https://example.com/cover.jpg",
                "Playlist description",
                24,
                OwnerName: "Playlist Curator"));
        Assert.NotNull(added);
        Assert.Equal("spotify", added!.Source);
        Assert.Equal("pl-123", added.SourceId);
        Assert.Equal("Playlist Curator", added.OwnerName);

        var renamed = await _repository.AddPlaylistWatchlistAsync(
            source: "spotify",
            sourceId: "pl-123",
            metadata: new PlaylistWatchlistMetadataInput(
                "Road Mix Renamed",
                "https://example.com/new-cover.jpg",
                "Renamed description",
                25));
        Assert.NotNull(renamed);
        Assert.Equal(added.Id, renamed!.Id);
        Assert.Equal("Road Mix Renamed", renamed.Name);

        var watchlisted = await _repository.IsPlaylistWatchlistedAsync("spotify", "pl-123");
        Assert.True(watchlisted);
        Assert.True(await _repository.IsPlaylistWatchlistedAsync(" SPOTIFY ", "  pl-123 "));

        await _repository.UpdatePlaylistWatchlistMetadataAsync(
            source: " SPOTIFY ",
            sourceId: " pl-123 ",
            metadata: new PlaylistWatchlistMetadataInput(
                "Road Mix Updated",
                null,
                "Updated description",
                25));
        var watchlist = await _repository.GetPlaylistWatchlistAsync();
        Assert.Contains(
            watchlist,
            item => item.Source == "spotify"
                    && item.SourceId == "pl-123"
                    && item.Name == "Road Mix Updated"
                    && item.OwnerName == "Playlist Curator");

        var pref = await _repository.UpsertPlaylistWatchPreferenceAsync(
            new LibraryRepository.PlaylistWatchPreferenceUpsertInput(
                Source: "  SPOTIFY ",
                SourceId: " pl-123  ",
                DestinationFolderId: seeded.Folder.Id,
                Service: "plex",
                SyncTargets: ["plex", "jellyfin"],
                PreferredEngine: "native",
                DownloadEngineOrder: null,
                DownloadVariantMode: "default",
                SyncMode: "mirror",
                UpdateArtwork: true,
                ReuseSavedArtwork: false,
                RoutingRules: new List<PlaylistTrackRoutingRule>
                {
                    new("artist", "contains", "Artist One", seeded.Folder.Id, 1)
                },
                IgnoreRules: new List<PlaylistTrackBlockRule>
                {
                    new("title", "contains", "Live", 1)
                }));
        Assert.NotNull(pref);
        Assert.Equal("mirror", pref.SyncMode);
        Assert.Single(pref.RoutingRules!);
        Assert.Single(pref.IgnoreRules!);
        await _repository.UpdatePlaylistWatchTargetPlaylistIdAsync("spotify", "pl-123", "plex", "plex-playlist-1");
        await _repository.UpdatePlaylistWatchTargetPlaylistIdAsync("spotify", "pl-123", "jellyfin", "jellyfin-playlist-1");
        var boundPref = await _repository.GetPlaylistWatchPreferenceAsync("spotify", "pl-123");
        Assert.Equal("plex-playlist-1", boundPref?.PlexPlaylistId);
        Assert.Equal("jellyfin-playlist-1", boundPref?.JellyfinPlaylistId);
        Assert.Equal("spotify", pref.Source);
        Assert.Equal("pl-123", pref.SourceId);

        await _repository.UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                Source: "Spotify ",
                SourceId: " pl-123",
                SnapshotId: "snap-1",
                TrackCount: 25,
                BatchNextOffset: 10,
                BatchProcessingSnapshotId: "snap-proc-1",
                LastCheckedUtc: DateTimeOffset.UtcNow));
        var watchState = await _repository.GetPlaylistWatchStateAsync("spotify", "pl-123");
        Assert.NotNull(watchState);
        Assert.Equal("snap-1", watchState!.SnapshotId);

        await _repository.UpdatePlaylistWatchPresentationSummaryAsync(
            "spotify",
            "pl-123",
            ignoredBlockedTrackCount: 2,
            reroutedTrackCount: 3);
        var summarizedWatchlist = await _repository.GetPlaylistWatchlistAsync();
        var summarizedPlaylist = Assert.Single(summarizedWatchlist, item => item.Source == "spotify" && item.SourceId == "pl-123");
        Assert.Equal(0, summarizedPlaylist.SyncedTrackCount);
        Assert.Equal(23, summarizedPlaylist.IncompleteTrackCount);
        Assert.Equal(2, summarizedPlaylist.IgnoredBlockedTrackCount);
        Assert.Equal(3, summarizedPlaylist.ReroutedTrackCount);

        await _repository.UpsertPlaylistWatchStateAsync(
            new LibraryRepository.PlaylistWatchStateUpsertInput(
                Source: "spotify",
                SourceId: "pl-123",
                SnapshotId: "snap-2",
                TrackCount: 25,
                BatchNextOffset: null,
                BatchProcessingSnapshotId: null,
                LastCheckedUtc: DateTimeOffset.UtcNow));
        var preservedSummary = Assert.Single(
            await _repository.GetPlaylistWatchlistAsync(),
            item => item.Source == "spotify" && item.SourceId == "pl-123");
        Assert.Equal(0, preservedSummary.SyncedTrackCount);

        await _repository.UpsertPlaylistTrackCandidateCacheAsync(
            source: " Spotify ",
            sourceId: " pl-123 ",
            snapshotId: "snap-1",
            candidatesJson: "[{\"id\":\"dz-song-1\"}]");
        var cache = await _repository.GetPlaylistTrackCandidateCacheAsync("spotify", "pl-123");
        Assert.NotNull(cache);
        Assert.Equal("snap-1", cache!.SnapshotId);
        Assert.True(await _repository.DeletePlaylistTrackCandidateCacheAsync(" spotify ", " pl-123 "));
        Assert.Null(await _repository.GetPlaylistTrackCandidateCacheAsync("spotify", "pl-123"));

        await _repository.AddPlaylistWatchTracksAsync(
            " spotify ",
            " pl-123 ",
            new List<PlaylistWatchTrackInsert>
            {
                new("dz-song-1", "ISRC00000001"),
                new("dz-song-2", "ISRC00000002")
            });
        await _repository.UpdatePlaylistWatchTrackStatusAsync(" Spotify ", " pl-123 ", "dz-song-1", "completed");
        var localTrackId = seeded.TrackIdsByTitle.Values.First();
        await _repository.UpdatePlaylistWatchTrackVerificationAsync(
            "spotify",
            "pl-123",
            new PlaylistWatchTrackVerification(
                "dz-song-1",
                localTrackId,
                "identity_verified",
                "Test identity verified."));
        var localOnlySummary = Assert.Single(
            await _repository.GetPlaylistWatchlistAsync(),
            item => item.Source == "spotify" && item.SourceId == "pl-123");
        Assert.Equal(0, localOnlySummary.SyncedTrackCount);
        await _repository.ReplacePlaylistWatchTargetMembershipAsync(
            "spotify",
            "pl-123",
            "plex",
            "plex-playlist-1",
            [
                new PlaylistWatchTargetMembership(
                    "dz-song-1",
                    localTrackId,
                    "plex-track-1")
            ]);
        var restartedRepository = new LibraryRepository(
            _configuration,
            NullLogger<LibraryRepository>.Instance);
        var summaryAfterRestart = Assert.Single(
            await restartedRepository.GetPlaylistWatchlistAsync(),
            item => item.Source == "spotify" && item.SourceId == "pl-123");
        Assert.Equal(1, summaryAfterRestart.SyncedTrackCount);
        Assert.Equal(22, summaryAfterRestart.IncompleteTrackCount);
        var plexOnlyStatuses = await restartedRepository.GetPlaylistWatchTrackStatusesAsync("spotify", "pl-123");
        var plexOnlyTrack = Assert.Single(plexOnlyStatuses, status => status.TrackSourceId == "dz-song-1");
        Assert.Equal("playlist_synced", plexOnlyTrack.SyncStatus);
        Assert.Equal("plex", plexOnlyTrack.TargetService);
        Assert.Equal("plex-track-1", plexOnlyTrack.TargetItemId);
        await _repository.ReplacePlaylistWatchTargetMembershipAsync(
            "spotify",
            "pl-123",
            "jellyfin",
            "jellyfin-playlist-1",
            [new PlaylistWatchTargetMembership("dz-song-1", localTrackId, "jellyfin-track-1")]);
        summaryAfterRestart = Assert.Single(
            await restartedRepository.GetPlaylistWatchlistAsync(),
            item => item.Source == "spotify" && item.SourceId == "pl-123");
        Assert.Equal(1, summaryAfterRestart.SyncedTrackCount);
        Assert.Equal(22, summaryAfterRestart.IncompleteTrackCount);
        Assert.Equal(2, summaryAfterRestart.IgnoredBlockedTrackCount);
        Assert.Equal(3, summaryAfterRestart.ReroutedTrackCount);
        await _repository.UpdatePlaylistWatchTrackVerificationAsync(
            "spotify",
            "pl-123",
            new PlaylistWatchTrackVerification(
                "dz-song-2",
                localTrackId,
                "review",
                "Identity mismatch."));
        await _repository.ReplacePlaylistWatchTargetMembershipAsync(
            "spotify",
            "pl-123",
            "plex",
            "plex-playlist-1",
            [
                new PlaylistWatchTargetMembership("dz-song-2", localTrackId, "plex-track-2")
            ]);
        var reviewStatuses = await _repository.GetPlaylistWatchTrackStatusesAsync("spotify", "pl-123");
        var reviewStatus = Assert.Single(reviewStatuses, status => status.TrackSourceId == "dz-song-2");
        Assert.Equal("review", reviewStatus.IdentityStatus);
        Assert.Equal("review", reviewStatus.SyncStatus);
        var summaryWithReview = Assert.Single(
            await _repository.GetPlaylistWatchlistAsync(),
            item => item.Source == "spotify" && item.SourceId == "pl-123");
        Assert.Equal(1, summaryWithReview.SyncedTrackCount);

        await _repository.UpsertPlaylistWatchDownloadClaimsAsync(
            " SPOTIFY ",
            " pl-123 ",
            "dz-song-2",
            SharedQueueUuids,
            seeded.Folder.Id);
        var claims = await _repository.GetPlaylistWatchDownloadClaimsAsync("queue-shared-1");
        var claim = Assert.Single(claims);
        Assert.Equal("spotify", claim.Source);
        Assert.Equal("pl-123", claim.SourceId);
        Assert.Equal("dz-song-2", claim.TrackSourceId);
        Assert.Equal(seeded.Folder.Id, claim.DestinationFolderId);
        Assert.Equal("pending", claim.Status);
        Assert.Single(await _repository.GetPlaylistWatchDownloadClaimsAsync("queue-shared-1", status: "pending"));
        var playlistPendingClaims = await _repository.GetPlaylistWatchDownloadClaimsForPlaylistAsync(" SPOTIFY ", " pl-123 ", status: "pending");
        Assert.Single(playlistPendingClaims);
        Assert.Equal("queue-shared-1", playlistPendingClaims.Single().QueueUuid);

        var claimUpdates = await _repository.UpdatePlaylistWatchDownloadClaimStatusAsync(
            "queue-shared-1",
            "spotify",
            "pl-123",
            "dz-song-2",
            "completed");
        Assert.Equal(1, claimUpdates);
        Assert.Equal("completed", (await _repository.GetPlaylistWatchDownloadClaimsAsync("queue-shared-1")).Single().Status);
        Assert.Empty(await _repository.GetPlaylistWatchDownloadClaimsAsync("queue-shared-1", status: "pending"));

        await _repository.AddPlaylistWatchIgnoredTracksAsync(
            " SPOTIFY ",
            " pl-123 ",
            new List<PlaylistWatchIgnoreInsert> { new("dz-song-ignore", "ISRC00009999") });
        var ignoredForPlaylist = await _repository.GetPlaylistWatchIgnoredTrackIdsAsync("spotify", "pl-123");
        Assert.Contains("dz-song-ignore", ignoredForPlaylist);

        var ignoredBySource = await _repository.GetPlaylistWatchIgnoredTrackIdsBySourceAsync(" SpOtIfY ");
        Assert.Contains("dz-song-ignore", ignoredBySource);

        var removedIgnore = await _repository.RemovePlaylistWatchIgnoredTrackAsync(" spotify ", " pl-123 ", "dz-song-ignore");
        Assert.True(removedIgnore);

        var blockTrack = await _repository.UpsertDownloadBlocklistEntryAsync("track", " Song One ", enabled: true);
        Assert.NotNull(blockTrack);
        var blockArtist = await _repository.UpsertDownloadBlocklistEntryAsync("artist", "Artist One", enabled: true);
        Assert.NotNull(blockArtist);

        var entries = await _repository.GetDownloadBlocklistEntriesAsync();
        Assert.True(entries.Count >= 2);

        var match = await _repository.FindMatchingDownloadBlocklistAsync("Song One", "Other Artist", "Other Album");
        Assert.NotNull(match);
        Assert.Equal("track", match!.Field);

        var removedBlock = await _repository.RemoveDownloadBlocklistEntryAsync(blockArtist!.Id);
        Assert.True(removedBlock);

        await _repository.AddPlaylistWatchIgnoredTracksAsync(
            "spotify",
            "pl-123",
            new List<PlaylistWatchIgnoreInsert> { new("dz-song-orphan-check", null) });
        var targetJobs = await _repository.EnqueueWatchlistPlaylistSyncJobsAsync(
            "spotify",
            "pl-123");
        Assert.Equal(
            new[] { "jellyfin", "plex" },
            targetJobs.Select(static job => job.TargetService).Order(StringComparer.Ordinal).ToArray());
        Assert.All(targetJobs, static job => Assert.Equal("playlist", job.TrackId));

        var removedWatchlist = await _repository.RemovePlaylistWatchlistAsync(" Spotify ", " pl-123 ");
        Assert.True(removedWatchlist);
        Assert.False(await _repository.IsPlaylistWatchlistedAsync("spotify", "pl-123"));
        Assert.Empty(await _repository.GetPlaylistWatchIgnoredTrackIdsAsync("spotify", "pl-123"));
        Assert.DoesNotContain(
            await _repository.ClaimDueWatchlistSyncJobsAsync(100, TimeSpan.FromMinutes(1), "test-owner"),
            job => job.Source == "spotify" && job.PlaylistId == "pl-123");
    }

    [Fact]
    public async Task PlayHistory_Queries_ReturnExpectedTrackOrdering()
    {
        var seeded = await SeedLibraryAsync(
            ("Song One", "dz-song-1", "sp-song-1", "am-song-1"),
            ("Song Two", "dz-song-2", "sp-song-2", "am-song-2"),
            ("Song Three", "dz-song-3", "sp-song-3", "am-song-3"));

        var plexUserId = await _repository.EnsurePlexUserAsync(
            username: "plex-user",
            plexUserId: "plex-uid-1",
            serverUrl: "http://plex.local:32400",
            machineId: "machine-1");
        Assert.True(plexUserId > 0);

        var trackOne = seeded.TrackIdsByTitle["Song One"];
        var trackTwo = seeded.TrackIdsByTitle["Song Two"];
        var trackThree = seeded.TrackIdsByTitle["Song Three"];

        var now = DateTimeOffset.UtcNow;
        await _repository.AddPlayHistoryAsync(new LibraryRepository.PlayHistoryWriteInput(
            PlexUserId: plexUserId,
            LibraryId: seeded.LibraryId,
            TrackId: trackOne,
            PlexTrackKey: "key-1",
            PlexRatingKey: "rating-1",
            PlayedAtUtc: now.AddMinutes(-4),
            DurationMs: 180000,
            MetadataJson: "{}"));
        await _repository.AddPlayHistoryAsync(new LibraryRepository.PlayHistoryWriteInput(
            PlexUserId: plexUserId,
            LibraryId: seeded.LibraryId,
            TrackId: trackOne,
            PlexTrackKey: "key-1",
            PlexRatingKey: "rating-1",
            PlayedAtUtc: now.AddMinutes(-3),
            DurationMs: 180000,
            MetadataJson: "{}"));
        await _repository.AddPlayHistoryAsync(new LibraryRepository.PlayHistoryWriteInput(
            PlexUserId: plexUserId,
            LibraryId: seeded.LibraryId,
            TrackId: trackTwo,
            PlexTrackKey: "key-2",
            PlexRatingKey: "rating-2",
            PlayedAtUtc: now.AddMinutes(-2),
            DurationMs: 190000,
            MetadataJson: "{}"));

        Assert.Equal(
            now.AddMinutes(-2),
            await _repository.GetLatestPlayHistoryUtcAsync(plexUserId, "plex"));

        var topTrackIds = await _repository.GetTopTrackIdsAsync(plexUserId, seeded.LibraryId, 3);
        Assert.Equal(trackOne, topTrackIds[0]);

        var mostPlayed = await _repository.GetMostPlayedTrackIdsAsync(plexUserId, seeded.LibraryId, 3);
        Assert.Equal(trackOne, mostPlayed[0]);

        var unplayed = await _repository.GetUnplayedTrackIdsAsync(plexUserId, seeded.LibraryId, 10);
        Assert.Contains(trackThree, unplayed);

        var rediscover = await _repository.GetRediscoverTrackIdsAsync(plexUserId, seeded.LibraryId, 10);
        Assert.Contains(trackThree, rediscover);
    }

    [Fact]
    public async Task SourceResolution_And_ExistenceChecks_WorkAcrossLibraryAndFolder()
    {
        var seeded = await SeedLibraryAsync(
            ("Song One", "dz-song-1", "sp-song-1", "am-song-1"),
            ("Song Two", "dz-song-2", "sp-song-2", "am-song-2"));

        var scopeTrackIds = await _repository.GetTrackIdsForLibraryScopeAsync(seeded.LibraryId, seeded.Folder.Id);
        Assert.Equal(2, scopeTrackIds.Count);

        var trackIdsBySpotify = await _repository.GetTrackIdsBySourceIdsAsync(
            "spotify",
            SpotifyTrackSourceIds);
        Assert.Equal(2, trackIdsBySpotify.Count);
        Assert.Equal(seeded.TrackIdsByTitle["Song One"], trackIdsBySpotify["sp-song-1"]);

        var trackIdsByMixedCaseSpotify = await _repository.GetTrackIdsBySourceIdsAsync(
            "SPOTIFY",
            UppercaseSpotifyTrackSourceIds);
        Assert.Single(trackIdsByMixedCaseSpotify);
        Assert.Equal(seeded.TrackIdsByTitle["Song One"], trackIdsByMixedCaseSpotify["SP-SONG-1"]);

        var albumFromTrackSource = await _repository.GetLocalAlbumIdByTrackSourceIdAsync("spotify", "sp-song-1");
        Assert.Equal(seeded.AlbumId, albumFromTrackSource);

        var albumFromAlbumSource = await _repository.GetLocalAlbumIdByAlbumSourceIdAsync("spotify", "sp-album-1");
        Assert.Equal(seeded.AlbumId, albumFromAlbumSource);

        var albumFromMetadata = await _repository.GetLocalAlbumIdByTrackMetadataAsync("Artist One", "Song One", 180000);
        Assert.Equal(seeded.AlbumId, albumFromMetadata);

        var existsTrackSource = await _repository.ExistsTrackSourceAsync("spotify", "sp-song-1");
        Assert.True(existsTrackSource);
        Assert.True(await _repository.ExistsTrackSourceAsync("spotify", "sp-song-1", "stereo"));
        Assert.False(await _repository.ExistsTrackSourceAsync("spotify", "sp-song-1", "atmos"));

        Assert.True(await _repository.ExistsTrackSourceInFolderAsync("spotify", "sp-song-1", seeded.Folder.Id));
        Assert.False(await _repository.ExistsTrackSourceInFolderAsync("spotify", "sp-song-1", seeded.Folder.Id + 999));

        Assert.True(await _repository.ExistsArtistSourceAsync("spotify", "sp-artist-1"));
        Assert.True(await _repository.ExistsAlbumSourceAsync("spotify", "sp-album-1"));

        Assert.True(await _repository.ExistsTrackByAlbumSourceAsync(
            "spotify",
            "sp-album-1",
            "Song One",
            "sp-artist-1"));
        Assert.True(await _repository.ExistsTrackByAlbumSourceInFolderAsync(
            "spotify",
            "sp-album-1",
            "Song Two",
            "sp-artist-1",
            seeded.Folder.Id));
        Assert.False(await _repository.ExistsTrackByAlbumSourceInFolderAsync(
            "spotify",
            "sp-album-1",
            "Song Two",
            "sp-artist-1",
            seeded.Folder.Id,
            "atmos"));

        var existenceResults = await _repository.ExistsInLibraryAsync(
            new[]
            {
                new LibraryRepository.LibraryExistenceInput("ISRC00000001", null, null, null),
                new LibraryRepository.LibraryExistenceInput(null, "Song One", "Artist One", 180000),
                new LibraryRepository.LibraryExistenceInput(null, "Missing Song", "Missing Artist", null)
            });
        Assert.Equal(3, existenceResults.Count);
        Assert.True(existenceResults[0]);
        Assert.True(existenceResults[1]);
        Assert.False(existenceResults[2]);

        var sourceIdentity = await _repository.ResolveLocalTrackIdentityAsync(
            new LibraryRepository.LibraryExistenceInput(
                null,
                "Wrong display title",
                "Wrong display artist",
                null,
                "spotify",
                "sp-song-1"));
        Assert.Equal("source_id", sourceIdentity.MatchType);
        Assert.Equal(seeded.TrackIdsByTitle["Song One"], sourceIdentity.LocalTrackId);

        var metadataIdentity = await _repository.ResolveLocalTrackIdentityAsync(
            new LibraryRepository.LibraryExistenceInput(
                null,
                "Song One",
                "Artist One",
                187000,
                AlbumTitle: "Album One"));
        Assert.Equal("metadata_exact", metadataIdentity.MatchType);
        Assert.Equal(seeded.TrackIdsByTitle["Song One"], metadataIdentity.LocalTrackId);

        var batchInputs = new[]
        {
            new LibraryRepository.LibraryExistenceInput(
                null,
                "Wrong display title",
                "Wrong display artist",
                null,
                "spotify",
                "sp-song-1"),
            new LibraryRepository.LibraryExistenceInput(
                null,
                "Song One",
                "Artist One",
                187000,
                AlbumTitle: "Album One"),
            new LibraryRepository.LibraryExistenceInput(
                null,
                "Missing Song",
                "Missing Artist",
                null)
        };
        var singleIdentities = new List<LibraryRepository.LocalTrackIdentityResult>(batchInputs.Length);
        foreach (var input in batchInputs)
        {
            singleIdentities.Add(await _repository.ResolveLocalTrackIdentityAsync(input));
        }

        var batchIdentities = await _repository.ResolveLocalTrackIdentitiesAsync(batchInputs);
        Assert.Equal(singleIdentities.Count, batchIdentities.Count);
        for (var index = 0; index < singleIdentities.Count; index++)
        {
            Assert.Equal(singleIdentities[index].LocalTrackId, batchIdentities[index].LocalTrackId);
            Assert.Equal(singleIdentities[index].MatchType, batchIdentities[index].MatchType);
            Assert.Equal(singleIdentities[index].CandidateTrackIds, batchIdentities[index].CandidateTrackIds);
        }
    }

    [Fact]
    public async Task LocalScanFileStates_RoundTrip_And_UnchangedIngestPreservesAudioTimestamp()
    {
        var root = Path.Join(_tempRoot, "music-library");
        var albumDir = Path.Join(root, "Artist One", "Album One");
        Directory.CreateDirectory(albumDir);
        var filePath = Path.Join(albumDir, "01 - Cached Song.flac");
        await File.WriteAllTextAsync(filePath, "audio placeholder");

        var folder = await _repository.AddFolderAsync(
            new LibraryRepository.FolderUpsertInput(
                RootPath: root,
                DisplayName: "Music Library",
                Enabled: true,
                LibraryName: "Music",
                DesiredQuality: "flac",
                ConvertEnabled: false,
                ConvertFormat: null,
                ConvertBitrate: null,
                AutoTagProfileId: "test-profile"));

        var artists = new List<LocalArtistScanDto> { new("Artist One", null) };
        var albums = new List<LocalAlbumScanDto>
        {
            new("Artist One", "Album One", null, new[] { folder.DisplayName })
        };
        var track = CreateTrackScan(
            title: "Cached Song",
            filePath: filePath,
            deezerTrackId: "dz-cached",
            spotifyTrackId: "sp-cached",
            appleTrackId: "ap-cached");

        await _repository.IngestLocalScanAsync(
            await _repository.GetFoldersAsync(),
            artists,
            albums,
            new[] { track },
            pruneMissingArtists: false);

        var states = await _repository.GetLocalScanFileStatesAsync(folder.Id);
        var state = Assert.Single(states.Values);
        Assert.Equal(new FileInfo(filePath).Length, state.Size);
        Assert.Equal("Cached Song", state.Scan.Title);
        Assert.Equal("sp-cached", state.Scan.SpotifyTrackId);
        Assert.Contains("Soundtrack", state.Scan.TagGenres);

        var updatedAt = await ReadAudioFileUpdatedAtAsync(filePath);
        await Task.Delay(1100);

        await _repository.IngestLocalScanAsync(
            await _repository.GetFoldersAsync(),
            artists,
            albums,
            new[] { track with { IsUnchanged = true } },
            pruneMissingArtists: false);

        Assert.Equal(updatedAt, await ReadAudioFileUpdatedAtAsync(filePath));
    }

    [Fact]
    public async Task ExistsInLibrary_ScopedToLibrary_DoesNotLeakAcrossOtherLibraries()
    {
        var primary = await SeedLibraryAsync(
            ("Primary Song", "sp-song-primary", "sp-song-primary", "ap-song-primary"));

        var otherFolder = await _repository.AddFolderAsync(
            new LibraryRepository.FolderUpsertInput(
                RootPath: "/music/library-b",
                DisplayName: "Library B",
                Enabled: true,
                LibraryName: "Secondary Music",
                DesiredQuality: "flac",
                ConvertEnabled: false,
                ConvertFormat: null,
                ConvertBitrate: null,
                AutoTagProfileId: "test-profile"));

        var allFolders = await _repository.GetFoldersAsync();
        var artists = new List<LocalArtistScanDto>
        {
            new("Other Artist", "/covers/other-artist.jpg")
        };
        var albums = new List<LocalAlbumScanDto>
        {
            new(
                ArtistName: "Other Artist",
                Title: "Other Album",
                PreferredCoverPath: "/covers/other-album.jpg",
                LocalFolders: new[] { otherFolder.DisplayName },
                HasAnimatedArtwork: false)
        };
        var tracks = new[]
        {
            CreateTrackScan(
                title: "Other Song",
                filePath: "/music/library-b/Other Artist/Other Album/01 - Other Song.flac",
                deezerTrackId: "sp-other-song",
                spotifyTrackId: "sp-other-song",
                appleTrackId: "ap-other-song")
        };

        await _repository.IngestLocalScanAsync(
            allFolders,
            artists,
            albums,
            tracks,
            pruneMissingArtists: true);

        var existenceResults = await _repository.ExistsInLibraryAsync(
            primary.LibraryId,
            null,
            new[]
            {
                new LibraryRepository.LibraryExistenceInput(null, "Other Song", "Other Artist", 180000)
            });

        Assert.Single(existenceResults);
        Assert.False(existenceResults[0]);
    }

    [Fact]
    public async Task ArtistsPaging_ReturnsStableSlices_And_TotalCount()
    {
        var folder = await _repository.AddFolderAsync(
            new LibraryRepository.FolderUpsertInput(
                RootPath: "/music/paging-library",
                DisplayName: "Paging Library",
                Enabled: true,
                LibraryName: "Music",
                DesiredQuality: "flac",
                ConvertEnabled: false,
                ConvertFormat: null,
                ConvertBitrate: null,
                AutoTagProfileId: "test-profile"));

        var allFolders = await _repository.GetFoldersAsync();
        var artists = Enumerable.Range(1, 25)
            .Select(index => new LocalArtistScanDto($"Artist {index:00}", $"/covers/artist-{index:00}.jpg"))
            .ToList();
        var albums = Enumerable.Range(1, 25)
            .Select(index => new LocalAlbumScanDto(
                ArtistName: $"Artist {index:00}",
                Title: $"Album {index:00}",
                PreferredCoverPath: $"/covers/album-{index:00}.jpg",
                LocalFolders: new[] { folder.DisplayName },
                HasAnimatedArtwork: false))
            .ToList();

        var tracks = Enumerable.Range(1, 25)
            .Select(index =>
            {
                var artistName = $"Artist {index:00}";
                var albumName = $"Album {index:00}";
                var title = $"Song {index:00}";
                return CreateTrackScan(
                    title: title,
                    filePath: $"/music/paging-library/{artistName}/{albumName}/01 - {title}.flac",
                    deezerTrackId: $"dz-paging-{index:00}",
                    spotifyTrackId: $"sp-paging-{index:00}",
                    appleTrackId: $"am-paging-{index:00}") with
                {
                    ArtistName = artistName,
                    AlbumTitle = albumName,
                    TagArtist = artistName,
                    TagAlbumArtist = artistName,
                    TagAlbum = albumName,
                    TrackNo = 1,
                    TagTrackNo = 1,
                    SourceId = $"sp-paging-{index:00}"
                };
            })
            .ToList();

        await _repository.IngestLocalScanAsync(
            allFolders,
            artists,
            albums,
            tracks,
            pruneMissingArtists: true);

        var page1 = await _repository.GetArtistsPageAsync("local", folder.Id, page: 1, pageSize: 10);
        Assert.Equal(25, page1.TotalCount);
        Assert.Equal(10, page1.Items.Count);
        Assert.Equal(1, page1.Page);
        Assert.Equal(10, page1.PageSize);
        Assert.Equal("Artist 01", page1.Items[0].Name);
        Assert.Equal("Artist 10", page1.Items[^1].Name);

        var page2 = await _repository.GetArtistsPageAsync("local", folder.Id, page: 2, pageSize: 10);
        Assert.Equal(25, page2.TotalCount);
        Assert.Equal(10, page2.Items.Count);
        Assert.Equal("Artist 11", page2.Items[0].Name);
        Assert.Equal("Artist 20", page2.Items[^1].Name);

        var page3 = await _repository.GetArtistsPageAsync("local", folder.Id, page: 3, pageSize: 10);
        Assert.Equal(25, page3.TotalCount);
        Assert.Equal(5, page3.Items.Count);
        Assert.Equal("Artist 21", page3.Items[0].Name);
        Assert.Equal("Artist 25", page3.Items[^1].Name);

        var clamped = await _repository.GetArtistsPageAsync("local", folder.Id, page: 0, pageSize: 5000);
        Assert.Equal(1, clamped.Page);
        Assert.Equal(1000, clamped.PageSize);
        Assert.Equal(25, clamped.Items.Count);

        var searched = await _repository.GetArtistsPageAsync("local", folder.Id, page: 1, pageSize: 20, search: "Artist 2", sort: "name-asc");
        Assert.Equal(6, searched.TotalCount);
        Assert.Equal("Artist 20", searched.Items[0].Name);
        Assert.Equal("Artist 25", searched.Items[^1].Name);

        var descending = await _repository.GetArtistsPageAsync("local", folder.Id, page: 1, pageSize: 5, search: null, sort: "name-desc");
        Assert.Equal(25, descending.TotalCount);
        Assert.Equal("Artist 25", descending.Items[0].Name);
        Assert.Equal("Artist 21", descending.Items[^1].Name);

        var allArtists = await _repository.GetArtistsAsync("local", folder.Id);
        Assert.Equal(25, allArtists.Count);
        Assert.Equal("Artist 01", allArtists[0].Name);
        Assert.Equal("Artist 25", allArtists[^1].Name);
    }

    [Fact]
    public async Task ShazamCache_And_PlexMetadata_RoundTrip_Works()
    {
        var seeded = await SeedLibraryAsync(
            ("Song One", "dz-song-1", "sp-song-1", "am-song-1"));
        var trackId = seeded.TrackIdsByTitle["Song One"];

        var initialCache = await _repository.GetShazamTrackCacheByTrackIdForLibraryAsync(seeded.LibraryId);
        Assert.True(initialCache.TryGetValue(trackId, out var initialEntry));
        Assert.Equal("pending", initialEntry!.Status);

        var staleBefore = DateTimeOffset.UtcNow;
        var initialRefreshCandidates = await _repository.GetTrackIdsNeedingShazamRefreshAsync(
            seeded.LibraryId,
            staleBefore,
            seeded.Folder.Id,
            limit: 10);
        Assert.Contains(trackId, initialRefreshCandidates);

        var scannedAt = DateTimeOffset.UtcNow;
        await _repository.UpsertTrackShazamCacheAsync(
            new LibraryRepository.TrackShazamCacheUpsertInput(
                TrackId: trackId,
                Status: "matched",
                ShazamTrackId: "shz-1",
                Title: "Song One",
                Artist: "Artist One",
                Isrc: "ISRC00000001",
                RelatedTracks:
                [
                    CreateRecommendationTrack("rel-1", "Related Song")
                ],
                ScannedAtUtc: scannedAt,
                Error: null));

        var updatedCache = await _repository.GetShazamTrackCacheByTrackIdForLibraryAsync(seeded.LibraryId);
        Assert.True(updatedCache.TryGetValue(trackId, out var updatedEntry));
        Assert.Equal("matched", updatedEntry!.Status);
        Assert.Equal("shz-1", updatedEntry.ShazamTrackId);
        Assert.Single(updatedEntry.RelatedTracks);

        var refreshedCandidates = await _repository.GetTrackIdsNeedingShazamRefreshAsync(
            seeded.LibraryId,
            scannedAt.AddMinutes(-1),
            seeded.Folder.Id,
            limit: 10);
        Assert.DoesNotContain(trackId, refreshedCandidates);

        await _repository.UpsertPlexTrackMetadataAsync(
            new PlexTrackMetadataDto(
                TrackId: trackId,
                PlexRatingKey: "rk-1",
                UserRating: 9,
                Genres: PlexMetadataGenres,
                Moods: PlexMetadataMoods,
                UpdatedAtUtc: DateTimeOffset.UtcNow));

        var metadata = await _repository.GetPlexTrackMetadataAsync(new[] { trackId });
        var metadataEntry = Assert.Single(metadata);
        Assert.Equal("rk-1", metadataEntry.PlexRatingKey);
        Assert.Single(metadataEntry.Genres);
        Assert.Single(metadataEntry.Moods);

        var plexUserId = await _repository.EnsurePlexUserAsync(
            username: "plex-user",
            plexUserId: "plex-uid-1",
            serverUrl: "http://plex.local:32400",
            machineId: "machine-1");
        await _repository.AddPlayHistoryAsync(
            new LibraryRepository.PlayHistoryWriteInput(
                PlexUserId: plexUserId,
                LibraryId: seeded.LibraryId,
                TrackId: trackId,
                PlexTrackKey: "track-key-1",
                PlexRatingKey: "rk-1",
                PlayedAtUtc: DateTimeOffset.UtcNow,
                DurationMs: 180000,
                MetadataJson: "{}"));

        var ratingKeys = await _repository.GetPlexRatingKeysAsync(new[] { trackId });
        Assert.Contains("rk-1", ratingKeys);

        var ratingKeysByTrack = await _repository.GetPlexRatingKeysByTrackIdsAsync(new[] { trackId });
        Assert.Equal("rk-1", ratingKeysByTrack[trackId]);

        var trackIdsByRatingKey = await _repository.GetTrackIdsByPlexRatingKeysAsync(PlexRatingKeys);
        Assert.Equal(trackId, trackIdsByRatingKey["rk-1"]);
    }

    [Fact]
    public async Task RecommendationRejection_RoundTrip_Works()
    {
        var seeded = await SeedLibraryAsync(
            ("Song One", "dz-song-1", "sp-song-1", "am-song-1"));
        var stationId = $"library:{seeded.LibraryId}:folder:{seeded.Folder.Id}";

        await _repository.AddRecommendationRejectionAsync(
            new RecommendationRejectionUpsertInput(
                seeded.LibraryId,
                null,
                stationId,
                " 12345 ",
                "ISRC00000001",
                "Recommended Song",
                "Recommended Artist"));

        var rejectedIds = await _repository.GetRecommendationRejectedTrackIdsAsync(
            seeded.LibraryId,
            seeded.Folder.Id,
            stationId);

        Assert.Contains("12345", rejectedIds);
    }

    [Fact]
    public async Task GetTracksForAnalysis_CanRetryCompletedStandardAnalysis_WhenRequested()
    {
        var seeded = await SeedLibraryAsync(
            ("Song One", "dz-song-1", "sp-song-1", "am-song-1"));
        var trackId = seeded.TrackIdsByTitle["Song One"];
        var analyzedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);

        await _repository.UpsertTrackAnalysisAsync(CreateAnalysisResult(
            trackId,
            seeded.LibraryId,
            analyzedAtUtc,
            ["happy"],
            analysisMode: "standard"));

        var defaultCandidates = await _repository.GetTracksForAnalysisAsync(10);
        Assert.DoesNotContain(defaultCandidates, item => item.TrackId == trackId);

        var retryCandidates = await _repository.GetTracksForAnalysisAsync(
            10,
            includeCompletedStandard: true);
        Assert.Contains(retryCandidates, item => item.TrackId == trackId);

        var throttledRetryCandidates = await _repository.GetTracksForAnalysisAsync(
            10,
            includeCompletedStandard: true,
            completedStandardRetryBeforeUtc: DateTimeOffset.UtcNow.AddMinutes(-30));
        Assert.DoesNotContain(throttledRetryCandidates, item => item.TrackId == trackId);

        await _repository.UpsertTrackAnalysisAsync(CreateAnalysisResult(
            trackId,
            seeded.LibraryId,
            analyzedAtUtc.AddMinutes(1),
            ["happy"],
            analysisMode: "enhanced"));

        var enhancedCandidates = await _repository.GetTracksForAnalysisAsync(
            10,
            includeCompletedStandard: true);
        Assert.DoesNotContain(enhancedCandidates, item => item.TrackId == trackId);
    }

    [Fact]
    public async Task TrackAnalysis_And_MixCache_Workflow_RoundTrip_Works()
    {
        var seeded = await SeedLibraryAsync(
            ("Song One", "dz-song-1", "sp-song-1", "am-song-1"),
            ("Song Two", "dz-song-2", "sp-song-2", "am-song-2"));
        var trackOne = seeded.TrackIdsByTitle["Song One"];
        var trackTwo = seeded.TrackIdsByTitle["Song Two"];

        var tracksForAnalysis = await _repository.GetTracksForAnalysisAsync(10);
        Assert.True(tracksForAnalysis.Count >= 2);
        Assert.Contains(tracksForAnalysis, item => item.TrackId == trackOne);

        var explicitTrack = await _repository.GetTrackForAnalysisAsync(trackOne);
        Assert.NotNull(explicitTrack);

        await _repository.MarkTrackAnalysisProcessingAsync(trackOne, seeded.LibraryId);
        await _repository.ResetProcessingTrackAnalysisAsync();
        var resetAnalysis = await _repository.GetTrackAnalysisAsync(trackOne);
        Assert.NotNull(resetAnalysis);
        Assert.Equal("pending", resetAnalysis!.Status);

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-2);
        await _repository.UpsertTrackAnalysisAsync(CreateAnalysisResult(trackOne, seeded.LibraryId, baseTime, ["happy"]));
        await _repository.UpsertTrackAnalysisAsync(CreateAnalysisResult(trackTwo, seeded.LibraryId, baseTime.AddMinutes(1), ["epic"]));

        var analysisOne = await _repository.GetTrackAnalysisAsync(trackOne);
        Assert.NotNull(analysisOne);
        Assert.Equal("complete", analysisOne!.Status);
        Assert.Equal("C#m", analysisOne.Key);

        var latest = await _repository.GetLatestTrackAnalysisAsync();
        Assert.NotNull(latest);
        Assert.Equal(trackTwo, latest!.Track.TrackId);

        var analysisByTrack = await _repository.GetTrackAnalysisByTrackIdsAsync(new[] { trackOne, trackTwo });
        Assert.Equal(2, analysisByTrack.Count);
        Assert.Equal(0.4, analysisByTrack[trackOne].Approachability);
        Assert.Equal(0.6, analysisByTrack[trackOne].Engagement);
        Assert.Equal(0.65, analysisByTrack[trackOne].ValenceMl);
        Assert.Equal(0.55, analysisByTrack[trackOne].ArousalMl);

        var candidates = await _repository.GetTrackAnalysisCandidatesAsync(seeded.LibraryId, trackOne, limit: 10);
        var candidate = Assert.Single(candidates, item => item.TrackId == trackTwo);
        Assert.Equal(0.2, candidate.VoiceInstrumental);
        Assert.Equal(0.8, candidate.TonalAtonal);
        Assert.Equal(0.5, candidate.DynamicComplexity);
        Assert.Equal(-9.0, candidate.LoudnessMl);

        var moodMatches = await _repository.GetTrackIdsByMoodTagsAsync(seeded.LibraryId, HappyMoodTags, 10);
        Assert.Contains(trackOne, moodMatches);

        var status = await _repository.GetAnalysisStatusAsync();
        Assert.True(status.AnalyzedTracks >= 2);

        var plexUserId = await _repository.EnsurePlexUserAsync(
            username: "plex-user",
            plexUserId: "plex-uid-1",
            serverUrl: "http://plex.local:32400",
            machineId: "machine-1");

        var mixCacheId = await _repository.UpsertMixCacheAsync(
            new LibraryRepository.MixCacheUpsertInput(
                MixId: "mix-happy",
                PlexUserId: plexUserId,
                LibraryId: seeded.LibraryId,
                Name: "Happy Mix",
                Description: "Auto generated",
                CoverUrls: MixCoverUrls,
                TrackCount: 2,
                GeneratedAtUtc: DateTimeOffset.UtcNow,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(2)));

        await _repository.ReplaceMixItemsAsync(mixCacheId, new[] { trackTwo, trackOne });

        var mixIdLookup = await _repository.GetMixCacheIdAsync("mix-happy", plexUserId, seeded.LibraryId);
        Assert.Equal(mixCacheId, mixIdLookup);

        var mixSummary = await _repository.GetMixCacheAsync("mix-happy", plexUserId, seeded.LibraryId);
        Assert.NotNull(mixSummary);
        Assert.Equal(2, mixSummary!.TrackCount);
        Assert.Single(mixSummary.CoverUrls);

        var mixTracks = await _repository.GetMixTracksAsync(mixCacheId);
        Assert.Equal(2, mixTracks.Count);
        Assert.Equal(trackTwo, mixTracks[0].TrackId);

        var coverPaths = await _repository.GetCoverPathsAsync(new[] { trackOne, trackTwo }, limit: 1);
        Assert.Single(coverPaths);
    }

    [Fact]
    public async Task ArtistArtworkProvenance_RoundTripsProviderUrlPathAndDimensions()
    {
        var seeded = await SeedLibraryAsync(("Provenance Song", "dz-prov", "sp-prov", "ap-prov"));
        var localPath = Path.Join(_tempRoot, "artist.jpg");

        await _repository.UpsertArtistArtworkCacheAsync(
            new ArtistArtworkCacheUpsertInput(
                seeded.ArtistId,
                "avatar",
                "spotify:artist-1",
                "spotify",
                "https://i.scdn.co/image/square",
                localPath,
                "hash",
                640,
                640,
                "not_scanned",
                null,
                false,
                false));

        var result = await _repository.GetArtistArtworkProvenanceAsync(
            seeded.ArtistId,
            "avatar",
            localPath);

        Assert.NotNull(result);
        Assert.Equal("spotify", result!.Source);
        Assert.Equal("https://i.scdn.co/image/square", result.OriginalUrl);
        Assert.Equal(localPath, result.LocalPath);
        Assert.Equal(640, result.Width);
        Assert.Equal(640, result.Height);
    }

    private async Task<SeededLibrary> SeedLibraryAsync(params (string Title, string DeezerTrackId, string SpotifyTrackId, string AppleTrackId)[] tracks)
    {
        Assert.NotEmpty(tracks);

        var libraryRoot = Path.Join(_tempRoot, "music", "library-a");
        var folder = await _repository.AddFolderAsync(
            new LibraryRepository.FolderUpsertInput(
                RootPath: libraryRoot,
                DisplayName: "Library A",
                Enabled: true,
                LibraryName: "Music",
                DesiredQuality: "flac",
                ConvertEnabled: false,
                ConvertFormat: null,
                ConvertBitrate: null,
                AutoTagProfileId: "test-profile"));

        var allFolders = await _repository.GetFoldersAsync();
        var artists = new List<LocalArtistScanDto>
        {
            new("Artist One", "/covers/artist-one.jpg")
        };
        var albums = new List<LocalAlbumScanDto>
        {
            new(
                ArtistName: "Artist One",
                Title: "Album One",
                PreferredCoverPath: "/covers/album-one.jpg",
                LocalFolders: new[] { folder.DisplayName },
                HasAnimatedArtwork: false)
        };

        var trackDtos = new List<LocalTrackScanDto>();
        var pathByTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tracks.Length; i++)
        {
            var item = tracks[i];
            var filePath = Path.Join(libraryRoot, "Artist One", "Album One", $"{i + 1:00} - {item.Title}.flac");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllTextAsync(filePath, "audio placeholder");
            pathByTitle[item.Title] = filePath;
            trackDtos.Add(CreateTrackScan(
                title: item.Title,
                filePath: filePath,
                deezerTrackId: item.DeezerTrackId,
                spotifyTrackId: item.SpotifyTrackId,
                appleTrackId: item.AppleTrackId));
        }

        await _repository.IngestLocalScanAsync(
            allFolders,
            artists,
            albums,
            trackDtos,
            pruneMissingArtists: true);

        var libraries = await _repository.GetLibrariesAsync();
        var library = Assert.Single(libraries.Where(item => item.Name == "Music"));

        var artist = Assert.Single(await _repository.GetArtistsAsync((string?)null));
        var album = Assert.Single(await _repository.GetArtistAlbumsAsync(artist.Id));

        var trackIdsByTitle = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in tracks)
        {
            var path = pathByTitle[item.Title];
            var trackId = await _repository.GetTrackIdForFilePathAsync(path);
            Assert.NotNull(trackId);
            trackIdsByTitle[item.Title] = trackId!.Value;
        }

        return new SeededLibrary(
            library.Id,
            folder,
            artist.Id,
            album.Id,
            trackIdsByTitle,
            pathByTitle);
    }

    private static LocalTrackScanDto CreateTrackScan(
        string title,
        string filePath,
        string deezerTrackId,
        string spotifyTrackId,
        string appleTrackId)
    {
        return new LocalTrackScanDto(
            ArtistName: "Artist One",
            AlbumTitle: "Album One",
            Title: title,
            FilePath: filePath,
            TagTitle: title,
            TagArtist: "Artist One",
            TagAlbum: "Album One",
            TagAlbumArtist: "Artist One",
            TagVersion: null,
            TagLabel: "Label One",
            TagCatalogNumber: "CAT-001",
            TagBpm: 120,
            TagKey: "C#m",
            TagTrackTotal: 1,
            TagDurationMs: 180000,
            TagYear: 2025,
            TagTrackNo: 1,
            TagDisc: 1,
            TagGenre: "Soundtrack",
            TagIsrc: "ISRC00000001",
            TagReleaseDate: "2025-01-01",
            TagPublishDate: "2025-01-01",
            TagUrl: null,
            TagReleaseId: null,
            TagTrackId: null,
            TagMetaTaggedDate: null,
            LyricsUnsynced: null,
            LyricsSynced: null,
            TagGenres: SoundtrackGenres,
            TagStyles: Array.Empty<string>(),
            TagMoods: Array.Empty<string>(),
            TagRemixers: Array.Empty<string>(),
            TagOtherTags: Array.Empty<LocalTrackOtherTag>(),
            TrackNo: 1,
            Disc: 1,
            DurationMs: 180000,
            LyricsStatus: null,
            LyricsType: null,
            Codec: "flac",
            BitrateKbps: 1000,
            SampleRateHz: 48000,
            BitsPerSample: 24,
            Channels: 2,
            QualityRank: 4,
            AudioVariant: "stereo",
            DeezerTrackId: deezerTrackId,
            Isrc: "ISRC00000001",
            DeezerAlbumId: "dz-album-1",
            DeezerArtistId: "dz-artist-1",
            SpotifyTrackId: spotifyTrackId,
            SpotifyAlbumId: "sp-album-1",
            SpotifyArtistId: "sp-artist-1",
            AppleTrackId: appleTrackId,
            AppleAlbumId: "am-album-1",
            AppleArtistId: "am-artist-1",
            Source: "spotify",
            SourceId: spotifyTrackId);
    }

    private async Task<string> ReadAudioFileUpdatedAtAsync(string filePath)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        await using var command = new SqliteCommand(
            "SELECT updated_at FROM audio_file WHERE path = @path LIMIT 1;",
            connection);
        command.Parameters.AddWithValue("path", filePath);
        var result = await command.ExecuteScalarAsync();
        return Assert.IsType<string>(result);
    }

    private static RecommendationTrackDto CreateRecommendationTrack(string id, string title)
    {
        return new RecommendationTrackDto(
            Id: id,
            Title: title,
            Duration: 180,
            Isrc: "ISRC00000001",
            TrackPosition: 1,
            Artist: new RecommendationArtistDto("artist-1", "Artist One"),
            Album: new RecommendationAlbumDto("album-1", "Album One", "https://example.com/cover.jpg"));
    }

    private static TrackAnalysisResultDto CreateAnalysisResult(
        long trackId,
        long libraryId,
        DateTimeOffset analyzedAtUtc,
        IReadOnlyList<string> moodTags,
        string analysisMode = "signal")
    {
        return new TrackAnalysisResultDto(
            TrackId: trackId,
            LibraryId: libraryId,
            Status: "complete",
            Energy: 0.80,
            Rms: 0.25,
            ZeroCrossing: 0.10,
            SpectralCentroid: 1500.0,
            Bpm: 120.0,
            AnalyzedAtUtc: analyzedAtUtc,
            Error: null,
            AnalysisMode: analysisMode,
            AnalysisVersion: "v1",
            MoodTags: moodTags,
            MoodHappy: 0.8,
            MoodSad: 0.1,
            MoodRelaxed: 0.3,
            MoodAggressive: 0.2,
            MoodParty: 0.6,
            MoodAcoustic: 0.1,
            MoodElectronic: 0.5,
            Valence: 0.7,
            Arousal: 0.6,
            BeatsCount: 350,
            Key: "C#m",
            KeyScale: "minor",
            KeyStrength: 0.9,
            Loudness: -8.0,
            DynamicRange: 9.0,
            Danceability: 0.75,
            Instrumentalness: 0.2,
            Acousticness: 0.1,
            Speechiness: 0.05,
            DanceabilityMl: 0.7,
            EssentiaGenres: EssentiaGenreTags,
            LastfmTags: LastfmGenreTags,
            Approachability: 0.4,
            Engagement: 0.6,
            VoiceInstrumental: 0.2,
            TonalAtonal: 0.8,
            ValenceMl: 0.65,
            ArousalMl: 0.55,
            DynamicComplexity: 0.5,
            LoudnessMl: -9.0);
    }
}
