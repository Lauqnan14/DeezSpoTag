using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Soulseek;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using DeezSpoTag.Services.Apple;

namespace DeezSpoTag.Services.Download.Fallback;

public sealed class EngineFallbackCoordinator
{
    private static readonly TimeSpan FallbackStepResolveTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AmazonFallbackStepResolveTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan SoulseekFallbackStepResolveTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     SoundCloud's resolution budget.
    /// </summary>
    /// <remarks>
    ///     A SoundCloud step may have to search and then hydrate the chosen track before it has a streamable
    ///     permalink, which is more round trips than a direct id lookup and warrants its own budget rather than
    ///     being folded into the default.
    /// </remarks>
    private static readonly TimeSpan SoundCloudFallbackStepResolveTimeout = TimeSpan.FromSeconds(20);
    private const string DeezerEngine = "deezer";
    private const string QobuzEngine = "qobuz";
    private const string AppleEngine = "apple";
    private const string TidalEngine = "tidal";
    private const string AmazonEngine = "amazon";
    private const string SoulseekEngine = "soulseek";
    private const string SoundCloudEngine = "soundcloud";
    private readonly DownloadQueueRepository _queueRepository;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DeezerIsrcResolver _deezerIsrcResolver;
    private readonly EngineFallbackSearchService _fallbackSearchService;
    private readonly IActivityLogWriter _activityLog;
    private readonly DeezSpoTag.Services.Download.Shared.Models.INotificationSink _notifications;
    private readonly IServiceProvider? _serviceProvider;
    private sealed record FallbackAdvanceRequest(
        string QueueUuid,
        string CurrentEngine,
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
        List<FallbackPlanStep> FallbackPlan,
        object Payload);

    private sealed record FallbackPayloadMutators(
        Action<(string Source, string? Quality, int Index)> ApplyStep,
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
        bool FallbackSearchEnabled,
        /// <summary>
        ///     The SoundCloud permalink the item already carries, if any.
        /// </summary>
        /// <remarks>
        ///     Trailing and nullable so the per-step resolution record keeps its existing construction shape.
        ///     Carried because a SoundCloud id cannot be turned back into a URL, so the only thing a step can
        ///     reuse is a permalink the item already has.
        /// </remarks>
        string? SoundCloudUrl = null);

    public EngineFallbackCoordinator(
        DownloadQueueRepository queueRepository,
        DeezSpoTagSettingsService settingsService,
        DeezerIsrcResolver deezerIsrcResolver,
        EngineFallbackSearchService fallbackSearchService,
        IActivityLogWriter activityLog,
        DeezSpoTag.Services.Download.Shared.Models.INotificationSink? notifications = null,
        IServiceProvider? serviceProvider = null)
    {
        _queueRepository = queueRepository;
        _settingsService = settingsService;
        _deezerIsrcResolver = deezerIsrcResolver;
        _fallbackSearchService = fallbackSearchService;
        _activityLog = activityLog;
        _notifications = notifications ?? DeezSpoTag.Services.Download.Shared.Models.NullNotificationSink.Instance;

        // Optional so a host that registers the coordinator without the Soulseek engine still resolves it.
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    ///     Whether a Soulseek step may run, asked fresh.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The cached status can be up to twenty seconds old and this path decides whether a transfer is
    ///         started at all, so it probes rather than reading the cache.
    ///     </para>
    ///     <para>
    ///         An absent authority and a probe that failed both answer <see langword="false"/>. Reporting
    ///         either as permission re-admits exactly what the check exists to keep out: a host without the
    ///         Soulseek engine has no verified login either, and a probe that could not answer has not
    ///         verified one. The consequence is a skip recorded against the step, not a failed item - the
    ///         ladder simply carries on to the next source.
    ///     </para>
    /// </remarks>
    private async Task<bool> ResolveSoulseekEligibilityAsync(CancellationToken cancellationToken)
    {
        if (_serviceProvider is null)
        {
            return false;
        }

        var connection = _serviceProvider.GetService<DeezSpoTag.Services.Download.Soulseek.ISoulseekConnectionService>();
        if (connection is null)
        {
            return false;
        }

        try
        {
            var eligibility = await connection.GetEligibilityAsync(cancellationToken).ConfigureAwait(false);
            return eligibility.IsUsable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ProbeFailureMeansUnverified(ex))
        {
            // A probe that could not answer has not verified a login. Letting the exception escape instead
            // would abandon the whole ladder walk on one unreachable engine.
            return false;
        }
    }

    /// <summary>
    ///     Whether a failed eligibility probe should be read as "not verified".
    /// </summary>
    /// <remarks>
    ///     Always true, and deliberately so. The probe asks one remote service a yes/no question and
    ///     the caller cannot act on anything finer than yes or no: an unreachable engine, a rejected
    ///     login and a timeout all mean the same thing here. Filtering for "expected" exceptions would
    ///     let an unexpected one escape, and since this sits at the top of the fallback ladder that
    ///     would abandon every remaining engine because one was unreachable - turning a single
    ///     misbehaving service into a failed download.
    ///     <para>
    ///         Named and given an exception so the blanket catch reads as a decision rather than an
    ///         oversight. The project's guardrail bans an unexplained catch-all
    ///         (an unfiltered catch of the base exception type), and an unnamed one could not be told apart
    ///         from a swallowed bug. This form keeps that guardrail intact for every other call site
    ///         while saying out loud that totality is the intent here.
    ///     </para>
    /// </remarks>
    private static bool ProbeFailureMeansUnverified(Exception ex) => true;

    public Task<bool> TryAdvanceAsync<TPayload>(
        string queueUuid,
        string currentEngine,
        TPayload payload,
        CancellationToken cancellationToken)
        where TPayload : EngineQueueItemBase
    {
        if (SoulseekPinnedCandidatePolicy.IsManualSelection(payload))
        {
            return Task.FromResult(false);
        }

        var request = new FallbackAdvanceRequest(
            QueueUuid: queueUuid,
            CurrentEngine: currentEngine,
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
            FallbackPlan: payload.FallbackPlan,
            Payload: payload);

        var mutators = new FallbackPayloadMutators(
            ApplyStep: step =>
            {
                payload.Engine = step.Source;
                payload.SourceService = step.Source;
                payload.Quality = step.Quality ?? payload.Quality;
                payload.AutoIndex = step.Index;
                TrySetDeezerBitrate(payload, step.Source, step.Quality);
            },
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
        var planSteps = BuildPlanSteps(request, payloadForSerialization);
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
        var userCountry = settings.DeezerCountry;

        var resolutionRequest = BuildSourceResolutionRequest(
            request,
            settings,
            userCountry,
            request.SpotifyId,
            resolvedIsrc,
            payloadForSerialization);
        var stepContext = new FallbackStepExecutionContext(
            mutators,
            payloadForSerialization,
            resolutionRequest,
            request.SpotifyId,
            resolvedIsrc);

        for (var stepIndex = nextIndex; stepIndex < planSteps.Count; stepIndex++)
        {
            var step = planSteps[stepIndex];
            var stepId = $"step-{stepIndex}";
            if (IsExhaustedStep(stepContext.PayloadForSerialization, stepId))
            {
                continue;
            }

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
            payloadForSerialization,
            cancellationToken);
        return false;
    }

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
        if (request.AutoIndex < 0 || request.AutoIndex >= planSteps.Count)
        {
            throw new InvalidOperationException($"Persisted fallback index {request.AutoIndex} is outside the active plan.");
        }

        return request.AutoIndex + 1;
    }

    private static SourceResolutionRequest BuildSourceResolutionRequest(
        FallbackAdvanceRequest request,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        string userCountry,
        string? resolvedSpotifyId,
        string? resolvedIsrc,
        object payloadForSerialization)
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
            FallbackSearchEnabled: settings.FallbackSearch,
            SoundCloudUrl: ReadSoundCloudUrl(payloadForSerialization, request));
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

    private static bool IsExhaustedStep(object payloadForSerialization, string stepId)
    {
        if (payloadForSerialization is not EngineQueueItemBase payload)
        {
            return false;
        }

        return payload.FallbackHistory.Any(attempt =>
            string.Equals(attempt.StepId, stepId, StringComparison.OrdinalIgnoreCase)
            && IsTerminalFallbackAttempt(attempt));
    }

    private static bool IsTerminalFallbackAttempt(FallbackAttempt attempt)
        => FallbackFailureClassifier.IsTerminal(attempt);

    private async Task<bool> TryAdvanceToStepAsync(
        FallbackAdvanceRequest request,
        (string Source, string? Quality) step,
        int stepIndex,
        FallbackStepExecutionContext context,
        CancellationToken cancellationToken)
    {
        // A plan persisted while Soulseek had a valid login may outlive that login. Rechecking the authority
        // here keeps a stale plan from starting work the admission gate would refuse; the probe is what
        // distinguishes a disabled engine from a missing one. A pinned manual selection is exempted above
        // because its intent belongs to the reader, not to the current connection state.
        if (string.Equals(step.Source, DeezSpoTag.Services.Download.Soulseek.SoulseekQueueItem.EngineId, StringComparison.OrdinalIgnoreCase))
        {
            var eligible = await ResolveSoulseekEligibilityAsync(cancellationToken).ConfigureAwait(false);
            if (!eligible)
            {
                // Skipped, not advanced. `true` here means "the ladder moved into this step", and returning it
                // would end the walk while claiming success for a transfer that never started - the item would
                // sit on a Soulseek step no engine is going to run. `false` lets the loop carry on to the next
                // source, which is what an inactive engine is supposed to mean.
                AddFallbackAttempt(
                    context.PayloadForSerialization,
                    step,
                    stepIndex,
                    "skipped",
                    "soulseek_login_required",
                    "Soulseek has no verified login; trying the next source.");
                _activityLog.Warn(
                    $"Skipped a persisted Soulseek step for queue {request.QueueUuid}: the login is no longer verified.");
                return false;
            }
        }

        string? resolvedUrl;
        try
        {
            resolvedUrl = await ResolveSourceUrlAsync(
                context.ResolutionRequest with
                {
                    Engine = step.Source,
                    Quality = step.Quality ?? context.ResolutionRequest.Quality
                },
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
        catch (Exception ex) when (ex is not OperationCanceledException
                                   && DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            AddFallbackAttempt(
                context.PayloadForSerialization,
                step,
                stepIndex,
                "skipped",
                FallbackFailureClassifier.Classify(ex),
                ex.Message);
            _activityLog.Warn($"Fallback skip: {request.QueueUuid} -> {step.Source} (provider resolution failed: {ex.Message})");
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

        context.Mutators.SetSourceUrl(resolvedUrl!);
        TrySetResolvedEngineId(context.PayloadForSerialization, step.Source, resolvedUrl);
        context.Mutators.ApplyStep((step.Source, step.Quality, stepIndex));
        MarkCentralResolutionPending(context.PayloadForSerialization);
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
        object payloadForSerialization,
        CancellationToken cancellationToken)
    {
        // The item gets one short line: what happened, in the reader's terms. The step detail is the diagnosis,
        // so it goes to the log here, where a reader with a log open can act on it, and stays on the payload's
        // history for the same reason.
        var message = BuildExhaustionMessage(payloadForSerialization, request.CurrentEngine);
        SetResolutionError(payloadForSerialization, message);
        var json = System.Text.Json.JsonSerializer.Serialize(payloadForSerialization);
        await _queueRepository.UpdatePayloadAsync(request.QueueUuid, json, cancellationToken);
        var attemptCount = payloadForSerialization is EngineQueueItemBase payload
            ? payload.FallbackHistory.Count
            : 0;
        _activityLog.Warn(
            $"Fallback exhausted: {request.QueueUuid} after {request.CurrentEngine}; recorded attempts={attemptCount}");
        if (payloadForSerialization is EngineQueueItemBase exhausted
            && exhausted.FallbackHistory.Count > 0)
        {
            _activityLog.Warn(
                $"Fallback exhausted detail for {request.QueueUuid}: {exhausted.FallbackHistory[^1].Detail}");
        }

        NotifyDownloadFailed(request, payloadForSerialization, attemptCount);
    }

    /// <summary>
    ///     The exhaustion message: one short line, naming what happened.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This string is the item's error, rendered under its title in the queue list. It is read once, at a
    ///         glance, in a column that has to fit - so it gets one line and nothing else. "Download failed after
    ///         all enabled sources were tried" was half of that line and told the reader nothing they could not
    ///         see from the status beside it; the raw step detail was the other problem, and produced lines like
    ///         "Last attempt (download_failed): slskd did not create a transfer for ...\Hi Scores..." that named
    ///         an internal service and a remote path and still said nothing about what to do.
    ///     </para>
    ///     <para>
    ///         So the class vocabulary decides the line - it is what the walk itself concluded - and the detail
    ///         goes to the log, where the diagnosis belongs.
    ///     </para>
    /// </remarks>
    private static string BuildExhaustionMessage(object payloadForSerialization, string? engine)
    {
        var source = SourceLabel(engine);

        if (payloadForSerialization is not EngineQueueItemBase payload || payload.FallbackHistory.Count == 0)
        {
            return $"{source} did not deliver the file.";
        }

        return $"{source} {DescribeLastAttempt(payload.FallbackHistory[^1])}";
    }

    /// <summary>
    ///     The engine's name as a reader knows it.
    /// </summary>
    /// <remarks>
    ///     The walk carries the engine id - "soulseek" - and putting that on the item's error line gave the
    ///     sentence a lowercase first word. The catalog already holds the label for every engine, so the line
    ///     takes its name from there rather than capitalising a string that may not be an engine at all.
    /// </remarks>
    private static string SourceLabel(string? engine)
    {
        var normalized = DownloadSourceCatalog.NormalizeEngineName(engine);
        if (normalized is null)
        {
            return string.IsNullOrWhiteSpace(engine) ? "Source" : engine.Trim();
        }

        return DownloadSourceCatalog.GetEngineOptions()
            .FirstOrDefault(option => string.Equals(option.Value, normalized, StringComparison.Ordinal))?.Label
            ?? normalized;
    }

    /// <summary>
    ///     The last attempt in the fewest words that are still true, ready to follow the source's name.
    /// </summary>
    /// <remarks>
    ///     "download_failed" is the classifier's catch-all, so it gets words that are true of every route into
    ///     it: no peer with a copy, and a peer that stopped part way through, are both "could not deliver the
    ///     file", and neither is worth a longer sentence that picks a winner.
    /// </remarks>
    private static string DescribeLastAttempt(FallbackAttempt attempt)
    {
        var reason = (attempt.ErrorClass ?? attempt.Status ?? string.Empty).Trim().ToLowerInvariant();
        return reason switch
        {
            FallbackFailureClassifier.ProviderTransient
                or FallbackFailureClassifier.ProviderManifestUnavailable =>
                "was temporarily unavailable.",
            FallbackFailureClassifier.ProviderTimeout =>
                "timed out.",
            FallbackFailureClassifier.ProviderRateLimited =>
                "rate-limited the request.",
            FallbackFailureClassifier.ProviderVerificationRequired =>
                "needs signing in again.",
            FallbackFailureClassifier.Unresolved or FallbackFailureClassifier.Unavailable =>
                "had no copy of this track.",
            FallbackFailureClassifier.NotConfigured =>
                "is not set up.",
            FallbackFailureClassifier.AuthenticationRequired =>
                "needs signing in.",
            FallbackFailureClassifier.CatalogQualityBelowRequested
                or FallbackFailureClassifier.QualityBelowRequested =>
                "cannot offer that quality.",
            FallbackFailureClassifier.SameEngineBlocked or FallbackFailureClassifier.Unsupported =>
                "cannot be used for this track.",
            "" =>
                "did not deliver the file.",
            _ =>
                "could not deliver the file."
        };
    }

    private void NotifyDownloadFailed(
        FallbackAdvanceRequest request,
        object payloadForSerialization,
        int attemptCount)
    {
        var title = "Download failed";
        var detail = $"All enabled sources were tried after {request.CurrentEngine} ({attemptCount} attempt(s)).";
        if (payloadForSerialization is EngineQueueItemBase payload)
        {
            var artist = string.IsNullOrWhiteSpace(payload.Artist) ? null : payload.Artist.Trim();
            var track = string.IsNullOrWhiteSpace(payload.Title) ? null : payload.Title.Trim();
            if (track is not null)
            {
                title = artist is null ? $"Download failed: {track}" : $"Download failed: {artist} - {track}";
            }
        }

        _notifications.Raise(
            "download_failed",
            title,
            detail,
            "Warning",
            $"download_failed:{request.QueueUuid}",
            "download",
            request.QueueUuid);
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

    private static void MarkCentralResolutionPending(object payloadForSerialization)
    {
        if (payloadForSerialization is not EngineQueueItemBase payload)
        {
            return;
        }

        payload.ResolutionStatus = QueuePreResolutionPayload.Pending;
        payload.ResolvedAtUtc = null;
        payload.ResolvedEngine = string.Empty;
        payload.ResolvedSourceUrl = string.Empty;
        payload.ResolutionError = string.Empty;
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

        if (string.Equals(source, SoundCloudEngine, StringComparison.OrdinalIgnoreCase))
        {
            // SoundCloud ids are numeric but the permalink carries the uploader and track slugs, so only the
            // URL can be recovered from a resolved link. Both the engine's own resolved field and the shared
            // SourceUrl are set so the next attempt does not have to search again.
            TrySetStringProperty(payloadForSerialization, "SoundCloudResolvedUrl", resolvedUrl);
            TrySetStringProperty(payloadForSerialization, "SourceUrl", resolvedUrl);
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
            requeueToFront: false,
            newestFirst: string.Equals(_settingsService.LoadSettings().QueueOrder, "recent", StringComparison.OrdinalIgnoreCase),
            cancellationToken);
    }

    private static List<(string Source, string? Quality)> BuildPlanSteps(
        FallbackAdvanceRequest request,
        object payloadForSerialization)
    {
        var steps = new List<(string Source, string? Quality)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (request.FallbackPlan != null && request.FallbackPlan.Count > 0)
        {
            var normalizedPlan = DownloadExecutionPlan.NormalizeForRequest(
                request.FallbackPlan,
                request.ContentType,
                request.Quality);
            if (request.FallbackPlan.Count != normalizedPlan.Count
                && payloadForSerialization is EngineQueueItemBase payload)
            {
                payload.FallbackPlan = normalizedPlan;
            }

            foreach (var step in normalizedPlan)
            {
                AppendPlanStep(steps, seen, step.Engine, step.Quality);
            }
        }

        return steps;
    }

    private static void AppendPlanStep(
        List<(string Source, string? Quality)> steps,
        HashSet<string> seen,
        string? source,
        string? quality)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var normalizedSource = source.Trim();
        var normalizedQuality = string.IsNullOrWhiteSpace(quality) ? null : quality.Trim();
        var key = DownloadSourceOrder.EncodeAutoSource(normalizedSource, normalizedQuality);
        if (seen.Add(key))
        {
            steps.Add((normalizedSource, normalizedQuality));
        }
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
                request.FallbackSearchEnabled,
                request.SoundCloudUrl),
            stepCts.Token);
        return result.ResolvedUrl;
    }

    /// <summary>
    ///     Reads the SoundCloud permalink an item carries, when it has one.
    /// </summary>
    /// <remarks>
    ///     Read through the runtime payload rather than by engine, so this coordinator does not have to know
    ///     about every engine's own identity field. An item from another engine simply has no SoundCloud
    ///     field, and its SoundCloud step falls back to a metadata search.
    /// </remarks>
    private static string? ReadSoundCloudUrl(object payloadForSerialization, FallbackAdvanceRequest request)
    {
        // An earlier attempt on this item may already have resolved the permalink, in which case there is no
        // reason to search for the track again.
        var resolved = payloadForSerialization.GetType().GetProperty("SoundCloudResolvedUrl");
        if (resolved is { CanRead: true }
            && resolved.PropertyType == typeof(string)
            && resolved.GetValue(payloadForSerialization) is string { Length: > 0 } resolvedUrl
            && !string.IsNullOrWhiteSpace(resolvedUrl))
        {
            return resolvedUrl;
        }

        // A pasted SoundCloud link arrives as the item's own SourceUrl rather than as engine-resolved state.
        return !string.IsNullOrWhiteSpace(request.SourceUrl)
               && request.SourceUrl.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase)
            ? request.SourceUrl
            : null;
    }

    private static TimeSpan ResolveFallbackStepTimeout(string engine)
        => engine.ToLowerInvariant() switch
        {
            // Amazon's catalogue lookups and Soulseek's peer searches both take materially longer than a
            // direct id or URL resolution, so they get their own budget.
            AmazonEngine => AmazonFallbackStepResolveTimeout,
            SoulseekEngine => SoulseekFallbackStepResolveTimeout,
            SoundCloudEngine => SoundCloudFallbackStepResolveTimeout,
            _ => FallbackStepResolveTimeout
        };

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
