using System.Collections.Concurrent;
using System.Threading.Channels;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class WatchlistPostDownloadSyncService : BackgroundService, IWatchlistPostDownloadSyncNotifier
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(90),
        TimeSpan.FromMinutes(3),
        TimeSpan.FromMinutes(5)
    ];

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
            destinationFolderId);
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
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
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
                _logger.LogWarning(
                    "Watchlist post-download sync skipped because playlist no longer exists: {Source}:{PlaylistId}.",
                    request.Source,
                    request.PlaylistId);
                return true;
            }

            await RunLocalLibraryScanAsync(scope.ServiceProvider, request, cancellationToken);
            await RefreshMediaServerAsync(scope.ServiceProvider, request, cancellationToken);

            var preference = await repository.GetPlaylistWatchPreferenceAsync(
                playlist.Source,
                playlist.SourceId,
                cancellationToken);
            var watcher = scope.ServiceProvider.GetRequiredService<PlaylistWatchService>();
            var candidates = await watcher.GetPlaylistTrackCandidatesAsync(
                playlist.Source,
                playlist.SourceId,
                cancellationToken);
            var syncService = scope.ServiceProvider.GetRequiredService<PlaylistSyncService>();
            var result = await syncService.SyncPlaylistAsync(
                playlist,
                preference,
                candidates,
                force: true,
                cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Watchlist post-download sync completed for {Source}:{PlaylistId} after completed track {TrackId} (attempt {Attempt}, syncedTracks={SyncedTracks}).",
                    request.Source,
                    request.PlaylistId,
                    request.TrackId,
                    attempt,
                    result.SyncedTracks);
                return true;
            }

            _logger.LogInformation(
                "Watchlist post-download sync not ready for {Source}:{PlaylistId} after completed track {TrackId} (attempt {Attempt}): {Message}",
                request.Source,
                request.PlaylistId,
                request.TrackId,
                attempt,
                result.Message);
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

        await scanner.WaitForCurrentScanAsync(cancellationToken);
        await scanner.RunAsync(
            refreshImages: false,
            reset: false,
            folderId: request.DestinationFolderId.Value,
            skipSpotifyFetch: true,
            cacheSpotifyImages: false,
            cancellationToken);
    }

    private async Task RefreshMediaServerAsync(
        IServiceProvider services,
        SyncRequest request,
        CancellationToken cancellationToken)
    {
        var repository = services.GetRequiredService<LibraryRepository>();
        var preference = await repository.GetPlaylistWatchPreferenceAsync(
            request.Source,
            request.PlaylistId,
            cancellationToken);
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
        long? DestinationFolderId);
}
