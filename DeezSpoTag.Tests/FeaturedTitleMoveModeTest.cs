using System.Collections.Generic;
using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Core.Utils;
using Xunit;

namespace DeezSpoTag.Tests;

/// <summary>
/// Regression coverage for the Featured To Title "Move to title" mode (value "2").
///
/// The mode used to append a featured credit even when the title already carried one in a
/// different spelling ("ft." / "featuring"), producing titles such as
/// "Rise Up (ft. Falz) (feat. Falz)", because the append decision only looked for the literal
/// "feat." while the removal logic stripped feat/ft/featuring.
/// </summary>
public sealed class FeaturedTitleMoveModeTest
{
    private static Track MakeTrack(string title, IReadOnlyList<string> main, IReadOnlyList<string> featured)
    {
        var artist = new Dictionary<string, List<string>> { ["Main"] = new List<string>(main) };
        if (featured.Count > 0)
        {
            artist["Featured"] = new List<string>(featured);
        }

        var all = new List<string>(main);
        all.AddRange(featured);

        return new Track
        {
            Title = title,
            MainArtist = new Artist(main.Count > 0 ? main[0] : string.Empty),
            Album = new Album("album-1", title),
            Artist = artist,
            Artists = all
        };
    }

    private static DeezSpoTagSettings MoveMode() => new()
    {
        FeaturedToTitle = "2",
        Tags = new TagSettings { MultiArtistSeparator = "default" }
    };

    [Theory]
    [InlineData("Rise Up")]
    [InlineData("Rise Up (live)")]
    public void MoveMode_AppendsTheCreditWhenTheTitleHasNone(string title)
    {
        var track = MakeTrack(title, new[] { "2Baba" }, new[] { "Falz" });

        track.ApplySettings(MoveMode());

        Assert.Equal($"{title} (feat. Falz)", track.Title);
    }

    [Theory]
    [InlineData("Rise Up (feat. Falz)")]
    [InlineData("Rise Up (ft. Falz)")]
    [InlineData("Rise Up (featuring Falz)")]
    [InlineData("Rise Up feat. Falz")]
    [InlineData("Rise Up ft. Falz")]
    [InlineData("Rise Up [feat. Falz]")]
    public void MoveMode_ProducesTheCanonicalFormForEverySpelling(string title)
    {
        var track = MakeTrack(title, new[] { "2Baba" }, new[] { "Falz" });

        track.ApplySettings(MoveMode());

        // Canonical form, identical to what the app writes when it adds a credit itself.
        Assert.Equal("Rise Up (feat. Falz)", track.Title);
    }

    [Theory]
    [InlineData("Rise Up (feat. Falz)")]
    [InlineData("Rise Up (ft. Falz)")]
    [InlineData("Rise Up (featuring Falz)")]
    [InlineData("Rise Up feat. Falz")]
    public void MoveMode_IsIdempotentAcrossRepeatedRuns(string title)
    {
        var track = MakeTrack(title, new[] { "2Baba" }, new[] { "Falz" });

        track.ApplySettings(MoveMode());
        var afterFirstRun = track.Title;
        track.ApplySettings(MoveMode());

        Assert.Equal(afterFirstRun, track.Title);
        Assert.Equal("Rise Up (feat. Falz)", track.Title);
    }

    [Fact]
    public void MoveMode_LeavesTheTitleAloneWhenNoFeatureArtistIsKnown()
    {
        var track = MakeTrack("Rise Up (feat. Falz)", new[] { "2Baba" }, System.Array.Empty<string>());

        track.ApplySettings(MoveMode());

        Assert.Equal("Rise Up (feat. Falz)", track.Title);
    }

    [Theory]
    [InlineData("Rise Up (feat. Falz)", "Rise Up (feat. Falz)")]
    [InlineData("Rise Up (ft. Falz)", "Rise Up (feat. Falz)")]
    [InlineData("Rise Up (featuring Falz)", "Rise Up (feat. Falz)")]
    [InlineData("Rise Up (Feat. Falz)", "Rise Up (feat. Falz)")]
    [InlineData("Rise Up feat. Falz", "Rise Up (feat. Falz)")]
    [InlineData("Rise Up ft. Falz", "Rise Up (feat. Falz)")]
    [InlineData("Rise Up [feat. Falz]", "Rise Up (feat. Falz)")]
    [InlineData("Rise Up [ft. Falz]", "Rise Up (feat. Falz)")]
    [InlineData("Rise Up (featuring Falz & Diamond)", "Rise Up (feat. Falz & Diamond)")]
    [InlineData("Rise Up (feat. Falz, Diamond)", "Rise Up (feat. Falz, Diamond)")]
    [InlineData("Rise Up (Live) (ft. Falz)", "Rise Up (Live) (feat. Falz)")]
    [InlineData("Rise Up", "Rise Up")]
    [InlineData("Rise Up (Live)", "Rise Up (Live)")]
    public void NormalizeCredit_RewritesTheSpellingAndKeepsTheNames(string title, string expected)
    {
        Assert.Equal(expected, FeaturedTitleMarker.NormalizeCredit(title));
    }

    [Fact]
    public void NormalizeCredit_DoesNotSwallowATrailingQualifierIntoTheCredit()
    {
        Assert.Equal(
            "Rise Up (feat. Falz) - Remastered",
            FeaturedTitleMarker.NormalizeCredit("Rise Up feat. Falz - Remastered"));
    }

    [Fact]
    public void NormalizeCredit_PreservesCreditedNamesThatTheArtistTagDoesNotKnow()
    {
        // The title may credit more people than the metadata lists; normalising must not
        // regenerate the credit from the artist tag and lose them.
        Assert.Equal(
            "Rise Up (feat. Falz & Diamond Platnumz)",
            FeaturedTitleMarker.NormalizeCredit("Rise Up (featuring Falz & Diamond Platnumz)"));
    }

    [Theory]
    [InlineData("Rise Up (feat. Falz)")]
    [InlineData("Rise Up (ft. Falz)")]
    [InlineData("Rise Up (featuring Falz)")]
    [InlineData("Rise Up feat. Falz")]
    [InlineData("Rise Up [feat. Falz]")]
    public void NormalizeCredit_IsStableWhenAppliedTwice(string title)
    {
        var once = FeaturedTitleMarker.NormalizeCredit(title);
        var twice = FeaturedTitleMarker.NormalizeCredit(once);

        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData("Rise Up (feat. Falz)", "Rise Up")]
    [InlineData("Rise Up (ft. Falz)", "Rise Up")]
    [InlineData("Rise Up (featuring Falz)", "Rise Up")]
    [InlineData("Rise Up [feat. Falz]", "Rise Up")]
    [InlineData("Rise Up feat. Falz", "Rise Up")]
    [InlineData("Rise Up", "Rise Up")]
    public void StripCredit_RemovesEveryAcceptedSpelling(string title, string expected)
    {
        Assert.Equal(expected, FeaturedTitleMarker.StripCredit(title));
    }

    [Fact]
    public void StripCredit_KeepsNonFeaturedQualifiers()
    {
        Assert.Equal(
            "Rise Up (Live)",
            FeaturedTitleMarker.StripCredit("Rise Up (Live) (feat. Falz)"));
    }

    [Theory]
    [InlineData("Rise Up (feat. Falz)", true)]
    [InlineData("Rise Up (ft. Falz)", true)]
    [InlineData("Rise Up (featuring Falz)", true)]
    [InlineData("Rise Up feat. Falz", true)]
    [InlineData("Rise Up [feat. Falz]", true)]
    [InlineData("Rise Up", false)]
    [InlineData("Rise Up (Live)", false)]
    [InlineData("With You", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HasCredit_RecognisesEveryAcceptedSpelling(string? title, bool expected)
    {
        Assert.Equal(expected, FeaturedTitleMarker.HasCredit(title));
    }

    /// <summary>
    /// Detection and removal must stay in step: anything reported as already credited must
    /// actually be removable, otherwise a title can be detected-but-not-stripped and the
    /// credit gets duplicated on the next run.
    /// </summary>
    [Theory]
    [InlineData("Rise Up (feat. Falz)")]
    [InlineData("Rise Up (ft. Falz)")]
    [InlineData("Rise Up (featuring Falz)")]
    [InlineData("Rise Up feat. Falz")]
    [InlineData("Rise Up [feat. Falz]")]
    public void EveryDetectedCreditIsAlsoRemovable(string title)
    {
        Assert.True(FeaturedTitleMarker.HasCredit(title));
        Assert.False(
            FeaturedTitleMarker.HasCredit(FeaturedTitleMarker.StripCredit(title)),
            $"stripping '{title}' should leave no credit behind");
    }
}
