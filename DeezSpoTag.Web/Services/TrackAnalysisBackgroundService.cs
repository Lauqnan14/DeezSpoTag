using DeezSpoTag.Services.Library;
using DeezSpoTag.Web.Services.AutoTag;
using DeezSpoTag.Web.Services.Audiomack;
using DeezSpoTag.Web.Services.Vibe;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed class TrackAnalysisBackgroundService : BackgroundService
{
    private readonly record struct AnalysisBatchOutcome(int Processed, int Completed);

    private const string StandardAnalysisMode = "standard";
    private const string StandardAnalysisVersion = "ffmpeg-basic-2";
    private const string EnhancedAnalysisMode = "enhanced";
    private const string EnhancedAnalysisVersion = "musicnn-1";
    private const string FailedAnalysisStatus = "failed";
    private const string CompletedAnalysisStatus = "completed";
    private const string UnknownTrackMetadata = "Unknown";
    private const string VibeModelsDirectoryEnvironmentVariable = "VIBE_ANALYZER_MODELS";
    private const string VibePythonEnvironmentVariable = "VIBE_ANALYZER_PYTHON";
    private const string VibePathEnvironmentVariable = "VIBE_ANALYZER_PATH";
    private const string VibeAnalyzerTimeoutSecondsEnvironmentVariable = "VIBE_ANALYZER_TIMEOUT_SECONDS";
    private const string VibeAnalyzerBatchTimeoutSecondsEnvironmentVariable = "VIBE_ANALYZER_BATCH_TIMEOUT_SECONDS";
    private const string VibeAnalyzerWorkersEnvironmentVariable = "VIBE_ANALYZER_WORKERS";
    private const string VibeAnalyzerUseBatchEnvironmentVariable = "VIBE_ANALYZER_USE_BATCH";
    private const string VibeEssentiaPackageEnvironmentVariable = "VIBE_ANALYZER_ESSENTIA_TF_PACKAGE";
    private const string VibeAnalyzerForceCpuEnvironmentVariable = "VIBE_ANALYZER_FORCE_CPU";
    private const string SonicAnalysisEnabledEnvironmentVariable = "VIBE_SONIC_ENABLED";
    private const string SonicAnalysisConfigurationPath = "SonicAnalysis:Enabled";

    // Sonic embedding identity. Bump any of these and every stored vector for
    // the affected identity becomes stale by construction, because the primary
    // key includes them.
    internal const string SonicModelId = "discogs-effnet-bs64-1";
    internal const string SonicModelVersion = "1";
    internal const string SonicEmbeddingVersion = "embedding-v1";
    private const string VibeAnalyzerProbeTimeoutSecondsEnvironmentVariable = "VIBE_ANALYZER_PROBE_TIMEOUT_SECONDS";
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
    private const int DefaultVibeAnalyzerProbeTimeoutSeconds = 180;
    private const int MinVibeAnalyzerProbeTimeoutSeconds = 30;
    private const int MaxVibeAnalyzerProbeTimeoutSeconds = 900;
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
    // Importing essentia.standard pulls in the TensorFlow shared libraries, which
    // is genuinely slow on a cold container. Judging the image-provided runtime
    // broken on a short budget used to trigger a redundant pip install that then
    // overrode VIBE_ANALYZER_PYTHON.
    private static readonly TimeSpan EssentiaImportTimeout = TimeSpan.FromSeconds(120);
    private const int MaxProcessOutputCharacters = 64 * 1024;

    // Kept in lockstep with scripts/fetch-vibe-models.sh. PublishingWorkflowGuardrailTest
    // asserts the two manifests declare the same file set so they cannot drift.
    private static readonly (string FileName, string Url, string Sha256)[] RequiredModelFiles =
    {
        ("msd-musicnn-1.pb", "https://essentia.upf.edu/models/feature-extractors/musicnn/msd-musicnn-1.pb", "cdea0722bcee7f731286843f2233e3aa69887bb5c3e2dce011eff55f38d04f3e"),
        ("mood_happy-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/mood_happy/mood_happy-msd-musicnn-1.pb", "d7382bc60304ea4578c298222968cd8d600c31252c7bf3e90b1f728ebb3ec36d"),
        ("mood_sad-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/mood_sad/mood_sad-msd-musicnn-1.pb", "a5e908cf7f59e8c379ff7c7d138dd85416985fddaebb5de14ca4193200411f61"),
        ("mood_relaxed-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/mood_relaxed/mood_relaxed-msd-musicnn-1.pb", "1252d28ca7d2204e34e0cdf84a00aa2bc9627a87bdcf923df3aad39cfa69d2d9"),
        ("mood_aggressive-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/mood_aggressive/mood_aggressive-msd-musicnn-1.pb", "3b6eb5645e4b47a2ceb28ef3f8612f224640c583048770791b9fc6e8e5627a67"),
        ("mood_party-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/mood_party/mood_party-msd-musicnn-1.pb", "765b096300ee1d92103cb0a122fc12c33882166fb94d37875284e82ce06322a1"),
        ("mood_acoustic-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/mood_acoustic/mood_acoustic-msd-musicnn-1.pb", "519ee3af8210fe32e021002a0094546aeb6fb5a59d22b7d53c48e4ee1ac9e6cc"),
        ("mood_electronic-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/mood_electronic/mood_electronic-msd-musicnn-1.pb", "86c109b504fc6cf666c7513d684381a594218a552c3c954f212dd3a9d0c6cdc5"),
        ("voice_instrumental-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/voice_instrumental/voice_instrumental-msd-musicnn-1.pb", "eb762cc7ee6751b2ea32179d3716e2d60a1d1a9e615b7e3b8be8a6f79d71675e"),
        ("tonal_atonal-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/tonal_atonal/tonal_atonal-msd-musicnn-1.pb", "45a36e68a70a6692a60434ee3ae81df9bd5c402204fb04c3355c39f9e3d24aaf"),
        ("danceability-msd-musicnn-1.pb", "https://essentia.upf.edu/models/classification-heads/danceability/danceability-msd-musicnn-1.pb", "874a4b86afc9e12de3f15a47baf9ff1ac676ace109c56203e26103f2259eb95e"),
        ("deam-msd-musicnn-2.pb", "https://essentia.upf.edu/models/classification-heads/deam/deam-msd-musicnn-2.pb", "beb5eeb0909266eeb78b8d6bb1323b10829cf2fe55e3c01a13fa1846fa98b371"),
        ("discogs-effnet-bs64-1.pb", "https://essentia.upf.edu/models/feature-extractors/discogs-effnet/discogs-effnet-bs64-1.pb", "3ed9af50d5367c0b9c795b294b00e7599e4943244f4cbd376869f3bfc87721b1"),
        ("approachability_regression-discogs-effnet-1.pb", "https://essentia.upf.edu/models/classification-heads/approachability/approachability_regression-discogs-effnet-1.pb", "7ffc208865426fb3aa2842f676b42fc6128282088c9b1d1fd2aba14b17cd121c"),
        ("engagement_regression-discogs-effnet-1.pb", "https://essentia.upf.edu/models/classification-heads/engagement/engagement_regression-discogs-effnet-1.pb", "43031d40b3a380e1995c8495a108d14ca74f620d924fad9b28df4189c84d20c5"),
        ("genre_discogs400-discogs-effnet-1.pb", "https://essentia.upf.edu/models/classification-heads/genre_discogs400/genre_discogs400-discogs-effnet-1.pb", "3885ba078a35249af94b8e5e4247689afac40deca4401a4bc888daf5a579c01c"),
        ("genre_discogs400-discogs-effnet-1.json", "https://essentia.upf.edu/models/classification-heads/genre_discogs400/genre_discogs400-discogs-effnet-1.json", "2d367319d9b782ffa10f69abf0e805b3ac4e10899025e5bdbaceda3919b243e0"),
        ("discogs-maest-30s-pw-519l-2.pb", "https://essentia.upf.edu/models/feature-extractors/maest/discogs-maest-30s-pw-519l-2.pb", "92783feb21187443d058b4f16d7a76f47888d43fbdc7a28e8bcc8e024603bd20"),
        ("discogs-maest-30s-pw-519l-2.json", "https://essentia.upf.edu/models/feature-extractors/maest/discogs-maest-30s-pw-519l-2.json", "83240aa553ffb491b0ec5a24565eb612553e5f38da5207403c25b890c5b34acd"),
        ("genre_discogs519-discogs-maest-30s-pw-519l-1.pb", "https://essentia.upf.edu/models/classification-heads/genre_discogs519/genre_discogs519-discogs-maest-30s-pw-519l-1.pb", "0f5d61d9b62e4a27dac058926e986eb424dca0fdb920c066ab53158229cff498"),
        ("genre_discogs519-discogs-maest-30s-pw-519l-1.json", "https://essentia.upf.edu/models/classification-heads/genre_discogs519/genre_discogs519-discogs-maest-30s-pw-519l-1.json", "07015a89f1a0e9b7cdceb63933783023d85f3ac4b36ce5c1b5488bd1fbad2304")
    };
    private static readonly string[] RequiredEnhancedModelFiles =
    {
        "msd-musicnn-1.pb",
        "mood_happy-msd-musicnn-1.pb",
        "mood_sad-msd-musicnn-1.pb",
        "mood_relaxed-msd-musicnn-1.pb",
        "mood_aggressive-msd-musicnn-1.pb"
    };
    private static readonly string[] RequiredGenre519ModelFiles =
    {
        "discogs-maest-30s-pw-519l-2.pb",
        "genre_discogs519-discogs-maest-30s-pw-519l-1.pb",
        "genre_discogs519-discogs-maest-30s-pw-519l-1.json"
    };
    private static readonly string[] RequiredGenre400ModelFiles =
    {
        "discogs-effnet-bs64-1.pb",
        "genre_discogs400-discogs-effnet-1.pb",
        "genre_discogs400-discogs-effnet-1.json"
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
    private readonly IAudiomackVibeMetadataService _vibeMetadataService;
    private readonly EmbeddedVibeMetadataReader _embeddedVibeReader;
    private readonly SonicAnalysisSettingsStore _sonicSettingsStore;
    private int _sonicEnabled;
    private readonly AutoTagProfileResolutionService _profileResolutionService;
    private readonly MoodBucketService _moodBucketService;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _analysisLock = new(1, 1);
    private readonly SemaphoreSlim _manualRunSignal = new(0, 1);
    private readonly object _runtimeLock = new();
    private readonly List<VibeAnalysisRecentItemDto> _recentAnalyses = new();
    private readonly object _mlCapabilityLock = new();
    private VibeAnalyzerWorker? _analyzerWorker;
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
    private DateTimeOffset _analyzerLastFallbackLoggedAt = DateTimeOffset.MinValue;
    private string? _analyzerLastFallbackReason;
    private DateTimeOffset _analyzerLastDegradationLoggedAt = DateTimeOffset.MinValue;
    private string? _analyzerLastDegradationReason;
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
        IAudiomackVibeMetadataService vibeMetadataService,
        EmbeddedVibeMetadataReader embeddedVibeReader,
        SonicAnalysisSettingsStore sonicSettingsStore,
        AutoTagProfileResolutionService profileResolutionService,
        MoodBucketService moodBucketService,
        IConfiguration configuration)
    {
        _repository = repository;
        _configStore = configStore;
        _logger = logger;
        _settingsStore = settingsStore;
        _lastFmTagService = lastFmTagService;
        _vibeMetadataService = vibeMetadataService;
        _embeddedVibeReader = embeddedVibeReader;
        _sonicSettingsStore = sonicSettingsStore;
        _profileResolutionService = profileResolutionService;
        _moodBucketService = moodBucketService;
        _configuration = configuration;
    }

    public async Task ApplySettingsAsync(VibeAnalysisSettingsDto settings, CancellationToken cancellationToken)
    {
        if (settings.Enabled)
        {
            lock (_runtimeLock)
            {
                if (string.Equals(_runtimeState, VibeAnalysisRuntimeStates.Paused, StringComparison.OrdinalIgnoreCase))
                {
                    _runtimeState = VibeAnalysisRuntimeStates.Idle;
                    _runtimeUpdatedAtUtc = DateTimeOffset.UtcNow;
                }
            }
            WakeAnalysisLoop();
            return;
        }

        PauseActiveRun();
        ClearPendingRunSignal();
        await _analysisLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await ResetInterruptedProcessingRowsAsync(CancellationToken.None).ConfigureAwait(false);
            await StopAnalyzerWorkerAsync().ConfigureAwait(false);
        }
        finally
        {
            _analysisLock.Release();
        }
    }

    public async Task<bool> TryStartManualAnalysisAsync(int batchSize)
        => (await RequestManualAnalysisAsync(batchSize).ConfigureAwait(false)).Queued;

    /// <summary>
    /// Requests a manual run and reports why it was or was not accepted.
    ///
    /// A rejected request is not the same as a disabled feature: enabling
    /// background analysis immediately starts a scheduled pass, so a manual run
    /// requested a moment later is declined because a pass is already active.
    /// Collapsing that into a plain false made the UI tell users to enable a
    /// setting they had already enabled.
    /// </summary>
    internal async Task<VibeAnalysisRunRequest> RequestManualAnalysisAsync(int batchSize)
    {
        if (!await IsAnalysisEnabledAsync().ConfigureAwait(false))
        {
            return new VibeAnalysisRunRequest(VibeAnalysisRunOutcome.Disabled, VibeAnalysisRunOutcome.Disabled.Reason());
        }

        return TryQueueAnalysisRun(batchSize);
    }

    public async Task<bool> TrySignalBackgroundAnalysisAsync(int batchSize)
        => await IsAnalysisEnabledAsync().ConfigureAwait(false)
            && TryQueueAnalysisRun(batchSize).Queued;

    private VibeAnalysisRunRequest TryQueueAnalysisRun(int batchSize)
    {
        var shouldSignal = false;
        lock (_runtimeLock)
        {
            if (_manualRunPending || _activeRunCancellation is not null || _analysisLock.CurrentCount == 0)
            {
                return new VibeAnalysisRunRequest(
                    VibeAnalysisRunOutcome.AlreadyRunning,
                    VibeAnalysisRunOutcome.AlreadyRunning.Reason());
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

        return new VibeAnalysisRunRequest(VibeAnalysisRunOutcome.Queued, VibeAnalysisRunOutcome.Queued.Reason());
    }

    private async Task<bool> IsAnalysisEnabledAsync()
        => (await _settingsStore.LoadAsync().ConfigureAwait(false)).Enabled;

    /// <summary>
    /// Sonic Analysis is opt-in and independent of Vibe Analysis. It is off by
    /// default because it adds a second inference pass per track.
    ///
    /// <para>The flag is cached because it is read while building a
    /// ProcessStartInfo, which is synchronous. It is refreshed from the settings
    /// store on apply and on the analysis loop, and can still be forced on through
    /// configuration or the environment for operators who never touch the UI.</para>
    /// </summary>
    private bool IsSonicAnalysisEnabled() => Volatile.Read(ref _sonicEnabled) != 0;

    /// <summary>
    /// Applies the Sonic settings to the running analyzer. Called when settings
    /// are saved so enabling Sonic takes effect without a restart.
    /// </summary>
    public async Task ApplySonicSettingsAsync(
        SonicAnalysisSettingsDto settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _sonicEnabled, ResolveSonicEnabled(settings) ? 1 : 0);

        // A long-running worker started without the flag can never produce a
        // vector, because the subprocess loaded no Sonic extractor. Replacing it
        // is what makes the change take effect.
        if (settings.Enabled && _analyzerWorker is not null)
        {
            await StopAnalyzerWorkerAsync().ConfigureAwait(false);
        }
    }

    private bool ResolveSonicEnabled(SonicAnalysisSettingsDto? settings)
        => (settings?.Enabled ?? false)
            || _configuration.GetValue(SonicAnalysisConfigurationPath, false)
            || IsTruthyEnvironment(SonicAnalysisEnabledEnvironmentVariable);

    private static bool IsTruthyEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is not null
            && (value.Equals("1", StringComparison.Ordinal)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private void ClearPendingRunSignal()
    {
        lock (_runtimeLock)
        {
            _manualRunPending = false;
            _manualRunBatchSize = 0;
        }

        while (_manualRunSignal.Wait(0, CancellationToken.None))
        {
            // Intentionally empty: draining every pending wake-up signal here, one per wait.
        }
    }

    private void WakeAnalysisLoop()
    {
        try
        {
            _manualRunSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already pending.
        }
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
                _runtimeUpdatedAtUtc,
                _analyzerWorker?.GetSnapshot() ?? new VibeAnalyzerWorkerSnapshot(
                    VibeAnalyzerWorkerStates.Stopped,
                    null,
                    null,
                    0));
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

        var track = summary ?? new MixTrackDto(result.TrackId, $"Track {result.TrackId}", UnknownTrackMetadata, UnknownTrackMetadata, null, null);
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
        var mixTrack = summary ?? new MixTrackDto(track.TrackId, $"Track {track.TrackId}", UnknownTrackMetadata, UnknownTrackMetadata, null, track.DurationMs);
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
                    await AnalyzeNowAsync(manualBatchSize, stoppingToken);
                    continue;
                }

                var settings = await _settingsStore.LoadAsync();

                // Refresh the Sonic flag every pass so a settings change written
                // by another process, or a first run after a restart, is picked up
                // without relying on ApplySonicSettingsAsync having been called.
                Volatile.Write(
                    ref _sonicEnabled,
                    ResolveSonicEnabled(await _sonicSettingsStore.LoadAsync().ConfigureAwait(false)) ? 1 : 0);

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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await StopAnalyzerWorkerAsync().ConfigureAwait(false);
    }

    private async Task StopAnalyzerWorkerAsync()
    {
        var worker = _analyzerWorker;
        _analyzerWorker = null;
        if (worker is not null)
        {
            await worker.DisposeAsync().ConfigureAwait(false);
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
                await AnalyzeStablePassesAsync(settings, stopWhenDisabled: true, run.Token);
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

    public async Task AnalyzeNowAsync(int batchSize, CancellationToken cancellationToken)
    {
        await _analysisLock.WaitAsync(cancellationToken);
        try
        {
            using var run = BeginRuntimeRun(cancellationToken);
            try
            {
                var settings = await _settingsStore.LoadAsync();
                if (settings.Enabled)
                {
                    await AnalyzeStablePassesAsync(
                        settings with { BatchSize = Math.Clamp(batchSize, 10, 500) },
                        stopWhenDisabled: true,
                        run.Token);
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
        if (trackId <= 0 || !await IsAnalysisEnabledAsync().ConfigureAwait(false))
        {
            return false;
        }

        await _analysisLock.WaitAsync(cancellationToken);
        try
        {
            if (!await IsAnalysisEnabledAsync().ConfigureAwait(false))
            {
                return false;
            }

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
            var completion = await AnalyzeTrackAsync(track, null, run.Token, summary);
            var result = await AttachLastFmTagsIfMissingAsync(completion.Result, summary, run.Token);

            await _repository.UpsertTrackAnalysisAsync(result, run.Token);
            await PersistSonicEmbeddingAsync(track, completion.Sonic, run.Token);
            var isComplete = IsAnalysisCompleteStatus(result.Status);
            await AssignMoodBucketsIfCompleteAsync(track.TrackId, isComplete, run.Token);

            RecordCompletedAnalysis(summary, result);

            return isComplete;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await ResetInterruptedProcessingRowsAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        finally
        {
            _analysisLock.Release();
        }
    }

    /// <summary>
    /// Stores the Sonic embedding for a track, if the analyzer produced one.
    ///
    /// Sonic persistence is strictly additive and fully isolated: any failure here
    /// is logged and swallowed, because a vector problem must never turn a
    /// completed semantic analysis into a failed track. The source file's size
    /// and modification time are recorded so a later run can decide the vector
    /// is stale without re-hashing the audio.
    /// </summary>
    private async Task PersistSonicEmbeddingAsync(
        TrackAnalysisInputDto track,
        SonicPayload? sonic,
        CancellationToken cancellationToken)
    {
        if (sonic is null || !IsSonicAnalysisEnabled())
        {
            return;
        }

        try
        {
            var vector = DecodeSonicVector(sonic);
            if (vector is null)
            {
                _logger.LogWarning(
                    "Sonic embedding for track {TrackId} rejected: {Reason}",
                    track.TrackId,
                    DescribeSonicRejection(sonic));
                return;
            }

            (long? size, DateTimeOffset? mtime) = TryReadSourceRevision(track.FilePath);
            await _repository.UpsertSonicEmbeddingAsync(
                new SonicEmbeddingDto(
                    track.TrackId,
                    track.LibraryId,
                    sonic.ModelId,
                    sonic.ModelVersion,
                    sonic.EmbeddingVersion,
                    vector.Count,
                    sonic.Pooling,
                    sonic.Normalization,
                    sonic.DistanceMetric,
                    vector,
                    size,
                    mtime,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // Isolated by design: the semantic analysis for this track is already
            // persisted and stays valid.
            _logger.LogWarning(ex, "Failed to persist Sonic embedding for track {TrackId}", track.TrackId);
        }
    }

    /// <summary>
    /// Decodes a base64 little-endian float32 vector, rejecting anything whose
    /// width disagrees with the declared dimension or that contains NaN or
    /// Infinity. A rejected vector must never be stored.
    /// </summary>
    private static IReadOnlyList<float>? DecodeSonicVector(SonicPayload sonic)
    {
        if (sonic.Dimensions <= 0 || string.IsNullOrWhiteSpace(sonic.VectorBase64))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(sonic.VectorBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        if (bytes.Length != sonic.Dimensions * sizeof(float))
        {
            return null;
        }

        var vector = new float[sonic.Dimensions];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector.Any(float.IsNaN) || vector.Any(float.IsInfinity) ? null : vector;
    }

    private static string DescribeSonicRejection(SonicPayload sonic)
    {
        var expected = sonic.Dimensions * sizeof(float);
        return $"declared {sonic.Dimensions} dimensions but the payload did not decode to "
            + $"{expected} bytes of little-endian float32, or it contained a non-finite value.";
    }

    private static (long? Size, DateTimeOffset? Mtime) TryReadSourceRevision(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return (null, null);
        }

        try
        {
            var info = new FileInfo(filePath);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (null, null);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return (null, null);
        }
    }

    private async Task<TrackAnalysisResultDto> AttachLastFmTagsIfMissingAsync(
        TrackAnalysisResultDto result,
        MixTrackDto? summary,
        CancellationToken cancellationToken)
    {
        if (result.LastfmTags is not null || summary is null)
        {
            return result;
        }

        var tags = await _lastFmTagService.GetTrackTagsAsync(summary.ArtistName, summary.Title, cancellationToken);
        return tags is null ? result : result with { LastfmTags = tags };
    }

    private async Task AssignMoodBucketsIfCompleteAsync(long trackId, bool isComplete, CancellationToken cancellationToken)
    {
        if (isComplete)
        {
            await AssignTrackMoodBucketsAsync(trackId, cancellationToken);
        }
    }

    private async Task AnalyzeStablePassesAsync(
        VibeAnalysisSettingsDto settings,
        bool stopWhenDisabled,
        CancellationToken cancellationToken)
    {
        var attemptedTrackIds = new HashSet<long>();
        var folders = await _repository.GetConfiguredEnabledMusicFoldersAsync(cancellationToken);
        var orderedFolderIds = ResolveAnalysisFolderOrder(folders, settings);
        if (orderedFolderIds.Count == 0)
        {
            return;
        }

        // Every enabled music library is resolved in a single query. The repository
        // encodes the alphabetical folder order in a temp scope table and then sorts
        // the complete result set in memory, because ArtistOrderKey needs the raw
        // artist credit to split multi-artist names and strip diacritics. Querying one
        // folder at a time therefore rescanned every table once per folder per pass for
        // no ordering benefit, and the terminating "nothing left" query could not be
        // avoided. The loop below now issues one query per pass and exits as soon as a
        // pass finds nothing new, which also picks up files added mid-run.
        var includeCompletedStandard = await IsEnhancedAnalysisAvailableForRetry(cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (stopWhenDisabled && await ShouldStopForDisabledAnalysisAsync(cancellationToken))
            {
                return;
            }

            var snapshot = await _repository.GetTracksForAnalysisAsync(
                int.MaxValue,
                includeCompletedStandard: includeCompletedStandard,
                completedStandardRetryBeforeUtc: includeCompletedStandard
                    ? DateTimeOffset.UtcNow.Subtract(CompletedStandardEnhancedRetryDelay)
                    : null,
                orderedLibraryIds: orderedFolderIds,
                excludedTrackIds: attemptedTrackIds,
                cancellationToken: cancellationToken);
            if (snapshot.Count == 0)
            {
                return;
            }

            await AnalyzeFrozenSnapshotAsync(
                snapshot,
                Math.Clamp(settings.BatchSize, 10, 500),
                attemptedTrackIds,
                stopWhenDisabled,
                cancellationToken);
        }
    }

    /// <summary>
    /// The order libraries are analysed in.
    ///
    /// <para>Alphabetical by folder display name by default, or the order a person
    /// arranged when they asked for a custom library order. Alphabetical ordering
    /// applies to <em>albums</em> within a library, not to the libraries themselves.</para>
    ///
    /// <para>A stored order REORDERS and never excludes. An earlier version treated the
    /// stored list as the whole scope, so a list left stale by a renamed or
    /// re-added library silently stopped that library being analysed at all, with
    /// nothing reporting the omission. Anything the list does not mention — a library
    /// added later, one whose id changed — is appended in alphabetical position rather
    /// than dropped, so every enabled music folder is always in scope.</para>
    /// </summary>
    internal static IReadOnlyList<long> ResolveAnalysisFolderOrder(
        IReadOnlyList<FolderDto> enabledAudioFolders,
        VibeAnalysisSettingsDto? settings = null)
    {
        var alphabetical = enabledAudioFolders
            .OrderBy(static folder => folder.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static folder => folder.Id)
            .Select(static folder => folder.Id)
            .ToList();

        var storedOrder = settings?.LibraryOrder;
        if (settings?.UseLibraryOrder != true || storedOrder is null || storedOrder.Count == 0)
        {
            return alphabetical;
        }

        var remaining = new Dictionary<long, int>();
        for (var index = 0; index < alphabetical.Count; index++)
        {
            remaining[alphabetical[index]] = index;
        }

        var ordered = new List<long>(alphabetical.Count);
        foreach (var folderId in storedOrder)
        {
            // Unknown ids are ignored rather than trusted: a stored list outlives the
            // libraries it names, and an id that no longer resolves must not be passed
            // downstream as a scope.
            if (remaining.Remove(folderId))
            {
                ordered.Add(folderId);
            }
        }

        // Appended, not dropped. This is the whole reason a stale custom order cannot
        // narrow what gets analysed.
        ordered.AddRange(remaining
            .OrderBy(static entry => entry.Value)
            .Select(static entry => entry.Key));

        return ordered;
    }

    internal static IReadOnlyList<(int Start, int End)> BuildStablePassRanges(
        IReadOnlyList<TrackAnalysisInputDto> snapshot,
        int batchSize)
        => EnhancementBatchPlanner.BuildRanges(
            snapshot.Select(static track => track.FilePath).ToList(),
            snapshot.Count,
            batchSize);

    private async Task AnalyzeFrozenSnapshotAsync(
        IReadOnlyList<TrackAnalysisInputDto> snapshot,
        int batchSize,
        HashSet<long> attemptedTrackIds,
        bool stopWhenDisabled,
        CancellationToken cancellationToken)
    {
        foreach (var (start, end) in BuildStablePassRanges(snapshot, batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopWhenDisabled && await ShouldStopForDisabledAnalysisAsync(cancellationToken))
            {
                return;
            }

            var batch = snapshot.Skip(start).Take(end - start).ToList();
            foreach (var track in batch)
            {
                attemptedTrackIds.Add(track.TrackId);
            }

            await AnalyzeBatchAsync(batch, stopWhenDisabled, cancellationToken);
        }
    }

    private async Task<AnalysisBatchOutcome> AnalyzeBatchAsync(
        IReadOnlyList<TrackAnalysisInputDto> tracks,
        bool stopWhenDisabled,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return new AnalysisBatchOutcome(0, 0);
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

            summaryMap.TryGetValue(track.TrackId, out var summary);
            var outcome = await AnalyzeBatchTrackAsync(track, summary, batchPredictions, cancellationToken);
            processedTracks++;
            if (outcome.Completed)
            {
                completed++;
            }
            else if (outcome.Error != null)
            {
                errors++;
                RecordErrorBucket(errorBuckets, outcome.Error);
            }
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Vibe analysis completed (ok={completed}, errors={errors})."));

        LogBatchErrors(errors, errorBuckets);

        return new AnalysisBatchOutcome(processedTracks, completed);
    }

    private sealed record BatchAnalysisOutcome(bool Completed, string? Error);

    private async Task<BatchAnalysisOutcome> AnalyzeBatchTrackAsync(
        TrackAnalysisInputDto track,
        MixTrackDto? summary,
        IReadOnlyDictionary<long, BatchPrediction>? batchPredictions,
        CancellationToken cancellationToken)
    {
        await _repository.MarkTrackAnalysisProcessingAsync(track.TrackId, track.LibraryId, cancellationToken);
        SetCurrentAnalysis(track, summary);
        var completion = await AnalyzeTrackWithOptionalLastFmAsync(track, summary, batchPredictions, cancellationToken);
        var result = completion.Result;
        await _repository.UpsertTrackAnalysisAsync(result, cancellationToken);
        await PersistSonicEmbeddingAsync(track, completion.Sonic, cancellationToken);
        if (IsAnalysisCompleteStatus(result.Status))
        {
            await AssignTrackMoodBucketsAsync(track.TrackId, cancellationToken);
            RecordCompletedAnalysis(summary, result);
            return new BatchAnalysisOutcome(true, null);
        }

        return IsAnalysisErrorStatus(result.Status)
            ? new BatchAnalysisOutcome(false, result.Error)
            : new BatchAnalysisOutcome(false, null);
    }

    private void LogBatchErrors(int errors, Dictionary<string, int> errorBuckets)
    {
        if (errors <= 0)
        {
            return;
        }

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

    private async Task<bool> IsEnhancedAnalysisAvailableForRetry(CancellationToken cancellationToken)
    {
        var capability = await GetOrProbeMlCapability(cancellationToken).ConfigureAwait(false);
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
               || string.Equals(status, CompletedAnalysisStatus, StringComparison.OrdinalIgnoreCase);
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

    private async Task<TrackAnalysisCompletion> AnalyzeTrackWithOptionalLastFmAsync(
        TrackAnalysisInputDto track,
        MixTrackDto? summary,
        IReadOnlyDictionary<long, BatchPrediction>? batchPredictions,
        CancellationToken cancellationToken)
    {
        var completion = await AnalyzeTrackAsync(track, batchPredictions, cancellationToken, summary);
        var result = await AttachLastFmTagsIfMissingAsync(completion.Result, summary, cancellationToken);
        return completion with { Result = result };
    }

    /// <summary>
    /// A completed track analysis plus the Sonic vector that was produced for it.
    ///
    /// The vector travels beside the result rather than inside it: carrying a
    /// multi-kilobyte embedding on every TrackAnalysisResultDto would put it in
    /// every ordinary analysis query, which is exactly what keeping Sonic in its
    /// own table exists to prevent.
    /// </summary>
    private sealed record TrackAnalysisCompletion(TrackAnalysisResultDto Result, SonicPayload? Sonic);

    private async Task<TrackAnalysisCompletion> AnalyzeTrackAsync(
        TrackAnalysisInputDto track,
        IReadOnlyDictionary<long, BatchPrediction>? batchPredictions,
        CancellationToken cancellationToken,
        MixTrackDto? summary = null)
    {
        try
        {
            var candidateErrors = new List<string>();
            foreach (var candidatePath in EnumerateAnalysisCandidatePaths(track))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = TryPrepareAnalysisCandidate(track, candidatePath);
                if (candidate.Error is not null)
                {
                    candidateErrors.Add(candidate.Error);
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var metrics = CalculateTrackSignalMetrics(candidate.Samples, candidate.SampleRate, candidate.Track.DurationMs);
                var (analysisOutput, predictionFailure) = await ResolveAnalysisOutputAsync(
                    track,
                    candidatePath,
                    candidate.Track,
                    batchPredictions,
                    cancellationToken);

                if (analysisOutput is null
                    && !string.IsNullOrWhiteSpace(predictionFailure))
                {
                    _logger.LogWarning("Vibe analyzer fallback to standard for {FilePath}: {Reason}", candidate.Track.FilePath, predictionFailure);
                    LogAnalyzerFallback(predictionFailure);
                }

                var storedAnalysis = await _repository
                    .GetTrackAnalysisAsync(track.TrackId, cancellationToken)
                    .ConfigureAwait(false);
                VibeSemantics? stored = storedAnalysis is { Status: CompletedAnalysisStatus }
                                       && storedAnalysis.ResolvedGenres is not null
                    ? new VibeSemantics(
                        storedAnalysis.ResolvedGenres,
                        storedAnalysis.ResolvedStyles,
                        storedAnalysis.ResolvedMoods,
                        storedAnalysis.SemanticEvidenceJson,
                        storedAnalysis.GenreModel,
                        storedAnalysis.ValenceSource,
                        storedAnalysis.ArousalSource,
                        storedAnalysis.EmbeddedSemanticFingerprint)
                    : null;

                var vibe = await BuildVibeSemanticsAsync(
                    analysisOutput, summary, cancellationToken, candidate.Track.FilePath, stored);
                var result = CreateCompletedAnalysisResult(candidate.Track, metrics, analysisOutput, summary, vibe);
                return new TrackAnalysisCompletion(result, analysisOutput?.SonicEmbedding);
            }

            return new TrackAnalysisCompletion(
                CreateFailure(
                    track.TrackId,
                    track.LibraryId,
                    FailedAnalysisStatus,
                    candidateErrors.Count == 0
                        ? "No usable audio candidates."
                        : $"No usable audio candidates: {string.Join(" | ", candidateErrors)}"),
                null);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return new TrackAnalysisCompletion(
                CreateFailure(track.TrackId, track.LibraryId, FailedAnalysisStatus, ex.Message),
                null);
        }
    }

    private sealed record AnalysisCandidate(
        TrackAnalysisInputDto Track,
        float[] Samples,
        int SampleRate,
        string? Error);

    private static AnalysisCandidate TryPrepareAnalysisCandidate(TrackAnalysisInputDto track, string candidatePath)
    {
        var candidateTrack = track with { FilePath = candidatePath };
        if (!TryLoadTrackSamples(candidateTrack, out var samples, out var sampleRate, out var failure))
        {
            return new AnalysisCandidate(candidateTrack, Array.Empty<float>(), 0, FormatAnalysisCandidateError(candidatePath, failure?.Error));
        }

        if (samples.Length == 0)
        {
            return new AnalysisCandidate(candidateTrack, samples, sampleRate, FormatAnalysisCandidateError(candidatePath, "No audio samples."));
        }

        return new AnalysisCandidate(candidateTrack, samples, sampleRate, null);
    }

    private async Task<(AnalysisOutput? Output, string? FailureReason)> ResolveAnalysisOutputAsync(
        TrackAnalysisInputDto originalTrack,
        string candidatePath,
        TrackAnalysisInputDto candidateTrack,
        IReadOnlyDictionary<long, BatchPrediction>? batchPredictions,
        CancellationToken cancellationToken)
    {
        if (!TryUseBatchPrediction(originalTrack, candidatePath, batchPredictions, out var batchPrediction))
        {
            return await TryPredictAnalysisOutputAsync(candidateTrack.FilePath, cancellationToken);
        }

        if (batchPrediction.Output is not null)
        {
            return (batchPrediction.Output, batchPrediction.FailureReason);
        }

        var (singleTrackOutput, singleTrackFailure) = await TryPredictAnalysisOutputAsync(candidateTrack.FilePath, cancellationToken);
        return singleTrackOutput is not null
            ? (singleTrackOutput, null)
            : (null, CombinePredictionFailures(batchPrediction.FailureReason, singleTrackFailure));
    }

    private static bool TryUseBatchPrediction(
        TrackAnalysisInputDto track,
        string candidatePath,
        IReadOnlyDictionary<long, BatchPrediction>? batchPredictions,
        out BatchPrediction batchPrediction)
    {
        batchPrediction = default!;
        if (!PathMatchesPrimaryAnalysisCandidate(track, candidatePath)
            || batchPredictions is null
            || !batchPredictions.TryGetValue(track.TrackId, out var prediction))
        {
            return false;
        }

        batchPrediction = prediction;
        return true;
    }

    private static IEnumerable<string> EnumerateAnalysisCandidatePaths(TrackAnalysisInputDto track)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(track.FilePath) && seen.Add(track.FilePath))
        {
            yield return track.FilePath;
        }

        foreach (var alternatePath in (track.AlternateFilePaths ?? Array.Empty<string>())
            .Where(alternatePath => !string.IsNullOrWhiteSpace(alternatePath) && seen.Add(alternatePath)))
        {
            yield return alternatePath;
        }
    }

    private static bool PathMatchesPrimaryAnalysisCandidate(TrackAnalysisInputDto track, string candidatePath)
        => string.Equals(track.FilePath, candidatePath, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private static string FormatAnalysisCandidateError(string candidatePath, string? error)
    {
        var reason = string.IsNullOrWhiteSpace(error) ? "unknown decode failure" : error.Trim();
        return $"{candidatePath}: {reason}";
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

        var resolution = await TryResolveBatchAnalyzerContext(tracks, cancellationToken).ConfigureAwait(false);
        if (resolution.Context is null)
        {
            return resolution.FailureMap;
        }

        var context = resolution.Context;

        var request = tracks.Select(track => new BatchAnalysisRequestItem(track.TrackId, track.FilePath)).ToList();
        var batchTempFilePath = WriteBatchRequestToSecureTempFile(request);
        try
        {
            using var process = Process.Start(
                CreateBatchProcessStartInfo(context, batchTempFilePath, IsSonicAnalysisEnabled()));
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

    private async Task<BatchAnalyzerResolution> TryResolveBatchAnalyzerContext(
        IReadOnlyList<TrackAnalysisInputDto> tracks,
        CancellationToken cancellationToken)
    {
        var failureMap = new Dictionary<long, BatchPrediction>();

        var capability = await GetOrProbeMlCapability(cancellationToken).ConfigureAwait(false);
        if (!capability.Available)
        {
            var reason = capability.Reason ?? "Unknown reason.";
            LogMlUnavailable(reason);
            return new BatchAnalyzerResolution(null, CreateBatchFailureMap(tracks, reason));
        }

        var scriptPath = ResolveAnalyzerScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            var reason = $"Analyzer script missing at {scriptPath}. Set {VibePathEnvironmentVariable} or ensure {ToolsDirectoryName}/{VibeAnalyzerScriptFileName} exists.";
            LogMlUnavailable(reason);
            return new BatchAnalyzerResolution(null, CreateBatchFailureMap(tracks, reason));
        }

        var modelsDir = ResolveModelsDirectory();
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
        {
            var reason = $"Models directory missing at {modelsDir}. Set {VibeModelsDirectoryEnvironmentVariable} or place models under {ToolsDirectoryName}/{ModelsDirectoryName}.";
            LogMlUnavailable(reason);
            return new BatchAnalyzerResolution(null, CreateBatchFailureMap(tracks, reason));
        }

        var batchTimeout = ResolveAnalyzerBatchTimeout();
        return new BatchAnalyzerResolution(
            new BatchAnalyzerContext(
                scriptPath,
                modelsDir,
                ResolveAnalyzerWorkers(),
                (int)ResolveAnalyzerTimeout().TotalSeconds,
                batchTimeout,
                (int)batchTimeout.TotalSeconds),
            failureMap);
    }

    private sealed record BatchAnalyzerResolution(BatchAnalyzerContext? Context, Dictionary<long, BatchPrediction> FailureMap);

    private static ProcessStartInfo CreateBatchProcessStartInfo(BatchAnalyzerContext context, string batchTempFilePath, bool sonicEnabled = false)
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
        ConfigurePythonEnvironment(startInfo, sonicEnabled);
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

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorOutput = await process.StandardError.ReadToEndAsync(cancellationToken);
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

        if (new FileInfo(track.FilePath).Length <= 0)
        {
            failure = CreateFailure(track.TrackId, track.LibraryId, FailedAnalysisStatus, "Audio file is empty.");
            return false;
        }

        if (!TryReadWithFfmpeg(track.FilePath, 30, out samples, out sampleRate, out var ffmpegError))
        {
            var extension = Path.GetExtension(track.FilePath)?.ToLowerInvariant();
            var format = string.IsNullOrWhiteSpace(extension) ? "audio file" : extension;
            failure = CreateFailure(track.TrackId, track.LibraryId, FailedAnalysisStatus, ffmpegError ?? $"Unable to decode {format}.");
            return false;
        }

        return true;
    }

    internal sealed record TrackSignalMetrics(
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

    internal static TrackAnalysisResultDto CreateCompletedAnalysisResult(
        TrackAnalysisInputDto track,
        TrackSignalMetrics metrics,
        AnalysisOutput? analysisOutput,
        MixTrackDto? summary = null,
        VibeSemantics? vibe = null)
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
            CompletedAnalysisStatus,
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
            analysisOutput?.Loudness,
            vibe?.ResolvedGenres,
            vibe?.ResolvedStyles,
            vibe?.ResolvedMoods,
            vibe?.SemanticEvidenceJson,
            analysisOutput?.GenreModel ?? vibe?.GenreModel,
            analysisOutput?.ValenceSource ?? vibe?.ValenceSource,
            analysisOutput?.ArousalSource ?? vibe?.ArousalSource);
    }

    /// <summary>
    /// Orchestrates Vibe semantics: online lookups (Last.fm + Audiomack, both
    /// non-fatal) plus acoustic evidence from the analyzer output, resolved by
    /// VibeSemanticResolver. Audiomack/Last.fm failure must never fail analysis.
    /// </summary>
    internal async Task<VibeSemantics?> BuildVibeSemanticsAsync(
        AnalysisOutput? analysisOutput,
        MixTrackDto? summary,
        CancellationToken cancellationToken,
        string? filePath = null,
        VibeSemantics? stored = null)
    {
        // Tier 0: read the semantic tags embedded in the file.
        EmbeddedVibeMetadata? embedded = null;
        string? fingerprint = null;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            try
            {
                var (id3, vorbis, mp4) = await ResolveStyleTagNamesAsync(filePath, cancellationToken).ConfigureAwait(false);
                embedded = _embeddedVibeReader.Read(filePath, id3, vorbis, mp4);
                fingerprint = embedded.ComputeFingerprint();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Vibe embedded tag read failed for {FilePath}", filePath);
            }
        }

        // Staleness: if AutoTag's embedded semantics are unchanged since the stored
        // resolution, reuse it — no online lookups and no MAEST rerun.
        if (stored is not null && fingerprint is not null && stored.EmbeddedSemanticFingerprint == fingerprint)
        {
            return stored;
        }

        var artist = summary?.ArtistName?.Trim();
        var title = summary?.Title?.Trim();

        IReadOnlyList<LastFmTagService.LastFmTagEvidence>? trackTags = null;
        IReadOnlyList<LastFmTagService.LastFmTagEvidence>? artistTags = null;
        AudiomackVibeMetadata? audiomack = null;

        var genreCovered = embedded?.Genres.Count > 0;
        var styleCovered = embedded?.Styles.Count > 0;
        var moodCovered = embedded?.Moods.Count > 0;
        var allDimensionsEmbedded = genreCovered && styleCovered && moodCovered;

        if (!allDimensionsEmbedded && !string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
        {
            try
            {
                trackTags = await _lastFmTagService
                    .GetTrackTagEvidenceAsync(artist, title, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Vibe Last.fm track evidence failed for {Artist} - {Title}", artist, title);
            }

            try
            {
                artistTags = await _lastFmTagService
                    .GetArtistTagEvidenceAsync(artist, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Vibe Last.fm artist evidence failed for {Artist}", artist);
            }

            try
            {
                audiomack = await _vibeMetadataService.FindTrackAsync(artist, title, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Vibe Audiomack lookup failed for {Artist} - {Title}", artist, title);
            }
        }

        return BuildVibeSemanticsCore(analysisOutput, embedded, audiomack, trackTags, artistTags)
            with { EmbeddedSemanticFingerprint = fingerprint };
    }

    /// <summary>Resolves the active AutoTag style raw-field names per format from
    /// the track's assigned profile; falls back to the AutoTag default (STYLE).</summary>
    private async Task<(string? Id3, string? Vorbis, string? Mp4)> ResolveStyleTagNamesAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        // The Core AutoTagSettings type does not expose stylesCustomTag, so the
        // reader falls back to AutoTag's default STYLE field name (the value the
        // stock profiles write). The reader accepts explicit overrides when the
        // profile-level lookup is added.
        await Task.CompletedTask.ConfigureAwait(false);
        return (null, null, null);
    }

    internal static VibeSemantics BuildVibeSemanticsCore(
        AnalysisOutput? analysisOutput,
        EmbeddedVibeMetadata? embedded,
        AudiomackVibeMetadata? audiomack,
        IReadOnlyList<LastFmTagService.LastFmTagEvidence>? trackTags,
        IReadOnlyList<LastFmTagService.LastFmTagEvidence>? artistTags)
    {
        var acousticGenres = analysisOutput?.EssentiaGenreEvidence?
            .Select(item => new VibeSemanticResolver.AcousticGenreEvidence(item.Label, item.Score, item.Model))
            .ToList();

        List<VibeSemanticResolver.AcousticMoodEvidence>? acousticMoods = null;
        var moodScores = analysisOutput?.MoodScores;
        if (moodScores is not null)
        {
            acousticMoods = new List<VibeSemanticResolver.AcousticMoodEvidence>
            {
                new("Happy", moodScores.Happy),
                new("Sad", moodScores.Sad),
                new("Relaxed", moodScores.Relaxed),
                new("Aggressive", moodScores.Aggressive),
                new("Party", moodScores.Party),
                new("Acoustic", moodScores.Acoustic),
                new("Electronic", moodScores.Electronic)
            };
        }

        var resolution = VibeSemanticResolver.Resolve(embedded, audiomack, trackTags, artistTags, acousticGenres, acousticMoods);

        var evidenceJson = JsonSerializer.Serialize(
            resolution.SemanticEvidence.Select(item => new
            {
                source = item.Source,
                kind = item.Kind.ToString().ToLowerInvariant(),
                rawValue = item.RawValue,
                canonicalValue = item.CanonicalValue,
                scope = item.Scope.ToString().ToLowerInvariant(),
                strength = item.Strength,
                matchConfidence = item.MatchConfidence,
                finalWeight = item.FinalWeight
            }),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        return new VibeSemantics(
            resolution.ResolvedGenres,
            resolution.ResolvedStyles,
            resolution.ResolvedMoods,
            evidenceJson,
            analysisOutput?.GenreModel,
            analysisOutput?.ValenceSource,
            analysisOutput?.ArousalSource);
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

        return new (string Mood, double? Score)[]
            {
                ("happy", moodScores.Happy),
                ("sad", moodScores.Sad),
                ("relaxed", moodScores.Relaxed),
                ("aggressive", moodScores.Aggressive),
                ("party", moodScores.Party),
                ("acoustic", moodScores.Acoustic),
                ("electronic", moodScores.Electronic)
            }
            .Where(static item => item.Score >= 0.6)
            .Select(static item => item.Mood)
            .ToArray();
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

    private async Task<(AnalysisOutput? Output, string? FailureReason)> TryPredictAnalysisOutputAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = await ResolveAnalyzerExecutionContext(cancellationToken).ConfigureAwait(false);
        if (context.FailureReason != null)
        {
            LogMlUnavailable(context.FailureReason);
            return (null, context.FailureReason);
        }

        try
        {
            var analysisTimeout = ResolveAnalyzerTimeout();
            _analyzerWorker ??= new VibeAnalyzerWorker(
                () => CreatePersistentAnalyzerStartInfo(context.ScriptPath!, context.ModelsDir!, IsSonicAnalysisEnabled()),
                analysisTimeout);
            var workerResult = await _analyzerWorker.AnalyzeAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (!workerResult.Succeeded || string.IsNullOrWhiteSpace(workerResult.PayloadJson))
            {
                return (null, workerResult.FailureReason ?? "Vibe analyzer worker failed.");
            }

            var parsed = JsonSerializer.Deserialize<AnalysisOutput>(workerResult.PayloadJson, CaseInsensitiveJsonOptions);
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

    private static ProcessStartInfo CreatePersistentAnalyzerStartInfo(string scriptPath, string modelsDir, bool sonicEnabled = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePythonExecutable(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--worker");
        startInfo.ArgumentList.Add("--models");
        startInfo.ArgumentList.Add(modelsDir);
        ConfigurePythonEnvironment(startInfo, sonicEnabled);
        return startInfo;
    }

    private sealed record AnalyzerExecutionContext(
        string? ScriptPath,
        string? ModelsDir,
        string? FailureReason);

    private async Task<AnalyzerExecutionContext> ResolveAnalyzerExecutionContext(CancellationToken cancellationToken)
    {
        var capability = await GetOrProbeMlCapability(cancellationToken).ConfigureAwait(false);
        if (!capability.Available)
        {
            return new AnalyzerExecutionContext(null, null, capability.Reason ?? "Unknown reason.");
        }

        var scriptPath = ResolveAnalyzerScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            return new AnalyzerExecutionContext(
                null,
                null,
                $"Analyzer script missing at {scriptPath}. Set VIBE_ANALYZER_PATH or ensure Tools/vibe_analyzer.py exists.");
        }

        var modelsDir = ResolveModelsDirectory();
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
        {
            return new AnalyzerExecutionContext(
                null,
                null,
                $"Models directory missing at {modelsDir}. Set VIBE_ANALYZER_MODELS or place models under Tools/models.");
        }

        return new AnalyzerExecutionContext(scriptPath, modelsDir, null);
    }

    private static TimeSpan ResolveProbeTimeout()
    {
        var timeoutSeconds = DefaultVibeAnalyzerProbeTimeoutSeconds;
        var configuredTimeout = Environment.GetEnvironmentVariable(VibeAnalyzerProbeTimeoutSecondsEnvironmentVariable);
        if (int.TryParse(configuredTimeout, out var parsedTimeoutSeconds))
        {
            timeoutSeconds = parsedTimeoutSeconds;
        }

        // The probe loads every model graph, including the ~332 MB Discogs519
        // MAEST graph, so the budget must not be a short fixed value.
        timeoutSeconds = Math.Clamp(timeoutSeconds, MinVibeAnalyzerProbeTimeoutSeconds, MaxVibeAnalyzerProbeTimeoutSeconds);

        return TimeSpan.FromSeconds(timeoutSeconds);
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

    private async Task<MlCapability> GetOrProbeMlCapability(CancellationToken cancellationToken)
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

        await EnsureMlRuntimeProvisioned(cancellationToken).ConfigureAwait(false);
        var capability = ProbeMlCapability();
        if (capability.Available && !string.IsNullOrWhiteSpace(capability.Reason))
        {
            // Enhanced mode works but the acoustic genre branch degraded. Report it
            // once per throttle window instead of dropping the detail.
            LogGenreModelDegradation(capability.Reason!);
        }

        lock (_mlCapabilityLock)
        {
            _mlCapability = capability;
            _mlCapabilityLastCheckedAt = DateTimeOffset.UtcNow;
        }

        return capability;
    }

    private void LogGenreModelDegradation(string reason)
    {
        var sanitized = SanitizeAnalyzerFailure(reason);
        var now = DateTimeOffset.UtcNow;
        lock (_runtimeLock)
        {
            if (string.Equals(_analyzerLastDegradationReason, sanitized, StringComparison.Ordinal)
                && now - _analyzerLastDegradationLoggedAt < MlWarningThrottle)
            {
                return;
            }

            _analyzerLastDegradationReason = sanitized;
            _analyzerLastDegradationLoggedAt = now;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            now,
            "warning",
            $"Vibe analysis is running in reduced genre mode: {sanitized}"));
    }

    private async Task EnsureMlRuntimeProvisioned(CancellationToken cancellationToken)
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
        await EnsureModelsProvisioned(cancellationToken).ConfigureAwait(false);
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

    private async Task EnsureModelsProvisioned(CancellationToken cancellationToken)
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
            foreach (var (fileName, url, sha256) in RequiredModelFiles)
            {
                var destinationPath = Path.Join(modelsDir, fileName);
                if (ModelFileMatches(destinationPath, sha256))
                {
                    continue;
                }

                if (await TryDownloadModel(url, destinationPath, sha256, cancellationToken).ConfigureAwait(false))
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

    private static bool ModelFileMatches(string path, string expectedSha256)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (new FileInfo(path).Length <= 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return true;
        }

        try
        {
            return string.Equals(ComputeFileSha256(path), expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private async Task<bool> TryDownloadModel(string url, string destinationPath, string expectedSha256, CancellationToken cancellationToken)
    {
        var tempPath = destinationPath + ".tmp";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await MlBootstrapHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to download model {Url}. Status code {StatusCode}.", url, (int)response.StatusCode);
                return false;
            }

            using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using (var fileStream = File.Create(tempPath))
            {
                await networkStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            // Never promote an unverified transfer. A truncated model that merely
            // exists on disk is otherwise skipped forever and permanently breaks
            // enhanced analysis with no self-heal.
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actualSha256 = ComputeFileSha256(tempPath);
                if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Discarding model {FileName} from {Url}: checksum mismatch (expected {Expected}, got {Actual}).",
                        Path.GetFileName(destinationPath),
                        url,
                        expectedSha256,
                        actualSha256);
                    File.Delete(tempPath);
                    return false;
                }
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
        return TryRunProcess(pythonExecutable, "-c \"import essentia.standard\"", EssentiaImportTimeout, out _);
    }

    private static string ResolveEssentiaPackage()
    {
        var configured = Environment.GetEnvironmentVariable(VibeEssentiaPackageEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured) ? DefaultEssentiaPackage : configured.Trim();
    }

    private readonly record struct ProcessCaptureResult(
        bool StartFailed,
        bool TimedOut,
        int ExitCode,
        string StandardOutput,
        string StandardError);

    /// <summary>
    /// Runs a short-lived helper process and captures both pipes.
    ///
    /// The readers are armed before the blocking wait. Reading the pipes only
    /// after the process has exited deadlocks as soon as the child fills the OS
    /// pipe buffer, which pip reliably does during a large install and which
    /// Essentia/TensorFlow can do on a noisy load.
    /// </summary>
    private static ProcessCaptureResult RunProcessCapturingOutput(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new ProcessCaptureResult(true, false, -1, string.Empty, string.Empty);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            lock (stdout)
            {
                AppendBounded(stdout, args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            lock (stderr)
            {
                AppendBounded(stderr, args.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 1000, int.MaxValue)))
        {
            TryTerminate(process);
            return new ProcessCaptureResult(false, true, -1, string.Empty, string.Empty);
        }

        // The parameterless overload also waits for the asynchronous readers to
        // observe end-of-stream, so the captured output is complete.
        process.WaitForExit();

        string capturedStdout;
        string capturedStderr;
        lock (stdout)
        {
            capturedStdout = stdout.ToString();
        }

        lock (stderr)
        {
            capturedStderr = stderr.ToString();
        }

        return new ProcessCaptureResult(false, false, process.ExitCode, capturedStdout, capturedStderr);
    }

    private static void AppendBounded(StringBuilder builder, string line)
    {
        if (builder.Length >= MaxProcessOutputCharacters)
        {
            return;
        }

        builder.AppendLine(line);
        if (builder.Length > MaxProcessOutputCharacters)
        {
            builder.Remove(0, builder.Length - MaxProcessOutputCharacters);
        }
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

            var capture = RunProcessCapturingOutput(startInfo, timeout);
            if (capture.StartFailed)
            {
                error = $"Failed to start process {fileName}.";
                return false;
            }

            if (capture.TimedOut)
            {
                error = $"{fileName} timed out.";
                return false;
            }

            if (capture.ExitCode == 0)
            {
                return true;
            }

            error = string.IsNullOrWhiteSpace(capture.StandardError)
                ? capture.StandardOutput
                : capture.StandardError;
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

            var capture = RunProcessCapturingOutput(startInfo, ResolveProbeTimeout());
            if (capture.StartFailed)
            {
                return new MlCapability(false, $"Failed to start {Python3Executable} for Essentia probe.");
            }

            if (capture.TimedOut)
            {
                return new MlCapability(
                    false,
                    $"Essentia probe timed out after {(int)ResolveProbeTimeout().TotalSeconds}s. " +
                    "Raise VIBE_ANALYZER_PROBE_TIMEOUT_SECONDS on hosts with slow model loading.");
            }

            return ParseProbeResult(capture.ExitCode, capture.StandardOutput, capture.StandardError);
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

        // Enhanced mode is available, but the acoustic genre branch may have
        // degraded. That is a legitimate state, so it must not fail the probe,
        // but it must not be silently discarded either.
        if (probe.GenreModelLoaded != true)
        {
            return new MlCapability(
                true,
                BuildGenreModelDegradation(probe));
        }

        return new MlCapability(true, null);
    }

    private static string BuildGenreModelDegradation(ProbeOutput probe)
    {
        var details = new List<string>();
        if (probe.MissingGenreModelFiles is { Count: > 0 })
        {
            details.Add($"missing genre models: {string.Join(", ", probe.MissingGenreModelFiles)}");
        }

        if (probe.MissingOptional is { Count: > 0 })
        {
            details.Add($"missing optional Essentia algorithms: {string.Join(", ", probe.MissingOptional)}");
        }

        var suffix = details.Count == 0 ? string.Empty : $" ({string.Join("; ", details)})";
        return $"Acoustic genre analysis is degraded; no Discogs genre head initialized{suffix}.";
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

    private static void ConfigurePythonEnvironment(ProcessStartInfo startInfo, bool sonicEnabled = false)
    {
        // The analyzer decides whether to load the Sonic extractor from this flag.
        // It is always set explicitly (including to "0") so an inherited shell
        // value cannot silently enable a second inference pass per track.
        startInfo.Environment[SonicAnalysisEnabledEnvironmentVariable] = sonicEnabled ? "1" : "0";

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

        return Directory.GetDirectories(linuxLibRoot, "python*")
            .Select(pythonDir => Path.Join(pythonDir, "site-packages"))
            .FirstOrDefault(Directory.Exists);
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

    internal static string SanitizeAnalyzerFailure(string? reason)
        => DeezSpoTag.Core.Security.LogSanitizer.OneLine(reason, 256);

    private void LogAnalyzerFallback(string reason)
    {
        var sanitized = SanitizeAnalyzerFailure(reason);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "Unknown Essentia failure.";
        }

        var now = DateTimeOffset.UtcNow;
        lock (_runtimeLock)
        {
            if (string.Equals(_analyzerLastFallbackReason, sanitized, StringComparison.Ordinal)
                && now - _analyzerLastFallbackLoggedAt < MlWarningThrottle)
            {
                return;
            }

            _analyzerLastFallbackReason = sanitized;
            _analyzerLastFallbackLoggedAt = now;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            now,
            "warning",
            $"Essentia analysis failed; standard analysis was used: {sanitized}"));
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

        return candidates
            .Select(TryResolveExistingFilePath)
            .FirstOrDefault(resolvedCandidate => !string.IsNullOrWhiteSpace(resolvedCandidate));
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

        // Prefer the most complete manifest rather than the first directory that
        // happens to exist. A stale or partially provisioned data-directory copy
        // must not shadow a complete bundled one and silently downgrade the
        // acoustic genre model.
        return resolvedCandidates
            .OrderByDescending(CountSatisfiedModelFiles)
            .ThenByDescending(HasRequiredGenre519Models)
            .ThenByDescending(HasRequiredEnhancedModels)
            .FirstOrDefault();
    }

    private static int CountSatisfiedModelFiles(string modelsDirectory)
    {
        if (string.IsNullOrWhiteSpace(modelsDirectory) || !Directory.Exists(modelsDirectory))
        {
            return 0;
        }

        return RequiredModelFiles.Count(model => ModelFilePresent(Path.Join(modelsDirectory, model.FileName)));
    }

    private static bool ModelFilePresent(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private static bool HasRequiredEnhancedModels(string modelsDirectory)
    {
        if (string.IsNullOrWhiteSpace(modelsDirectory) || !Directory.Exists(modelsDirectory))
        {
            return false;
        }

        return RequiredEnhancedModelFiles.All(requiredModelFile => ModelFilePresent(Path.Join(modelsDirectory, requiredModelFile)));
    }

    private static bool HasRequiredGenre519Models(string modelsDirectory)
    {
        if (string.IsNullOrWhiteSpace(modelsDirectory) || !Directory.Exists(modelsDirectory))
        {
            return false;
        }

        return RequiredGenre519ModelFiles.All(requiredModelFile => ModelFilePresent(Path.Join(modelsDirectory, requiredModelFile)));
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

    internal sealed record MoodScores(
        double Happy,
        double Sad,
        double Relaxed,
        double Aggressive,
        double Party,
        double Acoustic,
        double Electronic);

    internal sealed record VibeGenreEvidenceDto(string Label, double Score, string Model);

    /// <summary>
    /// A Sonic Analysis vector as it arrives from the analyzer: little-endian
    /// float32 base64 plus the provenance describing how it was produced.
    /// </summary>
    internal sealed record SonicPayload(
        string ModelId,
        string ModelVersion,
        string EmbeddingVersion,
        int Dimensions,
        string Pooling,
        string Normalization,
        string DistanceMetric,
        int FrameCount,
        string VectorBase64);

    internal sealed record VibeSemantics(
        IReadOnlyList<string>? ResolvedGenres,
        IReadOnlyList<string>? ResolvedStyles,
        IReadOnlyList<string>? ResolvedMoods,
        string? SemanticEvidenceJson,
        string? GenreModel,
        string? ValenceSource,
        string? ArousalSource,
        string? EmbeddedSemanticFingerprint = null);

    internal sealed record AnalysisOutput(
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
        double? DynamicComplexity,
        IReadOnlyList<VibeGenreEvidenceDto>? EssentiaGenreEvidence,
        string? GenreModel,
        string? ValenceSource,
        string? ArousalSource,
        bool? AudioTruncated,
        // Named to match the analyzer's payload key exactly. The payload is
        // deserialized case-insensitively by property name, so renaming this
        // would silently stop the vector binding rather than fail to compile.
        SonicPayload? SonicEmbedding = null,
        bool? SonicUnavailable = null)
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
        IReadOnlyList<string>? LoadedPredictionHeads,
        string? GenreModel,
        bool? GenreModelLoaded,
        IReadOnlyList<string>? MissingGenreModelFiles);

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
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:a:0");
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("s16le");
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add(channels.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add(sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-vn");
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

/// <summary>Why a manual analysis run was accepted or declined.</summary>
public enum VibeAnalysisRunOutcome
{
    /// <summary>The run was accepted and queued.</summary>
    Queued,

    /// <summary>A pass is already running or pending, so a second run was declined.
    /// This is a healthy state, not a disabled feature.</summary>
    AlreadyRunning,

    /// <summary>Background analysis is switched off.</summary>
    Disabled
}

public static class VibeAnalysisRunOutcomeExtensions
{
    public static bool IsAccepted(this VibeAnalysisRunOutcome outcome)
        => outcome == VibeAnalysisRunOutcome.Queued;

    public static string Reason(this VibeAnalysisRunOutcome outcome)
        => outcome switch
        {
            VibeAnalysisRunOutcome.Queued => "Vibe analysis run queued.",
            VibeAnalysisRunOutcome.AlreadyRunning => "Vibe analysis is already running.",
            _ => "Background analysis is disabled."
        };
}

public readonly record struct VibeAnalysisRunRequest(VibeAnalysisRunOutcome Outcome, string Reason)
{
    public bool Queued => Outcome.IsAccepted();
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
    DateTimeOffset? UpdatedAtUtc,
    VibeAnalyzerWorkerSnapshot Analyzer);
