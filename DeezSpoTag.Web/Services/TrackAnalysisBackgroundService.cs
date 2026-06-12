using DeezSpoTag.Services.Library;
using NAudio.Vorbis;
using NAudio.Wave;
using NLayer;
using System.Diagnostics;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed class TrackAnalysisBackgroundService : BackgroundService
{
    private const string StandardAnalysisMode = "standard";
    private const string StandardAnalysisVersion = "naudio-basic-1";
    private const string EnhancedAnalysisMode = "enhanced";
    private const string EnhancedAnalysisVersion = "musicnn-1";
    private const string FailedAnalysisStatus = "failed";
    private const string VibeModelsDirectoryEnvironmentVariable = "VIBE_ANALYZER_MODELS";
    private const string VibePythonEnvironmentVariable = "VIBE_ANALYZER_PYTHON";
    private const string VibePathEnvironmentVariable = "VIBE_ANALYZER_PATH";
    private const string VibeAnalyzerTimeoutSecondsEnvironmentVariable = "VIBE_ANALYZER_TIMEOUT_SECONDS";
    private const string VibeAnalyzerBatchTimeoutSecondsEnvironmentVariable = "VIBE_ANALYZER_BATCH_TIMEOUT_SECONDS";
    private const string VibeAnalyzerWorkersEnvironmentVariable = "VIBE_ANALYZER_WORKERS";
    private const string VibeAnalyzerUseBatchEnvironmentVariable = "VIBE_ANALYZER_USE_BATCH";
    private const string VibeEssentiaPackageEnvironmentVariable = "VIBE_ANALYZER_ESSENTIA_TF_PACKAGE";
    private const string VibeAnalyzerForceCpuEnvironmentVariable = "VIBE_ANALYZER_FORCE_CPU";
    private const string DefaultEssentiaPackage = "essentia-tensorflow==2.1b6.dev1389";
    private const string Python3Executable = "python3";
    private const string ToolsDirectoryName = "Tools";
    private const string ModelsDirectoryName = "models";
    private const string VibeAnalyzerScriptFileName = "vibe_analyzer.py";
    private const string DefaultVibeModelsRelativePath = "analysis/models";
    private const string DefaultVibeVenvRelativePath = "analysis/vibe/.venv";
    private const int DefaultVibeAnalyzerTimeoutSeconds = 180;
    private const int MinVibeAnalyzerTimeoutSeconds = 10;
    private const int MaxVibeAnalyzerTimeoutSeconds = 600;
    private const int DefaultVibeAnalyzerBatchTimeoutSeconds = 300;
    private static readonly TimeSpan CompletedStandardEnhancedRetryDelay = TimeSpan.FromMinutes(30);
    private const int MinVibeAnalyzerBatchTimeoutSeconds = 60;
    private const int MaxVibeAnalyzerBatchTimeoutSeconds = 3600;
    private const int MinVibeAnalyzerWorkers = 1;
    private const int MaxVibeAnalyzerWorkers = 16;
    private static readonly TimeSpan MlWarningThrottle = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MlCapabilityRetryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MlBootstrapRetryInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PipInstallTimeout = TimeSpan.FromMinutes(20);
    private static readonly (string FileName, string Url)[] RequiredModelFiles =
    {
        ("msd-musicnn-1.pb", "https://essentia.upf.edu/models/feature-extractors/musicnn/msd-musicnn-1.pb"),
        ("mood_happy-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/mood_happy/mood_happy-msd-musicnn-1.pb"),
        ("mood_sad-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/mood_sad/mood_sad-msd-musicnn-1.pb"),
        ("mood_relaxed-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/mood_relaxed/mood_relaxed-msd-musicnn-1.pb"),
        ("mood_aggressive-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/mood_aggressive/mood_aggressive-msd-musicnn-1.pb"),
        ("mood_party-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/mood_party/mood_party-msd-musicnn-1.pb"),
        ("mood_acoustic-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/mood_acoustic/mood_acoustic-msd-musicnn-1.pb"),
        ("mood_electronic-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/mood_electronic/mood_electronic-msd-musicnn-1.pb"),
        ("voice_instrumental-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/voice_instrumental/voice_instrumental-msd-musicnn-1.pb"),
        ("tonal_atonal-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/tonal_atonal/tonal_atonal-msd-musicnn-1.pb"),
        ("danceability-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/danceability/danceability-msd-musicnn-1.pb"),
        ("deam-msd-musicnn-2.pb", "https://essentia.upf.edu/models/classification-heads/deam/deam-msd-musicnn-2.pb"),
        ("discogs-effnet-bs64-1.pb", "https://essentia.upf.edu/models/feature-extractors/discogs-effnet/discogs-effnet-bs64-1.pb"),
        ("approachability_regression-discogs-effnet-1.pb", "https://essentia.upf.edu/models/classification-heads/approachability/approachability_regression-discogs-effnet-1.pb"),
        ("engagement_regression-discogs-effnet-1.pb", "https://essentia.upf.edu/models/classification-heads/engagement/engagement_regression-discogs-effnet-1.pb"),
        ("genre_discogs400-discogs-effnet-1.pb", "https://essentia.upf.edu/models/classification-heads/genre_discogs400/genre_discogs400-discogs-effnet-1.pb"),
        ("genre_discogs400-discogs-effnet-1.json", "https://essentia.upf.edu/models/classification-heads/genre_discogs400/genre_discogs400-discogs-effnet-1.json")
    };
    private static readonly string[] RequiredEnhancedModelFiles =
    {
        "msd-musicnn-1.pb",
        "mood_happy-msd-musicnn-1.pb",
        "mood_sad-msd-musicnn-1.pb",
        "mood_relaxed-msd-musicnn-1.pb",
        "mood_aggressive-msd-musicnn-1.pb"
    };
    private static readonly HttpClient MlBootstrapHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };
    private readonly LibraryRepository _repository;
    private readonly ILogger<TrackAnalysisBackgroundService> _logger;
    private readonly LibraryConfigStore _configStore;
    private readonly VibeAnalysisSettingsStore _settingsStore;
    private readonly LastFmTagService _lastFmTagService;
    private readonly MoodBucketService _moodBucketService;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _analysisLock = new(1, 1);
    private readonly SemaphoreSlim _manualRunSignal = new(0, 1);
    private readonly object _runtimeLock = new();
    private readonly List<VibeAnalysisRecentItemDto> _recentAnalyses = new();
    private readonly object _mlCapabilityLock = new();
    private CancellationTokenSource? _activeRunCancellation;
    private bool _manualRunPending;
    private int _manualRunBatchSize;
    private string _runtimeState = VibeAnalysisRuntimeStates.Idle;
    private LatestTrackAnalysisDto? _currentAnalysis;
    private LatestTrackAnalysisDto? _latestAnalysis;
    private DateTimeOffset? _runtimeUpdatedAtUtc;
    private MlCapability? _mlCapability;
    private DateTimeOffset _mlCapabilityLastCheckedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _mlBootstrapLastAttemptAt = DateTimeOffset.MinValue;
    private DateTimeOffset _mlLastWarningLoggedAt = DateTimeOffset.MinValue;
    private static readonly string? FfmpegExecutablePath = FfmpegPathResolver.ResolveExecutable();
    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TrackAnalysisBackgroundService(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        ILogger<TrackAnalysisBackgroundService> logger,
        VibeAnalysisSettingsStore settingsStore,
        LastFmTagService lastFmTagService,
        MoodBucketService moodBucketService,
        IConfiguration configuration)
    {
        _repository = repository;
        _configStore = configStore;
        _logger = logger;
        _settingsStore = settingsStore;
        _lastFmTagService = lastFmTagService;
        _moodBucketService = moodBucketService;
        _configuration = configuration;
    }

    public async Task ApplySettingsAsync(VibeAnalysisSettingsDto settings, CancellationToken cancellationToken)
    {
        if (settings.Enabled)
        {
            return;
        }

        PauseActiveRun();
        await Task.CompletedTask;
    }

    public bool TryStartManualAnalysis(int batchSize)
    {
        var shouldSignal = false;
        lock (_runtimeLock)
        {
            if (_manualRunPending || _activeRunCancellation is not null || _analysisLock.CurrentCount == 0)
            {
                return false;
            }

            _manualRunBatchSize = Math.Clamp(batchSize, 10, 500);
            _manualRunPending = true;
            _runtimeUpdatedAtUtc = DateTimeOffset.UtcNow;
            shouldSignal = true;
        }

        if (shouldSignal)
        {
            _manualRunSignal.Release();
        }

        return true;
    }

    public VibeAnalysisRuntimeDto GetRuntimeSnapshot()
    {
        lock (_runtimeLock)
        {
            return new VibeAnalysisRuntimeDto(
                _runtimeState,
                string.Equals(_runtimeState, VibeAnalysisRuntimeStates.Running, StringComparison.OrdinalIgnoreCase),
                _currentAnalysis,
                _latestAnalysis,
                _recentAnalyses.ToArray(),
                _runtimeUpdatedAtUtc);
        }
    }

    private void PauseActiveRun()
    {
        CancellationTokenSource? active;
        lock (_runtimeLock)
        {
            active = _activeRunCancellation;
            if (active is null)
            {
                _runtimeState = VibeAnalysisRuntimeStates.Paused;
                _currentAnalysis = null;
                _runtimeUpdatedAtUtc = DateTimeOffset.UtcNow;
                return;
            }

            _runtimeState = VibeAnalysisRuntimeStates.Pausing;
            _runtimeUpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        active.Cancel();
    }

    private RuntimeRunScope BeginRuntimeRun(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_runtimeLock)
        {
            _activeRunCancellation = linked;
            _runtimeState = VibeAnalysisRuntimeStates.Running;
            _runtimeUpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        return new RuntimeRunScope(this, linked);
    }

    private void CompleteRuntimeRun(CancellationTokenSource runCancellation)
    {
        lock (_runtimeLock)
        {
            if (!ReferenceEquals(_activeRunCancellation, runCancellation))
            {
                return;
            }

            _activeRunCancellation = null;
            _currentAnalysis = null;
            _runtimeState = runCancellation.IsCancellationRequested
                ? VibeAnalysisRuntimeStates.Paused
                : VibeAnalysisRuntimeStates.Idle;
            _runtimeUpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private void SetCurrentAnalysis(TrackAnalysisInputDto track, MixTrackDto? summary)
    {
        var current = BuildLatestTrackAnalysisDto(track, summary, "processing", DateTimeOffset.UtcNow, null);
        lock (_runtimeLock)
        {
            _currentAnalysis = current;
            _runtimeState = VibeAnalysisRuntimeStates.Running;
            _runtimeUpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private void RecordCompletedAnalysis(MixTrackDto? summary, TrackAnalysisResultDto result)
    {
        if (!IsAnalysisCompleteStatus(result.Status))
        {
            return;
        }

        var track = summary ?? new MixTrackDto(result.TrackId, $"Track {result.TrackId}", "Unknown", "Unknown", null, null);
        var latest = new LatestTrackAnalysisDto(track, result);
        var recent = new VibeAnalysisRecentItemDto(
            track.TrackId,
            $"{track.Title} · {track.ArtistName}",
            result.Status,
            result.AnalyzedAtUtc ?? DateTimeOffset.UtcNow);

        lock (_runtimeLock)
        {
            _latestAnalysis = latest;
            _recentAnalyses.RemoveAll(item => item.TrackId == track.TrackId);
            _recentAnalyses.Insert(0, recent);
            if (_recentAnalyses.Count > 10)
            {
                _recentAnalyses.RemoveRange(10, _recentAnalyses.Count - 10);
            }

            _runtimeUpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private static LatestTrackAnalysisDto BuildLatestTrackAnalysisDto(
        TrackAnalysisInputDto track,
        MixTrackDto? summary,
        string status,
        DateTimeOffset? analyzedAtUtc,
        string? error)
    {
        var mixTrack = summary ?? new MixTrackDto(track.TrackId, $"Track {track.TrackId}", "Unknown", "Unknown", null, track.DurationMs);
        var analysis = CreateFailure(track.TrackId, track.LibraryId, status, error ?? string.Empty) with
        {
            AnalyzedAtUtc = analyzedAtUtc
        };
        return new LatestTrackAnalysisDto(mixTrack, analysis);
    }

    private async Task ResetInterruptedProcessingRowsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _repository.ResetProcessingTrackAnalysisAsync(cancellationToken);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Failed to reset interrupted vibe analysis processing rows.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!BackgroundAutomationPolicy.IsEnabled(_configuration, "VibeAnalysis"))
        {
            return;
        }

        await ResetInterruptedProcessingRowsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (TryConsumeManualRun(out var manualBatchSize))
                {
                    await AnalyzeNowAsync(manualBatchSize, stoppingToken, forceWhenDisabled: true);
                    continue;
                }

                var settings = await _settingsStore.LoadAsync();
                if (settings.Enabled)
                {
                    await RunScheduledAnalysisBatchAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                await ResetInterruptedProcessingRowsAsync(CancellationToken.None);
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "info",
                    "Vibe analysis paused."));
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                _logger.LogWarning(ex, "Track analysis pass failed.");
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "error",
                    $"Vibe analysis failed: {ex.Message}"));
            }

            try
            {
                var settings = await _settingsStore.LoadAsync();
                var delay = TimeSpan.FromMinutes(Math.Clamp(settings.IntervalMinutes, 5, 240));
                await WaitForNextAnalysisWakeAsync(delay, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private bool TryConsumeManualRun(out int batchSize)
    {
        lock (_runtimeLock)
        {
            if (!_manualRunPending)
            {
                batchSize = 0;
                return false;
            }

            batchSize = _manualRunBatchSize;
            _manualRunPending = false;
            _manualRunBatchSize = 0;
            return true;
        }
    }

    private async Task RunScheduledAnalysisBatchAsync(CancellationToken stoppingToken)
    {
        await _analysisLock.WaitAsync(stoppingToken);
        try
        {
            var settings = await _settingsStore.LoadAsync();
            if (settings.Enabled)
            {
                using var run = BeginRuntimeRun(stoppingToken);
                await AnalyzeBatchAsync(settings, stopWhenDisabled: true, run.Token);
            }
        }
        finally
        {
            _analysisLock.Release();
        }
    }

    private async Task WaitForNextAnalysisWakeAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var delayTask = Task.Delay(delay, delayCancellation.Token);
        var manualRunTask = _manualRunSignal.WaitAsync(stoppingToken);
        var completedTask = await Task.WhenAny(delayTask, manualRunTask);
        if (completedTask == manualRunTask)
        {
            await manualRunTask;
            await delayCancellation.CancelAsync();
            return;
        }

        await delayTask;
    }

    public async Task AnalyzeNowAsync(int batchSize, CancellationToken cancellationToken, bool forceWhenDisabled = false)
    {
        await _analysisLock.WaitAsync(cancellationToken);
        try
        {
            using var run = BeginRuntimeRun(cancellationToken);
            try
            {
                while (!run.Token.IsCancellationRequested)
                {
                    var settings = await _settingsStore.LoadAsync();
                    if (!forceWhenDisabled && !settings.Enabled)
                    {
                        break;
                    }

                    var effectiveBatch = forceWhenDisabled ? batchSize : settings.BatchSize;
                    var processed = await AnalyzeBatchAsync(
                        settings with { BatchSize = Math.Clamp(effectiveBatch, 10, 500) },
                        stopWhenDisabled: !forceWhenDisabled,
                        run.Token);
                    if (processed == 0)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await ResetInterruptedProcessingRowsAsync(CancellationToken.None);
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "info",
                    "Vibe analysis paused."));
            }
        }
        finally
        {
            _analysisLock.Release();
        }
    }

    public async Task<bool> AnalyzeTrackByIdAsync(long trackId, CancellationToken cancellationToken)
    {
        if (trackId <= 0)
        {
            return false;
        }

        await _analysisLock.WaitAsync(cancellationToken);
        try
        {
            using var run = BeginRuntimeRun(cancellationToken);
            var track = await _repository.GetTrackForAnalysisAsync(trackId, cancellationToken);
            if (track is null)
            {
                return false;
            }

            var summaries = await _repository.GetTrackSummariesAsync(new List<long> { track.TrackId }, run.Token);
            var summary = summaries.Count > 0 ? summaries[0] : null;
            SetCurrentAnalysis(track, summary);
            await _repository.MarkTrackAnalysisProcessingAsync(track.TrackId, track.LibraryId, run.Token);
            var result = await AnalyzeTrackAsync(track, null, run.Token);
            if (result.LastfmTags is null && summary is not null)
            {
                var tags = await _lastFmTagService.GetTrackTagsAsync(summary.ArtistName, summary.Title, run.Token);
                if (tags is not null)
                {
                    result = result with { LastfmTags = tags };
                }
            }

            await _repository.UpsertTrackAnalysisAsync(result, run.Token);
            var isComplete = IsAnalysisCompleteStatus(result.Status);

            if (isComplete)
            {
                try
                {
                    await _moodBucketService.AssignTrackToMoodsAsync(track.TrackId, run.Token);
                }
                catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
                {
                    _logger.LogWarning(ex, "Mood bucket assignment failed for track {TrackId}", track.TrackId);
                }
            }

            RecordCompletedAnalysis(summary, result);

            return isComplete;
        }
        finally
        {
            _analysisLock.Release();
        }
    }

    private async Task<int> AnalyzeBatchAsync(VibeAnalysisSettingsDto settings, bool stopWhenDisabled, CancellationToken cancellationToken)
    {
        var includeCompletedStandard = IsEnhancedAnalysisAvailableForRetry();
        var orderedLibraryIds = settings.UseLibraryOrder ? settings.LibraryOrder : Array.Empty<long>();
        var tracks = await _repository.GetTracksForAnalysisAsync(
            Math.Clamp(settings.BatchSize, 10, 500),
            includeCompletedStandard: includeCompletedStandard,
            completedStandardRetryBeforeUtc: includeCompletedStandard ? DateTimeOffset.UtcNow.Subtract(CompletedStandardEnhancedRetryDelay) : null,
            orderedLibraryIds: orderedLibraryIds,
            cancellationToken: cancellationToken);
        if (tracks.Count == 0)
        {
            return 0;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Vibe analysis started ({tracks.Count} tracks)."));

        var completed = 0;
        var errors = 0;
        var errorBuckets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<long, BatchPrediction>? batchPredictions = null;
        if (ResolveUseBatchAnalyzer())
        {
            batchPredictions = await TryPredictAnalysisOutputBatchAsync(tracks, cancellationToken);
        }
        var summaries = await _repository.GetTrackSummariesAsync(tracks.Select(t => t.TrackId).ToList(), cancellationToken);
        var summaryMap = summaries.ToDictionary(item => item.TrackId);
        var processedTracks = 0;
        foreach (var track in tracks)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (stopWhenDisabled && await ShouldStopForDisabledAnalysisAsync(cancellationToken))
            {
                break;
            }

            await _repository.MarkTrackAnalysisProcessingAsync(track.TrackId, track.LibraryId, cancellationToken);
            summaryMap.TryGetValue(track.TrackId, out var summary);
            SetCurrentAnalysis(track, summary);
            var result = await AnalyzeTrackWithOptionalLastFmAsync(track, summary, batchPredictions, cancellationToken);
            await _repository.UpsertTrackAnalysisAsync(result, cancellationToken);
            processedTracks++;
            if (IsAnalysisCompleteStatus(result.Status))
            {
                completed++;
                await AssignTrackMoodBucketsAsync(track.TrackId, cancellationToken);
                RecordCompletedAnalysis(summary, result);
            }
            else if (IsAnalysisErrorStatus(result.Status))
            {
                errors++;
                RecordErrorBucket(errorBuckets, result.Error);
            }
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Vibe analysis completed (ok={completed}, errors={errors})."));

        if (errors > 0)
        {
            if (errorBuckets.Count == 0)
            {
                errorBuckets["Unknown error"] = errors;
            }
            var topReasons = errorBuckets
                .OrderByDescending(item => item.Value)
                .Take(3)
                .Select(item => $"{item.Key} ({item.Value})");
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Vibe analysis errors: {string.Join(", ", topReasons)}"));
        }

        return processedTracks;
    }

    private bool IsEnhancedAnalysisAvailableForRetry()
    {
        var capability = GetOrProbeMlCapability();
        if (!capability.Available)
        {
            LogMlUnavailable(capability.Reason ?? "Unknown reason.");
        }

        return capability.Available;
    }

    private async Task<bool> ShouldStopForDisabledAnalysisAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = await _settingsStore.LoadAsync();
        if (settings.Enabled)
        {
            return false;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            "Vibe analysis halted (disabled)."));
        return true;
    }

    private static bool IsAnalysisCompleteStatus(string? status)
    {
        return string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAnalysisErrorStatus(string? status)
    {
        return string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, FailedAnalysisStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static void RecordErrorBucket(Dictionary<string, int> errorBuckets, string? error)
    {
        var reason = string.IsNullOrWhiteSpace(error) ? "Unknown error" : error;
        errorBuckets[reason] = errorBuckets.TryGetValue(reason, out var count) ? count + 1 : 1;
    }

    private async Task AssignTrackMoodBucketsAsync(long trackId, CancellationToken cancellationToken)
    {
        try
        {
            await _moodBucketService.AssignTrackToMoodsAsync(trackId, cancellationToken);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Mood bucket assignment failed for track {TrackId}", trackId);
        }
    }

    private async Task<TrackAnalysisResultDto> AnalyzeTrackWithOptionalLastFmAsync(
        TrackAnalysisInputDto track,
        MixTrackDto? summary,
        IReadOnlyDictionary<long, BatchPrediction>? batchPredictions,
        CancellationToken cancellationToken)
    {
        var result = await AnalyzeTrackAsync(track, batchPredictions, cancellationToken);
        if (result.LastfmTags is not null || summary is null)
        {
            return result;
        }

        var tags = await _lastFmTagService.GetTrackTagsAsync(summary.ArtistName, summary.Title, cancellationToken);
        return tags is null
            ? result
            : result with { LastfmTags = tags };
    }

    private async Task<TrackAnalysisResultDto> AnalyzeTrackAsync(
        TrackAnalysisInputDto track,
        IReadOnlyDictionary<long, BatchPrediction>? batchPredictions,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryLoadTrackSamples(track, out var samples, out var sampleRate, out var failure))
            {
                return failure!;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (samples.Length == 0)
            {
                return CreateFailure(track.TrackId, track.LibraryId, FailedAnalysisStatus, "No audio samples.");
            }

            var metrics = CalculateTrackSignalMetrics(samples, sampleRate, track.DurationMs);
            AnalysisOutput? analysisOutput;
            string? predictionFailure;
            if (batchPredictions is not null && batchPredictions.TryGetValue(track.TrackId, out var batchPrediction))
            {
                analysisOutput = batchPrediction.Output;
                predictionFailure = batchPrediction.FailureReason;
                if (analysisOutput is null)
                {
                    var (singleTrackOutput, singleTrackFailure) = await TryPredictAnalysisOutputAsync(track.FilePath, cancellationToken);
                    if (singleTrackOutput is not null)
                    {
                        analysisOutput = singleTrackOutput;
                        predictionFailure = null;
                    }
                    else
                    {
                        predictionFailure = CombinePredictionFailures(predictionFailure, singleTrackFailure);
                    }
                }
            }
            else
            {
                (analysisOutput, predictionFailure) = await TryPredictAnalysisOutputAsync(track.FilePath, cancellationToken);
            }

            if (analysisOutput is null
                && !string.IsNullOrWhiteSpace(predictionFailure))
            {
                _logger.LogWarning("Vibe analyzer fallback to standard for {FilePath}: {Reason}", track.FilePath, predictionFailure);
            }

            return CreateCompletedAnalysisResult(track, metrics, analysisOutput);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return CreateFailure(track.TrackId, track.LibraryId, FailedAnalysisStatus, ex.Message);
        }
    }

    private static string? CombinePredictionFailures(string? batchFailure, string? singleTrackFailure)
    {
        if (string.IsNullOrWhiteSpace(batchFailure))
        {
            return singleTrackFailure;
        }

        if (string.IsNullOrWhiteSpace(singleTrackFailure)
            || string.Equals(batchFailure, singleTrackFailure, StringComparison.OrdinalIgnoreCase))
        {
            return batchFailure;
        }

        return $"{batchFailure}; single-track retry failed: {singleTrackFailure}";
    }

    private async Task<Dictionary<long, BatchPrediction>> TryPredictAnalysisOutputBatchAsync(
        IReadOnlyList<TrackAnalysisInputDto> tracks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tracks.Count == 0)
        {
            return new Dictionary<long, BatchPrediction>();
        }

        if (!TryResolveBatchAnalyzerContext(tracks, out var context, out var failureMap))
        {
            return failureMap;
        }

        var request = tracks.Select(track => new BatchAnalysisRequestItem(track.TrackId, track.FilePath)).ToList();
        var batchTempFilePath = WriteBatchRequestToSecureTempFile(request);
        try
        {
            using var process = Process.Start(CreateBatchProcessStartInfo(context, batchTempFilePath));
            if (process is null)
            {
                return CreateBatchFailureMap(tracks, "Failed to start vibe analyzer batch process.");
            }

            var execution = await ExecuteBatchAnalyzerProcessAsync(process, context.BatchTimeout, context.BatchTimeoutSeconds, cancellationToken);
            if (!execution.Succeeded)
            {
                if (execution.TimedOut)
                {
                    _logger.LogWarning("Vibe analysis ML batch timed out after {BatchTimeoutSeconds}s.", context.BatchTimeoutSeconds);
                }
                else if (!string.IsNullOrWhiteSpace(execution.ErrorOutput))
                {
                    _logger.LogWarning("Vibe analysis ML batch failed: {Error}", execution.ErrorOutput);
                }

                return CreateBatchFailureMap(tracks, execution.FailureReason);
            }

            if (TryReadAnalyzerFailure(execution.Output, out var analyzerFailure))
            {
                if (IsMlCapabilityFailure(analyzerFailure.ErrorCode))
                {
                    SetMlCapabilityUnavailable(analyzerFailure.Reason);
                    LogMlUnavailable(analyzerFailure.Reason);
                }

                return CreateBatchFailureMap(tracks, analyzerFailure.Reason);
            }

            var parsed = JsonSerializer.Deserialize<BatchAnalysisResponse>(execution.Output, CaseInsensitiveJsonOptions);
            if (parsed?.Results is null)
            {
                return CreateBatchFailureMap(tracks, "Vibe analyzer batch returned an invalid payload.");
            }

            return BuildBatchPredictionMap(tracks, parsed.Results);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Vibe analysis ML batch failed.");
            return CreateBatchFailureMap(tracks, ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(batchTempFilePath))
                {
                    File.Delete(batchTempFilePath);
                }
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                // Best effort temporary file cleanup.
            }
        }
    }

    private bool TryResolveBatchAnalyzerContext(
        IReadOnlyList<TrackAnalysisInputDto> tracks,
        out BatchAnalyzerContext context,
        out Dictionary<long, BatchPrediction> failureMap)
    {
        failureMap = new Dictionary<long, BatchPrediction>();
        context = default!;

        var capability = GetOrProbeMlCapability();
        if (!capability.Available)
        {
            var reason = capability.Reason ?? "Unknown reason.";
            LogMlUnavailable(reason);
            failureMap = CreateBatchFailureMap(tracks, reason);
            return false;
        }

        var scriptPath = ResolveAnalyzerScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            var reason = $"Analyzer script missing at {scriptPath}. Set {VibePathEnvironmentVariable} or ensure {ToolsDirectoryName}/{VibeAnalyzerScriptFileName} exists.";
            LogMlUnavailable(reason);
            failureMap = CreateBatchFailureMap(tracks, reason);
            return false;
        }

        var modelsDir = ResolveModelsDirectory();
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
        {
            var reason = $"Models directory missing at {modelsDir}. Set {VibeModelsDirectoryEnvironmentVariable} or place models under {ToolsDirectoryName}/{ModelsDirectoryName}.";
            LogMlUnavailable(reason);
            failureMap = CreateBatchFailureMap(tracks, reason);
            return false;
        }

        var batchTimeout = ResolveAnalyzerBatchTimeout();
        context = new BatchAnalyzerContext(
            scriptPath,
            modelsDir,
            ResolveAnalyzerWorkers(),
            (int)ResolveAnalyzerTimeout().TotalSeconds,
            batchTimeout,
            (int)batchTimeout.TotalSeconds);
        return true;
    }

    private static ProcessStartInfo CreateBatchProcessStartInfo(BatchAnalyzerContext context, string batchTempFilePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePythonExecutable(),
            Arguments =
                $"\"{context.ScriptPath}\" --batch-json \"{batchTempFilePath}\" --models \"{context.ModelsDir}\" --workers {context.Workers} --per-track-timeout-seconds {context.PerTrackTimeoutSeconds} --batch-timeout-seconds {context.BatchTimeoutSeconds}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        ConfigurePythonEnvironment(startInfo);
        return startInfo;
    }

    private static async Task<BatchProcessExecution> ExecuteBatchAnalyzerProcessAsync(
        Process process,
        TimeSpan batchTimeout,
        int batchTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var externalBatchTimeout = batchTimeout + TimeSpan.FromSeconds(30);
        using var timeout = new CancellationTokenSource(externalBatchTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            return new BatchProcessExecution(
                Succeeded: false,
                TimedOut: true,
                Output: string.Empty,
                ErrorOutput: string.Empty,
                FailureReason: $"Vibe analyzer batch timed out after {batchTimeoutSeconds}s.");
        }

        var output = process.StandardOutput.ReadToEnd();
        var errorOutput = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0)
        {
            var failureReason = string.IsNullOrWhiteSpace(errorOutput)
                ? "Vibe analyzer batch process failed."
                : errorOutput.Trim();
            return new BatchProcessExecution(
                Succeeded: false,
                TimedOut: false,
                Output: output,
                ErrorOutput: errorOutput,
                FailureReason: failureReason);
        }

        return new BatchProcessExecution(
            Succeeded: true,
            TimedOut: false,
            Output: output,
            ErrorOutput: errorOutput,
            FailureReason: string.Empty);
    }

    private static Dictionary<long, BatchPrediction> BuildBatchPredictionMap(
        IReadOnlyList<TrackAnalysisInputDto> tracks,
        IReadOnlyList<BatchAnalysisItem> results)
    {
        var predictionMap = new Dictionary<long, BatchPrediction>();
        foreach (var item in results.Where(item => item.TrackId is not null))
        {
            var trackId = item.TrackId!.Value;
            predictionMap[trackId] = item.Ok && item.Payload is not null
                ? new BatchPrediction(item.Payload, null)
                : new BatchPrediction(null, BuildBatchFailureReason(item.ErrorCode, item.Message));
        }

        foreach (var track in tracks.Where(track => !predictionMap.ContainsKey(track.TrackId)))
        {
            predictionMap[track.TrackId] = new BatchPrediction(null, "Vibe analyzer batch produced no result.");
        }

        return predictionMap;
    }

    private static string WriteBatchRequestToSecureTempFile(IReadOnlyList<BatchAnalysisRequestItem> request)
    {
        var tempDirectory = ResolveBatchRequestTempDirectory();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidatePath = Path.Join(tempDirectory, $"{Path.GetRandomFileName()}.json");
            try
            {
                using var stream = OpenSecureTempFile(candidatePath);
                JsonSerializer.Serialize(stream, request);
                return candidatePath;
            }
            catch (IOException)
            {
                // Retry with a different random candidate on any create/write race.
            }
        }

        throw new IOException("Unable to create a secure temporary file for vibe batch analysis.");
    }

    private static string ResolveBatchRequestTempDirectory()
    {
        var dataRoot = ResolveDataRootPath();
        var baseDirectory = string.IsNullOrWhiteSpace(dataRoot) ? AppContext.BaseDirectory : dataRoot;
        var tempDirectory = Path.Join(baseDirectory, "analysis", "tmp", "vibe");
        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }

    private static FileStream OpenSecureTempFile(string candidatePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(candidatePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }

        return new FileStream(candidatePath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }


    private static bool IsFfmpegHandledExtension(string? extension)
    {
        return extension is ".flac" or ".m4a" or ".m4b" or ".aac" or ".opus" or ".wma" or ".ape" or ".ogg" or ".oga";
    }

    private static bool TryLoadTrackSamples(
        TrackAnalysisInputDto track,
        out float[] samples,
        out int sampleRate,
        out TrackAnalysisResultDto? failure)
    {
        samples = Array.Empty<float>();
        sampleRate = 0;
        failure = null;
        if (!File.Exists(track.FilePath))
        {
            failure = CreateFailure(track.TrackId, track.LibraryId, FailedAnalysisStatus, "File not found.");
            return false;
        }

        var extension = Path.GetExtension(track.FilePath)?.ToLowerInvariant();
        if (string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadMp3Samples(track.FilePath, 30, out samples, out sampleRate, out var mp3Error))
            {
                failure = CreateFailure(track.TrackId, track.LibraryId, FailedAnalysisStatus, mp3Error ?? "Unable to decode mp3.");
                return false;
            }

            return true;
        }

        if (IsFfmpegHandledExtension(extension))
        {
            if (!TryReadWithFfmpeg(track.FilePath, 30, out samples, out sampleRate, out var ffmpegError))
            {
                failure = CreateFailure(track.TrackId, track.LibraryId, FailedAnalysisStatus, ffmpegError ?? $"Unable to decode {extension}.");
                return false;
            }

            return true;
        }

        using var reader = OpenAudioStream(track.FilePath, out var errorMessage);
        if (reader is null)
        {
            failure = CreateFailure(track.TrackId, track.LibraryId, FailedAnalysisStatus, errorMessage ?? "Unsupported audio format.");
            return false;
        }

        sampleRate = reader.WaveFormat.SampleRate;
        samples = ReadSamples(reader.ToSampleProvider(), 30);
        return true;
    }

    private sealed record TrackSignalMetrics(
        double Energy,
        double Rms,
        double ZeroCrossing,
        double Centroid,
        double Bpm,
        int? BeatsCount,
        string? Key,
        double? KeyStrength,
        double? Loudness,
        double? DynamicRange,
        double Brightness);

    private static TrackSignalMetrics CalculateTrackSignalMetrics(float[] samples, int sampleRate, int? durationMs)
    {
        var rms = Math.Sqrt(samples.Select(sample => sample * sample).Average());
        var energy = samples.Select(sample => Math.Abs(sample)).Average();
        var zeroCrossing = CalculateZeroCrossingRate(samples);
        var centroid = CalculateSpectralCentroid(samples, sampleRate);
        var bpm = EstimateBpm(samples, sampleRate);
        var beatsCount = CalculateBeatsCount(bpm, durationMs);
        var dominant = CalculateDominantFrequency(samples, sampleRate);
        var (key, keyStrength) = MapFrequencyToKey(dominant.FrequencyHz, dominant.Strength);
        var loudness = rms > 0 ? 20 * Math.Log10(rms) : (double?)null;
        var dynamicRange = CalculateDynamicRange(samples);
        var brightness = sampleRate > 0 ? centroid / (sampleRate * 0.5) : 0;
        return new TrackSignalMetrics(
            energy,
            rms,
            zeroCrossing,
            centroid,
            bpm,
            beatsCount,
            key,
            keyStrength,
            loudness,
            dynamicRange,
            brightness);
    }

    private static TrackAnalysisResultDto CreateCompletedAnalysisResult(
        TrackAnalysisInputDto track,
        TrackSignalMetrics metrics,
        AnalysisOutput? analysisOutput)
    {
        var moodScores = analysisOutput?.MoodScores;
        var analyzerReportedMode = analysisOutput?.AnalysisMode;
        var isEnhanced = !string.IsNullOrWhiteSpace(analyzerReportedMode)
            ? string.Equals(analyzerReportedMode, EnhancedAnalysisMode, StringComparison.OrdinalIgnoreCase)
            : moodScores is not null;
        var keyScale = analysisOutput?.KeyScale;
        if (string.IsNullOrWhiteSpace(keyScale) && moodScores is not null)
        {
            keyScale = moodScores.Happy >= moodScores.Sad ? "major" : "minor";
        }

        var moodTags = NormalizeMoodTags(analysisOutput?.MoodTags);
        if (moodTags.Length == 0)
        {
            moodTags = BuildMoodTags(moodScores);
        }
        var analysisMode = isEnhanced ? EnhancedAnalysisMode : StandardAnalysisMode;
        var analysisVersion = isEnhanced ? EnhancedAnalysisVersion : StandardAnalysisVersion;
        var valence = moodScores is null ? (double?)null : CalculateValence(moodScores);
        var arousal = moodScores is null ? (double?)null : CalculateArousal(moodScores);
        var danceability = analysisOutput?.Danceability ?? CalculateDanceability(metrics.Energy, metrics.Bpm);
        var acousticness = analysisOutput?.Acousticness ?? CalculateAcousticness(metrics.Brightness, metrics.ZeroCrossing);
        var speechiness = analysisOutput?.Speechiness ?? CalculateSpeechiness(metrics.ZeroCrossing, metrics.Brightness);
        var instrumentalness = speechiness.HasValue ? Math.Clamp(1 - speechiness.Value, 0, 1) : (double?)null;
        var danceabilityMl = analysisOutput?.DanceabilityMl ?? analysisOutput?.Danceability ?? danceability;

        return new TrackAnalysisResultDto(
            track.TrackId,
            track.LibraryId,
            "completed",
            metrics.Energy,
            metrics.Rms,
            metrics.ZeroCrossing,
            metrics.Centroid,
            metrics.Bpm > 0 ? metrics.Bpm : null,
            DateTimeOffset.UtcNow,
            null,
            analysisMode,
            analysisVersion,
            moodTags,
            moodScores?.Happy,
            moodScores?.Sad,
            moodScores?.Relaxed,
            moodScores?.Aggressive,
            moodScores?.Party,
            moodScores?.Acoustic,
            moodScores?.Electronic,
            valence,
            arousal,
            analysisOutput?.BeatsCount ?? metrics.BeatsCount,
            analysisOutput?.Key ?? metrics.Key,
            keyScale,
            analysisOutput?.KeyStrength ?? metrics.KeyStrength,
            metrics.Loudness,
            metrics.DynamicRange,
            danceability,
            analysisOutput?.Instrumentalness ?? instrumentalness,
            acousticness,
            speechiness,
            danceabilityMl,
            analysisOutput?.Genres,
            null,
            analysisOutput?.Approachability,
            analysisOutput?.Engagement,
            analysisOutput?.VoiceInstrumental,
            analysisOutput?.TonalAtonal,
            analysisOutput?.ValenceMl,
            analysisOutput?.ArousalMl,
            analysisOutput?.DynamicComplexity,
            analysisOutput?.Loudness);
    }

    private static TrackAnalysisResultDto CreateFailure(
        long trackId,
        long? libraryId,
        string status,
        string error)
    {
        return new TrackAnalysisResultDto(
            trackId,
            libraryId,
            status,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            error,
            StandardAnalysisMode,
            StandardAnalysisVersion,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            // Vibe analysis - new fields (all null for failure)
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private static string[] BuildMoodTags(MoodScores? moodScores)
    {
        if (moodScores is null)
        {
            return Array.Empty<string>();
        }

        var tags = new List<string>();
        AddMoodTag(tags, "happy", moodScores.Happy);
        AddMoodTag(tags, "sad", moodScores.Sad);
        AddMoodTag(tags, "relaxed", moodScores.Relaxed);
        AddMoodTag(tags, "aggressive", moodScores.Aggressive);
        AddMoodTag(tags, "party", moodScores.Party);
        AddMoodTag(tags, "acoustic", moodScores.Acoustic);
        AddMoodTag(tags, "electronic", moodScores.Electronic);
        return tags.ToArray();
    }

    private static string[] NormalizeMoodTags(IReadOnlyList<string>? moodTags)
    {
        if (moodTags is null || moodTags.Count == 0)
        {
            return Array.Empty<string>();
        }

        return moodTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddMoodTag(List<string> tags, string mood, double? score)
    {
        if (score is null)
        {
            return;
        }

        if (score >= 0.6)
        {
            tags.Add(mood);
        }
    }

    private async Task<(AnalysisOutput? Output, string? FailureReason)> TryPredictAnalysisOutputAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capability = GetOrProbeMlCapability();
        if (!capability.Available)
        {
            var reason = capability.Reason ?? "Unknown reason.";
            LogMlUnavailable(reason);
            return (null, reason);
        }

        var scriptPath = ResolveAnalyzerScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            var reason = $"Analyzer script missing at {scriptPath}. Set VIBE_ANALYZER_PATH or ensure Tools/vibe_analyzer.py exists.";
            LogMlUnavailable(reason);
            return (null, reason);
        }

        var modelsDir = ResolveModelsDirectory();
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
        {
            var reason = $"Models directory missing at {modelsDir}. Set VIBE_ANALYZER_MODELS or place models under Tools/models.";
            LogMlUnavailable(reason);
            return (null, reason);
        }

        try
        {
            var analysisTimeout = ResolveAnalyzerTimeout();
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolvePythonExecutable(),
                Arguments = $"\"{scriptPath}\" --file \"{filePath}\" --models \"{modelsDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            ConfigurePythonEnvironment(startInfo);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (null, "Failed to start vibe analyzer process.");
            }

            using var timeout = new CancellationTokenSource(analysisTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryTerminate(process);
                throw;
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                _logger.LogWarning(
                    "Vibe analysis ML timed out for {FilePath} after {TimeoutSeconds}s",
                    filePath,
                    (int)analysisTimeout.TotalSeconds);
                return (null, "Vibe analysis ML timed out.");
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorOutput = await process.StandardError.ReadToEndAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Vibe analysis ML failed for {FilePath}: {Error}", filePath, errorOutput);
                return (null, string.IsNullOrWhiteSpace(errorOutput) ? "Vibe analyzer process failed." : errorOutput.Trim());
            }

            if (TryReadAnalyzerFailure(output, out var analyzerFailure))
            {
                if (IsMlCapabilityFailure(analyzerFailure.ErrorCode))
                {
                    SetMlCapabilityUnavailable(analyzerFailure.Reason);
                    LogMlUnavailable(analyzerFailure.Reason);
                }

                return (null, analyzerFailure.Reason);
            }

            var parsed = JsonSerializer.Deserialize<AnalysisOutput>(output, CaseInsensitiveJsonOptions);
            if (parsed is null)
            {
                return (null, "Vibe analyzer returned an empty payload.");
            }

            return (parsed, null);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Vibe analysis ML failed for {FilePath}", filePath);
            return (null, ex.Message);
        }
    }

    private static TimeSpan ResolveAnalyzerTimeout()
    {
        var timeoutSeconds = DefaultVibeAnalyzerTimeoutSeconds;
        var configuredTimeout = Environment.GetEnvironmentVariable(VibeAnalyzerTimeoutSecondsEnvironmentVariable);
        if (int.TryParse(configuredTimeout, out var parsedTimeoutSeconds))
        {
            timeoutSeconds = parsedTimeoutSeconds;
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, MinVibeAnalyzerTimeoutSeconds, MaxVibeAnalyzerTimeoutSeconds);

        return TimeSpan.FromSeconds(timeoutSeconds);
    }

    private static TimeSpan ResolveAnalyzerBatchTimeout()
    {
        var timeoutSeconds = DefaultVibeAnalyzerBatchTimeoutSeconds;
        var configuredTimeout = Environment.GetEnvironmentVariable(VibeAnalyzerBatchTimeoutSecondsEnvironmentVariable);
        if (int.TryParse(configuredTimeout, out var parsedTimeoutSeconds))
        {
            timeoutSeconds = parsedTimeoutSeconds;
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, MinVibeAnalyzerBatchTimeoutSeconds, MaxVibeAnalyzerBatchTimeoutSeconds);
        return TimeSpan.FromSeconds(timeoutSeconds);
    }

    private static int ResolveAnalyzerWorkers()
    {
        if (IsCpuOnlyMlRuntimeEnabled())
        {
            return 1;
        }

        var cpuCount = Environment.ProcessorCount > 0 ? Environment.ProcessorCount : 4;
        var autoWorkers = Math.Clamp(Math.Max(2, cpuCount / 2), MinVibeAnalyzerWorkers, MaxVibeAnalyzerWorkers);
        var configuredWorkers = Environment.GetEnvironmentVariable(VibeAnalyzerWorkersEnvironmentVariable);
        if (!int.TryParse(configuredWorkers, out var parsedWorkers))
        {
            return autoWorkers;
        }

        return Math.Clamp(parsedWorkers, MinVibeAnalyzerWorkers, MaxVibeAnalyzerWorkers);
    }

    private static bool ResolveUseBatchAnalyzer()
    {
        var configured = Environment.GetEnvironmentVariable(VibeAnalyzerUseBatchEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            // Default to per-track analysis for easier progress monitoring and diagnostics.
            return false;
        }

        return configured.Equals("1", StringComparison.OrdinalIgnoreCase)
            || configured.Equals("true", StringComparison.OrdinalIgnoreCase)
            || configured.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || configured.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<long, BatchPrediction> CreateBatchFailureMap(
        IReadOnlyList<TrackAnalysisInputDto> tracks,
        string failureReason)
    {
        var map = new Dictionary<long, BatchPrediction>();
        foreach (var track in tracks)
        {
            map[track.TrackId] = new BatchPrediction(null, failureReason);
        }

        return map;
    }

    private static string BuildBatchFailureReason(string? errorCode, string? message)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return string.IsNullOrWhiteSpace(message) ? "Vibe analyzer batch failed." : message.Trim();
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return errorCode.Trim();
        }

        return $"{errorCode.Trim()}: {message.Trim()}";
    }

    private MlCapability GetOrProbeMlCapability()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_mlCapabilityLock)
        {
            if (_mlCapability is not null
                && (_mlCapability.Available || now - _mlCapabilityLastCheckedAt < MlCapabilityRetryInterval))
            {
                return _mlCapability;
            }

            _mlCapabilityLastCheckedAt = now;
        }

        EnsureMlRuntimeProvisioned();
        var capability = ProbeMlCapability();
        lock (_mlCapabilityLock)
        {
            _mlCapability = capability;
            _mlCapabilityLastCheckedAt = DateTimeOffset.UtcNow;
        }

        return capability;
    }

    private void EnsureMlRuntimeProvisioned()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_mlCapabilityLock)
        {
            if (now - _mlBootstrapLastAttemptAt < MlBootstrapRetryInterval)
            {
                return;
            }

            _mlBootstrapLastAttemptAt = now;
        }

        EnsureAnalyzerScriptEnvironmentPath();
        EnsureModelsProvisioned();
        EnsureEssentiaPythonProvisioned();
    }

    private static void EnsureAnalyzerScriptEnvironmentPath()
    {
        var scriptPath = ResolveAnalyzerScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            return;
        }

        Environment.SetEnvironmentVariable(VibePathEnvironmentVariable, scriptPath);
    }

    private void EnsureModelsProvisioned()
    {
        var modelsDir = ResolveModelsDirectoryForProvisioning();
        if (string.IsNullOrWhiteSpace(modelsDir))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(modelsDir);
            var downloaded = 0;
            foreach (var (fileName, url) in RequiredModelFiles)
            {
                var destinationPath = Path.Join(modelsDir, fileName);
                if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
                {
                    continue;
                }

                if (TryDownloadModel(url, destinationPath))
                {
                    downloaded++;
                }
            }

            Environment.SetEnvironmentVariable(VibeModelsDirectoryEnvironmentVariable, modelsDir);
            if (downloaded > 0 && _logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Vibe analysis models provisioned in {Directory}. Downloaded {Count} files.", modelsDir, downloaded);
            }
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Failed to provision vibe analysis models at {Directory}", modelsDir);
        }
    }

    private bool TryDownloadModel(string url, string destinationPath)
    {
        var tempPath = destinationPath + ".tmp";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = MlBootstrapHttpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to download model {Url}. Status code {StatusCode}.", url, (int)response.StatusCode);
                return false;
            }

            using var networkStream = response.Content.ReadAsStream();
            using (var fileStream = File.Create(tempPath))
            {
                networkStream.CopyTo(fileStream);
            }

            File.Move(tempPath, destinationPath, true);
            return true;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Model download failed for {Url}", url);
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception cleanupException) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(cleanupException))
            {
                // Best effort cleanup.
            }

            return false;
        }
    }

    private void EnsureEssentiaPythonProvisioned()
    {
        var currentPython = ResolvePythonExecutable();
        if (SupportsEssentia(currentPython))
        {
            return;
        }

        var dataRoot = ResolveDataRootPath();
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return;
        }

        var venvRoot = Path.Join(dataRoot, DefaultVibeVenvRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var venvPython = ResolveVenvPythonPath(venvRoot);
        try
        {
            if (string.IsNullOrWhiteSpace(venvPython) || !File.Exists(venvPython))
            {
                Directory.CreateDirectory(venvRoot);
                if (!TryRunProcess(Python3Executable, $"-m venv \"{venvRoot}\"", PipInstallTimeout, out var venvError))
                {
                    _logger.LogWarning("Unable to create vibe analysis venv at {VenvRoot}: {Error}", venvRoot, venvError);
                    return;
                }

                venvPython = ResolveVenvPythonPath(venvRoot);
            }

            if (string.IsNullOrWhiteSpace(venvPython) || !File.Exists(venvPython))
            {
                _logger.LogWarning("Vibe analysis venv python was not found at {VenvRoot}", venvRoot);
                return;
            }

            if (!SupportsEssentia(venvPython))
            {
                var essentiaPackage = ResolveEssentiaPackage();
                TryRunProcess(venvPython, "-m pip install --upgrade pip", PipInstallTimeout, out _);
                if (!TryRunProcess(venvPython, $"-m pip install {essentiaPackage}", PipInstallTimeout, out var installError))
                {
                    _logger.LogWarning("Essentia install failed for vibe analysis venv (package {Package}): {Error}", essentiaPackage, installError);
                    return;
                }
            }

            if (SupportsEssentia(venvPython))
            {
                Environment.SetEnvironmentVariable(VibePythonEnvironmentVariable, venvPython);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Vibe analysis python provisioned at {PythonPath}", venvPython);
                }
            }
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Failed to provision Essentia python runtime.");
        }
    }

    private static bool SupportsEssentia(string pythonExecutable)
    {
        return TryRunProcess(pythonExecutable, "-c \"import essentia.standard\"", TimeSpan.FromSeconds(20), out _);
    }

    private static string ResolveEssentiaPackage()
    {
        var configured = Environment.GetEnvironmentVariable(VibeEssentiaPackageEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured) ? DefaultEssentiaPackage : configured.Trim();
    }

    private static bool TryRunProcess(string fileName, string arguments, TimeSpan timeout, out string error)
    {
        error = string.Empty;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error = $"Failed to start process {fileName}.";
                return false;
            }

            if (!process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 1000, int.MaxValue)))
            {
                TryTerminate(process);
                error = $"{fileName} timed out.";
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (process.ExitCode == 0)
            {
                return true;
            }

            error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return false;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? ResolveModelsDirectoryForProvisioning()
    {
        var overridePath = Environment.GetEnvironmentVariable(VibeModelsDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return ResolveAbsolutePath(overridePath);
        }

        var dataRoot = ResolveDataRootPath();
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            return Path.Join(dataRoot, DefaultVibeModelsRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        return Path.Join(AppContext.BaseDirectory, ToolsDirectoryName, ModelsDirectoryName);
    }

    private static string ResolveAbsolutePath(string path)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Join(Directory.GetCurrentDirectory(), path));
    }

    private static string? ResolveDataRootPath()
    {
        var configuredDataDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configuredDataDir))
        {
            return ResolveAbsolutePath(configuredDataDir.Trim());
        }

        var configuredConfigDir = Environment.GetEnvironmentVariable("DEEZSPOTAG_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configuredConfigDir))
        {
            return ResolveAbsolutePath(configuredConfigDir.Trim());
        }

        return null;
    }

    private static string? ResolveVenvPythonPath(string venvRoot)
    {
        var linuxPath = Path.Join(venvRoot, "bin", Python3Executable);
        if (File.Exists(linuxPath))
        {
            return linuxPath;
        }

        var windowsPath = Path.Join(venvRoot, "Scripts", "python.exe");
        return File.Exists(windowsPath) ? windowsPath : null;
    }

    private static MlCapability ProbeMlCapability()
    {
        var scriptPath = ResolveAnalyzerScriptPath();
        var modelsDir = ResolveModelsDirectory();
        var prereqFailure = ValidateProbeInputs(scriptPath, modelsDir);
        if (prereqFailure is not null)
        {
            return prereqFailure;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolvePythonExecutable(),
                Arguments = $"\"{scriptPath}\" --probe --models \"{modelsDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            ConfigurePythonEnvironment(startInfo);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new MlCapability(false, $"Failed to start {Python3Executable} for Essentia probe.");
            }

            if (!process.WaitForExit(15000))
            {
                TryTerminate(process);
                return new MlCapability(false, "Essentia probe timed out.");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            return ParseProbeResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return new MlCapability(false, $"Essentia probe failed: {ex.Message}");
        }
    }

    private static MlCapability? ValidateProbeInputs(string? scriptPath, string? modelsDir)
    {
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            return new MlCapability(false, $"Analyzer script missing at {scriptPath}.");
        }

        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
        {
            return new MlCapability(false, $"Models directory missing at {modelsDir}.");
        }

        return null;
    }

    private static MlCapability ParseProbeResult(int exitCode, string stdout, string stderr)
    {
        if (exitCode != 0)
        {
            return new MlCapability(false, string.IsNullOrWhiteSpace(stderr) ? "Essentia probe failed." : stderr.Trim());
        }

        var probe = JsonSerializer.Deserialize<ProbeOutput>(stdout, CaseInsensitiveJsonOptions);
        if (probe is null)
        {
            return new MlCapability(false, "Essentia probe returned no output.");
        }

        if (!probe.Ok)
        {
            var details = string.IsNullOrWhiteSpace(probe.Message) ? probe.ErrorCode : $"{probe.ErrorCode}: {probe.Message}";
            return new MlCapability(false, string.IsNullOrWhiteSpace(details) ? "Essentia unavailable." : details);
        }

        if (probe.EnhancedMode != true)
        {
            var details = BuildEnhancedProbeFailure(probe);
            return new MlCapability(false, details);
        }

        return new MlCapability(true, null);
    }

    private static string BuildEnhancedProbeFailure(ProbeOutput probe)
    {
        var details = new List<string>();
        if (probe.MissingEnhancedModels is { Count: > 0 })
        {
            details.Add($"missing models: {string.Join(", ", probe.MissingEnhancedModels)}");
        }

        if (probe.LoadedPredictionHeads is not null)
        {
            details.Add($"loaded heads: {string.Join(", ", probe.LoadedPredictionHeads)}");
        }

        var suffix = details.Count == 0 ? string.Empty : $" ({string.Join("; ", details)})";
        return $"Enhanced vibe analysis is unavailable; analyzer would fall back to standard mode{suffix}.";
    }

    private static string ResolvePythonExecutable()
    {
        var overrideExecutable = ResolvePythonOverrideExecutable();
        if (!string.IsNullOrWhiteSpace(overrideExecutable))
        {
            return overrideExecutable;
        }

        var venvExecutable = ResolveVenvPythonExecutable();
        return string.IsNullOrWhiteSpace(venvExecutable) ? Python3Executable : venvExecutable;
    }

    private static string? ResolvePythonOverrideExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable(VibePythonEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(overridePath))
        {
            return null;
        }

        var normalized = overridePath.Trim();
        var looksLikeCommand = !Path.IsPathRooted(normalized)
            && normalized.IndexOf(Path.DirectorySeparatorChar) < 0
            && normalized.IndexOf(Path.AltDirectorySeparatorChar) < 0;
        if (looksLikeCommand)
        {
            return normalized;
        }

        var existingFile = TryResolveExistingFilePath(normalized);
        return string.IsNullOrWhiteSpace(existingFile) ? null : existingFile;
    }

    private static string? ResolveVenvPythonExecutable()
    {
        var dataRoot = ResolveDataRootPath();
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            var managedVenv = ResolveVenvPythonPath(Path.Join(dataRoot, DefaultVibeVenvRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!string.IsNullOrWhiteSpace(managedVenv))
            {
                return managedVenv;
            }
        }

        var scriptPath = ResolveAnalyzerScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            return null;
        }

        var scriptDir = Path.GetDirectoryName(scriptPath);
        if (string.IsNullOrWhiteSpace(scriptDir))
        {
            return null;
        }

        var venvDir = Path.Join(scriptDir, "venv");
        var linuxPython = new[]
        {
            Path.Join(venvDir, "bin", Python3Executable),
            Path.Join(venvDir, "bin", "python")
        }.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(linuxPython))
        {
            return linuxPython;
        }

        return new[]
        {
            Path.Join(venvDir, "Scripts", "python.exe"),
            Path.Join(venvDir, "Scripts", "python")
        }.FirstOrDefault(File.Exists);
    }

    private static void ConfigurePythonEnvironment(ProcessStartInfo startInfo)
    {
        if (!string.IsNullOrWhiteSpace(FfmpegExecutablePath))
        {
            startInfo.Environment["DEEZSPOTAG_FFMPEG_PATH"] = FfmpegExecutablePath;
        }

        // Force CPU TensorFlow/Essentia execution by default for stability on
        // hosts without CUDA runtime libraries (common on NAS/CPU-only Linux).
        if (IsCpuOnlyMlRuntimeEnabled())
        {
            startInfo.Environment["CUDA_VISIBLE_DEVICES"] = "-1";
            startInfo.Environment["TF_CPP_MIN_LOG_LEVEL"] = "2";
            startInfo.Environment["TF_USE_CUDA"] = "0";
        }

        var sitePackagesPath = ResolveVenvSitePackagesPath();
        if (string.IsNullOrWhiteSpace(sitePackagesPath))
        {
            return;
        }

        if (startInfo.Environment.TryGetValue("PYTHONPATH", out var current) && !string.IsNullOrWhiteSpace(current))
        {
            var segments = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Any(path => string.Equals(path, sitePackagesPath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            startInfo.Environment["PYTHONPATH"] = $"{sitePackagesPath}{Path.PathSeparator}{current}";
            return;
        }

        startInfo.Environment["PYTHONPATH"] = sitePackagesPath;
    }

    private static bool IsCpuOnlyMlRuntimeEnabled()
    {
        var configured = Environment.GetEnvironmentVariable(VibeAnalyzerForceCpuEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return true;
        }

        return configured.Equals("1", StringComparison.OrdinalIgnoreCase)
            || configured.Equals("true", StringComparison.OrdinalIgnoreCase)
            || configured.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || configured.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveVenvSitePackagesPath()
    {
        var scriptPath = ResolveAnalyzerScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            return null;
        }

        var scriptDir = Path.GetDirectoryName(scriptPath);
        if (string.IsNullOrWhiteSpace(scriptDir))
        {
            return null;
        }

        var venvDir = Path.Join(scriptDir, "venv");

        var windowsSitePackages = Path.Join(venvDir, "Lib", "site-packages");
        if (Directory.Exists(windowsSitePackages))
        {
            return windowsSitePackages;
        }

        var linuxLibRoot = Path.Join(venvDir, "lib");
        if (!Directory.Exists(linuxLibRoot))
        {
            return null;
        }

        foreach (var sitePackages in Directory.GetDirectories(linuxLibRoot, "python*")
            .Select(pythonDir => Path.Join(pythonDir, "site-packages"))
            .Where(Directory.Exists))
        {
            return sitePackages;
        }

        return null;
    }

    private void LogMlUnavailable(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_mlCapabilityLock)
        {
            if (now - _mlLastWarningLoggedAt < MlWarningThrottle)
            {
                return;
            }
            _mlLastWarningLoggedAt = now;
        }

        _logger.LogWarning("Vibe analysis ML unavailable: {Reason}", reason);
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            now,
            "warning",
            $"Vibe analysis ML unavailable: {reason}"));
    }

    private void SetMlCapabilityUnavailable(string reason)
    {
        lock (_mlCapabilityLock)
        {
            _mlCapability = new MlCapability(false, reason);
            _mlCapabilityLastCheckedAt = DateTimeOffset.UtcNow;
        }
    }

    private static bool TryReadAnalyzerFailure(string output, out AnalyzerFailure failure)
    {
        failure = default;
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("ok", out var okProp) || okProp.ValueKind != JsonValueKind.False)
            {
                return false;
            }

            var errorCode = document.RootElement.TryGetProperty("errorCode", out var errorCodeProp) ? errorCodeProp.GetString() : null;
            var message = document.RootElement.TryGetProperty("message", out var messageProp) ? messageProp.GetString() : null;
            string reason;
            if (string.IsNullOrWhiteSpace(message))
            {
                reason = errorCode ?? "Vibe analyzer reported failure.";
            }
            else if (string.IsNullOrWhiteSpace(errorCode))
            {
                reason = message;
            }
            else
            {
                reason = $"{errorCode}: {message}";
            }

            failure = new AnalyzerFailure(errorCode, reason);
            return true;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static bool IsMlCapabilityFailure(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return false;
        }

        return errorCode.Trim().ToUpperInvariant() is
            "ESSENTIA_MISSING_REQUIRED" or
            "VIBE_MODELS_MISSING" or
            "VIBE_ENHANCED_UNAVAILABLE" or
            "VIBE_ANALYZER_NOT_INITIALIZED";
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // Best effort: the process may have already exited or cannot be signaled.
        }
    }

    private static string? ResolveAnalyzerScriptPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(VibePathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var resolvedOverride = TryResolveExistingFilePath(overridePath.Trim());
            if (!string.IsNullOrWhiteSpace(resolvedOverride))
            {
                return resolvedOverride;
            }
        }

        var candidates = new[]
        {
            Path.Join(AppContext.BaseDirectory, ToolsDirectoryName, VibeAnalyzerScriptFileName),
            Path.Join(AppContext.BaseDirectory, "..", ToolsDirectoryName, VibeAnalyzerScriptFileName),
            Path.Join(AppContext.BaseDirectory, "..", "..", "..", ToolsDirectoryName, VibeAnalyzerScriptFileName),
            Path.Join(Directory.GetCurrentDirectory(), ToolsDirectoryName, VibeAnalyzerScriptFileName),
            Path.Join(Directory.GetCurrentDirectory(), "DeezSpoTag.Web", ToolsDirectoryName, VibeAnalyzerScriptFileName)
        };

        foreach (var resolvedCandidate in candidates
            .Select(TryResolveExistingFilePath)
            .Where(resolvedCandidate => !string.IsNullOrWhiteSpace(resolvedCandidate)))
        {
            return resolvedCandidate;
        }

        return null;
    }

    private static string? ResolveModelsDirectory()
    {
        var resolvedCandidates = new List<string>();

        static void AddUniqueCandidate(List<string> candidates, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(path);
            }
        }

        var overridePath = Environment.GetEnvironmentVariable(VibeModelsDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var resolvedOverride = TryResolveExistingDirectoryPath(overridePath.Trim());
            AddUniqueCandidate(resolvedCandidates, resolvedOverride);
        }

        var dataRoot = ResolveDataRootPath();
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            var dataModels = Path.Join(dataRoot, DefaultVibeModelsRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var resolvedDataModels = TryResolveExistingDirectoryPath(dataModels);
            AddUniqueCandidate(resolvedCandidates, resolvedDataModels);
        }

        var candidates = new[]
        {
            Path.Join(AppContext.BaseDirectory, ModelsDirectoryName),
            Path.Join(AppContext.BaseDirectory, ToolsDirectoryName, ModelsDirectoryName),
            Path.Join(AppContext.BaseDirectory, "..", ToolsDirectoryName, ModelsDirectoryName),
            Path.Join(AppContext.BaseDirectory, "..", "..", "..", ToolsDirectoryName, ModelsDirectoryName),
            Path.Join(Directory.GetCurrentDirectory(), ToolsDirectoryName, ModelsDirectoryName),
            Path.Join(Directory.GetCurrentDirectory(), "DeezSpoTag.Web", ToolsDirectoryName, ModelsDirectoryName),
            Path.Join(Path.DirectorySeparatorChar.ToString(), "app", ModelsDirectoryName)
        };

        foreach (var candidate in candidates)
        {
            var resolvedCandidate = TryResolveExistingDirectoryPath(candidate);
            AddUniqueCandidate(resolvedCandidates, resolvedCandidate);
        }

        var preferredCandidate = resolvedCandidates.FirstOrDefault(HasRequiredEnhancedModels);
        if (!string.IsNullOrWhiteSpace(preferredCandidate))
        {
            return preferredCandidate;
        }

        return resolvedCandidates.Count > 0 ? resolvedCandidates[0] : null;
    }

    private static bool HasRequiredEnhancedModels(string modelsDirectory)
    {
        if (string.IsNullOrWhiteSpace(modelsDirectory) || !Directory.Exists(modelsDirectory))
        {
            return false;
        }

        return RequiredEnhancedModelFiles.All(requiredModelFile => File.Exists(Path.Join(modelsDirectory, requiredModelFile)));
    }

    private static string? TryResolveExistingFilePath(string path)
    {
        return ExpandPathCandidates(path).FirstOrDefault(File.Exists);
    }

    private static string? TryResolveExistingDirectoryPath(string path)
    {
        return ExpandPathCandidates(path).FirstOrDefault(Directory.Exists);
    }

    private static IEnumerable<string> ExpandPathCandidates(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        if (Path.IsPathRooted(path))
        {
            yield return Path.GetFullPath(path);
            yield break;
        }

        yield return Path.GetFullPath(Path.Join(Directory.GetCurrentDirectory(), path));
        yield return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, path));
    }

    private sealed record MoodScores(
        double Happy,
        double Sad,
        double Relaxed,
        double Aggressive,
        double Party,
        double Acoustic,
        double Electronic);

    private sealed record AnalysisOutput(
        string? AnalysisMode,
        double? Bpm,
        int? BeatsCount,
        string? Key,
        string? KeyScale,
        double? KeyStrength,
        double? Danceability,
        double? Acousticness,
        double? Instrumentalness,
        double? Speechiness,
        IReadOnlyList<string>? Genres,
        IReadOnlyList<string>? MoodTags,
        double? Happy,
        double? Sad,
        double? Relaxed,
        double? Aggressive,
        double? Party,
        double? Acoustic,
        double? Electronic,
        // Vibe analysis - new Essentia model fields
        double? Approachability,
        double? Engagement,
        double? VoiceInstrumental,
        double? TonalAtonal,
        double? ValenceMl,
        double? ArousalMl,
        double? DanceabilityMl,
        double? Loudness,
        double? DynamicComplexity)
    {
        public MoodScores? MoodScores => Happy.HasValue
            ? new MoodScores(
                Happy.Value,
                Sad ?? 0,
                Relaxed ?? 0,
                Aggressive ?? 0,
                Party ?? 0,
                Acoustic ?? 0,
                Electronic ?? 0)
            : null;
    }

    private sealed record BatchAnalysisRequestItem(
        long TrackId,
        string FilePath);

    private sealed record BatchAnalysisItem(
        long? TrackId,
        string? FilePath,
        bool Ok,
        AnalysisOutput? Payload,
        string? ErrorCode,
        string? Message);

    private sealed record BatchAnalysisResponse(
        bool Ok,
        bool Retryable,
        string? ErrorCode,
        string? Message,
        IReadOnlyList<BatchAnalysisItem>? Results);

    private sealed record BatchPrediction(
        AnalysisOutput? Output,
        string? FailureReason);

    private sealed record BatchAnalyzerContext(
        string ScriptPath,
        string ModelsDir,
        int Workers,
        int PerTrackTimeoutSeconds,
        TimeSpan BatchTimeout,
        int BatchTimeoutSeconds);

    private sealed record BatchProcessExecution(
        bool Succeeded,
        bool TimedOut,
        string Output,
        string ErrorOutput,
        string FailureReason);

    private readonly record struct AnalyzerFailure(string? ErrorCode, string Reason);

    private sealed record MlCapability(bool Available, string? Reason);

    private sealed record ProbeOutput(
        bool Ok,
        string? ErrorCode,
        string? Message,
        IReadOnlyList<string>? MissingRequired,
        IReadOnlyList<string>? MissingOptional,
        bool? EnhancedMode,
        IReadOnlyList<string>? MissingEnhancedModels,
        IReadOnlyList<string>? LoadedPredictionHeads);

    private static bool TryReadWithFfmpeg(string path, int seconds, out float[] samples, out int sampleRate, out string? errorMessage)
    {
        samples = Array.Empty<float>();
        sampleRate = 44100;
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(FfmpegExecutablePath))
        {
            errorMessage = "ffmpeg executable not configured.";
            return false;
        }

        const int channels = 2;
        var targetBytes = sampleRate * seconds * channels * sizeof(short);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = FfmpegExecutablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-nostdin");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("s16le");
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add(channels.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add(sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                errorMessage = "Failed to start ffmpeg.";
                return false;
            }

            var buffer = new byte[targetBytes];
            var totalRead = 0;
            while (totalRead < targetBytes)
            {
                var read = process.StandardOutput.BaseStream.Read(buffer, totalRead, targetBytes - totalRead);
                if (read <= 0)
                {
                    break;
                }
                totalRead += read;
            }

            process.WaitForExit(5000);

            if (totalRead <= 0)
            {
                var stderr = process.StandardError.ReadToEnd();
                errorMessage = string.IsNullOrWhiteSpace(stderr) ? "No audio data decoded." : stderr.Trim();
                return false;
            }

            var sampleCount = totalRead / sizeof(short);
            samples = new float[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                var value = BitConverter.ToInt16(buffer, i * sizeof(short));
                samples[i] = value / 32768f;
            }
            return true;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static float[] ReadSamples(ISampleProvider provider, int seconds)
    {
        var sampleRate = provider.WaveFormat.SampleRate;
        var channels = provider.WaveFormat.Channels;
        var sampleCount = sampleRate * seconds * channels;
        var buffer = new float[sampleCount];
        var read = provider.Read(buffer, 0, buffer.Length);
        if (read <= 0)
        {
            return Array.Empty<float>();
        }
        if (read == buffer.Length)
        {
            return buffer;
        }
        var trimmed = new float[read];
        Array.Copy(buffer, trimmed, read);
        return trimmed;
    }

    private static bool TryReadMp3Samples(string path, int seconds, out float[] samples, out int sampleRate, out string? errorMessage)
    {
        samples = Array.Empty<float>();
        sampleRate = 0;
        errorMessage = null;
        try
        {
            using var mpeg = new MpegFile(path);
            sampleRate = mpeg.SampleRate;
            var channels = mpeg.Channels;
            if (sampleRate <= 0 || channels <= 0)
            {
                errorMessage = "Invalid mp3 format metadata.";
                return false;
            }
            var sampleCount = sampleRate * seconds * channels;
            var buffer = new float[sampleCount];
            var read = mpeg.ReadSamples(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                errorMessage = "No mp3 samples decoded.";
                return false;
            }
            if (read == buffer.Length)
            {
                samples = buffer;
                return true;
            }
            var trimmed = new float[read];
            Array.Copy(buffer, trimmed, read);
            samples = trimmed;
            return true;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static WaveStream? OpenAudioStream(string path, out string? errorMessage)
    {
        errorMessage = null;
        var extension = Path.GetExtension(path)?.ToLowerInvariant();
        try
        {
            WaveStream? reader = null;
            switch (extension)
            {
                case ".wav":
                    reader = new WaveFileReader(path);
                    break;
                case ".aiff":
                case ".aif":
                    reader = new AiffFileReader(path);
                    break;
                case ".ogg":
                case ".oga":
                    reader = new VorbisWaveReader(path);
                    break;
                case ".m4a":
                case ".m4b":
                case ".aac":
                    if (OperatingSystem.IsWindows())
                    {
                        reader = new MediaFoundationReader(path);
                    }
                    break;
            }

            if (reader is null)
            {
                errorMessage = string.IsNullOrWhiteSpace(extension)
                    ? "Unsupported format"
                    : $"Unsupported format {extension}";
            }

            return reader;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            errorMessage = ex.Message;
            return null;
        }
    }

    private static double CalculateZeroCrossingRate(float[] samples)
    {
        if (samples.Length < 2)
        {
            return 0;
        }

        var crossings = 0;
        for (var i = 1; i < samples.Length; i++)
        {
            if ((samples[i - 1] >= 0 && samples[i] < 0) || (samples[i - 1] < 0 && samples[i] >= 0))
            {
                crossings++;
            }
        }

        return crossings / (double)samples.Length;
    }

    private static int? CalculateBeatsCount(double bpm, int? durationMs)
    {
        if (bpm <= 0 || durationMs is null)
        {
            return null;
        }

        var minutes = durationMs.Value / 60000.0;
        if (minutes <= 0)
        {
            return null;
        }

        return (int)Math.Round(bpm * minutes);
    }

    private static (double FrequencyHz, double Strength) CalculateDominantFrequency(float[] samples, int sampleRate)
    {
        if (samples.Length == 0 || sampleRate <= 0)
        {
            return (0, 0);
        }

        var n = Math.Min(samples.Length, 8192);
        n = HighestPowerOfTwo(n);
        if (n == 0)
        {
            return (0, 0);
        }

        var fft = new System.Numerics.Complex[n];
        for (var i = 0; i < n; i++)
        {
            fft[i] = new System.Numerics.Complex(samples[i], 0);
        }

        FFT(fft);

        var half = n / 2;
        var maxMagnitude = 0.0;
        var maxIndex = 0;
        var sumMagnitude = 0.0;
        for (var i = 1; i < half; i++)
        {
            var magnitude = fft[i].Magnitude;
            sumMagnitude += magnitude;
            if (magnitude > maxMagnitude)
            {
                maxMagnitude = magnitude;
                maxIndex = i;
            }
        }

        if (maxIndex == 0 || sumMagnitude <= 0)
        {
            return (0, 0);
        }

        var frequency = (double)maxIndex * sampleRate / n;
        var strength = maxMagnitude / sumMagnitude;
        return (frequency, strength);
    }

    private static (string? Key, double? Strength) MapFrequencyToKey(double frequencyHz, double strength)
    {
        if (frequencyHz <= 0 || strength <= 0)
        {
            return (null, null);
        }

        if (frequencyHz < 55 || frequencyHz > 2000 || strength < 0.08)
        {
            return (null, null);
        }

        var a4 = 440.0;
        var semitone = 12 * Math.Log2(frequencyHz / a4);
        var midi = (int)Math.Round(semitone + 69);
        var note = ((midi % 12) + 12) % 12;
        var noteNames = new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        var key = noteNames[note];
        return (key, Math.Clamp(strength * 1.5, 0, 1));
    }

    private static double? CalculateDynamicRange(float[] samples)
    {
        if (samples.Length == 0)
        {
            return null;
        }

        var max = 0.0;
        var min = double.MaxValue;
        foreach (var value in samples.Select(Math.Abs))
        {
            if (value > max)
            {
                max = value;
            }
            if (value > 0 && value < min)
            {
                min = value;
            }
        }

        if (max <= 0 || Math.Abs(min - double.MaxValue) <= double.Epsilon)
        {
            return null;
        }

        return 20 * Math.Log10(max / min);
    }

    private static double CalculateValence(MoodScores scores)
    {
        return (scores.Happy * 0.5)
            + (scores.Party * 0.3)
            + ((1 - scores.Sad) * 0.2);
    }

    private static double CalculateArousal(MoodScores scores)
    {
        return (scores.Aggressive * 0.35)
            + (scores.Party * 0.25)
            + (scores.Electronic * 0.2)
            + ((1 - scores.Relaxed) * 0.1)
            + ((1 - scores.Acoustic) * 0.1);
    }

    private static double CalculateSpectralCentroid(float[] samples, int sampleRate)
    {
        var n = Math.Min(samples.Length, 8192);
        n = HighestPowerOfTwo(n);
        if (n == 0)
        {
            return 0;
        }

        var fft = new System.Numerics.Complex[n];
        for (var i = 0; i < n; i++)
        {
            fft[i] = new System.Numerics.Complex(samples[i], 0);
        }

        FFT(fft);

        double weightedSum = 0;
        double magnitudeSum = 0;
        var half = n / 2;
        for (var i = 0; i < half; i++)
        {
            var magnitude = fft[i].Magnitude;
            var frequency = (double)i * sampleRate / n;
            weightedSum += frequency * magnitude;
            magnitudeSum += magnitude;
        }

        return magnitudeSum <= double.Epsilon ? 0 : weightedSum / magnitudeSum;
    }

    private static double? CalculateDanceability(double energy, double bpm)
    {
        if (energy <= 0 && bpm <= 0)
        {
            return null;
        }

        var bpmScore = 0.0;
        if (bpm > 0)
        {
            var delta = (bpm - 120.0) / 40.0;
            bpmScore = Math.Exp(-delta * delta);
        }

        var energyScore = energy > 0
            ? 1 - Math.Clamp(Math.Abs(energy - 0.6) / 0.6, 0, 1)
            : 0;

        return Math.Clamp((energyScore * 0.55) + (bpmScore * 0.45), 0, 1);
    }

    private static double? CalculateAcousticness(double brightness, double zeroCrossing)
    {
        if (brightness <= 0 && zeroCrossing <= 0)
        {
            return null;
        }

        var brightnessScore = 1 - Math.Clamp(brightness * 1.2, 0, 1);
        var crossingScore = 1 - Math.Clamp(zeroCrossing * 6, 0, 1);
        var score = (brightnessScore * 0.6) + (crossingScore * 0.4);
        return Math.Clamp(score, 0, 1);
    }

    private static double? CalculateSpeechiness(double zeroCrossing, double brightness)
    {
        if (zeroCrossing <= 0 && brightness <= 0)
        {
            return null;
        }

        if (zeroCrossing < 0.02)
        {
            return 0;
        }

        var crossingScore = Math.Clamp(zeroCrossing * 6, 0, 1);
        var brightnessPenalty = Math.Clamp(brightness * 0.5, 0, 1);
        var score = crossingScore * (1 - brightnessPenalty);
        return Math.Clamp(score, 0, 1);
    }

    private static double EstimateBpm(float[] samples, int sampleRate)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        var frameSize = 1024;
        var hop = 512;
        var energies = CalculateFrameEnergies(samples, frameSize, hop);
        if (energies.Count < 4)
        {
            return 0;
        }

        NormalizeEnergies(energies);
        var (minLag, maxLag) = ComputeLagBounds(sampleRate, hop, energies.Count);
        var bestLag = FindBestLag(energies, minLag, maxLag);
        if (bestLag <= 0)
        {
            return 0;
        }

        var secondsPerBeat = bestLag * hop / (double)sampleRate;
        if (secondsPerBeat <= 0)
        {
            return 0;
        }

        var bpm = 60.0 / secondsPerBeat;
        while (bpm > 200)
        {
            bpm /= 2;
        }
        while (bpm > 0 && bpm < 60)
        {
            bpm *= 2;
        }
        return bpm;
    }

    private static List<double> CalculateFrameEnergies(float[] samples, int frameSize, int hop)
    {
        var energies = new List<double>();
        for (var i = 0; i + frameSize <= samples.Length; i += hop)
        {
            double sum = 0;
            for (var j = 0; j < frameSize; j++)
            {
                var value = samples[i + j];
                sum += value * value;
            }

            energies.Add(sum / frameSize);
        }

        return energies;
    }

    private static void NormalizeEnergies(List<double> energies)
    {
        var mean = energies.Average();
        for (var i = 0; i < energies.Count; i++)
        {
            energies[i] = Math.Max(0, energies[i] - mean);
        }
    }

    private static (int MinLag, int MaxLag) ComputeLagBounds(int sampleRate, int hop, int energyCount)
    {
        var minLag = (int)Math.Round((60.0 / 200.0) * (sampleRate / (double)hop));
        var maxLag = (int)Math.Round(sampleRate / (double)hop);
        minLag = Math.Max(1, minLag);
        maxLag = Math.Min(maxLag, energyCount - 1);
        return (minLag, maxLag);
    }

    private static double FindBestLag(List<double> energies, int minLag, int maxLag)
    {
        double bestLag = 0;
        double bestScore = 0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double score = 0;
            for (var i = 0; i < energies.Count - lag; i++)
            {
                score += energies[i] * energies[i + lag];
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        return bestLag;
    }

    private static int HighestPowerOfTwo(int value)
    {
        var power = 1;
        while (power * 2 <= value)
        {
            power *= 2;
        }
        return power;
    }

    private static void FFT(System.Numerics.Complex[] buffer)
    {
        var n = buffer.Length;
        if (n <= 1)
        {
            return;
        }

        var even = new System.Numerics.Complex[n / 2];
        var odd = new System.Numerics.Complex[n / 2];
        for (var i = 0; i < n / 2; i++)
        {
            even[i] = buffer[i * 2];
            odd[i] = buffer[i * 2 + 1];
        }

        FFT(even);
        FFT(odd);

        for (var k = 0; k < n / 2; k++)
        {
            var exp = -2 * Math.PI * k / n;
            var wk = new System.Numerics.Complex(Math.Cos(exp), Math.Sin(exp));
            buffer[k] = even[k] + wk * odd[k];
            buffer[k + n / 2] = even[k] - wk * odd[k];
        }
    }

    private sealed class RuntimeRunScope : IDisposable
    {
        private readonly TrackAnalysisBackgroundService _owner;
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        public RuntimeRunScope(TrackAnalysisBackgroundService owner, CancellationTokenSource source)
        {
            _owner = owner;
            _source = source;
        }

        public CancellationToken Token => _source.Token;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _owner.CompleteRuntimeRun(_source);
            _source.Dispose();
            _disposed = true;
        }
    }
}

public static class VibeAnalysisRuntimeStates
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Pausing = "pausing";
    public const string Paused = "paused";
}

public sealed record VibeAnalysisRecentItemDto(
    long TrackId,
    string Title,
    string Status,
    DateTimeOffset AtUtc);

public sealed record VibeAnalysisRuntimeDto(
    string State,
    bool Running,
    LatestTrackAnalysisDto? Current,
    LatestTrackAnalysisDto? Latest,
    IReadOnlyList<VibeAnalysisRecentItemDto> Recent,
    DateTimeOffset? UpdatedAtUtc);
