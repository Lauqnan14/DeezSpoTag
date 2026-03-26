using DeezSpoTag.Integrations.Deezer;
using Microsoft.Extensions.Logging;
using CoreTrack = DeezSpoTag.Core.Models.Track;
using DeezerTrack = DeezSpoTag.Integrations.Deezer.Track;
using System.Linq;

namespace DeezSpoTag.Services.Download.Utils;

/// <summary>
/// Service for enriching track data with additional metadata from Deezer Gateway API
/// Ported from deezspotag track parsing logic
/// </summary>
public class TrackEnrichmentService
{
    private readonly ILogger<TrackEnrichmentService> _logger;
    private readonly DeezerGatewayService _gatewayService;

    public TrackEnrichmentService(
        ILogger<TrackEnrichmentService> logger,
        DeezerGatewayService gatewayService)
    {
        _logger = logger;
        _gatewayService = gatewayService;
    }

    /// <summary>
    /// Check if track needs enrichment (missing critical data)
    /// </summary>
    public static bool NeedsEnrichment(DeezerTrack track)
    {
        return string.IsNullOrEmpty(track.TrackToken) ||
               string.IsNullOrEmpty(track.MD5) ||
               string.IsNullOrEmpty(track.MediaVersion);
    }

    /// <summary>
    /// Enrich track with additional data from Gateway API (port of parseData method from deezspotag)
    /// </summary>
    public async Task<DeezerTrack> EnrichTrackAsync(DeezerTrack track, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Enriching track {TrackId} with Gateway API data", track.Id);

            // Get track data with fallback (exactly like deezspotag)
            var gwTrack = await _gatewayService.GetTrackWithFallbackAsync(track.Id ?? "");
            if (gwTrack == null)
            {
                _logger.LogWarning("Failed to get Gateway track data for {TrackId}", track.Id);
                return track;
            }

            // Map essential data from Gateway track (port of parseEssentialData from deezspotag)
            MapEssentialData(track, gwTrack);

            var lyricsPayload = await TryGetLyricsPayloadAsync(gwTrack, track.Id, cancellationToken);
            if (lyricsPayload != null)
            {
                track.Lyrics = new Lyrics
                {
                    Id = gwTrack.LyricsId ?? string.Empty,
                    Sync = lyricsPayload.Value.Sync,
                    Unsync = lyricsPayload.Value.Unsync
                };
            }

            _logger.LogDebug("Successfully enriched track {TrackId}", track.Id);
            return track;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to enrich track {TrackId}", track.Id);
            return track;
        }
    }

    /// <summary>
    /// Map essential data from Gateway track (port of parseEssentialData from deezspotag Track class)
    /// </summary>
    private void MapEssentialData(DeezerTrack track, DeezSpoTag.Core.Models.Deezer.GwTrack gwTrack)
    {
        // Essential data for downloading (exactly like deezspotag)
        track.MD5 = gwTrack.Md5Origin;
        track.MediaVersion = gwTrack.MediaVersion.ToString();
        track.TrackToken = gwTrack.TrackToken ?? "";
        track.TrackTokenExpire = gwTrack.TrackTokenExpire;

        track.FileSizes = TrackEnrichmentMappingHelper.BuildFileSizes(gwTrack);

        // Additional metadata
        track.ISRC = gwTrack.Isrc ?? track.ISRC ?? "";
        track.ReplayGain = gwTrack.Gain.ToString("F2");
        track.LyricsId = gwTrack.LyricsId ?? string.Empty;
        track.Copyright = gwTrack.Copyright ?? track.Copyright ?? "";
        track.PhysicalReleaseDate = gwTrack.PhysicalReleaseDate ?? track.PhysicalReleaseDate ?? "";

        // Handle fallback data (port of fallback logic from deezspotag)
        if (gwTrack.Fallback != null)
        {
            // Extract fallback ID from the fallback object
            track.FallbackID = TrackEnrichmentMappingHelper.ExtractFallbackId(gwTrack.Fallback);
            _logger.LogDebug("Track {TrackId} has fallback ID: {FallbackId}", track.Id, track.FallbackID);
        }

        // Handle album fallback data (for ISRC fallback)
        if (gwTrack.AlbumFallback != null)
        {
            // Extract album IDs for ISRC matching
            track.AlbumsFallback = TrackEnrichmentMappingHelper.ExtractAlbumsFallback(gwTrack.AlbumFallback);
            _logger.LogDebug("Track {TrackId} has album fallback data: {AlbumCount} albums", track.Id, track.AlbumsFallback.Count);
        }

        LogMappedTrackEssentials(track.Id, track.MD5, track.MediaVersion, track.TrackToken, track.FileSizes, gwTrack);
    }

    /// <summary>
    /// Get track by ISRC for fallback scenarios
    /// Ported from: ISRC search logic in deezspotag
    /// </summary>
    public async Task<DeezerTrack?> FindByIsrcAsync(string isrc, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(isrc))
            return null;

        try
        {
            _logger.LogDebug("Searching for track by ISRC: {ISRC}", isrc);

            // Use the Gateway API to search for tracks by ISRC
            var searchResults = await _gatewayService.SearchAsync($"isrc:{isrc}");

            var trackData = searchResults?.Track?.Data;
            if (trackData is { Length: > 0 })
            {
                var firstResult = trackData[0];
                if (firstResult is Dictionary<string, object> trackDict && trackDict.TryGetValue("SNG_ID", out var trackIdObj))
                {
                    var trackId = trackIdObj.ToString();
                    _logger.LogDebug("Found track by ISRC {ISRC}: {TrackId}", isrc, trackId);
                    
                    // Get full track data
                    var gwTrack = await _gatewayService.GetTrackWithFallbackAsync(trackId ?? "");
                    if (gwTrack != null)
                    {
                        var track = new DeezerTrack();
                        MapEssentialData(track, gwTrack);
                        return track;
                    }
                }
            }

            _logger.LogDebug("No track found for ISRC: {ISRC}", isrc);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to find track by ISRC: {ISRC}", isrc);
            return null;
        }
    }

    /// <summary>
    /// Find alternative track using search fallback
    /// Ported from: Search fallback logic in deezspotag
    /// </summary>
    public async Task<DeezerTrack?> FindAlternativeAsync(DeezerTrack originalTrack, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Searching for alternative to track {TrackId}: {Artist} - {Title}", 
                originalTrack.Id, originalTrack.MainArtist?.Name, originalTrack.Title);

            // Build search query from track metadata (like deezspotag does)
            var searchQuery = $"{originalTrack.MainArtist?.Name} {originalTrack.Title}";
            if (!string.IsNullOrEmpty(originalTrack.Album?.Title))
            {
                searchQuery += $" {originalTrack.Album.Title}";
            }

            _logger.LogDebug("Search query for alternative: {Query}", searchQuery);

            // Search using Gateway API
            var searchResults = await _gatewayService.SearchAsync(searchQuery);

            var trackData = searchResults?.Track?.Data;
            if (trackData is { Length: > 0 })
            {
                // Find the best match (first result that's not the original track)
                foreach (var trackId in trackData
                             .Select(static result =>
                             {
                                 if (result is Dictionary<string, object> trackDict
                                     && trackDict.TryGetValue("SNG_ID", out var trackIdObj))
                                 {
                                     return trackIdObj?.ToString();
                                 }

                                 return null;
                             })
                             .Where(trackId => !string.IsNullOrWhiteSpace(trackId)
                                               && !string.Equals(trackId, originalTrack.Id, StringComparison.Ordinal)))
                {
                    _logger.LogDebug("Found alternative track: {TrackId}", trackId);

                    // Get full track data
                    var gwTrack = await _gatewayService.GetTrackWithFallbackAsync(trackId ?? "");
                    if (gwTrack != null)
                    {
                        var track = new DeezerTrack();
                        MapEssentialData(track, gwTrack);
                        track.Searched = true; // Mark as found via search

                        _logger.LogDebug("Successfully mapped alternative track: {TrackId} - {Artist} - {Title}",
                            track.Id, track.MainArtist?.Name, track.Title);

                        return track;
                    }
                }
            }

            _logger.LogDebug("No alternative found for track {TrackId}", originalTrack.Id);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to find alternative for track {TrackId}", originalTrack.Id);
            return null;
        }
    }

    /// <summary>
    /// Find tracks in album by ISRC (for ISRC fallback)
    /// Ported from: Album ISRC search logic in deezspotag
    /// </summary>
    public async Task<DeezerTrack?> FindTrackInAlbumByIsrcAsync(string albumId, string isrc, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(albumId) || string.IsNullOrEmpty(isrc))
            return null;

        try
        {
            _logger.LogDebug("Searching for track with ISRC {ISRC} in album {AlbumId}", isrc, albumId);

            // Get album page to access all tracks
            var albumPage = await _gatewayService.GetAlbumPageAsync(albumId);
            if (albumPage?.Songs?.Data == null)
            {
                _logger.LogDebug("No tracks found in album {AlbumId}", albumId);
                return null;
            }

            // Find track with matching ISRC
            var matchingTrack = albumPage.Songs.Data.FirstOrDefault(t => t.Isrc == isrc);
            if (matchingTrack == null)
            {
                _logger.LogDebug("No track with ISRC {ISRC} found in album {AlbumId}", isrc, albumId);
                return null;
            }

            _logger.LogDebug("Found track with ISRC {ISRC} in album {AlbumId}: track {TrackId}", 
                isrc, albumId, matchingTrack.SngId);

            // Get full track data
            var gwTrack = await _gatewayService.GetTrackWithFallbackAsync(matchingTrack.SngId.ToString());
            if (gwTrack != null)
            {
                var track = new DeezerTrack();
                MapEssentialData(track, gwTrack);
                return track;
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to find track with ISRC {ISRC} in album {AlbumId}", isrc, albumId);
            return null;
        }
    }

    /// <summary>
    /// Enrich Core Track with additional data from Gateway API (for Downloader compatibility)
    /// </summary>
    public async Task EnrichCoreTrackAsync(CoreTrack track, Dictionary<string, object> trackData, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Enriching Core track {TrackId} with Gateway API data", track.Id);

            // Get track data with fallback (exactly like deezspotag)
            var gwTrack = await _gatewayService.GetTrackWithFallbackAsync(track.Id ?? "");
            if (gwTrack == null)
            {
                _logger.LogWarning("Failed to get Gateway track data for {TrackId}", track.Id);
                return;
            }

            // Map essential data from Gateway track to Core Track
            MapEssentialDataToCore(track, gwTrack);

            var lyricsPayload = await TryGetLyricsPayloadAsync(gwTrack, track.Id, cancellationToken);
            if (lyricsPayload != null)
            {
                track.Lyrics = new DeezSpoTag.Core.Models.Lyrics
                {
                    Id = gwTrack.LyricsId ?? string.Empty,
                    Sync = lyricsPayload.Value.Sync,
                    Unsync = lyricsPayload.Value.Unsync
                };
            }

            _logger.LogDebug("Successfully enriched Core track {TrackId}", track.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to enrich Core track {TrackId}", track.Id);
        }
    }

    /// <summary>
    /// Map essential data from Gateway track to Core Track (port of parseEssentialData from deezspotag Track class)
    /// </summary>
    private void MapEssentialDataToCore(CoreTrack track, DeezSpoTag.Core.Models.Deezer.GwTrack gwTrack)
    {
        track.ParseEssentialData(gwTrack);

        LogMappedTrackEssentials(track.Id, track.MD5, track.MediaVersion, track.TrackToken, track.FileSizes, gwTrack);
    }

    private void LogMappedTrackEssentials(
        string? trackId,
        string? md5,
        string? mediaVersion,
        string? trackToken,
        IReadOnlyDictionary<string, int> fileSizes,
        DeezSpoTag.Core.Models.Deezer.GwTrack gwTrack)
    {
        _logger.LogDebug(
            "Mapped essential data for track {TrackId}: MD5={MD5}, MediaVersion={MediaVersion}, TrackToken={HasToken}, FileSizes={FileSizeCount}",
            trackId,
            md5,
            mediaVersion,
            !string.IsNullOrEmpty(trackToken),
            fileSizes.Count);

        if (fileSizes.Count > 0)
        {
            _logger.LogDebug(
                "Track {TrackId} file sizes: {FileSizes}",
                trackId,
                string.Join(", ", fileSizes.Select(kvp => $"{kvp.Key}={kvp.Value}")));
            return;
        }

        _logger.LogWarning(
            "Track {TrackId} has no valid file sizes from GW API. Raw sizes: mp3_128={Mp3128}, mp3_320={Mp3320}, flac={Flac}",
            trackId,
            gwTrack.FilesizeMp3128,
            gwTrack.FilesizeMp3320,
            gwTrack.FilesizeFlac);
    }

    private async Task<(string Sync, string Unsync)?> TryGetLyricsPayloadAsync(
        DeezSpoTag.Core.Models.Deezer.GwTrack gwTrack,
        string? trackId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(gwTrack.LyricsId))
        {
            return null;
        }

        try
        {
            var lyricsData = await _gatewayService.GetLyricsAsync(gwTrack.SngId.ToString(), cancellationToken);
            if (lyricsData == null)
            {
                return null;
            }

            return (
                lyricsData.GetValueOrDefault("LYRICS_SYNC_JSON", "")?.ToString() ?? "",
                lyricsData.GetValueOrDefault("LYRICS_TEXT", "")?.ToString() ?? "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to get lyrics for track {TrackId}", trackId);
            return null;
        }
    }
}

/// <summary>
/// PHASE 3: Extension methods for Track to support Gateway API integration
/// </summary>
public static class TrackExtensions
{
    /// <summary>
    /// PHASE 3: Parse essential track data from Gateway track (port from deezspotag parseEssentialData)
    /// </summary>
    public static void ParseEssentialData(this CoreTrack track, DeezSpoTag.Core.Models.Deezer.GwTrack gwTrack)
    {
        // EXACT PORT: Essential data mapping from deezspotag parseEssentialData
        track.Id = gwTrack.SngId.ToString();
        track.Title = gwTrack.SngTitle ?? track.Title;
        track.Duration = gwTrack.Duration;
        track.MD5 = gwTrack.Md5Origin;
        track.MediaVersion = gwTrack.MediaVersion.ToString();
        track.TrackToken = gwTrack.TrackToken;
        track.TrackTokenExpire = gwTrack.TrackTokenExpire;
        track.TrackTokenExpiration = gwTrack.TrackTokenExpire;
        track.ISRC = gwTrack.Isrc ?? track.ISRC;
        track.TrackNumber = gwTrack.TrackNumber;
        track.DiscNumber = gwTrack.DiskNumber;
        track.DiskNumber = gwTrack.DiskNumber; // Compatibility
        track.Explicit = gwTrack.ExplicitLyrics;
        track.Gain = gwTrack.Gain;
        track.ReplayGain = gwTrack.Gain.ToString("F2");
        track.LyricsId = gwTrack.LyricsId;
        track.Copyright = gwTrack.Copyright ?? track.Copyright;
        track.PhysicalReleaseDate = gwTrack.PhysicalReleaseDate ?? track.PhysicalReleaseDate;

        track.FileSizes = TrackEnrichmentMappingHelper.BuildFileSizes(gwTrack);

        // Update artist and album info if available
        if (gwTrack.ArtId > 0 && !string.IsNullOrEmpty(gwTrack.ArtName))
        {
            track.MainArtist = new DeezSpoTag.Core.Models.Artist(gwTrack.ArtId.ToString(), gwTrack.ArtName);
            if (!track.Artist["Main"].Contains(gwTrack.ArtName))
            {
                track.Artist["Main"].Clear();
                track.Artist["Main"].Add(gwTrack.ArtName);
            }
            if (!track.Artists.Contains(gwTrack.ArtName))
            {
                track.Artists.Clear();
                track.Artists.Add(gwTrack.ArtName);
            }
        }

        if (!string.IsNullOrEmpty(gwTrack.AlbId) && !string.IsNullOrEmpty(gwTrack.AlbTitle))
        {
            track.Album = new DeezSpoTag.Core.Models.Album(gwTrack.AlbId, gwTrack.AlbTitle);
        }

        // Handle fallback data (port of fallback logic from deezspotag)
        if (gwTrack.Fallback != null)
        {
            track.FallbackID = TrackEnrichmentMappingHelper.ExtractFallbackId(gwTrack.Fallback);
            track.FallbackId = track.FallbackID; // Compatibility
        }

        // Handle album fallback data (for ISRC fallback)
        if (gwTrack.AlbumFallback != null)
        {
            track.AlbumsFallback = TrackEnrichmentMappingHelper.ExtractAlbumsFallback(gwTrack.AlbumFallback);
        }

        // Reset URLs as they need to be regenerated
        track.Urls = new Dictionary<string, string>();
        track.Local = false;
    }

}

internal static class TrackEnrichmentMappingHelper
{
    public static Dictionary<string, int> BuildFileSizes(DeezSpoTag.Core.Models.Deezer.GwTrack gwTrack)
    {
        var fileSizes = new Dictionary<string, int>();
        AddFileSize(fileSizes, "default", gwTrack.Filesize);
        AddFileSize(fileSizes, "mp3_128", gwTrack.FilesizeMp3128);
        AddFileSize(fileSizes, "mp3_320", gwTrack.FilesizeMp3320);
        AddFileSize(fileSizes, "flac", gwTrack.FilesizeFlac);
        AddFileSize(fileSizes, "mp4_ra1", gwTrack.FilesizeMp4Ra1);
        AddFileSize(fileSizes, "mp4_ra2", gwTrack.FilesizeMp4Ra2);
        AddFileSize(fileSizes, "mp4_ra3", gwTrack.FilesizeMp4Ra3);
        return fileSizes;
    }

    public static int ExtractFallbackId(object fallback)
    {
        if (fallback is int intValue)
        {
            return intValue;
        }

        if (fallback is string strValue && int.TryParse(strValue, out var parsed))
        {
            return parsed;
        }

        if (fallback is long longValue)
        {
            return (int)longValue;
        }

        if (fallback is Dictionary<string, object> dict)
        {
            if (dict.TryGetValue("SNG_ID", out var sngId))
            {
                return ExtractFallbackId(sngId);
            }

            if (dict.TryGetValue("id", out var id))
            {
                return ExtractFallbackId(id);
            }
        }

        return 0;
    }

    public static List<string> ExtractAlbumsFallback(object albumFallback)
    {
        if (albumFallback is List<string> stringList)
        {
            return stringList;
        }

        if (albumFallback is List<object> objectList)
        {
            return objectList
                .Select(static o => o?.ToString())
                .Where(static s => !string.IsNullOrEmpty(s))
                .Select(static s => s!)
                .ToList();
        }

        if (albumFallback is string[] stringArray)
        {
            return stringArray.ToList();
        }

        if (albumFallback is Dictionary<string, object> dict)
        {
            return dict.Values
                .Select(static value => value?.ToString())
                .Where(static albumId => !string.IsNullOrEmpty(albumId))
                .Select(static albumId => albumId!)
                .ToList();
        }

        return new List<string>();
    }

    private static void AddFileSize(IDictionary<string, int> fileSizes, string key, int value)
    {
        if (value > 0)
        {
            fileSizes[key] = value;
        }
    }

    private static void AddFileSize(IDictionary<string, int> fileSizes, string key, int? value)
    {
        if (value is > 0)
        {
            fileSizes[key] = value.Value;
        }
    }
}
