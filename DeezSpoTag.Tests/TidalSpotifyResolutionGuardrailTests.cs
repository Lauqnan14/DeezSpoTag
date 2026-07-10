using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TidalSpotifyResolutionGuardrailTests
{
    [Fact]
    public void DownloadIntentSpotifyIdentityResolution_DoesNotUseSongLink()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");

        Assert.DoesNotContain("TryPopulateSpotifyIdentityFromDeezerAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PopulateSpotifyIdentityFromSourceUrlAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveByDeezerTrackIdAsync(intent.DeezerId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("engine != SpotifyPlatform && engine != DeezerPlatform && engine != ApplePlatform", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TidalPlatform => songLink.TidalUrl", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsSongLinkDisabledForEngine", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadIntentTidalMetadataResolution_UsesTidalServiceMetadata()
    {
        var intentSource = ReadSource("DeezSpoTag.Web", "Services", "DownloadIntentService.cs");
        var tidalSource = ReadSource("DeezSpoTag.Services", "Download", "Tidal", "TidalDownloadService.cs");

        Assert.Contains("PopulateTidalMetadataAsync", intentSource, StringComparison.Ordinal);
        Assert.Contains("ResolveTrackMetadataAsync", tidalSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackAvailabilityColumn_UsesCentralResolverNotLegacySongLinkOrDownloadIntentAvailability()
    {
        var source = ReadSource("DeezSpoTag.Web", "Services", "TrackAvailabilityService.cs");

        Assert.Contains("ITrackIdentityResolver", source, StringComparison.Ordinal);
        Assert.Contains("ResolveAvailabilityTargets", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SongLink", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveProxy", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadIntentService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LookupAvailabilityAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CentralResolver_UsesAuthenticatedDeezerAndSpotifyLibrespotFallback()
    {
        var resolverSource = ReadSource("DeezSpoTag.Web", "Services", "TrackIdentityResolver.cs");
        var searchSource = ReadSource("DeezSpoTag.Web", "Services", "SpotifySearchService.cs");

        Assert.Contains("AuthenticatedDeezerService", resolverSource, StringComparison.Ordinal);
        Assert.Contains("DeezSpoTag.Integrations.Deezer", resolverSource, StringComparison.Ordinal);
        Assert.Contains("deezer-auth-missing", resolverSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PublicDeezerClient", resolverSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Services.AutoTag.DeezerClient", resolverSource, StringComparison.Ordinal);
        Assert.Contains("SearchTracksViaLibrespotAsync", searchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SpotifySearch_IsrcHydrationUsesLibrespotTrackMetadataNotBrokenQuerySearch()
    {
        var searchSource = ReadSource("DeezSpoTag.Web", "Services", "SpotifySearchService.cs");
        var blobSource = ReadSource("DeezSpoTag.Web", "Services", "SpotifyBlobService.cs");

        Assert.Contains("FetchLibrespotTrackIsrcsAsync", searchSource, StringComparison.Ordinal);
        Assert.Contains("GetLibrespotTracksAsync", searchSource, StringComparison.Ordinal);
        Assert.Contains("SearchTracksViaLibrespotAsync", searchSource, StringComparison.Ordinal);
        Assert.Contains("SearchLibrespotTracksAsync", blobSource, StringComparison.Ordinal);
        Assert.DoesNotContain("api.spotify.com" + "/v1" + "/search", searchSource, StringComparison.Ordinal);
        Assert.False(File.Exists(ResolveRepoCandidatePath("DeezSpoTag.Services", "Download", "Spotify", "SpotifyIdResolver.cs")));
        Assert.True(File.Exists(ResolveRepoCandidatePath("DeezSpoTag.Web", "Tools", "spotify_librespot_search.py")));
    }

    [Fact]
    public void SpotifyWebResolver_IsrcSearchRequiresMatchingIsrc()
    {
        var items = new List<SpotifySearchItem>
        {
            new("wrong", "Wrong Song", "track", "https://open.spotify.com/track/wrong", null, "Wrong Artist - Wrong Album", 180_000),
            new("expected", "Target Song", "track", "https://open.spotify.com/track/expected", null, "Target Artist - Target Album", 180_000, Isrc: "TZA1X2200742")
        };

        var selected = InvokeSelectBestCandidate(
            items,
            "Target Song",
            "Target Artist",
            "Target Album",
            "TZA1X2200742",
            allowFirstWhenMetadataMissing: true);

        Assert.Equal("expected", selected?.Id);
    }

    [Fact]
    public void SpotifyWebResolver_IsrcSearchRejectsMetadataMatchWithoutIsrcEvidence()
    {
        var items = new List<SpotifySearchItem>
        {
            new("expected-looking", "Target Song", "track", "https://open.spotify.com/track/expected-looking", null, "Target Artist - Target Album", 180_000)
        };

        var selected = InvokeSelectBestCandidate(
            items,
            "Target Song",
            "Target Artist",
            "Target Album",
            "TZA1X2200742",
            allowFirstWhenMetadataMissing: true);

        Assert.Null(selected);
    }

    [Fact]
    public void SpotifyWebResolver_IsrcSearchRejectsConflictingMetadata()
    {
        var items = new List<SpotifySearchItem>
        {
            new("wrong", "Wrong Song", "track", "https://open.spotify.com/track/wrong", null, "Wrong Artist - Wrong Album", 180_000)
        };

        var selected = InvokeSelectBestCandidate(
            items,
            "Target Song",
            "Target Artist",
            "Target Album",
            "TZA1X2200742",
            allowFirstWhenMetadataMissing: true);

        Assert.Null(selected);
    }

    private static SpotifySearchItem? InvokeSelectBestCandidate(
        List<SpotifySearchItem> items,
        string title,
        string artist,
        string? album,
        string? isrc,
        bool allowFirstWhenMetadataMissing)
    {
        var method = typeof(DeezSpoTag.Web.Services.SpotifyIdResolver).GetMethod(
            "SelectBestCandidate",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return method!.Invoke(null, [items, title, artist, album, isrc, allowFirstWhenMetadataMissing]) as SpotifySearchItem;
    }

    private static string ReadSource(params string[] relativeParts)
        => File.ReadAllText(ResolveRepoPath(relativeParts));

    private static string ResolveRepoPath(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(relativeParts));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate source file.", Path.Combine(relativeParts));
    }

    private static string ResolveRepoCandidatePath(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var marker = Path.Combine(dir.FullName, "DeezSpoTag.Web", "DeezSpoTag.Web.csproj");
            if (File.Exists(marker))
            {
                return Path.Combine(dir.FullName, Path.Combine(relativeParts));
            }

            dir = dir.Parent;
        }

        return Path.Combine(relativeParts);
    }
}
