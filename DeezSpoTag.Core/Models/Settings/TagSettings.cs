using System.Text.Json.Serialization;

namespace DeezSpoTag.Core.Models.Settings;

/// <summary>
/// Tag settings for audio files (complete port from deezspotag Tags interface)
/// </summary>
public class TagSettings
{
    // Basic metadata tags
    [JsonPropertyName("title")]
    public bool Title { get; set; } = true;

    [JsonPropertyName("artist")]
    public bool Artist { get; set; } = true;

    [JsonPropertyName("artists")]
    public bool Artists { get; set; } = true;

    [JsonPropertyName("album")]
    public bool Album { get; set; } = true;

    [JsonPropertyName("cover")]
    public bool Cover { get; set; } = true;

    // Track/Disc information
    [JsonPropertyName("trackNumber")]
    public bool TrackNumber { get; set; } = true;

    [JsonPropertyName("trackTotal")]
    public bool TrackTotal { get; set; } = false;

    [JsonPropertyName("discNumber")]
    public bool DiscNumber { get; set; } = true;

    [JsonPropertyName("discTotal")]
    public bool DiscTotal { get; set; } = false;

    // Artist information
    [JsonPropertyName("albumArtist")]
    public bool AlbumArtist { get; set; } = true;

    // Genre and classification
    [JsonPropertyName("genre")]
    public bool Genre { get; set; } = true;

    // Date information
    [JsonPropertyName("year")]
    public bool Year { get; set; } = true;

    [JsonPropertyName("date")]
    public bool Date { get; set; } = true;

    // Content flags
    [JsonPropertyName("explicit")]
    public bool Explicit { get; set; } = false;

    // Identifiers
    [JsonPropertyName("isrc")]
    public bool Isrc { get; set; } = true;

    [JsonPropertyName("barcode")]
    public bool Barcode { get; set; } = true;

    [JsonPropertyName("recordingId")]
    public bool RecordingId { get; set; } = false;

    [JsonPropertyName("artistId")]
    public bool ArtistId { get; set; } = false;

    [JsonPropertyName("albumArtistId")]
    public bool AlbumArtistId { get; set; } = false;

    [JsonPropertyName("releaseGroupId")]
    public bool ReleaseGroupId { get; set; } = false;

    [JsonPropertyName("albumId")]
    public bool AlbumId { get; set; } = false;

    [JsonPropertyName("releaseStatus")]
    public bool ReleaseStatus { get; set; } = false;

    [JsonPropertyName("releaseType")]
    public bool ReleaseType { get; set; } = false;

    [JsonPropertyName("releaseCountry")]
    public bool ReleaseCountry { get; set; } = false;

    [JsonPropertyName("media")]
    public bool Media { get; set; } = false;

    [JsonPropertyName("catalogNumber")]
    public bool CatalogNumber { get; set; } = false;

    // Technical information
    [JsonPropertyName("length")]
    public bool Length { get; set; } = true;

    [JsonPropertyName("bpm")]
    public bool Bpm { get; set; } = true;

    [JsonPropertyName("key")]
    public bool Key { get; set; } = false;

    [JsonPropertyName("danceability")]
    public bool Danceability { get; set; } = false;

    [JsonPropertyName("energy")]
    public bool Energy { get; set; } = false;

    [JsonPropertyName("valence")]
    public bool Valence { get; set; } = false;

    [JsonPropertyName("acousticness")]
    public bool Acousticness { get; set; } = false;

    [JsonPropertyName("instrumentalness")]
    public bool Instrumentalness { get; set; } = false;

    [JsonPropertyName("speechiness")]
    public bool Speechiness { get; set; } = false;

    [JsonPropertyName("loudness")]
    public bool Loudness { get; set; } = false;

    [JsonPropertyName("tempo")]
    public bool Tempo { get; set; } = false;

    [JsonPropertyName("timeSignature")]
    public bool TimeSignature { get; set; } = false;

    [JsonPropertyName("liveness")]
    public bool Liveness { get; set; } = false;

    [JsonPropertyName("replayGain")]
    public bool ReplayGain { get; set; } = false;

    [JsonPropertyName("style")]
    public bool Style { get; set; } = false;

    [JsonPropertyName("mood")]
    public bool Mood { get; set; } = false;

    [JsonPropertyName("activity")]
    public bool Activity { get; set; } = false;

    // Label and copyright
    [JsonPropertyName("label")]
    public bool Label { get; set; } = true;

    [JsonPropertyName("copyright")]
    public bool Copyright { get; set; } = false;

    // Lyrics
    [JsonPropertyName("lyrics")]
    public bool Lyrics { get; set; } = false;

    [JsonPropertyName("tagSyncedLyrics")]
    public bool SyncedLyrics { get; set; } = false;

    [JsonPropertyName("ttmlLyrics")]
    public bool TtmlLyrics { get; set; } = false;

    // Credits
    [JsonPropertyName("composer")]
    public bool Composer { get; set; } = false;

    [JsonPropertyName("lyricist")]
    public bool Lyricist { get; set; } = false;

    [JsonPropertyName("involvedPeople")]
    public bool InvolvedPeople { get; set; } = false;

    [JsonPropertyName("publisher")]
    public bool Publisher { get; set; } = false;

    [JsonPropertyName("description")]
    public bool Description { get; set; } = false;

    [JsonPropertyName("remixer")]
    public bool Remixer { get; set; } = false;

    [JsonPropertyName("version")]
    public bool Version { get; set; } = false;

    [JsonPropertyName("language")]
    public bool Language { get; set; } = false;

    [JsonPropertyName("otherTags")]
    public bool OtherTags { get; set; } = false;

    [JsonPropertyName("metaTags")]
    public bool MetaTags { get; set; } = false;

    [JsonPropertyName("releaseDate")]
    public bool ReleaseDate { get; set; } = false;

    [JsonPropertyName("publishDate")]
    public bool PublishDate { get; set; } = false;

    // Source and rating
    [JsonPropertyName("source")]
    public bool Source { get; set; } = false;

    [JsonPropertyName("url")]
    public bool Url { get; set; } = false;

    [JsonPropertyName("trackId")]
    public bool TrackId { get; set; } = false;

    [JsonPropertyName("releaseId")]
    public bool ReleaseId { get; set; } = false;

    [JsonPropertyName("rating")]
    public bool Rating { get; set; } = false;

    // Playlist settings
    [JsonPropertyName("savePlaylistAsCompilation")]
    public bool SavePlaylistAsCompilation { get; set; } = false;

    // Technical tag settings
    [JsonPropertyName("useNullSeparator")]
    public bool UseNullSeparator { get; set; } = false;

    [JsonPropertyName("saveID3v1")]
    public bool SaveID3v1 { get; set; } = true;

    [JsonPropertyName("multiArtistSeparator")]
    public string MultiArtistSeparator { get; set; } = "default";

    [JsonPropertyName("singleAlbumArtist")]
    public bool SingleAlbumArtist { get; set; } = true;

    [JsonPropertyName("coverDescriptionUTF8")]
    public bool CoverDescriptionUTF8 { get; set; } = true;
}
