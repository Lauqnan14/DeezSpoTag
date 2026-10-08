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
        Assert.True(ArtistIdentityTextNormalizer.ShouldRequireAlbumOverlap(["Album One"]));
        Assert.True(ArtistIdentityTextNormalizer.ShouldRequireAlbumOverlap(["Album One", "Album Two"]));
        Assert.False(ArtistIdentityTextNormalizer.ShouldRequireAlbumOverlap([]));
        // FilterResolvableTitles keeps originals when everything is compilation-like,
        // so a compilation-only set still requires one of those titles to overlap.
        Assert.True(ArtistIdentityTextNormalizer.ShouldRequireAlbumOverlap(
            ArtistIdentityTextNormalizer.FilterResolvableTitles(["Greatest Hits", "Best of Something"])));
        Assert.True(ArtistIdentityTextNormalizer.ShouldRequireAlbumOverlap(
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

    // ---------------------------------------------------------------------
    // Graded overlap
    //
    // CountAlbumOverlap answers "how many"; the score also answers "how
    // confident" and names the evidence, so a rejection is diagnosable
    // rather than a silent null.
    // ---------------------------------------------------------------------

    [Fact]
    public void ScoreAlbumOverlap_ReportsCountRatioAndTheMatchingTitles()
    {
        var held = new[] { "Abbey Road", "Let It Be", "Help!" };
        var candidate = new[] { "Abbey Road (Remastered)", "Let It Be", "Unrelated" };

        var score = ArtistIdentityTextNormalizer.ScoreAlbumOverlap(held, candidate);

        Assert.Equal(2, score.MatchedCount);
        Assert.Equal(3, score.HeldCount);
        Assert.Equal(2d / 3d, score.Ratio, 6);
        Assert.True(score.HasOverlap);
        // The matched titles are the held ones, so a log line points at the
        // albums that actually agreed.
        Assert.Equal(["Abbey Road", "Let It Be"], score.MatchedTitles);
    }

    /// <summary>
    /// The rule that makes a wrong same-name artist impossible: no album in
    /// common is not a match, and the score must be able to say so.
    /// </summary>
    [Fact]
    public void ScoreAlbumOverlap_HasNoOverlapWhenNothingIsShared()
    {
        var score = ArtistIdentityTextNormalizer.ScoreAlbumOverlap(
            ["Album One", "Album Two"],
            ["Someone Else Entirely"]);

        Assert.Equal(0, score.MatchedCount);
        Assert.False(score.HasOverlap);
        Assert.Equal(0d, score.Ratio);
        Assert.Empty(score.MatchedTitles);
    }

    /// <summary>
    /// A single shared album is a match, including for a one-album artist.
    /// The threshold is one album, with no additional ratio gate.
    /// </summary>
    [Fact]
    public void ScoreAlbumOverlap_OneSharedAlbumIsEnoughForAOneAlbumArtist()
    {
        var score = ArtistIdentityTextNormalizer.ScoreAlbumOverlap(["Only Album"], ["Only Album", "Extra"]);

        Assert.Equal(1, score.MatchedCount);
        Assert.Equal(1d, score.Ratio);
        Assert.True(score.HasOverlap);
    }

    /// <summary>
    /// More shared albums must score strictly higher, so the best candidate can
    /// be chosen from a list of same-name search hits.
    /// </summary>
    [Fact]
    public void ScoreAlbumOverlap_MoreSharedAlbumsScoresHigher()
    {
        var held = new[] { "A", "B", "C", "D" };

        var weak = ArtistIdentityTextNormalizer.ScoreAlbumOverlap(held, ["A", "Z", "Y", "X"]);
        var strong = ArtistIdentityTextNormalizer.ScoreAlbumOverlap(held, ["A", "B", "C", "Y"]);

        Assert.True(strong.MatchedCount > weak.MatchedCount);
        Assert.True(strong.Ratio > weak.Ratio);
    }

    /// <summary>
    /// An empty library side yields no score rather than a division by zero.
    /// The caller decides what an absent cross-check means; the scorer only
    /// reports what it can see.
    /// </summary>
    [Fact]
    public void ScoreAlbumOverlap_HandlesEmptyHeldCollection()
    {
        var score = ArtistIdentityTextNormalizer.ScoreAlbumOverlap(
            Array.Empty<string>(), ["Album One"]);

        Assert.Equal(0, score.MatchedCount);
        Assert.Equal(0, score.HeldCount);
        Assert.Equal(0d, score.Ratio);
        Assert.False(score.HasOverlap);
    }

    /// <summary>
    /// Overlap must be counted against the resolvable titles only, so a
    /// compilation in the library cannot make an unrelated artist look like a
    /// match.
    /// </summary>
    [Fact]
    public void ScoreAlbumOverlap_CountsHeldTitlesOnce()
    {
        var held = new[] { "Abbey Road", "Abbey Road" };
        var score = ArtistIdentityTextNormalizer.ScoreAlbumOverlap(held, ["Abbey Road"]);

        // Two held rows, one album: the score is a title count, not a row count.
        Assert.Equal(1, score.MatchedCount);
    }
}
