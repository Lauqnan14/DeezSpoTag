using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Web.Services;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TaggingProfileSettingsMapperTests
{
    [Fact]
    public void ApplyProfileToSettings_DisablesEmbeddedLyricsTags_WhenEmbedLyricsIsOff()
    {
        var settings = new DeezSpoTagSettings
        {
            Tags = new TagSettings
            {
                Lyrics = true,
                SyncedLyrics = true
            }
        };
        var profile = new TaggingProfile
        {
            Technical = new TechnicalTagSettings
            {
                EmbedLyrics = false,
                SaveLyrics = true,
                SyncedLyrics = true
            },
            TagConfig = new UnifiedTagConfig
            {
                UnsyncedLyrics = TagSource.DownloadSource,
                SyncedLyrics = TagSource.DownloadSource
            }
        };

        TaggingProfileSettingsMapper.ApplyProfileToSettings(settings, profile);

        Assert.False(settings.Tags.Lyrics);
        Assert.False(settings.Tags.SyncedLyrics);
    }

    [Fact]
    public void ApplyProfileToSettings_RespectsLyricsTogglesAndTagSources_WhenEmbedLyricsIsOn()
    {
        var settings = new DeezSpoTagSettings
        {
            Tags = new TagSettings()
        };
        var profile = new TaggingProfile
        {
            Technical = new TechnicalTagSettings
            {
                EmbedLyrics = true,
                SaveLyrics = false,
                SyncedLyrics = true
            },
            TagConfig = new UnifiedTagConfig
            {
                UnsyncedLyrics = TagSource.Both,
                SyncedLyrics = TagSource.None
            }
        };

        TaggingProfileSettingsMapper.ApplyProfileToSettings(settings, profile);

        Assert.False(settings.Tags.Lyrics);
        Assert.False(settings.Tags.SyncedLyrics);
    }
}
