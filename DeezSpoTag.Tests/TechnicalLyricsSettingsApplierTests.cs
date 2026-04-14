using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared;
using Xunit;

namespace DeezSpoTag.Tests;

public sealed class TechnicalLyricsSettingsApplierTests
{
    [Fact]
    public void Apply_OverlaysTechnicalFields()
    {
        var settings = new DeezSpoTagSettings
        {
            Tags = new TagSettings
            {
                SavePlaylistAsCompilation = false,
                UseNullSeparator = false,
                SaveID3v1 = false,
                MultiArtistSeparator = "default",
                SingleAlbumArtist = true,
                CoverDescriptionUTF8 = false,
                Lyrics = true,
                SyncedLyrics = true
            },
            DateFormat = "Y-M-D",
            FeaturedToTitle = "0",
            TitleCasing = "nothing",
            ArtistCasing = "nothing",
            AlbumVariousArtists = false,
            RemoveDuplicateArtists = false,
            RemoveAlbumVersion = false,
            SaveLyrics = false,
            SyncedLyrics = false,
            LrcType = "lyrics",
            LrcFormat = "lrc",
            LyricsFallbackEnabled = false,
            ArtworkFallbackEnabled = false,
            ArtistArtworkFallbackEnabled = false,
            LyricsFallbackOrder = "apple,deezer",
            ArtworkFallbackOrder = "apple,deezer",
            ArtistArtworkFallbackOrder = "apple,deezer"
        };

        var technical = new TechnicalTagSettings
        {
            SavePlaylistAsCompilation = true,
            UseNullSeparator = true,
            SaveID3v1 = true,
            MultiArtistSeparator = " / ",
            SingleAlbumArtist = false,
            CoverDescriptionUTF8 = true,
            DateFormat = "D-M-Y",
            FeaturedToTitle = "2",
            TitleCasing = "capitalize",
            ArtistCasing = "uppercase",
            AlbumVariousArtists = true,
            RemoveDuplicateArtists = true,
            RemoveAlbumVersion = true,
            SaveLyrics = true,
            SyncedLyrics = true,
            EmbedLyrics = false,
            LrcType = "lyrics,syllable-lyrics,unsynced-lyrics",
            LrcFormat = "both",
            LyricsFallbackEnabled = true,
            ArtworkFallbackEnabled = true,
            ArtistArtworkFallbackEnabled = true,
            LyricsFallbackOrder = "apple,lrclib,musixmatch,deezer",
            ArtworkFallbackOrder = "apple,deezer,shazam",
            ArtistArtworkFallbackOrder = "deezer,apple,shazam"
        };

        TechnicalLyricsSettingsApplier.Apply(settings, technical);

        Assert.True(settings.Tags.SavePlaylistAsCompilation);
        Assert.True(settings.Tags.UseNullSeparator);
        Assert.True(settings.Tags.SaveID3v1);
        Assert.Equal(" / ", settings.Tags.MultiArtistSeparator);
        Assert.False(settings.Tags.SingleAlbumArtist);
        Assert.True(settings.Tags.CoverDescriptionUTF8);
        Assert.Equal("D-M-Y", settings.DateFormat);
        Assert.Equal("2", settings.FeaturedToTitle);
        Assert.Equal("capitalize", settings.TitleCasing);
        Assert.Equal("uppercase", settings.ArtistCasing);
        Assert.True(settings.AlbumVariousArtists);
        Assert.True(settings.RemoveDuplicateArtists);
        Assert.True(settings.RemoveAlbumVersion);
        Assert.True(settings.SaveLyrics);
        Assert.True(settings.SyncedLyrics);
        Assert.True(settings.LyricsFallbackEnabled);
        Assert.True(settings.ArtworkFallbackEnabled);
        Assert.True(settings.ArtistArtworkFallbackEnabled);
        Assert.Equal("lyrics,syllable-lyrics,unsynced-lyrics", settings.LrcType);
        Assert.Equal("both", settings.LrcFormat);
        Assert.Equal("apple,lrclib,musixmatch,deezer", settings.LyricsFallbackOrder);
        Assert.Equal("apple,deezer,shazam", settings.ArtworkFallbackOrder);
        Assert.Equal("deezer,apple,shazam", settings.ArtistArtworkFallbackOrder);
        Assert.False(settings.Tags.Lyrics);
        Assert.False(settings.Tags.SyncedLyrics);
    }
}
