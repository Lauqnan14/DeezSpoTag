using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace DeezSpoTag.Web.Services;

public sealed partial class ArtistMetadataUpdaterService
{
    private const string SpotifyPlatform = "spotify";
    private const string DeezerPlatform = "deezer";
    private const string ApplePlatform = "apple";
    private const string TidalPlatform = "tidal";
    private const string QobuzPlatform = "qobuz";
    private const string LastFmPlatform = "lastfm";
    private const string MetadataSourceAuto = "auto";
    private const string MetadataSourceSpotify = SpotifyPlatform;
    private const string MetadataSourceDeezer = DeezerPlatform;
    private const string MetadataSourceApple = ApplePlatform;
    private const string MetadataSourceTidal = TidalPlatform;
    private const string MetadataSourceQobuz = QobuzPlatform;
    private const string MetadataSourceLastFm = LastFmPlatform;
    private const string PlexTarget = MediaServerTargetServices.Plex;
    private const string JellyfinTarget = MediaServerTargetServices.Jellyfin;
    private const string NavidromeTarget = MediaServerTargetServices.Navidrome;
    private const string LegacyBothTargets = "both";
    private const string AvatarSlot = "avatar";
    private const string BackgroundSlot = "background";
    private const string FileFingerprintPrefix = "f:";
    private const double VisualDuplicateMeanAbsDifference = 28d;

    private readonly LibraryRepository _libraryRepository;
    private readonly PlatformAuthService _platformAuthService;
    private readonly PlexApiClient _plexClient;
    private readonly JellyfinApiClient _jellyfinClient;
    private readonly NavidromeApiClient _navidromeClient;
    private readonly ArtistPopularSongsSyncService _artistPopularSongsSyncService;
    private readonly ArtistArtworkCatalogService _artistArtworkCatalog;
    private readonly LibraryConfigStore _configStore;
    private readonly ISettingsService _settingsService;
    private readonly IDownloadTagSettingsResolver _profileSettingsResolver;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ArtistMetadataUpdaterService> _logger;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private static readonly TimeSpan ArtistYield = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Bounds a single artist's server push so one stalled media server cannot hold the run open.
    /// Mirrors the cache refresh's per-artist bound.
    /// </summary>
    private static readonly TimeSpan ArtistPushTimeout = TimeSpan.FromMinutes(10);
    private readonly object _statusLock = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _statePath;
    private MetadataUpdaterStatusSnapshot _status = MetadataUpdaterStatusSnapshot.Idle();
    private Task? _activeRun;
    private CancellationTokenSource? _runCts;

    public ArtistMetadataUpdaterService(
        IServiceProvider serviceProvider,
        IWebHostEnvironment environment,
        ILogger<ArtistMetadataUpdaterService> logger)
    {
        _libraryRepository = serviceProvider.GetRequiredService<LibraryRepository>();
        _platformAuthService = serviceProvider.GetRequiredService<PlatformAuthService>();
        _plexClient = serviceProvider.GetRequiredService<PlexApiClient>();
        _jellyfinClient = serviceProvider.GetRequiredService<JellyfinApiClient>();
        _navidromeClient = serviceProvider.GetRequiredService<NavidromeApiClient>();
        _artistPopularSongsSyncService = serviceProvider.GetRequiredService<ArtistPopularSongsSyncService>();
        _artistArtworkCatalog = serviceProvider.GetRequiredService<ArtistArtworkCatalogService>();
        _configStore = serviceProvider.GetRequiredService<LibraryConfigStore>();
        _settingsService = serviceProvider.GetRequiredService<ISettingsService>();
        _profileSettingsResolver = serviceProvider.GetService<IDownloadTagSettingsResolver>()
            ?? new NullDownloadTagSettingsResolver();
        _environment = environment;
        _logger = logger;
        _statePath = Path.Join(
            AppDataPaths.GetDataRoot(environment),
            "library-artist-images",
            SpotifyPlatform,
            "metadata-updater-state.json");
    }

    public MetadataUpdaterStatusSnapshot GetStatus()
    {
        lock (_statusLock)
        {
            return _status;
        }
    }

    public bool Cancel()
    {
        CancellationTokenSource? cts;
        bool running;
        lock (_statusLock)
        {
            cts = _runCts;
            running = _status.Running || _activeRun is { IsCompleted: false };
        }

        if (cts is null)
        {
            return running;
        }

        try
        {
            if (!cts.IsCancellationRequested)
            {
                cts.Cancel();
            }

            return true;
        }
        catch (ObjectDisposedException)
        {
            return running;
        }
    }

    public async Task RegisterFromManualPushAsync(
        ManualPushRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return;
        }

        var artistId = request.ArtistId;
        var artistName = request.ArtistName;
        if (artistId <= 0 || string.IsNullOrWhiteSpace(artistName))
        {
            return;
        }

        var state = await LoadStateAsync(cancellationToken);
        var normalizedTargets = NormalizeTargets(request.Targets, request.Target);
        var normalizedInterval = NormalizeIntervalDays(request.IntervalDays ?? 30);
        var tracked = state.Artists.FirstOrDefault(item => item.ArtistId == artistId);
        if (tracked is null)
        {
            tracked = new MetadataUpdaterTrackedArtist
            {
                ArtistId = artistId
            };
            state.Artists.Add(tracked);
        }

        tracked.ArtistName = artistName.Trim();
        tracked.Target = ToLegacyTarget(normalizedTargets);
        tracked.Targets = normalizedTargets.ToList();
        tracked.IncludeAvatar = request.IncludeAvatar;
        tracked.IncludeBackground = request.IncludeBackground;
        tracked.IncludeBio = request.IncludeBio;
        tracked.IncludePopularSongs = request.IncludePopularSongs;
        tracked.IntervalDays = normalizedInterval;
        if (!string.IsNullOrWhiteSpace(request.Source))
        {
            tracked.Source = NormalizeMetadataSource(request.Source);
        }
        var nowUtc = DateTimeOffset.UtcNow;
        tracked.LastPushedAtUtc = nowUtc;
        tracked.UpdatedAtUtc = nowUtc;
        tracked.AvatarRotationIndex = 0;
        tracked.BackgroundRotationIndex = 0;
        await SaveStateAsync(state, cancellationToken);
    }

    public Task<bool> RunAndWaitAsync(
        MetadataUpdaterRunRequest request,
        bool isAutomatic,
        CancellationToken cancellationToken)
        => RunAndWaitAsync(request, isAutomatic, progress: null, resumedRun: null, outcomeSink: null, targetSink: null, cancellationToken);

    public async Task<bool> RunAndWaitAsync(
        MetadataUpdaterRunRequest request,
        bool isAutomatic,
        IProgress<ArtistMetadataOperationProgress>? progress,
        ArtistRunResumeContext? resumedRun,
        Action<ArtistRunOutcomeRecord>? outcomeSink,
        Action<IReadOnlyList<long>>? targetSink,
        CancellationToken cancellationToken)
    {
        Task run;
        CancellationTokenSource? runCts = null;
        CancellationTokenSource? linkedCts = null;
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            if (_activeRun is { IsCompleted: false })
            {
                return false;
            }

            runCts = new CancellationTokenSource();
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runCts.Token);
            lock (_statusLock)
            {
                _runCts = runCts;
            }

            run = RunInternalAsync(
                request ?? new MetadataUpdaterRunRequest(),
                isAutomatic,
                progress,
                resumedRun,
                outcomeSink,
                targetSink,
                linkedCts.Token);
            _activeRun = run;
        }
        finally
        {
            _runGate.Release();
        }

        try
        {
            await run;
            return !linkedCts.Token.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            lock (_statusLock)
            {
                if (runCts is not null && ReferenceEquals(_runCts, runCts))
                {
                    _runCts = null;
                }

                if (ReferenceEquals(_activeRun, run))
                {
                    _activeRun = null;
                }
            }

            linkedCts?.Dispose();
            runCts?.Dispose();
        }
    }

    private async Task RunInternalAsync(
        MetadataUpdaterRunRequest request,
        bool isAutomatic,
        IProgress<ArtistMetadataOperationProgress>? progress,
        ArtistRunResumeContext? resumedRun,
        Action<ArtistRunOutcomeRecord>? outcomeSink,
        Action<IReadOnlyList<long>>? targetSink,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        BeginRunStatus(isAutomatic, startedAtUtc);

        try
        {
            if (!TryEnsureLibraryConfigured())
            {
                return;
            }

            var auth = await _platformAuthService.LoadAsync();
            var runPreparation = await PrepareRunAsync(request, auth, resumedRun, cancellationToken);
            if (runPreparation is null)
            {
                return;
            }

            var state = runPreparation.State;
            var allCandidates = runPreparation.Candidates;

            // Publish the resolved artist set so a resume reuses exactly these targets instead of
            // re-deriving them, which for the missing-artwork mode means re-probing the live servers
            // and potentially selecting a different set than the recorded outcomes belong to.
            targetSink?.Invoke(allCandidates.Select(candidate => candidate.ArtistId).ToList());

            // Counters are rebuilt from the persisted outcomes of the interrupted run, so the bucket
            // totals always add up to Processed instead of being inferred from an id count.
            var counters = MetadataRunCounters.FromOutcomes(allCandidates.Count, resumedRun?.Outcomes);
            // Only terminal outcomes are skipped: successes, partial successes and not-due skips.
            // Failures and missing metadata stay in the retry set.
            var skipSet = resumedRun?.Outcomes is { Count: > 0 }
                ? ArtistRunOutcomes.BuildResumeSkipSet(resumedRun.Outcomes)
                : new HashSet<long>();
            var targetSummaries = new Dictionary<string, TargetAccumulator>(StringComparer.OrdinalIgnoreCase);

            foreach (var tracked in allCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (skipSet.Contains(tracked.ArtistId))
                {
                    continue;
                }

                UpdateProgressStatus(tracked.ArtistName, counters);

                var result = await ProcessTrackedArtistAsync(
                    tracked,
                    request,
                    auth,
                    runPreparation.NowUtc,
                    cancellationToken);
                var record = new ArtistRunOutcomeRecord(
                    tracked.ArtistId,
                    DescribeRunOutcome(result.Outcome),
                    result.Reason);
                counters.Apply(record);
                AccumulateTargetResults(targetSummaries, record.Targets);
                UpdateCounterStatus(counters);
                // Deliberately not cancellable: the artist's push record must survive an interrupt that
                // lands immediately after the push, otherwise a resumed run skips an unpushed artist.
                await SaveStateAsync(state, CancellationToken.None);
                // Reported synchronously so the durable checkpoint is not left lagging the work.
                outcomeSink?.Invoke(record);
                progress?.Report(new ArtistMetadataOperationProgress(
                    counters.ProcessedArtists,
                    counters.TotalArtists,
                    tracked.ArtistName,
                    tracked.ArtistId));
                await Task.Delay(ArtistYield, cancellationToken);
            }

            UpdateStatus(_status with
            {
                Running = false,
                CurrentArtist = null,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Phase = "Metadata update completed",
                Message = BuildCompletionMessage(counters),
                SkipReasons = counters.SkipReasonsSnapshot(),
                PartialArtists = counters.PartialArtists,
                NoMetadataArtists = counters.NoMetadataArtists,
                ResumedArtists = counters.ResumedArtists,
                Targets = BuildTargetSummaries(targetSummaries)
            });
        }
        catch (OperationCanceledException)
        {
            UpdateStatus(_status with
            {
                Running = false,
                CurrentArtist = null,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Phase = "Metadata update cancelled",
                Message = "Metadata updater was cancelled."
            });
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Metadata updater run failed.");
            UpdateStatus(_status with
            {
                Running = false,
                CurrentArtist = null,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Phase = "Metadata update failed",
                Message = ex.Message
            });
            throw;
        }
    }

    private void BeginRunStatus(bool isAutomatic, DateTimeOffset startedAtUtc)
    {
        UpdateStatus(_status with
        {
            Running = true,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = null,
            Phase = isAutomatic ? "Automatic metadata renewal started" : "Metadata update started",
            Message = null,
            ProcessedArtists = 0,
            SuccessfulArtists = 0,
            FailedArtists = 0,
            SkippedArtists = 0,
            SkipReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            TotalArtists = 0,
            CurrentArtist = null
        });
    }

    private bool TryEnsureLibraryConfigured()
    {
        if (_libraryRepository.IsConfigured)
        {
            return true;
        }

        UpdateStatus(_status with
        {
            Running = false,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Phase = "Metadata update failed",
            Message = "Library database is not configured."
        });
        return false;
    }

    private async Task<bool> PruneMissingTrackedArtistsAsync(
        MetadataUpdaterState state,
        CancellationToken cancellationToken)
    {
        if (state.Artists.Count == 0)
        {
            return false;
        }

        var existingIds = (await _libraryRepository.GetExistingArtistIdsAsync(cancellationToken)).ToHashSet();
        var before = state.Artists.Count;
        state.Artists.RemoveAll(tracked => !existingIds.Contains(tracked.ArtistId));
        return state.Artists.Count != before;
    }

    private async Task<PreparedRunState?> PrepareRunAsync(
        MetadataUpdaterRunRequest request,
        PlatformAuthState auth,
        ArtistRunResumeContext? resumedRun,
        CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken);
        IReadOnlySet<long>? missingArtistIds = null;
        if (resumedRun is { TargetArtistIds.Count: > 0 })
        {
            // A resumed run reuses the artist set resolved when it started. Re-deriving it would
            // re-probe the live servers and could produce a different set than the one the persisted
            // outcomes were recorded against. Artists that left the library are still filtered out.
            await SeedArtistsFromLibraryAsync(state, request, cancellationToken);
            if (await PruneMissingTrackedArtistsAsync(state, cancellationToken))
            {
                await SaveStateAsync(state, cancellationToken);
            }

            missingArtistIds = resumedRun.TargetArtistIds.ToHashSet();
        }
        else
        {
            missingArtistIds = request.MissingArtistArtworkOnly == true
                ? await SeedMissingArtistArtworkCandidatesAsync(state, request, auth, cancellationToken)
                : null;
            if (request.MissingArtistArtworkOnly != true)
            {
                // Always re-sync tracking with the current library so the run target is the
                // true artist count for the selected scope: new artists join, and the set
                // never shrinks below the library as rescans reassign ids.
                await SeedArtistsFromLibraryAsync(state, request, cancellationToken);

                // Tracked artists whose library rows are gone can never be updated; drop them so a
                // stale id cannot abort the run (the policy write enforces artist(id) as a FK).
                if (await PruneMissingTrackedArtistsAsync(state, cancellationToken))
                {
                    await SaveStateAsync(state, cancellationToken);
                }
            }

            // State written before the target set was frozen has no recorded scope, so the plan is
            // re-derived here once. In missing-artwork mode that plan excludes the artists this run
            // already pushed, because pushing artwork is exactly what makes them stop being "missing".
            // Union them back in so the reported total still covers the whole run; they are skipped by
            // the resume set rather than reprocessed, so this changes the denominator only.
            if (resumedRun?.Outcomes is { Count: > 0 } finished)
            {
                var scope = new HashSet<long>(missingArtistIds ?? state.Artists.Select(artist => artist.ArtistId));
                scope.UnionWith(finished.Select(outcome => outcome.ArtistId));
                missingArtistIds = scope;
            }
        }

        var allCandidates = BuildRunCandidates(state.Artists, request, missingArtistIds);
        UpdateStatus(_status with { TotalArtists = allCandidates.Count });
        if (allCandidates.Count == 0)
        {
            UpdateStatus(_status with
            {
                Running = false,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Phase = "Metadata update completed",
                Message = "No tracked artists available for metadata updater."
            });
            return null;
        }

        return new PreparedRunState(state, allCandidates, DateTimeOffset.UtcNow);
    }

    private static List<MetadataUpdaterTrackedArtist> BuildRunCandidates(
        IReadOnlyCollection<MetadataUpdaterTrackedArtist> artists,
        MetadataUpdaterRunRequest request,
        IReadOnlySet<long>? scopedArtistIds = null)
    {
        var candidates = artists
            .Where(artist => artist.ArtistId > 0 && !string.IsNullOrWhiteSpace(artist.ArtistName))
            .ToList();
        if (scopedArtistIds is not null)
        {
            candidates = candidates
                .Where(artist => scopedArtistIds.Contains(artist.ArtistId))
                .ToList();
        }
        if (!request.ArtistId.HasValue)
        {
            return candidates;
        }

        return candidates
            .Where(artist => artist.ArtistId == request.ArtistId.Value)
            .ToList();
    }

    private async Task<ArtistProcessingResult> ProcessTrackedArtistAsync(
        MetadataUpdaterTrackedArtist tracked,
        MetadataUpdaterRunRequest request,
        PlatformAuthState auth,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var effectiveIntervalDays = NormalizeIntervalDays(request.IntervalDays ?? tracked.IntervalDays);
        if (ShouldSkipTrackedArtist(tracked, request, effectiveIntervalDays, nowUtc))
        {
            tracked.IntervalDays = effectiveIntervalDays;
            return new ArtistProcessingResult(ArtistProcessingOutcome.SkippedNotDue, MetadataSkipReasons.NotDue);
        }

        ApplyRequestOverrides(tracked, request, effectiveIntervalDays);
        if (request.OcrTextArtBlockingEnabled.HasValue)
        {
            await _libraryRepository.SetArtistMetadataOcrTextArtBlockingAsync(
                tracked.ArtistId,
                request.OcrTextArtBlockingEnabled.Value,
                cancellationToken);
        }
        try
        {
            var outcome = await PushTrackedArtistMetadataWithTimeoutAsync(tracked, auth, request.FolderId, cancellationToken);
            return new ArtistProcessingResult(
                ClassifyProcessingOutcome(outcome),
                DescribePushSkipReason(outcome));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Metadata updater failed for artist {ArtistId}", tracked.ArtistId);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "error",
                $"Metadata updater failed for {tracked.ArtistName}: {ex.Message}"));
            return new ArtistProcessingResult(ArtistProcessingOutcome.Failed, null);
        }
    }

    /// <summary>An artist's run outcome plus the reason it was skipped, when it was.</summary>
    private readonly record struct ArtistProcessingResult(ArtistProcessingOutcome Outcome, string? Reason);

    /// <summary>
    /// Bounds one artist so a stalled media server cannot hold the whole run open. The parent token
    /// still wins: a shutdown or user cancel propagates rather than being reported as a timeout.
    /// </summary>
    private async Task<ArtistPushOutcome> PushTrackedArtistMetadataWithTimeoutAsync(
        MetadataUpdaterTrackedArtist tracked,
        PlatformAuthState auth,
        long? folderId,
        CancellationToken cancellationToken)
    {
        using var artistCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pushTask = PushTrackedArtistMetadataAsync(tracked, auth, folderId, artistCancellation.Token);
        try
        {
            return await pushTask.WaitAsync(ArtistPushTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            artistCancellation.Cancel();
            ObserveLatePushCompletion(pushTask);
            _logger.LogWarning(
                "Metadata updater timed out after {TimeoutSeconds}s for artist {ArtistId}.",
                ArtistPushTimeout.TotalSeconds,
                tracked.ArtistId);
            return ArtistPushOutcome.Failed;
        }
    }

    private static void ObserveLatePushCompletion(Task pushTask)
    {
        if (pushTask.IsCompleted)
        {
            return;
        }

        _ = pushTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static ArtistProcessingOutcome ClassifyProcessingOutcome(ArtistPushOutcome outcome)
        => outcome switch
        {
            ArtistPushOutcome.Succeeded => ArtistProcessingOutcome.Succeeded,
            ArtistPushOutcome.Partial => ArtistProcessingOutcome.Partial,
            ArtistPushOutcome.NoMetadata => ArtistProcessingOutcome.NoMetadata,
            ArtistPushOutcome.Failed => ArtistProcessingOutcome.Failed,
            ArtistPushOutcome.ScanOnly => ArtistProcessingOutcome.SkippedOther,
            ArtistPushOutcome.SyncBlocked => ArtistProcessingOutcome.SkippedOther,
            ArtistPushOutcome.ArtistRowMissing => ArtistProcessingOutcome.SkippedOther,
            _ => ArtistProcessingOutcome.SkippedOther
        };

    private static bool ShouldSkipTrackedArtist(
        MetadataUpdaterTrackedArtist tracked,
        MetadataUpdaterRunRequest request,
        int effectiveIntervalDays,
        DateTimeOffset nowUtc)
    {
        if (request.Force == true)
        {
            return false;
        }

        if (request.MissingArtistArtworkOnly == true)
        {
            return false;
        }

        return !IsTrackedArtistDue(tracked, effectiveIntervalDays, nowUtc);
    }

    private static bool IsTrackedArtistDue(
        MetadataUpdaterTrackedArtist tracked,
        int intervalDays,
        DateTimeOffset nowUtc)
    {
        if (intervalDays <= 0)
        {
            return false;
        }

        var baseline = ResolveScheduleBaselineUtc(tracked);
        if (!baseline.HasValue)
        {
            return true;
        }

        return nowUtc - baseline.Value >= TimeSpan.FromDays(intervalDays);
    }

    private static DateTimeOffset? ResolveScheduleBaselineUtc(MetadataUpdaterTrackedArtist tracked)
    {
        if (tracked.LastPushedAtUtc.HasValue)
        {
            return tracked.LastPushedAtUtc.Value;
        }

        return tracked.UpdatedAtUtc == default ? null : tracked.UpdatedAtUtc;
    }

    private static void ApplyRequestOverrides(
        MetadataUpdaterTrackedArtist tracked,
        MetadataUpdaterRunRequest request,
        int effectiveIntervalDays)
    {
        if (!string.IsNullOrWhiteSpace(request.Source))
        {
            tracked.Source = NormalizeMetadataSource(request.Source);
        }

        if (!string.IsNullOrWhiteSpace(request.Target))
        {
            tracked.Targets = NormalizeTargets(request.Targets, request.Target).ToList();
            tracked.Target = ToLegacyTarget(tracked.Targets);
        }
        else if (request.Targets is { Count: > 0 })
        {
            tracked.Targets = NormalizeTargets(request.Targets, request.Target).ToList();
            tracked.Target = ToLegacyTarget(tracked.Targets);
        }

        tracked.IntervalDays = effectiveIntervalDays;
        if (request.IncludeAvatar.HasValue)
        {
            tracked.IncludeAvatar = request.IncludeAvatar.Value;
        }

        if (request.IncludeBackground.HasValue)
        {
            tracked.IncludeBackground = request.IncludeBackground.Value;
        }

        if (request.IncludeBio.HasValue)
        {
            tracked.IncludeBio = request.IncludeBio.Value;
        }

        if (request.IncludePopularSongs.HasValue)
        {
            tracked.IncludePopularSongs = request.IncludePopularSongs.Value;
        }

        if (request.OcrTextArtBlockingEnabled.HasValue)
        {
            tracked.OcrTextArtBlockingEnabled = request.OcrTextArtBlockingEnabled.Value;
        }

        if (request.SaveArtistFolderImage.HasValue)
        {
            tracked.SaveArtistFolderImage = request.SaveArtistFolderImage.Value;
        }
    }

    private async Task<ArtistPushOutcome> PushTrackedArtistMetadataAsync(
        MetadataUpdaterTrackedArtist tracked,
        PlatformAuthState auth,
        long? folderId,
        CancellationToken cancellationToken)
    {
        var artist = await _libraryRepository.GetArtistAsync(tracked.ArtistId, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                $"Metadata updater skipped artist {tracked.ArtistId}: artist missing."));
            return ArtistPushOutcome.ArtistRowMissing;
        }

        tracked.ArtistName = artist.Name;
        var policy = await _libraryRepository.GetArtistMetadataPolicyAsync(artist.Id, cancellationToken);
        var popularSongsSynced = await SyncPopularSongsIfRequestedAsync(
            tracked,
            artist.Id,
            artist.Name,
            cancellationToken);
        var source = NormalizeMetadataSource(tracked.Source);
        await _artistArtworkCatalog.RefreshAsync(
            artist.Id,
            artist.Name,
            artist.PreferredImagePath,
            cancellationToken,
            onlyProvider: source == MetadataSourceAuto ? null : source,
            forceProviderRefresh: false,
            includeGallery: true,
            allowArtistPageScrape: false);
        var resolved = await ResolveArtistMetadataAsync(
            artist.Id,
            artist.Name,
            source,
            tracked.IncludeBio,
            tracked.OcrTextArtBlockingEnabled,
            cancellationToken);
        if (resolved is null)
        {
            // No upstream metadata is a normal condition for some artists, not a failure.
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                popularSongsSynced
                    ? $"Metadata updater found no {source} metadata for {artist.Name}; synced popular songs only."
                    : $"Metadata updater found no {source} metadata for {artist.Name}."));
            return ArtistPushOutcome.NoMetadata;
        }

        var prepared = await PrepareVisualsAsync(
            tracked,
            resolved.Candidates,
            cancellationToken,
            preferExistingSlots: !tracked.OcrTextArtBlockingEnabled);
        await UpdateManagedArtistVisualsAsync(artist.Id, prepared, cancellationToken);

        if (policy.SyncBlocked)
        {
            // No server was contacted, so this must not be reported as an update.
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Metadata updater skipped server sync for {artist.Name} because artist sync is blocked."));
            tracked.UpdatedAtUtc = DateTimeOffset.UtcNow;
            return ArtistPushOutcome.SyncBlocked;
        }

        if (tracked.SaveArtistFolderImage
            && tracked.IncludeAvatar
            && !string.IsNullOrWhiteSpace(prepared.AvatarPath))
        {
            await SaveAvatarIntoArtistFoldersAsync(
                artist.Id,
                artist.Name,
                prepared.AvatarPath!,
                folderId,
                cancellationToken);
        }

        var biography = tracked.IncludeBio
            ? SanitizeBiography(resolved.Biography)
            : null;
        var pushed = await PushArtistMetadataAsync(
            new PushMetadataRequest(
                artist.Id,
                auth,
                artist.Name,
                ResolveTrackedTargets(tracked),
                tracked.IncludeAvatar ? prepared.AvatarPath : null,
                tracked.IncludeBackground ? prepared.BackgroundPath : null,
                biography),
            cancellationToken);

        var outcome = ClassifyArtistPushOutcome(pushed, popularSongsSynced);
        if (outcome is not (ArtistPushOutcome.Succeeded or ArtistPushOutcome.Partial))
        {
            var warningText = DescribePushShortfall(pushed)
                ?? "No server metadata was updated.";
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                $"Metadata updater could not push {artist.Name}: {warningText}"));
            return outcome;
        }

        tracked.LastPushedAtUtc = DateTimeOffset.UtcNow;
        tracked.UpdatedAtUtc = DateTimeOffset.UtcNow;
        tracked.AvatarRotationIndex = prepared.NextAvatarIndex;
        tracked.BackgroundRotationIndex = prepared.NextBackgroundIndex;
        foreach (var target in ResolveTrackedTargets(tracked))
        {
            var targetResult = pushed.Targets.FirstOrDefault(candidate =>
                string.Equals(candidate.Target, target, StringComparison.OrdinalIgnoreCase));
            // Biography is only recorded as synced for targets that can actually store it, so
            // Navidrome's read-only biography no longer needs a warning-text match to be excluded.
            var biographySyncUtc = targetResult?.LimitationList.Any(limitation =>
                limitation.Contains("biography is read-only", StringComparison.OrdinalIgnoreCase)) == true
                ? (DateTimeOffset?)null
                : DateTimeOffset.UtcNow;
            await _libraryRepository.UpsertArtistServerSyncStateAsync(
                new ArtistServerSyncStateUpsertInput(
                    artist.Id,
                    target,
                    DateTimeOffset.UtcNow,
                    biographySyncUtc,
                    ComputeFileHashOrNull(prepared.AvatarPath),
                    ComputeFileHashOrNull(prepared.BackgroundPath),
                    ComputeTextHashOrNull(biography),
                    tracked.AvatarRotationIndex,
                    tracked.BackgroundRotationIndex,
                    DescribeTargetSyncResult(targetResult),
                    targetResult?.Error),
                cancellationToken);
        }
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            popularSongsSynced
                ? $"Metadata updater pushed {artist.Name} and synced popular songs to {string.Join(", ", ResolveTrackedTargets(tracked))}."
                : $"Metadata updater pushed {artist.Name} to {string.Join(", ", ResolveTrackedTargets(tracked))}."));
        return outcome;
    }

    /// <summary>
    /// Turns a per-target push into an artist-level outcome. A declared capability limit (Navidrome's
    /// read-only biography) never degrades the result, and a rescan on its own is not an update.
    /// </summary>
    private static ArtistPushOutcome ClassifyArtistPushOutcome(PushOutcome pushed, bool popularSongsSynced)
    {
        if (pushed.Updated)
        {
            return pushed.HasFailures ? ArtistPushOutcome.Partial : ArtistPushOutcome.Succeeded;
        }

        if (pushed.ScanOnly)
        {
            return ArtistPushOutcome.ScanOnly;
        }

        if (pushed.HasFailures)
        {
            return ArtistPushOutcome.Failed;
        }

        return popularSongsSynced ? ArtistPushOutcome.NoMetadata : ArtistPushOutcome.Unchanged;
    }

    private static string? DescribePushShortfall(PushOutcome pushed)
    {
        var text = pushed.Targets
            .Where(static target => target.Error is not null)
            .Select(static target => target.Error!)
            .ToList();
        text.AddRange(pushed.Warnings);
        return text.Count == 0 ? null : string.Join(" ", text);
    }

    private static string DescribeTargetSyncResult(ArtistTargetResult? target) => target?.Outcome switch
    {
        ArtistTargetOutcome.Updated => "updated",
        ArtistTargetOutcome.NotFound => "not-found",
        ArtistTargetOutcome.NotConfigured => "not-configured",
        ArtistTargetOutcome.Failed => "failed",
        _ => "skipped"
    };

    private async Task<bool> SyncPopularSongsIfRequestedAsync(
        MetadataUpdaterTrackedArtist tracked,
        long artistId,
        string artistName,
        CancellationToken cancellationToken)
    {
        if (!tracked.IncludePopularSongs)
        {
            return false;
        }

        var result = await _artistPopularSongsSyncService.SyncAsync(
            artistId,
            artistName,
            ResolveTrackedTargets(tracked),
            cancellationToken);
        if (!result.Success)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                $"Popular songs sync failed for {artistName}: {result.Message}"));
        }

        return result.Success;
    }

    private async Task UpdateManagedArtistVisualsAsync(
        long artistId,
        PreparedVisuals prepared,
        CancellationToken cancellationToken)
    {
        var linkedArtistIds = await ResolveLinkedArtistIdsAsync(artistId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(prepared.AvatarPath))
        {
            foreach (var linkedArtistId in linkedArtistIds)
            {
                await _libraryRepository.UpdateArtistImagePathAsync(linkedArtistId, prepared.AvatarPath!, cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(prepared.BackgroundPath))
        {
            foreach (var linkedArtistId in linkedArtistIds)
            {
                await _libraryRepository.UpdateArtistBackgroundPathAsync(linkedArtistId, prepared.BackgroundPath!, cancellationToken);
            }
        }
    }

    private async Task SaveAvatarIntoArtistFoldersAsync(
        long artistId,
        string artistName,
        string avatarPath,
        long? folderId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(avatarPath) || !File.Exists(avatarPath))
        {
            _logger.LogWarning(
                "Artist-folder image save skipped because the prepared avatar is missing. artist={ArtistId} artistName={ArtistName}",
                artistId,
                artistName);
            return;
        }

        var directories = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var linkedArtistId in await ResolveLinkedArtistIdsAsync(artistId, cancellationToken))
        {
            foreach (var (directory, destinationFolderId) in await ResolveArtistDirectoriesAsync(linkedArtistId, folderId, cancellationToken))
            {
                directories[directory] = destinationFolderId;
            }
        }

        if (directories.Count == 0)
        {
            _logger.LogWarning(
                "Artist-folder image save skipped because no local artist folders could be resolved from the library. artist={ArtistId} artistName={ArtistName} folderId={FolderId}",
                artistId,
                artistName,
                folderId);
            return;
        }

        foreach (var (directory, destinationFolderId) in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = await ResolveArtistFolderImageSettingsAsync(destinationFolderId, cancellationToken);
            if (settings is null)
            {
                continue;
            }

            var template = string.IsNullOrWhiteSpace(settings.ArtistImageTemplate)
                ? "folder"
                : settings.ArtistImageTemplate.Trim();
            var stem = PathTemplateGenerator.GenerateArtistName(
                template,
                new DeezSpoTag.Core.Models.Artist(artistId, artistName),
                settings,
                rootArtist: null);
            stem = Path.GetFileName(stem.Replace('\\', '/').Trim('/'));
            if (string.IsNullOrWhiteSpace(stem))
            {
                stem = "folder";
            }

            var formats = AppleQueueHelpers.GetArtworkOutputFormats(settings);
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var format in formats)
                {
                    var destination = Path.Join(directory, $"{stem}.{format}");
                    await WriteArtistFolderImageAsync(avatarPath, destination, format, settings, cancellationToken);
                    written.Add(Path.GetFullPath(destination));
                }

                DeleteArtistFolderImageVariants(directory, stem, written);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to write artist-folder image. artist={ArtistId} directory={Directory} template={Template}",
                    artistId,
                    directory,
                    template);
            }
        }
    }

    private async Task<DeezSpoTagSettings?> ResolveArtistFolderImageSettingsAsync(
        long destinationFolderId,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            if (destinationFolderId <= 0)
            {
                return settings;
            }

            var profile = await _profileSettingsResolver.ResolveProfileAsync(destinationFolderId, cancellationToken);
            if (profile is not null)
            {
                DownloadEngineSettingsHelper.ApplyResolvedProfileToSettings(settings, profile);
            }

            return settings;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not load artist-artwork template settings for folder {FolderId}.", destinationFolderId);
            return null;
        }
    }

    private static async Task WriteArtistFolderImageAsync(
        string avatarPath,
        string destination,
        string format,
        DeezSpoTagSettings settings,
        CancellationToken cancellationToken)
    {
        var sourceExtension = ImageFileExtensionResolver.NormalizeStandardImageExtension(Path.GetExtension(avatarPath));
        var wantsJpeg = string.Equals(format, "jpg", StringComparison.OrdinalIgnoreCase);
        if (wantsJpeg && sourceExtension == ".jpg")
        {
            await using var sourceStream = File.Open(avatarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var destinationStream = File.Create(destination);
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
            return;
        }

        if (!wantsJpeg && sourceExtension == ".png")
        {
            await using var sourceStream = File.Open(avatarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var destinationStream = File.Create(destination);
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
            return;
        }

        await using (var sourceStream = File.Open(avatarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var image = await Image.LoadAsync(sourceStream, cancellationToken))
        {
            if (wantsJpeg)
            {
                await image.SaveAsJpegAsync(
                    destination,
                    new JpegEncoder { Quality = Math.Clamp(settings.JpegImageQuality, 1, 100) },
                    cancellationToken);
                return;
            }

            await image.SaveAsPngAsync(destination, new PngEncoder(), cancellationToken);
        }
    }

    private async Task<IReadOnlyCollection<(string Directory, long FolderId)>> ResolveArtistDirectoriesAsync(
        long artistId,
        long? folderId,
        CancellationToken cancellationToken)
    {
        var paths = await _libraryRepository.GetArtistLocalAudioPathsAsync(artistId, cancellationToken, folderId);
        var directories = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in paths)
        {
            if (string.IsNullOrWhiteSpace(item.FilePath) || string.IsNullOrWhiteSpace(item.RootPath))
            {
                continue;
            }

            string fullPath;
            string rootPath;
            try
            {
                fullPath = Path.GetFullPath(item.FilePath);
                rootPath = Path.GetFullPath(item.RootPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(rootPath, fullPath);
            var firstSegment = relative
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstSegment)
                || firstSegment is "." or "..")
            {
                continue;
            }

            var artistDirectory = Path.Join(rootPath, firstSegment);
            if (Directory.Exists(artistDirectory))
            {
                directories[artistDirectory] = item.FolderId;
            }
        }

        return directories.Select(pair => (pair.Key, pair.Value)).ToList();
    }

    private static void DeleteArtistFolderImageVariants(
        string directory,
        string stem,
        IReadOnlySet<string> keepPaths)
    {
        if (!Directory.Exists(directory) || string.IsNullOrWhiteSpace(stem))
        {
            return;
        }

        foreach (var path in Directory.GetFiles(directory, stem + ".*", SearchOption.TopDirectoryOnly))
        {
            if (keepPaths.Contains(Path.GetFullPath(path)))
            {
                continue;
            }

            TryDeleteBestEffort(path);
        }
    }

    private async Task<IReadOnlyCollection<long>> ResolveLinkedArtistIdsAsync(long artistId, CancellationToken cancellationToken)
    {
        var artistIds = new HashSet<long> { artistId };
        foreach (var source in new[] { SpotifyPlatform, DeezerPlatform, ApplePlatform, TidalPlatform, QobuzPlatform })
        {
            var sourceId = await _libraryRepository.GetArtistSourceIdAsync(artistId, source, cancellationToken);
            if (string.IsNullOrWhiteSpace(sourceId))
            {
                continue;
            }

            var linkedIds = await _libraryRepository.GetArtistIdsBySourceIdAsync(source, sourceId, cancellationToken);
            foreach (var linkedId in linkedIds)
            {
                artistIds.Add(linkedId);
            }
        }

        return artistIds;
    }

    private void UpdateProgressStatus(string artistName, MetadataRunCounters counters)
    {
        UpdateStatus(_status with
        {
            ProcessedArtists = counters.ProcessedArtists,
            CurrentArtist = artistName,
            Phase = "Updating artists"
        });
    }

    private void UpdateCounterStatus(MetadataRunCounters counters)
    {
        UpdateStatus(_status with
        {
            SuccessfulArtists = counters.SuccessfulArtists,
            FailedArtists = counters.FailedArtists,
            SkippedArtists = counters.SkippedArtists,
            SkipReasons = counters.SkipReasonsSnapshot()
        });
    }

    private static string DescribeRunOutcome(ArtistProcessingOutcome outcome)
        => outcome switch
        {
            ArtistProcessingOutcome.Succeeded => ArtistRunOutcomes.Succeeded,
            ArtistProcessingOutcome.Partial => ArtistRunOutcomes.Partial,
            ArtistProcessingOutcome.NoMetadata => ArtistRunOutcomes.NoMetadata,
            ArtistProcessingOutcome.Failed => ArtistRunOutcomes.Failed,
            _ => ArtistRunOutcomes.Skipped
        };

    internal sealed class TargetAccumulator
    {
        public int Updated { get; set; }
        public int Unchanged { get; set; }
        public int NotFound { get; set; }
        public int NotConfigured { get; set; }
        public int Failed { get; set; }
        public SortedSet<string> Limitations { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static void AccumulateTargetResults(
        Dictionary<string, TargetAccumulator> summaries,
        IReadOnlyList<ArtistTargetResult>? results)
    {
        if (results is null)
        {
            return;
        }

        foreach (var result in results)
        {
            if (string.IsNullOrWhiteSpace(result.Target))
            {
                continue;
            }

            if (!summaries.TryGetValue(result.Target, out var accumulator))
            {
                accumulator = new TargetAccumulator();
                summaries[result.Target] = accumulator;
            }

            switch (result.Outcome)
            {
                case ArtistTargetOutcome.Updated:
                    accumulator.Updated++;
                    break;
                case ArtistTargetOutcome.Unchanged:
                    accumulator.Unchanged++;
                    break;
                case ArtistTargetOutcome.NotFound:
                    accumulator.NotFound++;
                    break;
                case ArtistTargetOutcome.NotConfigured:
                    accumulator.NotConfigured++;
                    break;
                case ArtistTargetOutcome.Failed:
                    accumulator.Failed++;
                    break;
            }

            foreach (var limitation in result.LimitationList)
            {
                accumulator.Limitations.Add(limitation);
            }
        }
    }

    /// <summary>Declared write capability per target, so the UI can explain a low update count.</summary>
    internal static ArtistTargetCapability ResolveTargetCapability(string target)
        => string.Equals(target, NavidromeTarget, StringComparison.OrdinalIgnoreCase)
            ? ArtistTargetCapability.ArtworkOnly
            : ArtistTargetCapability.Full;

    internal static IReadOnlyList<ArtistTargetRunSummary> BuildTargetSummaries(
        IReadOnlyDictionary<string, TargetAccumulator> summaries)
        => summaries
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new ArtistTargetRunSummary(
                pair.Key,
                ResolveTargetCapability(pair.Key),
                pair.Value.Updated,
                pair.Value.Unchanged,
                pair.Value.NotFound,
                pair.Value.NotConfigured,
                pair.Value.Failed,
                pair.Value.Limitations.ToList()))
            .ToList();

    private static string BuildCompletionMessage(MetadataRunCounters counters)
    {
        var skippedSuffix = counters.SkippedArtists > 0 && counters.SkipReasons.Count > 0
            ? $": {string.Join(", ", counters.SkipReasons.Select(pair => $"{pair.Value} {FormatSkipReason(pair.Key)}"))}"
            : string.Empty;
        var carriedOver = counters.ResumedArtists > 0
            ? $" ({counters.ResumedArtists} carried over from an interrupted run)"
            : string.Empty;
        return $"Processed {counters.ProcessedArtists} of {counters.TotalArtists} artists{carriedOver}: "
            + $"{counters.SuccessfulArtists} updated, {counters.PartialArtists} partial, "
            + $"{counters.NoMetadataArtists} without upstream metadata, {counters.FailedArtists} failed, "
            + $"{counters.SkippedArtists} skipped{skippedSuffix}.";
    }

    private static string FormatSkipReason(string reason)
        => reason switch
        {
            MetadataSkipReasons.NotDue => "not due",
            _ => reason
        };

    private async Task SeedArtistsFromLibraryAsync(
        MetadataUpdaterState state,
        MetadataUpdaterRunRequest request,
        CancellationToken cancellationToken)
    {
        var artists = await _libraryRepository.GetArtistsAsync("all", request.FolderId, cancellationToken);
        SeedTrackedArtists(state, request, artists);
    }

    private async Task<IReadOnlySet<long>> SeedMissingArtistArtworkCandidatesAsync(
        MetadataUpdaterState state,
        MetadataUpdaterRunRequest request,
        PlatformAuthState auth,
        CancellationToken cancellationToken)
    {
        var plan = await BuildMissingArtistArtworkPlanAsync(request, auth, cancellationToken);
        var scopedIds = plan.ArtistIds;
        var artists = (await _libraryRepository.GetArtistsAsync("all", request.FolderId, cancellationToken))
            .Where(artist => scopedIds.Contains(artist.Id))
            .ToList();
        SeedTrackedArtists(state, request, artists);
        await SaveStateAsync(state, cancellationToken);
        var counts = string.Join(", ", plan.MissingCounts.Select(pair => $"{pair.Key} {pair.Value}"));
        var message = $"Missing artist art driver: {plan.DriverTarget}. Missing counts: {counts}. Selected candidates: {scopedIds.Count}.";
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, "info", message));
        if (plan.Warnings.Count > 0)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                $"Missing artist art planning warnings: {string.Join(" ", plan.Warnings)}"));
        }

        UpdateStatus(_status with { Message = message });
        return scopedIds;
    }

    private async Task<MissingArtistArtworkPlan> BuildMissingArtistArtworkPlanAsync(
        MetadataUpdaterRunRequest request,
        PlatformAuthState auth,
        CancellationToken cancellationToken)
    {
        var artists = await _libraryRepository.GetArtistsAsync("all", request.FolderId, cancellationToken);
        var scopedArtists = artists
            .Where(static artist => artist.Id > 0 && !string.IsNullOrWhiteSpace(artist.Name))
            .ToList();
        var selectedTargets = NormalizeTargets(request.Targets, request.Target);
        var warnings = new List<string>();
        var missingByTarget = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in selectedTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            missingByTarget[target] = target switch
            {
                PlexTarget => await FindPlexMissingArtistArtworkAsync(scopedArtists, auth.Plex, warnings, cancellationToken),
                JellyfinTarget => await FindJellyfinMissingArtistArtworkAsync(scopedArtists, auth.Jellyfin, warnings, cancellationToken),
                NavidromeTarget => await FindNavidromeMissingArtistArtworkAsync(scopedArtists, auth.Navidrome, warnings, cancellationToken),
                _ => new HashSet<long>()
            };
        }

        var driverTarget = selectedTargets
            .Select((target, index) => new { Target = target, Index = index })
            .OrderByDescending(item => missingByTarget.TryGetValue(item.Target, out var ids) ? ids.Count : 0)
            .ThenBy(item => item.Index)
            .Select(item => item.Target)
            .FirstOrDefault() ?? PlexTarget;
        var driverIds = missingByTarget.TryGetValue(driverTarget, out var selectedIds)
            ? selectedIds
            : new HashSet<long>();

        // Target servers report a placeholder as "has artwork", so a stored provider placeholder
        // would never be revisited. Fold those artists in so the run replaces them.
        var placeholderIds = await FindArtistsWithPlaceholderArtworkAsync(scopedArtists, cancellationToken);
        if (placeholderIds.Count > 0)
        {
            driverIds.UnionWith(placeholderIds);
            warnings.Add($"Included {placeholderIds.Count} artist(s) whose stored artwork is a provider placeholder.");
        }

        var counts = missingByTarget.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Count,
            StringComparer.OrdinalIgnoreCase);
        counts["placeholder"] = placeholderIds.Count;
        return new MissingArtistArtworkPlan(driverTarget, counts, driverIds, warnings);
    }

    private async Task<HashSet<long>> FindArtistsWithPlaceholderArtworkAsync(
        IReadOnlyList<ArtistDto> scopedArtists,
        CancellationToken cancellationToken)
    {
        var placeholders = new HashSet<long>();
        try
        {
            var scoped = scopedArtists.Select(static artist => artist.Id).ToHashSet();
            var stored = await _libraryRepository.GetArtistArtworkOriginalUrlsAsync("artist", cancellationToken);
            foreach (var (artistId, originalUrl) in stored)
            {
                if (scoped.Contains(artistId)
                    && !DeezSpoTag.Services.Download.Utils.DeezerImageUrlValidator.IsAllowedDeezerImageUrl(originalUrl))
                {
                    placeholders.Add(artistId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Placeholder artist artwork audit failed.");
        }

        return placeholders;
    }

    private async Task<HashSet<long>> FindPlexMissingArtistArtworkAsync(
        IReadOnlyList<ArtistDto> artists,
        PlexAuth? plex,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var missing = new HashSet<long>();
        if (!TryGetPlexConnection(plex, out var plexUrl, out var plexToken))
        {
            warnings.Add("Plex missing-art audit skipped because Plex is not configured.");
            return missing;
        }

        foreach (var artist in artists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var locations = await _plexClient.FindArtistLocationsAsync(plexUrl, plexToken, artist.Name, cancellationToken);
                if (locations.Count == 0)
                {
                    missing.Add(artist.Id);
                    continue;
                }

                var hasArtwork = false;
                foreach (var location in locations)
                {
                    var metadata = await _plexClient.GetArtistMetadataAsync(plexUrl, plexToken, location.RatingKey, cancellationToken);
                    hasArtwork = hasArtwork || !string.IsNullOrWhiteSpace(metadata?.Thumb);
                    if (hasArtwork)
                    {
                        break;
                    }
                }

                if (!hasArtwork)
                {
                    missing.Add(artist.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Plex missing-art audit failed for artist {ArtistId}", artist.Id);
                warnings.Add($"Plex missing-art audit failed for {artist.Name}.");
            }
        }

        return missing;
    }

    private async Task<HashSet<long>> FindJellyfinMissingArtistArtworkAsync(
        IReadOnlyList<ArtistDto> artists,
        JellyfinAuth? jellyfin,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var missing = new HashSet<long>();
        if (jellyfin is null
            || string.IsNullOrWhiteSpace(jellyfin.Url)
            || string.IsNullOrWhiteSpace(jellyfin.ApiKey)
            || string.IsNullOrWhiteSpace(jellyfin.UserId))
        {
            warnings.Add("Jellyfin missing-art audit skipped because Jellyfin is not configured.");
            return missing;
        }

        foreach (var artist in artists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var artistIds = await _jellyfinClient.FindArtistIdsAsync(jellyfin.Url, jellyfin.ApiKey, artist.Name, cancellationToken);
                if (artistIds.Count == 0)
                {
                    missing.Add(artist.Id);
                    continue;
                }

                var hasArtwork = false;
                foreach (var artistId in artistIds)
                {
                    var item = await _jellyfinClient.GetItemAsync(
                        jellyfin.Url,
                        jellyfin.ApiKey,
                        jellyfin.UserId,
                        artistId,
                        cancellationToken);
                    hasArtwork = hasArtwork || item?.ImageTags?.ContainsKey("Primary") == true;
                    if (hasArtwork)
                    {
                        break;
                    }
                }

                if (!hasArtwork)
                {
                    missing.Add(artist.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Jellyfin missing-art audit failed for artist {ArtistId}", artist.Id);
                warnings.Add($"Jellyfin missing-art audit failed for {artist.Name}.");
            }
        }

        return missing;
    }

    private async Task<HashSet<long>> FindNavidromeMissingArtistArtworkAsync(
        IReadOnlyList<ArtistDto> artists,
        NavidromeAuth? navidrome,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var missing = new HashSet<long>();
        if (navidrome is null
            || string.IsNullOrWhiteSpace(navidrome.Url)
            || string.IsNullOrWhiteSpace(navidrome.Username)
            || string.IsNullOrWhiteSpace(navidrome.Password))
        {
            warnings.Add("Navidrome missing-art audit skipped because Navidrome is not configured.");
            return missing;
        }

        foreach (var artist in artists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var matches = await _navidromeClient.SearchArtistsAsync(
                    navidrome.Url,
                    navidrome.Username,
                    navidrome.Password,
                    artist.Name,
                    cancellationToken);
                var match = matches.FirstOrDefault(candidate => string.Equals(candidate.Name.Trim(), artist.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? matches.FirstOrDefault();
                if (match is null)
                {
                    missing.Add(artist.Id);
                    continue;
                }

                var hasArtwork = !string.IsNullOrWhiteSpace(match.CoverArt);
                if (!hasArtwork)
                {
                    var info = await _navidromeClient.GetArtistInfoAsync(
                        navidrome.Url,
                        navidrome.Username,
                        navidrome.Password,
                        match.Id,
                        cancellationToken);
                    hasArtwork = !string.IsNullOrWhiteSpace(info?.SmallImageUrl)
                        || !string.IsNullOrWhiteSpace(info?.MediumImageUrl)
                        || !string.IsNullOrWhiteSpace(info?.LargeImageUrl);
                }

                if (!hasArtwork)
                {
                    missing.Add(artist.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Navidrome missing-art audit failed for artist {ArtistId}", artist.Id);
                warnings.Add($"Navidrome missing-art audit failed for {artist.Name}.");
            }
        }

        return missing;
    }

    private static void SeedTrackedArtists(
        MetadataUpdaterState state,
        MetadataUpdaterRunRequest request,
        IReadOnlyList<ArtistDto> artists)
    {
        var targets = NormalizeTargets(request.Targets, request.Target);
        var target = ToLegacyTarget(targets);
        var hasSourceOverride = !string.IsNullOrWhiteSpace(request.Source);
        var source = NormalizeMetadataSource(request.Source);
        var intervalDays = NormalizeIntervalDays(request.IntervalDays ?? 30);
        var byId = state.Artists.ToDictionary(item => item.ArtistId);
        foreach (var artist in artists)
        {
            if (artist.Id <= 0 || string.IsNullOrWhiteSpace(artist.Name))
            {
                continue;
            }

            if (!byId.TryGetValue(artist.Id, out var tracked))
            {
                tracked = new MetadataUpdaterTrackedArtist
                {
                    ArtistId = artist.Id
                };
                state.Artists.Add(tracked);
                byId[artist.Id] = tracked;
            }

            tracked.ArtistName = artist.Name.Trim();
            if (hasSourceOverride || string.IsNullOrWhiteSpace(tracked.Source))
            {
                tracked.Source = source;
            }
            tracked.Target = target;
            tracked.Targets = targets.ToList();
            tracked.IntervalDays = intervalDays;
            if (request.IncludeAvatar.HasValue)
            {
                tracked.IncludeAvatar = request.IncludeAvatar.Value;
            }
            if (request.IncludeBackground.HasValue)
            {
                tracked.IncludeBackground = request.IncludeBackground.Value;
            }
            if (request.IncludeBio.HasValue)
            {
                tracked.IncludeBio = request.IncludeBio.Value;
            }
            if (request.IncludePopularSongs.HasValue)
            {
                tracked.IncludePopularSongs = request.IncludePopularSongs.Value;
            }
            if (request.OcrTextArtBlockingEnabled.HasValue)
            {
                tracked.OcrTextArtBlockingEnabled = request.OcrTextArtBlockingEnabled.Value;
            }
            if (request.SaveArtistFolderImage.HasValue)
            {
                tracked.SaveArtistFolderImage = request.SaveArtistFolderImage.Value;
            }
            tracked.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private async Task<ResolvedArtistMetadata?> ResolveArtistMetadataAsync(
        long artistId,
        string artistName,
        string source,
        bool includeBiography,
        bool excludeTextArt,
        CancellationToken cancellationToken)
    {
        var normalizedSource = NormalizeMetadataSource(source);
        var artwork = await _artistArtworkCatalog.GetAsync(artistId, cancellationToken, excludeTextArt);
        var candidates = artwork.Visuals
            .Where(item => normalizedSource == MetadataSourceAuto
                || string.Equals(item.Source, normalizedSource, StringComparison.OrdinalIgnoreCase)
                || normalizedSource == MetadataSourceApple
                   && string.Equals(item.Source, "itunes", StringComparison.OrdinalIgnoreCase))
            .Select(item => ArtworkCandidate.FromLocal(item.Path, item.Identity, item.Source, item.ContentHash))
            .ToList();
        var biography = includeBiography
            ? await _libraryRepository.GetArtistBiographyCacheAsync(
                artistId,
                normalizedSource == MetadataSourceAuto ? null : normalizedSource,
                allowFallback: normalizedSource == MetadataSourceAuto,
                cancellationToken: cancellationToken)
            : null;
        if (candidates.Count == 0 && biography is null)
        {
            return null;
        }

        return new ResolvedArtistMetadata(biography?.Biography, candidates);
    }

    public async Task ApplyCatalogVisualsToSlotsAsync(
        long artistId,
        string artistName,
        bool ocrTextArtBlockingEnabled,
        CancellationToken cancellationToken)
    {
        if (artistId <= 0 || string.IsNullOrWhiteSpace(artistName))
        {
            return;
        }

        var artwork = await _artistArtworkCatalog.GetAsync(artistId, cancellationToken, ocrTextArtBlockingEnabled);
        var candidates = artwork.Visuals
            .Where(item => !IsNameOnlyItunesIdentity(item.Identity))
            .OrderBy(item => RankArtworkSource(item.Source, item.Identity))
            .ThenByDescending(item => (item.Width ?? 0) * (item.Height ?? 0))
            .Select(item => ArtworkCandidate.FromLocal(item.Path, item.Identity, item.Source, item.ContentHash))
            .ToList();
        if (ocrTextArtBlockingEnabled)
        {
            candidates = await FilterUsableArtworkCandidatesAsync(artistId, candidates, cancellationToken);
        }
        if (candidates.Count == 0)
        {
            return;
        }

        var tracked = new MetadataUpdaterTrackedArtist
        {
            ArtistId = artistId,
            ArtistName = artistName,
            IncludeAvatar = true,
            IncludeBackground = true,
            OcrTextArtBlockingEnabled = ocrTextArtBlockingEnabled
        };
        var prepared = await PrepareVisualsAsync(
            tracked,
            candidates,
            cancellationToken,
            preferExistingSlots: false);
        await UpdateManagedArtistVisualsAsync(artistId, prepared, cancellationToken);
    }

    private async Task<PreparedVisuals> PrepareVisualsAsync(
        MetadataUpdaterTrackedArtist tracked,
        IReadOnlyList<ArtworkCandidate> sourceCandidates,
        CancellationToken cancellationToken,
        bool preferExistingSlots = true)
    {
        var managedRoot = Path.Join(
            AppDataPaths.GetDataRoot(_environment),
            "library-artist-images",
            SpotifyPlatform,
            "artists",
            tracked.ArtistId.ToString());

        Directory.CreateDirectory(managedRoot);

        var sourceCandidatesNormalized = NormalizeCandidates(sourceCandidates);
        if (tracked.OcrTextArtBlockingEnabled)
        {
            sourceCandidatesNormalized = await FilterUsableArtworkCandidatesAsync(
                tracked.ArtistId,
                sourceCandidatesNormalized,
                cancellationToken);
        }

        var avatarSlot = ResolveSlotCandidate(managedRoot, AvatarSlot);
        var backgroundSlot = ResolveSlotCandidate(managedRoot, BackgroundSlot);
        var avatarCandidates = preferExistingSlots
            ? BuildSlotCandidates(avatarSlot, AvatarSlot, RankCandidatesForSlot(sourceCandidatesNormalized, AvatarSlot))
            : RankCandidatesForSlot(sourceCandidatesNormalized, AvatarSlot);
        var backgroundCandidates = preferExistingSlots
            ? BuildSlotCandidates(backgroundSlot, BackgroundSlot, RankCandidatesForSlot(sourceCandidatesNormalized, BackgroundSlot))
            : RankCandidatesForSlot(sourceCandidatesNormalized, BackgroundSlot);

        var nextAvatarIndex = tracked.AvatarRotationIndex;
        var nextBackgroundIndex = tracked.BackgroundRotationIndex;
        string? avatarPath = avatarSlot;
        string? backgroundPath = backgroundSlot;
        ArtworkCandidate? selectedAvatarCandidate = null;
        var rotateUnused = !preferExistingSlots && tracked.ArtistId > 0;

        if (tracked.IncludeAvatar)
        {
            var avatarSelection = await SelectAndMaterializeSlotAsync(
                tracked.ArtistId,
                avatarCandidates,
                tracked.AvatarRotationIndex,
                managedRoot,
                AvatarSlot,
                excludedFingerprints: null,
                tracked.OcrTextArtBlockingEnabled,
                rotateUnused,
                cancellationToken);
            avatarPath = avatarSelection.Path;
            selectedAvatarCandidate = avatarSelection.Candidate;
            if (!string.IsNullOrWhiteSpace(avatarPath))
            {
                nextAvatarIndex = (tracked.AvatarRotationIndex + 1) % Math.Max(1, avatarCandidates.Count);
            }
        }

        if (tracked.IncludeBackground)
        {
            var excluded = BuildArtworkFingerprints(selectedAvatarCandidate, avatarPath);
            var backgroundSelection = await SelectAndMaterializeSlotAsync(
                tracked.ArtistId,
                backgroundCandidates,
                tracked.BackgroundRotationIndex,
                managedRoot,
                BackgroundSlot,
                excluded,
                tracked.OcrTextArtBlockingEnabled,
                rotateUnused,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(backgroundSelection.Path)
                && excluded.Count > 0)
            {
                backgroundSelection = await SelectAndMaterializeSlotAsync(
                    tracked.ArtistId,
                    backgroundCandidates,
                    tracked.BackgroundRotationIndex,
                    managedRoot,
                    BackgroundSlot,
                    excludedFingerprints: null,
                    tracked.OcrTextArtBlockingEnabled,
                    rotateUnused,
                    cancellationToken);
            }

            backgroundPath = backgroundSelection.Path;
            if (!string.IsNullOrWhiteSpace(backgroundPath))
            {
                nextBackgroundIndex = (tracked.BackgroundRotationIndex + 1) % Math.Max(1, backgroundCandidates.Count);
            }
        }

        return new PreparedVisuals(avatarPath, backgroundPath, nextAvatarIndex, nextBackgroundIndex);
    }

    private async Task<(string? Path, ArtworkCandidate? Candidate)> SelectAndMaterializeSlotAsync(
        long artistId,
        IReadOnlyList<ArtworkCandidate> candidates,
        int rotationIndex,
        string managedRoot,
        string slot,
        IReadOnlySet<string>? excludedFingerprints,
        bool textArtBlockingEnabled,
        bool rotateUnused,
        CancellationToken cancellationToken)
    {
        var pool = candidates;
        var wrapped = false;
        if (rotateUnused && artistId > 0)
        {
            var used = await _libraryRepository.GetUsedVisualHashesAsync(artistId, slot, cancellationToken);
            var unused = FilterUnusedCandidates(candidates, used);
            if (unused.Count == 0)
            {
                await _libraryRepository.ClearVisualUsageAsync(artistId, slot, cancellationToken);
                wrapped = true;
            }
            else
            {
                pool = unused;
            }
        }

        var selected = await RotateAndMaterializeSlotAsync(
            pool,
            rotationIndex,
            managedRoot,
            slot,
            excludedFingerprints,
            textArtBlockingEnabled,
            cancellationToken);
        if (rotateUnused
            && artistId > 0
            && !wrapped
            && selected.Candidate is null
            && candidates.Count > 0)
        {
            await _libraryRepository.ClearVisualUsageAsync(artistId, slot, cancellationToken);
            selected = await RotateAndMaterializeSlotAsync(
                candidates,
                rotationIndex,
                managedRoot,
                slot,
                excludedFingerprints,
                textArtBlockingEnabled,
                cancellationToken);
        }

        if (rotateUnused && artistId > 0 && selected.Candidate is not null)
        {
            var hash = selected.Candidate.ContentHash ?? ComputeFileHashOrNull(selected.Path);
            if (!string.IsNullOrWhiteSpace(hash))
            {
                await _libraryRepository.RecordVisualUsageAsync(
                    artistId,
                    slot,
                    hash,
                    selected.Candidate.Identity,
                    cancellationToken);
            }
        }

        return selected;
    }

    private static List<ArtworkCandidate> FilterUnusedCandidates(
        IReadOnlyList<ArtworkCandidate> candidates,
        IReadOnlyCollection<string> usedHashes)
    {
        if (usedHashes.Count == 0)
        {
            return candidates.ToList();
        }

        var used = new HashSet<string>(
            usedHashes.Where(hash => !string.IsNullOrWhiteSpace(hash)).Select(hash => hash.Trim()),
            StringComparer.OrdinalIgnoreCase);
        return candidates
            .Where(candidate =>
                string.IsNullOrWhiteSpace(candidate.ContentHash)
                || !used.Contains(candidate.ContentHash.Trim()))
            .ToList();
    }

    private static List<ArtworkCandidate> BuildSlotCandidates(
        string? slotPath,
        string slot,
        IReadOnlyList<ArtworkCandidate> sourceCandidates)
    {
        var candidates = new List<ArtworkCandidate>();
        if (!string.IsNullOrWhiteSpace(slotPath))
        {
            candidates.Add(ArtworkCandidate.FromLocal(
                slotPath,
                $"slot:{slot}:{Path.GetFullPath(slotPath)}",
                "managed"));
        }

        candidates.AddRange(sourceCandidates);
        return NormalizeCandidates(candidates);
    }

    private async Task<(string? Path, ArtworkCandidate? Candidate)> RotateAndMaterializeSlotAsync(
        IReadOnlyList<ArtworkCandidate> candidates,
        int rotationIndex,
        string managedRoot,
        string slot,
        IReadOnlySet<string>? excludedFingerprints,
        bool textArtBlockingEnabled,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return (ResolveDistinctSlotCandidate(managedRoot, slot, excludedFingerprints), null);
        }

        var boundedIndex = Math.Abs(rotationIndex) % candidates.Count;
        var artistId = long.TryParse(Path.GetFileName(managedRoot), out var parsedArtistId)
            ? parsedArtistId
            : 0;
        for (var offset = 0; offset < candidates.Count; offset++)
        {
            var selected = candidates[(boundedIndex + offset) % candidates.Count];
            if (artistId > 0
                && _libraryRepository is not null
                && await _libraryRepository.IsArtistArtworkBlockedAsync(artistId, slot, selected.Identity, cancellationToken))
            {
                LogRejectedArtworkCandidate(slot, managedRoot, selected.Source);
                continue;
            }

            if (excludedFingerprints is { Count: > 0 } && IsExcludedArtwork(selected, excludedFingerprints))
            {
                continue;
            }

            var materialized = await TryMaterializeSlotCandidateAsync(selected, managedRoot, slot, textArtBlockingEnabled, cancellationToken);
            if (!string.IsNullOrWhiteSpace(materialized.Path)
                && (excludedFingerprints is not { Count: > 0 }
                    || !IsExcludedPath(materialized.Path, excludedFingerprints)))
            {
                return materialized;
            }
        }

        return (ResolveDistinctSlotCandidate(managedRoot, slot, excludedFingerprints), null);
    }

    private async Task<(string? Path, ArtworkCandidate? Candidate)> TryMaterializeSlotCandidateAsync(
        ArtworkCandidate selected,
        string managedRoot,
        string slot,
        bool textArtBlockingEnabled,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(selected.LocalPath) && File.Exists(selected.LocalPath))
        {
            if (textArtBlockingEnabled && !await IsArtworkCandidateUsableAsync(selected.LocalPath, cancellationToken))
            {
                LogRejectedArtworkCandidate(slot, managedRoot, selected.Source);
                await PersistRejectedArtworkCandidateAsync(managedRoot, slot, selected, selected.LocalPath, cancellationToken);
                return (null, null);
            }

            return (await CopyIntoSlotAsync(managedRoot, slot, selected.LocalPath, cancellationToken), selected);
        }

        return (null, null);
    }

    private async Task<List<ArtworkCandidate>> FilterUsableArtworkCandidatesAsync(
        long artistId,
        IReadOnlyList<ArtworkCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var usable = new List<ArtworkCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(candidate.LocalPath)
                || !File.Exists(candidate.LocalPath))
            {
                continue;
            }

            if (await IsArtworkCandidateUsableAsync(candidate.LocalPath, cancellationToken))
            {
                usable.Add(candidate);
                continue;
            }

            await PersistRejectedArtworkCandidateAsync(artistId, candidate, candidate.LocalPath, cancellationToken);
        }

        return usable;
    }

    private async Task PersistRejectedArtworkCandidateAsync(
        string managedRoot,
        string slot,
        ArtworkCandidate selected,
        string? localPath,
        CancellationToken cancellationToken)
    {
        if (!long.TryParse(Path.GetFileName(managedRoot), out var artistId) || artistId <= 0)
        {
            return;
        }

        await PersistRejectedArtworkCandidateAsync(artistId, selected, localPath, cancellationToken);
    }

    private async Task PersistRejectedArtworkCandidateAsync(
        long artistId,
        ArtworkCandidate selected,
        string? localPath,
        CancellationToken cancellationToken)
    {
        if (_libraryRepository is null || artistId <= 0)
        {
            return;
        }

        await _libraryRepository.MarkArtistArtworkTextBlockedAsync(
            artistId,
            selected.Identity,
            selected.ContentHash ?? ComputeFileHashOrNull(localPath),
            localPath,
            cancellationToken);
    }

    private void LogRejectedArtworkCandidate(string slot, string managedRoot, string source)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Rejected text-heavy artwork candidate for {Slot}. artist={ArtistId} source={Source}",
                slot,
                managedRoot,
                source);
        }
    }

    private static ArtworkCandidate? NormalizeCandidate(ArtworkCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.LocalPath))
        {
            var fullPath = Path.GetFullPath(candidate.LocalPath);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            return candidate with
            {
                LocalPath = fullPath,
                Identity = string.IsNullOrWhiteSpace(candidate.Identity) ? fullPath : candidate.Identity,
                ContentHash = string.IsNullOrWhiteSpace(candidate.ContentHash)
                    ? ComputeFileHashOrNull(fullPath)
                    : candidate.ContentHash
            };
        }

        return null;
    }

    private static List<ArtworkCandidate> NormalizeCandidates(IEnumerable<ArtworkCandidate> candidates)
        => candidates
            .Select(NormalizeCandidate)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .GroupBy(candidate => ArtworkFingerprint(candidate) ?? candidate.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    private static HashSet<string> BuildArtworkFingerprints(ArtworkCandidate? candidate, string? materializedPath)
    {
        var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddFingerprint(fingerprints, ArtworkFingerprint(candidate));
        AddFingerprint(fingerprints, PathFingerprint(materializedPath));
        AddFingerprint(fingerprints, PathFingerprint(candidate?.LocalPath));
        AddFingerprint(fingerprints, FileFingerprint(materializedPath));
        AddFingerprint(fingerprints, FileFingerprint(candidate?.LocalPath));
        return fingerprints;
    }

    private static bool IsExcludedArtwork(ArtworkCandidate candidate, IReadOnlySet<string> excludedFingerprints)
        => excludedFingerprints.Contains(ArtworkFingerprint(candidate) ?? string.Empty)
           || excludedFingerprints.Contains(PathFingerprint(candidate.LocalPath) ?? string.Empty)
           || IsVisuallyExcluded(candidate.LocalPath, excludedFingerprints);

    private static bool IsExcludedPath(string path, IReadOnlySet<string> excludedFingerprints)
        => excludedFingerprints.Contains(PathFingerprint(path) ?? string.Empty)
           || IsVisuallyExcluded(path, excludedFingerprints);

    private static bool IsVisuallyExcluded(string? path, IReadOnlySet<string> excludedFingerprints)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var fingerprint in excludedFingerprints)
        {
            if (!fingerprint.StartsWith(FileFingerprintPrefix, StringComparison.OrdinalIgnoreCase)
                || fingerprint.Length <= FileFingerprintPrefix.Length)
            {
                continue;
            }

            if (AreVisuallyTheSamePhoto(path, fingerprint[FileFingerprintPrefix.Length..]))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FileFingerprint(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return FileFingerprintPrefix + Path.GetFullPath(path);
    }

    private static bool AreVisuallyTheSamePhoto(string pathA, string pathB)
    {
        if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
        {
            return false;
        }

        try
        {
            var fullA = Path.GetFullPath(pathA);
            var fullB = Path.GetFullPath(pathB);
            if (string.Equals(fullA, fullB, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!File.Exists(fullA) || !File.Exists(fullB))
            {
                return false;
            }

            using var imageA = Image.Load<Rgba32>(fullA);
            using var imageB = Image.Load<Rgba32>(fullB);
            if (imageA.Width <= 0 || imageA.Height <= 0 || imageB.Width <= 0 || imageB.Height <= 0)
            {
                return false;
            }

            var areaA = (long)imageA.Width * imageA.Height;
            var areaB = (long)imageB.Width * imageB.Height;
            var larger = areaA >= areaB ? imageA : imageB;
            var smaller = areaA >= areaB ? imageB : imageA;
            using var resized = larger.Clone(ctx => ctx.Resize(smaller.Width, smaller.Height));
            return MeanAbsoluteDifference(resized, smaller) <= VisualDuplicateMeanAbsDifference;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static double MeanAbsoluteDifference(Image<Rgba32> left, Image<Rgba32> right)
    {
        if (left.Width != right.Width || left.Height != right.Height || left.Width == 0 || left.Height == 0)
        {
            return double.MaxValue;
        }

        long total = 0;
        var count = (long)left.Width * left.Height;
        for (var y = 0; y < left.Height; y++)
        {
            for (var x = 0; x < left.Width; x++)
            {
                var a = left[x, y];
                var b = right[x, y];
                total += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            }
        }

        return total / (3d * count);
    }

    private static string? ArtworkFingerprint(ArtworkCandidate? candidate)
    {
        if (candidate is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(candidate.ContentHash))
        {
            return "h:" + candidate.ContentHash.Trim().ToLowerInvariant();
        }

        return PathFingerprint(candidate.LocalPath);
    }

    private static string? PathFingerprint(string? path)
    {
        var hash = ComputeFileHashOrNull(path);
        return string.IsNullOrWhiteSpace(hash) ? null : "h:" + hash;
    }

    private static void AddFingerprint(HashSet<string> fingerprints, string? fingerprint)
    {
        if (!string.IsNullOrWhiteSpace(fingerprint))
        {
            fingerprints.Add(fingerprint);
        }
    }

    private static bool IsNameOnlyItunesIdentity(string? identity)
        => !string.IsNullOrWhiteSpace(identity)
           && identity.StartsWith("itunes:http", StringComparison.OrdinalIgnoreCase);

    private static List<ArtworkCandidate> RankCandidatesForSlot(
        IReadOnlyList<ArtworkCandidate> candidates,
        string slot)
        => candidates
            .OrderBy(candidate => RankCandidateForSlot(candidate, slot))
            .ToList();

    private static int RankCandidateForSlot(ArtworkCandidate candidate, string slot)
    {
        var identity = candidate.Identity ?? string.Empty;
        if (identity.Contains(":profile:", StringComparison.OrdinalIgnoreCase))
        {
            return slot == AvatarSlot ? 0 : 2;
        }

        if (identity.Contains(":header:", StringComparison.OrdinalIgnoreCase))
        {
            return slot == BackgroundSlot ? 0 : 2;
        }

        var aspect = TryReadAspectRatio(candidate.LocalPath);
        if (slot == AvatarSlot)
        {
            return 10 + (int)Math.Round(Math.Abs(aspect - 1d) * 100) + RankArtworkSource(candidate.Source, identity);
        }

        return aspect >= 1.35 ? 1 : 20 + RankArtworkSource(candidate.Source, identity);
    }

    private static double TryReadAspectRatio(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return 1d;
        }

        try
        {
            var info = Image.Identify(path);
            return info.Height <= 0 ? 1d : info.Width / (double)info.Height;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 1d;
        }
    }

    private static int RankArtworkSource(string? source, string? identity)
    {
        if (!string.IsNullOrWhiteSpace(identity)
            && identity.StartsWith("apple:", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return source?.Trim().ToLowerInvariant() switch
        {
            "spotify" => 0,
            "apple" => 1,
            "deezer" => 2,
            "tidal" => 3,
            "qobuz" => 4,
            "lastfm" => 5,
            "itunes" => 6,
            "local" => 7,
            _ => 8
        };
    }

    private async Task<PushOutcome> PushArtistMetadataAsync(
        PushMetadataRequest request,
        CancellationToken cancellationToken)
    {
        var runWarnings = new List<string>();
        var results = new List<ArtistTargetResult>(3);
        if (request.Targets.Contains(PlexTarget, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(await PushToPlexAsync(request, runWarnings, cancellationToken));
        }

        if (request.Targets.Contains(JellyfinTarget, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(await PushToJellyfinAsync(request, runWarnings, cancellationToken));
        }

        if (request.Targets.Contains(NavidromeTarget, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(await PushToNavidromeAsync(request, runWarnings, cancellationToken));
        }

        return new PushOutcome(results, runWarnings);
    }

    /// <summary>
    /// Navidrome is declared <see cref="ArtistTargetCapability.ArtworkOnly"/>: it accepts artwork
    /// uploads, its biography is read-only, and <c>startScan</c> only asks Navidrome to re-read the
    /// library. None of those three facts is a failure, and none of them means content changed.
    /// </summary>
    private async Task<ArtistTargetResult> PushToNavidromeAsync(
        PushMetadataRequest request,
        List<string> runWarnings,
        CancellationToken cancellationToken)
    {
        var navidrome = request.Auth.Navidrome;
        if (navidrome is null
            || string.IsNullOrWhiteSpace(navidrome.Url)
            || string.IsNullOrWhiteSpace(navidrome.Username)
            || string.IsNullOrWhiteSpace(navidrome.Password))
        {
            return ArtistTargetResult.NotConfigured(NavidromeTarget, "Navidrome is not configured.");
        }

        var updatedFields = new List<string>();
        var limitations = new List<string>();
        var errors = new List<string>();
        try
        {
            var artistIds = await _navidromeClient.FindArtistIdsAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                request.ArtistName,
                cancellationToken);
            if (artistIds.Count == 0)
            {
                return ArtistTargetResult.NotFound(NavidromeTarget);
            }

            var localAlbumTitles = await GetLocalAlbumTitlesForServerVerificationAsync(request.LocalArtistId, cancellationToken);
            if (localAlbumTitles.Count > 0)
            {
                var candidateAlbumTitles = new List<IReadOnlyList<string>>();
                foreach (var candidateId in artistIds.Take(8))
                {
                    candidateAlbumTitles.Add(
                        await _navidromeClient.GetArtistAlbumTitlesAsync(navidrome.Url, navidrome.Username, navidrome.Password, candidateId, cancellationToken));
                }

                var verifiedIndices = SelectServerCandidatesByAlbumOverlap(localAlbumTitles, candidateAlbumTitles, runWarnings, "Navidrome");
                artistIds = verifiedIndices.Select(index => artistIds[index]).ToList();
            }

            if (artistIds.Count == 0)
            {
                return ArtistTargetResult.NotFound(NavidromeTarget);
            }

            if (request.LocalArtistId > 0)
            {
                await _libraryRepository.UpsertArtistSourceIdAsync(request.LocalArtistId, NavidromeTarget, artistIds[0], cancellationToken);
            }

            var avatarAvailable = HasLocalFile(request.AvatarPath);
            var navidromeImagePath = avatarAvailable
                ? request.AvatarPath
                : HasLocalFile(request.BackgroundPath)
                    ? request.BackgroundPath
                    : null;
            var usedBackgroundSlot = !avatarAvailable && HasLocalFile(request.BackgroundPath);

            if (HasLocalFile(navidromeImagePath))
            {
                var uploadFailures = 0;
                foreach (var artistId in artistIds)
                {
                    var uploaded = await _navidromeClient.UpdateArtistImageFromFileAsync(
                        navidrome.Url,
                        navidrome.Username,
                        navidrome.Password,
                        artistId,
                        navidromeImagePath!,
                        null,
                        cancellationToken);
                    if (uploaded)
                    {
                        updatedFields.Add(usedBackgroundSlot ? ArtistTargetFields.Background : ArtistTargetFields.Avatar);
                    }
                    else
                    {
                        uploadFailures++;
                    }
                }

                if (uploadFailures > 0)
                {
                    errors.Add(
                        $"Navidrome artist image upload failed for {uploadFailures} of {artistIds.Count} matched artists. "
                        + "Navidrome rejects artwork uploads unless the account is an admin or EnableArtworkUpload=true is set in navidrome.toml.");
                }
            }

            // Declared capability limit: deliberately not a failure and not a partial-update trigger.
            if (!string.IsNullOrWhiteSpace(request.Biography))
            {
                limitations.Add("Navidrome biography is read-only and was not updated.");
            }

            var scanStarted = await _navidromeClient.StartScanAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                cancellationToken);
            if (scanStarted)
            {
                // A rescan asks Navidrome to re-read; it is a notification, not a metadata write.
                updatedFields.Add(ArtistTargetFields.Scan);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Metadata updater Navidrome scan request failed for {Artist}", request.ArtistName);
            errors.Add("Navidrome scan request failed.");
        }

        return BuildTargetResult(NavidromeTarget, updatedFields, limitations, errors);
    }

    /// <summary>
    /// Collapses a target's collected fields, limitations and errors into one typed result. A genuine
    /// error wins over a partial success, but a declared limitation never does.
    /// </summary>
    private static ArtistTargetResult BuildTargetResult(
        string target,
        List<string> updatedFields,
        List<string> limitations,
        List<string> errors)
    {
        var wroteContent = updatedFields.Any(field => field != ArtistTargetFields.Scan);
        var outcome = errors.Count > 0
            ? ArtistTargetOutcome.Failed
            : wroteContent
                ? ArtistTargetOutcome.Updated
                : ArtistTargetOutcome.Unchanged;
        return new ArtistTargetResult(
            target,
            outcome,
            updatedFields.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            limitations,
            errors.Count == 0 ? null : string.Join(" ", errors));
    }

    private async Task<ArtistTargetResult> PushToPlexAsync(
        PushMetadataRequest request,
        List<string> runWarnings,
        CancellationToken cancellationToken)
    {
        if (!TryGetPlexConnection(request.Auth.Plex, out var plexUrl, out var plexToken))
        {
            return ArtistTargetResult.NotConfigured(PlexTarget, "Plex is not configured.");
        }

        var updates = new PushUpdateAccumulator();
        var targetWarnings = new List<string>();
        try
        {
            var locations = await _plexClient.FindArtistLocationsAsync(plexUrl, plexToken, request.ArtistName, cancellationToken);
            if (locations.Count == 0)
            {
                return ArtistTargetResult.NotFound(PlexTarget);
            }

            var localAlbumTitles = await GetLocalAlbumTitlesForServerVerificationAsync(request.LocalArtistId, cancellationToken);
            if (localAlbumTitles.Count > 0)
            {
                var candidateAlbumTitles = new List<IReadOnlyList<string>>();
                foreach (var location in locations.Take(8))
                {
                    candidateAlbumTitles.Add(
                        await _plexClient.GetArtistAlbumTitlesAsync(plexUrl, plexToken, location.RatingKey, cancellationToken));
                }

                var verifiedIndices = SelectServerCandidatesByAlbumOverlap(localAlbumTitles, candidateAlbumTitles, runWarnings, "Plex");
                locations = verifiedIndices.Select(index => locations[index]).ToList();
            }

            if (locations.Count == 0)
            {
                return ArtistTargetResult.NotFound(PlexTarget);
            }

            await UpsertPlexSourceIdAsync(request, locations[0], cancellationToken);
            foreach (var location in locations)
            {
                var artworkUpdates = await UpdatePlexArtworkAsync(request, plexUrl, plexToken, location, cancellationToken);
                updates.AvatarUpdated = artworkUpdates.AvatarUpdated || updates.AvatarUpdated;
                updates.BackgroundUpdated = artworkUpdates.BackgroundUpdated || updates.BackgroundUpdated;
                await TryLockPlexArtworkAsync(plexUrl, plexToken, location, artworkUpdates, targetWarnings, cancellationToken);
                await UpdatePlexBiographyAsync(request, plexUrl, plexToken, location, updates, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Metadata updater Plex push failed for {Artist}", request.ArtistName);
            targetWarnings.Add("Plex update failed.");
        }

        return BuildTargetResult(
            PlexTarget,
            DescribePlexUpdatedFields(updates),
            limitations: new List<string>(),
            errors: targetWarnings);
    }

    private static List<string> DescribePlexUpdatedFields(PushUpdateAccumulator updates)
    {
        var fields = new List<string>(3);
        if (updates.AvatarUpdated)
        {
            fields.Add(ArtistTargetFields.Avatar);
        }

        if (updates.BackgroundUpdated)
        {
            fields.Add(ArtistTargetFields.Background);
        }

        if (updates.BioUpdated)
        {
            fields.Add(ArtistTargetFields.Biography);
        }

        return fields;
    }

    private static bool TryGetPlexConnection(PlexAuth? plex, out string url, out string token)
    {
        url = string.Empty;
        token = string.Empty;
        if (plex is null || string.IsNullOrWhiteSpace(plex.Url) || string.IsNullOrWhiteSpace(plex.Token))
        {
            return false;
        }

        url = plex.Url;
        token = plex.Token;
        return true;
    }

    private async Task UpsertPlexSourceIdAsync(
        PushMetadataRequest request,
        PlexArtistLocation location,
        CancellationToken cancellationToken)
    {
        if (request.LocalArtistId <= 0 || string.IsNullOrWhiteSpace(location.RatingKey))
        {
            return;
        }

        await _libraryRepository.UpsertArtistSourceIdAsync(request.LocalArtistId, PlexTarget, location.RatingKey, cancellationToken);
    }

    private async Task<PlexArtworkUpdates> UpdatePlexArtworkAsync(
        PushMetadataRequest request,
        string plexUrl,
        string plexToken,
        PlexArtistLocation location,
        CancellationToken cancellationToken)
    {
        var avatarUpdated = false;
        if (HasLocalFile(request.AvatarPath))
        {
            avatarUpdated = await _plexClient.UpdateArtistPosterFromFileAsync(
                plexUrl,
                plexToken,
                location.RatingKey,
                request.AvatarPath!,
                cancellationToken);
        }

        var backgroundUpdated = false;
        if (HasLocalFile(request.BackgroundPath))
        {
            backgroundUpdated = await _plexClient.UpdateArtistArtFromFileAsync(
                plexUrl,
                plexToken,
                location.RatingKey,
                request.BackgroundPath!,
                cancellationToken);
        }

        return new PlexArtworkUpdates(avatarUpdated, backgroundUpdated);
    }

    private async Task TryLockPlexArtworkAsync(
        string plexUrl,
        string plexToken,
        PlexArtistLocation location,
        PlexArtworkUpdates artworkUpdates,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!artworkUpdates.HasAnyUpdate)
        {
            return;
        }

        var locked = await _plexClient.LockArtistArtworkAsync(
            plexUrl,
            plexToken,
            location.SectionKey,
            location.RatingKey,
            lockPoster: artworkUpdates.AvatarUpdated,
            lockBackground: artworkUpdates.BackgroundUpdated,
            cancellationToken);
        if (!locked)
        {
            warnings.Add("Plex artwork lock failed; Plex may revert avatar/background on refresh.");
        }
    }

    private async Task UpdatePlexBiographyAsync(
        PushMetadataRequest request,
        string plexUrl,
        string plexToken,
        PlexArtistLocation location,
        PushUpdateAccumulator updates,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Biography))
        {
            return;
        }

        updates.BioUpdated = await _plexClient.UpdateArtistBiographyAsync(
            plexUrl,
            plexToken,
            location.SectionKey,
            location.RatingKey,
            request.Biography,
            cancellationToken) || updates.BioUpdated;
    }

    private static bool HasLocalFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private async Task<ArtistTargetResult> PushToJellyfinAsync(
        PushMetadataRequest request,
        List<string> runWarnings,
        CancellationToken cancellationToken)
    {
        var jellyfin = request.Auth.Jellyfin;
        if (jellyfin is null || string.IsNullOrWhiteSpace(jellyfin.Url) || string.IsNullOrWhiteSpace(jellyfin.ApiKey))
        {
            return ArtistTargetResult.NotConfigured(JellyfinTarget, "Jellyfin is not configured.");
        }

        var updates = new PushUpdateAccumulator();
        var errors = new List<string>();
        try
        {
            var artistIds = await _jellyfinClient.FindArtistIdsAsync(jellyfin.Url, jellyfin.ApiKey, request.ArtistName, cancellationToken);
            if (artistIds.Count == 0)
            {
                return ArtistTargetResult.NotFound(JellyfinTarget);
            }

            var localAlbumTitles = await GetLocalAlbumTitlesForServerVerificationAsync(request.LocalArtistId, cancellationToken);
            if (localAlbumTitles.Count > 0)
            {
                var candidateAlbumTitles = new List<IReadOnlyList<string>>();
                foreach (var candidateId in artistIds.Take(8))
                {
                    candidateAlbumTitles.Add(
                        await _jellyfinClient.GetArtistAlbumTitlesAsync(jellyfin.Url, jellyfin.ApiKey, candidateId, cancellationToken));
                }

                var verifiedIndices = SelectServerCandidatesByAlbumOverlap(localAlbumTitles, candidateAlbumTitles, runWarnings, "Jellyfin");
                artistIds = verifiedIndices.Select(index => artistIds[index]).ToList();
            }

            if (artistIds.Count == 0)
            {
                return ArtistTargetResult.NotFound(JellyfinTarget);
            }

            if (request.LocalArtistId > 0)
            {
                await _libraryRepository.UpsertArtistSourceIdAsync(request.LocalArtistId, JellyfinTarget, artistIds[0], cancellationToken);
            }

            foreach (var artistId in artistIds)
            {
                await PushSingleJellyfinArtistMetadataAsync(
                    request,
                    jellyfin.Url,
                    jellyfin.ApiKey,
                    artistId,
                    updates,
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Metadata updater Jellyfin push failed for {Artist}", request.ArtistName);
            errors.Add("Jellyfin update failed.");
        }

        return BuildTargetResult(
            JellyfinTarget,
            DescribePlexUpdatedFields(updates),
            limitations: new List<string>(),
            errors: errors);
    }

    private async Task PushSingleJellyfinArtistMetadataAsync(
        PushMetadataRequest request,
        string jellyfinUrl,
        string jellyfinApiKey,
        string artistId,
        PushUpdateAccumulator updates,
        CancellationToken cancellationToken)
    {
        if (HasLocalFile(request.AvatarPath))
        {
            updates.AvatarUpdated = await _jellyfinClient.UpdateArtistImageAsync(
                jellyfinUrl,
                jellyfinApiKey,
                artistId,
                request.AvatarPath!,
                cancellationToken) || updates.AvatarUpdated;
        }

        if (HasLocalFile(request.BackgroundPath))
        {
            updates.BackgroundUpdated = await _jellyfinClient.UpdateArtistBackdropAsync(
                jellyfinUrl,
                jellyfinApiKey,
                artistId,
                request.BackgroundPath!,
                cancellationToken) || updates.BackgroundUpdated;
        }

        if (!string.IsNullOrWhiteSpace(request.Biography))
        {
            updates.BioUpdated = await _jellyfinClient.UpdateArtistOverviewAsync(
                jellyfinUrl,
                jellyfinApiKey,
                artistId,
                request.Biography,
                cancellationToken) || updates.BioUpdated;
        }
    }

    private static async Task<string?> CopyIntoSlotAsync(string managedRoot, string slot, string sourcePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            return null;
        }

        var extension = ImageFileExtensionResolver.NormalizeStandardImageExtension(Path.GetExtension(sourcePath));
        var destination = Path.Join(managedRoot, $"{slot}{extension}");
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            return destination;
        }

        await using (var sourceStream = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        await using (var destinationStream = File.Create(destination))
        {
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
        }

        DeleteSlotVariants(managedRoot, slot, destination);
        return destination;
    }

    private async Task<bool> IsArtworkCandidateUsableAsync(string imagePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return false;
        }

        try
        {
            await using var stream = File.OpenRead(imagePath);
            using var image = await Image.LoadAsync<Rgba32>(stream, cancellationToken);
            return !ArtistArtworkTextInspector.LikelyContainsOverlayText(image);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Artwork text inspection failed for {Path}", imagePath);
            }
            return false;
        }
    }

    private static string? ResolveSlotCandidate(string managedRoot, string slot)
        => ResolveDistinctSlotCandidate(managedRoot, slot, excludedFingerprints: null);

    private static string? ResolveDistinctSlotCandidate(
        string managedRoot,
        string slot,
        IReadOnlySet<string>? excludedFingerprints)
    {
        if (!Directory.Exists(managedRoot))
        {
            return null;
        }

        var path = Directory.GetFiles(managedRoot, $"{slot}.*", SearchOption.TopDirectoryOnly)
            .Where(File.Exists)
            .OrderByDescending(item => new FileInfo(item).LastWriteTimeUtc)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path)
            || (excludedFingerprints is { Count: > 0 } && IsExcludedPath(path, excludedFingerprints)))
        {
            return null;
        }

        return path;
    }

    private static void DeleteSlotVariants(string managedRoot, string slot, string keepPath)
    {
        if (!Directory.Exists(managedRoot))
        {
            return;
        }

        foreach (var path in Directory.GetFiles(managedRoot, $"{slot}.*", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(keepPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                // Best effort cleanup only.
            }
        }
    }

    private static void TryDeleteBestEffort(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // Best effort cleanup only.
        }
    }

    private async Task<MetadataUpdaterState> LoadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath) || new FileInfo(_statePath).Length == 0)
        {
            return new MetadataUpdaterState();
        }

        try
        {
            await using var stream = File.OpenRead(_statePath);
            var state = await JsonSerializer.DeserializeAsync<MetadataUpdaterState>(stream, _jsonOptions, cancellationToken);
            return state ?? new MetadataUpdaterState();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load metadata updater state.");
            return new MetadataUpdaterState();
        }
    }

    private async Task SaveStateAsync(MetadataUpdaterState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _statePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken);
        }
        File.Move(temporaryPath, _statePath, overwrite: true);
    }

    private void UpdateStatus(MetadataUpdaterStatusSnapshot status)
    {
        lock (_statusLock)
        {
            _status = status;
        }
    }

    private static int NormalizeIntervalDays(int value) => Math.Clamp(value, 0, 365);

    private static IReadOnlyList<string> ResolveTrackedTargets(MetadataUpdaterTrackedArtist tracked)
        => NormalizeTargets(tracked.Targets, tracked.Target);

    private static IReadOnlyList<string> NormalizeTargets(IReadOnlyList<string>? targets, string? legacyTarget)
    {
        var normalized = new List<string>();
        if (targets is not null)
        {
            foreach (var target in targets)
            {
                AddNormalizedTarget(normalized, target);
            }
        }

        if (normalized.Count == 0)
        {
            AddNormalizedTarget(normalized, legacyTarget);
        }

        return normalized.Count == 0 ? new[] { PlexTarget } : normalized;
    }

    private static void AddNormalizedTarget(List<string> targets, string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (normalized == LegacyBothTargets)
        {
            AddTargetIfMissing(targets, PlexTarget);
            AddTargetIfMissing(targets, JellyfinTarget);
            return;
        }

        if (normalized is PlexTarget or JellyfinTarget or NavidromeTarget)
        {
            AddTargetIfMissing(targets, normalized);
        }
    }

    private static void AddTargetIfMissing(List<string> targets, string target)
    {
        if (!targets.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            targets.Add(target);
        }
    }

    private static string ToLegacyTarget(IReadOnlyList<string> targets)
    {
        var hasPlex = targets.Contains(PlexTarget, StringComparer.OrdinalIgnoreCase);
        var hasJellyfin = targets.Contains(JellyfinTarget, StringComparer.OrdinalIgnoreCase);
        var hasNavidrome = targets.Contains(NavidromeTarget, StringComparer.OrdinalIgnoreCase);
        if (hasPlex && hasJellyfin && !hasNavidrome)
        {
            return LegacyBothTargets;
        }

        if (hasJellyfin && !hasPlex && !hasNavidrome)
        {
            return JellyfinTarget;
        }

        if (hasNavidrome && !hasPlex && !hasJellyfin)
        {
            return NavidromeTarget;
        }

        return PlexTarget;
    }

    private static string NormalizeMetadataSource(string? value)
    {
        var normalized = (value ?? MetadataSourceAuto).Trim().ToLowerInvariant();
        return normalized switch
        {
            MetadataSourceSpotify => MetadataSourceSpotify,
            MetadataSourceDeezer => MetadataSourceDeezer,
            MetadataSourceApple => MetadataSourceApple,
            MetadataSourceTidal => MetadataSourceTidal,
            MetadataSourceQobuz => MetadataSourceQobuz,
            MetadataSourceLastFm => MetadataSourceLastFm,
            _ => MetadataSourceAuto
        };
    }

    private static string? SanitizeBiography(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = ArtistBiographySanitizer.StripPlatformMarkup(text);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return text;
    }

    private static string? ComputeFileHashOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }



    /// <summary>
    /// Local album titles used to verify that a name-searched server artist is really
    /// the library artist before pushing metadata to it.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetLocalAlbumTitlesForServerVerificationAsync(
        long artistId,
        CancellationToken cancellationToken)
    {
        if (artistId <= 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            var albums = await _libraryRepository.GetArtistAlbumsAsync(artistId, cancellationToken);
            return ArtistIdentityTextNormalizer.FilterResolvableTitles(
                albums
                    .Select(album => album.Title ?? string.Empty)
                    .Where(title => !string.IsNullOrWhiteSpace(title))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not load local album titles for server-artist verification. artist={ArtistId}", artistId);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Picks which server-side candidates receive the push: when the library has a
    /// resolvable album, only candidates whose own album list overlaps the library
    /// are kept. If none overlap, the original candidates are kept and a warning is
    /// surfaced so pushes never silently break.
    /// </summary>
    private static List<int> SelectServerCandidatesByAlbumOverlap(
        IReadOnlyList<string> localAlbumTitles,
        IReadOnlyList<IReadOnlyList<string>> candidateAlbumTitles,
        ICollection<string> warnings,
        string serverLabel)
    {
        var allIndices = Enumerable.Range(0, candidateAlbumTitles.Count).ToList();
        if (!ArtistIdentityTextNormalizer.ShouldRequireAlbumOverlap(localAlbumTitles)
            || candidateAlbumTitles.Count == 0)
        {
            return allIndices;
        }

        var verified = new List<int>();
        for (var index = 0; index < candidateAlbumTitles.Count; index++)
        {
            if (ArtistIdentityTextNormalizer.CountAlbumOverlap(localAlbumTitles, candidateAlbumTitles[index]) > 0)
            {
                verified.Add(index);
            }
        }

        if (verified.Count > 0)
        {
            return verified;
        }

        warnings.Add(
            $"{serverLabel} artist matches could not be verified against local albums; pushing to all name matches.");
        return allIndices;
    }

    private static string? ComputeTextHashOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed record PreparedRunState(
        MetadataUpdaterState State,
        List<MetadataUpdaterTrackedArtist> Candidates,
        DateTimeOffset NowUtc);
    private sealed record ResolvedArtistMetadata(string? Biography, IReadOnlyList<ArtworkCandidate> Candidates);
    private enum ArtistProcessingOutcome
    {
        Succeeded,
        Partial,
        NoMetadata,
        Failed,
        SkippedNotDue,
        SkippedOther
    }

    /// <summary>What one artist produced, before it is folded into the run counters.</summary>
    private enum ArtistPushOutcome
    {
        Succeeded,
        Partial,
        NoMetadata,
        Failed,
        Unchanged,

        /// <summary>Only a Navidrome rescan was triggered: nothing was written.</summary>
        ScanOnly,

        /// <summary>Server sync is blocked for this artist, so no server was contacted.</summary>
        SyncBlocked,

        /// <summary>The library row disappeared, so the artist could not be processed.</summary>
        ArtistRowMissing
    }

    private static string? DescribePushSkipReason(ArtistPushOutcome outcome)
        => outcome switch
        {
            ArtistPushOutcome.ScanOnly => MetadataSkipReasons.ScanOnly,
            ArtistPushOutcome.SyncBlocked => MetadataSkipReasons.SyncBlocked,
            ArtistPushOutcome.ArtistRowMissing => MetadataSkipReasons.ArtistRowMissing,
            _ => null
        };

    private static class MetadataSkipReasons
    {
        public const string NotDue = ArtistRunSkipReasons.NotDue;
        public const string SyncBlocked = ArtistRunSkipReasons.SyncBlocked;
        public const string ScanOnly = ArtistRunSkipReasons.ScanOnly;
        public const string ArtistRowMissing = ArtistRunSkipReasons.ArtistRowMissing;
    }

    private sealed class MetadataRunCounters
    {
        private readonly Dictionary<string, int> _skipReasons = new(StringComparer.OrdinalIgnoreCase);

        public MetadataRunCounters(int totalArtists)
        {
            TotalArtists = totalArtists;
        }

        public int TotalArtists { get; }
        public int ProcessedArtists { get; private set; }
        public int SuccessfulArtists { get; private set; }
        public int PartialArtists { get; private set; }
        public int NoMetadataArtists { get; private set; }
        public int FailedArtists { get; private set; }
        public int SkippedArtists { get; private set; }

        /// <summary>Artists already finished by an earlier interrupted run of this same run.</summary>
        public int ResumedArtists { get; private set; }

        public IReadOnlyDictionary<string, int> SkipReasons => _skipReasons;

        /// <summary>
        /// Rebuilds counters from the persisted per-artist outcomes of an interrupted run. Processed
        /// is the number of outcome records, so the bucket totals always add up to it.
        /// </summary>
        public static MetadataRunCounters FromOutcomes(
            int totalArtists,
            IReadOnlyList<ArtistRunOutcomeRecord>? outcomes)
        {
            var counters = new MetadataRunCounters(totalArtists);
            if (outcomes is null)
            {
                return counters;
            }

            foreach (var outcome in outcomes)
            {
                counters.Apply(outcome);
            }

            counters.ResumedArtists = counters.ProcessedArtists;
            return counters;
        }

        public void Apply(ArtistRunOutcomeRecord record)
        {
            ProcessedArtists++;
            switch (record.Outcome)
            {
                case ArtistRunOutcomes.Succeeded:
                    SuccessfulArtists++;
                    return;
                case ArtistRunOutcomes.Partial:
                    PartialArtists++;
                    return;
                case ArtistRunOutcomes.NoMetadata:
                    NoMetadataArtists++;
                    return;
                case ArtistRunOutcomes.Failed:
                    FailedArtists++;
                    return;
                case ArtistRunOutcomes.Skipped:
                    SkippedArtists++;
                    AddSkipReason(string.IsNullOrWhiteSpace(record.Reason)
                        ? MetadataSkipReasons.NotDue
                        : record.Reason);
                    return;
                default:
                    ProcessedArtists--;
                    return;
            }
        }

        public void Apply(ArtistProcessingOutcome outcome)
        {
            switch (outcome)
            {
                case ArtistProcessingOutcome.Succeeded:
                    Apply(new ArtistRunOutcomeRecord(0, ArtistRunOutcomes.Succeeded));
                    return;
                case ArtistProcessingOutcome.Partial:
                    Apply(new ArtistRunOutcomeRecord(0, ArtistRunOutcomes.Partial));
                    return;
                case ArtistProcessingOutcome.NoMetadata:
                    Apply(new ArtistRunOutcomeRecord(0, ArtistRunOutcomes.NoMetadata));
                    return;
                case ArtistProcessingOutcome.Failed:
                    Apply(new ArtistRunOutcomeRecord(0, ArtistRunOutcomes.Failed));
                    return;
                case ArtistProcessingOutcome.SkippedNotDue:
                    Apply(new ArtistRunOutcomeRecord(0, ArtistRunOutcomes.Skipped, MetadataSkipReasons.NotDue));
                    return;
                case ArtistProcessingOutcome.SkippedOther:
                    Apply(new ArtistRunOutcomeRecord(0, ArtistRunOutcomes.Skipped, MetadataSkipReasons.ScanOnly));
                    return;
                default:
                    return;
            }
        }

        public Dictionary<string, int> SkipReasonsSnapshot()
            => new(_skipReasons, StringComparer.OrdinalIgnoreCase);

        private void AddSkipReason(string reason)
        {
            _skipReasons.TryGetValue(reason, out var count);
            _skipReasons[reason] = count + 1;
        }
    }

    private sealed record PreparedVisuals(string? AvatarPath, string? BackgroundPath, int NextAvatarIndex, int NextBackgroundIndex);
    private sealed record MissingArtistArtworkPlan(
        string DriverTarget,
        IReadOnlyDictionary<string, int> MissingCounts,
        IReadOnlySet<long> ArtistIds,
        IReadOnlyList<string> Warnings);
    private sealed record PlexArtworkUpdates(bool AvatarUpdated, bool BackgroundUpdated)
    {
        public bool HasAnyUpdate => AvatarUpdated || BackgroundUpdated;
    }
    private sealed record ArtworkCandidate(string Identity, string Source, string LocalPath, string? ContentHash = null)
    {
        public static ArtworkCandidate FromLocal(string path, string identity, string source, string? contentHash = null)
            => new(identity, source, path, contentHash);
    }
    private sealed record PushOutcome(
        IReadOnlyList<ArtistTargetResult> Targets,
        IReadOnlyList<string> Warnings)
    {
        /// <summary>
        /// At least one server genuinely stored new content. Keyed off the updated fields rather than
        /// the target outcome alone, so a rescan-only entry can never be counted as an update.
        /// </summary>
        public bool Updated => Targets.Any(static target =>
            target.Outcome == ArtistTargetOutcome.Updated
            && target.UpdatedFieldList.Any(static name => name != ArtistTargetFields.Scan));

        /// <summary>
        /// A server genuinely failed. Declared capability limits (for example Navidrome's read-only
        /// biography) are recorded as limitations and must never degrade the artist-level outcome.
        /// </summary>
        public bool HasFailures => Targets.Any(static target => target.Outcome == ArtistTargetOutcome.Failed);

        /// <summary>The only thing that happened was a Navidrome rescan notification: nothing was written.</summary>
        public bool ScanOnly => Targets.Count > 0
            && Targets.All(static target =>
                target.Outcome != ArtistTargetOutcome.Updated
                && target.UpdatedFieldList.Contains(ArtistTargetFields.Scan));

        public IReadOnlyList<string> UpdatedTargets => Targets
            .Where(static target => target.Outcome == ArtistTargetOutcome.Updated)
            .Select(static target => target.Target)
            .ToList();
    }

    private sealed record PushMetadataRequest(
        long LocalArtistId,
        PlatformAuthState Auth,
        string ArtistName,
        IReadOnlyList<string> Targets,
        string? AvatarPath,
        string? BackgroundPath,
        string? Biography)
    {
        public PushMetadataRequest(
            long localArtistId,
            PlatformAuthState auth,
            string artistName,
            string target,
            string? avatarPath,
            string? backgroundPath,
            string? biography)
            : this(localArtistId, auth, artistName, NormalizeTargets(null, target), avatarPath, backgroundPath, biography)
        {
        }
    }
    /// <summary>
    /// Per-target write flags for one artist. Success and failure are derived from the typed
    /// <see cref="ArtistTargetResult"/> values instead of being tracked here, so a Navidrome rescan
    /// can never be mistaken for a metadata write.
    /// </summary>
    private sealed class PushUpdateAccumulator
    {
        public bool AvatarUpdated { get; set; }
        public bool BackgroundUpdated { get; set; }
        public bool BioUpdated { get; set; }
    }
}

public sealed class MetadataUpdaterRunRequest
{
    public long? ArtistId { get; set; }
    public string? Source { get; set; }
    public string? Target { get; set; }
    public List<string>? Targets { get; set; }
    public int? IntervalDays { get; set; }
    public bool? IncludeAvatar { get; set; }
    public bool? IncludeBackground { get; set; }
    public bool? IncludeBio { get; set; }
    public bool? IncludePopularSongs { get; set; }
    public bool? IncludeDiscography { get; set; }
    public bool? OcrTextArtBlockingEnabled { get; set; }
    public bool? SaveArtistFolderImage { get; set; }
    public bool? IncludeAllArtists { get; set; }
    public bool? Force { get; set; }
    public long? FolderId { get; set; }
    public bool? MissingArtistArtworkOnly { get; set; }
}

public sealed class ManualPushRegistrationRequest
{
    public long ArtistId { get; set; }
    public string ArtistName { get; set; } = string.Empty;
    public string? Source { get; set; }
    public string? Target { get; set; }
    public List<string>? Targets { get; set; }
    public bool IncludeAvatar { get; set; }
    public bool IncludeBackground { get; set; }
    public bool IncludeBio { get; set; }
    public bool IncludePopularSongs { get; set; }
    public bool OcrTextArtBlockingEnabled { get; set; } = true;
    public int? IntervalDays { get; set; }
}

public sealed class MetadataUpdaterState
{
    public int Version { get; set; } = 1;
    public List<MetadataUpdaterTrackedArtist> Artists { get; set; } = new();
}

public sealed class MetadataUpdaterTrackedArtist
{
    public long ArtistId { get; set; }
    public string ArtistName { get; set; } = string.Empty;
    public string Source { get; set; } = "auto";
    public string Target { get; set; } = "plex";
    public List<string> Targets { get; set; } = new() { "plex" };
    public bool IncludeAvatar { get; set; } = true;
    public bool IncludeBackground { get; set; } = true;
    public bool IncludeBio { get; set; }
    public bool IncludePopularSongs { get; set; }
    public bool OcrTextArtBlockingEnabled { get; set; } = true;
    public bool SaveArtistFolderImage { get; set; }
    public int IntervalDays { get; set; } = 30;
    public DateTimeOffset? LastPushedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int AvatarRotationIndex { get; set; }
    public int BackgroundRotationIndex { get; set; }
}

/// <summary>
/// Everything a resumed run needs from its interrupted predecessor: the per-artist outcomes that
/// rebuild the counters, and the artist set that was resolved when the run started.
/// </summary>
public sealed record ArtistRunResumeContext(
    IReadOnlyList<ArtistRunOutcomeRecord>? Outcomes = null,
    IReadOnlyList<long>? TargetArtistIds = null);

/// <summary>
/// Declared write capability of a media server target. Declared rather than inferred so the UI and
/// the run maths cannot drift from reality.
/// </summary>
public enum ArtistTargetCapability
{
    /// <summary>Accepts avatar, background and biography writes.</summary>
    Full = 0,

    /// <summary>
    /// Accepts artwork writes only. Navidrome's biography is read-only, and a library rescan is a
    /// notification that Navidrome should re-read, not a metadata write.
    /// </summary>
    ArtworkOnly = 1
}

/// <summary>Result of asking one media server to update one artist.</summary>
public enum ArtistTargetOutcome
{
    /// <summary>The server stored new content.</summary>
    Updated = 0,

    /// <summary>The server was reachable and already had the requested content.</summary>
    Unchanged = 1,

    /// <summary>The artist does not exist on that server.</summary>
    NotFound = 2,

    /// <summary>The target is selected but has no usable credentials or URL configured.</summary>
    NotConfigured = 3,

    /// <summary>The write genuinely failed and is actionable.</summary>
    Failed = 4
}

public static class ArtistTargetFields
{
    public const string Avatar = "avatar";
    public const string Background = "background";
    public const string Biography = "biography";
    public const string Scan = "scan";
}

/// <summary>Per-server truth for a single artist, so one flat warning list is no longer needed.</summary>
public sealed record ArtistTargetResult(
    string Target,
    ArtistTargetOutcome Outcome,
    IReadOnlyList<string>? UpdatedFields = null,
    IReadOnlyList<string>? Limitations = null,
    string? Error = null)
{
    public IReadOnlyList<string> UpdatedFieldList => UpdatedFields ?? Array.Empty<string>();
    public IReadOnlyList<string> LimitationList => Limitations ?? Array.Empty<string>();
    public bool IsLimitation => LimitationList.Count > 0;

    public static ArtistTargetResult NotConfigured(string target, string reason)
        => new(target, ArtistTargetOutcome.NotConfigured, Limitations: new[] { reason });

    public static ArtistTargetResult NotFound(string target)
        => new(target, ArtistTargetOutcome.NotFound);
}

public sealed record MetadataUpdaterStatusSnapshot(
    bool Running,
    string Phase,
    string? Message,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int TotalArtists,
    int ProcessedArtists,
    int SuccessfulArtists,
    int FailedArtists,
    int SkippedArtists,
    string? CurrentArtist)
{
    public Dictionary<string, int> SkipReasons { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Artists where at least one server stored new content and at least one genuinely failed.</summary>
    public int PartialArtists { get; init; }

    /// <summary>Artists with no usable upstream metadata. Expected for some artists, not an error.</summary>
    public int NoMetadataArtists { get; init; }

    /// <summary>Artists carried over from a previous interrupted run, so the totals can be reconciled.</summary>
    public int ResumedArtists { get; init; }

    /// <summary>Per-server results, aggregated across the run for the selected targets.</summary>
    public IReadOnlyList<ArtistTargetRunSummary> Targets { get; init; } = Array.Empty<ArtistTargetRunSummary>();

    public static MetadataUpdaterStatusSnapshot Idle()
        => new(
            Running: false,
            Phase: "Idle",
            Message: null,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            TotalArtists: 0,
            ProcessedArtists: 0,
            SuccessfulArtists: 0,
            FailedArtists: 0,
            SkippedArtists: 0,
            CurrentArtist: null);
}

/// <summary>Aggregated per-server result for a whole run.</summary>
public sealed record ArtistTargetRunSummary(
    string Target,
    ArtistTargetCapability Capability,
    int Updated,
    int Unchanged,
    int NotFound,
    int NotConfigured,
    int Failed,
    IReadOnlyList<string>? Limitations = null)
{
    public IReadOnlyList<string> LimitationList => Limitations ?? Array.Empty<string>();
}
