using System.Globalization;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public static class QualityScanTrackFormatter
{
    public static string FormatTechnicalProfile(QualityScanTrackDto track)
    {
        var extension = string.IsNullOrWhiteSpace(track.BestExtension)
            ? "UNKNOWN"
            : track.BestExtension.Trim().ToUpperInvariant();
        var bitDepth = track.BestBitsPerSample.HasValue && track.BestBitsPerSample.Value > 0
            ? $"{track.BestBitsPerSample.Value}-bit"
            : "unknown";
        var sampleRate = track.BestSampleRateHz.HasValue && track.BestSampleRateHz.Value > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{track.BestSampleRateHz.Value / 1000d:0.0} kHz")
            : "unknown";
        return $"{extension} • {bitDepth} • {sampleRate}";
    }
}
