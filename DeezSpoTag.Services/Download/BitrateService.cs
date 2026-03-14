using DeezSpoTag.Core.Enums;

namespace DeezSpoTag.Services.Download;

/// <summary>
/// Bitrate service ported from deezspotag bitrate.ts
/// </summary>
public static class BitrateService
{
    private static readonly Dictionary<TrackFormat, string[]> BitrateTextMap = new()
    {
        [TrackFormat.MP3_128] = ["mp3_128", "128", "1"],
        [TrackFormat.MP3_320] = ["mp3_320", "mp3", "320", "3"],
        [TrackFormat.FLAC] = ["flac", "lossless", "9"],
        [TrackFormat.MP4_RA1] = ["mp4_ra1", "360_lq", "13"],
        [TrackFormat.MP4_RA2] = ["mp4_ra2", "360_mq", "14"],
        [TrackFormat.MP4_RA3] = ["mp4_ra3", "360", "360_hq", "15"]
    };

    private static readonly Dictionary<TrackFormat, string> FormatExtensions = new()
    {
        [TrackFormat.MP3_128] = ".mp3",
        [TrackFormat.MP3_320] = ".mp3",
        [TrackFormat.FLAC] = ".flac",
        [TrackFormat.MP4_RA1] = ".mp4",
        [TrackFormat.MP4_RA2] = ".mp4",
        [TrackFormat.MP4_RA3] = ".mp4"
    };

    /// <summary>
    /// Parse bitrate text to TrackFormat (port of getBitrateNumberFromText)
    /// </summary>
    public static TrackFormat? ParseBitrate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim().ToLowerInvariant();

        return BitrateTextMap
            .Where(kvp => kvp.Value.Contains(text))
            .Select(kvp => (TrackFormat?)kvp.Key)
            .FirstOrDefault();
    }

    /// <summary>
    /// Get file extension for track format
    /// </summary>
    public static string GetFileExtension(TrackFormat format)
    {
        return FormatExtensions.TryGetValue(format, out var extension) ? extension : ".mp3";
    }

    /// <summary>
    /// Get format number for API calls
    /// </summary>
    public static string GetFormatNumber(TrackFormat format)
    {
        return format switch
        {
            TrackFormat.MP3_128 => "1",
            TrackFormat.MP3_320 => "3",
            TrackFormat.FLAC => "9",
            TrackFormat.MP4_RA1 => "13",
            TrackFormat.MP4_RA2 => "14",
            TrackFormat.MP4_RA3 => "15",
            _ => "3" // Default to MP3_320
        };
    }

    /// <summary>
    /// Get available bitrates for display
    /// </summary>
    public static Dictionary<TrackFormat, string[]> GetAvailableBitrates()
    {
        return new Dictionary<TrackFormat, string[]>(BitrateTextMap);
    }

    /// <summary>
    /// Get preferred bitrate based on settings and availability
    /// </summary>
    public static TrackFormat GetPreferredBitrate(
        TrackFormat maxBitrate,
        TrackFormat[] availableFormats,
        bool fallbackEnabled = true)
    {
        // Try to get the exact requested bitrate
        if (availableFormats.Contains(maxBitrate))
            return maxBitrate;

        if (!fallbackEnabled)
            return maxBitrate; // Return requested even if not available

        // Fallback logic - try to get the highest available bitrate that's <= maxBitrate
        var orderedFormats = new[]
        {
            TrackFormat.MP4_RA3,
            TrackFormat.MP4_RA2,
            TrackFormat.MP4_RA1,
            TrackFormat.FLAC,
            TrackFormat.MP3_320,
            TrackFormat.MP3_128
        };

        var maxBitrateIndex = Array.IndexOf(orderedFormats, maxBitrate);
        if (maxBitrateIndex == -1)
            maxBitrateIndex = orderedFormats.Length - 1;

        // Look for the best available format starting from maxBitrate and going down
        for (int i = maxBitrateIndex; i < orderedFormats.Length; i++)
        {
            if (availableFormats.Contains(orderedFormats[i]))
                return orderedFormats[i];
        }

        // If nothing found, return the first available format
        return availableFormats.FirstOrDefault();
    }

    /// <summary>
    /// Check if format is lossless
    /// </summary>
    public static bool IsLossless(TrackFormat format)
    {
        return format == TrackFormat.FLAC;
    }

    /// <summary>
    /// Check if format is 360 Reality Audio
    /// </summary>
    public static bool Is360RealityAudio(TrackFormat format)
    {
        return format is TrackFormat.MP4_RA1 or TrackFormat.MP4_RA2 or TrackFormat.MP4_RA3;
    }

    /// <summary>
    /// Get bitrate quality description
    /// </summary>
    public static string GetQualityDescription(TrackFormat format)
    {
        return format switch
        {
            TrackFormat.MP3_128 => "Standard Quality (128 kbps)",
            TrackFormat.MP3_320 => "High Quality (320 kbps)",
            TrackFormat.FLAC => "Lossless Quality (FLAC)",
            TrackFormat.MP4_RA1 => "360 Reality Audio (Low Quality)",
            TrackFormat.MP4_RA2 => "360 Reality Audio (Medium Quality)",
            TrackFormat.MP4_RA3 => "360 Reality Audio (High Quality)",
            _ => "Unknown Quality"
        };
    }
}
