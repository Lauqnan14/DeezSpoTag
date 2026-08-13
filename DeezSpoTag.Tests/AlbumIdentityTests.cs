using System;
using DeezSpoTag.Core.Models;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AlbumIdentityTests
{
    [Fact]
    public void BuildKey_IsCaseAndWhitespaceInsensitive()
    {
        Assert.Equal(
            AlbumIdentity.BuildKey("Terry Linen", "Universal Message Vol. 2"),
            AlbumIdentity.BuildKey("  terry linen ", "UNIVERSAL MESSAGE VOL. 2"));
    }

    [Fact]
    public void BuildKey_IsNullWithoutAnAlbumTitle()
    {
        Assert.Null(AlbumIdentity.BuildKey("Terry Linen", null));
        Assert.Null(AlbumIdentity.BuildKey("Terry Linen", "   "));
    }

    [Fact]
    public void BuildKey_SeparatesDifferentAlbumsBySameArtist()
    {
        Assert.NotEqual(
            AlbumIdentity.BuildKey("Terry Linen", "Volume 1"),
            AlbumIdentity.BuildKey("Terry Linen", "Volume 2"));
    }

    [Fact]
    public void Establish_FirstTrackDefinesTheAlbumIdentity()
    {
        var registry = new AlbumIdentityRegistry();
        var key = AlbumIdentity.BuildKey("Terry Linen", "Universal Message Vol. 2")!;

        registry.Establish(key, new AlbumIdentity("2019-03-01", "alb-1", "art-1"));
        var second = registry.Establish(key, new AlbumIdentity("2016-07-22", "single-9", "art-1"));

        Assert.Equal("2019-03-01", second.ReleaseDate);
        Assert.Equal("alb-1", second.AlbumId);
    }

    [Fact]
    public void Establish_FillsBlanksFromLaterTracksWithoutOverwriting()
    {
        var registry = new AlbumIdentityRegistry();
        var key = "k";

        registry.Establish(key, new AlbumIdentity("2019-03-01", null, null));
        var merged = registry.Establish(key, new AlbumIdentity("2016-07-22", "alb-1", "art-1"));

        Assert.Equal("2019-03-01", merged.ReleaseDate);
        Assert.Equal("alb-1", merged.AlbumId);
        Assert.Equal("art-1", merged.AlbumArtistId);
    }

    [Fact]
    public void Establish_SeedFromDiskOutranksTheFirstMatchOfThisRun()
    {
        var registry = new AlbumIdentityRegistry();

        var established = registry.Establish(
            "k",
            new AlbumIdentity("2016-07-22", "single-9", null),
            new AlbumIdentity("2019-03-01", "alb-1", null));

        Assert.Equal("2019-03-01", established.ReleaseDate);
        Assert.Equal("alb-1", established.AlbumId);
    }

    [Fact]
    public void Establish_SeedIsOnlyConsultedForTheFirstTrackOfAGroup()
    {
        var registry = new AlbumIdentityRegistry();

        registry.Establish("k", new AlbumIdentity("2019-03-01", null, null));
        var later = registry.Establish("k", new AlbumIdentity("2016-07-22", null, null), new AlbumIdentity("1999-01-01", null, null));

        Assert.Equal("2019-03-01", later.ReleaseDate);
    }

    [Fact]
    public void Establish_DistinctAlbumsDoNotShareIdentity()
    {
        var registry = new AlbumIdentityRegistry();

        registry.Establish("a", new AlbumIdentity("2019-03-01", "alb-1", null));
        var other = registry.Establish("b", new AlbumIdentity("2021-05-05", "alb-2", null));

        Assert.Equal("2021-05-05", other.ReleaseDate);
        Assert.Equal("alb-2", other.AlbumId);
    }

    [Fact]
    public void Establish_WithoutAKeyReturnsTheCandidateUnchanged()
    {
        var registry = new AlbumIdentityRegistry();
        var candidate = new AlbumIdentity("2016-07-22", "single-9", null);

        Assert.Same(candidate, registry.Establish(null, candidate));
    }

    [Theory]
    [InlineData("2019-03-01", 2019, 3, 1)]
    [InlineData("2019-03", 2019, 3, 1)]
    [InlineData("2019", 2019, 1, 1)]
    public void ParseReleaseDate_AcceptsThePartialFormsProvidersReturn(string value, int year, int month, int day)
    {
        var parsed = AlbumIdentity.ParseReleaseDate(value);

        Assert.Equal(new DateTime(year, month, day), parsed);
    }

    [Fact]
    public void ParseReleaseDate_RejectsGarbage()
    {
        Assert.Null(AlbumIdentity.ParseReleaseDate("not a date"));
        Assert.Null(AlbumIdentity.ParseReleaseDate(null));
    }

    [Fact]
    public void FormatReleaseDate_RoundTripsThroughParse()
    {
        var formatted = AlbumIdentity.FormatReleaseDate(new DateTime(2019, 3, 1));

        Assert.Equal("2019-03-01", formatted);
        Assert.Equal(new DateTime(2019, 3, 1), AlbumIdentity.ParseReleaseDate(formatted));
    }

    [Fact]
    public void IsEmpty_IsTrueOnlyWhenEveryFieldIsBlank()
    {
        Assert.True(AlbumIdentity.Empty.IsEmpty);
        Assert.True(new AlbumIdentity("  ", null, "").IsEmpty);
        Assert.False(new AlbumIdentity(null, "alb-1", null).IsEmpty);
    }
}
