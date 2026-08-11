using System;
using System.IO;
using System.Linq;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyRecommendationSyncGuardrailTests
{
    [Fact]
    public void SpotifyRecommendationCardsExposeNavidromeSyncAndMonitorActions()
    {
        var view = ReadSource("DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml");

        Assert.Contains("function syncSpotifyRecommendationPlaylistToNavidrome(button)", view, StringComparison.Ordinal);
        Assert.Contains("Sync to Navidrome", view, StringComparison.Ordinal);
        Assert.Contains("data-action=\"monitor\"", view, StringComparison.Ordinal);
        Assert.Contains("/api/spotify/recommendations/playlists/${encodeURIComponent(playlistId)}/sync", view, StringComparison.Ordinal);
        Assert.Contains("event.target.closest('#spotify-recommendations .spotify-recommendation-sync-btn')", view, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifyRecommendationSyncEndpointUsesExistingPlaylistSyncPipeline()
    {
        var controller = ReadSource("DeezSpoTag.Web", "Controllers", "Api", "SpotifyDiscoveryTracklistApiController.cs");
        var service = ReadSource("DeezSpoTag.Web", "Services", "PlaylistSyncService.cs");

        Assert.Contains("[HttpPost(\"/api/spotify/recommendations/playlists/{playlistId}/sync\")]", controller, StringComparison.Ordinal);
        Assert.Contains("SyncSpotifyRecommendationPlaylistToNavidromeAsync", controller, StringComparison.Ordinal);
        Assert.Contains("SyncSpotifyRecommendationPlaylistToNavidromeAsync", service, StringComparison.Ordinal);
        Assert.Contains("LoadTracksForSyncAsync(playlist, trackCandidates: null, cancellationToken)", service, StringComparison.Ordinal);
        Assert.Contains("SyncPlaylistToTargetAsync(\n            NavidromeService,", service.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("ResolveManagedVisualUrlAsync(\n                SpotifySource,", service.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var root = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(root))
        {
            var candidate = Path.Combine(new[] { root }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            root = Directory.GetParent(root)?.FullName;
        }

        throw new FileNotFoundException("Unable to locate source file.", Path.Combine(relativeParts));
    }
}
