using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeezSpoTag.Integrations.Jellyfin;
using DeezSpoTag.Integrations.Navidrome;
using DeezSpoTag.Integrations.Plex;
using DeezSpoTag.Services.Library;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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
    private const string PlexTarget = "plex";
    private const string JellyfinTarget = "jellyfin";
    private const string NavidromeTarget = "navidrome";
    private const string LegacyBothTargets = "both";
    private const string AvatarSlot = "avatar";
    private const string BackgroundSlot = "background";

    private readonly LibraryRepository _libraryRepository;
    private readonly PlatformAuthService _platformAuthService;
    private readonly PlexApiClient _plexClient;
    private readonly JellyfinApiClient _jellyfinClient;
    private readonly NavidromeApiClient _navidromeClient;
    private readonly ArtistPopularSongsSyncService _artistPopularSongsSyncService;
    private readonly ArtistArtworkCatalogService _artistArtworkCatalog;
    private readonly LibraryConfigStore _configStore;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ArtistMetadataUpdaterService> _logger;
    private readonly DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator _workCoordinator;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _statusLock = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _statePath;
    private MetadataUpdaterStatusSnapshot _status = MetadataUpdaterStatusSnapshot.Idle();
    private Task? _activeRun;

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
        _environment = environment;
        _logger = logger;
        _workCoordinator = serviceProvider.GetRequiredService<DeezSpoTag.Services.Runtime.BackgroundWorkCoordinator>();
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
        => RunAndWaitAsync(request, isAutomatic, progress: null, completedArtistIds: null, cancellationToken);

    public async Task<bool> RunAndWaitAsync(
        MetadataUpdaterRunRequest request,
        bool isAutomatic,
        IProgress<ArtistMetadataOperationProgress>? progress,
        IReadOnlySet<long>? completedArtistIds,
        CancellationToken cancellationToken)
    {
        Task run;
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            if (_activeRun is { IsCompleted: false })
            {
                return false;
            }
            run = _workCoordinator.RunHeavyWorkAsync(
                token => RunInternalAsync(
                    request ?? new MetadataUpdaterRunRequest(),
                    isAutomatic,
                    progress,
                    completedArtistIds,
                    token),
                cancellationToken);
            _activeRun = run;
        }
        finally
        {
            _runGate.Release();
        }

        await run;
        return true;
    }

    private async Task RunInternalAsync(
        MetadataUpdaterRunRequest request,
        bool isAutomatic,
        IProgress<ArtistMetadataOperationProgress>? progress,
        IReadOnlySet<long>? completedArtistIds,
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
            var runPreparation = await PrepareRunAsync(request, auth, cancellationToken);
            if (runPreparation is null)
            {
                return;
            }

            var state = runPreparation.State;
            var allCandidates = runPreparation.Candidates;
            var counters = new MetadataRunCounters(allCandidates.Count);

            foreach (var tracked in allCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (completedArtistIds is not null && completedArtistIds.Contains(tracked.ArtistId))
                {
                    continue;
                }

                counters.ProcessedArtists++;
                UpdateProgressStatus(tracked.ArtistName, counters);

                var outcome = await ProcessTrackedArtistAsync(
                    tracked,
                    request,
                    auth,
                    runPreparation.NowUtc,
                    cancellationToken);
                counters.Apply(outcome);
                UpdateCounterStatus(counters);
                await SaveStateAsync(state, cancellationToken);
                progress?.Report(new ArtistMetadataOperationProgress(
                    counters.ProcessedArtists,
                    counters.TotalArtists,
                    tracked.ArtistName,
                    tracked.ArtistId));
            }

            UpdateStatus(_status with
            {
                Running = false,
                CurrentArtist = null,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Phase = "Metadata update completed",
                Message = BuildCompletionMessage(counters),
                SkipReasons = counters.SkipReasonsSnapshot()
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

    private async Task<PreparedRunState?> PrepareRunAsync(
        MetadataUpdaterRunRequest request,
        PlatformAuthState auth,
        CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken);
        var missingArtistIds = request.MissingArtistArtworkOnly == true
            ? await SeedMissingArtistArtworkCandidatesAsync(state, request, auth, cancellationToken)
            : null;
        if (request.MissingArtistArtworkOnly != true
            && (request.IncludeAllArtists == true || state.Artists.Count == 0))
        {
            await SeedArtistsFromLibraryAsync(state, request, cancellationToken);
            await SaveStateAsync(state, cancellationToken);
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

    private async Task<ArtistProcessingOutcome> ProcessTrackedArtistAsync(
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
            return ArtistProcessingOutcome.SkippedNotDue;
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
            var updated = await PushTrackedArtistMetadataAsync(tracked, auth, cancellationToken);
            return updated
                ? ArtistProcessingOutcome.Succeeded
                : ArtistProcessingOutcome.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Metadata updater failed for artist {ArtistId}", tracked.ArtistId);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "error",
                $"Metadata updater failed for {tracked.ArtistName}: {ex.Message}"));
            return ArtistProcessingOutcome.Failed;
        }
    }

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
    }

    private async Task<bool> PushTrackedArtistMetadataAsync(
        MetadataUpdaterTrackedArtist tracked,
        PlatformAuthState auth,
        CancellationToken cancellationToken)
    {
        var artist = await _libraryRepository.GetArtistAsync(tracked.ArtistId, cancellationToken);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                $"Metadata updater skipped artist {tracked.ArtistId}: artist missing."));
            return false;
        }

        tracked.ArtistName = artist.Name;
        var policy = await _libraryRepository.GetArtistMetadataPolicyAsync(artist.Id, cancellationToken);
        var popularSongsSynced = await SyncPopularSongsIfRequestedAsync(
            tracked,
            artist.Id,
            artist.Name,
            cancellationToken);
        var source = NormalizeMetadataSource(tracked.Source);
        var resolved = await ResolveArtistMetadataAsync(
            artist.Id,
            artist.Name,
            source,
            tracked.IncludeBio,
            cancellationToken);
        if (resolved is null)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                $"Metadata updater failed for {artist.Name}: {source} metadata unavailable."));
            return popularSongsSynced;
        }

        var prepared = await PrepareVisualsAsync(tracked, resolved.Candidates, cancellationToken);
        await UpdateManagedArtistVisualsAsync(artist.Id, prepared, cancellationToken);

        if (policy.SyncBlocked)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Metadata updater skipped server sync for {artist.Name} because artist sync is blocked."));
            tracked.UpdatedAtUtc = DateTimeOffset.UtcNow;
            return true;
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

        if (!pushed.Updated && !popularSongsSynced)
        {
            var warningText = pushed.Warnings.Count == 0
                ? "No server metadata was updated."
                : string.Join(" ", pushed.Warnings);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                $"Metadata updater could not push {artist.Name}: {warningText}"));
            return false;
        }

        tracked.LastPushedAtUtc = DateTimeOffset.UtcNow;
        tracked.UpdatedAtUtc = DateTimeOffset.UtcNow;
        tracked.AvatarRotationIndex = prepared.NextAvatarIndex;
        tracked.BackgroundRotationIndex = prepared.NextBackgroundIndex;
        foreach (var target in ResolveTrackedTargets(tracked))
        {
            await _libraryRepository.UpsertArtistServerSyncStateAsync(
                new ArtistServerSyncStateUpsertInput(
                    artist.Id,
                    target,
                    DateTimeOffset.UtcNow,
                    target == NavidromeTarget && pushed.Warnings.Any(warning => warning.Contains("Navidrome artist metadata sync is not supported", StringComparison.OrdinalIgnoreCase))
                        ? null
                        : DateTimeOffset.UtcNow,
                    ComputeFileHashOrNull(prepared.AvatarPath),
                    ComputeFileHashOrNull(prepared.BackgroundPath),
                    ComputeTextHashOrNull(biography),
                    tracked.AvatarRotationIndex,
                    tracked.BackgroundRotationIndex,
                    pushed.Updated ? "updated" : "skipped",
                    pushed.Warnings.Count == 0 ? null : string.Join(" ", pushed.Warnings)),
                cancellationToken);
        }
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            popularSongsSynced
                ? $"Metadata updater pushed {artist.Name} and synced popular songs to {string.Join(", ", ResolveTrackedTargets(tracked))}."
                : $"Metadata updater pushed {artist.Name} to {string.Join(", ", ResolveTrackedTargets(tracked))}."));
        return true;
    }

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

    private static string BuildCompletionMessage(MetadataRunCounters counters)
    {
        var skippedSuffix = counters.SkippedArtists > 0 && counters.SkipReasons.Count > 0
            ? $": {string.Join(", ", counters.SkipReasons.Select(pair => $"{pair.Value} {FormatSkipReason(pair.Key)}"))}"
            : string.Empty;
        return $"Processed {counters.ProcessedArtists} artists ({counters.SuccessfulArtists} success, {counters.FailedArtists} failed, {counters.SkippedArtists} skipped{skippedSuffix}).";
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
        var counts = missingByTarget.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Count,
            StringComparer.OrdinalIgnoreCase);
        return new MissingArtistArtworkPlan(driverTarget, counts, driverIds, warnings);
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
            tracked.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private async Task<ResolvedArtistMetadata?> ResolveArtistMetadataAsync(
        long artistId,
        string artistName,
        string source,
        bool includeBiography,
        CancellationToken cancellationToken)
    {
        var normalizedSource = NormalizeMetadataSource(source);
        var artwork = await _artistArtworkCatalog.GetAsync(artistId, cancellationToken);
        var candidates = artwork.Visuals
            .Where(item => normalizedSource == MetadataSourceAuto
                || string.Equals(item.Source, normalizedSource, StringComparison.OrdinalIgnoreCase)
                || normalizedSource == MetadataSourceApple
                   && string.Equals(item.Source, "itunes", StringComparison.OrdinalIgnoreCase))
            .Select(item => ArtworkCandidate.FromLocal(item.Path, item.Identity, item.Source))
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

    private async Task<PreparedVisuals> PrepareVisualsAsync(
        MetadataUpdaterTrackedArtist tracked,
        IReadOnlyList<ArtworkCandidate> sourceCandidates,
        CancellationToken cancellationToken)
    {
        var managedRoot = Path.Join(
            AppDataPaths.GetDataRoot(_environment),
            "library-artist-images",
            SpotifyPlatform,
            "artists",
            tracked.ArtistId.ToString());

        Directory.CreateDirectory(managedRoot);

        var sourceCandidatesNormalized = NormalizeCandidates(sourceCandidates);

        var avatarSlot = ResolveSlotCandidate(managedRoot, AvatarSlot);
        var backgroundSlot = ResolveSlotCandidate(managedRoot, BackgroundSlot);
        var avatarCandidates = BuildSlotCandidates(avatarSlot, AvatarSlot, sourceCandidatesNormalized);
        var backgroundCandidates = BuildSlotCandidates(backgroundSlot, BackgroundSlot, sourceCandidatesNormalized);

        var nextAvatarIndex = tracked.AvatarRotationIndex;
        var nextBackgroundIndex = tracked.BackgroundRotationIndex;
        string? avatarPath = avatarSlot;
        string? backgroundPath = backgroundSlot;
        ArtworkCandidate? selectedAvatarCandidate = null;

        if (tracked.IncludeAvatar)
        {
            var avatarSelection = await RotateAndMaterializeSlotAsync(
                avatarCandidates,
                tracked.AvatarRotationIndex,
                managedRoot,
                AvatarSlot,
                excludedIdentity: null,
                textArtBlockingEnabled: tracked.OcrTextArtBlockingEnabled,
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
            var backgroundSelection = await RotateAndMaterializeSlotAsync(
                backgroundCandidates,
                tracked.BackgroundRotationIndex,
                managedRoot,
                BackgroundSlot,
                selectedAvatarCandidate?.Identity,
                textArtBlockingEnabled: tracked.OcrTextArtBlockingEnabled,
                cancellationToken);
            backgroundPath = backgroundSelection.Path;
            if (!string.IsNullOrWhiteSpace(backgroundPath))
            {
                nextBackgroundIndex = (tracked.BackgroundRotationIndex + 1) % Math.Max(1, backgroundCandidates.Count);
            }
        }

        return new PreparedVisuals(avatarPath, backgroundPath, nextAvatarIndex, nextBackgroundIndex);
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
        string? excludedIdentity,
        bool textArtBlockingEnabled,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return (ResolveSlotCandidate(managedRoot, slot), null);
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

            if (!string.IsNullOrWhiteSpace(excludedIdentity)
                && candidates.Count > 1
                && string.Equals(selected.Identity, excludedIdentity, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var materialized = await TryMaterializeSlotCandidateAsync(selected, managedRoot, slot, textArtBlockingEnabled, cancellationToken);
            if (!string.IsNullOrWhiteSpace(materialized.Path))
            {
                return materialized;
            }
        }

        return (ResolveSlotCandidate(managedRoot, slot), null);
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

        await _libraryRepository.UpsertArtistArtworkCacheAsync(
            new ArtistArtworkCacheUpsertInput(
                artistId,
                slot,
                selected.Identity,
                selected.Source,
                null,
                localPath,
                ComputeFileHashOrNull(localPath),
                null,
                null,
                "heuristic",
                null,
                true,
                false),
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
                Identity = string.IsNullOrWhiteSpace(candidate.Identity) ? fullPath : candidate.Identity
            };
        }

        return null;
    }

    private static List<ArtworkCandidate> NormalizeCandidates(IEnumerable<ArtworkCandidate> candidates)
        => candidates
            .Select(NormalizeCandidate)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .GroupBy(candidate => candidate.Identity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    private async Task<PushOutcome> PushArtistMetadataAsync(
        PushMetadataRequest request,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var updates = new PushUpdateAccumulator();
        if (request.Targets.Contains(PlexTarget, StringComparer.OrdinalIgnoreCase))
        {
            await PushToPlexAsync(request, updates, warnings, cancellationToken);
        }

        if (request.Targets.Contains(JellyfinTarget, StringComparer.OrdinalIgnoreCase))
        {
            await PushToJellyfinAsync(request, updates, warnings, cancellationToken);
        }

        if (request.Targets.Contains(NavidromeTarget, StringComparer.OrdinalIgnoreCase))
        {
            await PushToNavidromeAsync(request, updates, warnings, cancellationToken);
        }

        return new PushOutcome(updates.HasAnyUpdate, warnings);
    }

    private async Task PushToNavidromeAsync(
        PushMetadataRequest request,
        PushUpdateAccumulator updates,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var navidrome = request.Auth.Navidrome;
        if (navidrome is null
            || string.IsNullOrWhiteSpace(navidrome.Url)
            || string.IsNullOrWhiteSpace(navidrome.Username)
            || string.IsNullOrWhiteSpace(navidrome.Password))
        {
            warnings.Add("Navidrome is not configured.");
            return;
        }

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
                warnings.Add("Navidrome artist not found.");
                return;
            }

            if (request.LocalArtistId > 0)
            {
                await _libraryRepository.UpsertArtistSourceIdAsync(request.LocalArtistId, NavidromeTarget, artistIds[0], cancellationToken);
            }

            var navidromeImagePath = HasLocalFile(request.AvatarPath)
                ? request.AvatarPath
                : HasLocalFile(request.BackgroundPath)
                    ? request.BackgroundPath
                    : null;

            if (HasLocalFile(navidromeImagePath))
            {
                foreach (var artistId in artistIds)
                {
                    updates.AvatarUpdated = await _navidromeClient.UpdateArtistImageFromFileAsync(
                        navidrome.Url,
                        navidrome.Username,
                        navidrome.Password,
                        artistId,
                        navidromeImagePath!,
                        null,
                        cancellationToken) || updates.AvatarUpdated;
                }
            }

            if (!string.IsNullOrWhiteSpace(request.Biography))
            {
                warnings.Add("Navidrome biography is read-only and was not updated.");
            }

            var scanStarted = await _navidromeClient.StartScanAsync(
                navidrome.Url,
                navidrome.Username,
                navidrome.Password,
                cancellationToken);
            if (scanStarted)
            {
                updates.NavidromeScanTriggered = true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Metadata updater Navidrome scan request failed for {Artist}", request.ArtistName);
            warnings.Add("Navidrome scan request failed.");
        }
    }

    private async Task PushToPlexAsync(
        PushMetadataRequest request,
        PushUpdateAccumulator updates,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!TryGetPlexConnection(request.Auth.Plex, out var plexUrl, out var plexToken))
        {
            warnings.Add("Plex is not configured.");
            return;
        }

        try
        {
            var locations = await _plexClient.FindArtistLocationsAsync(plexUrl, plexToken, request.ArtistName, cancellationToken);
            if (locations.Count == 0)
            {
                warnings.Add("Plex artist not found.");
                return;
            }

            await UpsertPlexSourceIdAsync(request, locations[0], cancellationToken);
            foreach (var location in locations)
            {
                var artworkUpdates = await UpdatePlexArtworkAsync(request, plexUrl, plexToken, location, cancellationToken);
                updates.AvatarUpdated = artworkUpdates.AvatarUpdated || updates.AvatarUpdated;
                updates.BackgroundUpdated = artworkUpdates.BackgroundUpdated || updates.BackgroundUpdated;
                await TryLockPlexArtworkAsync(plexUrl, plexToken, location, artworkUpdates, warnings, cancellationToken);
                await UpdatePlexBiographyAsync(request, plexUrl, plexToken, location, updates, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Metadata updater Plex push failed for {Artist}", request.ArtistName);
            warnings.Add("Plex update failed.");
        }
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

    private async Task PushToJellyfinAsync(
        PushMetadataRequest request,
        PushUpdateAccumulator updates,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var jellyfin = request.Auth.Jellyfin;
        if (jellyfin is null || string.IsNullOrWhiteSpace(jellyfin.Url) || string.IsNullOrWhiteSpace(jellyfin.ApiKey))
        {
            warnings.Add("Jellyfin is not configured.");
            return;
        }

        try
        {
            var artistIds = await _jellyfinClient.FindArtistIdsAsync(jellyfin.Url, jellyfin.ApiKey, request.ArtistName, cancellationToken);
            if (artistIds.Count == 0)
            {
                warnings.Add("Jellyfin artist not found.");
                return;
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
            warnings.Add("Jellyfin update failed.");
        }
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
            return !LikelyContainsOverlayText(image);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Artwork text inspection failed for {Path}", imagePath);
            }
            return true;
        }
    }

    private static bool LikelyContainsOverlayText(Image<Rgba32> image)
    {
        const int maxWidth = 384;
        using var sampled = image.CloneAs<Rgba32>();
        if (sampled.Width > maxWidth)
        {
            var resizedHeight = Math.Max(1, (int)Math.Round(sampled.Height * (maxWidth / (double)sampled.Width)));
            sampled.Mutate(ctx => ctx.Resize(maxWidth, resizedHeight));
        }

        if (sampled.Width < 48 || sampled.Height < 48)
        {
            return false;
        }

        var topBandHeight = Math.Max(8, (int)Math.Round(sampled.Height * 0.22));
        var bottomBandStart = Math.Max(0, sampled.Height - topBandHeight);
        var middleStart = topBandHeight;
        var middleHeight = Math.Max(8, bottomBandStart - middleStart);

        var top = AnalyzeBand(sampled, 0, topBandHeight);
        var middle = AnalyzeBand(sampled, middleStart, middleHeight);
        var bottom = AnalyzeBand(sampled, bottomBandStart, sampled.Height - bottomBandStart);

        return IsTextHeavyBand(top, middle) || IsTextHeavyBand(bottom, middle);
    }

    private static ArtworkBandAnalysis AnalyzeBand(Image<Rgba32> image, int startRow, int height)
    {
        var yStart = Math.Max(1, startRow);
        var yEnd = Math.Min(image.Height - 1, startRow + Math.Max(1, height));
        if (yEnd <= yStart)
        {
            return new ArtworkBandAnalysis(0, 0);
        }

        var totalPixels = 0;
        var edgePixels = 0;
        var transitions = 0;
        var rows = 0;

        for (var y = yStart; y < yEnd; y++)
        {
            rows++;
            var previousEdge = false;
            var rowTransitions = 0;

            for (var x = 1; x < image.Width - 1; x++)
            {
                var current = image[x, y];
                var right = image[x + 1, y];
                var down = image[x, y + 1];
                var edge = Math.Abs(GetLuminance(current) - GetLuminance(right))
                           + Math.Abs(GetLuminance(current) - GetLuminance(down)) >= 95;

                totalPixels++;
                if (edge)
                {
                    edgePixels++;
                }

                if (x > 1 && edge != previousEdge)
                {
                    rowTransitions++;
                }

                previousEdge = edge;
            }

            transitions += rowTransitions;
        }

        if (totalPixels <= 0 || rows <= 0)
        {
            return new ArtworkBandAnalysis(0, 0);
        }

        return new ArtworkBandAnalysis(
            edgePixels / (double)totalPixels,
            transitions / ((double)rows * Math.Max(1, image.Width - 2)));
    }

    private static bool IsTextHeavyBand(ArtworkBandAnalysis band, ArtworkBandAnalysis middle)
    {
        return band.EdgeDensity >= 0.135
            && band.TransitionDensity >= 0.18
            && band.EdgeDensity >= Math.Max(0.04, middle.EdgeDensity) * 1.45
            && band.TransitionDensity >= Math.Max(0.06, middle.TransitionDensity) * 1.35;
    }

    private static double GetLuminance(Rgba32 pixel)
        => (pixel.R * 0.299) + (pixel.G * 0.587) + (pixel.B * 0.114);

    private static string? ResolveSlotCandidate(string managedRoot, string slot)
    {
        if (!Directory.Exists(managedRoot))
        {
            return null;
        }

        return Directory.GetFiles(managedRoot, $"{slot}.*", SearchOption.TopDirectoryOnly)
            .Where(File.Exists)
            .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
            .FirstOrDefault();
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
        Failed,
        SkippedNotDue
    }

    private static class MetadataSkipReasons
    {
        public const string NotDue = "notDue";
    }

    private sealed class MetadataRunCounters
    {
        private readonly Dictionary<string, int> _skipReasons = new(StringComparer.OrdinalIgnoreCase);

        public MetadataRunCounters(int totalArtists)
        {
            TotalArtists = totalArtists;
        }

        public int TotalArtists { get; }
        public int ProcessedArtists { get; set; }
        public int SuccessfulArtists { get; private set; }
        public int FailedArtists { get; private set; }
        public int SkippedArtists { get; private set; }
        public IReadOnlyDictionary<string, int> SkipReasons => _skipReasons;

        public void Apply(ArtistProcessingOutcome outcome)
        {
            switch (outcome)
            {
                case ArtistProcessingOutcome.Succeeded:
                    SuccessfulArtists++;
                    return;
                case ArtistProcessingOutcome.Failed:
                    FailedArtists++;
                    return;
                case ArtistProcessingOutcome.SkippedNotDue:
                    SkippedArtists++;
                    AddSkipReason(MetadataSkipReasons.NotDue);
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
    private sealed record ArtworkBandAnalysis(double EdgeDensity, double TransitionDensity);
    private sealed record MissingArtistArtworkPlan(
        string DriverTarget,
        IReadOnlyDictionary<string, int> MissingCounts,
        IReadOnlySet<long> ArtistIds,
        IReadOnlyList<string> Warnings);
    private sealed record PlexArtworkUpdates(bool AvatarUpdated, bool BackgroundUpdated)
    {
        public bool HasAnyUpdate => AvatarUpdated || BackgroundUpdated;
    }
    private sealed record ArtworkCandidate(string Identity, string Source, string LocalPath)
    {
        public static ArtworkCandidate FromLocal(string path, string identity, string source)
            => new(identity, source, path);
    }
    private sealed record PushOutcome(bool Updated, IReadOnlyList<string> Warnings);
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
    private sealed class PushUpdateAccumulator
    {
        public bool AvatarUpdated { get; set; }
        public bool BackgroundUpdated { get; set; }
        public bool BioUpdated { get; set; }
        public bool NavidromeScanTriggered { get; set; }
        public bool HasAnyUpdate => AvatarUpdated || BackgroundUpdated || BioUpdated || NavidromeScanTriggered;
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
    public bool? OcrTextArtBlockingEnabled { get; set; }
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
    public int IntervalDays { get; set; } = 30;
    public DateTimeOffset? LastPushedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int AvatarRotationIndex { get; set; }
    public int BackgroundRotationIndex { get; set; }
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
