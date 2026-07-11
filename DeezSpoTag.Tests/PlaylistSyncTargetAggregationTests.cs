using System.Collections.Generic;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class PlaylistSyncTargetAggregationTests
{
    [Fact]
    public void CombinePlaylistSyncTargetResults_ReturnsSuccessWhenAtLeastOneTargetSucceeds()
    {
        var result = PlaylistSyncService.CombinePlaylistSyncTargetResults(new List<(string Service, PlaylistSyncResult Result)>
        {
            ("plex", new PlaylistSyncResult(true, "Playlist synced to Plex.", "plex-1", SyncedTracks: 2, SourceTracks: 3, LocalMatches: 2, TargetMatches: 2)),
            ("jellyfin", PlaylistSyncResult.Failed("Jellyfin sync failed: connection refused")),
            ("navidrome", new PlaylistSyncResult(true, "Playlist synced to Navidrome.", "navidrome-1", SyncedTracks: 2, SourceTracks: 3, LocalMatches: 2, TargetMatches: 2))
        });

        Assert.True(result.Success);
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
}
