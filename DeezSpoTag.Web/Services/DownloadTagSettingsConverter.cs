using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class DownloadTagSettingsConverter
{
    public TagSettings ToTagSettings(UnifiedTagConfig config, TechnicalTagSettings? technical)
    {
        var embedLyrics = technical?.EmbedLyrics ?? true;
        var settings = new TagSettings
        {
            Title = UsesDownload(config.Title),
            Artist = UsesDownload(config.Artist),
            Artists = UsesDownload(config.Artists),
            Album = UsesDownload(config.Album),
            AlbumArtist = UsesDownload(config.AlbumArtist),
            Cover = UsesDownload(config.Cover),
            TrackNumber = UsesDownload(config.TrackNumber),
            TrackTotal = UsesDownload(config.TrackTotal),
            DiscNumber = UsesDownload(config.DiscNumber),
            DiscTotal = UsesDownload(config.DiscTotal),
            Genre = UsesDownload(config.Genre),
            Year = UsesDownload(config.Year),
            Date = UsesDownload(config.Date),
            Isrc = UsesDownload(config.Isrc),
            Barcode = UsesDownload(config.Barcode),
            RecordingId = UsesDownload(config.RecordingId),
            ArtistId = UsesDownload(config.ArtistId),
            AlbumArtistId = UsesDownload(config.AlbumArtistId),
            ReleaseGroupId = UsesDownload(config.ReleaseGroupId),
            AlbumId = UsesDownload(config.AlbumId),
            ReleaseStatus = UsesDownload(config.ReleaseStatus),
            ReleaseType = UsesDownload(config.ReleaseType),
            ReleaseCountry = UsesDownload(config.ReleaseCountry),
            Media = UsesDownload(config.Media),
            CatalogNumber = UsesDownload(config.CatalogNumber),
            Bpm = UsesDownload(config.Bpm),
            Key = UsesDownload(config.Key),
            Length = UsesDownload(config.Duration),
            ReplayGain = UsesDownload(config.ReplayGain),
            Style = UsesDownload(config.Style),
            Mood = UsesDownload(config.Mood),
            Activity = UsesDownload(config.Activity),
            Danceability = UsesDownload(config.Danceability),
            Energy = UsesDownload(config.Energy),
            Valence = UsesDownload(config.Valence),
            Acousticness = UsesDownload(config.Acousticness),
            Instrumentalness = UsesDownload(config.Instrumentalness),
            Speechiness = UsesDownload(config.Speechiness),
            Loudness = UsesDownload(config.Loudness),
            Tempo = UsesDownload(config.Tempo),
            TimeSignature = UsesDownload(config.TimeSignature),
            Liveness = UsesDownload(config.Liveness),
            Label = UsesDownload(config.Label),
            Copyright = UsesDownload(config.Copyright),
            Lyrics = embedLyrics && UsesDownload(config.UnsyncedLyrics),
            SyncedLyrics = embedLyrics && UsesDownload(config.SyncedLyrics),
            TtmlLyrics = embedLyrics && UsesDownload(config.TtmlLyrics),
            Composer = UsesDownload(config.Composer),
            Lyricist = UsesDownload(config.Lyricist),
            InvolvedPeople = UsesDownload(config.InvolvedPeople),
            Publisher = UsesDownload(config.Publisher),
            Description = UsesDownload(config.Description),
            Remixer = UsesDownload(config.Remixer),
            Version = UsesDownload(config.Version),
            Language = UsesDownload(config.Language),
            OtherTags = UsesDownload(config.OtherTags),
            MetaTags = UsesDownload(config.MetaTags),
            ReleaseDate = UsesDownload(config.ReleaseDate),
            PublishDate = UsesDownload(config.PublishDate),
            Source = UsesDownload(config.Source),
            Url = UsesDownload(config.Url),
            TrackId = UsesDownload(config.TrackId),
            ReleaseId = UsesDownload(config.ReleaseId),
            Explicit = UsesDownload(config.Explicit),
            Rating = UsesDownload(config.Rating)
        };

        if (technical != null)
        {
            settings.SavePlaylistAsCompilation = technical.SavePlaylistAsCompilation;
            settings.UseNullSeparator = technical.UseNullSeparator;
            settings.SaveID3v1 = technical.SaveID3v1;
            settings.MultiArtistSeparator = technical.MultiArtistSeparator;
            settings.SingleAlbumArtist = technical.SingleAlbumArtist;
            settings.CoverDescriptionUTF8 = technical.CoverDescriptionUTF8;
        }

        return settings;
    }

    private static bool UsesDownload(TagSource source)
    {
        return source == TagSource.DownloadSource || source == TagSource.Both;
    }
}
