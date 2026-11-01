using System.Threading.Channels;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

/// <summary>One durable cache-refresh worker for discovery, artist buttons and library sweeps.</summary>
public sealed class LibraryArtistMetadataQueueService : BackgroundService
{
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly ArtistMetadataCacheRefreshService _cacheRefresh;
    private readonly ILogger<LibraryArtistMetadataQueueService> _logger;
    private readonly Channel<QueueItem> _channel = Channel.CreateUnbounded<QueueItem>();
    private readonly Dictionary<long, QueueItem> _queueItems = new();
    private readonly Dictionary<string, Dictionary<long, ArtistRunOutcomeRecord>> _outcomes = new();
    private readonly Dictionary<string, ArtistMetadataOutcomeJournal> _journals = new();
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _completionGate = new(1, 1);
    private readonly string _queuePath;
    private long _nextManualQueueKey;
    private long _activeQueueKey;
    private long? _priorityYieldKey;
    private string? _currentArtist;
    private CancellationTokenSource? _activeCancellation;
    private ArtistMetadataAutomationStatus? _lastLibraryStatus;

    public LibraryArtistMetadataQueueService(LibraryRepository repository, LibraryConfigStore configStore,
        ArtistMetadataCacheRefreshService cacheRefresh, ArtistPageCacheRepository artistPageCache,
        IWebHostEnvironment environment, ILogger<LibraryArtistMetadataQueueService> logger)
    {
        _repository = repository;
        _configStore = configStore;
        _cacheRefresh = cacheRefresh;
        _logger = logger;
        _queuePath = Path.Join(AppDataPaths.GetDataRoot(environment), "artist-metadata-queue.json");
    }

    public async Task EnqueueMissingAsync(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured) return;
        ReplayRecoverableAutomaticWork();
        var artists = await _repository.GetArtistsAsync("all", cancellationToken);
        var enqueued = await PersistentArtistQueueStore.EnqueueArtistsAsync(artists, static artist => artist.Id,
            static artist => artist.Name, ShouldSkipAsync, static (id, name) => new QueueItem(id, name),
            TryEnqueue, cancellationToken);
        if (enqueued > 0) LogEnqueued(enqueued);
    }

    public void EnqueueArtists(IEnumerable<NewlyIndexedArtist> artists)
    {
        var enqueued = artists.Count(artist => artist.Id > 0 && !string.IsNullOrWhiteSpace(artist.Name)
            && TryEnqueue(new QueueItem(artist.Id, artist.Name)));
        if (enqueued > 0) LogEnqueued(enqueued);
    }

    private void LogEnqueued(int count) => _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
        DateTimeOffset.UtcNow, "info", $"Artist metadata fetch queued ({count} artists)."));

    public async Task<(bool Queued, string? RunId)> EnqueueRefreshAsync(
        ArtistMetadataCacheRefreshRequest request, CancellationToken cancellationToken,
        ArtistMetadataActiveRun? importedRun = null, IReadOnlyList<ArtistRunOutcomeRecord>? importedOutcomes = null,
        bool automatic = false)
    {
        if (importedRun is not null)
        {
            lock (_queueLock)
                if (_queueItems.Values.Any(item => item.RunId == importedRun.RunId)) return (true, importedRun.RunId);
        }
        List<long> ids;
        string name;
        if (importedRun is { TargetArtistIds.Count: > 0 })
        {
            ids = importedRun.TargetArtistIds;
            name = "Artist cache refresh";
        }
        else if (request.ArtistId is { } artistId)
        {
            if (artistId <= 0) return (false, null);
            var artist = await _repository.GetArtistAsync(artistId, cancellationToken);
            if (artist is null) return (false, null);
            ids = [artistId];
            name = artist.Name;
        }
        else
        {
            ids = ArtistMetadataCacheRefreshService.SelectRunArtists(
                await _repository.GetArtistsAsync("all", request.FolderId, cancellationToken), request, null)
                .Select(artist => artist.Id).ToList();
            name = "Artist cache refresh";
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Journal migration must be durable before the old coordinator checkpoint can be discarded.
        var run = importedRun ?? new ArtistMetadataActiveRun { Operation = "cache-refresh", Automatic = automatic,
            RunId = Guid.NewGuid().ToString("N"), CacheRequest = request, TargetArtistIds = ids };
        if (!Guid.TryParseExact(run.RunId, "N", out _)) run.RunId = Guid.NewGuid().ToString("N");
        if (importedOutcomes is { Count: > 0 })
        {
            await using var journal = await ArtistMetadataOutcomeJournal.OpenAsync(JournalPath(run.RunId), run.RunId,
                truncate: false, seed: importedOutcomes, cancellationToken);
            await journal.FlushAsync(cancellationToken);
        }
        run.Outcomes = null;
        run.CompletedArtistIds = null;
        lock (_queueLock)
        {
            var existing = _queueItems.Values.FirstOrDefault(item => item.RunId == run.RunId
                || (importedRun is null && item.Request == request && item.RunId is not null));
            if (existing is not null)
            {
                // Only an explicit artist-page request takes precedence over retained work.
                // Older snapshots may have the request and exhausted attempts but no Run yet.
                if (!automatic && importedRun is null && request.ArtistId.HasValue && existing.Key != _activeQueueKey
                    && (existing.Run is null || existing.Run.Paused || existing.Run.Automatic
                        || existing.Run.NextAttemptUtc is not null))
                {
                    var resumed = ArtistMetadataAutomationCoordinator.SnapshotRun(existing.Run ?? run);
                    resumed.RunId = existing.RunId!;
                    resumed.Automatic = false;
                    ResetRetry(resumed);
                    var retry = existing with { Run = resumed, Attempts = 0, NeedsAttention = false,
                        NextAttemptUtc = null, Reason = null, UserPaused = false,
                        AuthenticationBlocked = false, RecoveryAfterUtc = null };
                    _queueItems[retry.Key] = retry;
                    try { Persist(); }
                    catch { _queueItems[existing.Key] = existing; throw; }
                    _attempted.Remove(resumed.RunId);
                    _channel.Writer.TryWrite(retry);
                }
                if (!automatic && importedRun is null) YieldToArtistRequest(request, existing.Key);
                return (true, existing.RunId);
            }
            _nextManualQueueKey = Math.Min(_nextManualQueueKey, _queueItems.Keys.DefaultIfEmpty(0).Min());
            var item = new QueueItem(request.ArtistId ?? 0, name, RunId: run.RunId,
                Request: request, QueueKey: --_nextManualQueueKey, Run: run, UserPaused: importedRun is null ? false : null);
            if (!TryEnqueue(item)) return (false, null);
            if (!automatic && importedRun is null) YieldToArtistRequest(request, item.Key);
            return (true, item.RunId);
        }
    }

    // Called with the queue lock held, after the manual request is durably saved.
    private void YieldToArtistRequest(ArtistMetadataCacheRefreshRequest request, long requestedKey)
    {
        if (request is not { ArtistId: not null, ForceProviderRefresh: true }
            || requestedKey == _activeQueueKey || _activeCancellation is null
            || !_queueItems.TryGetValue(_activeQueueKey, out var active)
            || (active.Run?.Automatic != true && active.Request?.ArtistId is not null)) return;
        _priorityYieldKey = _activeQueueKey;
        ArtistMetadataAutomationCoordinator.TryCancel(_activeCancellation);
    }

    public ArtistMetadataAutomationStatus? GetRefreshStatus(string? runId, long? artistId = null)
    {
        if (string.IsNullOrWhiteSpace(runId) && !artistId.HasValue) return null;
        lock (_queueLock)
        {
            var item = _queueItems.Values.Where(item => (string.IsNullOrWhiteSpace(runId) || item.RunId == runId)
                && (!artistId.HasValue || item.ArtistId == artistId || item.Run?.TargetArtistIds.Contains(artistId.Value) == true))
                .OrderByDescending(item => item.Run is { Automatic: false, Paused: false } && item.Request?.ArtistId.HasValue == true)
                .ThenByDescending(item => item.Key == _activeQueueKey).FirstOrDefault();
            return item is null ? null : Status(item, artistId);
        }
    }

    public ArtistMetadataAutomationStatus? GetLibraryRefreshStatus()
    {
        lock (_queueLock)
        {
            var item = _queueItems.Values.Where(item => item.Request is { ArtistId: null })
                .OrderByDescending(item => item.Run?.StartedAtUtc).FirstOrDefault();
            return item is null ? _lastLibraryStatus : Status(item);
        }
    }

    public async Task<bool> WaitForAttemptAsync(string runId, CancellationToken token)
    {
        while (GetRefreshStatus(runId) is { } status)
        {
            if (status.RunState is "paused" or "pending-retry") return false;
            await Task.Delay(100, token);
        }
        return (await _cacheRefresh.GetRunResultAsync(runId, token))?.Result is { Failed: 0 };
    }

    public async Task<bool> ControlAsync(string runId, string action, CancellationToken token)
    {
        await _completionGate.WaitAsync(token);
        try
        {
            lock (_queueLock)
            {
                var item = _queueItems.Values.FirstOrDefault(item => item.RunId == runId);
                if (item?.Run is not { } run) return false;
                if (action == "cancel")
                {
                    var current = Status(item);
                    var cancelled = current with { ActiveOperation = null, RunState = "idle",
                        CacheRefresh = current.CacheRefresh with { Running = true } };
                    PersistentArtistQueueStore.CompleteItem(item, _queueItems, _queueLock, _queuePath, queued => queued.Key);
                    if (item.Request?.ArtistId is null)
                        _lastLibraryStatus = ArtistMetadataAutomationCoordinator.FinalizeOperationStatus(cancelled, "cache-refresh", false, DateTimeOffset.UtcNow);
                }
                else if (action is "pause" or "resume")
                {
                    if (action == "resume" && !run.Paused) return false;
                    var updated = item with { Run = ArtistMetadataAutomationCoordinator.SnapshotRun(run), UserPaused = action == "pause",
                        AuthenticationBlocked = action == "resume" ? false : item.AuthenticationBlocked,
                        RecoveryAfterUtc = action == "resume" ? null : item.RecoveryAfterUtc };
                    if (action == "pause") updated.Run!.Paused = true;
                    else
                    {
                        ResetRetry(updated.Run!);
                        if (updated.Request is { } request)
                        {
                            updated = updated with { Request = request with { ForceProviderRefresh = true } };
                            updated.Run!.CacheRequest = updated.Request;
                        }
                    }
                    _queueItems[item.Key] = updated;
                    try { Persist(); }
                    catch { _queueItems[item.Key] = item; throw; }
                    if (action == "resume") _channel.Writer.TryWrite(updated);
                }
                else return false;
                if (action != "resume" && _activeQueueKey == item.Key) _activeCancellation?.Cancel();
                return true;
            }
        }
        finally { _completionGate.Release(); }
    }

    private static void ResetRetry(ArtistMetadataActiveRun run)
    {
        run.Paused = false;
        run.NextAttemptUtc = null;
        run.PendingNotice = null;
        run.Disposition = null;
        run.CacheAttempts.Clear();
        run.ConsecutiveFailures = 0;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        PersistentArtistQueueStore.RestoreAndReplaySnapshot(_channel, _queueItems, _queueLock, _queuePath,
            static item => item.Key, static item => !string.IsNullOrWhiteSpace(item.ArtistName), _logger);
        return base.StartAsync(cancellationToken);
    }

    // Caller holds _queueLock. Recheck after asynchronous preparation as well as between artists.
    private QueueItem? NextManualRequest() => _queueItems.Values
        .Where(candidate => candidate.Request is { ArtistId: not null, ForceProviderRefresh: true }
            && candidate.Run is { Automatic: false, Paused: false } pending
            && !(pending.NextAttemptUtc > DateTimeOffset.UtcNow))
        .OrderByDescending(candidate => candidate.Key).FirstOrDefault();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var notification in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            QueueItem item;
            lock (_queueLock)
            {
                // Yield between artists. One artist button must not sit behind a whole library sweep.
                var manual = NextManualRequest();
                if (manual is null && !_queueItems.ContainsKey(notification.Key)) continue;
                item = manual ?? _queueItems[notification.Key];
                if (manual is not null && manual.Key != notification.Key && _queueItems.ContainsKey(notification.Key))
                    _channel.Writer.TryWrite(notification);
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            try
            {
                lock (_queueLock)
                {
                    var recovered = RecoverAutomaticItem(item, DateTimeOffset.UtcNow);
                    if (recovered is not null)
                    {
                        _queueItems[item.Key] = recovered;
                        try { Persist(); }
                        catch { _queueItems[item.Key] = item; throw; }
                        item = recovered;
                    }
                }
                if (item.Run is null && (item.NeedsAttention || item.NextAttemptUtc > DateTimeOffset.UtcNow))
                {
                    if (!item.NeedsAttention) _ = ReplayWhenDueAsync(item, stoppingToken);
                    continue;
                }
                item = await PrepareAsync(item, stoppingToken);
                var run = item.Run!;
                if (run.Paused) continue;
                if (run.NextAttemptUtc > DateTimeOffset.UtcNow)
                {
                    _ = ReplayWhenDueAsync(item, stoppingToken);
                    continue;
                }
                lock (_queueLock)
                {
                    if (!_queueItems.TryGetValue(item.Key, out var latest) || latest.Run is not { } currentRun || currentRun.Paused) continue;
                    item = latest;
                    run = currentRun;
                    if ((run.Automatic || item.Request?.ArtistId is null)
                        && NextManualRequest() is { } waiting && waiting.Key != item.Key)
                    {
                        _channel.Writer.TryWrite(item);
                        continue;
                    }
                    if (run.NextAttemptUtc > DateTimeOffset.UtcNow) { _ = ReplayWhenDueAsync(item, stoppingToken); continue; }
                    if (run.NextAttemptUtc is not null) { run.NextAttemptUtc = null; Persist(); }
                    _activeQueueKey = item.Key;
                    _currentArtist = item.ArtistName;
                    _activeCancellation = cancellation;
                }
                await ProcessNextArtistAsync(item, cancellation.Token, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                lock (_queueLock)
                    if (_priorityYieldKey == item.Key && _queueItems.TryGetValue(item.Key, out var retained)
                        && retained.Run?.Paused != true)
                        _channel.Writer.TryWrite(retained);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Artist metadata fetch failed for {ArtistName}; work retained.", item.ArtistName);
                lock (_queueLock)
                {
                    if (_queueItems.TryGetValue(item.Key, out var legacy) && legacy.Run is null)
                    {
                        var retry = legacy with { Attempts = legacy.Attempts + 1, NeedsAttention = legacy.Attempts + 1 >= 3,
                            NextAttemptUtc = DateTimeOffset.UtcNow + RetryInterval, Reason = ex.GetType().Name };
                        _queueItems[item.Key] = retry;
                        PersistentArtistQueueStore.PersistRetainedQueueSnapshot(_queueItems, _queueLock, _queuePath, _logger);
                        if (!retry.NeedsAttention) _ = ReplayWhenDueAsync(retry, stoppingToken);
                        else _channel.Writer.TryWrite(retry);
                    }
                    if (_queueItems.TryGetValue(item.Key, out var current) && current.Run is { } run)
                    {
                        run.ConsecutiveFailures++;
                        run.Paused = run.ConsecutiveFailures >= 3;
                        run.NextAttemptUtc = run.Paused ? null : DateTimeOffset.UtcNow + RetryInterval;
                        run.PendingNotice = "Artist cache work retained: " + ex.GetType().Name;
                        run.Disposition = run.Paused ? ArtistMetadataRunDispositions.NeedsAttention : ArtistMetadataRunDispositions.PendingRetry;
                        PersistentArtistQueueStore.PersistRetainedQueueSnapshot(_queueItems, _queueLock, _queuePath, _logger);
                        if (run.Paused && run.Automatic && current.UserPaused == false)
                        {
                            current = current with { RecoveryAfterUtc = DateTimeOffset.UtcNow + RecoveryCooldown };
                            _queueItems[current.Key] = current;
                            Persist();
                            _channel.Writer.TryWrite(current);
                        }
                        else if (!run.Paused) _ = ReplayWhenDueAsync(current, stoppingToken);
                    }
                }
            }
            finally
            {
                if (item.RunId is { } id && (item.Run?.Paused == true || cancellation.IsCancellationRequested))
                    await CloseJournalAsync(id);
                lock (_queueLock)
                {
                    _priorityYieldKey = null;
                    _activeQueueKey = 0;
                    _currentArtist = null;
                    _activeCancellation = null;
                    if (!_queueItems.ContainsKey(item.Key) && item.RunId is { } removedId)
                    { _outcomes.Remove(removedId); _attempted.Remove(removedId); }
                }
            }
        }
    }

    private async Task<QueueItem> PrepareAsync(QueueItem item, CancellationToken token)
    {
        if (item.Run is not null) return item;
        // Existing automatic queue snapshots retain their artist IDs and are upgraded in place.
        var request = item.Request ?? await _cacheRefresh.BuildAutomaticRequestAsync(item.ArtistId, ArtistCacheRefreshScope.Full);
        var run = new ArtistMetadataActiveRun { Operation = "cache-refresh", Automatic = item.Request is null,
            RunId = item.RunId ?? Guid.NewGuid().ToString("N"), CacheRequest = request, TargetArtistIds = [item.ArtistId],
            Paused = item.NeedsAttention, NextAttemptUtc = item.NextAttemptUtc, ConsecutiveFailures = item.Attempts };
        var upgraded = item with { RunId = run.RunId, Request = request, Run = run,
            UserPaused = item.Request is null ? false : item.UserPaused };
        lock (_queueLock)
        {
            _queueItems[item.Key] = upgraded;
            Persist();
        }
        return upgraded;
    }

    private async Task ProcessNextArtistAsync(QueueItem item, CancellationToken token, CancellationToken stoppingToken)
    {
        var run = item.Run!;
        Dictionary<long, ArtistRunOutcomeRecord> outcomes;
        lock (_queueLock) outcomes = Outcomes(item);
        var ids = run.TargetArtistIds;
        // Each pass visits each unresolved artist once; retry starts another pass using the same journal.
        var attempted = _attempted.GetValueOrDefault(run.RunId);
        if (attempted is null) _attempted[run.RunId] = attempted = new HashSet<long>();
        var next = ids.FirstOrDefault(id => !attempted.Contains(id)
            && (!outcomes.TryGetValue(id, out var row) || row.CacheWork is null || !ArtistRunOutcomes.IsTerminal(row)));
        if (next > 0)
        {
            var retainedStages = run.CacheAttempts.Where(pair => pair.Value >= 3 && pair.Key.StartsWith($"{next}:", StringComparison.Ordinal))
                .Select(pair => pair.Key[(next.ToString(System.Globalization.CultureInfo.InvariantCulture).Length + 1)..])
                .Concat(outcomes.TryGetValue(next, out var previousRow)
                    ? (previousRow.CacheWork ?? []).Where(work => work.Status == ArtistCacheWorkStatus.AuthenticationRequired
                        && run.CacheAttempts.ContainsKey($"{next}:{work.Provider}:{work.Stage}"))
                        .Select(work => $"{work.Provider}:{work.Stage}") : [])
                .Distinct(StringComparer.Ordinal).ToArray();
            var request = item.Request! with { ArtistId = next, FolderId = null, RefreshRunId = run.RunId,
                RetainedStages = retainedStages,
                RevalidateProviderAccess = item.Request.ForceProviderRefresh && run.CacheAttempts.Count == 0 };
            ArtistRunOutcomeRecord record;
            try
            {
                var result = await _cacheRefresh.RefreshArtistWithOutcomeAsync(request, token,
                    name => { lock (_queueLock) _currentArtist = name; });
                record = new(next, result.IsComplete ? ArtistRunOutcomes.Succeeded : ArtistRunOutcomes.Failed, CacheWork: result.Work);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Artist cache refresh failed for {ArtistId}; work retained.", next);
                record = new(next, ArtistRunOutcomes.Failed, CacheWork:
                    [new("local", "artist", ex is TimeoutException ? ArtistCacheWorkStatus.TimedOut : ArtistCacheWorkStatus.Failed, ReasonCode: ex.GetType().Name)]);
            }
            token.ThrowIfCancellationRequested();
            ArtistMetadataOutcomeJournal? journal;
            lock (_queueLock) _journals.TryGetValue(run.RunId, out journal);
            if (journal is null)
            {
                journal = await ArtistMetadataOutcomeJournal.OpenAsync(JournalPath(run.RunId), run.RunId,
                    truncate: false, seed: null, token);
                lock (_queueLock) _journals[run.RunId] = journal;
            }
            await journal.AppendAsync(record, token);
            lock (_queueLock) outcomes[next] = record;
            attempted.Add(next);
            // A failed append never marks the artist visited, so recovery retries it.
            if (ids.Any(id => !attempted.Contains(id) && (!outcomes.TryGetValue(id, out var row)
                || row.CacheWork is null || !ArtistRunOutcomes.IsTerminal(row))))
            {
                _channel.Writer.TryWrite(item);
                return;
            }
        }
        await _completionGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            lock (_queueLock) if (!_queueItems.ContainsKey(item.Key)) return;
            await CloseJournalAsync(run.RunId);
            var retained = RetainUnresolvedCacheWork(run, outcomes.Values.ToArray(), DateTimeOffset.UtcNow);
            var summary = new ArtistMetadataCacheRefreshResult(ids.Count,
                outcomes.Values.Count(row => row.CacheWork is not null && ArtistRunOutcomes.IsTerminal(row)),
                ids.Count(id => !outcomes.TryGetValue(id, out var row) || row.CacheWork is null || !ArtistRunOutcomes.IsTerminal(row)),
                retained ? run.PendingNotice : null);
            await _cacheRefresh.SaveRunResultAsync(run.RunId, item.Request!.ArtistId, summary, token);
            ArtistMetadataAutomationStatus? schedule = null;
            if (!retained && item.Request.ArtistId is null)
                schedule = await _cacheRefresh.SaveSweepCompletionAsync(DateTimeOffset.UtcNow, item.Request.ForceProviderRefresh, token);
            lock (_queueLock)
            {
                if (!_queueItems.ContainsKey(item.Key)) return;
                if (retained)
                {
                    run.ConsecutiveFailures = 0;
                    if (run.Paused && run.Automatic && item.UserPaused == false)
                    {
                        var auth = outcomes.Values.SelectMany(row => row.CacheWork ?? [])
                            .Where(work => !work.IsComplete).All(work => work.Status == ArtistCacheWorkStatus.AuthenticationRequired);
                        item = item with { AuthenticationBlocked = auth, RecoveryAfterUtc = DateTimeOffset.UtcNow + RecoveryCooldown };
                        _queueItems[item.Key] = item;
                    }
                    Persist();
                    attempted.Clear();
                    if (!run.Paused) _ = ReplayWhenDueAsync(item, stoppingToken);
                    else if (run.Automatic && item.UserPaused == false && !item.AuthenticationBlocked)
                        _channel.Writer.TryWrite(item);
                }
                else
                {
                    var completed = Status(item);
                    var finishedStatus = completed with
                    { LastCacheRefreshUtc = schedule?.LastCacheRefreshUtc ?? completed.LastCacheRefreshUtc,
                        NextCacheRefreshUtc = schedule is not null ? schedule.NextCacheRefreshUtc : completed.NextCacheRefreshUtc,
                        ActiveOperation = null, RunState = "idle", RunDisposition = ArtistMetadataRunDispositions.Completed,
                        CacheRefresh = completed.CacheRefresh with { Running = false, RunState = "idle", CurrentArtist = null, Phase = "Cache refresh completed", Message = $"{summary.Succeeded} succeeded, {summary.Failed} failed.", CompletedAtUtc = DateTimeOffset.UtcNow } };
                    if (item.Request.ArtistId is null) _lastLibraryStatus = finishedStatus;
                    PersistentArtistQueueStore.CompleteItem(item, _queueItems, _queueLock, _queuePath, queued => queued.Key);
                    _outcomes.Remove(run.RunId);
                    _attempted.Remove(run.RunId);
                    // Completion evidence was saved before the queue entry and journal are removed.
                    try { File.Delete(JournalPath(run.RunId)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { _logger.LogWarning(ex, "Completed artist cache journal could not be removed for {RunId}.", run.RunId); }
                }
            }
        }
        finally { _completionGate.Release(); }
    }

    private async Task CloseJournalAsync(string runId)
    {
        ArtistMetadataOutcomeJournal? journal;
        lock (_queueLock) _journals.Remove(runId, out journal);
        if (journal is not null) await journal.DisposeAsync();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        string[] open;
        lock (_queueLock) open = _journals.Keys.ToArray();
        foreach (var id in open) await CloseJournalAsync(id);
    }

    private readonly Dictionary<string, HashSet<long>> _attempted = new();
    private Dictionary<long, ArtistRunOutcomeRecord> Outcomes(QueueItem item)
    {
        var id = item.RunId!;
        if (!_outcomes.TryGetValue(id, out var rows))
            _outcomes[id] = rows = ArtistMetadataOutcomeJournal.Read(JournalPath(id), id)
                .Where(row => item.Run!.TargetArtistIds.Contains(row.ArtistId))
                .GroupBy(row => row.ArtistId).ToDictionary(group => group.Key, group => group.Last());
        return rows;
    }

    private ArtistMetadataAutomationStatus Status(QueueItem item, long? artistId = null)
    {
        var run = item.Run;
        var rows = run is null ? [] : Outcomes(item).Values.Where(row => !artistId.HasValue || row.ArtistId == artistId).ToArray();
        var running = item.Key == _activeQueueKey && run?.Paused != true;
        var state = run?.Paused == true ? "paused" : running ? "running" : run?.NextAttemptUtc is not null ? "pending-retry" : "queued";
        if (artistId.HasValue && rows.Any(ArtistRunOutcomes.IsTerminal)) { running = false; state = "idle"; }
        return ArtistMetadataAutomationStatus.Idle() with
        {
            RunId = item.RunId, RunOperation = "cache-refresh", RunState = state,
            LastCacheRefreshUtc = _lastLibraryStatus?.LastCacheRefreshUtc,
            NextCacheRefreshUtc = _lastLibraryStatus?.NextCacheRefreshUtc,
            ActiveOperation = running ? "cache-refresh" : null,
            RunDisposition = run?.Disposition, RunPendingNotice = run?.PendingNotice, RunNextAttemptUtc = run?.NextAttemptUtc,
            CacheRefresh = new(running, run?.Automatic == true, running ? "Refreshing artist cache" : state,
                run?.PendingNotice, rows.Length, artistId.HasValue ? 1 : run?.TargetArtistIds.Count ?? 1,
                running ? _currentArtist ?? item.ArtistName : null, run?.StartedAtUtc, null,
                rows.Count(ArtistRunOutcomes.IsTerminal), rows.Count(row => !ArtistRunOutcomes.IsTerminal(row)),
                RunId: item.RunId, RunState: state)
        };
    }

    private string JournalPath(string runId)
    {
        if (!Guid.TryParseExact(runId, "N", out _)) throw new InvalidDataException("Invalid cache refresh run ID.");
        return Path.Join(Path.GetDirectoryName(_queuePath)!, "artist-cache-runs", runId + ".ndjson");
    }
    private void Persist() => PersistentArtistQueueStore.PersistQueueSnapshot(_queueItems, _queueLock, _queuePath);
    private async Task ReplayWhenDueAsync(QueueItem item, CancellationToken token)
    {
        try
        {
            var delay = (item.Run?.NextAttemptUtc ?? item.NextAttemptUtc ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            await _channel.Writer.WriteAsync(item, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async ValueTask<bool> ShouldSkipAsync(ArtistDto artist, CancellationToken token)
        => await _cacheRefresh.IsCompleteAsync(await _cacheRefresh.BuildAutomaticRequestAsync(artist.Id, ArtistCacheRefreshScope.Full), token);
    private bool TryEnqueue(QueueItem item) => PersistentArtistQueueStore.TryEnqueue(item, _channel, _queueItems,
        _queueLock, _queuePath, queued => queued.Key);

    private sealed record QueueItem(long ArtistId, string ArtistName, int Attempts = 0, DateTimeOffset? NextAttemptUtc = null,
        bool NeedsAttention = false, string? Reason = null, string? RunId = null,
        ArtistMetadataCacheRefreshRequest? Request = null, long QueueKey = 0, ArtistMetadataActiveRun? Run = null, bool? UserPaused = null,
        bool AuthenticationBlocked = false, DateTimeOffset? RecoveryAfterUtc = null)
    {
        public long Key => QueueKey == 0 ? ArtistId : QueueKey;
    }

    private void ReplayRecoverableAutomaticWork()
    {
        lock (_queueLock)
            foreach (var item in _queueItems.Values)
                if (item.Key != _activeQueueKey && RecoverAutomaticItem(item, DateTimeOffset.UtcNow) is not null)
                    _channel.Writer.TryWrite(item);
    }

    // Unknown structured pauses may have been requested by the user; never infer consent.
    private static QueueItem? RecoverAutomaticItem(QueueItem item, DateTimeOffset now)
    {
        if (item.UserPaused == true || item.AuthenticationBlocked
            || (item.Reason?.Contains("Authentication", StringComparison.OrdinalIgnoreCase) ?? false)) return null;
        var run = item.Run;
        if (run is null ? item.Request is not null || !item.NeedsAttention
            : !run.Automatic || !run.Paused || item.UserPaused != false) return null;
        var due = item.RecoveryAfterUtc ?? (item.NextAttemptUtc is { } previous ? previous + RecoveryCooldown : now + RecoveryCooldown);
        if (run is not null)
        {
            run = ArtistMetadataAutomationCoordinator.SnapshotRun(run);
            ResetRetry(run);
            run.NextAttemptUtc = due;
        }
        return item with { Attempts = 0, NeedsAttention = false, Reason = null, NextAttemptUtc = due,
            Run = run, UserPaused = false, RecoveryAfterUtc = null };
    }

    internal static bool RetainUnresolvedCacheWork(ArtistMetadataActiveRun run,
        IReadOnlyList<ArtistRunOutcomeRecord> outcomes, DateTimeOffset nowUtc)
    {
        var recorded = outcomes.Select(row => row.ArtistId).ToHashSet();
        var missing = (run.TargetArtistIds ?? []).Where(id => !recorded.Contains(id))
            .Select(id => new ArtistRunOutcomeRecord(id, ArtistRunOutcomes.Failed,
                CacheWork: [new ArtistCacheWorkOutcome("local", "artist", ArtistCacheWorkStatus.Failed, ReasonCode: "missing-outcome")]));
        var pending = outcomes.Concat(missing).SelectMany(row => (row.CacheWork ??
            [new ArtistCacheWorkOutcome("local", "artist", ArtistCacheWorkStatus.Failed, ReasonCode: "unknown-stage-evidence")])
            .Where(work => !work.IsComplete).Select(work => (row.ArtistId, Work: work))).ToList();
        if (pending.Count == 0) return false;
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in pending)
        {
            var key = $"{entry.ArtistId}:{entry.Work.Provider}:{entry.Work.Stage}";
            attempts[key] = Math.Min(3, run.CacheAttempts.GetValueOrDefault(key) + (entry.Work.Attempted ? 1 : 0));
        }
        run.CacheAttempts = attempts;
        var retryable = pending.Where(item => item.Work.Status != ArtistCacheWorkStatus.AuthenticationRequired
            && attempts[$"{item.ArtistId}:{item.Work.Provider}:{item.Work.Stage}"] < 3).ToList();
        var needsAttention = retryable.Count == 0;
        // Schedule the earliest eligible service. Individual stage deadlines are checked by
        // the shared executor, so a long provider cooldown cannot delay another provider.
        var retryDue = retryable.Select(item => item.Work.RetryAtUtc is { } due && due > nowUtc ? due : nowUtc + RetryInterval)
            .DefaultIfEmpty(nowUtc + RetryInterval).Min();
        run.NextAttemptUtc = needsAttention ? null : retryDue;
        run.Paused = needsAttention;
        run.Disposition = needsAttention ? ArtistMetadataRunDispositions.NeedsAttention : ArtistMetadataRunDispositions.PendingRetry;
        run.PendingNotice = needsAttention
            ? $"Refresh Cache retained {pending.Count} unfinished source update(s). Check provider access, then Resume."
            : $"Refresh Cache retained {pending.Count} unfinished source update(s) for retry.";
        return true;
    }

}
