using System;
using System.IO;
using System.Collections.Generic;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlaylistSyncTargetAggregationTests
{
    [Fact]
    public void CombinePlaylistSyncTargetResults_ReturnsFailureUntilEveryTargetSucceeds()
    {
        var result = PlaylistSyncService.CombinePlaylistSyncTargetResults(new List<(string Service, PlaylistSyncResult Result)>
        {
            ("plex", new PlaylistSyncResult(true, "Playlist synced to Plex.", "plex-1", SyncedTracks: 2, SourceTracks: 3, LocalMatches: 2, TargetMatches: 2)),
            ("jellyfin", PlaylistSyncResult.Failed("Jellyfin sync failed: connection refused")),
            ("navidrome", new PlaylistSyncResult(true, "Playlist synced to Navidrome.", "navidrome-1", SyncedTracks: 2, SourceTracks: 3, LocalMatches: 2, TargetMatches: 2))
        });

        Assert.False(result.Success);
        Assert.Equal(4, result.SyncedTracks);
        Assert.Equal(4, result.TargetMatches);
        Assert.Contains("Plex: Playlist synced to Plex.", result.Message);
        Assert.Contains("Jellyfin: Jellyfin sync failed: connection refused", result.Message);
        Assert.Contains("Navidrome: Playlist synced to Navidrome.", result.Message);
    }

    [Fact]
    public void CombinePlaylistSyncTargetResults_ReturnsFailureWhenEveryTargetFails()
    {
        var result = PlaylistSyncService.CombinePlaylistSyncTargetResults(new List<(string Service, PlaylistSyncResult Result)>
        {
            ("plex", PlaylistSyncResult.Failed("Plex is not configured.")),
            ("jellyfin", PlaylistSyncResult.Failed("Jellyfin sync failed: connection refused"))
        });

        Assert.False(result.Success);
        Assert.Contains("Plex: Plex is not configured.", result.Message);
        Assert.Contains("Jellyfin: Jellyfin sync failed: connection refused", result.Message);
    }

    [Fact]
    public void JellyfinMirrorSync_DiffsInsteadOfClearingThePlaylist()
    {
        var source = ReadPlaylistSyncSource();
        var start = source.IndexOf("> ReplaceJellyfinPlaylistItemsAsync(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = source[start..(start + 2600)];

        Assert.DoesNotContain("Failed to clear existing Jellyfin playlist items.", source, StringComparison.Ordinal);
        Assert.Contains("staleEntryIds", body, StringComparison.Ordinal);
        Assert.Contains("if (staleEntryIds.Count == 0 && pending.Count == 0)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void NavidromeMirrorSync_UsesIncrementalUpdateInsteadOfCreatePlaylist()
    {
        var source = ReadNavidromeSource();
        var start = source.IndexOf("> CreateOrUpdatePlaylistAsync(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = source[start..(start + 4200)];

        Assert.Contains("songIndexToRemove", source, StringComparison.Ordinal);
        Assert.Contains("if (removalIndexes.Count == 0 && targetIds.Count == 0)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("appendMissingOnly ? \"updatePlaylist\" : \"createPlaylist\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockedJobsDoNotChangeConfiguredTargetDenominator()
    {
        var schema = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Services", "Library", "LibraryDbService.cs"));
        var viewStart = schema.IndexOf("CREATE VIEW playlist_watch_configured_sync_targets AS", StringComparison.Ordinal);
        Assert.True(viewStart > 0);
        var viewEnd = schema.IndexOf("CREATE VIEW playlist_watch_track_sync_progress AS", viewStart, StringComparison.Ordinal);
        Assert.True(viewEnd > viewStart);
        var view = schema[viewStart..viewEnd];

        Assert.Contains("playlist_watch_preferences preference", view, StringComparison.Ordinal);
        Assert.Contains("json_each", view, StringComparison.Ordinal);
        Assert.DoesNotContain("watchlist_sync_job", view, StringComparison.Ordinal);
        Assert.DoesNotContain("blocked", view, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SyncViewsAreRebuiltAfterTableMigrations()
    {
        var schema = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Services", "Library", "LibraryDbService.cs"));
        var drop = schema.IndexOf("DROP VIEW IF EXISTS playlist_watch_configured_sync_targets;\", cancellationToken);", StringComparison.Ordinal);
        var legacyMigration = schema.IndexOf("await MigrateLegacyPlaylistWatchTargetMembershipAsync(connection, cancellationToken);", StringComparison.Ordinal);
        var create = schema.IndexOf("await EnsurePlaylistWatchTargetSyncViewsAsync(connection, cancellationToken);", StringComparison.Ordinal);

        Assert.True(drop > 0 && legacyMigration > 0 && create > 0);
        Assert.True(drop < legacyMigration, "Views must be dropped before table rebuilds validate them.");
        Assert.True(create > legacyMigration, "Views must be recreated after migrations complete.");
    }

    [Fact]
    public void PerTargetSyncStateIsSurfacedSeparatelyFromConfiguredTargets()
    {
        var repository = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));
        var controller = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Controllers", "Api", "LibraryPlaylistWatchlistApiController.cs"));

        Assert.Contains("AS synced_target_service", repository, StringComparison.Ordinal);
        Assert.Contains("AS missing_target_service", repository, StringComparison.Ordinal);
        Assert.Contains("SyncedTargetServices = persistedStatus?.SyncedTargetServices", controller, StringComparison.Ordinal);
        Assert.Contains("MissingTargetServices = persistedStatus?.MissingTargetServices", controller, StringComparison.Ordinal);
        Assert.Contains("status?.MissingTargetServices", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtistWatchRespectsAndOpensItsOwnSourceCircuit()
    {
        var coordinator = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Services", "WatchlistRunCoordinator.cs"));
        var start = coordinator.IndexOf("> ProcessArtistWatchItemsAsync(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = coordinator[start..(start + 4000)];

        Assert.Contains("ArtistWatchType", body, StringComparison.Ordinal);
        Assert.Contains("IsCircuitOpen(openArtistCircuit)", body, StringComparison.Ordinal);
        Assert.Contains("artist_watch_systemic_failure", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetCircuitStateIsVisibleInTheRuntimeEndpoint()
    {
        var controller = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Controllers", "Api", "LibraryPlaylistWatchlistApiController.cs"));

        Assert.Contains("targetCircuits", controller, StringComparison.Ordinal);
        Assert.Contains("GetWatchlistTargetCircuitStateAsync(target, cancellationToken)", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void RegionBlockedTracksEnterTheAvailabilityRecheckModel()
    {
        var engine = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));
        var start = engine.IndexOf("bool ShouldMarkWatchTrackUnavailable(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = engine[start..(start + 3200)];

        Assert.Contains("region_blocked", body, StringComparison.Ordinal);
        Assert.Contains("geo_restricted", body, StringComparison.Ordinal);
        Assert.Contains("not available in your country", body, StringComparison.Ordinal);

        var regionCheck = body.IndexOf("not available in your", StringComparison.Ordinal);
        var forbiddenExclusion = body.IndexOf("message.Contains(\"forbidden\"", StringComparison.Ordinal);
        Assert.True(regionCheck > 0 && forbiddenExclusion > 0);
        Assert.True(
            regionCheck < forbiddenExclusion,
            "Region blocks must be classified before the forbidden exclusion, which would otherwise treat them as retryable.");
    }

    [Fact]
    public void StateDriftIsDetectedEveryCycleAndSurfaced()
    {
        var repository = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));
        var coordinator = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Services", "WatchlistRunCoordinator.cs"));
        var controller = File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Controllers", "Api", "LibraryPlaylistWatchlistApiController.cs"));

        Assert.Contains("DetectWatchlistStateDriftAsync", repository, StringComparison.Ordinal);
        Assert.Contains("AS applied_without_membership", repository, StringComparison.Ordinal);
        Assert.Contains("AS membership_without_applied", repository, StringComparison.Ordinal);
        Assert.Contains("AS orphaned_membership", repository, StringComparison.Ordinal);
        Assert.Contains("Watchlist state drift detected", coordinator, StringComparison.Ordinal);
        Assert.Contains("stateDrift = new", controller, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://music.apple.com/us/playlist/hip-hop-r-b-throwback/pl.674abcd261d04582b58d6388394cd047", "us")]
    [InlineData("https://music.apple.com/ke/playlist/x/pl.123", "ke")]
    [InlineData("https://music.apple.com/gb-en/playlist/x/pl.123", "gb-en")]
    [InlineData(null, null)]
    [InlineData("not-a-url", null)]
    public void AppleStorefrontIsDerivedFromTheBrowsedUrl(string? appleUrl, string? expected)
    {
        var actual = InvokeResolveStorefrontFromAppleUrl(appleUrl);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PersistedAppleUrlWinsOverTheConfiguredStorefront()
    {
        var controller = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Controllers", "Api", "AppleTracklistApiController.cs"));

        Assert.Contains("ResolveStorefrontFromAppleUrl(playlist?.SourceUrl)", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitoringAnApplePlaylistAlwaysRecordsItsSourceUrl()
    {
        var controller = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Controllers", "Api", "LibraryPlaylistWatchlistApiController.cs"));

        Assert.Contains("sourceUrl = $\"https://music.apple.com/{sourceStorefront}/playlist/", controller, StringComparison.Ordinal);
    }

    private static string? InvokeResolveStorefrontFromAppleUrl(string? appleUrl)
    {
        var type = typeof(DeezSpoTag.Web.Controllers.Api.AppleTracklistApiController);
        var method = type.GetMethod(
            "ResolveStorefrontFromAppleUrl",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, new object?[] { appleUrl });
    }

    [Fact]
    public void AppleAndDeezerArtistWatchPageThroughTheDiscography()
    {
        var service = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistWatchService.cs"));

        Assert.DoesNotContain("                Math.Clamp(settings.WatchMaxReleasesPerArtist, 1, MaxReleasesPerArtistLimit),\n                0,", service, StringComparison.Ordinal);
        Assert.DoesNotContain("index: 0,", service, StringComparison.Ordinal);
        Assert.Contains("ResolveSourcePagingOffset(artist, appleState?.AppleNextOffset)", service, StringComparison.Ordinal);
        Assert.Contains("ResolveSourcePagingOffset(artist, deezerState?.DeezerNextOffset)", service, StringComparison.Ordinal);
        Assert.Contains("UpsertArtistWatchSourceOffsetAsync", service, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtistPagingOffsetsAreStoredPerSource()
    {
        var repository = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Services", "Library", "LibraryRepository.cs"));
        var schema = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Services", "Library", "LibraryDbService.cs"));

        Assert.Contains("\"apple\" => \"apple_next_offset\"", repository, StringComparison.Ordinal);
        Assert.Contains("\"deezer\" => \"deezer_next_offset\"", repository, StringComparison.Ordinal);
        Assert.Contains("\"apple_next_offset\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"deezer_next_offset\"", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchIntentIdentityCoversEveryPlatformIdOnTheIntent()
    {
        var engine = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));
        var start = engine.IndexOf("ResolveIntentTrackId(DownloadIntent intent)", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = engine[start..(start + 1200)];

        Assert.Contains("intent.SpotifyId", body, StringComparison.Ordinal);
        Assert.Contains("intent.DeezerId", body, StringComparison.Ordinal);
        Assert.Contains("intent.AppleId", body, StringComparison.Ordinal);
        Assert.Contains("intent.QobuzId", body, StringComparison.Ordinal);
        Assert.Contains("intent.TidalId", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ArtistWatchCoversQobuzAlongsideTheOtherPlatforms()
    {
        var service = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistWatchService.cs"));

        Assert.Contains("await CheckSpotifyArtistAsync(", service, StringComparison.Ordinal);
        Assert.Contains("await CheckAppleArtistAsync(", service, StringComparison.Ordinal);
        Assert.Contains("await CheckDeezerArtistAsync(", service, StringComparison.Ordinal);
        Assert.Contains("await CheckQobuzArtistAsync(", service, StringComparison.Ordinal);
        Assert.Contains("await CheckTidalArtistAsync(", service, StringComparison.Ordinal);
        Assert.Contains("api.tidal.com/v1/artists/", service, StringComparison.Ordinal);
        Assert.Contains("BuildTidalAlbumIntents", service, StringComparison.Ordinal);
        Assert.Contains("BuildQobuzAlbumIntents", service, StringComparison.Ordinal);
        Assert.Contains("QueueWatchIntentsWithOutcomeAsync", service, StringComparison.Ordinal);
    }

    [Fact]
    public void IntentQueueLabelsThePlatformItCameFrom()
    {
        var engine = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));

        Assert.Contains("BuildQueueSourceLabel(platformLabel", engine, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildQueueSourceLabel(\"Apple Music\", options.CollectionType", engine, StringComparison.Ordinal);
    }

    [Fact]
    public void TidalArtistWatchUsesTheSameEndpointShapeTheAppAlreadyProves()
    {
        var service = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistWatchService.cs"));
        var controller = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Controllers", "ApiController.cs"));

        Assert.Contains("api.tidal.com/v1/artists/", controller, StringComparison.Ordinal);
        Assert.Contains("EPSANDSINGLES", controller, StringComparison.Ordinal);
        Assert.Contains("api.tidal.com/v1/artists/", service, StringComparison.Ordinal);
        Assert.Contains("TidalEpsAndSinglesFilter", service, StringComparison.Ordinal);
        Assert.Contains("ResolveTidalReleaseFilters", service, StringComparison.Ordinal);
    }

    [Fact]
    public void TidalArtistWatchHonoursTheWatchedAlbumGroups()
    {
        var service = File.ReadAllText(Path.Join(
            FindRepoRoot(), "DeezSpoTag.Web", "Services", "ArtistWatchService.cs"));
        var start = service.IndexOf("ResolveTidalReleaseFilters(IReadOnlyCollection<string> albumGroups)", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = service[start..(start + 700)];

        Assert.Contains("ShouldIncludeAlbumGroup(AlbumGroup, albumGroups)", body, StringComparison.Ordinal);
        Assert.Contains("ShouldIncludeAlbumGroup(SingleGroup, albumGroups)", body, StringComparison.Ordinal);
    }

    private static string ReadPlaylistSyncSource()
        => File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Web", "Services", "PlaylistSyncService.cs"));

    private static string ReadNavidromeSource()
        => File.ReadAllText(Path.Join(FindRepoRoot(), "DeezSpoTag.Integrations", "Navidrome", "NavidromeApiClient.cs"));

    private static string FindRepoRoot()
    {
        var directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (Directory.Exists(Path.Join(directory, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(directory, "DeezSpoTag.Tests")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
