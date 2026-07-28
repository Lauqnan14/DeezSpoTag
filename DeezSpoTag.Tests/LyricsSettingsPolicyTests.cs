using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using System;
using System.Reflection;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class LyricsSettingsPolicyTests
{
    [Fact]
    public void CanFetchLyrics_ReturnsFalse_WhenAllGatesDisabled()
    {
        var settings = new DeezSpoTagSettings
        {
            SaveLyrics = false,
            SyncedLyrics = false,
            Tags = new TagSettings
            {
                Lyrics = false,
                SyncedLyrics = false
            }
        };

        var result = LyricsSettingsPolicy.CanFetchLyrics(settings);

        Assert.False(result);
    }

    [Fact]
    public void CanFetchLyrics_ReturnsTrue_WhenSaveLyricsEnabled()
    {
        var settings = new DeezSpoTagSettings
        {
            SaveLyrics = true,
            SyncedLyrics = false,
            LrcType = "unsynced-lyrics",
            Tags = new TagSettings()
        };

        var result = LyricsSettingsPolicy.CanFetchLyrics(settings);

        Assert.True(result);
    }

    [Fact]
    public void CanFetchLyrics_ReturnsFalse_WhenOnlyTagLyricsFlagsAreEnabled()
    {
        var settings = new DeezSpoTagSettings
        {
            SaveLyrics = false,
            SyncedLyrics = false,
            LrcType = "lyrics,syllable-lyrics,unsynced-lyrics",
            Tags = new TagSettings
            {
                Lyrics = true,
                SyncedLyrics = true
            }
        };

        var result = LyricsSettingsPolicy.CanFetchLyrics(settings);

        Assert.False(result);
    }

    [Fact]
    public void CanFetchLyrics_AcceptsLegacyTypeAliases()
    {
        var settings = new DeezSpoTagSettings
        {
            SaveLyrics = true,
            SyncedLyrics = false,
            LrcType = "time-synced-lyrics,unsynced",
            Tags = new TagSettings()
        };

        var result = LyricsSettingsPolicy.CanFetchLyrics(settings);

        Assert.True(result);
    }

    [Fact]
    public void CanFetchLyrics_ReturnsTrue_WhenOnlyTtmlIsSelected()
    {
        var settings = new DeezSpoTagSettings
        {
            SaveLyrics = false,
            SyncedLyrics = true,
            LrcType = "ttml-lyrics",
            LrcFormat = "ttml"
        };

        Assert.True(LyricsSettingsPolicy.CanFetchLyrics(settings));
    }

    [Fact]
    public void ResolveSettings_PreservesTtmlOnlySelection()
    {
        var builderType = typeof(LyricsSettingsPolicy).Assembly.GetType(
            "DeezSpoTag.Services.Download.Shared.LyricsResolveSettingsBuilder")
            ?? throw new InvalidOperationException("LyricsResolveSettingsBuilder not found.");
        var build = builderType.GetMethod("Build", BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("LyricsResolveSettingsBuilder.Build not found.");
        var source = new DeezSpoTagSettings
        {
            SyncedLyrics = true,
            SaveLyrics = false,
            LrcType = "ttml-lyrics",
            LrcFormat = "ttml"
        };

        var result = (DeezSpoTagSettings)build.Invoke(
            null,
            [source, new TagSettings { SyncedLyrics = true }])!;

        Assert.Equal("ttml-lyrics", result.LrcType);
        Assert.Equal("ttml", result.LrcFormat);
    }
}
