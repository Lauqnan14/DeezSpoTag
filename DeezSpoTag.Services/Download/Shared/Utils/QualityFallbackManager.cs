using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Core.Exceptions;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Integrations.Deezer;
using Microsoft.Extensions.Logging;
using DeezerTrack = DeezSpoTag.Integrations.Deezer.Track;

namespace DeezSpoTag.Services.Download.Shared.Utils;

/// <summary>
/// Quality fallback manager for handling bitrate selection and fallback logic
/// Ported from: Quality fallback logic in deezspotag getPreferredBitrate.ts
/// </summary>
public class QualityFallbackManager
{
    private readonly ILogger<QualityFallbackManager> _logger;
    private readonly DeezerClient _deezerClient;

    // Track format mappings (ported from deezspotag)
    private static readonly Dictionary<int, string> FormatsNon360 = new()
    {
        [9] = "FLAC",      // TrackFormats.FLAC
        [3] = "MP3_320",   // TrackFormats.MP3_320  
        [1] = "MP3_128",   // TrackFormats.MP3_128
    };

    private static readonly Dictionary<int, string> Formats360 = new()
    {
        [15] = "MP4_RA1",  // TrackFormats.MP4_RA1
        [14] = "MP4_RA2",  // TrackFormats.MP4_RA2
        [13] = "MP4_RA3",  // TrackFormats.MP4_RA3
    };

    public QualityFallbackManager(ILogger<QualityFallbackManager> logger, DeezerClient deezerClient)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _deezerClient = deezerClient ?? throw new ArgumentNullException(nameof(deezerClient));
    }

    public static EngineQualityFallbackResult ApplyQualityFallback(string engine, string? requestedQuality, DeezSpoTagSettings settings)
    {
        var normalized = engine?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new EngineQualityFallbackResult(
                Engine: normalized,
                RequestedQuality: requestedQuality,
                SelectedQuality: requestedQuality,
                FallbackApplied: false,
                AvailableQualities: Array.Empty<string>(),
                Reason: "unknown_engine");
        }

        var engineOptions = QualityCatalog.GetEngineQualityOptions();
        if (!engineOptions.TryGetValue(normalized, out var options) || options.Count == 0)
        {
            return new EngineQualityFallbackResult(
                Engine: normalized,
                RequestedQuality: requestedQuality,
                SelectedQuality: requestedQuality,
                FallbackApplied: false,
                AvailableQualities: Array.Empty<string>(),
                Reason: "unsupported_engine");
        }

        var available = options.Select(option => option.Value).ToList();
        var ordered = DownloadSourceOrder.ResolveEngineQualitySources(
                normalized,
                requestedQuality,
                strict: !settings.FallbackBitrate)
            .Select(DownloadSourceOrder.DecodeAutoSource)
            .Select(step => step.Quality)
            .Where(quality => !string.IsNullOrWhiteSpace(quality))
            .Select(quality => quality!)
            .ToList();

        if (ordered.Count == 0)
        {
            ordered = available;
        }

        var selected = ordered.FirstOrDefault(quality => available.Contains(quality, StringComparer.OrdinalIgnoreCase))
            ?? available[0];
        var requested = string.IsNullOrWhiteSpace(requestedQuality) ? null : requestedQuality;
        var fallbackApplied = !string.Equals(selected, requested, StringComparison.OrdinalIgnoreCase);
        string? reason = null;
        if (fallbackApplied)
        {
            reason = requested == null ? "default_quality" : "unsupported_quality";
        }

        return new EngineQualityFallbackResult(
            Engine: normalized,
            RequestedQuality: requestedQuality,
            SelectedQuality: selected,
            FallbackApplied: fallbackApplied,
            AvailableQualities: available,
            Reason: reason);
    }

    /// <summary>
    /// Get available formats based on settings and preferred bitrate
    /// Ported from: Format selection logic in deezspotag getPreferredBitrate.ts
    /// </summary>
    public List<(int formatNumber, string formatName)> GetAvailableFormats(int preferredBitrate, DeezSpoTagSettings settings)
    {
        try
        {
            _logger.LogDebug("Getting available formats for preferred bitrate: {PreferredBitrate}, fallback enabled: {FallbackEnabled}", 
                preferredBitrate, settings.FallbackBitrate);

            var is360Format = Formats360.ContainsKey(preferredBitrate);
            Dictionary<int, string> formats;

            if (!settings.FallbackBitrate)
            {
                // No fallback - use all formats
                formats = new Dictionary<int, string>(FormatsNon360);
                foreach (var kvp in Formats360)
                {
                    formats[kvp.Key] = kvp.Value;
                }
            }
            else if (is360Format)
            {
                // 360 format requested - only use 360 formats
                formats = new Dictionary<int, string>(Formats360);
            }
            else
            {
                // Regular format requested - only use non-360 formats
                formats = new Dictionary<int, string>(FormatsNon360);
            }

            // Return formats in descending order of quality, but only up to preferred bitrate
            var availableFormats = formats
                .Where(kvp => kvp.Key <= preferredBitrate)
                .OrderByDescending(kvp => kvp.Key)
                .Select(kvp => (kvp.Key, kvp.Value))
                .ToList();

            _logger.LogDebug("Found {FormatCount} available formats: {Formats}", 
                availableFormats.Count, string.Join(", ", availableFormats.Select(f => f.Value)));

            return availableFormats;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error getting available formats");
            return new List<(int, string)>();
        }
    }

    /// <summary>
    /// Apply quality fallback logic for track
    /// Ported from: Quality fallback logic in deezspotag getPreferredBitrate.ts
    /// </summary>
    public QualityFallbackResult ApplyTrackQualityFallback(DeezerTrack track, int preferredBitrate, DeezSpoTagSettings settings)
    {
        try
        {
            _logger.LogDebug("Applying quality fallback for track {TrackId}, preferred bitrate: {PreferredBitrate}", 
                track.Id, preferredBitrate);

            var result = new QualityFallbackResult
            {
                OriginalBitrate = preferredBitrate,
                FallbackApplied = false,
                SelectedBitrate = preferredBitrate,
                SelectedFormat = GetFormatName(preferredBitrate),
                AvailableFormats = GetAvailableFormats(preferredBitrate, settings)
            };

            // Check if track has file sizes information
            if (track.FileSizes == null || track.FileSizes.Count == 0)
            {
                _logger.LogWarning("Track {TrackId} has no file size information", track.Id);
                result.ErrorMessage = "No file size information available";
                return result;
            }

            // Check each format in order of preference
            foreach (var (formatNumber, formatName) in result.AvailableFormats)
            {
                var formatKey = formatName.ToLower();
                
                if (track.FileSizes.TryGetValue(formatKey, out var fileSize) && fileSize > 0)
                {
                    // Check license requirements
                    if (IsLicenseValid(formatName))
                    {
                        result.SelectedBitrate = formatNumber;
                        result.SelectedFormat = formatName;
                        result.FileSize = fileSize;
                        result.FallbackApplied = formatNumber != preferredBitrate;
                        
                        if (result.FallbackApplied)
                        {
                            _logger.LogInformation("Applied quality fallback for track {TrackId}: {OriginalFormat} -> {FallbackFormat}", 
                                track.Id, GetFormatName(preferredBitrate), formatName);
                        }
                        
                        return result;
                    }
                    else
                    {
                        _logger.LogDebug("License check failed for format {FormatName}", formatName);
                    }
                }
                else
                {
                    _logger.LogDebug("Format {FormatName} not available for track {TrackId} (file size: {FileSize})", 
                        formatName, track.Id, fileSize);
                }
            }

            // No suitable format found
            result.ErrorMessage = "No suitable format available";
            _logger.LogWarning("No suitable format found for track {TrackId}", track.Id);
            
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error applying quality fallback for track {TrackId}", track.Id);
            return new QualityFallbackResult
            {
                OriginalBitrate = preferredBitrate,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Check if user license allows streaming the specified format
    /// Ported from: License checking logic in deezspotag deezer.ts get_tracks_url method
    /// </summary>
    private bool IsLicenseValid(string formatName)
    {
        try
        {
            // Get current user from DeezerClient (exact port from deezspotag deezer.ts)
            var currentUser = _deezerClient.CurrentUser;
            if (currentUser == null)
            {
                _logger.LogWarning("No current user available for license checking, allowing format {FormatName}", formatName);
                return true; // Allow if no user info available
            }

            // Exact license checking logic from deezspotag deezer.ts
            switch (formatName)
            {
                case "FLAC":
                case "MP4_RA1":
                case "MP4_RA2":
                case "MP4_RA3":
                    // Lossless formats require premium subscription (exact deezspotag logic)
                    if (!(currentUser.CanStreamLossless ?? false))
                    {
                        _logger.LogDebug("User cannot stream lossless format {FormatName}", formatName);
                        return false;
                    }
                    break;
                
                case "MP3_320":
                    // HQ MP3 requires premium subscription (exact deezspotag logic)
                    if (!(currentUser.CanStreamHq ?? false))
                    {
                        _logger.LogDebug("User cannot stream HQ format {FormatName}", formatName);
                        return false;
                    }
                    break;
                
                case "MP3_128":
                case "MP3_MISC":
                default:
                    // Basic formats are available to all users
                    break;
            }

            _logger.LogDebug("License check passed for format {FormatName}", formatName);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error checking license for format {FormatName}", formatName);
            return false;
        }
    }

    /// <summary>
    /// Get format name from format number
    /// </summary>
    private static string GetFormatName(int formatNumber)
    {
        if (FormatsNon360.TryGetValue(formatNumber, out var format))
        {
            return format;
        }
        
        if (Formats360.TryGetValue(formatNumber, out var format360))
        {
            return format360;
        }
        
        return "MP3_128"; // Default fallback
    }

    /// <summary>
    /// Get format number from format name
    /// </summary>
    public static int GetFormatNumber(string formatName)
    {
        var format = FormatsNon360.FirstOrDefault(kvp => kvp.Value == formatName);
        if (format.Key != 0)
        {
            return format.Key;
        }
        
        var format360 = Formats360.FirstOrDefault(kvp => kvp.Value == formatName);
        if (format360.Key != 0)
        {
            return format360.Key;
        }
        
        return 1; // Default to MP3_128
    }

    /// <summary>
    /// Check if format is 360 Reality Audio
    /// </summary>
    public static bool Is360Format(int formatNumber)
    {
        return Formats360.ContainsKey(formatNumber);
    }

    /// <summary>
    /// Check if format is lossless
    /// </summary>
    public static bool IsLosslessFormat(int formatNumber)
    {
        var formatName = GetFormatName(formatNumber);
        return formatName == "FLAC" || formatName.StartsWith("MP4_RA");
    }

    /// <summary>
    /// Get quality description for format
    /// </summary>
    public static string GetQualityDescription(int formatNumber)
    {
        return QualityDescriptionMap.GetQualityDescription(formatNumber);
    }
}

public sealed record EngineQualityFallbackResult(
    string Engine,
    string? RequestedQuality,
    string? SelectedQuality,
    bool FallbackApplied,
    IReadOnlyList<string> AvailableQualities,
    string? Reason);

/// <summary>
/// Result of quality fallback operation
/// </summary>
public class QualityFallbackResult
{
    public int OriginalBitrate { get; set; }
    public int SelectedBitrate { get; set; }
    public string SelectedFormat { get; set; } = "";
    public bool FallbackApplied { get; set; }
    public long FileSize { get; set; }
    public string? ErrorMessage { get; set; }
    public List<(int formatNumber, string formatName)> AvailableFormats { get; set; } = new();
    
    public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);
    public string QualityDescription => QualityDescriptionMap.GetQualityDescription(SelectedBitrate);
}

internal static class QualityDescriptionMap
{
    public static string GetQualityDescription(int formatNumber)
    {
        return formatNumber switch
        {
            9 => "FLAC Lossless",
            3 => "MP3 320kbps",
            1 => "MP3 128kbps",
            15 => "360 Reality Audio (High)",
            14 => "360 Reality Audio (Medium)",
            13 => "360 Reality Audio (Low)",
            0 => "Default MP3",
            _ => "Unknown Quality"
        };
    }
}
