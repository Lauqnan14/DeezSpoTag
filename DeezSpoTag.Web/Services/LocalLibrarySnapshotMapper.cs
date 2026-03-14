using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

internal static class LocalLibrarySnapshotMapper
{
    public static LocalLibraryIngestPayload BuildIngestPayload(LibraryConfigStore.LocalLibrarySnapshot snapshot)
    {
        var artists = snapshot.Artists
            .Select(artist => new LocalArtistScanDto(artist.Name, artist.ImagePath))
            .ToList();
        var albums = snapshot.Albums
            .Select(album => new LocalAlbumScanDto(
                snapshot.Artists.FirstOrDefault(artist => artist.Id == album.ArtistId)?.Name ?? string.Empty,
                album.Title,
                album.PreferredCoverPath,
                album.LocalFolders,
                album.HasAnimatedArtwork))
            .Where(album => !string.IsNullOrWhiteSpace(album.ArtistName))
            .ToList();
        var tracks = snapshot.Tracks
            .Select(track =>
            {
                var album = snapshot.Albums.FirstOrDefault(item => item.Id == track.AlbumId);
                var artistName = album is null
                    ? null
                    : snapshot.Artists.FirstOrDefault(item => item.Id == album.ArtistId)?.Name;
                return new { album, artistName, track.Scan };
            })
            .Where(item => item.album is not null && !string.IsNullOrWhiteSpace(item.artistName))
            .Select(item => item.Scan with
            {
                ArtistName = item.artistName!,
                AlbumTitle = item.album!.Title
            })
            .ToList();

        return new LocalLibraryIngestPayload(artists, albums, tracks);
    }

    internal sealed record LocalLibraryIngestPayload(
        List<LocalArtistScanDto> Artists,
        List<LocalAlbumScanDto> Albums,
        List<LocalTrackScanDto> Tracks);
}
