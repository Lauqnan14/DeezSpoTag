using DeezSpoTag.Core.Models.Qobuz;

namespace DeezSpoTag.Services.Metadata.Qobuz;

public interface IQobuzMetadataService
{
    Task<QobuzTrack?> FindTrackByISRC(string isrc, CancellationToken ct);
    Task<QobuzAlbum?> FindAlbumByUPC(string upc, CancellationToken ct);
    Task<QobuzArtist?> FindArtistByName(string name, CancellationToken ct);
    Task<List<QobuzTrack>> SearchTracks(string query, CancellationToken ct);
    Task<List<QobuzTrack>> SearchTracksAutosuggest(string query, string? store, CancellationToken ct);
    Task<List<QobuzAlbum>> SearchAlbums(string query, CancellationToken ct);
    Task<List<QobuzArtist>> SearchArtists(string query, CancellationToken ct);
    Task<QobuzArtist?> GetArtistDiscography(int artistId, string store, CancellationToken ct);
    Task<List<QobuzAlbum>> GetArtistAlbums(int artistId, string store, CancellationToken ct);
    Task<QobuzQualityInfo?> GetTrackQuality(int trackId, CancellationToken ct);
}
