using System.Collections.Concurrent;
using System.Threading.Channels;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class WatchlistPostDownloadSyncService : BackgroundService, IWatchlistPostDownloadSyncNotifier
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10)
    ];
    private static readonly TimeSpan FollowUpDelay = TimeSpan.FromMinutes(10);
    private const int MaxFollowUpPasses = 12;

    private readonly Channel<SyncRequest> _queue = Channel.CreateUnbounded<SyncRequest>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _playlistLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SyncRequest> _pendingAfterCurrentRun = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _executionGate = new(initialCount: 2, maxCount: 2);
    private readonly IServiceProvider _serviceProvider;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ILogger<WatchlistPostDownloadSyncService> _logger;

    public WatchlistPostDownloadSyncService(
        IServiceProvider serviceProvider,
        DeezSpoTagSettingsService settingsService,
        ILogger<WatchlistPostDownloadSyncService> logger)
    {
        _serviceProvider = serviceProvider;
        _settingsService = settingsService;
        _logger = logger;
    }

    public ValueTask NotifyFinalizedAsync(
        string source,
        string playlistId,
        string trackId,
        long? destinationFolderId,
        IReadOnlyList<string>? finalFilePaths = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsWatchlistEnabled())
        {
            return ValueTask.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(source)
            || string.IsNullOrWhiteSpace(playlistId)
            || string.IsNullOrWhiteSpace(trackId))
        {
            return ValueTask.CompletedTask;
        }

        var request = new SyncRequest(
            source.Trim().ToLowerInvariant(),
            playlistId.Trim(),
            trackId.Trim(),
            destinationFolderId,
            NormalizeChangedFilePaths(finalFilePaths),
            FollowUpPass: 0);
        return _queue.Writer.WriteAsync(request, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!IsWatchlistEnabled())
            {
                continue;
            }

            _ = RunQueuedRequestAsync(request, stoppingToken);
        }
    }

    private bool IsWatchlistEnabled()
    {
        var settings = _settingsService.LoadSettings();
        return settings.WatchEnabled;
    }

    private async Task RunQueuedRequestAsync(SyncRequest request, CancellationToken stoppingToken)
    {
        await _executionGate.WaitAsync(stoppingToken);
        try
        {
            await ProcessWithRetriesAsync(request, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service shutdown.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Watchlist playlist sync worker failed for {Source}:{PlaylistId} after finalized track {TrackId}.",
                request.Source,
                request.PlaylistId,
                request.TrackId);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private async Task ProcessWithRetriesAsync(SyncRequest request, CancellationToken stoppingToken)
    {
        var key = BuildKey(request);
        var playlistLock = _playlistLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await playlistLock.WaitAsync(0, stoppingToken))
        {
            _pendingAfterCurrentRun[key] = request;
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Watchlist playlist sync already running for {Source}:{PlaylistId}; queued one follow-up pass.",
                    request.Source,
                    request.PlaylistId);
            }
            return;
        }

        try
        {
            for (var attempt = 0; attempt < RetryDelays.Length; attempt++)
            {
                await Task.Delay(RetryDelays[attempt], stoppingToken);

                var synced = await TrySyncOnceAsync(request, attempt + 1, stoppingToken);
                if (synced)
                {
                    return;
                }
            }

            _logger.LogWarning(
                "Watchlist playlist sync exhausted retries for {Source}:{PlaylistId} after finalized track {TrackId}.",
                request.Source,
                request.PlaylistId,
                request.TrackId);
            ScheduleFollowUp(request, stoppingToken);
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    ex,
                    "Watchlist playlist sync canceled for {Source}:{PlaylistId}.",
                    request.Source,
                    request.PlaylistId);
            }
        }
        finally
        {
            playlistLock.Release();
            if (_pendingAfterCurrentRun.TryRemove(key, out var pendingRequest)
                && !stoppingToken.IsCancellationRequested)
            {
                await _queue.Writer.WriteAsync(pendingRequest, stoppingToken);
            }
        }
    }

    private static string BuildKey(SyncRequest request)
        => $"{request.Source}:{request.PlaylistId}";

    private void ScheduleFollowUp(SyncRequest request, CancellationToken cancellationToken)
    {
        if (request.FollowUpPass >= MaxFollowUpPasses)
        {
            _logger.LogWarning(
                "Watchlist playlist sync stopped after {FollowUpPasses} follow-up passes for {Source}:{PlaylistId} after finalized track {TrackId}.",
                request.FollowUpPass,
                request.Source,
                request.PlaylistId,
                request.TrackId);
            return;
        }

        var followUp = request with { FollowUpPass = request.FollowUpPass + 1 };
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Watchlist playlist sync scheduled follow-up pass {FollowUpPass}/{MaxFollowUpPasses} for {Source}:{PlaylistId} after finalized track {TrackId}.",
                followUp.FollowUpPass,
                MaxFollowUpPasses,
                followUp.Source,
                followUp.PlaylistId,
                followUp.TrackId);
        }

        _ = DelayAndQueueFollowUpAsync(followUp, cancellationToken);
    }

    private async Task DelayAndQueueFollowUpAsync(SyncRequest followUp, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FollowUpDelay, cancellationToken);
            await _queue.Writer.WriteAsync(followUp, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Watchlist playlist sync failed to schedule follow-up for {Source}:{PlaylistId} after finalized track {TrackId}.",
                followUp.Source,
                followUp.PlaylistId,
                followUp.TrackId);
        }
    }

    private async Task<bool> TrySyncOnceAsync(SyncRequest request, int attempt, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<LibraryRepository>();
            if (!repository.IsConfigured)
            {
                return false;
            }

            var playlist = await FindPlaylistAsync(repository, request, cancellationToken);
            if (playlist == null)
            {
                LogPlaylistMissing(request);
                return true;
            }

            var preference = await repository.GetPlaylistWatchPreferenceAsync(
                playlist.Source,
                playlist.SourceId,
                cancellationToken);
            var effectiveRequest = ResolveEffectiveRequest(request, preference);

            if (!await VerifyLocalLibraryIngestionAsync(scope.ServiceProvider, effectiveRequest, cancellationToken))
            {
                return false;
            }
            await RefreshMediaServerAsync(scope.ServiceProvider, preference, cancellationToken);

            var watcher = scope.ServiceProvider.GetRequiredService<PlaylistWatchService>();
            var reconciliationResult = await watcher.ReconcilePlaylistAsync(
                playlist,
                cancellationToken,
                forceMediaServerSync: true);

            if (reconciliationResult.SyncResult?.Success == true)
            {
                await AddPlaylistSyncHistoryAsync(
                    repository,
                    playlist,
                    "media_sync_completed",
                    cancellationToken);
                LogSyncCompleted(request, attempt, reconciliationResult.SyncResult.SyncedTracks);
                return true;
            }

            var syncResult = reconciliationResult.SyncResult;
            await AddPlaylistSyncHistoryAsync(
                repository,
                playlist,
                syncResult is not null && IsTerminalSyncFailure(syncResult) ? "media_sync_blocked" : "media_sync_waiting",
                cancellationToken);
            LogSyncNotReady(request, attempt, syncResult?.Message ?? reconciliationResult.Message);
            return syncResult is not null && IsTerminalSyncFailure(syncResult);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(
                ex,
                "Watchlist playlist sync attempt {Attempt} failed for {Source}:{PlaylistId} after finalized track {TrackId}.",
                attempt,
                request.Source,
                request.PlaylistId,
                request.TrackId);
            return false;
        }
    }

    private static bool IsTerminalSyncFailure(PlaylistSyncResult syncResult)
    {
        if (syncResult.Success)
        {
            return false;
        }

        return string.Equals(syncResult.Message, "Playlist not available.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(syncResult.Message, "No target server selected.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(syncResult.Message, "Playlist sync target is disabled.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(syncResult.Message, "Unsupported playlist sync target.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(syncResult.Message, "No eligible tracks after blocked/ignored filtering.", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AddPlaylistSyncHistoryAsync(
        LibraryRepository repository,
        PlaylistWatchlistDto playlist,
        string status,
        CancellationToken cancellationToken)
    {
        await repository.AddWatchlistHistoryAsync(
            new WatchlistHistoryInsert(
                playlist.Source,
                "playlist",
                playlist.SourceId,
                playlist.Name,
                "playlist",
                playlist.TrackCount ?? 0,
                status,
                null),
            cancellationToken);
    }

    private void LogPlaylistMissing(SyncRequest request)
    {
        _logger.LogWarning(
            "Watchlist playlist sync skipped because playlist no longer exists: {Source}:{PlaylistId}.",
            request.Source,
            request.PlaylistId);
    }

    private SyncRequest ResolveEffectiveRequest(SyncRequest request, PlaylistWatchPreferenceDto? preference)
    {
        var preferenceDestinationFolderId = preference?.DestinationFolderId;
        if (request.DestinationFolderId.HasValue || !preferenceDestinationFolderId.HasValue)
        {
            return request;
        }

        var destinationFolderId = preferenceDestinationFolderId.Value;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Watchlist playlist sync resolved destination folder {DestinationFolderId} from playlist preference for {Source}:{PlaylistId}.",
                destinationFolderId,
                request.Source,
                request.PlaylistId);
        }

        return request with { DestinationFolderId = destinationFolderId };
    }

    private void LogSyncCompleted(SyncRequest request, int attempt, int syncedTracks)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _logger.LogInformation(
            "Watchlist playlist sync completed for {Source}:{PlaylistId} after finalized track {TrackId} (attempt {Attempt}, syncedTracks={SyncedTracks}).",
            request.Source,
            request.PlaylistId,
            request.TrackId,
            attempt,
            syncedTracks);
    }

    private void LogSyncNotReady(SyncRequest request, int attempt, string message)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _logger.LogInformation(
            "Watchlist playlist sync not ready for {Source}:{PlaylistId} after finalized track {TrackId} (attempt {Attempt}): {Message}",
            request.Source,
            request.PlaylistId,
            request.TrackId,
            attempt,
            message);
    }

    private static async Task<PlaylistWatchlistDto?> FindPlaylistAsync(
        LibraryRepository repository,
        SyncRequest request,
        CancellationToken cancellationToken)
    {
        var items = await repository.GetPlaylistWatchlistAsync(cancellationToken);
        return items.FirstOrDefault(item =>
            string.Equals(item.Source, request.Source, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.SourceId, request.PlaylistId, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> VerifyLocalLibraryIngestionAsync(
        IServiceProvider services,
        SyncRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.DestinationFolderId.HasValue)
        {
            return true;
        }

        var ingestionService = services.GetService<KnownLibraryFileIngestionService>();
        if (ingestionService == null)
        {
            return true;
        }

        if (request.ChangedFilePaths.Count > 0)
        {
            var ingestion = await ingestionService.VerifyAsync(
                new Dictionary<long, List<string>>
                {
                    [request.DestinationFolderId.Value] = request.ChangedFilePaths.ToList()
                },
                cancellationToken);
            return ingestion.IsComplete;
        }

        // Missing final paths are a notifier bug. The sync must be driven by real destination files.
        var configStore = services.GetService<LibraryConfigStore>();
        configStore?.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "warning",
            $"Watchlist playlist direct library ingestion skipped because no final file paths were provided for {request.Source}:{request.PlaylistId}:{request.TrackId} (destinationFolderId={request.DestinationFolderId})."));
        return false;
    }

    private static List<string> NormalizeChangedFilePaths(IReadOnlyList<string>? changedFilePaths)
    {
        if (changedFilePaths is null || changedFilePaths.Count == 0)
        {
            return new List<string>();
        }

        return changedFilePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task RefreshMediaServerAsync(
        IServiceProvider services,
        PlaylistWatchPreferenceDto? preference,
        CancellationToken cancellationToken)
    {
        var service = (preference?.Service ?? string.Empty).Trim().ToLowerInvariant();
        if (service == "none")
        {
            return;
        }

        var refreshService = services.GetRequiredService<MediaServerLibraryRefreshService>();
        await refreshService.RefreshAsync(service, cancellationToken);
    }

    private sealed record SyncRequest(
        string Source,
        string PlaylistId,
        string TrackId,
        long? DestinationFolderId,
        IReadOnlyList<string> ChangedFilePaths,
        int FollowUpPass);
}
