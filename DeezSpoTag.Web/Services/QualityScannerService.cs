using DeezSpoTag.Services.Download.Queue;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class QualityScannerService
{
    private const string FinishedStatus = "finished";
    private const string AppleSource = "apple";
    private const string DeezerSource = "deezer";
    private const string SpotifySource = "spotify";
    private const string TrackType = "track";
    private const string AtmosQuality = "atmos";
    private const string HiResLosslessQuality = "hi_res_lossless";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly object _stateLock = new();
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly SpotifySearchService _spotifySearchService;
    private readonly DeezSpoTagSearchService _searchService;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly DownloadQueueRepository _queueRepository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TrackAnalysisBackgroundService _trackAnalysisService;
    private readonly ShazamRecognitionService _shazamRecognitionService;
    private readonly ILogger<QualityScannerService> _logger;
    private CancellationTokenSource? _cts;
    private QualityScannerState _state = QualityScannerState.Idle();
    private sealed record TrackActionOutcome(string LastAction, DateTimeOffset? LastQueuedAtUtc, string? LastError, bool IncrementDuplicateCount)
    {
        public static TrackActionOutcome NoChange(string action) => new(action, null, null, false);
        public static TrackActionOutcome Queued(string action, DateTimeOffset queuedAtUtc) => new(action, queuedAtUtc, null, false);
        public static TrackActionOutcome Error(string action, string? error) => new(action, null, error, false);
    }
    private sealed record QualityThresholdResult(bool BelowTechnicalThresholds, bool BelowDesiredQuality)
    {
        public bool RequiresUpgrade => BelowTechnicalThresholds || BelowDesiredQuality;
    }
    private sealed record UpgradeQueueContext(
        long RunId,
        QualityScannerRunOptions Options,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings Settings,
        DownloadIntentService DownloadIntentService,
        CancellationToken CancellationToken);
    private sealed record UpgradeQueueRequest(string EffectiveIsrc, string RequestedQuality, string UpgradeContentType);

    public QualityScannerService(
        IServiceProvider serviceProvider,
        ILogger<QualityScannerService> logger)
    {
        _repository = serviceProvider.GetRequiredService<LibraryRepository>();
        _configStore = serviceProvider.GetRequiredService<LibraryConfigStore>();
        _spotifySearchService = serviceProvider.GetRequiredService<SpotifySearchService>();
        _searchService = serviceProvider.GetRequiredService<DeezSpoTagSearchService>();
        _settingsService = serviceProvider.GetRequiredService<DeezSpoTagSettingsService>();
        _queueRepository = serviceProvider.GetRequiredService<DownloadQueueRepository>();
        _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        _trackAnalysisService = serviceProvider.GetRequiredService<TrackAnalysisBackgroundService>();
        _shazamRecognitionService = serviceProvider.GetRequiredService<ShazamRecognitionService>();
        _logger = logger;
    }

    public QualityScannerState GetState()
    {
        lock (_stateLock)
        {
            return _state;
        }
    }

    public async Task<QualityScannerAutomationSettingsDto> GetAutomationSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await _repository.GetQualityScannerAutomationSettingsAsync(cancellationToken);
    }

    public async Task<QualityScannerAutomationSettingsDto> UpdateAutomationSettingsAsync(
        QualityScannerAutomationSettingsDto settings,
        CancellationToken cancellationToken = default)
    {
        return await _repository.UpdateQualityScannerAutomationSettingsAsync(settings, cancellationToken);
    }

    public async Task<bool> StartAsync(
        QualityScannerStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedScope = NormalizeScope(request.Scope);
        QualityScannerAutomationSettingsDto automationSettings;
        try
        {
            automationSettings = await _repository.GetQualityScannerAutomationSettingsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load quality scanner automation settings. Using defaults.");
            automationSettings = new QualityScannerAutomationSettingsDto(
                Enabled: false,
                IntervalMinutes: 360,
                Scope: "watchlist",
                FolderId: null,
                QueueAtmosAlternatives: false,
                CooldownMinutes: 1440,
                LastStartedUtc: null,
                LastFinishedUtc: null);
        }

        var settings = _settingsService.LoadSettings();
        var effectiveQueueAtmos = request.QueueAtmosAlternatives ?? automationSettings.QueueAtmosAlternatives;
        var effectiveCooldown = Math.Clamp(request.CooldownMinutes ?? automationSettings.CooldownMinutes, 0, 43200);
        var atmosDestinationFolderId = GetAtmosDestinationFolderId(settings);
        CancellationTokenSource? previousCts;

        lock (_stateLock)
        {
            if (string.Equals(_state.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _state = QualityScannerState.Running(
                normalizedScope,
                request.FolderId,
                request.Trigger,
                effectiveQueueAtmos,
                effectiveCooldown);
            previousCts = _cts;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var options = new QualityScannerRunOptions(
                Scope: normalizedScope,
                FolderId: request.FolderId,
                MinFormatRank: NormalizeMinFormatRank(request.MinFormat),
                MinFormat: NormalizeMinFormat(request.MinFormat),
                MinBitDepth: NormalizeMinBitDepth(request.MinBitDepth),
                MinSampleRateHz: NormalizeMinSampleRateHz(request.MinSampleRateHz),
                Trigger: string.IsNullOrWhiteSpace(request.Trigger) ? "manual" : request.Trigger.Trim().ToLowerInvariant(),
                QueueAtmosAlternatives: effectiveQueueAtmos,
                CooldownMinutes: effectiveCooldown,
                AtmosDestinationFolderId: atmosDestinationFolderId,
                MarkAutomationWindow: request.MarkAutomationWindow,
                TechnicalProfiles: NormalizeTechnicalProfiles(request.TechnicalProfiles),
                FolderIds: NormalizeFolderIds(request.FolderIds, request.FolderId));
            _ = Task.Run(() => RunAsync(options, _cts.Token), _cts.Token);
        }

        if (previousCts is not null)
        {
            await previousCts.CancelAsync();
            previousCts.Dispose();
        }

        return true;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? currentCts = null;

        lock (_stateLock)
        {
            if (!string.Equals(_state.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _state = _state with
            {
                Status = FinishedStatus,
                Phase = "Scan stopped by user"
            };
            currentCts = _cts;
        }

        if (currentCts is not null)
        {
            await currentCts.CancelAsync();
        }
    }

    private async Task RunAsync(QualityScannerRunOptions options, CancellationToken cancellationToken)
    {
        long runId = 0;
        var finishedStatus = FinishedStatus;
        string? runError = null;

        try
        {
            if (!TryValidateRunPrerequisites(out runError))
            {
                finishedStatus = "error";
                return;
            }

            await MarkAutomationWindowStartedAsync(options, cancellationToken);
            runId = await CreateRunRecordAsync(options, cancellationToken);
            UpdateState(state => state with { RunId = runId > 0 ? runId : null });

            var tracks = await LoadRunTracksAsync(options, cancellationToken);
            var settings = _settingsService.LoadSettings();
            await InitializeRunStateAsync(options, tracks, runId, cancellationToken);
            if (await HandleEmptyTrackSetAsync(options, tracks, runId, cancellationToken))
            {
                return;
            }

            using var serviceScope = _scopeFactory.CreateScope();
            var downloadIntentService = serviceScope.ServiceProvider.GetRequiredService<DownloadIntentService>();
            await ProcessQualityUpgradeStageAsync(runId, tracks, options, settings, downloadIntentService, cancellationToken);
            if (options.QueueAtmosAlternatives)
            {
                await ProcessAtmosEnhancementStageAsync(runId, tracks, options, downloadIntentService, cancellationToken);
            }

            await MarkRunSuccessAsync(runId, cancellationToken);
            LogInfo("Quality scan completed.");
        }
        catch (OperationCanceledException)
        {
            UpdateState(state => state with
            {
                Status = "finished",
                Phase = "Scan stopped"
            });
            finishedStatus = "cancelled";
            LogInfo("Quality scan cancelled.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Quality scanner run failed.");
            UpdateError(ex.Message);
            finishedStatus = "error";
            runError = ex.Message;
        }
        finally
        {
            try
            {
                await PersistRunProgressAsync(runId, CancellationToken.None);
                await _repository.CompleteQualityScannerRunAsync(runId, finishedStatus, runError, CancellationToken.None);
                if (options.MarkAutomationWindow)
                {
                    await _repository.MarkQualityScannerAutomationFinishedAsync(DateTimeOffset.UtcNow, CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to persist quality scanner completion state.");
            }
        }
    }

    private bool TryValidateRunPrerequisites(out string? runError)
    {
        runError = null;
        if (_repository.IsConfigured)
        {
            return true;
        }

        runError = "Library DB not configured";
        UpdateError(runError);
        return false;
    }

    private async Task MarkAutomationWindowStartedAsync(QualityScannerRunOptions options, CancellationToken cancellationToken)
    {
        if (!options.MarkAutomationWindow)
        {
            return;
        }

        await _repository.MarkQualityScannerAutomationStartedAsync(DateTimeOffset.UtcNow, cancellationToken);
    }

    private async Task<long> CreateRunRecordAsync(QualityScannerRunOptions options, CancellationToken cancellationToken)
    {
        return await _repository.StartQualityScannerRunAsync(
            options.Trigger,
            options.Scope,
            options.FolderId,
            options.QueueAtmosAlternatives,
            cancellationToken);
    }

    private async Task<List<QualityScanTrackDto>> LoadRunTracksAsync(QualityScannerRunOptions options, CancellationToken cancellationToken)
    {
        var tracks = (await _repository.GetQualityScanTracksAsync(
            options.Scope,
            options.FolderId,
            options.MinFormat,
            options.MinBitDepth,
            options.MinSampleRateHz,
            cancellationToken)).ToList();
        if (options.FolderIds.Count > 1)
        {
            tracks = tracks
                .Where(track => track.DestinationFolderId.HasValue && options.FolderIds.Contains(track.DestinationFolderId.Value))
                .ToList();
        }

        if (options.TechnicalProfiles.Count > 0)
        {
                tracks = tracks
                .Where(track => options.TechnicalProfiles.Contains(QualityScanTrackFormatter.FormatTechnicalProfile(track)))
                .ToList();
        }

        return tracks;
    }

    private async Task InitializeRunStateAsync(
        QualityScannerRunOptions options,
        IReadOnlyCollection<QualityScanTrackDto> tracks,
        long runId,
        CancellationToken cancellationToken)
    {
        UpdateState(state => state with
        {
            Total = tracks.Count,
            Phase = $"Scanning {tracks.Count} tracks..."
        });
        await PersistRunProgressAsync(runId, cancellationToken);

        LogInfo(
            $"Quality scan started: scope={options.Scope}, folderId={(options.FolderId?.ToString() ?? "all")}, tracks={tracks.Count}, trigger={options.Trigger}, queueAtmos={options.QueueAtmosAlternatives}, minFormat={(options.MinFormat ?? "any")}, minBitDepth={(options.MinBitDepth?.ToString() ?? "any")}, minSampleRateHz={(options.MinSampleRateHz?.ToString() ?? "any")}");
    }

    private async Task<bool> HandleEmptyTrackSetAsync(
        QualityScannerRunOptions options,
        IReadOnlyCollection<QualityScanTrackDto> tracks,
        long runId,
        CancellationToken cancellationToken)
    {
        if (tracks.Count > 0)
        {
            return false;
        }

        var emptyMessage = ResolveEmptyTrackMessage(options);
        UpdateState(state => state with
        {
            Status = FinishedStatus,
            Phase = emptyMessage
        });
        await PersistRunProgressAsync(runId, cancellationToken);
        LogInfo($"Quality scan finished with no tracks: scope={options.Scope}, folderId={(options.FolderId?.ToString() ?? "all")}");
        return true;
    }

    private static string ResolveEmptyTrackMessage(QualityScannerRunOptions options)
    {
        var hasTechnicalThresholds = options.MinBitDepth.HasValue || options.MinSampleRateHz.HasValue;
        if (hasTechnicalThresholds)
        {
            return "No tracks found below the selected format/bit depth/sample rate thresholds.";
        }

        if (options.MinFormatRank.HasValue)
        {
            return "No tracks found below the selected format threshold.";
        }

        return string.Equals(options.Scope, "watchlist", StringComparison.OrdinalIgnoreCase)
            ? "No watchlist tracks found (add watchlist artists or use All Library Tracks)."
            : "No tracks found for the selected folder scope.";
    }

    private async Task ProcessQualityUpgradeStageAsync(
        long runId,
        IReadOnlyList<QualityScanTrackDto> tracks,
        QualityScannerRunOptions options,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        DownloadIntentService downloadIntentService,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < tracks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = tracks[index];
            UpdateState(state => state with
            {
                Processed = index + 1,
                Progress = tracks.Count == 0 ? 0 : (index + 1) * 100d / tracks.Count,
                Phase = $"Scanning: {track.ArtistName} - {track.Title}"
            });

            await TryRunTrackAnalysisAsync(track.TrackId, cancellationToken);
            var outcome = await ProcessQualityUpgradeTrackAsync(
                runId,
                track,
                options,
                settings,
                downloadIntentService,
                DateTimeOffset.UtcNow,
                cancellationToken);
            await TryUpdateTrackStateAsync(
                new QualityScannerTrackStateUpdateDto(
                    TrackId: track.TrackId,
                    RunId: runId > 0 ? runId : null,
                    BestQualityRank: track.BestQualityRank,
                    DesiredQualityRank: track.DesiredQualityRank,
                    LastAction: outcome.LastAction,
                    LastUpgradeQueuedUtc: outcome.LastQueuedAtUtc,
                    LastAtmosQueuedUtc: null,
                    LastError: outcome.LastError),
                cancellationToken);
            await PersistRunProgressAsync(runId, cancellationToken);
        }
    }

    private async Task<TrackActionOutcome> ProcessQualityUpgradeTrackAsync(
        long runId,
        QualityScanTrackDto track,
        QualityScannerRunOptions options,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings,
        DownloadIntentService downloadIntentService,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var thresholdResult = EvaluateQualityThresholds(track, options);
        if (!thresholdResult.RequiresUpgrade)
        {
            UpdateState(state => state with { QualityMet = state.QualityMet + 1 });
            return TrackActionOutcome.NoChange("quality_met");
        }

        UpdateState(state => state with { LowQuality = state.LowQuality + 1 });
        var shazamResult = await ResolveShazamMatchContextAsync(track, cancellationToken);
        if (!shazamResult.Success || shazamResult.MatchContext == null)
        {
            var error = string.IsNullOrWhiteSpace(shazamResult.ErrorMessage)
                ? "Shazam scan did not produce usable metadata."
                : shazamResult.ErrorMessage;
            UpdateState(state => state with { MatchMissed = state.MatchMissed + 1 });
            await TryRecordActionAsync(new QualityScannerActionLogDto(
                RunId: runId > 0 ? runId : null,
                TrackId: track.TrackId,
                ActionType: "upgrade_shazam_failed",
                Source: null,
                Quality: null,
                ContentType: null,
                DestinationFolderId: track.DestinationFolderId,
                QueueUuid: null,
                Message: error), cancellationToken);
            return TrackActionOutcome.Error("upgrade_shazam_failed", error);
        }

        var match = await FindBestMatchAsync(shazamResult.MatchContext, cancellationToken);
        if (match == null)
        {
            UpdateState(state => state with { MatchMissed = state.MatchMissed + 1 });
            return TrackActionOutcome.NoChange("upgrade_no_match");
        }

        return await QueueUpgradeForMatchAsync(
            track,
            shazamResult.MatchContext,
            match,
            thresholdResult.BelowTechnicalThresholds,
            thresholdResult.BelowDesiredQuality,
            nowUtc,
            new UpgradeQueueContext(runId, options, settings, downloadIntentService, cancellationToken));
    }

    private async Task<TrackActionOutcome> QueueUpgradeForMatchAsync(
        QualityScanTrackDto track,
        QualityScanShazamMatchContext shazamMatchContext,
        QualityScanMatch match,
        bool belowTechnicalThresholds,
        bool belowDesiredQuality,
        DateTimeOffset nowUtc,
        UpgradeQueueContext context)
    {
        var request = BuildUpgradeQueueRequest(
            track,
            shazamMatchContext,
            match,
            belowTechnicalThresholds,
            belowDesiredQuality,
            context.Settings);
        var duplicateOutcome = await TryHandleDuplicateUpgradeAsync(track, match, request, context);
        if (duplicateOutcome is not null)
        {
            return duplicateOutcome;
        }

        return await EnqueueUpgradeAsync(track, match, request, context, nowUtc);
    }

    private static QualityThresholdResult EvaluateQualityThresholds(QualityScanTrackDto track, QualityScannerRunOptions options)
    {
        var belowBitDepthThreshold = options.MinBitDepth.HasValue
            && (!track.BestBitsPerSample.HasValue || track.BestBitsPerSample.Value < options.MinBitDepth.Value);
        var belowSampleRateThreshold = options.MinSampleRateHz.HasValue
            && (!track.BestSampleRateHz.HasValue || track.BestSampleRateHz.Value < options.MinSampleRateHz.Value);
        var belowFormatThreshold = options.MinFormatRank.HasValue
            && (!track.BestFormatRank.HasValue || track.BestFormatRank.Value < options.MinFormatRank.Value);
        var belowDesiredQuality = track.DesiredQualityRank != 0 && track.BestQualityRank < track.DesiredQualityRank;
        return new QualityThresholdResult(
            belowBitDepthThreshold || belowSampleRateThreshold || belowFormatThreshold,
            belowDesiredQuality);
    }

    private static UpgradeQueueRequest BuildUpgradeQueueRequest(
        QualityScanTrackDto track,
        QualityScanShazamMatchContext shazamMatchContext,
        QualityScanMatch match,
        bool belowTechnicalThresholds,
        bool belowDesiredQuality,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        var effectiveIsrc = string.IsNullOrWhiteSpace(track.Isrc) ? shazamMatchContext.Isrc : track.Isrc;
        var thresholdDrivenUpgrade = belowTechnicalThresholds && !belowDesiredQuality;
        var requestedQuality = ResolveRequestedUpgradeQuality(track, match.Source, thresholdDrivenUpgrade, settings);
        if (string.IsNullOrWhiteSpace(requestedQuality))
        {
            requestedQuality = GetRequestedQualityValue(match.Source, settings);
        }

        var upgradeContentType = IsAtmosQuality(requestedQuality)
            ? DownloadContentTypes.Atmos
            : DownloadContentTypes.Stereo;
        return new UpgradeQueueRequest(effectiveIsrc, requestedQuality ?? string.Empty, upgradeContentType);
    }

    private async Task<TrackActionOutcome?> TryHandleDuplicateUpgradeAsync(
        QualityScanTrackDto track,
        QualityScanMatch match,
        UpgradeQueueRequest request,
        UpgradeQueueContext context)
    {
        var duplicateUpgrade = await _queueRepository.ExistsDuplicateAsync(
            new DuplicateLookupRequest
            {
                Isrc = request.EffectiveIsrc,
                DeezerTrackId = string.Equals(match.Source, DeezerSource, StringComparison.OrdinalIgnoreCase) ? match.Id : null,
                SpotifyTrackId = string.Equals(match.Source, SpotifySource, StringComparison.OrdinalIgnoreCase) ? match.Id : null,
                AppleTrackId = string.Equals(match.Source, AppleSource, StringComparison.OrdinalIgnoreCase) ? match.Id : null,
                ArtistName = track.ArtistName,
                TrackTitle = track.Title,
                DurationMs = track.DurationMs,
                DestinationFolderId = track.DestinationFolderId,
                ContentType = request.UpgradeContentType,
                RedownloadCooldownMinutes = context.Options.CooldownMinutes
            },
            context.CancellationToken);
        if (!duplicateUpgrade)
        {
            return null;
        }

        UpdateState(state => state with { DuplicateSkipped = state.DuplicateSkipped + 1 });
        await TryRecordActionAsync(new QualityScannerActionLogDto(
            RunId: context.RunId > 0 ? context.RunId : null,
            TrackId: track.TrackId,
            ActionType: "upgrade_duplicate",
            Source: match.Source,
            Quality: request.RequestedQuality,
            ContentType: request.UpgradeContentType,
            DestinationFolderId: track.DestinationFolderId,
            QueueUuid: null,
            Message: "Skipped duplicate upgrade queue item."), context.CancellationToken);
        return TrackActionOutcome.NoChange("upgrade_duplicate");
    }

    private async Task<TrackActionOutcome> EnqueueUpgradeAsync(
        QualityScanTrackDto track,
        QualityScanMatch match,
        UpgradeQueueRequest request,
        UpgradeQueueContext context,
        DateTimeOffset nowUtc)
    {
        var intent = BuildUpgradeIntent(track, match, request);
        var result = await context.DownloadIntentService.EnqueueAsync(intent, context.CancellationToken);
        if (result.Success && result.Queued.Count > 0)
        {
            UpdateState(state => state with
            {
                Matched = state.Matched + 1,
                UpgradesQueued = state.UpgradesQueued + result.Queued.Count
            });
            await RecordQueuedUpgradeActionsAsync(track, match, request, context, result.Queued);

            return TrackActionOutcome.Queued("upgrade_queued", nowUtc);
        }

        var error = string.IsNullOrWhiteSpace(result.Message) ? "Upgrade enqueue failed." : result.Message;
        UpdateState(state => state with { MatchMissed = state.MatchMissed + 1 });
        await TryRecordActionAsync(new QualityScannerActionLogDto(
            RunId: context.RunId > 0 ? context.RunId : null,
            TrackId: track.TrackId,
            ActionType: "upgrade_enqueue_failed",
            Source: match.Source,
            Quality: request.RequestedQuality,
            ContentType: request.UpgradeContentType,
            DestinationFolderId: track.DestinationFolderId,
            QueueUuid: null,
            Message: error), context.CancellationToken);
        return TrackActionOutcome.Error("upgrade_enqueue_failed", error);
    }

    private static DownloadIntent BuildUpgradeIntent(
        QualityScanTrackDto track,
        QualityScanMatch match,
        UpgradeQueueRequest request)
    {
        return new DownloadIntent
        {
            SourceService = match.Source,
            SpotifyId = match.Source == SpotifySource ? match.Id : string.Empty,
            DeezerId = match.Source == DeezerSource ? match.Id : string.Empty,
            AppleId = match.Source == AppleSource ? match.Id : string.Empty,
            Isrc = request.EffectiveIsrc,
            Title = match.Title,
            Artist = match.Artist,
            Album = match.Album,
            DurationMs = match.DurationMs > 0 ? match.DurationMs : (track.DurationMs ?? 0),
            PreferredEngine = match.Source == SpotifySource ? string.Empty : match.Source,
            Quality = request.RequestedQuality,
            ContentType = request.UpgradeContentType,
            DestinationFolderId = track.DestinationFolderId,
            AllowQualityUpgrade = true
        };
    }

    private async Task RecordQueuedUpgradeActionsAsync(
        QualityScanTrackDto track,
        QualityScanMatch match,
        UpgradeQueueRequest request,
        UpgradeQueueContext context,
        IReadOnlyCollection<string> queueUuids)
    {
        foreach (var queueUuid in queueUuids)
        {
            await TryRecordActionAsync(new QualityScannerActionLogDto(
                RunId: context.RunId > 0 ? context.RunId : null,
                TrackId: track.TrackId,
                ActionType: "upgrade_queued",
                Source: match.Source,
                Quality: request.RequestedQuality,
                ContentType: request.UpgradeContentType,
                DestinationFolderId: track.DestinationFolderId,
                QueueUuid: queueUuid,
                Message: "Queued quality upgrade."), context.CancellationToken);
        }
    }

    private static string? ResolveRequestedUpgradeQuality(
        QualityScanTrackDto track,
        string matchSource,
        bool thresholdDrivenUpgrade,
        DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        if (thresholdDrivenUpgrade || string.IsNullOrWhiteSpace(track.DesiredQualityValue))
        {
            return GetRequestedQualityValue(matchSource, settings);
        }

        return track.DesiredQualityValue;
    }

    private async Task ProcessAtmosEnhancementStageAsync(
        long runId,
        IReadOnlyList<QualityScanTrackDto> tracks,
        QualityScannerRunOptions options,
        DownloadIntentService downloadIntentService,
        CancellationToken cancellationToken)
    {
        UpdateState(state => state with
        {
            Phase = $"Enhancement stage: Atmos alternatives ({tracks.Count} tracks)"
        });
        await PersistRunProgressAsync(runId, cancellationToken);

        for (var index = 0; index < tracks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = tracks[index];
            UpdateState(state => state with
            {
                Progress = tracks.Count == 0 ? 100 : (index + 1) * 100d / tracks.Count,
                Phase = $"Enhancement stage: {track.ArtistName} - {track.Title}"
            });

            var outcome = MapAtmosTrackOutcome(
                await QueueAtmosAlternativeAsync(track, options, downloadIntentService, cancellationToken),
                DateTimeOffset.UtcNow);
            if (outcome.IncrementDuplicateCount)
            {
                UpdateState(state => state with { DuplicateSkipped = state.DuplicateSkipped + 1 });
            }

            await TryUpdateTrackStateAsync(
                new QualityScannerTrackStateUpdateDto(
                    TrackId: track.TrackId,
                    RunId: runId > 0 ? runId : null,
                    BestQualityRank: track.BestQualityRank,
                    DesiredQualityRank: track.DesiredQualityRank,
                    LastAction: outcome.LastAction,
                    LastUpgradeQueuedUtc: null,
                    LastAtmosQueuedUtc: outcome.LastQueuedAtUtc,
                    LastError: outcome.LastError),
                cancellationToken);

            await PersistRunProgressAsync(runId, cancellationToken);
        }
    }

    private static TrackActionOutcome MapAtmosTrackOutcome(AtmosQueueResult atmosResult, DateTimeOffset nowUtc)
    {
        if (atmosResult.Queued)
        {
            return TrackActionOutcome.Queued("enhancement_atmos_queued", nowUtc);
        }

        if (atmosResult.Duplicate)
        {
            return new TrackActionOutcome("enhancement_atmos_duplicate", null, null, true);
        }

        if (atmosResult.SkippedNoMatch)
        {
            return TrackActionOutcome.NoChange("enhancement_atmos_no_match");
        }

        if (!string.IsNullOrWhiteSpace(atmosResult.ErrorMessage))
        {
            return TrackActionOutcome.Error("enhancement_atmos_failed", atmosResult.ErrorMessage);
        }

        return TrackActionOutcome.NoChange("enhancement_no_change");
    }

    private async Task MarkRunSuccessAsync(long runId, CancellationToken cancellationToken)
    {
        UpdateState(state => state with
        {
            Status = FinishedStatus,
            Progress = 100,
            Phase = "Scan complete"
        });
        await PersistRunProgressAsync(runId, cancellationToken);
    }

    private async Task TryRunTrackAnalysisAsync(long trackId, CancellationToken cancellationToken)
    {
        if (trackId <= 0)
        {
            return;
        }

        try
        {
            await _trackAnalysisService.AnalyzeTrackByIdAsync(trackId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Quality scanner failed to run full analysis for trackId {TrackId}.", trackId);
        }
    }

    private async Task<AtmosQueueResult> QueueAtmosAlternativeAsync(
        QualityScanTrackDto track,
        QualityScannerRunOptions options,
        DownloadIntentService downloadIntentService,
        CancellationToken cancellationToken)
    {
        var atmosphereDestination = options.AtmosDestinationFolderId ?? track.DestinationFolderId;
        var duplicateAtmos = await _queueRepository.ExistsDuplicateAsync(
            new DuplicateLookupRequest
            {
                Isrc = track.Isrc,
                ArtistName = track.ArtistName,
                TrackTitle = track.Title,
                DurationMs = track.DurationMs,
                DestinationFolderId = atmosphereDestination,
                ContentType = DownloadContentTypes.Atmos,
                RedownloadCooldownMinutes = options.CooldownMinutes
            },
            cancellationToken);

        if (duplicateAtmos)
        {
            await TryRecordActionAsync(new QualityScannerActionLogDto(
                RunId: GetState().RunId,
                TrackId: track.TrackId,
                ActionType: "atmos_duplicate",
                Source: AppleSource,
                Quality: AtmosQuality,
                ContentType: DownloadContentTypes.Atmos,
                DestinationFolderId: atmosphereDestination,
                QueueUuid: null,
                Message: "Skipped duplicate Atmos queue item."), cancellationToken);
            return AtmosQueueResult.DuplicateItem();
        }

        var appleMatch = await FindAppleAtmosMatchAsync(track, cancellationToken);
        if (appleMatch == null)
        {
            return AtmosQueueResult.NoMatch();
        }

        var intent = new DownloadIntent
        {
            SourceService = AppleSource,
            PreferredEngine = AppleSource,
            AppleId = appleMatch.Id,
            Isrc = string.IsNullOrWhiteSpace(appleMatch.Isrc) ? track.Isrc : appleMatch.Isrc,
            Title = string.IsNullOrWhiteSpace(appleMatch.Title) ? track.Title : appleMatch.Title,
            Artist = string.IsNullOrWhiteSpace(appleMatch.Artist) ? track.ArtistName : appleMatch.Artist,
            Album = string.IsNullOrWhiteSpace(appleMatch.Album) ? track.AlbumTitle : appleMatch.Album,
            DurationMs = appleMatch.DurationMs > 0 ? appleMatch.DurationMs : (track.DurationMs ?? 0),
            Quality = AtmosQuality,
            HasAtmos = true,
            ContentType = DownloadContentTypes.Atmos,
            DestinationFolderId = atmosphereDestination,
            AllowQualityUpgrade = true
        };

        var result = await downloadIntentService.EnqueueAsync(intent, cancellationToken);
        if (result.Success && result.Queued.Count > 0)
        {
            UpdateState(state => state with
            {
                AtmosQueued = state.AtmosQueued + result.Queued.Count,
                Matched = state.Matched + 1
            });

            foreach (var queueUuid in result.Queued)
            {
                await TryRecordActionAsync(new QualityScannerActionLogDto(
                    RunId: GetState().RunId,
                    TrackId: track.TrackId,
                    ActionType: "atmos_queued",
                    Source: AppleSource,
                    Quality: AtmosQuality,
                    ContentType: DownloadContentTypes.Atmos,
                    DestinationFolderId: atmosphereDestination,
                    QueueUuid: queueUuid,
                    Message: "Queued Atmos alternative."), cancellationToken);
            }

            return AtmosQueueResult.QueuedItem();
        }

        var errorMessage = string.IsNullOrWhiteSpace(result.Message)
            ? "Atmos enqueue failed."
            : result.Message;
        await TryRecordActionAsync(new QualityScannerActionLogDto(
            RunId: GetState().RunId,
            TrackId: track.TrackId,
            ActionType: "atmos_enqueue_failed",
            Source: AppleSource,
            Quality: AtmosQuality,
            ContentType: DownloadContentTypes.Atmos,
            DestinationFolderId: atmosphereDestination,
            QueueUuid: null,
            Message: errorMessage), cancellationToken);
        return AtmosQueueResult.Error(errorMessage);
    }

    private async Task PersistRunProgressAsync(long runId, CancellationToken cancellationToken)
    {
        if (runId <= 0)
        {
            return;
        }

        var snapshot = GetState();
        var progress = new QualityScannerRunProgressDto(
            TotalTracks: snapshot.Total,
            ProcessedTracks: snapshot.Processed,
            QualityMet: snapshot.QualityMet,
            LowQuality: snapshot.LowQuality,
            UpgradesQueued: snapshot.UpgradesQueued,
            AtmosQueued: snapshot.AtmosQueued,
            DuplicateSkipped: snapshot.DuplicateSkipped,
            MatchMissed: snapshot.MatchMissed);

        await _repository.UpdateQualityScannerRunProgressAsync(runId, progress, snapshot.Phase, cancellationToken);
    }

    private async Task TryUpdateTrackStateAsync(QualityScannerTrackStateUpdateDto update, CancellationToken cancellationToken)
    {
        try
        {
            await _repository.UpsertQualityScannerTrackStateAsync(update, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to persist quality scanner track state for trackId {TrackId}.", update.TrackId);
        }
    }

    private async Task TryRecordActionAsync(QualityScannerActionLogDto action, CancellationToken cancellationToken)
    {
        try
        {
            await _repository.AddQualityScannerActionLogAsync(action, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to persist quality scanner action log for trackId {TrackId}.", action.TrackId);
        }
    }

    private async Task<QualityScanMatch?> FindBestMatchAsync(
        QualityScanShazamMatchContext shazamMatchContext,
        CancellationToken cancellationToken)
    {
        var query = shazamMatchContext.Query;
        var engines = new[] { DeezerSource, SpotifySource, AppleSource };

        foreach (var engine in engines)
        {
            var match = await FindBestMatchForEngineAsync(engine, query, shazamMatchContext, cancellationToken);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private async Task<QualityScanMatch?> FindAppleAtmosMatchAsync(QualityScanTrackDto track, CancellationToken cancellationToken)
    {
        var query = $"{track.ArtistName} {track.Title}";
        var search = await _searchService.SearchByTypeAsync(AppleSource, query, TrackType, 8, 0, cancellationToken);
        if (search == null || search.Items.Count == 0)
        {
            return null;
        }

        QualityScanMatch? best = null;
        foreach (var raw in search.Items)
        {
            if (!TryParseSearchCandidate(raw, AppleSource, out var candidate))
            {
                continue;
            }

            if (!candidate.HasAtmos)
            {
                continue;
            }

            var score = ComputeMatchScore(track, candidate);
            if (!string.IsNullOrWhiteSpace(track.Isrc)
                && !string.IsNullOrWhiteSpace(candidate.Isrc)
                && string.Equals(track.Isrc.Trim(), candidate.Isrc.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Max(score, 0.99);
            }

            if (score < 0.7)
            {
                continue;
            }

            if (best == null || score > best.Score)
            {
                best = new QualityScanMatch(
                    Source: AppleSource,
                    Id: candidate.Id,
                    Title: candidate.Title,
                    Artist: candidate.Artist,
                    Album: candidate.Album,
                    Isrc: candidate.Isrc,
                    DurationMs: candidate.DurationMs,
                    Score: score,
                    HasAtmos: true);
            }
        }

        return best;
    }

    private async Task<QualityScanMatch?> FindBestMatchForEngineAsync(
        string engine,
        string query,
        QualityScanShazamMatchContext shazamMatchContext,
        CancellationToken cancellationToken)
    {
        if (string.Equals(engine, SpotifySource, StringComparison.OrdinalIgnoreCase))
        {
            return await FindBestSpotifyMatchAsync(query, shazamMatchContext, cancellationToken);
        }

        return await FindBestNonSpotifyMatchAsync(engine, query, shazamMatchContext, cancellationToken);
    }

    private async Task<QualityScanMatch?> FindBestSpotifyMatchAsync(
        string query,
        QualityScanShazamMatchContext shazamMatchContext,
        CancellationToken cancellationToken)
    {
        var results = await _spotifySearchService.SearchByTypeAsync(query, "track", 5, 0, cancellationToken);
        if (results == null || results.Items.Count == 0)
        {
            return null;
        }

        QualityScanMatch? best = null;
        foreach (var item in results.Items)
        {
            var candidate = ParseSpotifySearchItem(item);
            if (!TryBuildScoredMatch(shazamMatchContext, candidate.Artist, candidate.Title, candidate.Isrc, out var score))
            {
                continue;
            }

            var scoredMatch = new QualityScanMatch(
                Source: SpotifySource,
                Id: item.Id,
                Title: candidate.Title,
                Artist: candidate.Artist,
                Album: candidate.Album,
                Isrc: candidate.Isrc,
                DurationMs: item.DurationMs ?? 0,
                Score: score,
                HasAtmos: false);
            best = SelectBestMatch(best, scoredMatch);
        }

        return best;
    }

    private async Task<QualityScanMatch?> FindBestNonSpotifyMatchAsync(
        string engine,
        string query,
        QualityScanShazamMatchContext shazamMatchContext,
        CancellationToken cancellationToken)
    {
        var search = await _searchService.SearchByTypeAsync(engine, query, "track", 5, 0, cancellationToken);
        if (search == null || search.Items.Count == 0)
        {
            return null;
        }

        QualityScanMatch? engineBest = null;
        foreach (var raw in search.Items)
        {
            if (!TryParseSearchCandidate(raw, engine, out var candidate))
            {
                continue;
            }

            if (!IsCandidateCompatibleWithShazam(shazamMatchContext, candidate.Isrc))
            {
                continue;
            }

            if (!TryBuildScoredMatch(shazamMatchContext, candidate.Artist, candidate.Title, candidate.Isrc, out var score))
            {
                continue;
            }

            var scoredMatch = new QualityScanMatch(
                candidate.Source,
                candidate.Id,
                candidate.Title,
                candidate.Artist,
                candidate.Album,
                candidate.Isrc,
                candidate.DurationMs,
                score,
                candidate.HasAtmos);
            engineBest = SelectBestMatch(engineBest, scoredMatch);
        }

        return engineBest;
    }

    private static QualityScanMatch? SelectBestMatch(QualityScanMatch? currentBest, QualityScanMatch candidate)
    {
        return currentBest == null || candidate.Score > currentBest.Score
            ? candidate
            : currentBest;
    }

    private static bool TryBuildScoredMatch(
        QualityScanShazamMatchContext shazamMatchContext,
        string candidateArtist,
        string candidateTitle,
        string candidateIsrc,
        out double score)
    {
        score = 0;
        if (!IsCandidateCompatibleWithShazam(shazamMatchContext, candidateIsrc))
        {
            return false;
        }

        score = ComputeMatchScore(shazamMatchContext.Artist, shazamMatchContext.Title, candidateArtist, candidateTitle);
        if (IsShazamIsrcExactMatch(shazamMatchContext, candidateIsrc))
        {
            score = Math.Max(score, 0.99);
        }

        return score >= 0.7;
    }

    private async Task<ShazamMatchResolution> ResolveShazamMatchContextAsync(
        QualityScanTrackDto track,
        CancellationToken cancellationToken)
    {
        if (!_shazamRecognitionService.IsAvailable)
        {
            return ShazamMatchResolution.Failed("Shazam recognition is unavailable.");
        }

        var filePath = await _repository.GetTrackPrimaryFilePathAsync(track.TrackId, cancellationToken);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return ShazamMatchResolution.Failed("Primary audio file not found for Shazam scan.");
        }

        ShazamRecognitionInfo? recognized;
        try
        {
            recognized = _shazamRecognitionService.Recognize(filePath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Quality scanner Shazam scan failed for trackId {TrackId} ({Path}).", track.TrackId, filePath);
            return ShazamMatchResolution.Failed("Shazam scan failed.");
        }

        if (recognized == null || !recognized.HasCoreMetadata)
        {
            return ShazamMatchResolution.Failed("Shazam could not identify this track.");
        }

        var artist = recognized.Artists.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                     ?? recognized.Artist;
        var title = recognized.Title;
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return ShazamMatchResolution.Failed("Shazam returned incomplete metadata.");
        }

        var context = new QualityScanShazamMatchContext(
            Query: $"{artist.Trim()} {title.Trim()}",
            Artist: artist.Trim(),
            Title: title.Trim(),
            Isrc: NormalizeIsrc(recognized.Isrc));
        return ShazamMatchResolution.Matched(context);
    }

    private static double TokenSimilarity(string left, string right)
    {
        var leftTokens = NormalizeTokens(left);
        var rightTokens = NormalizeTokens(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        var intersection = leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).Count();
        return (2d * intersection) / (leftTokens.Count + rightTokens.Count);
    }

    private static List<string> NormalizeTokens(string value)
    {
        var normalized = value.ToLowerInvariant();
        normalized = normalized.Replace("–", "-").Replace("—", "-");
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"\(.*?\)|\[.*?\]|\{.*?\}",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.None,
            RegexTimeout);
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"[^a-z0-9]+",
            " ",
            System.Text.RegularExpressions.RegexOptions.None,
            RegexTimeout).Trim();
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static double ComputeMatchScore(string referenceArtist, string referenceTitle, ParsedSearchCandidate item)
    {
        var artistScore = TokenSimilarity(referenceArtist, item.Artist);
        var titleScore = TokenSimilarity(referenceTitle, item.Title);
        return artistScore * 0.5 + titleScore * 0.5;
    }

    private static double ComputeMatchScore(
        string referenceArtist,
        string referenceTitle,
        string candidateArtist,
        string candidateTitle)
    {
        var artistScore = TokenSimilarity(referenceArtist, candidateArtist);
        var titleScore = TokenSimilarity(referenceTitle, candidateTitle);
        return artistScore * 0.5 + titleScore * 0.5;
    }

    private static double ComputeMatchScore(QualityScanTrackDto track, ParsedSearchCandidate item)
    {
        return ComputeMatchScore(track.ArtistName, track.Title, item);
    }

    private static bool IsCandidateCompatibleWithShazam(QualityScanShazamMatchContext shazamMatchContext, string? candidateIsrc)
    {
        if (string.IsNullOrWhiteSpace(shazamMatchContext.Isrc) || string.IsNullOrWhiteSpace(candidateIsrc))
        {
            return true;
        }

        return string.Equals(
            shazamMatchContext.Isrc,
            NormalizeIsrc(candidateIsrc),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShazamIsrcExactMatch(QualityScanShazamMatchContext shazamMatchContext, string? candidateIsrc)
    {
        if (string.IsNullOrWhiteSpace(shazamMatchContext.Isrc) || string.IsNullOrWhiteSpace(candidateIsrc))
        {
            return false;
        }

        return string.Equals(
            shazamMatchContext.Isrc,
            NormalizeIsrc(candidateIsrc),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeIsrc(string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return string.Empty;
        }

        return isrc.Trim().ToUpperInvariant();
    }

    private static ParsedSearchItem ParseSpotifySearchItem(SpotifySearchItem item)
    {
        var title = item.Name ?? string.Empty;
        var artist = string.Empty;
        var album = string.Empty;
        if (!string.IsNullOrWhiteSpace(item.Subtitle))
        {
            var parts = item.Subtitle.Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                artist = parts[0];
            }
            if (parts.Length > 1)
            {
                album = parts[1];
            }
        }

        return new ParsedSearchItem(title, artist, album, string.Empty);
    }

    private static bool TryParseSearchCandidate(object item, string engine, out ParsedSearchCandidate candidate)
    {
        candidate = default!;
        if (item is DeezSpoTag.Core.Models.Deezer.ApiTrack deezerTrack)
        {
            candidate = new ParsedSearchCandidate(
                DeezerSource,
                deezerTrack.Id ?? string.Empty,
                deezerTrack.Title ?? string.Empty,
                deezerTrack.Artist?.Name ?? string.Empty,
                deezerTrack.Album?.Title ?? string.Empty,
                string.Empty,
                deezerTrack.Duration > 0 ? deezerTrack.Duration * 1000 : 0,
                false);
            return !string.IsNullOrWhiteSpace(candidate.Id);
        }

        var source = TryGetString(item, "source") ?? engine;
        var title = TryGetString(item, "name") ?? TryGetString(item, "title") ?? string.Empty;
        var artist = TryGetString(item, "artist") ?? string.Empty;
        var album = TryGetString(item, "album") ?? string.Empty;
        var durationMs = TryGetLong(item, "durationMs");
        var isrc = TryGetString(item, "isrc") ?? string.Empty;
        var hasAtmos = TryGetBool(item, "hasAtmos");
        var id = source switch
        {
            SpotifySource => TryGetString(item, "spotifyId"),
            DeezerSource => TryGetString(item, "deezerId"),
            AppleSource => TryGetString(item, "appleId"),
            _ => TryGetString(item, "id")
        };

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        candidate = new ParsedSearchCandidate(
            source,
            id,
            title,
            artist,
            album,
            isrc,
            durationMs > int.MaxValue ? int.MaxValue : (int)durationMs,
            hasAtmos);
        return true;
    }

    private static string? TryGetString(object item, string property)
    {
        var prop = item.GetType().GetProperty(property);
        if (prop == null)
        {
            return null;
        }

        return prop.GetValue(item)?.ToString();
    }

    private static long TryGetLong(object item, string property)
    {
        var prop = item.GetType().GetProperty(property);
        if (prop == null)
        {
            return 0;
        }

        var value = prop.GetValue(item);
        return value switch
        {
            null => 0,
            int intValue => intValue,
            long longValue => longValue,
            _ => long.TryParse(value.ToString(), out var parsed) ? parsed : 0
        };
    }

    private static bool TryGetBool(object item, string property)
    {
        var prop = item.GetType().GetProperty(property);
        if (prop == null)
        {
            return false;
        }

        var value = prop.GetValue(item);
        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            int i => i != 0,
            long l => l != 0,
            _ => false
        };
    }

    private static string GetRequestedQualityValue(string engine, DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        var normalized = (engine ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized == SpotifySource)
        {
            normalized = string.IsNullOrWhiteSpace(settings.Service) ? DeezerSource : settings.Service;
        }

        if (normalized == "auto")
        {
            normalized = DeezerSource;
        }

        return normalized switch
        {
            DeezerSource => settings.MaxBitrate.ToString(),
            AppleSource => settings.AppleMusic.PreferredAudioProfile ?? string.Empty,
            "qobuz" => settings.QobuzQuality ?? string.Empty,
            "tidal" => settings.TidalQuality ?? string.Empty,
            _ => string.Empty
        };
    }

    private static string NormalizeScope(string? scope)
    {
        return string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase)
            ? "all"
            : "watchlist";
    }

    private static HashSet<string> NormalizeTechnicalProfiles(IReadOnlyCollection<string>? source)
    {
        if (source is null || source.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return source
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<long> NormalizeFolderIds(IReadOnlyCollection<long>? source, long? folderId)
    {
        var normalized = source is null
            ? new HashSet<long>()
            : source.Where(id => id > 0).ToHashSet();
        if (normalized.Count == 0 && folderId.HasValue && folderId.Value > 0)
        {
            normalized.Add(folderId.Value);
        }
        return normalized;
    }

    private static int? NormalizeMinBitDepth(int? minBitDepth)
    {
        if (!minBitDepth.HasValue || minBitDepth.Value <= 0)
        {
            return null;
        }

        return Math.Clamp(minBitDepth.Value, 1, 64);
    }

    private static int? NormalizeMinSampleRateHz(int? minSampleRateHz)
    {
        if (!minSampleRateHz.HasValue || minSampleRateHz.Value <= 0)
        {
            return null;
        }

        return Math.Clamp(minSampleRateHz.Value, 1000, 768000);
    }

    private static string? NormalizeMinFormat(string? minFormat)
    {
        var normalized = minFormat?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "lossy" => "lossy",
            "lossless" => "lossless",
            HiResLosslessQuality => HiResLosslessQuality,
            "hi-res-lossless" => HiResLosslessQuality,
            "hires_lossless" => HiResLosslessQuality,
            "hires" => HiResLosslessQuality,
            "hi_res" => HiResLosslessQuality,
            AtmosQuality => AtmosQuality,
            _ => null
        };
    }

    private static int? NormalizeMinFormatRank(string? minFormat)
    {
        return NormalizeMinFormat(minFormat) switch
        {
            "lossy" => 1,
            "lossless" => 2,
            HiResLosslessQuality => 3,
            AtmosQuality => 4,
            _ => null
        };
    }

    private static bool IsAtmosQuality(string? quality)
    {
        return !string.IsNullOrWhiteSpace(quality)
            && quality.Contains(AtmosQuality, StringComparison.OrdinalIgnoreCase);
    }

    private static long? GetAtmosDestinationFolderId(DeezSpoTag.Core.Models.Settings.DeezSpoTagSettings settings)
    {
        var multiQuality = settings.MultiQuality;
        return multiQuality?.SecondaryDestinationFolderId;
    }

    private void LogInfo(string message)
    {
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, "info", message));
    }

    private void UpdateError(string message)
    {
        UpdateState(state => state with
        {
            Status = "error",
            ErrorMessage = message,
            Phase = $"Error: {message}"
        });
    }

    private void UpdateState(Func<QualityScannerState, QualityScannerState> updater)
    {
        lock (_stateLock)
        {
            _state = updater(_state);
        }
    }
}

public sealed class QualityScannerStartRequest
{
    public string Scope { get; init; } = "watchlist";
    public long? FolderId { get; init; }
    public string? MinFormat { get; init; }
    public int? MinBitDepth { get; init; }
    public int? MinSampleRateHz { get; init; }
    public bool? QueueAtmosAlternatives { get; init; }
    public int? CooldownMinutes { get; init; }
    public string Trigger { get; init; } = "manual";
    public bool MarkAutomationWindow { get; init; }
    public IReadOnlyCollection<string>? TechnicalProfiles { get; init; }
    public IReadOnlyCollection<long>? FolderIds { get; init; }
}

public sealed record QualityScannerState(
    string Status,
    string Phase,
    double Progress,
    int Processed,
    int Total,
    int QualityMet,
    int LowQuality,
    int Matched,
    int UpgradesQueued,
    int AtmosQueued,
    int DuplicateSkipped,
    int MatchMissed,
    string ErrorMessage,
    string Scope,
    long? FolderId,
    string Trigger,
    bool QueueAtmosAlternatives,
    int CooldownMinutes,
    long? RunId)
{
    public static QualityScannerState Idle()
        => new("idle", "Ready to scan", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, string.Empty, "watchlist", null, "manual", false, 1440, null);

    public static QualityScannerState Running(
        string scope,
        long? folderId,
        string trigger,
        bool queueAtmosAlternatives,
        int cooldownMinutes)
        => new(
            "running",
            "Initializing scan...",
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            string.Empty,
            scope,
            folderId,
            trigger,
            queueAtmosAlternatives,
            cooldownMinutes,
            null);
}

internal sealed record QualityScannerRunOptions(
    string Scope,
    long? FolderId,
    int? MinFormatRank,
    string? MinFormat,
    int? MinBitDepth,
    int? MinSampleRateHz,
    string Trigger,
    bool QueueAtmosAlternatives,
    int CooldownMinutes,
    long? AtmosDestinationFolderId,
    bool MarkAutomationWindow,
    IReadOnlySet<string> TechnicalProfiles,
    IReadOnlySet<long> FolderIds);

internal sealed record QualityScanMatch(
    string Source,
    string Id,
    string Title,
    string Artist,
    string Album,
    string Isrc,
    int DurationMs,
    double Score,
    bool HasAtmos);

internal sealed record QualityScanShazamMatchContext(
    string Query,
    string Artist,
    string Title,
    string Isrc);

internal sealed record ShazamMatchResolution(
    bool Success,
    QualityScanShazamMatchContext? MatchContext,
    string? ErrorMessage)
{
    public static ShazamMatchResolution Matched(QualityScanShazamMatchContext context) => new(true, context, null);
    public static ShazamMatchResolution Failed(string errorMessage) => new(false, null, errorMessage);
}

internal sealed record ParsedSearchItem(
    string Title,
    string Artist,
    string Album,
    string Isrc);

internal sealed record ParsedSearchCandidate(
    string Source,
    string Id,
    string Title,
    string Artist,
    string Album,
    string Isrc,
    int DurationMs,
    bool HasAtmos);

internal sealed record AtmosQueueResult(bool Queued, bool Duplicate, bool SkippedNoMatch, string? ErrorMessage)
{
    public static AtmosQueueResult QueuedItem() => new(true, false, false, null);
    public static AtmosQueueResult DuplicateItem() => new(false, true, false, null);
    public static AtmosQueueResult NoMatch() => new(false, false, true, null);
    public static AtmosQueueResult Error(string message) => new(false, false, false, message);
}
