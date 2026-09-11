using System;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class ArtistIdentityTextNormalizerTest
{
    [Theory]
    [InlineData("Abbey Road [Remastered]", "abbey road")]
    [InlineData("Abbey Road (Deluxe Edition)", "abbey road")]
    // Bare "feat." only strips the marker (Spotify-matcher parity); bracketed forms
    // strip the whole group. Track titles strip trailing featuring segments entirely.
    [InlineData("Abbey Road feat. Someone", "abbey road someone")]
    [InlineData("Abbey-Road!", "abbey road")]
    public void NormalizeAlbumTitle_StripsSuffixesAndPunctuation(string input, string expected)
    {
        Assert.Equal(expected, ArtistIdentityTextNormalizer.NormalizeAlbumTitle(input));
    }

    [Theory]
    [InlineData("Song feat. X", "song")]
    [InlineData("Song (feat. X)", "song")]
    [InlineData("Song [2011 Remaster]", "song")]
    public void NormalizeTrackTitle_StripsFeaturingAndSuffixes(string input, string expected)
    {
        Assert.Equal(expected, ArtistIdentityTextNormalizer.NormalizeTrackTitle(input));
    }

    [Theory]
    [InlineData("Beyonce", "Beyoncé")]
    [InlineData("Motley Crue", "Mötley Crüe")]
    [InlineData("Soulja Boy Tell 'Em", "Soulja Boy")]
    [InlineData("2 pac", "2pac")]
    [InlineData("ROMANS", "RØMANS")]
    [InlineData("Simon & Garfunkel", "Simon and Garfunkel")]
    public void NamesEquivalent_FoldsAccentsAndAliases(string local, string candidate)
    {
        Assert.True(ArtistIdentityTextNormalizer.NamesEquivalent(local, candidate));
    }

    [Theory]
    [InlineData("Bob Marley", "Bob Dylan")]
    [InlineData("2 pac", "2 chainz")]
    public void NamesEquivalent_RejectsDifferentArtists(string local, string candidate)
    {
        Assert.False(ArtistIdentityTextNormalizer.NamesEquivalent(local, candidate));
    }

    [Fact]
    public void ShouldRequireAlbumOverlap_UsesResolvableAlbumCount()
    {
        Assert.False(ArtistIdentityTextNormalizer.ShouldRequireAlbumOverlap(["Album One"]));
        Assert.True(ArtistIdentityTextNormalizer.ShouldRequireAlbumOverlap(["Album One", "Album Two"]));
        // FilterResolvableTitles keeps originals when everything is compilation-like
        // (Spotify-parity), so a compilation-only set of two still requires overlap.
        Assert.True(ArtistIdentityTextNormalizer.ShouldRequireAlbumOverlap(
            ArtistIdentityTextNormalizer.FilterResolvableTitles(["Greatest Hits", "Best of Something"])));
        Assert.False(ArtistIdentityTextNormalizer.ShouldRequireAlbumOverlap(
            ArtistIdentityTextNormalizer.FilterResolvableTitles(["Greatest Hits", "Album One"])));
    }

    [Fact]
    public void FilterResolvableTitles_DropsCompilationsButKeepsOriginalsWhenAllFiltered()
    {
        var filtered = ArtistIdentityTextNormalizer.FilterResolvableTitles(
            ["Greatest Hits", "Album One", "The Best of X"]);
        Assert.Equal(["Album One"], filtered);

        var kept = ArtistIdentityTextNormalizer.FilterResolvableTitles(["Greatest Hits"]);
        Assert.Equal(["Greatest Hits"], kept);
    }

    [Fact]
    public void CountAlbumOverlap_MatchesNormalizedTitles()
    {
        var local = new[] { "Abbey Road [Remastered]", "Let It Be (Deluxe)", "Random Album" };
        var candidate = new[] { "Abbey Road", "Let It Be", "Something Else" };
        Assert.Equal(2, ArtistIdentityTextNormalizer.CountAlbumOverlap(local, candidate));
    }

    [Fact]
    public void CountAlbumOverlap_ReturnsZeroForEmptyCandidate()
    {
        var local = new[] { "Album One" };
        Assert.Equal(0, ArtistIdentityTextNormalizer.CountAlbumOverlap(local, Array.Empty<string>()));
    }
}
