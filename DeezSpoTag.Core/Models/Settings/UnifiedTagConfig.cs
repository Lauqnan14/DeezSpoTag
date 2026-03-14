namespace DeezSpoTag.Core.Models.Settings;

public class UnifiedTagConfig
{
    public TagSource Title { get; set; } = TagSource.DownloadSource;
    public TagSource Artist { get; set; } = TagSource.DownloadSource;
    public TagSource Artists { get; set; } = TagSource.DownloadSource;
    public TagSource Album { get; set; } = TagSource.DownloadSource;
    public TagSource AlbumArtist { get; set; } = TagSource.DownloadSource;
    public TagSource Cover { get; set; } = TagSource.DownloadSource;

    public TagSource TrackNumber { get; set; } = TagSource.DownloadSource;
    public TagSource TrackTotal { get; set; } = TagSource.DownloadSource;
    public TagSource DiscNumber { get; set; } = TagSource.DownloadSource;
    public TagSource DiscTotal { get; set; } = TagSource.DownloadSource;

    public TagSource Genre { get; set; } = TagSource.DownloadSource;
    public TagSource Year { get; set; } = TagSource.DownloadSource;
    public TagSource Date { get; set; } = TagSource.DownloadSource;

    public TagSource Isrc { get; set; } = TagSource.DownloadSource;
    public TagSource Barcode { get; set; } = TagSource.DownloadSource;

    public TagSource Bpm { get; set; } = TagSource.DownloadSource;
    public TagSource Duration { get; set; } = TagSource.DownloadSource;
    public TagSource ReplayGain { get; set; } = TagSource.DownloadSource;

    public TagSource Danceability { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Energy { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Valence { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Acousticness { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Instrumentalness { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Speechiness { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Loudness { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Tempo { get; set; } = TagSource.AutoTagPlatform;
    public TagSource TimeSignature { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Liveness { get; set; } = TagSource.AutoTagPlatform;

    public TagSource Label { get; set; } = TagSource.DownloadSource;
    public TagSource Copyright { get; set; } = TagSource.DownloadSource;

    public TagSource UnsyncedLyrics { get; set; } = TagSource.DownloadSource;
    public TagSource SyncedLyrics { get; set; } = TagSource.DownloadSource;

    public TagSource Composer { get; set; } = TagSource.DownloadSource;
    public TagSource InvolvedPeople { get; set; } = TagSource.DownloadSource;

    public TagSource Source { get; set; } = TagSource.DownloadSource;
    public TagSource Explicit { get; set; } = TagSource.DownloadSource;
    public TagSource Rating { get; set; } = TagSource.DownloadSource;

    public TagSource Style { get; set; } = TagSource.AutoTagPlatform;
    public TagSource ReleaseDate { get; set; } = TagSource.AutoTagPlatform;
    public TagSource PublishDate { get; set; } = TagSource.AutoTagPlatform;
    public TagSource ReleaseId { get; set; } = TagSource.AutoTagPlatform;
    public TagSource TrackId { get; set; } = TagSource.AutoTagPlatform;
    public TagSource CatalogNumber { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Key { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Remixer { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Version { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Mood { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Url { get; set; } = TagSource.AutoTagPlatform;
    public TagSource OtherTags { get; set; } = TagSource.AutoTagPlatform;
    public TagSource MetaTags { get; set; } = TagSource.AutoTagPlatform;
}
