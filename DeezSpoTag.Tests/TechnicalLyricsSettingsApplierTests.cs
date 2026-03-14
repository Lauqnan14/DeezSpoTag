using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TechnicalLyricsSettingsApplierTests
{
    [Fact]
    public void Apply_OverlaysLyricsTechnicalFields()
    {
        var settings = new DeezSpoTagSettings
        {
            SaveLyrics = false,
            SyncedLyrics = false,
            LrcType = "lyrics",
            LrcFormat = "lrc",
            LyricsFallbackOrder = "apple,deezer",
            ArtworkFallbackOrder = "apple,deezer",
            ArtistArtworkFallbackOrder = "apple,deezer"
        };

        var technical = new TechnicalTagSettings
        {
            SaveLyrics = true,
            SyncedLyrics = true,
            LrcType = "lyrics,syllable-lyrics,unsynced-lyrics",
            LrcFormat = "both",
            LyricsFallbackOrder = "apple,lrclib,musixmatch,deezer",
            ArtworkFallbackOrder = "apple,deezer,shazam",
            ArtistArtworkFallbackOrder = "deezer,apple,shazam"
        };

        TechnicalLyricsSettingsApplier.Apply(settings, technical);

        Assert.True(settings.SaveLyrics);
        Assert.True(settings.SyncedLyrics);
        Assert.Equal("lyrics,syllable-lyrics,unsynced-lyrics", settings.LrcType);
        Assert.Equal("both", settings.LrcFormat);
        Assert.Equal("apple,lrclib,musixmatch,deezer", settings.LyricsFallbackOrder);
        Assert.Equal("apple,deezer,shazam", settings.ArtworkFallbackOrder);
        Assert.Equal("deezer,apple,shazam", settings.ArtistArtworkFallbackOrder);
    }
}
