using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Apple;

namespace DeezSpoTag.Services.Download.Fallback;

public sealed class EngineFallbackCoordinator
{
    private static readonly TimeSpan FallbackStepResolveTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AmazonFallbackStepResolveTimeout = TimeSpan.FromSeconds(25);
    private const string DeezerEngine = "deezer";
    private const string QobuzEngine = "qobuz";
    private const string AppleEngine = "apple";
    private const string TidalEngine = "tidal";
    private const string AmazonEngine = "amazon";
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DeezerIsrcResolver _deezerIsrcResolver;
    private readonly EngineFallbackSearchService _fallbackSearchService;
    private readonly IActivityLogWriter _activityLog;
    private sealed record FallbackAdvanceRequest(
        string QueueUuid,
        string CurrentEngine,
        List<string> AutoSources,
        int AutoIndex,
        string SourceUrl,
        string SpotifyId,
        string AppleId,
        string QobuzId,
        string TidalId,
        string AmazonId,
        string Isrc,
        string DeezerId,
        string Title,
        string Artist,
        string Album,
        int? DurationMs,
        string Quality,
        string ContentType,
        QueueSourceSettingsSnapshot SourceSettingsSnapshot,
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
        string QobuzId,
        string TidalId,
        string AmazonId,
        string? Isrc,
        string Title,
        string Artist,
        string Album,
        int? DurationMs,
        string DeezerId,
        string Quality,
        string ContentType,
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
        IActivityLogWriter activityLog)
    {
        _queueRepository = queueRepository;
        _settingsService = settingsService;
        _deezerIsrcResolver = deezerIsrcResolver;
        _fallbackSearchService = fallbackSearchService;
        _activityLog = activityLog;
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
            QobuzId: payload.QobuzId,
            TidalId: payload.TidalId,
            AmazonId: payload.AmazonId,
            Isrc: payload.Isrc,
            DeezerId: payload.DeezerId,
            Title: payload.Title,
            Artist: payload.Artist,
            Album: payload.Album,
            DurationMs: payload.DurationSeconds > 0 ? payload.DurationSeconds * 1000 : (int?)null,
            Quality: payload.Quality,
            ContentType: payload.ContentType,
            SourceSettingsSnapshot: payload.SourceSettingsSnapshot,
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
        var settings = ResolveEffectiveSettings(request);
        var planSteps = BuildPlanSteps(request, settings);
        if (planSteps.Count == 0)
        {
            _activityLog.Warn($"Quality plan unavailable: {request.QueueUuid}");
            return false;
        }

        var resolvedIsrc = await ResolveIsrcForFallbackAsync(request, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolvedIsrc))
        {
            TrySetIsrc(payloadForSerialization, resolvedIsrc);
        }

        var nextIndex = ResolveNextPlanIndex(planSteps, request);
        mutators.ApplyAutoSources(EncodePlanSteps(planSteps));
        var userCountry = settings.DeezerCountry;

        var resolutionRequest = BuildSourceResolutionRequest(
            request,
            settings,
            userCountry,
            request.SpotifyId,
            resolvedIsrc);
        var stepContext = new FallbackStepExecutionContext(
            mutators,
            payloadForSerialization,
            resolutionRequest,
            request.SpotifyId,
            resolvedIsrc);

        for (var stepIndex = nextIndex; stepIndex < planSteps.Count; stepIndex++)
        {
            var step = planSteps[stepIndex];
            if (ShouldSkipStep(step, request.CurrentEngine, settings.FallbackBitrate))
            {
                AddFallbackAttempt(
                    stepContext.PayloadForSerialization,
                    step,
                    stepIndex,
                    "skipped",
                    "same_engine_blocked",
                    "Same-engine quality fallback is disabled.");
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

        await PersistFallbackExhaustionAsync(
            request,
            planSteps,
            nextIndex,
            payloadForSerialization,
            cancellationToken);
        return false;
    }

    private static List<string> EncodePlanSteps(List<(string Source, string? Quality)> planSteps)
        => planSteps
            .Select(step => DownloadSourceOrder.EncodeAutoSource(step.Source, step.Quality))
            .ToList();

    private DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings ResolveEffectiveSettings(FallbackAdvanceRequest request)
    {
        var liveSettings = _settingsService.LoadSettings();
        return request.SourceSettingsSnapshot?.HasValues == true
            ? request.SourceSettingsSnapshot.ApplyTo(liveSettings)
            : liveSettings;
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
            QobuzId: request.QobuzId,
            TidalId: request.TidalId,
            AmazonId: request.AmazonId,
            Isrc: resolvedIsrc,
            Title: request.Title,
            Artist: request.Artist,
            Album: request.Album,
            DurationMs: request.DurationMs,
            DeezerId: request.DeezerId,
            Quality: request.Quality,
            ContentType: request.ContentType,
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
        string? resolvedUrl;
        try
        {
            resolvedUrl = await ResolveSourceUrlAsync(
                context.ResolutionRequest with { Engine = step.Source },
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AddFallbackAttempt(
                context.PayloadForSerialization,
                step,
                stepIndex,
                "skipped",
                "timeout",
                "Timed out while resolving fallback URL.");
            _activityLog.Warn($"Fallback skip: {request.QueueUuid} -> {step.Source} (resolution timeout)");
            return false;
        }

        if (string.IsNullOrWhiteSpace(resolvedUrl))
        {
            AddFallbackAttempt(
                context.PayloadForSerialization,
                step,
                stepIndex,
                "skipped",
                "unresolved",
                "No resolvable URL for enabled fallback step.");
            _activityLog.Warn($"Fallback skip: {request.QueueUuid} -> {step.Source} (no resolvable URL)");
            return false;
        }

        context.Mutators.SetSourceUrl(resolvedUrl ?? string.Empty);
        TrySetResolvedEngineId(context.PayloadForSerialization, step.Source, resolvedUrl);
        context.Mutators.ApplyStep((step.Source, step.Quality, stepIndex));
        ClearResolutionError(context.PayloadForSerialization);
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

    private async Task PersistFallbackExhaustionAsync(
        FallbackAdvanceRequest request,
        List<(string Source, string? Quality)> planSteps,
        int nextIndex,
        object payloadForSerialization,
        CancellationToken cancellationToken)
    {
        if (!HasLaterDistinctEngineStep(planSteps, request.CurrentEngine, nextIndex))
        {
            return;
        }

        var triedSteps = planSteps
            .Skip(Math.Max(0, nextIndex))
            .Select(step => string.IsNullOrWhiteSpace(step.Quality)
                ? step.Source
                : $"{step.Source} {step.Quality}")
            .ToList();
        var detail = triedSteps.Count == 0
            ? "No later enabled fallback source remained in the queue plan."
            : $"Tried enabled fallback steps: {string.Join(", ", triedSteps)}.";
        var message = $"Enabled fallback sources could not resolve this track after {request.CurrentEngine} failed. {detail}";
        SetResolutionError(payloadForSerialization, message);
        var json = System.Text.Json.JsonSerializer.Serialize(payloadForSerialization);
        await _queueRepository.UpdatePayloadAsync(request.QueueUuid, json, cancellationToken);
    }

    private static bool HasLaterDistinctEngineStep(
        List<(string Source, string? Quality)> planSteps,
        string currentEngine,
        int nextIndex)
    {
        for (var index = Math.Max(0, nextIndex); index < planSteps.Count; index++)
        {
            if (!string.Equals(planSteps[index].Source, currentEngine, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddFallbackAttempt(
        object payloadForSerialization,
        (string Source, string? Quality) step,
        int stepIndex,
        string status,
        string errorClass,
        string detail)
    {
        if (payloadForSerialization is not EngineQueueItemBase payload)
        {
            return;
        }

        var stepId = $"step-{stepIndex}";
        var attemptDetail = string.IsNullOrWhiteSpace(step.Quality)
            ? $"{step.Source}: {detail}"
            : $"{step.Source} {step.Quality}: {detail}";
        if (payload.FallbackHistory.Any(attempt =>
                string.Equals(attempt.StepId, stepId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.Status, status, StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.ErrorClass, errorClass, StringComparison.OrdinalIgnoreCase)
                && string.Equals(attempt.Detail, attemptDetail, StringComparison.Ordinal)))
        {
            return;
        }

        payload.FallbackHistory.Add(new FallbackAttempt(
            stepId,
            status,
            errorClass,
            attemptDetail));
    }

    private static void SetResolutionError(object payloadForSerialization, string message)
    {
        if (payloadForSerialization is EngineQueueItemBase payload)
        {
            payload.ResolutionError = message;
        }
    }

    private static void ClearResolutionError(object payloadForSerialization)
    {
        if (payloadForSerialization is EngineQueueItemBase payload)
        {
            payload.ResolutionError = string.Empty;
        }
    }

    private static void TrySetResolvedEngineId(object payloadForSerialization, string source, string? resolvedUrl)
    {
        if (string.IsNullOrWhiteSpace(resolvedUrl))
        {
            return;
        }

        if (string.Equals(source, AppleEngine, StringComparison.OrdinalIgnoreCase))
        {
            var resolvedAppleId = AppleIdParser.TryExtractFromUrl(resolvedUrl);
            if (!string.IsNullOrWhiteSpace(resolvedAppleId))
            {
                TrySetStringProperty(payloadForSerialization, "AppleId", resolvedAppleId);
            }

            return;
        }

        if (string.Equals(source, QobuzEngine, StringComparison.OrdinalIgnoreCase))
        {
            var resolvedQobuzId = EngineLinkParser.TryExtractQobuzTrackId(resolvedUrl);
            if (!string.IsNullOrWhiteSpace(resolvedQobuzId))
            {
                TrySetStringProperty(payloadForSerialization, "QobuzId", resolvedQobuzId);
            }

            return;
        }

        if (string.Equals(source, TidalEngine, StringComparison.OrdinalIgnoreCase))
        {
            var resolvedTidalId = EngineLinkParser.TryExtractTidalTrackId(resolvedUrl);
            if (!string.IsNullOrWhiteSpace(resolvedTidalId))
            {
                TrySetStringProperty(payloadForSerialization, "TidalId", resolvedTidalId);
            }

            return;
        }

        if (string.Equals(source, AmazonEngine, StringComparison.OrdinalIgnoreCase))
        {
            var resolvedAmazonId = EngineLinkParser.TryExtractAmazonTrackId(resolvedUrl, EngineLinkParser.RegexTimeout);
            if (!string.IsNullOrWhiteSpace(resolvedAmazonId))
            {
                TrySetStringProperty(payloadForSerialization, "AmazonId", resolvedAmazonId);
            }

            return;
        }

        if (string.Equals(source, DeezerEngine, StringComparison.OrdinalIgnoreCase))
        {
            var resolvedDeezerId = EngineLinkParser.TryExtractDeezerTrackId(resolvedUrl);
            if (!string.IsNullOrWhiteSpace(resolvedDeezerId))
            {
                TrySetStringProperty(payloadForSerialization, "DeezerId", resolvedDeezerId);
            }
        }
    }

    private async Task<bool> PersistAdvancedFallbackStateAsync(
        string queueUuid,
        string stepSource,
        object payloadForSerialization,
        CancellationToken cancellationToken)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payloadForSerialization);
        await _queueRepository.UpdatePayloadAndEngineAsync(queueUuid, stepSource, json, cancellationToken);
        await _queueRepository.ClearRetryArtifactsAsync(queueUuid, cancellationToken);
        return await _queueRepository.RequeueAsync(
            queueUuid,
            QueueRequeueOrigin.FallbackAdvance,
            cancellationToken);
    }

    private static List<(string Source, string? Quality)> BuildPlanSteps(
        FallbackAdvanceRequest request,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        _ = settings;
        var steps = new List<(string Source, string? Quality)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var atmosOnly = IsAtmosRequest(request);

        if (request.AutoSources.Count > 0)
        {
            foreach (var encodedSource in request.AutoSources)
            {
                var step = DownloadSourceOrder.DecodeAutoSource(encodedSource);
                AppendPlanStep(steps, seen, step.Source, step.Quality, atmosOnly);
            }
        }

        if (steps.Count == 0 && request.FallbackPlan != null && request.FallbackPlan.Count > 0)
        {
            foreach (var step in request.FallbackPlan)
            {
                AppendPlanStep(steps, seen, step.Engine, step.Quality, atmosOnly);
            }
        }

        return steps;
    }

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
                && string.Equals(quality?.Trim(), "DOLBY_ATMOS", StringComparison.OrdinalIgnoreCase))
           || (string.Equals(source, AmazonEngine, StringComparison.OrdinalIgnoreCase)
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
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stepCts.CancelAfter(ResolveFallbackStepTimeout(request.Engine));
        var result = await _fallbackSearchService.ResolveAsync(
            new EngineFallbackSearchRequest(
                request.Engine,
                request.SourceUrl,
                request.SpotifyId,
                request.AppleId,
                request.QobuzId,
                request.TidalId,
                request.AmazonId,
                request.Isrc,
                request.Title,
                request.Artist,
                request.Album,
                request.DurationMs,
                request.DeezerId,
                request.Quality,
                request.ContentType,
                request.Storefront,
                request.Language,
                request.MediaUserToken,
                request.UserCountry,
                request.FallbackSearchEnabled),
            stepCts.Token);
        return result.ResolvedUrl;
    }

    private static TimeSpan ResolveFallbackStepTimeout(string engine)
        => string.Equals(engine, AmazonEngine, StringComparison.OrdinalIgnoreCase)
            ? AmazonFallbackStepResolveTimeout
            : FallbackStepResolveTimeout;

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

    private static void TrySetStringProperty(object payload, string propertyName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var property = payload.GetType().GetProperty(propertyName);
        if (property == null || !property.CanWrite || property.PropertyType != typeof(string))
        {
            return;
        }

        property.SetValue(payload, value.Trim());
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
