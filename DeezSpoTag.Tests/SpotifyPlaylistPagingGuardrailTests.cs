using System;
using System.IO;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyPlaylistPagingGuardrailTests
{
    [Fact]
    public void PlaylistPageFailure_IsExplicitAndNeverLooksAvailable()
    {
        var page = SpotifyPlaylistPage.Failed(100, "spotify_auth_unavailable");

        Assert.False(page.IsComplete);
        Assert.False(page.HasMore);
        Assert.Empty(page.Tracks);
        Assert.Equal(100, page.NextOffset);
        Assert.Equal("spotify_auth_unavailable", page.FailureCode);
    }

    [Fact]
    public void Tracklist_UsesOnePagedEndpointAndProviderNextOffset()
    {
        var root = ResolveRepoRoot();
        var controller = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Controllers", "Api", "SpotifyPlaylistTracklistApiController.cs"));
        var view = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml"));

        Assert.DoesNotContain("playlist/metadata", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("playlist/metadata", view, StringComparison.Ordinal);
        Assert.Contains("playlist/tracks", view, StringComparison.Ordinal);
        Assert.Contains("nextOffset = page.NextOffset", controller, StringComparison.Ordinal);
        Assert.Contains("payload.nextOffset", view, StringComparison.Ordinal);
        Assert.DoesNotContain("trackSource === 'librespot' ? 1000 : 50", view, StringComparison.Ordinal);
    }

    [Fact]
    public void VisiblePageMatching_RemainsImmediateForPathfinderAndLibrespot()
    {
        var root = ResolveRepoRoot();
        var controller = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Controllers", "Api", "SpotifyPlaylistTracklistApiController.cs"));
        var view = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Views", "Tracklist", "Index.cshtml"));

        Assert.Contains("StartVisibleTrackMatching", controller, StringComparison.Ordinal);
        Assert.Contains("ApplyStoredMatchesToTracks", controller, StringComparison.Ordinal);
        Assert.Contains("appendSpotifyTrackRows(tracks)", view, StringComparison.Ordinal);
        Assert.Contains("startSpotifyMatchPolling", view, StringComparison.Ordinal);
        Assert.Contains("void hydrateLibrespotTrackDetails(tracks)", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Watchlist_CompletenessUsesConsumedSourceItemsInsteadOfParsedTrackCount()
    {
        var root = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));

        Assert.Contains("sourceItemsConsumed += page.SourceItemCount", source, StringComparison.Ordinal);
        Assert.Contains("offset = page.NextOffset", source, StringComparison.Ordinal);
        Assert.Contains("sourceItemsConsumed < metadata.TotalTracks.Value", source, StringComparison.Ordinal);
        Assert.DoesNotContain("offset += page.Tracks.Count", source, StringComparison.Ordinal);
        Assert.DoesNotContain("candidates.Count < metadata.TotalTracks.Value", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PathfinderPageRequests_DoNotUseFullPlaylistExpansionOrCacheFailures()
    {
        var root = ResolveRepoRoot();
        var metadata = File.ReadAllText(Path.Join(root, "DeezSpoTag.Web", "Services", "SpotifyMetadataService.cs"));

        Assert.Contains("FetchPlaylistPageAsync(", metadata, StringComparison.Ordinal);
        Assert.Contains("if (page.IsComplete)", metadata, StringComparison.Ordinal);
        Assert.Contains("if (tracks.Count == 0)", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("FetchSpotiFlacPlaylistAsync", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPathfinderPlaylistTracksAsync", metadata, StringComparison.Ordinal);
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(current.FullName, "DeezSpoTag.Tests")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
