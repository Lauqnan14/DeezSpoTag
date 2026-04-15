using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Deezer;
using CoreTrack = DeezSpoTag.Core.Models.Track;
using CoreArtist = DeezSpoTag.Core.Models.Artist;
using CoreAlbum = DeezSpoTag.Core.Models.Album;
using DeezSpoTag.Integrations.Deezer;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Utils;

/// <summary>
/// Consolidated search and enrichment utilities merging SearchFallbackService and TrackEnrichmentService
/// EXACT PORT from deezspotag api.ts get_track_id_from_metadata and track parsing logic
/// </summary>
public class SearchFallbackService
{
    private readonly ILogger<SearchFallbackService> _logger;
    private readonly AuthenticatedDeezerService _authenticatedDeezerService;

    public SearchFallbackService(ILogger<SearchFallbackService> logger, AuthenticatedDeezerService authenticatedDeezerService)
    {
        _logger = logger;
        _authenticatedDeezerService = authenticatedDeezerService;
    }

    /// <summary>
    /// Search for track ID using metadata - EXACT PORT from deezspotag api.ts get_track_id_from_metadata
    /// </summary>
    public async Task<string?> GetTrackIdFromMetadataAsync(string artist, string title, string album, CancellationToken cancellationToken = default)
    {
        try
        {
            var deezerClient = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
            if (deezerClient == null)
            {
                _logger.LogWarning("Deezer client not authenticated for search fallback");
                return null;
            }

            // EXACT PORT: Use the API service's search method
            var trackId = await deezerClient.Api.GetTrackIdFromMetadataAsync(artist, title, album);

            if (trackId != "0")
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Found track via search fallback: {Artist} - {Title} -> {TrackId}", artist, title, trackId);                }
                return trackId;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("No track found via search fallback: {Artist} - {Title} ({Album})", artist, title, album);            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error in search fallback for: {Artist} - {Title} ({Album})", artist, title, album);
            return null;
        }
    }

    /// <summary>
    /// Search for alternative track using various search strategies
    /// </summary>
    public async Task<CoreTrack?> SearchForAlternativeTrackAsync(CoreTrack originalTrack, CancellationToken cancellationToken = default)
    {
        try
        {
            var artist = originalTrack.MainArtist?.Name ?? "";
            var title = originalTrack.Title ?? "";
            var album = originalTrack.Album?.Title ?? "";

            var trackId = await GetTrackIdFromMetadataAsync(artist, title, album, cancellationToken);

            if (!HasResolvedTrackId(trackId))
            {
                return null;
            }

            var deezerClient = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
            if (deezerClient == null)
            {
                return null;
            }

            var gwTrack = await TryGetGatewayTrackAsync(deezerClient, trackId!);
            if (gwTrack == null)
            {
                return null;
            }

            var track = MapGwTrackToCoreTrack(gwTrack, markAsSearched: true);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Successfully found alternative track via search: {OriginalId} -> {NewId}",
                    originalTrack.Id,
                    track.Id);            }

            return track;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error searching for alternative track: {TrackId}", originalTrack.Id);
            return null;
        }
    }

    /// <summary>
    /// Find track by ISRC in specific album - EXACT PORT from deezspotag ISRC fallback logic
    /// </summary>
    public async Task<CoreTrack?> FindTrackByIsrcInAlbumAsync(string isrc, string albumId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(isrc) || string.IsNullOrEmpty(albumId))
                return null;

            var deezerClient = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
            if (deezerClient == null)
            {
                _logger.LogWarning("Deezer client not authenticated for ISRC fallback");
                return null;
            }

            // Get album page with tracks
            var albumPage = await deezerClient.Gw.GetAlbumPageAsync(albumId);
            var matchingTrack = albumPage?.Songs?.Data?.FirstOrDefault(t => t.Isrc == isrc);
            if (matchingTrack != null)
            {
                var track = MapGwTrackToCoreTrack(matchingTrack);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Found track by ISRC {ISRC} in album {AlbumId}: {TrackId}",
                        isrc, albumId, track.Id);                }
                return track;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("No track found with ISRC {ISRC} in album {AlbumId}", isrc, albumId);            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error finding track by ISRC {ISRC} in album {AlbumId}", isrc, albumId);
            return null;
        }
    }

    /// <summary>
    /// Get track with fallback ID - EXACT PORT from deezspotag gw.get_track_with_fallback
    /// </summary>
    public async Task<CoreTrack?> GetTrackWithFallbackAsync(string fallbackId, CancellationToken cancellationToken = default)
    {
        try
        {
            var deezerClient = await _authenticatedDeezerService.GetAuthenticatedClientAsync();
            if (deezerClient == null)
            {
                _logger.LogWarning("Deezer client not authenticated for fallback track");
                return null;
            }

            var gwTrack = await deezerClient.Gw.GetTrackWithFallbackAsync(fallbackId);
            if (gwTrack != null)
            {
                var track = MapGwTrackToCoreTrack(gwTrack);

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Successfully retrieved fallback track: {FallbackId} -> {TrackId}",
                        fallbackId, track.Id);                }

                return track;
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting track with fallback ID: {FallbackId}", fallbackId);
            return null;
        }
    }

    private async Task<GwTrack?> TryGetGatewayTrackAsync(DeezerClient deezerClient, string trackId)
    {
        try
        {
            return await deezerClient.Gw.GetTrackWithFallbackAsync(trackId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to get track data for search result: {TrackId}", trackId);
            return null;
        }
    }

    private static bool HasResolvedTrackId(string? trackId)
        => !string.IsNullOrWhiteSpace(trackId) && trackId != "0";

    private static CoreTrack MapGwTrackToCoreTrack(GwTrack gwTrack, bool markAsSearched = false)
    {
        var track = new CoreTrack
        {
            Id = gwTrack.SngId.ToString(),
            Title = gwTrack.SngTitle,
            Duration = gwTrack.Duration,
            MD5 = gwTrack.Md5Origin,
            MediaVersion = gwTrack.MediaVersion.ToString(),
            TrackToken = gwTrack.TrackToken,
            TrackTokenExpire = gwTrack.TrackTokenExpire,
            ISRC = gwTrack.Isrc,
            Searched = markAsSearched,
            FileSizes = BuildFileSizes(gwTrack),
            MainArtist = new CoreArtist
            {
                Id = gwTrack.ArtId.ToString(),
                Name = gwTrack.ArtName
            },
            Album = new CoreAlbum(gwTrack.AlbId, gwTrack.AlbTitle)
        };

        return track;
    }

    private static Dictionary<string, int> BuildFileSizes(GwTrack gwTrack)
    {
        var fileSizes = new Dictionary<string, int>();
        AddFileSizeIfPositive(fileSizes, "mp3_128", gwTrack.FilesizeMp3128);
        AddFileSizeIfPositive(fileSizes, "mp3_320", gwTrack.FilesizeMp3320);
        AddFileSizeIfPositive(fileSizes, "flac", gwTrack.FilesizeFlac);
        return fileSizes;
    }

    private static void AddFileSizeIfPositive(Dictionary<string, int> fileSizes, string key, int? value)
    {
        if (value.HasValue && value.Value > 0)
        {
            fileSizes[key] = value.Value;
        }
    }
}
