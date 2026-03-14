using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services;

public sealed record SpotiFlacPlaylistPayload(
    [property: JsonPropertyName("playlist_info")] SpotiFlacPlaylistInfo PlaylistInfo,
    [property: JsonPropertyName("track_list")] List<SpotiFlacAlbumTrackMetadata> TrackList);

public sealed record SpotiFlacPlaylistInfo(
    [property: JsonPropertyName("tracks")] SpotiFlacPlaylistTracks Tracks,
    [property: JsonPropertyName("followers")] SpotiFlacPlaylistFollowers Followers,
    [property: JsonPropertyName("owner")] SpotiFlacPlaylistOwner Owner,
    [property: JsonPropertyName("cover")] string? Cover,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("batch")] string? Batch);

public sealed record SpotiFlacPlaylistTracks(
    [property: JsonPropertyName("total")] int Total);

public sealed record SpotiFlacPlaylistFollowers(
    [property: JsonPropertyName("total")] int Total);

public sealed record SpotiFlacPlaylistOwner(
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("images")] string? Images);

public sealed record SpotiFlacArtistSimple(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("external_urls")] string ExternalUrl);

public sealed record SpotiFlacAlbumTrackMetadata(
    [property: JsonPropertyName("spotify_id")] string? SpotifyId,
    [property: JsonPropertyName("artists")] string? Artists,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("album_name")] string? AlbumName,
    [property: JsonPropertyName("album_artist")] string? AlbumArtist,
    [property: JsonPropertyName("duration_ms")] int DurationMs,
    [property: JsonPropertyName("images")] string? Images,
    [property: JsonPropertyName("release_date")] string? ReleaseDate,
    [property: JsonPropertyName("track_number")] int TrackNumber,
    [property: JsonPropertyName("total_tracks")] int TotalTracks,
    [property: JsonPropertyName("disc_number")] int DiscNumber,
    [property: JsonPropertyName("total_discs")] int TotalDiscs,
    [property: JsonPropertyName("external_urls")] string? ExternalUrl,
    [property: JsonPropertyName("isrc")] string? Isrc,
    [property: JsonPropertyName("album_type")] string? AlbumType,
    [property: JsonPropertyName("album_id")] string? AlbumId,
    [property: JsonPropertyName("album_url")] string? AlbumUrl,
    [property: JsonPropertyName("artist_id")] string? ArtistId,
    [property: JsonPropertyName("artist_url")] string? ArtistUrl,
    [property: JsonPropertyName("artists_data")] List<SpotiFlacArtistSimple>? ArtistsData,
    [property: JsonPropertyName("preview_url")] string? PreviewUrl);
