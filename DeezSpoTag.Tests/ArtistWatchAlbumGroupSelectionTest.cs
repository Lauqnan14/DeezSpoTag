using System;
using System.Collections.Generic;
using System.Linq;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Covers the artist watch option separation: an explicitly empty album-group selection
/// (for example "Spotify's Top Songs" only) must stay empty instead of being replaced by the
/// first-run album+single default, and each provider must honour the selected groups.
/// </summary>
public sealed class ArtistWatchAlbumGroupSelectionTest
{
    private const string AlbumGroup = "album";
    private const string SingleGroup = "single";
    private const string CompilationGroup = "compilation";
    private const string AppearsOnGroup = "appears_on";

    [Fact]
    public void NormalizeAlbumGroups_NeverConfigured_AppliesFirstRunDefault()
    {
        Assert.Equal(new[] { AlbumGroup, SingleGroup }, ArtistWatchService.NormalizeAlbumGroups(null));
    }

    [Fact]
    public void NormalizeAlbumGroups_ExplicitEmptySelection_StaysEmpty()
    {
        // Regression: selecting only "Spotify's Top Songs" used to be rewritten to
        // album+single, so album and singles always came back selected.
        Assert.Empty(ArtistWatchService.NormalizeAlbumGroups(Array.Empty<string>()));
    }

    [Fact]
    public void NormalizeAlbumGroups_GenuineAlbumAndSingleSelection_IsPreserved()
    {
        // Users who really did pick albums and singles must keep their selection.
        var normalized = ArtistWatchService.NormalizeAlbumGroups(new[] { AlbumGroup, SingleGroup });

        Assert.Equal(new[] { AlbumGroup, SingleGroup }, normalized);
    }

    [Fact]
    public void NormalizeAlbumGroups_UnknownValues_DoNotResurrectTheDefault()
    {
        // Junk input is an explicit (empty) choice, not "never configured".
        Assert.Empty(ArtistWatchService.NormalizeAlbumGroups(new[] { "unsupported" }));
    }

    [Theory]
    [InlineData(AlbumGroup)]
    [InlineData(SingleGroup)]
    [InlineData(CompilationGroup)]
    [InlineData(AppearsOnGroup)]
    public void ShouldIncludeAlbumGroup_EmptySelection_ExcludesEveryRelease(string albumGroup)
    {
        // An empty selection must not fall back to "include everything", otherwise fixing the
        // top-songs-only case would hand the user the artist's entire discography.
        Assert.False(ArtistWatchService.ShouldIncludeAlbumGroup(albumGroup, Array.Empty<string>()));
    }

    [Fact]
    public void ShouldIncludeAlbumGroup_AlbumOnly_ExcludesOtherGroups()
    {
        var groups = new[] { AlbumGroup };

        Assert.True(ArtistWatchService.ShouldIncludeAlbumGroup(AlbumGroup, groups));
        Assert.False(ArtistWatchService.ShouldIncludeAlbumGroup(SingleGroup, groups));
        Assert.False(ArtistWatchService.ShouldIncludeAlbumGroup(CompilationGroup, groups));
        Assert.False(ArtistWatchService.ShouldIncludeAlbumGroup(AppearsOnGroup, groups));
    }

    [Fact]
    public void ShouldIncludeAlbumGroup_SingleSelection_IncludesSpotifyEps()
    {
        // Spotify reports EPs as their own release type but buckets them with singles
        // (ResolveDiscographySection -> "singles_eps"), so "single" must match them.
        var groups = new[] { SingleGroup };

        Assert.True(ArtistWatchService.ShouldIncludeAlbumGroup("ep", groups));
        Assert.True(ArtistWatchService.ShouldIncludeAlbumGroup(SingleGroup, groups));
        Assert.False(ArtistWatchService.ShouldIncludeAlbumGroup(AlbumGroup, groups));
    }

    [Fact]
    public void ShouldIncludeAlbumGroup_AppearsOnOnly_ExcludesAlbums()
    {
        var groups = new[] { AppearsOnGroup };

        Assert.True(ArtistWatchService.ShouldIncludeAlbumGroup(AppearsOnGroup, groups));
        Assert.False(ArtistWatchService.ShouldIncludeAlbumGroup(AlbumGroup, groups));
        Assert.False(ArtistWatchService.ShouldIncludeAlbumGroup(SingleGroup, groups));
    }

    [Fact]
    public void BuildFilteredAlbumPage_AlbumSelection_ExcludesSinglesAndCompilations()
    {
        var page = SpotifyArtistService.BuildFilteredAlbumPage(
            Discography(),
            new[] { AlbumGroup },
            offset: 0,
            limit: 50);

        Assert.Equal(new[] { "album-1", "album-2" }, page.Albums.Select(album => album.Id));
        Assert.Equal(2, page.Total);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void BuildFilteredAlbumPage_SingleSelection_IncludesEps()
    {
        var page = SpotifyArtistService.BuildFilteredAlbumPage(
            Discography(),
            new[] { SingleGroup },
            offset: 0,
            limit: 50);

        Assert.Equal(new[] { "single-1", "ep-1" }, page.Albums.Select(album => album.Id));
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public void BuildFilteredAlbumPage_EmptySelection_ReturnsNoReleases()
    {
        var page = SpotifyArtistService.BuildFilteredAlbumPage(
            Discography(),
            Array.Empty<string>(),
            offset: 0,
            limit: 50);

        Assert.Empty(page.Albums);
        Assert.Equal(0, page.Total);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void BuildFilteredAlbumPage_PaginationCountsTheFilteredReleases()
    {
        // Paging must walk the selected releases, not the unfiltered discography:
        // offset 1 with limit 1 has to return the second *album*, not the second release.
        var first = SpotifyArtistService.BuildFilteredAlbumPage(
            Discography(),
            new[] { AlbumGroup },
            offset: 0,
            limit: 1);

        Assert.Equal(new[] { "album-1" }, first.Albums.Select(album => album.Id));
        Assert.True(first.HasMore);

        var second = SpotifyArtistService.BuildFilteredAlbumPage(
            Discography(),
            new[] { AlbumGroup },
            offset: 1,
            limit: 1);

        Assert.Equal(new[] { "album-2" }, second.Albums.Select(album => album.Id));
        Assert.Equal(2, second.Total);
        Assert.False(second.HasMore);
    }

    [Fact]
    public void BuildFilteredAlbumPage_PreservesReleaseMetadata()
    {
        // The projection used to hardcode the album group and drop the release date.
        var page = SpotifyArtistService.BuildFilteredAlbumPage(
            Discography(),
            new[] { CompilationGroup },
            offset: 0,
            limit: 50);

        var compilation = Assert.Single(page.Albums);
        Assert.Equal(CompilationGroup, compilation.AlbumGroup);
        Assert.Equal("2020-05-01", compilation.ReleaseDate);
    }

    private static List<SpotifyAlbumSummary> Discography() =>
    [
        Summary("album-1", "ALBUM", "2019-01-01"),
        Summary("single-1", "SINGLE", "2019-06-01"),
        Summary("album-2", "ALBUM", "2020-01-01"),
        Summary("compilation-1", "COMPILATION", "2020-05-01"),
        Summary("ep-1", "EP", "2021-01-01")
    ];

    private static SpotifyAlbumSummary Summary(string id, string releaseType, string releaseDate)
        => new(
            id,
            id,
            "Artist",
            null,
            $"https://open.spotify.com/album/{id}",
            10,
            releaseDate,
            AlbumGroup: null,
            ReleaseType: releaseType);
}
