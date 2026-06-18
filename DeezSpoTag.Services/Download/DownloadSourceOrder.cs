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
    public sealed record DownloadEngineOrderValidationResult(bool IsValid, string? Error);

    // WARNING: Do not change this order and do not remove any items; fallback behavior depends on it.
    private static readonly DownloadProfile[] AutoPriority =
    [
        new(QobuzSource, "Max Hi-Res (24-bit/192kHz)", "27", null),
        new(TidalSource, "Max Hi-Res (24-bit/192kHz)", "HI_RES_LOSSLESS", null),
        new(AppleSource, "Apple Music ALAC (lossless)", "ALAC", null),
        new(QobuzSource, "Hi-Res (24-bit/96kHz)", "7", null),
        new(TidalSource, "Hi-Res (24-bit/96kHz)", "HI_RES", null),
        new(QobuzSource, "CD Lossless (16-bit/44.1kHz)", "6", null),
        new(TidalSource, "CD Lossless (16-bit/44.1kHz)", "LOSSLESS", null),
        new(AmazonSource, "Amazon FLAC", "FLAC", null),
        new(DeezerSource, "Deezer FLAC", "9", DeezerFlac),
        new(AppleSource, "Apple Music AAC", "AAC", null),
        new(QobuzSource, "MP3 (320kbps)", "5", null),
        new(TidalSource, "MP3 (320kbps)", "HIGH", null),
        new(DeezerSource, "Deezer 320kbps", "3", DeezerMp3High),
        new(DeezerSource, "Deezer 128kbps", "1", DeezerMp3Low),
        new(TidalSource, "Low (96kbps)", "LOW", null),
        new(AppleSource, "Apple Music Atmos", "ATMOS", null),
        new(TidalSource, "Tidal Dolby Atmos", "DOLBY_ATMOS", null)
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
            var firstAvailable = ResolveConfiguredProfiles(settings).FirstOrDefault(profile => IsSourceAvailable(profile.Source));
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
            return CollapseAutoSourcesByService(BuildConfiguredAutoSources(
                settings,
                includeDeezer,
                profile => string.Equals(profile.Source, forcedService, StringComparison.OrdinalIgnoreCase)));
        }

        return CollapseAutoSourcesByService(BuildConfiguredAutoSources(settings, includeDeezer));
    }

    public static List<string> ResolveQualityAutoSources(
        DeezSpoTagSettings settings,
        bool includeDeezer,
        string? targetQuality,
        string? forcedServiceOverride = null)
    {
        var forcedService = string.IsNullOrWhiteSpace(forcedServiceOverride)
            ? settings.Service?.Trim().ToLowerInvariant()
            : forcedServiceOverride.Trim().ToLowerInvariant();
        var includeAtmos = IsAtmosQuality(targetQuality);
        var sources = BuildConfiguredAutoSources(
            settings,
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
        return ResolveEngineQualitySources(null, engine, requestedQuality, strict);
    }

    public static List<string> ResolveEngineQualitySources(
        DeezSpoTagSettings? settings,
        string engine,
        string? requestedQuality,
        bool strict)
    {
        var normalized = engine?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new List<string>();
        }

        var engineQualities = ResolveConfiguredProfiles(settings)
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

    public static DownloadEngineOrderSettings NormalizeDownloadEngineOrderSettings(DownloadEngineOrderSettings? configured)
    {
        var defaults = DownloadEngineOrderSettings.CreateDefault();
        if (configured == null)
        {
            return defaults;
        }

        var normalized = new DownloadEngineOrderSettings
        {
            Enabled = configured.Enabled,
            Engines = new List<DownloadEngineOrderItem>()
        };

        var defaultByEngine = defaults.Engines.ToDictionary(
            item => NormalizeEngine(item.Engine),
            item => item,
            StringComparer.OrdinalIgnoreCase);
        var seenEngines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var incoming in configured.Engines ?? new List<DownloadEngineOrderItem>())
        {
            var engine = NormalizeEngine(incoming.Engine);
            if (!defaultByEngine.TryGetValue(engine, out var defaultEngine) || !seenEngines.Add(engine))
            {
                continue;
            }

            normalized.Engines.Add(new DownloadEngineOrderItem
            {
                Engine = engine,
                Enabled = incoming.Enabled,
                Qualities = NormalizeQualityItems(engine, incoming.Qualities, defaultEngine.Qualities)
            });
        }

        foreach (var defaultEngine in defaults.Engines)
        {
            var engine = NormalizeEngine(defaultEngine.Engine);
            if (!seenEngines.Add(engine))
            {
                continue;
            }

            normalized.Engines.Add(CloneEngineOrderItem(defaultEngine));
        }

        return normalized;
    }

    public static DownloadEngineOrderValidationResult ValidateDownloadEngineOrderSettings(DownloadEngineOrderSettings? configured)
    {
        if (configured?.Enabled == true)
        {
            var rawValidation = ValidateRawDownloadEngineOrderSettings(configured);
            if (!rawValidation.IsValid)
            {
                return rawValidation;
            }
        }

        var normalized = NormalizeDownloadEngineOrderSettings(configured);
        if (!normalized.Enabled)
        {
            return new DownloadEngineOrderValidationResult(true, null);
        }

        var enabledEngines = normalized.Engines.Where(engine => engine.Enabled).ToList();
        if (enabledEngines.Count == 0)
        {
            return new DownloadEngineOrderValidationResult(false, "Custom download engine order requires at least one enabled engine.");
        }

        var engineWithoutEnabledQuality = enabledEngines.FirstOrDefault(engine =>
            engine.Qualities == null || !engine.Qualities.Any(quality => quality.Enabled));
        if (engineWithoutEnabledQuality != null)
        {
            return new DownloadEngineOrderValidationResult(
                false,
                $"Custom download engine order requires at least one enabled quality for {GetDisplayName(engineWithoutEnabledQuality.Engine)}.");
        }

        return new DownloadEngineOrderValidationResult(true, null);
    }

    private static DownloadEngineOrderValidationResult ValidateRawDownloadEngineOrderSettings(DownloadEngineOrderSettings configured)
    {
        if (configured.Engines == null || configured.Engines.Count == 0)
        {
            return new DownloadEngineOrderValidationResult(false, "Custom download engine order requires configured engines.");
        }

        var defaults = DownloadEngineOrderSettings.CreateDefault();
        var defaultByEngine = defaults.Engines.ToDictionary(
            engine => NormalizeEngine(engine.Engine),
            engine => engine,
            StringComparer.OrdinalIgnoreCase);
        var seenEngines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var engine in configured.Engines)
        {
            var normalizedEngine = NormalizeEngine(engine.Engine);
            if (!defaultByEngine.TryGetValue(normalizedEngine, out var defaultEngine))
            {
                return new DownloadEngineOrderValidationResult(false, "Custom download engine order contains an unknown engine.");
            }

            if (!seenEngines.Add(normalizedEngine))
            {
                return new DownloadEngineOrderValidationResult(false, $"Custom download engine order contains duplicate {GetDisplayName(normalizedEngine)} entries.");
            }

            var qualityValidation = ValidateRawQualityItems(normalizedEngine, engine.Qualities, defaultEngine.Qualities);
            if (!qualityValidation.IsValid)
            {
                return qualityValidation;
            }
        }

        return new DownloadEngineOrderValidationResult(true, null);
    }

    private static DownloadEngineOrderValidationResult ValidateRawQualityItems(
        string engine,
        List<DownloadEngineQualityItem>? configuredQualities,
        IReadOnlyList<DownloadEngineQualityItem> defaultQualities)
    {
        if (configuredQualities == null || configuredQualities.Count == 0)
        {
            return new DownloadEngineOrderValidationResult(false, $"Custom download engine order requires qualities for {GetDisplayName(engine)}.");
        }

        var validQualities = new HashSet<string>(
            defaultQualities.Select(quality => NormalizeQuality(engine, quality.Quality)),
            StringComparer.OrdinalIgnoreCase);
        var seenQualities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var normalizedQualities = configuredQualities
            .Select(quality => NormalizeQuality(engine, quality.Quality));

        foreach (var normalizedQuality in normalizedQualities)
        {
            if (!validQualities.Contains(normalizedQuality))
            {
                return new DownloadEngineOrderValidationResult(false, $"Custom download engine order contains an unknown {GetDisplayName(engine)} quality.");
            }

            if (!seenQualities.Add(normalizedQuality))
            {
                return new DownloadEngineOrderValidationResult(false, $"Custom download engine order contains duplicate {GetDisplayName(engine)} quality entries.");
            }
        }

        return new DownloadEngineOrderValidationResult(true, null);
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

    private static List<string> BuildConfiguredAutoSources(
        DeezSpoTagSettings settings,
        bool includeDeezer,
        Func<DownloadProfile, bool>? profileFilter = null)
    {
        var profiles = ResolveConfiguredProfiles(settings);
        var sources = new List<string>();
        foreach (var profile in profiles)
        {
            if (!ShouldIncludeProfile(includeDeezer, profile, profileFilter))
            {
                continue;
            }

            sources.Add(EncodeAutoSource(profile.Source, profile.Quality));
        }

        return sources;
    }

    private static IReadOnlyList<DownloadProfile> ResolveConfiguredProfiles(DeezSpoTagSettings? settings)
    {
        if (settings?.DownloadEngineOrder?.Enabled != true)
        {
            return AutoPriority;
        }

        var normalized = NormalizeDownloadEngineOrderSettings(settings.DownloadEngineOrder);
        var profiles = new List<DownloadProfile>();
        foreach (var engine in normalized.Engines)
        {
            if (!engine.Enabled)
            {
                continue;
            }

            foreach (var quality in engine.Qualities)
            {
                if (!quality.Enabled)
                {
                    continue;
                }

                var source = NormalizeEngine(engine.Engine);
                var normalizedQuality = NormalizeQuality(source, quality.Quality);
                var profile = AutoPriority.FirstOrDefault(candidate =>
                    string.Equals(candidate.Source, source, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.Quality, normalizedQuality, StringComparison.OrdinalIgnoreCase));
                if (profile != null)
                {
                    profiles.Add(profile);
                }
            }
        }

        return profiles;
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

    private static List<DownloadEngineQualityItem> NormalizeQualityItems(
        string engine,
        IEnumerable<DownloadEngineQualityItem>? configuredQualities,
        IReadOnlyList<DownloadEngineQualityItem> defaultQualities)
    {
        var defaultQualityByValue = defaultQualities.ToDictionary(
            item => NormalizeQuality(engine, item.Quality),
            item => item,
            StringComparer.OrdinalIgnoreCase);
        var seenQualities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<DownloadEngineQualityItem>();

        foreach (var incoming in configuredQualities ?? Enumerable.Empty<DownloadEngineQualityItem>())
        {
            var quality = NormalizeQuality(engine, incoming.Quality);
            if (!defaultQualityByValue.TryGetValue(quality, out _) || !seenQualities.Add(quality))
            {
                continue;
            }

            normalized.Add(new DownloadEngineQualityItem
            {
                Quality = quality,
                Enabled = incoming.Enabled
            });
        }

        foreach (var defaultQuality in defaultQualities)
        {
            var quality = NormalizeQuality(engine, defaultQuality.Quality);
            if (!seenQualities.Add(quality))
            {
                continue;
            }

            normalized.Add(new DownloadEngineQualityItem
            {
                Quality = quality,
                Enabled = defaultQuality.Enabled
            });
        }

        return normalized;
    }

    private static DownloadEngineOrderItem CloneEngineOrderItem(DownloadEngineOrderItem source)
    {
        return new DownloadEngineOrderItem
        {
            Engine = NormalizeEngine(source.Engine),
            Enabled = source.Enabled,
            Qualities = source.Qualities
                .Select(quality => new DownloadEngineQualityItem
                {
                    Quality = NormalizeQuality(source.Engine, quality.Quality),
                    Enabled = quality.Enabled
                })
                .ToList()
        };
    }

    private static string NormalizeEngine(string? engine)
    {
        var normalized = engine?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "applemusic" or "apple-music" or "apple_music" => AppleSource,
            "amazonmusic" or "amazon-music" or "amazon_music" => AmazonSource,
            _ => normalized
        };
    }

    private static string NormalizeQuality(string? engine, string? quality)
    {
        var source = NormalizeEngine(engine);
        var normalized = quality?.Trim() ?? string.Empty;
        if (string.Equals(source, TidalSource, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, AppleSource, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, AmazonSource, StringComparison.OrdinalIgnoreCase))
        {
            return normalized.ToUpperInvariant();
        }

        return normalized;
    }

    private static string GetDisplayName(string? engine)
    {
        return NormalizeEngine(engine) switch
        {
            QobuzSource => "Qobuz",
            TidalSource => "Tidal",
            AppleSource => "Apple Music",
            AmazonSource => "Amazon Music",
            DeezerSource => "Deezer",
            _ => "the selected engine"
        };
    }

    private static bool ShouldIncludeQualityProfile(
        DownloadProfile profile,
        string? forcedService,
        bool includeAtmos)
    {
        if (!includeAtmos && IsAtmosQuality(profile.Quality))
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

    private static bool IsAtmosQuality(string? quality)
        => !string.IsNullOrWhiteSpace(quality)
           && quality.Contains("ATMOS", StringComparison.OrdinalIgnoreCase);

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
