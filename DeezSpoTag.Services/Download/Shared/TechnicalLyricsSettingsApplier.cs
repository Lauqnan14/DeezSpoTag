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

        settings.SyncedLyrics = technical.SyncedLyrics;
        settings.SaveLyrics = technical.SaveLyrics;
        settings.LyricsFallbackEnabled = technical.LyricsFallbackEnabled;
        settings.ArtworkFallbackEnabled = technical.ArtworkFallbackEnabled;
        settings.ArtistArtworkFallbackEnabled = technical.ArtistArtworkFallbackEnabled;
        settings.Tags ??= new TagSettings();
        settings.Tags.SingleAlbumArtist = technical.SingleAlbumArtist;

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
