using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Services.Download.Shared;

public static class TechnicalLyricsSettingsApplier
{
    public static void Apply(DeezSpoTagSettings settings, TechnicalTagSettings? technical)
    {
        if (technical == null)
        {
            return;
        }

        settings.Tags ??= new TagSettings();

        settings.Tags.SavePlaylistAsCompilation = technical.SavePlaylistAsCompilation;
        settings.Tags.UseNullSeparator = technical.UseNullSeparator;
        settings.Tags.SaveID3v1 = technical.SaveID3v1;
        settings.Tags.SingleAlbumArtist = technical.SingleAlbumArtist;
        settings.Tags.CoverDescriptionUTF8 = technical.CoverDescriptionUTF8;
        settings.AlbumVariousArtists = technical.AlbumVariousArtists;
        settings.RemoveDuplicateArtists = technical.RemoveDuplicateArtists;
        settings.RemoveAlbumVersion = technical.RemoveAlbumVersion;

        if (!string.IsNullOrWhiteSpace(technical.MultiArtistSeparator))
        {
            settings.Tags.MultiArtistSeparator = technical.MultiArtistSeparator;
        }

        if (!string.IsNullOrWhiteSpace(technical.DateFormat))
        {
            settings.DateFormat = technical.DateFormat;
        }

        if (!string.IsNullOrWhiteSpace(technical.FeaturedToTitle))
        {
            settings.FeaturedToTitle = technical.FeaturedToTitle;
        }

        if (!string.IsNullOrWhiteSpace(technical.TitleCasing))
        {
            settings.TitleCasing = technical.TitleCasing;
        }

        if (!string.IsNullOrWhiteSpace(technical.ArtistCasing))
        {
            settings.ArtistCasing = technical.ArtistCasing;
        }

        settings.SyncedLyrics = technical.SyncedLyrics;
        settings.SaveLyrics = technical.SaveLyrics;
        settings.LyricsFallbackEnabled = technical.LyricsFallbackEnabled;
        settings.ArtworkFallbackEnabled = technical.ArtworkFallbackEnabled;
        settings.ArtistArtworkFallbackEnabled = technical.ArtistArtworkFallbackEnabled;

        if (!technical.EmbedLyrics)
        {
            settings.Tags.Lyrics = false;
            settings.Tags.SyncedLyrics = false;
        }

        if (!string.IsNullOrWhiteSpace(technical.LrcType))
        {
            settings.LrcType = technical.LrcType;
        }

        if (!string.IsNullOrWhiteSpace(technical.LrcFormat))
        {
            settings.LrcFormat = technical.LrcFormat;
        }

        if (!string.IsNullOrWhiteSpace(technical.LyricsFallbackOrder))
        {
            settings.LyricsFallbackOrder = technical.LyricsFallbackOrder;
        }

        if (!string.IsNullOrWhiteSpace(technical.ArtworkFallbackOrder))
        {
            settings.ArtworkFallbackOrder = technical.ArtworkFallbackOrder;
        }

        if (!string.IsNullOrWhiteSpace(technical.ArtistArtworkFallbackOrder))
        {
            settings.ArtistArtworkFallbackOrder = technical.ArtistArtworkFallbackOrder;
        }
    }
}
