using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Metadata;
using DeezSpoTag.Services.Metadata.Qobuz;

namespace DeezSpoTag.Web.Services;

public sealed class QobuzMetadataResolver : IMetadataResolver
{
    private readonly IQobuzMetadataService _qobuzService;
    private readonly ILogger<QobuzMetadataResolver> _logger;

    public QobuzMetadataResolver(
        IQobuzMetadataService qobuzService,
        ILogger<QobuzMetadataResolver> logger)
    {
        _qobuzService = qobuzService;
        _logger = logger;
    }

    public string SourceKey => "qobuz";

    public async Task ResolveTrackAsync(Track track, DeezSpoTagSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(track.ISRC))
            {
                var qobuzTrack = await _qobuzService.FindTrackByISRC(track.ISRC, cancellationToken);
                if (qobuzTrack != null)
                {
                    EnrichTrackFromQobuz(track, qobuzTrack);
                    return;
                }
            }

            var albumUpc = track.Album?.UPC ?? track.Album?.Barcode;
            if (!string.IsNullOrWhiteSpace(albumUpc))
            {
                var qobuzAlbum = await _qobuzService.FindAlbumByUPC(albumUpc, cancellationToken);
                if (qobuzAlbum != null)
                {
                    var qobuzTrack = await FindTrackInAlbumAsync(qobuzAlbum, track, cancellationToken);
                    if (qobuzTrack != null)
                    {
                        EnrichTrackFromQobuz(track, qobuzTrack);
                        return;
                    }
                }
            }

            var query = $"{track.MainArtist?.Name ?? track.ArtistString} {track.Title}".Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                return;
            }

            var searchResults = await _qobuzService.SearchTracks(query, cancellationToken);
            var bestMatch = QobuzTrackMatchingService.FindBestMatch(
                track.Title,
                track.MainArtist?.Name ?? track.ArtistString,
                track.Duration > 0 ? track.Duration : null,
                searchResults);
            if (bestMatch != null)
            {
                EnrichTrackFromQobuz(track, bestMatch);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Qobuz metadata resolver failed for track {TrackId}", track.Id);
            }
        }
    }

    private async Task<QobuzTrack?> FindTrackInAlbumAsync(QobuzAlbum album, Track track, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(track.Title))
        {
            return null;
        }

        var searchQuery = $"{track.Title} {album.Title}".Trim();
        var candidates = await _qobuzService.SearchTracks(searchQuery, ct);
        if (candidates.Count == 0)
        {
            return null;
        }

        var filtered = candidates
            .Where(item => !string.IsNullOrWhiteSpace(album.Id) &&
                           string.Equals(item.Album?.Id, album.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (track.TrackNumber > 0)
        {
            var byTrackNumber = filtered.FirstOrDefault(item => item.TrackNumber == track.TrackNumber);
            if (byTrackNumber != null)
            {
                return byTrackNumber;
            }
        }

        return QobuzTrackMatchingService.FindBestMatch(
            track.Title,
            track.MainArtist?.Name ?? track.ArtistString,
            track.Duration > 0 ? track.Duration : null,
            filtered.Count > 0 ? filtered : candidates);
    }

    private static void EnrichTrackFromQobuz(Track track, QobuzTrack qobuzTrack)
    {
        track.QobuzId = qobuzTrack.Id.ToString();
        track.QobuzAlbumId = qobuzTrack.Album?.Id;
        track.QobuzArtistId = qobuzTrack.Performer?.Id.ToString();

        track.QobuzQuality = new QobuzQualityInfo
        {
            BitDepth = qobuzTrack.MaximumBitDepth,
            SampleRate = qobuzTrack.MaximumSamplingRate,
            IsHiRes = qobuzTrack.HiRes,
            IsStreamable = qobuzTrack.Album?.Streamable ?? false,
            IsDownloadable = qobuzTrack.Album?.Downloadable ?? false,
            IsPurchasable = qobuzTrack.Album?.Purchasable ?? false
        };

        if (qobuzTrack.Album != null)
        {
            track.Album ??= new Album(qobuzTrack.Album.Title ?? string.Empty);
            track.Album.QobuzId = qobuzTrack.Album.Id;
            track.Album.Label = qobuzTrack.Album.Label?.Name ?? track.Album.Label;
            track.Album.Copyright = qobuzTrack.Album.Label?.Name ?? track.Album.Copyright;
            track.Album.UPC = qobuzTrack.Album.UPC ?? track.Album.UPC;
            track.Album.Barcode = qobuzTrack.Album.Barcode ?? track.Album.Barcode;
            if (qobuzTrack.Album.Genre?.Name != null)
            {
                track.Album.Genre ??= new List<string>();
                if (!track.Album.Genre.Contains(qobuzTrack.Album.Genre.Name))
                {
                    track.Album.Genre.Add(qobuzTrack.Album.Genre.Name);
                }
            }

            track.Album.QobuzQuality = track.QobuzQuality;
        }
    }
}
