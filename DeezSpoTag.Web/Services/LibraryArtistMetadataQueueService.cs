using System.Threading.Channels;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryArtistMetadataQueueService : BackgroundService
{
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly ArtistMetadataCacheRefreshService _cacheRefresh;
    private readonly ILogger<LibraryArtistMetadataQueueService> _logger;
    private readonly Channel<QueueItem> _channel = Channel.CreateUnbounded<QueueItem>();
    private readonly Dictionary<long, QueueItem> _queueItems = new();
    private readonly object _queueLock = new();
    private readonly string _queuePath;
    private long _nextManualQueueKey;
    private long _activeQueueKey;

    public LibraryArtistMetadataQueueService(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        ArtistMetadataCacheRefreshService cacheRefresh,
        ArtistPageCacheRepository artistPageCache,
        IWebHostEnvironment environment,
        ILogger<LibraryArtistMetadataQueueService> logger)
    {
        _repository = repository;
        _configStore = configStore;
        _cacheRefresh = cacheRefresh;
        _logger = logger;
        _queuePath = Path.Join(AppDataPaths.GetDataRoot(environment), "artist-metadata-queue.json");
    }

    public async Task EnqueueMissingAsync(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return;
        }

        var artists = await _repository.GetArtistsAsync("all", cancellationToken);
        var enqueued = await PersistentArtistQueueStore.EnqueueArtistsAsync(
            artists,
            static artist => artist.Id,
            static artist => artist.Name,
            ShouldSkipAsync,
            static (artistId, artistName) => new QueueItem(artistId, artistName),
            TryEnqueue,
            cancellationToken);

        if (enqueued > 0)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Artist metadata fetch queued ({enqueued} artists)."));
        }
    }

    public void EnqueueArtists(IEnumerable<NewlyIndexedArtist> artists)
    {
        var enqueued = 0;
        foreach (var artist in artists)
        {
            if (artist.Id <= 0 || string.IsNullOrWhiteSpace(artist.Name))
            {
                continue;
            }

            if (TryEnqueue(new QueueItem(artist.Id, artist.Name)))
            {
                enqueued++;
            }
        }

        if (enqueued > 0)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Artist metadata fetch queued for {enqueued} newly indexed artist(s)."));
        }
    }

    public async Task<(bool Queued, string? RunId)> EnqueueRefreshAsync(
        ArtistMetadataCacheRefreshRequest request, CancellationToken cancellationToken)
    {
        if (request.ArtistId is not { } artistId || artistId <= 0) return (false, null);
        var artist = await _repository.GetArtistAsync(artistId, cancellationToken);
        if (artist is null) return (false, null);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_queueLock)
        {
            var existing = _queueItems.Values.FirstOrDefault(item => item.Request == request && item.RunId is not null);
            if (existing is not null)
            {
                // Repeated clicks follow the same in-flight job. An explicit retry of retained
                // work starts a new retry window without removing its persisted request.
                if (existing.Key != Interlocked.Read(ref _activeQueueKey) && existing.Attempts > 0)
                {
                    var retry = existing with { Attempts = 0, NextAttemptUtc = null, NeedsAttention = false, Reason = null };
                    _queueItems[retry.Key] = retry;
                    try
                    {
                        PersistentArtistQueueStore.PersistQueueSnapshot(_queueItems, _queueLock, _queuePath);
                        if (!_channel.Writer.TryWrite(retry)) throw new InvalidOperationException("Artist cache queue is closed.");
                    }
                    catch
                    {
                        _queueItems[existing.Key] = existing;
                        PersistentArtistQueueStore.PersistRetainedQueueSnapshot(_queueItems, _queueLock, _queuePath, _logger);
                        throw;
                    }
                }
                return (true, existing.RunId);
            }
            // Automatic work keeps its artist-ID dedupe key. Persisted negative keys let scoped
            // manual requests coexist with it, using the same queue and worker.
            _nextManualQueueKey = Math.Min(_nextManualQueueKey, _queueItems.Keys.DefaultIfEmpty(0).Min());
            var item = new QueueItem(artistId, artist.Name, RunId: Guid.NewGuid().ToString("N"),
                Request: request, QueueKey: --_nextManualQueueKey);
            return TryEnqueue(item) ? (true, item.RunId) : (false, null);
        }
    }

    public ArtistMetadataAutomationStatus? GetRefreshStatus(string? runId, long? artistId = null)
    {
        if (string.IsNullOrWhiteSpace(runId)) return null;
        lock (_queueLock)
        {
            var item = _queueItems.Values.FirstOrDefault(item => item.RunId == runId && (!artistId.HasValue || item.ArtistId == artistId.Value));
            if (item is null) return null;
            var running = item.Key == Interlocked.Read(ref _activeQueueKey);
            var pending = item.NeedsAttention || item.NextAttemptUtc > DateTimeOffset.UtcNow;
            return ArtistMetadataAutomationStatus.Idle() with
            {
                RunId = item.RunId, RunOperation = "cache-refresh",
                RunState = running ? "running" : pending ? "pending-retry" : "queued",
                RunPendingNotice = item.Reason, RunNextAttemptUtc = item.NextAttemptUtc,
                CacheRefresh = ArtistMetadataCacheStatus.Idle() with
                { Running = running, CurrentArtist = item.ArtistName, TotalArtists = 1,
                    Phase = running ? "Refreshing artist cache" : pending ? "Source work retained" : "Queued",
                    Message = item.Reason, FailedArtists = pending ? 1 : 0 }
            };
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        PersistentArtistQueueStore.RestoreAndReplaySnapshot(
            _channel,
            _queueItems,
            _queueLock,
            _queuePath,
            static item => item.Key,
            static item => !string.IsNullOrWhiteSpace(item.ArtistName),
            _logger);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var notification in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            QueueItem item;
            lock (_queueLock)
            {
                // A user click must not sit behind the entire automatic discovery backlog.
                var manual = _queueItems.Values.Where(candidate => candidate.Request?.ForceProviderRefresh == true
                    && !candidate.NeedsAttention && !(candidate.NextAttemptUtc > DateTimeOffset.UtcNow))
                    .OrderByDescending(candidate => candidate.Key).FirstOrDefault();
                var current = _queueItems.TryGetValue(notification.Key, out var saved) && saved == notification;
                item = manual ?? notification;
                if (manual is null && !current) continue;
                if (manual is not null && manual.Key != notification.Key && current)
                    _channel.Writer.TryWrite(notification);
            }
            try
            {
                var request = item.Request ?? await _cacheRefresh.BuildAutomaticRequestAsync(item.ArtistId, ArtistCacheRefreshScope.Full);
                if (item.Request is null && await _cacheRefresh.IsCompleteAsync(request, stoppingToken))
                {
                    PersistentArtistQueueStore.CompleteItem(item, _queueItems, _queueLock, _queuePath, static queued => queued.Key);
                    continue;
                }
                if (item.NeedsAttention) continue;
                if (item.NextAttemptUtc > DateTimeOffset.UtcNow)
                {
                    _ = ReplayWhenDueAsync(item, stoppingToken);
                    continue;
                }
                Interlocked.Exchange(ref _activeQueueKey, item.Key);
                var result = await _cacheRefresh.RefreshArtistWithOutcomeAsync(request, stoppingToken);
                if (item.RunId is not null)
                    await _cacheRefresh.SaveRunResultAsync(item.RunId, item.ArtistId,
                        new(1, result.IsComplete ? 1 : 0, result.IsComplete ? 0 : 1,
                            result.IsComplete ? null : string.Join(", ", result.UnresolvedWork.Select(work => $"{work.Provider}/{work.Stage}: {work.Status}"))), stoppingToken);
                if (result.IsComplete)
                    PersistentArtistQueueStore.CompleteItem(item, _queueItems, _queueLock, _queuePath, static queued => queued.Key);
                else Retain(item, result.UnresolvedWork.Any(work => work.Status == ArtistCacheWorkStatus.AuthenticationRequired),
                    string.Join(", ", result.UnresolvedWork.Select(work => $"{work.Provider}/{work.Stage}: {work.Status}")), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Artist metadata fetch failed for {ArtistName}; work retained.", item.ArtistName);
                Retain(item, false, ex.GetType().Name, stoppingToken);
                if (item.RunId is not null)
                {
                    try
                    {
                        await _cacheRefresh.SaveRunResultAsync(item.RunId, item.ArtistId,
                            new(1, 0, 1, "Artist cache refresh failed; unfinished work retained."), stoppingToken);
                    }
                    catch (Exception saveError) when (saveError is not OperationCanceledException)
                    {
                        _logger.LogWarning(saveError, "Could not save artist cache request result; its queue entry remains retained.");
                    }
                }
            }
            finally { Interlocked.CompareExchange(ref _activeQueueKey, 0, item.Key); }
        }
    }

    private void Retain(QueueItem item, bool needsAttention, string reason, CancellationToken token)
    {
        var retained = item with { Attempts = item.Attempts + 1, NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(30),
            NeedsAttention = needsAttention || item.Attempts + 1 >= 3, Reason = reason };
        lock (_queueLock)
        {
            _queueItems[item.Key] = retained;
            PersistentArtistQueueStore.PersistRetainedQueueSnapshot(_queueItems, _queueLock, _queuePath, _logger);
        }
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, "warn",
            $"Artist metadata work retained for {item.ArtistName}: {reason}."));
        if (!retained.NeedsAttention) _ = ReplayWhenDueAsync(retained, token);
    }

    private async Task ReplayWhenDueAsync(QueueItem item, CancellationToken token)
    {
        try
        {
            var delay = (item.NextAttemptUtc ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            await _channel.Writer.WriteAsync(item, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async ValueTask<bool> ShouldSkipAsync(ArtistDto artist, CancellationToken token)
        => await _cacheRefresh.IsCompleteAsync(await _cacheRefresh.BuildAutomaticRequestAsync(artist.Id, ArtistCacheRefreshScope.Full), token);

    private bool TryEnqueue(QueueItem item)
        => PersistentArtistQueueStore.TryEnqueue(
            item,
            _channel,
            _queueItems,
            _queueLock,
            _queuePath,
            static queued => queued.Key);

    private sealed record QueueItem(long ArtistId, string ArtistName, int Attempts = 0, DateTimeOffset? NextAttemptUtc = null,
        bool NeedsAttention = false, string? Reason = null, string? RunId = null,
        ArtistMetadataCacheRefreshRequest? Request = null, long QueueKey = 0)
    {
        public long Key => QueueKey == 0 ? ArtistId : QueueKey;
    }
}
