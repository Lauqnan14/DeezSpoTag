using System;
using System.Collections.Generic;
using System.Reflection;
using DeezSpoTag.Web.Services.AutoTag;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class DiscogsMatcherVariantGuardTests
{
    private static readonly MethodInfo MatchTracksMethod =
        typeof(DiscogsMatcher).GetMethod(
            "MatchTracks",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DiscogsMatcher.MatchTracks not found.");

    [Theory]
    [InlineData("Spotlight", "Spotlight (Instrumental)")]
    [InlineData("Spotlight", "Spotlight - Instrumental")]
    [InlineData("Essence", "Essence (Acapella)")]
    [InlineData("Essence", "Essence (Dub)")]
    [InlineData("Essence", "Essence (Radio Edit)")]
    [InlineData("Essence", "Essence (Extended Mix)")]
    public void IsVariantCompatible_RejectsDiscogsCandidateThatAddsUnrequestedVariant(
        string sourceTitle,
        string discogsTitle)
    {
        var compatible = DiscogsMatcher.IsVariantCompatible(
            BuildInfo(sourceTitle),
            BuildTrack(discogsTitle));

        Assert.False(compatible);
    }

    [Theory]
    [InlineData("Spotlight (Instrumental)", "Spotlight (Instrumental)")]
    [InlineData("Spotlight - Instrumental", "Spotlight (Instrumental Version)")]
    [InlineData("Essence (Radio Edit)", "Essence - Radio Version")]
    [InlineData("Essence (Extended Mix)", "Essence - Extended")]
    [InlineData("Clean Up", "Clean Up")]
    public void IsVariantCompatible_AllowsSameVariantOrNormalTitle(
        string sourceTitle,
        string discogsTitle)
    {
        var compatible = DiscogsMatcher.IsVariantCompatible(
            BuildInfo(sourceTitle),
            BuildTrack(discogsTitle));

        Assert.True(compatible);
    }

    [Theory]
    [InlineData("Spotlight (Instrumental)", "Spotlight")]
    [InlineData("Essence (Clean)", "Essence (Dirty)")]
    [InlineData("Essence (Acoustic)", "Essence (Live)")]
    public void IsVariantCompatible_RejectsDiscogsCandidateWithMissingOrConflictingVariant(
        string sourceTitle,
        string discogsTitle)
    {
        var compatible = DiscogsMatcher.IsVariantCompatible(
            BuildInfo(sourceTitle),
            BuildTrack(discogsTitle));

        Assert.False(compatible);
    }

    [Fact]
    public void MatchTracks_DoesNotReturnNearMissAlternativeTitleFromSameArtist()
    {
        var match = InvokeMatchTracks(
            BuildInfo("Hold Me Close"),
            new List<DiscogsTrackInfo>
            {
                BuildTrack("Hold Me Closer")
            });

        Assert.Null(match);
    }

    [Fact]
    public void MatchTracks_DoesNotReturnDiscogsInstrumentalCandidateForNormalSourceTitle()
    {
        var match = InvokeMatchTracks(
            BuildInfo("Spotlight"),
            new List<DiscogsTrackInfo>
            {
                BuildTrack("Spotlight (Instrumental)")
            });

        Assert.Null(match);
    }

    [Fact]
    public void MatchTracks_ReturnsDiscogsCandidateWhenVariantIsCompatible()
    {
        var match = InvokeMatchTracks(
            BuildInfo("Spotlight"),
            new List<DiscogsTrackInfo>
            {
                BuildTrack("Spotlight")
            });

        Assert.NotNull(match);
    }

    private static AutoTagAudioInfo BuildInfo(string title)
        => new()
        {
            Title = title,
            Artist = "Gucci Mane",
            Artists = new List<string> { "Gucci Mane" },
            DurationSeconds = 240
        };

    private static DiscogsTrackInfo BuildTrack(string title)
        => new()
        {
            Title = title,
            Artists = new List<string> { "Gucci Mane" },
            Duration = TimeSpan.FromSeconds(240)
        };

    private static object? InvokeMatchTracks(AutoTagAudioInfo info, List<DiscogsTrackInfo> tracks)
        => MatchTracksMethod.Invoke(
            null,
            new object?[]
            {
                info,
                tracks,
                new AutoTagMatchingConfig
                {
                    Strictness = 0.7,
                    MatchDuration = true,
                    MaxDurationDifferenceSeconds = 4
                },
                false
            });
}
