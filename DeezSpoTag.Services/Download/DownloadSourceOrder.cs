using System.Linq;
using DeezSpoTag.Core.Models.Settings;

namespace DeezSpoTag.Services.Download;

public static class DownloadSourceOrder
{
    private const string AutoService = "auto";
    private const string DeezerSource = "deezer";
    private const string QobuzSource = "qobuz";
    private const string TidalSource = "tidal";
    private const string AppleSource = "apple";
    private const string AmazonSource = "amazon";
    public const int DeezerFlac = 9;
    public const int DeezerMp3High = 3;
    public const int DeezerMp3Low = 1;

    public readonly record struct AutoSourceStep(string Source, string? Quality);

    private sealed record DownloadProfile(string Source, string Label, string? Quality, int? DeezerBitrate);

    // WARNING: Do not change this order and do not remove any items; fallback behavior depends on it.
    private static readonly DownloadProfile[] AutoPriority =
    [
        new(QobuzSource, "Qobuz Hi-Res (24-bit/96kHz+)", "27", null),
        new(TidalSource, "Tidal Hi-Res Lossless (24-bit/48kHz+)", "HI_RES_LOSSLESS", null),
        new(AppleSource, "Apple Music ALAC (lossless)", "ALAC", null),
        new(QobuzSource, "Qobuz FLAC 24-bit", "7", null),
        new(QobuzSource, "Qobuz FLAC 16-bit (CD)", "6", null),
        new(TidalSource, "Tidal Lossless 16-bit", "LOSSLESS", null),
        new(AmazonSource, "Amazon FLAC", "FLAC", null),
        new(DeezerSource, "Deezer FLAC", "9", DeezerFlac),
        new(AppleSource, "Apple Music AAC", "AAC", null),
        new(DeezerSource, "Deezer 320kbps", "3", DeezerMp3High),
        new(DeezerSource, "Deezer 128kbps", "1", DeezerMp3Low)
    ];

    public static string ResolveService(DeezSpoTagSettings settings)
    {
        var service = settings.Service?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(service))
        {
            return DeezerSource;
        }

        if (service == AutoService)
        {
            var firstAvailable = AutoPriority.FirstOrDefault(profile => IsSourceAvailable(profile.Source));
            return firstAvailable?.Source ?? DeezerSource;
        }

        return service;
    }

    public static List<string> ResolveAutoSources(bool includeDeezer)
    {
        // Back-compat: keep previous signature for call sites that have not yet been updated.
        // This intentionally excludes Apple because Apple availability is runtime-dependent (wrapper/token readiness),
        // not a persisted settings toggle.
        var settings = new DeezSpoTagSettings();
        return ResolveAutoSources(settings, includeDeezer);
    }

    public static List<string> ResolveAutoSources(DeezSpoTagSettings settings, bool includeDeezer)
    {
        var forcedService = settings.Service?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(forcedService) && forcedService != AutoService)
        {
            return CollapseAutoSourcesByService(BuildAutoSources(
                includeDeezer,
                profile => string.Equals(profile.Source, forcedService, StringComparison.OrdinalIgnoreCase)));
        }

        return CollapseAutoSourcesByService(BuildAutoSources(includeDeezer));
    }

    public static List<string> ResolveQualityAutoSources(
        DeezSpoTagSettings settings,
        bool includeDeezer,
        string? targetQuality)
    {
        var forcedService = settings.Service?.Trim().ToLowerInvariant();
        var includeAtmos = string.Equals(targetQuality, "atmos", StringComparison.OrdinalIgnoreCase);
        var sources = BuildAutoSources(
            includeDeezer,
            profile => ShouldIncludeQualityProfile(profile, forcedService, includeAtmos));

        if (string.IsNullOrWhiteSpace(targetQuality))
        {
            return sources;
        }

        return ApplyTargetQualityStart(sources, targetQuality);
    }

    private static List<string> ApplyTargetQualityStart(List<string> sources, string targetQuality)
    {
        var startIndex = sources.FindIndex(source =>
        {
            var step = DecodeAutoSource(source);
            return string.Equals(step.Quality, targetQuality, StringComparison.OrdinalIgnoreCase);
        });

        return startIndex >= 0 ? sources.Skip(startIndex).ToList() : sources;
    }

    public static List<string> ResolveEngineQualitySources(string engine, string? requestedQuality, bool strict)
    {
        var normalized = engine?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new List<string>();
        }

        var engineQualities = AutoPriority
            .Where(profile => string.Equals(profile.Source, normalized, StringComparison.OrdinalIgnoreCase))
            .Select(profile => profile.Quality)
            .Where(quality => !string.IsNullOrWhiteSpace(quality))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (strict)
        {
            var selected = string.IsNullOrWhiteSpace(requestedQuality)
                ? engineQualities.FirstOrDefault()
                : requestedQuality;
            if (string.IsNullOrWhiteSpace(selected))
            {
                return new List<string> { EncodeAutoSource(normalized, null) };
            }

            return new List<string> { EncodeAutoSource(normalized, selected) };
        }

        // Return qualities from the requested quality downward (lower quality),
        // following the engine's catalog order (index 0 = highest).
        var startIndex = 0;
        if (!string.IsNullOrWhiteSpace(requestedQuality))
        {
            var idx = engineQualities.FindIndex(q =>
                string.Equals(q, requestedQuality, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                startIndex = idx;
            }
        }

        var ordered = engineQualities.Skip(startIndex).ToList();
        if (ordered.Count == 0)
        {
            return new List<string> { EncodeAutoSource(normalized, null) };
        }

        return ordered
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(quality => EncodeAutoSource(normalized, quality))
            .ToList();
    }

    public static int ResolveDeezerBitrate(DeezSpoTagSettings settings, int requestedBitrate)
    {
        if (requestedBitrate > 0)
        {
            return requestedBitrate;
        }

        if (string.Equals(settings.Service, AutoService, StringComparison.OrdinalIgnoreCase))
        {
            var deezerProfile = AutoPriority.FirstOrDefault(profile => profile.Source == DeezerSource);
            return deezerProfile?.DeezerBitrate ?? DeezerFlac;
        }

        return settings.MaxBitrate > 0 ? settings.MaxBitrate : DeezerMp3Low;
    }

    private static bool IsSourceAvailable(string source)
    {
        if (source is "deezer" or "tidal" or "qobuz" or "amazon")
        {
            return true;
        }

        if (source == "apple")
        {
            // Apple wrapper readiness is tracked separately (platform auth + wrapper service),
            // so do not gate Apple behind a settings toggle here.
            return true;
        }

        return false;
    }

    private static List<string> BuildAutoSources(
        bool includeDeezer,
        Func<DownloadProfile, bool>? profileFilter = null)
    {
        var sources = new List<string>();
        foreach (var profile in AutoPriority)
        {
            if (!ShouldIncludeProfile(includeDeezer, profile, profileFilter))
            {
                continue;
            }

            sources.Add(EncodeAutoSource(profile.Source, profile.Quality));
        }

        return sources;
    }

    private static bool ShouldIncludeProfile(
        bool includeDeezer,
        DownloadProfile profile,
        Func<DownloadProfile, bool>? profileFilter)
    {
        if (!includeDeezer && profile.Source == DeezerSource)
        {
            return false;
        }

        if (!IsSourceAvailable(profile.Source))
        {
            return false;
        }

        return profileFilter?.Invoke(profile) ?? true;
    }

    private static bool ShouldIncludeQualityProfile(
        DownloadProfile profile,
        string? forcedService,
        bool includeAtmos)
    {
        if (!includeAtmos && string.Equals(profile.Quality, "atmos", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(forcedService) && forcedService != AutoService
            && !string.Equals(profile.Source, forcedService, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string? ResolvePreferredQuality(DeezSpoTagSettings settings, string engine)
    {
        if (settings == null || string.IsNullOrWhiteSpace(engine))
        {
            return null;
        }

        var normalized = engine.Trim().ToLowerInvariant();
        return normalized switch
        {
            AppleSource => settings.AppleMusic?.PreferredAudioProfile,
            DeezerSource => settings.MaxBitrate > 0 ? settings.MaxBitrate.ToString() : null,
            TidalSource => settings.TidalQuality,
            QobuzSource => settings.QobuzQuality,
            AmazonSource => "FLAC",
            _ => null
        };
    }

    public static string EncodeAutoSource(string source, string? quality)
    {
        return string.IsNullOrWhiteSpace(quality) ? source : $"{source}|{quality}";
    }

    public static AutoSourceStep DecodeAutoSource(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return new AutoSourceStep(string.Empty, null);
        }

        var parts = encoded.Split('|', 2, StringSplitOptions.TrimEntries);
        var source = parts.Length > 0 ? parts[0] : string.Empty;
        var quality = parts.Length > 1 ? parts[1] : null;
        return new AutoSourceStep(source, string.IsNullOrWhiteSpace(quality) ? null : quality);
    }

    public static List<string> CollapseAutoSourcesByService(List<string> autoSources)
    {
        if (autoSources == null || autoSources.Count == 0)
        {
            return autoSources ?? new List<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collapsed = new List<string>(autoSources.Count);

        foreach (var entry in autoSources)
        {
            var step = DecodeAutoSource(entry);
            if (string.IsNullOrWhiteSpace(step.Source))
            {
                continue;
            }

            var key = string.IsNullOrWhiteSpace(step.Quality)
                ? step.Source
                : $"{step.Source}|{step.Quality}";
            if (seen.Add(key))
            {
                collapsed.Add(entry);
            }
        }

        return collapsed;
    }

    public static int FindAutoIndex(List<string> autoSources, string engine, string? quality)
    {
        if (autoSources == null || autoSources.Count == 0)
        {
            return -1;
        }

        for (var i = 0; i < autoSources.Count; i++)
        {
            var step = DecodeAutoSource(autoSources[i]);
            if (!string.Equals(step.Source, engine, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(step.Quality) || string.IsNullOrWhiteSpace(quality))
            {
                return i;
            }

            if (string.Equals(step.Quality, quality, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    public static (int Index, string? Quality) ResolveInitialAutoStep(
        List<string> autoSources,
        string engine,
        string? requestedQuality)
    {
        var matchedIndex = FindAutoIndex(autoSources, engine, requestedQuality);
        if (matchedIndex >= 0)
        {
            var matchedStep = DecodeAutoSource(autoSources[matchedIndex]);
            return (matchedIndex, matchedStep.Quality ?? requestedQuality);
        }

        for (var i = 0; i < autoSources.Count; i++)
        {
            var step = DecodeAutoSource(autoSources[i]);
            if (string.Equals(step.Source, engine, StringComparison.OrdinalIgnoreCase))
            {
                return (i, step.Quality);
            }
        }

        return (-1, requestedQuality);
    }
}
