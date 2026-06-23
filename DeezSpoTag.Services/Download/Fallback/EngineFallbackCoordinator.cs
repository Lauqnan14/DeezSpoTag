using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Apple;

namespace DeezSpoTag.Services.Download.Fallback;

public sealed class EngineFallbackCoordinator
{
    public sealed class OptionalServices
    {
        public IDownloadApiHealthTracker? ApiHealthTracker { get; init; }
    }
    private const string DeezerEngine = "deezer";
    private const string QobuzEngine = "qobuz";
    private const string AppleEngine = "apple";
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DeezerIsrcResolver _deezerIsrcResolver;
    private readonly EngineFallbackSearchService _fallbackSearchService;
    private readonly IActivityLogWriter _activityLog;
    private readonly IDownloadApiHealthTracker _apiHealthTracker;
    private sealed record FallbackAdvanceRequest(
        string QueueUuid,
        string CurrentEngine,
        List<string> AutoSources,
        int AutoIndex,
        string SourceUrl,
        string SpotifyId,
        string AppleId,
        string Isrc,
        string DeezerId,
        string Title,
        string Artist,
        string Album,
        int? DurationMs,
        string Quality,
        string ContentType,
        List<FallbackPlanStep> FallbackPlan);
    private sealed record FallbackPayloadMutators(
        Action<(string Source, string? Quality, int Index)> ApplyStep,
        Action<List<string>> ApplyAutoSources,
        Action<string> SetSourceUrl);
    private sealed record FallbackStepExecutionContext(
        FallbackPayloadMutators Mutators,
        object PayloadForSerialization,
        SourceResolutionRequest ResolutionRequest,
        string? SpotifyId,
        string? ResolvedIsrc);
    private sealed record SourceResolutionRequest(
        string Engine,
        string SourceUrl,
        string SpotifyId,
        string AppleId,
        string? Isrc,
        string Title,
        string Artist,
        string Album,
        int? DurationMs,
        string DeezerId,
        string Storefront,
        string Language,
        string? MediaUserToken,
        string UserCountry,
        bool FallbackSearchEnabled);

    public EngineFallbackCoordinator(
        DownloadQueueRepository queueRepository,
        DeezSpoTagSettingsService settingsService,
        DeezerIsrcResolver deezerIsrcResolver,
        EngineFallbackSearchService fallbackSearchService,
        IActivityLogWriter activityLog,
        OptionalServices? optionalServices = null)
    {
        optionalServices ??= new OptionalServices();
        _queueRepository = queueRepository;
        _settingsService = settingsService;
        _deezerIsrcResolver = deezerIsrcResolver;
        _fallbackSearchService = fallbackSearchService;
        _activityLog = activityLog;
        _apiHealthTracker = optionalServices.ApiHealthTracker ?? new DownloadApiHealthTracker();
    }

    public Task<bool> TryAdvanceAsync<TPayload>(
        string queueUuid,
        string currentEngine,
        TPayload payload,
        CancellationToken cancellationToken)
        where TPayload : EngineQueueItemBase
    {
        var request = new FallbackAdvanceRequest(
            QueueUuid: queueUuid,
            CurrentEngine: currentEngine,
            AutoSources: payload.AutoSources,
            AutoIndex: payload.AutoIndex,
            SourceUrl: payload.SourceUrl,
            SpotifyId: payload.SpotifyId,
            AppleId: payload.AppleId,
            Isrc: payload.Isrc,
            DeezerId: payload.DeezerId,
            Title: payload.Title,
            Artist: payload.Artist,
            Album: payload.Album,
            DurationMs: payload.DurationSeconds > 0 ? payload.DurationSeconds * 1000 : (int?)null,
            Quality: payload.Quality,
            ContentType: payload.ContentType,
            FallbackPlan: payload.FallbackPlan);

        var mutators = new FallbackPayloadMutators(
            ApplyStep: step =>
            {
                payload.Engine = step.Source;
                payload.SourceService = step.Source;
                payload.Quality = step.Quality ?? payload.Quality;
                payload.AutoIndex = step.Index;
                TrySetDeezerBitrate(payload, step.Source, step.Quality);
            },
            ApplyAutoSources: sources => payload.AutoSources = sources,
            SetSourceUrl: url => payload.SourceUrl = url);

        return TryAdvanceCoreAsync(
            request,
            mutators,
            payload,
            cancellationToken);
    }

    private async Task<bool> TryAdvanceCoreAsync(
        FallbackAdvanceRequest request,
        FallbackPayloadMutators mutators,
        object payloadForSerialization,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.LoadSettings();
        var planSteps = BuildPlanSteps(request, settings);

        var resolvedIsrc = await ResolveIsrcForFallbackAsync(request, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolvedIsrc))
        {
            TrySetIsrc(payloadForSerialization, resolvedIsrc);
        }

        var nextIndex = ResolveNextPlanIndex(planSteps, request);
        planSteps = PrioritizeRemainingPlanSteps(planSteps, nextIndex, settings);
        mutators.ApplyAutoSources(EncodePlanSteps(planSteps));
        var userCountry = settings.DeezerCountry;
        var resolvedSpotifyId = await ResolveSpotifyIdForFallbackAsync(request, userCountry, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolvedSpotifyId))
        {
            TrySetSpotifyId(payloadForSerialization, resolvedSpotifyId);
        }

        var resolutionRequest = BuildSourceResolutionRequest(
            request,
            settings,
            userCountry,
            resolvedSpotifyId,
            resolvedIsrc);
        var stepContext = new FallbackStepExecutionContext(
            mutators,
            payloadForSerialization,
            resolutionRequest,
            resolvedSpotifyId ?? request.SpotifyId,
            resolvedIsrc);

        for (var stepIndex = nextIndex; stepIndex < planSteps.Count; stepIndex++)
        {
            var step = planSteps[stepIndex];
            if (ShouldSkipStep(step, request.CurrentEngine, settings.FallbackBitrate))
            {
                continue;
            }

            var advanced = await TryAdvanceToStepAsync(
                request,
                step,
                stepIndex,
                stepContext,
                cancellationToken);
            if (advanced)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> EncodePlanSteps(List<(string Source, string? Quality)> planSteps)
        => planSteps
            .Select(step => DownloadSourceOrder.EncodeAutoSource(step.Source, step.Quality))
            .ToList();

    private List<(string Source, string? Quality)> PrioritizeRemainingPlanSteps(
        List<(string Source, string? Quality)> planSteps,
        int nextIndex,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        if (planSteps.Count <= 1 || nextIndex >= planSteps.Count - 1)
        {
            return planSteps;
        }

        if (string.Equals(settings.Service?.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            return planSteps;
        }

        var completedSteps = nextIndex > 0
            ? planSteps.Take(nextIndex).ToList()
            : new List<(string Source, string? Quality)>();
        var prioritizedRemaining = _apiHealthTracker
            .PrioritizeSources(EncodePlanSteps(planSteps.Skip(nextIndex).ToList()))
            .Select(DownloadSourceOrder.DecodeAutoSource)
            .Where(static step => !string.IsNullOrWhiteSpace(step.Source))
            .Select(static step => (step.Source, step.Quality))
            .ToList();

        completedSteps.AddRange(prioritizedRemaining);
        return completedSteps;
    }

    private async Task<string?> ResolveIsrcForFallbackAsync(
        FallbackAdvanceRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Isrc))
        {
            return request.Isrc;
        }

        var resolvedIsrc = await _deezerIsrcResolver.ResolveByTrackIdAsync(request.DeezerId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolvedIsrc))
        {
            return resolvedIsrc;
        }

        return await _deezerIsrcResolver.ResolveByMetadataAsync(
            request.Title,
            request.Artist,
            request.Album,
            request.DurationMs,
            cancellationToken);
    }

    private static int ResolveNextPlanIndex(List<(string Source, string? Quality)> planSteps, FallbackAdvanceRequest request)
    {
        var matchedIndex = FindStepIndex(planSteps, request.CurrentEngine, request.Quality);
        // Reconcile persisted auto-index with current engine/quality:
        // some engines can internally step down quality before bubbling a failure.
        // If that happened, prefer the furthest progressed index so fallback does not revisit already-attempted steps.
        var currentIndex = request.AutoIndex >= 0
            ? Math.Max(request.AutoIndex, matchedIndex)
            : matchedIndex;
        return currentIndex + 1;
    }

    private static SourceResolutionRequest BuildSourceResolutionRequest(
        FallbackAdvanceRequest request,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        string userCountry,
        string? resolvedSpotifyId,
        string? resolvedIsrc)
    {
        return new SourceResolutionRequest(
            Engine: string.Empty,
            SourceUrl: request.SourceUrl,
            SpotifyId: resolvedSpotifyId ?? request.SpotifyId,
            AppleId: request.AppleId,
            Isrc: resolvedIsrc,
            Title: request.Title,
            Artist: request.Artist,
            Album: request.Album,
            DurationMs: request.DurationMs,
            DeezerId: request.DeezerId,
            Storefront: settings.AppleMusic?.Storefront ?? string.Empty,
            Language: settings.DeezerLanguage ?? string.Empty,
            MediaUserToken: settings.AppleMusic?.MediaUserToken,
            UserCountry: userCountry,
            FallbackSearchEnabled: settings.FallbackSearch);
    }

    private static bool ShouldSkipStep(
        (string Source, string? Quality) step,
        string currentEngine,
        bool fallbackBitrateEnabled)
    {
        if (string.IsNullOrWhiteSpace(step.Source))
        {
            return true;
        }

        return !fallbackBitrateEnabled
            && string.Equals(step.Source, currentEngine, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryAdvanceToStepAsync(
        FallbackAdvanceRequest request,
        (string Source, string? Quality) step,
        int stepIndex,
        FallbackStepExecutionContext context,
        CancellationToken cancellationToken)
    {
        var resolvedUrl = await ResolveSourceUrlAsync(
            context.ResolutionRequest with { Engine = step.Source },
            cancellationToken);
        var canAdvanceWithoutResolvedUrl = CanAdvanceWithoutResolvedUrl(step.Source, context.SpotifyId, request, context.ResolvedIsrc);
        if (string.IsNullOrWhiteSpace(resolvedUrl) && !canAdvanceWithoutResolvedUrl)
        {
            _activityLog.Warn($"Fallback skip: {request.QueueUuid} -> {step.Source} (no resolvable URL)");
            return false;
        }

        context.Mutators.SetSourceUrl(resolvedUrl ?? string.Empty);
        TrySetResolvedAppleId(context.PayloadForSerialization, step.Source, resolvedUrl);
        context.Mutators.ApplyStep((step.Source, step.Quality, stepIndex));
        var requeued = await PersistAdvancedFallbackStateAsync(
            request.QueueUuid,
            step.Source,
            context.PayloadForSerialization,
            cancellationToken);
        if (!requeued)
        {
            _activityLog.Warn($"Fallback requeue blocked: {request.QueueUuid} -> {step.Source}");
            return false;
        }

        _activityLog.Info($"Fallback advanced: {request.QueueUuid} -> {step.Source} (auto_index={stepIndex})");
        return true;
    }

    private static void TrySetResolvedAppleId(object payloadForSerialization, string source, string? resolvedUrl)
    {
        if (!string.Equals(source, AppleEngine, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var resolvedAppleId = AppleIdParser.TryExtractFromUrl(resolvedUrl);
        if (!string.IsNullOrWhiteSpace(resolvedAppleId))
        {
            TrySetAppleId(payloadForSerialization, resolvedAppleId);
        }
    }

    private async Task<bool> PersistAdvancedFallbackStateAsync(
        string queueUuid,
        string stepSource,
        object payloadForSerialization,
        CancellationToken cancellationToken)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payloadForSerialization);
        await _queueRepository.UpdatePayloadAsync(queueUuid, json, cancellationToken);
        await _queueRepository.UpdateEngineAsync(queueUuid, stepSource, cancellationToken);
        await _queueRepository.ClearRetryArtifactsAsync(queueUuid, cancellationToken);
        return await _queueRepository.RequeueAsync(
            queueUuid,
            QueueRequeueOrigin.FallbackAdvance,
            cancellationToken);
    }

    private async Task<string?> ResolveSpotifyIdForFallbackAsync(
        FallbackAdvanceRequest request,
        string userCountry,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            return request.SpotifyId;
        }

        return await _fallbackSearchService.ResolveSpotifyIdAsync(
            request.SourceUrl,
            request.DeezerId,
            userCountry,
            cancellationToken);
    }

    private static List<(string Source, string? Quality)> BuildPlanSteps(
        FallbackAdvanceRequest request,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        var steps = new List<(string Source, string? Quality)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var atmosOnly = IsAtmosRequest(request);

        if (request.AutoSources != null && request.AutoSources.Count > 0)
        {
            foreach (var decoded in request.AutoSources.Select(DownloadSourceOrder.DecodeAutoSource))
            {
                AppendPlanStep(steps, seen, decoded.Source, decoded.Quality, atmosOnly);
            }
        }

        if (request.FallbackPlan != null && request.FallbackPlan.Count > 0)
        {
            foreach (var step in request.FallbackPlan)
            {
                AppendPlanStep(steps, seen, step.Engine, step.Quality, atmosOnly);
            }
        }

        if (!atmosOnly && IsAutoOrCustomSourceOrder(settings))
        {
            foreach (var decoded in DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null)
                .Select(DownloadSourceOrder.DecodeAutoSource))
            {
                AppendPlanStep(steps, seen, decoded.Source, decoded.Quality, atmosOnly);
            }
        }

        if (steps.Count == 0 && !atmosOnly)
        {
            foreach (var decoded in DownloadSourceOrder.ResolveQualityAutoSources(settings, includeDeezer: true, targetQuality: null)
                .Select(DownloadSourceOrder.DecodeAutoSource))
            {
                AppendPlanStep(steps, seen, decoded.Source, decoded.Quality, atmosOnly);
            }
        }

        return steps;
    }

    private static bool IsAutoOrCustomSourceOrder(DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
        => string.Equals(settings.Service?.Trim(), "auto", StringComparison.OrdinalIgnoreCase)
           || settings.DownloadEngineOrder?.Enabled == true;

    private static bool IsAtmosRequest(FallbackAdvanceRequest request)
        => string.Equals(request.ContentType?.Trim(), "atmos", StringComparison.OrdinalIgnoreCase)
           || string.Equals(request.Quality?.Trim(), "ATMOS", StringComparison.OrdinalIgnoreCase)
           || string.Equals(request.Quality?.Trim(), "DOLBY_ATMOS", StringComparison.OrdinalIgnoreCase);

    private static void AppendPlanStep(
        List<(string Source, string? Quality)> steps,
        HashSet<string> seen,
        string? source,
        string? quality,
        bool atmosOnly = false)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var normalizedSource = source.Trim();
        var normalizedQuality = string.IsNullOrWhiteSpace(quality) ? null : quality.Trim();
        if (atmosOnly && !IsAtmosStep(normalizedSource, normalizedQuality))
        {
            return;
        }

        var key = DownloadSourceOrder.EncodeAutoSource(normalizedSource, normalizedQuality);
        if (seen.Add(key))
        {
            steps.Add((normalizedSource, normalizedQuality));
        }
    }

    private static bool IsAtmosStep(string source, string? quality)
        => (string.Equals(source, AppleEngine, StringComparison.OrdinalIgnoreCase)
                && string.Equals(quality?.Trim(), "ATMOS", StringComparison.OrdinalIgnoreCase))
           || (string.Equals(source, "tidal", StringComparison.OrdinalIgnoreCase)
                && string.Equals(quality?.Trim(), "DOLBY_ATMOS", StringComparison.OrdinalIgnoreCase));

    private static int FindStepIndex(List<(string Source, string? Quality)> autoSources, string engine, string quality)
    {
        for (var i = 0; i < autoSources.Count; i++)
        {
            var step = autoSources[i];
            if (string.Equals(step.Source, engine, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(step.Quality) || string.IsNullOrWhiteSpace(quality))
                {
                    return i;
                }

                if (string.Equals(step.Quality, quality, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private async Task<string?> ResolveSourceUrlAsync(
        SourceResolutionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _fallbackSearchService.ResolveAsync(
            new EngineFallbackSearchRequest(
                request.Engine,
                request.SourceUrl,
                request.SpotifyId,
                request.AppleId,
                request.Isrc,
                request.Title,
                request.Artist,
                request.Album,
                request.DurationMs,
                request.DeezerId,
                request.Storefront,
                request.Language,
                request.MediaUserToken,
                request.UserCountry,
                request.FallbackSearchEnabled),
            cancellationToken);
        return result.ResolvedUrl;
    }

    private static bool CanAdvanceWithoutResolvedUrl(
        string engine,
        string? spotifyId,
        FallbackAdvanceRequest request,
        string? resolvedIsrc)
    {
        if (string.IsNullOrWhiteSpace(engine))
        {
            return false;
        }

        if (string.Equals(engine, QobuzEngine, StringComparison.OrdinalIgnoreCase))
        {
            // Qobuz path can proceed with ISRC-only resolution.
            return !string.IsNullOrWhiteSpace(resolvedIsrc);
        }

        if (string.Equals(engine, "amazon", StringComparison.OrdinalIgnoreCase))
        {
            // Amazon path can resolve from Spotify ID when URL is missing.
            return !string.IsNullOrWhiteSpace(spotifyId);
        }

        if (string.Equals(engine, "tidal", StringComparison.OrdinalIgnoreCase))
        {
            // Tidal path can resolve from Spotify ID or from metadata in-engine.
            return !string.IsNullOrWhiteSpace(spotifyId)
                || !string.IsNullOrWhiteSpace(resolvedIsrc)
                || (!string.IsNullOrWhiteSpace(request.Title) && !string.IsNullOrWhiteSpace(request.Artist));
        }

        return false;
    }

    private static void TrySetIsrc(object payload, string isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return;
        }

        var property = payload.GetType().GetProperty("Isrc");
        if (property == null || !property.CanWrite)
        {
            return;
        }

        property.SetValue(payload, isrc);
    }

    private static void TrySetSpotifyId(object payload, string spotifyId)
    {
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return;
        }

        var property = payload.GetType().GetProperty("SpotifyId");
        if (property == null || !property.CanWrite)
        {
            return;
        }

        property.SetValue(payload, spotifyId);
    }

    private static void TrySetAppleId(object payload, string appleId)
    {
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return;
        }

        var property = payload.GetType().GetProperty("AppleId");
        if (property == null || !property.CanWrite)
        {
            return;
        }

        property.SetValue(payload, appleId);
    }

    private static void TrySetDeezerBitrate(object payload, string source, string? quality)
    {
        if (!string.Equals(source, DeezerEngine, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(quality)
            || !int.TryParse(quality, out var bitrate)
            || bitrate <= 0)
        {
            return;
        }

        var property = payload.GetType().GetProperty("Bitrate");
        if (property == null || !property.CanWrite)
        {
            return;
        }

        if (property.PropertyType == typeof(int))
        {
            property.SetValue(payload, bitrate);
            return;
        }

        if (property.PropertyType == typeof(int?))
        {
            property.SetValue(payload, (int?)bitrate);
        }
    }
}
