using System;
using System.Reflection;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class AutoTagReleaseCategoryTests
{
    [Theory]
    [InlineData("single", "single")]
    [InlineData("Single", "single")]
    [InlineData("album", "album")]
    [InlineData("LP", "album")]
    [InlineData("EP", "ep")]
    [InlineData("Extended Play", "ep")]
    [InlineData("Compilation", "compilation")]
    [InlineData("Album; Compilation", "compilation")]
    public void Resolve_UsesExplicitReleaseTypeWhenAvailable(string input, string expected)
    {
        Assert.Equal(expected, AutoTagReleaseCategory.Resolve(input, 12));
    }

    [Theory]
    [InlineData(1, "single")]
    [InlineData(2, "album")]
    [InlineData(14, "album")]
    public void Resolve_UsesTrackTotalWhenExplicitReleaseTypeIsMissing(int trackTotal, string expected)
    {
        Assert.Equal(expected, AutoTagReleaseCategory.Resolve(null, trackTotal));
    }

    [Fact]
    public void Resolve_ReturnsNullWhenReleaseShapeIsUnknown()
    {
        Assert.Null(AutoTagReleaseCategory.Resolve(null, null));
    }

    [Theory]
    [InlineData("Album", "Compilation", "compilation")]
    [InlineData("Album", "EP", "ep")]
    [InlineData(null, "Single", "single")]
    public void Resolve_UsesProviderClassificationSignalsBeforeTrackCount(
        string? primaryType,
        string additionalType,
        string expected)
    {
        Assert.Equal(
            expected,
            AutoTagReleaseCategory.Resolve(primaryType, [additionalType], 12));
    }

    [Theory]
    [InlineData("single", 1, "single", true)]
    [InlineData("album", 12, "album", true)]
    [InlineData("EP", 5, "ep", true)]
    [InlineData("EP", 5, "album", false)]
    [InlineData("compilation", 30, "compilation", true)]
    [InlineData("compilation", 30, "album", false)]
    [InlineData("album", 12, "single", false)]
    public void MatchesPreference_EnforcesExplicitManualReleaseChoice(
        string releaseType,
        int trackTotal,
        string preference,
        bool expected)
    {
        Assert.Equal(
            expected,
            AutoTagReleaseCategory.MatchesPreference(releaseType, trackTotal, preference));
    }

    [Fact]
    public void MusicBrainzMapper_UsesCompilationSecondaryType()
    {
        var track = new MusicBrainzTrack { TrackTotal = 12 };
        var release = new Release
        {
            Id = "release-id",
            ReleaseGroup = new ReleaseGroup
            {
                Id = "release-group-id",
                PrimaryType = "Album",
                SecondaryTypes = ["Compilation"]
            }
        };
        var applyReleaseMetadata = typeof(MusicBrainzMatcher).GetMethod(
            "ApplyReleaseMetadata",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MusicBrainz release mapper not found.");

        applyReleaseMetadata.Invoke(null, [track, release]);

        Assert.Equal("compilation", track.ReleaseType);
        var releaseTypeTag = Assert.Single(track.Other, value => value.Key == "RELEASETYPE");
        Assert.Equal("compilation", Assert.Single(releaseTypeTag.Values));
    }

    [Theory]
    [InlineData("EP", "ep")]
    [InlineData("Compilation", "compilation")]
    public void DiscogsMapper_UsesFormatDescription(string description, string expected)
    {
        var release = new DiscogsRelease
        {
            Id = 123,
            Title = "Release",
            Tracks = [new DiscogsTrack { Position = "1", Title = "Track" }],
            Formats =
            [
                new DiscogsReleaseFormat
                {
                    Name = "Vinyl",
                    Descriptions = [description]
                }
            ]
        };
        var toTrack = typeof(DiscogsMatcher).GetMethod(
            "ToTrack",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Discogs release mapper not found.");
        var toAutoTagTrack = typeof(DiscogsMatcher).GetMethod(
            "ToAutoTagTrack",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Discogs AutoTag mapper not found.");

        var providerTrack = Assert.IsType<DiscogsTrackInfo>(
            toTrack.Invoke(null, [release, 0, new DiscogsConfig()]));
        var autoTagTrack = Assert.IsType<AutoTagTrack>(
            toAutoTagTrack.Invoke(null, [providerTrack]));

        Assert.Equal(expected, autoTagTrack.ReleaseType);
    }
}
