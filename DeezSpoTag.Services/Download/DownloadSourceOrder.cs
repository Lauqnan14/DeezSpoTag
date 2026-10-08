using System.Linq;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Soulseek;

namespace DeezSpoTag.Services.Download;

public static class DownloadSourceOrder
{
    private const string AutoService = "auto";
    private const string CustomService = "custom";
    private const string DeezerSource = "deezer";
    private const string QobuzSource = "qobuz";
    private const string TidalSource = "tidal";
    private const string AppleSource = "apple";
    private const string AmazonSource = "amazon";
    private const string SoulseekSource = "soulseek";
    private const string SoundCloudSource = "soundcloud";
    public const int DeezerFlac = 9;
    public const int DeezerMp3High = 3;
    public const int DeezerMp3Low = 1;

    public readonly record struct AutoSourceStep(string Source, string? Quality);

    private sealed record DownloadProfile(string Source, string Label, string? Quality, int? DeezerBitrate);
    public sealed record DownloadEngineOrderValidationResult(bool IsValid, string? Error);

    /// <summary>
    ///     The stereo ladder Auto mode walks, best quality first.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Soulseek is woven in rather than appended. Each of its steps sits immediately after the
    ///         catalogue steps of the <em>same</em> quality, so a peer search is only attempted once every
    ///         service that can deliver that exact tier has been tried. Appending Soulseek as a block at the
    ///         bottom would instead make a 24/192 request walk past 96kbps catalogue steps before considering
    ///         a peer that had exactly what was asked for.
    ///     </para>
    ///     <para>
    ///         The catalogue order is unchanged from the ladder that shipped before Soulseek existed.
    ///     </para>
    /// </remarks>
    private static readonly DownloadProfile[] StereoLadder =
    [
        // 24/192. Qobuz and Tidal first, then a peer.
        new(QobuzSource, "Max Hi-Res (24-bit/192kHz)", "27", null),
        new(TidalSource, "Max Hi-Res (24-bit/192kHz)", "HI_RES_LOSSLESS", null),
        new(SoulseekSource, "Max Hi-Res (24-bit/192kHz)", "FLAC_HI_RES_LOSSLESS", null),

        // 24/96. Amazon's Ultra HD FLAC is the same tier, so it stays ahead of the peer step.
        new(QobuzSource, "Hi-Res (24-bit/96kHz)", "7", null),
        new(TidalSource, "Hi-Res (24-bit/96kHz)", "HI_RES", null),
        new(AmazonSource, "Ultra HD FLAC", "ULTRA_HD_FLAC", null),
        new(SoulseekSource, "Hi-Res (24-bit/96kHz)", "FLAC_HI_RES", null),

        // Lossless. Apple Music's ALAC keeps its original position, ahead of the CD-lossless steps, so the
        // catalogue order is untouched and a request for ALAC still seeks straight to it. Soulseek's FLAC is
        // 16/44.1 and its LOSSLESS covers ALAC, WAV and APE, so both sit at the bottom of the lossless group,
        // with the stricter FLAC request first.
        new(AppleSource, "Apple Music ALAC (lossless)", "ALAC", null),
        new(QobuzSource, "CD Lossless (16-bit/44.1kHz)", "6", null),
        new(TidalSource, "CD Lossless (16-bit/44.1kHz)", "LOSSLESS", null),
        new(AmazonSource, "HD FLAC", "HD_FLAC", null),
        new(DeezerSource, "Deezer FLAC", "9", DeezerFlac),
        new(SoulseekSource, "FLAC (16-bit/44.1kHz)", "FLAC", null),
        new(SoulseekSource, "Lossless (FLAC, ALAC, WAV, APE)", "LOSSLESS", null),

        // 320kbps, then the lossy tail.
        new(AppleSource, "Apple Music AAC", "AAC", null),
        new(QobuzSource, "MP3 (320kbps)", "5", null),
        new(TidalSource, "MP3 (320kbps)", "HIGH", null),
        new(DeezerSource, "Deezer 320kbps", "3", DeezerMp3High),

        // SoundCloud's HQ is a 256kbps MP3 tier, which ranks alongside the 320kbps steps: it is the highest
        // SoundCloud advertises, so it belongs with them rather than anywhere in the sub-256 rungs below. It
        // is a Go+ stream advertised only to an entitled account, so authentication alone does not reach it;
        // the ladder position is static regardless, and an unadvertised tier is not offered at download time.
        new(SoundCloudSource, "SoundCloud HQ (256kbps)", "HQ", null),
        new(SoulseekSource, "MP3 320 kbps", "MP3_320", null),
        new(SoulseekSource, "MP3 256 kbps", "MP3_256", null),

        // Opus is a lossy tier, so it must sit below every 320kbps step. It used to sit above
        // Deezer 320kbps, which made the ladder step back up in quality: the plan would take a
        // ~96-160kbps Opus file while a 320kbps MP3 step was still waiting further down.
        new(AmazonSource, "Opus", "OPUS", null),
        new(SoulseekSource, "MP3 192 kbps", "MP3_192", null),
        new(DeezerSource, "Deezer 128kbps", "1", DeezerMp3Low),
        new(SoundCloudSource, "SoundCloud Standard (128kbps)", "SQ", null),
        new(SoulseekSource, "MP3 128 kbps", "MP3_128", null),
        new(TidalSource, "Low (96kbps)", "LOW", null),

        // Only reachable when the user has explicitly allowed unknown-quality candidates; the scoring
        // service rejects them otherwise.
        new(SoulseekSource, "Unknown quality", "UNKNOWN", null),

        // SoundCloud's 64kbps tier is worse than every rung above it, including the unknown-quality peer step,
        // so it sits last. Placing it anywhere higher would let a 64kbps file satisfy a step that promised
        // more than that, which is the inversion this ladder exists to prevent.
        new(SoundCloudSource, "SoundCloud Low (64kbps)", "LQ", null)
    ];

    /// <summary>
    ///     The catalogue-only portion of <see cref="StereoLadder"/>, in the same order.
    /// </summary>
    /// <remarks>
    ///     Derived rather than hand-maintained so the two cannot drift apart. Used where Soulseek must not
    ///     appear: the public-API readiness check, and resolving Deezer's bitrate for a quality label.
    /// </remarks>
    private static readonly DownloadProfile[] StereoPriority =
        StereoLadder.Where(profile => profile.Source != SoulseekSource).ToArray();

    private static readonly DownloadProfile[] AtmosPriority =
    [
        new(AppleSource, "Apple Music Atmos", "ATMOS", null),
        new(TidalSource, "Tidal Dolby Atmos", "DOLBY_ATMOS", null),
        new(AmazonSource, "Amazon Dolby Atmos", "DOLBY_ATMOS", null)
    ];

    /// <summary>
    ///     Soulseek's own steps, in the order they appear on the ladder.
    /// </summary>
    private static readonly DownloadProfile[] SoulseekPriority =
        StereoLadder.Where(profile => profile.Source == SoulseekSource).ToArray();

    /// <summary>
    ///     Every profile the engine can be configured with, in the order they are offered.
    /// </summary>
    private static readonly DownloadProfile[] KnownProfiles = StereoLadder.Concat(AtmosPriority).ToArray();

    /// <summary>
    ///     Resolves the configured service setting to a concrete source, defaulting to Deezer when
    ///     nothing is configured or automatic selection has nothing available.
    /// </summary>
    /// <param name="soulseekEligible">
    ///     Whether Soulseek has a verified login right now. Defaults to <see langword="true"/>, which is the
    ///     pre-existing behaviour: a caller that has not checked keeps Soulseek in the plan and the admission
    ///     gate reports the reason precisely if the download then cannot start.
    /// </param>
    public static string ResolveService(DeezSpoTagSettings settings, bool soulseekEligible = true)
    {
        var service = settings.Service?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(service))
        {
            return DeezerSource;
        }

        if (service is AutoService or CustomService)
        {
            var firstAvailable = ResolveConfiguredProfiles(settings).FirstOrDefault(profile => IsSourceAvailable(profile.Source, soulseekEligible));
            return firstAvailable?.Source ?? DeezerSource;
        }

        return service;
    }

    /// <summary>
    ///     Resolves the automatic source list from default settings, kept as a compatibility overload
    ///     for call sites that have no settings of their own to pass.
    /// </summary>
    /// <param name="soulseekEligible">Whether Soulseek has a verified login right now.</param>
    public static List<string> ResolveAutoSources(bool includeDeezer, bool soulseekEligible = true)
    {
        // Back-compat: keep previous signature for call sites that have not yet been updated.
        // This intentionally excludes Apple because Apple availability is runtime-dependent (wrapper/token readiness),
        // not a persisted settings toggle.
        var settings = new DeezSpoTagSettings();
        return ResolveAutoSources(settings, includeDeezer, soulseekEligible);
    }

    /// <summary>
    ///     Resolves the ordered automatic source list for the given settings, honouring a forced
    ///     service when one is configured and the caller's Soulseek eligibility either way.
    /// </summary>
    /// <param name="soulseekEligible">
    ///     Whether Soulseek has a verified login right now. Defaults to <see langword="true"/> so a caller that
    ///     has not checked keeps today's behaviour; a caller that has checked gets a plan without Soulseek in
    ///     it when the source is not usable. Saved order and quality choices are untouched either way - the
    ///     step is simply not offered.
    /// </param>
    public static List<string> ResolveAutoSources(
        DeezSpoTagSettings settings,
        bool includeDeezer,
        bool soulseekEligible = true)
    {
        var forcedService = settings.Service?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(forcedService)
            && forcedService != AutoService
            && forcedService != CustomService)
        {
            return CollapseAutoSourcesByService(BuildConfiguredAutoSources(
                settings,
                includeDeezer,
                profile => string.Equals(profile.Source, forcedService, StringComparison.OrdinalIgnoreCase),
                soulseekEligible: soulseekEligible));
        }

        return CollapseAutoSourcesByService(BuildConfiguredAutoSources(
            settings,
            includeDeezer,
            soulseekEligible: soulseekEligible));
    }

    /// <summary>
    ///     Resolves the automatic sources limited to the requested quality tier, then trims the
    ///     list so it starts at the requested quality step.
    /// </summary>
    /// <param name="soulseekEligible">Whether Soulseek has a verified login right now.</param>
    public static List<string> ResolveQualityAutoSources(
        DeezSpoTagSettings settings,
        bool includeDeezer,
        string? targetQuality,
        string? forcedServiceOverride = null,
        bool soulseekEligible = true)
    {
        var forcedService = string.IsNullOrWhiteSpace(forcedServiceOverride)
            ? settings.Service?.Trim().ToLowerInvariant()
            : forcedServiceOverride.Trim().ToLowerInvariant();
        var includeAtmos = IsAtmosQuality(targetQuality);

        // A concrete engine selected by the user must resolve even though it is not in StereoPriority. This
        // is what makes Soulseek (and any future opt-in engine) selectable directly from the source picker
        // rather than only through a Custom engine order. Auto mode is unaffected because it has no forced
        // engine and keeps using StereoPriority/AtmosPriority verbatim.
        var hasForcedEngine = !string.IsNullOrWhiteSpace(forcedService)
            && forcedService != AutoService
            && forcedService != CustomService;
        var qualityProfiles = settings.DownloadEngineOrder?.Enabled == true
            ? ResolveConfiguredProfiles(settings)
                .Where(profile => IsAtmosQuality(profile.Quality) == includeAtmos)
                .ToArray()
            : hasForcedEngine
                ? KnownProfiles
                    .Where(profile => IsAtmosQuality(profile.Quality) == includeAtmos)
                    .ToArray()
                : includeAtmos
                    ? AtmosPriority
                    : StereoLadder;
        var sources = BuildConfiguredAutoSources(
            settings,
            includeDeezer,
            profile => ShouldIncludeQualityProfile(profile, forcedService),
            qualityProfiles,
            soulseekEligible);

        if (string.IsNullOrWhiteSpace(targetQuality))
        {
            return sources;
        }

        return ApplyTargetQualityStart(sources, targetQuality);
    }

    public static List<string> ResolveAtmosSources(
        DeezSpoTagSettings settings,
        string? preferredEngine)
    {
        var enabledProfiles = settings.DownloadEngineOrder?.Enabled == true
            ? ResolveConfiguredProfiles(settings)
            : AtmosPriority;
        var enabledAtmosProfiles = enabledProfiles
            .Where(profile => IsAtmosQuality(profile.Quality))
            .ToList();
        var normalizedPreferred = NormalizeEngine(preferredEngine);
        var preferred = enabledAtmosProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Source, normalizedPreferred, StringComparison.OrdinalIgnoreCase));

        if (settings.MultiQuality?.AtmosFallbackEnabled != true)
        {
            return preferred == null
                ? new List<string>()
                : new List<string> { EncodeAutoSource(preferred.Source, preferred.Quality) };
        }

        return enabledAtmosProfiles
            .OrderByDescending(profile => preferred != null
                && string.Equals(profile.Source, preferred.Source, StringComparison.OrdinalIgnoreCase))
            .Select(profile => EncodeAutoSource(profile.Source, profile.Quality))
            .ToList();
    }

    private static List<string> ApplyTargetQualityStart(List<string> sources, string targetQuality)
    {
        var startIndex = sources.FindIndex(source =>
        {
            var step = DecodeAutoSource(source);

            // Soulseek's codes are deliberately not linked into the shared quality tier table, so a requested
            // target must never match one. Without this a request for "flac" would land on Soulseek's FLAC step
            // part-way down the ladder and skip every catalogue engine above it.
            return !string.Equals(step.Source, SoulseekSource, StringComparison.OrdinalIgnoreCase)
                && string.Equals(step.Quality, targetQuality, StringComparison.OrdinalIgnoreCase);
        });

        return startIndex >= 0 ? sources.Skip(startIndex).ToList() : sources;
    }

    /// <summary>
    ///     Resolves the quality steps one engine contributes when there is no settings object to
    ///     read, delegating to the settings-aware overload.
    /// </summary>
    /// <param name="soulseekEligible">Whether Soulseek has a verified login right now.</param>
    public static List<string> ResolveEngineQualitySources(
        string engine,
        string? requestedQuality,
        bool strict,
        bool soulseekEligible = true)
    {
        return ResolveEngineQualitySources(null, engine, requestedQuality, strict, soulseekEligible);
    }

    /// <summary>
    ///     Resolves the ordered quality steps one engine contributes: the single requested step in
    ///     strict mode, or every step from the requested quality downward otherwise.
    /// </summary>
    /// <param name="soulseekEligible">
    ///     Whether Soulseek has a verified login right now. When it does not, asking for Soulseek produces no
    ///     steps: an empty list, never a differently-ordered one. The caller decides what an absent engine
    ///     means, which is what keeps a pinned request from quietly becoming a different engine's work.
    /// </param>
    public static List<string> ResolveEngineQualitySources(
        DeezSpoTagSettings? settings,
        string engine,
        string? requestedQuality,
        bool strict,
        bool soulseekEligible = true)
    {
        var normalized = engine?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new List<string>();
        }

        if (string.Equals(normalized, SoulseekSource, StringComparison.OrdinalIgnoreCase) && !soulseekEligible)
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

    /// <summary>
    ///     Resolves the source list a fallback plan runs from: the steps the requested engine and
    ///     every automatic source contribute, or the caller's automatic list when none yield one.
    /// </summary>
    /// <param name="soulseekEligible">
    ///     Whether Soulseek has a verified login right now. A plan persisted while it was logged in can outlive
    ///     that login, so the verdict is applied again here rather than trusting the stored step list.
    /// </param>
    public static List<String> ResolveFallbackPlanSources(
        DeezSpoTagSettings settings,
        IReadOnlyList<string> autoSources,
        string engine,
        string? requestedQuality,
        bool strict,
        bool includeDeezer,
        bool soulseekEligible = true)
    {
        var service = settings.Service?.Trim().ToLowerInvariant();
        if (string.Equals(service, AutoService, StringComparison.OrdinalIgnoreCase)
            || string.Equals(service, CustomService, StringComparison.OrdinalIgnoreCase))
        {
            return CollapseAutoSourcesByService(
                ResolveQualityAutoSources(settings, includeDeezer, requestedQuality, soulseekEligible: soulseekEligible));
        }

        var planSources = new List<string>();
        if (!string.IsNullOrWhiteSpace(engine))
        {
            planSources.AddRange(ResolveEngineQualitySources(settings, engine, requestedQuality, strict, soulseekEligible));
        }

        var seenEngines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(engine))
        {
            seenEngines.Add(engine);
        }

        foreach (var decoded in autoSources.Select(DecodeAutoSource))
        {
            if (string.IsNullOrWhiteSpace(decoded.Source) || seenEngines.Contains(decoded.Source))
            {
                continue;
            }

            seenEngines.Add(decoded.Source);
            planSources.AddRange(ResolveEngineQualitySources(settings, decoded.Source, decoded.Quality, strict, soulseekEligible));
        }

        // Only a plan that filtered Soulseek out is allowed to fall back to the caller's list wholesale.
        // Restoring it there would put back the step that was just removed, which is how an inactive source
        // reappears in a plan that was built without it.
        var soulseekRemoved = !soulseekEligible
            && (autoSources.Any(source => IsSoulseekSource(source)) || IsSoulseekSource(engine));
        if (planSources.Count == 0 && autoSources.Count > 0 && !soulseekRemoved)
        {
            planSources.AddRange(autoSources);
        }

        return CollapseAutoSourcesByService(planSources);
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

            var appended = CloneEngineOrderItem(defaultEngine);

            // An engine the saved order predates is appended disabled, not enabled.
            //
            // A saved order is the reader's own statement about which engines to use. Silently enabling a
            // newly-shipped engine would add it to their fallback walk without their asking, and if it is not
            // reachable - no credential, a host it cannot reach - every download that used to succeed would
            // start failing at that step. Disabled means the behaviour they saved is what they keep, and the
            // engine becomes available in Settings for them to turn on.
            appended.Enabled = false;
            foreach (var quality in appended.Qualities)
            {
                quality.Enabled = false;
            }

            normalized.Engines.Add(appended);
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

        var normalized = NormalizeDownloadEngineOrderSettingsForExecution(configured);
        if (!normalized.Enabled)
        {
            return new DownloadEngineOrderValidationResult(true, null);
        }

        var enabledEngines = normalized.Engines.Where(engine => engine.Enabled).ToList();
        if (enabledEngines.Count == 0)
        {
            return new DownloadEngineOrderValidationResult(false, "Custom download selection requires at least one enabled engine.");
        }

        var engineWithoutEnabledQuality = enabledEngines.FirstOrDefault(engine =>
            engine.Qualities == null || !engine.Qualities.Any(quality => quality.Enabled));
        if (engineWithoutEnabledQuality != null)
        {
            return new DownloadEngineOrderValidationResult(
                false,
                $"Custom download selection requires at least one enabled quality for {GetDisplayName(engineWithoutEnabledQuality.Engine)}.");
        }

        return new DownloadEngineOrderValidationResult(true, null);
    }

    private static DownloadEngineOrderValidationResult ValidateRawDownloadEngineOrderSettings(DownloadEngineOrderSettings configured)
    {
        if (configured.Engines == null || configured.Engines.Count == 0)
        {
            return new DownloadEngineOrderValidationResult(false, "Custom download selection requires configured engines.");
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
                return new DownloadEngineOrderValidationResult(false, "Custom download selection contains an unknown engine.");
            }

            if (!seenEngines.Add(normalizedEngine))
            {
                return new DownloadEngineOrderValidationResult(false, $"Custom download selection contains duplicate {GetDisplayName(normalizedEngine)} entries.");
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
            return new DownloadEngineOrderValidationResult(false, $"Custom download selection requires qualities for {GetDisplayName(engine)}.");
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
                return new DownloadEngineOrderValidationResult(false, $"Custom download selection contains an unknown {GetDisplayName(engine)} quality.");
            }

            if (!seenQualities.Add(normalizedQuality))
            {
                return new DownloadEngineOrderValidationResult(false, $"Custom download selection contains duplicate {GetDisplayName(engine)} quality entries.");
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

        if (string.Equals(settings.Service, AutoService, StringComparison.OrdinalIgnoreCase)
            || string.Equals(settings.Service, CustomService, StringComparison.OrdinalIgnoreCase))
        {
            var deezerProfile = StereoPriority.FirstOrDefault(profile => profile.Source == DeezerSource);
            return deezerProfile?.DeezerBitrate ?? DeezerFlac;
        }

        return settings.MaxBitrate > 0 ? settings.MaxBitrate : DeezerMp3Low;
    }

    /// <summary>Whether an encoded step or engine name refers to Soulseek.</summary>
    private static bool IsSoulseekSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var decoded = DecodeAutoSource(value);
        return string.Equals(decoded.Source, SoulseekSource, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSourceAvailable(string source, bool soulseekEligible)
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

        if (source == SoundCloudSource)
        {
            // SoundCloud needs no configuration to serve public tracks, so it is always available. Private
            // tracks and the advertised hq tier need a saved OAuth token, and the download service reports a
            // precise failure for those rather than the engine being silently skipped here.
            return true;
        }

        if (source == SoulseekSource)
        {
            // Unlike the others, Soulseek is not usable merely because it is selected: it needs a verified
            // login, and that is a question about the network rather than about settings.
            //
            // The verdict is passed in rather than probed here, because this helper stays pure and a plan is
            // built on a background worker where a network round trip would be both slow and surprising. A
            // caller that has not verified a login passes false, which omits Soulseek from the plan - the
            // same thing that happens when the reader has deselected it. The step is not silently reordered
            // or swapped for another engine; it is absent, and the fallback machinery below does what it
            // already does for any engine with nothing to offer.
            return soulseekEligible;
        }

        return false;
    }

    private static List<string> BuildConfiguredAutoSources(
        DeezSpoTagSettings settings,
        bool includeDeezer,
        Func<DownloadProfile, bool>? profileFilter = null,
        IReadOnlyList<DownloadProfile>? explicitProfiles = null,
        bool soulseekEligible = true)
    {
        var profiles = explicitProfiles ?? ResolveConfiguredProfiles(settings);
        var sources = new List<string>();
        foreach (var profile in profiles)
        {
            if (!ShouldIncludeProfile(includeDeezer, profile, profileFilter, soulseekEligible))
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
            // The full ladder, so Auto mode reaches Soulseek at its per-tier position. Callers that must not
            // see Soulseek - the public-API readiness check - filter by engine themselves.
            return StereoLadder;
        }

        var normalized = NormalizeDownloadEngineOrderSettingsForExecution(settings.DownloadEngineOrder);
        var enabledProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                enabledProfiles.Add(BuildProfileKey(source, normalizedQuality));
            }
        }

        return KnownProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Quality)
                && enabledProfiles.Contains(BuildProfileKey(profile.Source, profile.Quality)))
            .ToArray();
    }

    private static string BuildProfileKey(string source, string quality)
        => $"{NormalizeEngine(source)}|{NormalizeQuality(source, quality)}";

    private static bool ShouldIncludeProfile(
        bool includeDeezer,
        DownloadProfile profile,
        Func<DownloadProfile, bool>? profileFilter,
        bool soulseekEligible)
    {
        if (!includeDeezer && profile.Source == DeezerSource)
        {
            return false;
        }

        if (!IsSourceAvailable(profile.Source, soulseekEligible))
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

    private static DownloadEngineOrderSettings NormalizeDownloadEngineOrderSettingsForExecution(
        DownloadEngineOrderSettings? configured)
    {
        if (configured == null)
        {
            return DownloadEngineOrderSettings.CreateDefault();
        }

        if (!configured.Enabled)
        {
            return NormalizeDownloadEngineOrderSettings(configured);
        }

        var defaults = DownloadEngineOrderSettings.CreateDefault();
        var defaultByEngine = defaults.Engines.ToDictionary(
            item => NormalizeEngine(item.Engine),
            item => item,
            StringComparer.OrdinalIgnoreCase);
        var seenEngines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new DownloadEngineOrderSettings
        {
            Enabled = true,
            Engines = new List<DownloadEngineOrderItem>()
        };

        foreach (var incoming in configured.Engines ?? Enumerable.Empty<DownloadEngineOrderItem>())
        {
            var engine = NormalizeEngine(incoming.Engine);
            if (!defaultByEngine.TryGetValue(engine, out var defaultEngine) || !seenEngines.Add(engine))
            {
                continue;
            }

            var validQualities = defaultEngine.Qualities
                .Select(quality => NormalizeQuality(engine, quality.Quality))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seenQualities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var qualities = new List<DownloadEngineQualityItem>();
            foreach (var incomingQuality in incoming.Qualities ?? Enumerable.Empty<DownloadEngineQualityItem>())
            {
                var quality = NormalizeQuality(engine, incomingQuality.Quality);
                if (!validQualities.Contains(quality) || !seenQualities.Add(quality))
                {
                    continue;
                }

                qualities.Add(new DownloadEngineQualityItem
                {
                    Quality = quality,
                    Enabled = incomingQuality.Enabled
                });
            }

            normalized.Engines.Add(new DownloadEngineOrderItem
            {
                Engine = engine,
                Enabled = incoming.Enabled,
                Qualities = qualities
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

    /// <summary>
    ///     Returns the Soulseek quality codes the user currently has enabled, in ladder order.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the one place that answers "which qualities is Soulseek configured for", so the
    ///         downloader and the search tab cannot disagree. It honours the source setting the same way the
    ///         downloader does: Auto and Custom resolve through the ladder, and a specific engine resolves
    ///         through its own configured qualities.
    ///     </para>
    ///     <para>
    ///         A custom order that ticks no Soulseek quality yields nothing, so the search tab shows no
    ///         group for it rather than offering one the download would never use.
    ///     </para>
    /// </remarks>
    public static List<string> ResolveEnabledSoulseekQualities(DeezSpoTagSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var service = settings.Service?.Trim().ToLowerInvariant();
        var sources = service is SoulseekSource
            ? ResolveEngineQualitySources(settings, SoulseekSource, requestedQuality: null, strict: false)
            : ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null);

        var codes = sources
            .Select(DecodeAutoSource)
            .Where(step => string.Equals(step.Source, SoulseekSource, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(step.Quality))
            .Select(step => NormalizeQuality(SoulseekSource, step.Quality))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return codes;
    }

    /// <summary>
    ///     Returns the Soulseek qualities the search tab renders as groups, in ladder order.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is a display projection of <see cref="ResolveEnabledSoulseekQualities"/>, not a second
    ///         answer about what is enabled. The downloader keeps reading that method unchanged, so the tab
    ///         can never widen what the download will take; this only decides how much of the ladder the tab
    ///         shows the user.
    ///     </para>
    ///     <para>
    ///         An empty selection and a complete selection both mean "no restriction" to whoever is reading
    ///         the tab, so both render the whole ladder: nothing ticked means every quality is acceptable,
    ///         and everything ticked is the same statement. A partial Custom selection renders only the
    ///         ticked qualities, so the tab never offers a step the download would refuse.
    ///     </para>
    ///     <para>
    ///         Unknown quality is the last rung of the ladder and it is only walked when the user has
    ///         switched it on, so it is rendered as a trailing group exactly when it is allowed and is absent
    ///         entirely when it is not. Rendering it unconditionally used to leave a group of files the
    ///         download would refuse sitting in the tab, which is a step the ladder will not take.
    ///     </para>
    /// </remarks>
    /// <param name="enabledCodes">What <see cref="ResolveEnabledSoulseekQualities"/> resolved.</param>
    /// <param name="allowUnknownQuality">Whether Soulseek is configured to accept undetermined quality.</param>
    /// <returns>The codes to render as groups, in ladder order. Never <see langword="null"/>.</returns>
    public static IReadOnlyList<string> ResolveDisplayQualityGroups(
        IReadOnlyList<string> enabledCodes,
        bool allowUnknownQuality = false)
    {
        ArgumentNullException.ThrowIfNull(enabledCodes);

        // SoulseekPriority is StereoLadder filtered to Soulseek, so the rendering order is literally the
        // order the download walks rather than a second hand-maintained list that could drift from it.
        // The undetermined rung is set aside: it is not a tier, it is what is left when no tier applies, so
        // it is decided by its own setting rather than by which tiers happen to be ticked.
        var ladder = SoulseekPriority
            .Select(profile => NormalizeQuality(SoulseekSource, profile.Quality))
            .Where(code => !SoulseekQuality.IsUnknownCode(code))
            .ToArray();

        var enabled = new HashSet<string>(
            enabledCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => NormalizeQuality(SoulseekSource, code)),
            StringComparer.OrdinalIgnoreCase);

        // Nothing ticked means nothing enabled, and it renders nothing. Expanding an empty selection to the
        // whole ladder showed groups of files the download will not walk - the ladder itself is built from
        // ResolveEnabledSoulseekQualities, which resolves that same empty selection to no rung at all - so the
        // tab described a search the queue was never going to run.
        var tiers = ladder.Where(enabled.Contains).ToArray();

        // The undetermined rung needs both halves of its own switch: the safety setting that allows such a
        // file, and the selection that enables it. Either alone is not enough, and a rung rendered without it
        // would be a step the ladder refuses to take.
        if (!allowUnknownQuality || !enabled.Contains(SoulseekQualityInfo.UnknownCode))
        {
            return tiers;
        }

        return [.. tiers, SoulseekQualityInfo.UnknownCode];
    }

    private static string NormalizeEngine(string? engine)
    {
        // The catalog owns the alias table, so an engine is spelled the same way here as it is everywhere the
        // value is validated. Previously this method had its own private copy, which meant an alias could be
        // understood here and rejected by the catalog that decides what is valid.
        var canonical = DownloadSourceCatalog.NormalizeEngineName(engine);
        if (canonical is not null)
        {
            return canonical;
        }

        return engine?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static string NormalizeQuality(string? engine, string? quality)
    {
        var source = NormalizeEngine(engine);
        var normalized = quality?.Trim() ?? string.Empty;
        if (string.Equals(source, TidalSource, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, AppleSource, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, AmazonSource, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, SoulseekSource, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, SoundCloudSource, StringComparison.OrdinalIgnoreCase))
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
            SoundCloudSource => "SoundCloud",
            SoulseekSource => "Soulseek",
            _ => "the selected engine"
        };
    }

    private static bool ShouldIncludeQualityProfile(
        DownloadProfile profile,
        string? forcedService)
    {
        if (!string.IsNullOrWhiteSpace(forcedService)
            && forcedService != AutoService
            && forcedService != CustomService
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
