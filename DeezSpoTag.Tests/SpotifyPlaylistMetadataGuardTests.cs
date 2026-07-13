using System;
using System.Collections.Generic;
using System.IO;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class SpotifyPlaylistMetadataGuardTests
{
    [Fact]
    public void GenericSpotifyPlaylistMetadata_IsNotTrusted()
    {
        var metadata = new SpotifyUrlMetadata(
            "playlist",
            "37i9dQZF1DXcBWIGoYBM5M",
            "Spotify Playlist",
            "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M",
            null,
            null,
            0,
            null,
            new List<SpotifyTrackSummary>(),
            new List<SpotifyAlbumSummary>(),
            "Spotify",
            null,
            null);

        Assert.False(SpotifyMetadataService.HasTrustedPlaylistMetadata(metadata));
        Assert.True(SpotifyMetadataService.IsGenericSpotifyPlaylistName(metadata.Name));
    }

    [Fact]
    public void RealSpotifyPlaylistMetadata_IsTrusted()
    {
        var metadata = new SpotifyUrlMetadata(
            "playlist",
            "37i9dQZF1DXcBWIGoYBM5M",
            "Today&apos;s Top Hits",
            "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M",
            "https://i.scdn.co/image/ab67706f00000002real",
            null,
            50,
            null,
            new List<SpotifyTrackSummary>(),
            new List<SpotifyAlbumSummary>(),
            "Spotify",
            1_000,
            "snapshot");

        Assert.True(SpotifyMetadataService.HasTrustedPlaylistMetadata(metadata));
        Assert.False(SpotifyMetadataService.IsGenericSpotifyPlaylistName(metadata.Name));
    }

    [Fact]
    public void SpotifyMetadataService_DoesNotManufactureLightweightPlaylistMetadata()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "SpotifyMetadataService.cs"));

        Assert.DoesNotContain("BuildLightweightPlaylistMetadata", source, StringComparison.Ordinal);
        Assert.DoesNotContain("returning lightweight metadata", source, StringComparison.Ordinal);
        Assert.Contains("HasTrustedPlaylistMetadata(metadata)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchlistEngine_ProtectsExistingSpotifyMetadataFromGenericSnapshots()
    {
        var repoRoot = ResolveRepoRoot();
        var source = File.ReadAllText(Path.Join(repoRoot, "DeezSpoTag.Web", "Services", "WatchlistEngine.cs"));

        Assert.Contains("HasTrustedLivePlaylistMetadata(source, liveSnapshot)", source, StringComparison.Ordinal);
        Assert.Contains("SpotifyMetadataService.IsGenericSpotifyPlaylistName(liveSnapshot.Name)", source, StringComparison.Ordinal);
        Assert.Contains("ResolveCurrentPlaylistImageUrl(playlist, liveSnapshot, trustedMetadata)", source, StringComparison.Ordinal);
        Assert.Contains("PreferSpotifyPlaylistMetadataValue(metadata.Name, page.Name", source, StringComparison.Ordinal);
    }

    private static string ResolveRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(Path.Join(current, "DeezSpoTag.Web"))
                && Directory.Exists(Path.Join(current, "DeezSpoTag.Tests")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new DirectoryNotFoundException("Could not resolve repository root.");
    }
}
