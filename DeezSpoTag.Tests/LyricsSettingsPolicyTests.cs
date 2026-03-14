using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
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
}
