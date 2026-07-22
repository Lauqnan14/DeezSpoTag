using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed class ArtistMetadataAutomationCoordinator : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);
    private readonly ArtistMetadataCacheRefreshService _cacheRefresh;
    private readonly ArtistMetadataUpdaterService _targetUpdate;
    private readonly UserPreferencesStore _preferences;
    private readonly ILogger<ArtistMetadataAutomationCoordinator> _logger;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _statusLock = new();
    private readonly string _statePath;
    private readonly string _legacyStatePath;
    private ArtistMetadataAutomationStatus _status = ArtistMetadataAutomationStatus.Idle();
    private Task? _activeOperation;

    public ArtistMetadataAutomationCoordinator(
        ArtistMetadataCacheRefreshService cacheRefresh,
        ArtistMetadataUpdaterService targetUpdate,
        UserPreferencesStore preferences,
        IWebHostEnvironment environment,
        ILogger<ArtistMetadataAutomationCoordinator> logger)
    {
        _cacheRefresh = cacheRefresh;
        _targetUpdate = targetUpdate;
        _preferences = preferences;
        _logger = logger;
        _statePath = Path.Join(AppDataPaths.GetDataRoot(environment), "library-artist-images", "metadata-automation-state.json");
        _legacyStatePath = Path.Join(AppDataPaths.GetDataRoot(environment), "library-artist-images", "spotify", "metadata-updater-state.json");
    }

    public ArtistMetadataAutomationStatus GetStatus()
    {
        lock (_statusLock)
        {
            return _status with { TargetUpdate = _targetUpdate.GetStatus() };
        }
    }

    public Task<bool> EnqueueCacheRefreshAsync(ArtistMetadataCacheRefreshRequest request, CancellationToken cancellationToken)
        => EnqueueAsync(
            "cache-refresh",
            async token => string.IsNullOrWhiteSpace(
                (await RunCacheRefreshAsync(request, automatic: false, token)).Error),
            cancellationToken);

    public Task<bool> EnqueueTargetUpdateAsync(MetadataUpdaterRunRequest request, CancellationToken cancellationToken)
        => EnqueueAsync("target-update", token => RunTargetUpdateAsync(request, automatic: false, token), cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunScheduledOperationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Artist metadata schedule evaluation failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task RunScheduledOperationsAsync(CancellationToken cancellationToken)
    {
        if (_activeOperation is { IsCompleted: false })
        {
            return;
        }

        var preferences = await _preferences.LoadAsync();
        var state = await LoadStateAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var cacheDue = IsDue(state.LastCacheRefreshUtc, preferences.MetadataCacheRefreshIntervalDays, now);
        var updateDue = IsDue(state.LastTargetUpdateUtc, preferences.MetadataTargetUpdateIntervalDays, now);
        UpdateScheduleStatus(state, preferences, now);
        if (!cacheDue && !updateDue)
        {
            return;
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            if (cacheDue)
            {
                var result = await RunCacheRefreshAsync(BuildCacheRequest(preferences), automatic: true, cancellationToken);
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    return;
                }
                state.LastCacheRefreshUtc = DateTimeOffset.UtcNow;
                await SaveStateAsync(state, cancellationToken);
            }

            if (updateDue)
            {
                var ran = await RunTargetUpdateAsync(BuildTargetRequest(preferences), automatic: true, cancellationToken);
                if (ran)
                {
                    state.LastTargetUpdateUtc = DateTimeOffset.UtcNow;
                    await SaveStateAsync(state, cancellationToken);
                }
            }
        }
        finally
        {
            _operationGate.Release();
            UpdateScheduleStatus(state, preferences, DateTimeOffset.UtcNow);
        }
    }

    private async Task<bool> EnqueueAsync(
        string operation,
        Func<CancellationToken, Task<bool>> run,
        CancellationToken cancellationToken)
    {
        if (!await _operationGate.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        try
        {
            if (_activeOperation is { IsCompleted: false })
            {
                _operationGate.Release();
                return false;
            }

            lock (_statusLock)
            {
                _status = _status with { ActiveOperation = operation };
            }
            _activeOperation = RunManualOperationAsync(operation, run);
            return true;
        }
        catch
        {
            _operationGate.Release();
            throw;
        }
    }

    private async Task RunManualOperationAsync(
        string operation,
        Func<CancellationToken, Task<bool>> run)
    {
        try
        {
            var completed = await run(CancellationToken.None);
            if (!completed)
            {
                return;
            }

            var state = await LoadStateAsync(CancellationToken.None);
            if (operation == "cache-refresh")
            {
                state.LastCacheRefreshUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                state.LastTargetUpdateUtc = DateTimeOffset.UtcNow;
            }
            await SaveStateAsync(state, CancellationToken.None);
            var preferences = await _preferences.LoadAsync();
            UpdateScheduleStatus(state, preferences, DateTimeOffset.UtcNow);
        }
        finally
        {
            _operationGate.Release();
            lock (_statusLock)
            {
                _status = _status with { ActiveOperation = null };
            }
        }
    }

    private async Task<ArtistMetadataCacheRefreshResult> RunCacheRefreshAsync(
        ArtistMetadataCacheRefreshRequest request,
        bool automatic,
        CancellationToken cancellationToken)
    {
        UpdateCacheStatus(new ArtistMetadataCacheStatus(true, automatic, "Refreshing artist metadata cache", null, 0, 0, null, DateTimeOffset.UtcNow, null));
        var progress = new Progress<ArtistMetadataOperationProgress>(value =>
            UpdateCacheStatus(GetStatus().CacheRefresh with
            {
                ProcessedArtists = value.Processed,
                TotalArtists = value.Total,
                CurrentArtist = value.CurrentArtist
            }));
        var result = await _cacheRefresh.RefreshAsync(request, progress, cancellationToken);
        UpdateCacheStatus(GetStatus().CacheRefresh with
        {
            Running = false,
            Phase = result.Error is null ? "Cache refresh completed" : "Cache refresh failed",
            Message = result.Error ?? $"{result.Succeeded} succeeded, {result.Failed} failed.",
            ProcessedArtists = result.Total,
            TotalArtists = result.Total,
            CurrentArtist = null,
            CompletedAtUtc = DateTimeOffset.UtcNow
        });
        return result;
    }

    private async Task<bool> RunTargetUpdateAsync(
        MetadataUpdaterRunRequest request,
        bool automatic,
        CancellationToken cancellationToken)
    {
        lock (_statusLock)
        {
            _status = _status with { ActiveOperation = "target-update" };
        }
        return await _targetUpdate.RunAndWaitAsync(request, automatic, cancellationToken);
    }

    private void UpdateCacheStatus(ArtistMetadataCacheStatus status)
    {
        lock (_statusLock)
        {
            _status = _status with { CacheRefresh = status, ActiveOperation = status.Running ? "cache-refresh" : _status.ActiveOperation };
        }
    }

    private void UpdateScheduleStatus(ArtistMetadataAutomationState state, UserPreferencesDto preferences, DateTimeOffset now)
    {
        lock (_statusLock)
        {
            _status = _status with
            {
                LastCacheRefreshUtc = state.LastCacheRefreshUtc,
                LastTargetUpdateUtc = state.LastTargetUpdateUtc,
                NextCacheRefreshUtc = NextDue(state.LastCacheRefreshUtc, preferences.MetadataCacheRefreshIntervalDays, now),
                NextTargetUpdateUtc = NextDue(state.LastTargetUpdateUtc, preferences.MetadataTargetUpdateIntervalDays, now)
            };
        }
    }

    private static ArtistMetadataCacheRefreshRequest BuildCacheRequest(UserPreferencesDto preferences)
        => new(
            null,
            ParseFolderId(preferences.MetadataUpdaterFolderId),
            preferences.MetadataUpdaterSource,
            preferences.MetadataUpdaterIncludePopularSongs);

    private static MetadataUpdaterRunRequest BuildTargetRequest(UserPreferencesDto preferences)
        => new()
        {
            Source = preferences.MetadataUpdaterSource,
            Targets = preferences.MetadataUpdaterTargets,
            FolderId = ParseFolderId(preferences.MetadataUpdaterFolderId),
            IncludeAvatar = preferences.MetadataUpdaterIncludeAvatar,
            IncludeBackground = preferences.MetadataUpdaterIncludeBackground,
            IncludeBio = preferences.MetadataUpdaterIncludeBio,
            IncludePopularSongs = preferences.MetadataUpdaterIncludePopularSongs,
            MissingArtistArtworkOnly = preferences.MetadataUpdaterMissingArtistArtworkOnly,
            OcrTextArtBlockingEnabled = preferences.MetadataUpdaterOcrTextArtBlocking,
            IncludeAllArtists = true,
            Force = true
        };

    private static long? ParseFolderId(string? value) => long.TryParse(value, out var id) && id > 0 ? id : null;
    private static bool IsDue(DateTimeOffset? lastRun, int intervalDays, DateTimeOffset now)
        => intervalDays > 0 && (!lastRun.HasValue || now - lastRun.Value >= TimeSpan.FromDays(intervalDays));
    private static DateTimeOffset? NextDue(DateTimeOffset? lastRun, int intervalDays, DateTimeOffset now)
        => intervalDays <= 0 ? null : lastRun?.AddDays(intervalDays) ?? now;

    private async Task<ArtistMetadataAutomationState> LoadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return await MigrateLegacyStateAsync(cancellationToken);
        }
        await using var stream = File.OpenRead(_statePath);
        return await JsonSerializer.DeserializeAsync<ArtistMetadataAutomationState>(stream, cancellationToken: cancellationToken)
            ?? new ArtistMetadataAutomationState();
    }

    private async Task<ArtistMetadataAutomationState> MigrateLegacyStateAsync(CancellationToken cancellationToken)
    {
        var state = new ArtistMetadataAutomationState();
        if (!File.Exists(_legacyStatePath))
        {
            return state;
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(_legacyStatePath, cancellationToken));
        if (document.RootElement.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            DateTimeOffset? latest = null;
            foreach (var artist in artists.EnumerateArray())
            {
                foreach (var property in new[] { "lastPushedAtUtc", "updatedAtUtc" })
                {
                    if (artist.TryGetProperty(property, out var value)
                        && value.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(value.GetString(), out var timestamp)
                        && (!latest.HasValue || timestamp > latest.Value))
                    {
                        latest = timestamp;
                    }
                }
            }
            state.LastCacheRefreshUtc = latest;
            state.LastTargetUpdateUtc = latest;
        }

        await SaveStateAsync(state, cancellationToken);
        return state;
    }

    private async Task SaveStateAsync(ArtistMetadataAutomationState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporary = _statePath + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
        }
        File.Move(temporary, _statePath, true);
    }
}

public sealed class ArtistMetadataAutomationState
{
    public int Version { get; set; } = 2;
    public DateTimeOffset? LastCacheRefreshUtc { get; set; }
    public DateTimeOffset? LastTargetUpdateUtc { get; set; }
}

public sealed record ArtistMetadataAutomationStatus(
    string? ActiveOperation,
    ArtistMetadataCacheStatus CacheRefresh,
    MetadataUpdaterStatusSnapshot TargetUpdate,
    DateTimeOffset? LastCacheRefreshUtc,
    DateTimeOffset? LastTargetUpdateUtc,
    DateTimeOffset? NextCacheRefreshUtc,
    DateTimeOffset? NextTargetUpdateUtc)
{
    public static ArtistMetadataAutomationStatus Idle()
        => new(null, ArtistMetadataCacheStatus.Idle(), MetadataUpdaterStatusSnapshot.Idle(), null, null, null, null);
}

public sealed record ArtistMetadataCacheStatus(
    bool Running,
    bool Automatic,
    string Phase,
    string? Message,
    int ProcessedArtists,
    int TotalArtists,
    string? CurrentArtist,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc)
{
    public static ArtistMetadataCacheStatus Idle() => new(false, false, "Idle", null, 0, 0, null, null, null);
}
