using System.Collections.Concurrent;
using System.Threading.Channels;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class WatchlistPostDownloadSyncService : BackgroundService, IWatchlistPostDownloadSyncNotifier
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5)
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
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WatchlistPostDownloadSyncService> _logger;

    public WatchlistPostDownloadSyncService(
        IServiceProvider serviceProvider,
        ILogger<WatchlistPostDownloadSyncService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public ValueTask NotifyCompletedAsync(
        string source,
        string playlistId,
        string trackId,
        long? destinationFolderId,
        IReadOnlyList<string>? changedFilePaths = null,
        CancellationToken cancellationToken = default)
    {
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
            NormalizeChangedFilePaths(changedFilePaths),
            FollowUpPass: 0);
        return _queue.Writer.WriteAsync(request, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            _ = ProcessWithRetriesAsync(request, stoppingToken);
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
                    "Watchlist post-download sync already running for {Source}:{PlaylistId}; queued one follow-up pass.",
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
                "Watchlist post-download sync exhausted retries for {Source}:{PlaylistId} after completed track {TrackId}.",
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
                    "Watchlist post-download sync canceled for {Source}:{PlaylistId}.",
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
                "Watchlist post-download sync stopped after {FollowUpPasses} follow-up passes for {Source}:{PlaylistId} after completed track {TrackId}.",
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
                "Watchlist post-download sync scheduled follow-up pass {FollowUpPass}/{MaxFollowUpPasses} for {Source}:{PlaylistId} after completed track {TrackId}.",
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
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Watchlist post-download sync failed to schedule follow-up for {Source}:{PlaylistId} after completed track {TrackId}.",
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

            await RunLocalLibraryScanAsync(scope.ServiceProvider, effectiveRequest, cancellationToken);
            await RefreshMediaServerAsync(scope.ServiceProvider, preference, cancellationToken);

            var watcher = scope.ServiceProvider.GetRequiredService<PlaylistWatchService>();
            var candidates = await watcher.GetPlaylistTrackCandidatesAsync(
                playlist.Source,
                playlist.SourceId,
                cancellationToken);
            var syncService = scope.ServiceProvider.GetRequiredService<PlaylistSyncService>();
            var readiness = await syncService.CheckPlaylistReadyForAutomaticSyncAsync(
                playlist,
                preference,
                candidates,
                cancellationToken);
            if (!readiness.Ready)
            {
                return HandleNotReady(request, attempt, readiness);
            }

            var reconciliation = await watcher.ReconcilePlaylistAsync(
                playlist,
                cancellationToken,
                forceMediaServerSync: true);

            if (reconciliation.SyncResult?.Success == true)
            {
                LogSyncCompleted(request, attempt, reconciliation.SyncResult?.SyncedTracks ?? 0);
                return true;
            }

            LogSyncNotReady(request, attempt, reconciliation.SyncResult?.Message ?? reconciliation.Message);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Watchlist post-download sync attempt {Attempt} failed for {Source}:{PlaylistId} after completed track {TrackId}.",
                attempt,
                request.Source,
                request.PlaylistId,
                request.TrackId);
            return false;
        }
    }

    private void LogPlaylistMissing(SyncRequest request)
    {
        _logger.LogWarning(
            "Watchlist post-download sync skipped because playlist no longer exists: {Source}:{PlaylistId}.",
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
                "Watchlist post-download sync recovered destination folder {DestinationFolderId} from playlist preference for {Source}:{PlaylistId}.",
                destinationFolderId,
                request.Source,
                request.PlaylistId);
        }

        return request with { DestinationFolderId = destinationFolderId };
    }

    private bool HandleNotReady(
        SyncRequest request,
        int attempt,
        PlaylistSyncService.PlaylistTrackSyncReadiness readiness)
    {
        if (readiness.Terminal)
        {
            _logger.LogWarning(
                "Watchlist post-download sync stopped for {Source}:{PlaylistId} after completed track {TrackId}: {Message}",
                request.Source,
                request.PlaylistId,
                request.TrackId,
                readiness.Message);
            return true;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Watchlist post-download sync waiting for readiness for {Source}:{PlaylistId} after completed track {TrackId} (attempt {Attempt}): {Message}",
                request.Source,
                request.PlaylistId,
                request.TrackId,
                attempt,
                readiness.Message);
        }

        return false;
    }

    private void LogSyncCompleted(SyncRequest request, int attempt, int syncedTracks)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _logger.LogInformation(
            "Watchlist post-download sync completed for {Source}:{PlaylistId} after completed track {TrackId} (attempt {Attempt}, syncedTracks={SyncedTracks}).",
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
            "Watchlist post-download sync not ready for {Source}:{PlaylistId} after completed track {TrackId} (attempt {Attempt}): {Message}",
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

    private async Task RunLocalLibraryScanAsync(
        IServiceProvider services,
        SyncRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.DestinationFolderId.HasValue)
        {
            return;
        }

        var scanner = services.GetService<LibraryScanRunner>();
        if (scanner == null)
        {
            return;
        }

        if (request.ChangedFilePaths.Count > 0)
        {
            await scanner.RunChangedFilesAsync(
                new Dictionary<long, List<string>>
                {
                    [request.DestinationFolderId.Value] = request.ChangedFilePaths.ToList()
                },
                skipSpotifyFetch: true,
                cancellationToken);
            return;
        }

        await scanner.RunChangedFoldersAsync(
            new[] { request.DestinationFolderId.Value },
            skipSpotifyFetch: true,
            cancellationToken);
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
