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
    public TagSource ReleaseType { get; set; } = TagSource.DownloadSource;
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
    public TagSource Melodicness { get; set; } = TagSource.AutoTagPlatform;
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
    public TagSource TtmlLyrics { get; set; } = TagSource.DownloadSource;

    public TagSource Composer { get; set; } = TagSource.DownloadSource;
    public TagSource Lyricist { get; set; } = TagSource.DownloadSource;
    public TagSource InvolvedPeople { get; set; } = TagSource.DownloadSource;
    public TagSource Publisher { get; set; } = TagSource.DownloadSource;
    public TagSource Description { get; set; } = TagSource.DownloadSource;

    // Distinct credit roles. Conservative None defaults: no legacy selection is
    // invented by default — the profile canonicalizer migrates unversioned
    // profiles that actually enabled the legacy combined-credit field.
    public TagSource Songwriter { get; set; } = TagSource.None;
    public TagSource Producer { get; set; } = TagSource.None;
    public TagSource ExecutiveProducer { get; set; } = TagSource.None;
    public TagSource CoProducer { get; set; } = TagSource.None;
    public TagSource AdditionalProducer { get; set; } = TagSource.None;
    public TagSource VocalProducer { get; set; } = TagSource.None;
    public TagSource Engineer { get; set; } = TagSource.None;
    public TagSource RecordingEngineer { get; set; } = TagSource.None;
    public TagSource MixingEngineer { get; set; } = TagSource.None;
    public TagSource MasteringEngineer { get; set; } = TagSource.None;
    public TagSource AssistantEngineer { get; set; } = TagSource.None;
    public TagSource Arranger { get; set; } = TagSource.None;
    public TagSource Orchestrator { get; set; } = TagSource.None;
    public TagSource Conductor { get; set; } = TagSource.None;
    public TagSource Performer { get; set; } = TagSource.None;
    public TagSource DjMixer { get; set; } = TagSource.None;
    public TagSource Director { get; set; } = TagSource.None;

    public TagSource Source { get; set; } = TagSource.DownloadSource;
    public TagSource Explicit { get; set; } = TagSource.DownloadSource;
    public TagSource Rating { get; set; } = TagSource.DownloadSource;

    public TagSource Style { get; set; } = TagSource.AutoTagPlatform;
    public TagSource ReleaseDate { get; set; } = TagSource.AutoTagPlatform;
    public TagSource PublishDate { get; set; } = TagSource.AutoTagPlatform;
    public TagSource ReleaseId { get; set; } = TagSource.AutoTagPlatform;
    public TagSource TrackId { get; set; } = TagSource.AutoTagPlatform;
    public TagSource RecordingId { get; set; } = TagSource.AutoTagPlatform;
    public TagSource ArtistId { get; set; } = TagSource.AutoTagPlatform;
    public TagSource AlbumArtistId { get; set; } = TagSource.AutoTagPlatform;
    public TagSource ReleaseGroupId { get; set; } = TagSource.AutoTagPlatform;
    public TagSource AlbumId { get; set; } = TagSource.AutoTagPlatform;
    public TagSource ReleaseStatus { get; set; } = TagSource.AutoTagPlatform;
    public TagSource ReleaseCountry { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Media { get; set; } = TagSource.AutoTagPlatform;
    public TagSource CatalogNumber { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Key { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Remixer { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Version { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Mood { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Activity { get; set; } = TagSource.AutoTagPlatform;
    public TagSource Language { get; set; } = TagSource.AutoTagPlatform;
    public TagSource ArtistCountry { get; set; } = TagSource.None;
    public TagSource ArtistCity { get; set; } = TagSource.None;
    public TagSource ArtistRegion { get; set; } = TagSource.None;
    public TagSource ArtistLanguage { get; set; } = TagSource.None;
    public TagSource Url { get; set; } = TagSource.AutoTagPlatform;
    public TagSource OtherTags { get; set; } = TagSource.AutoTagPlatform;
    public TagSource MetaTags { get; set; } = TagSource.AutoTagPlatform;
}
