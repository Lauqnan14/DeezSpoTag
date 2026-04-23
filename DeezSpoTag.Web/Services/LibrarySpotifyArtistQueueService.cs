using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using System.Linq;
using System.Threading.Channels;

namespace DeezSpoTag.Web.Services;

public sealed class LibrarySpotifyArtistQueueService : BackgroundService
{
    private const string SpotifySource = "spotify";
    private const int DefaultBatchSize = 25;
    private const int MinBatchSize = 1;
    private const int MaxBatchSize = 500;
    private const int DefaultParallelWorkers = 4;
    private const int MaxParallelWorkers = 8;
    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5) };
    private static readonly TimeSpan BatchInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DispatchInterval = TimeSpan.FromMilliseconds(120);
    private readonly LibraryRepository _repository;
    private readonly ArtistPageCacheRepository _cacheRepository;
    private readonly SpotifyArtistService _spotifyArtistService;
    private readonly SpotifyPathfinderMetadataClient _pathfinderMetadataClient;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly LibraryConfigStore _configStore;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<LibrarySpotifyArtistQueueService> _logger;
    private readonly Channel<QueueItem> _channel = Channel.CreateUnbounded<QueueItem>();
    private readonly Dictionary<long, QueueItem> _queueItems = new();
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private DateTimeOffset _lastDispatchUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAuthUnavailableLogUtc = DateTimeOffset.MinValue;
    private string QueuePath => Path.Join(AppDataPaths.GetDataRoot(_environment), "spotify-artist-queue.json");

    public sealed class Dependencies
    {
        public required LibraryRepository Repository { get; init; }
        public required ArtistPageCacheRepository CacheRepository { get; init; }
        public required SpotifyArtistService SpotifyArtistService { get; init; }
        public required SpotifyPathfinderMetadataClient PathfinderMetadataClient { get; init; }
        public required DeezSpoTagSettingsService SettingsService { get; init; }
        public required LibraryConfigStore ConfigStore { get; init; }
        public required IWebHostEnvironment Environment { get; init; }
    }

    public LibrarySpotifyArtistQueueService(
        Dependencies dependencies,
        ILogger<LibrarySpotifyArtistQueueService> logger)
    {
        _repository = dependencies.Repository;
        _cacheRepository = dependencies.CacheRepository;
        _spotifyArtistService = dependencies.SpotifyArtistService;
        _pathfinderMetadataClient = dependencies.PathfinderMetadataClient;
        _settingsService = dependencies.SettingsService;
        _configStore = dependencies.ConfigStore;
        _environment = dependencies.Environment;
        _logger = logger;
    }

    public async Task EnqueueMissingAsync(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return;
        }

        var artists = await _repository.GetArtistsAsync("all", cancellationToken);
        if (!artists.Any())
        {
            return;
        }

        var enqueued = 0;
        foreach (var artist in artists)
        {
            if (string.IsNullOrWhiteSpace(artist.Name))
            {
                continue;
            }

            if (await ShouldSkipAsync(artist.Id, cancellationToken))
            {
                continue;
            }

            if (TryEnqueue(new QueueItem(artist.Id, artist.Name, 0)))
            {
                enqueued++;
            }
        }

        if (enqueued > 0)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Spotify artist fetch queued ({enqueued} artists)."));
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        PersistentArtistQueueStore.RestoreAndReplaySnapshot(
            _channel,
            _queueItems,
            _queueLock,
            QueuePath,
            static item => item.ArtistId,
            static item => !string.IsNullOrWhiteSpace(item.ArtistName),
            _logger);

        _ = Task.Run(() => EnqueueMissingAsync(cancellationToken), cancellationToken);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configuredBatchSize = ResolveConfiguredBatchSize();
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Spotify artist metadata queue started with batch size {BatchSize} and interval {BatchIntervalSeconds}s.",
                configuredBatchSize,
                BatchInterval.TotalSeconds);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            QueueItem firstItem;
            try
            {
                firstItem = await _channel.Reader.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            configuredBatchSize = ResolveConfiguredBatchSize();
            var batch = new List<QueueItem>(configuredBatchSize)
            {
                firstItem
            };

            while (batch.Count < configuredBatchSize && _channel.Reader.TryRead(out var nextItem))
            {
                batch.Add(nextItem);
            }

            var parallelWorkers = ResolveParallelWorkers(configuredBatchSize);
            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions
                {
                    CancellationToken = stoppingToken,
                    MaxDegreeOfParallelism = parallelWorkers
                },
                async (item, token) =>
                {
                    await ProcessQueueItemAsync(item, token);
                });

            await Task.Delay(BatchInterval, stoppingToken);
        }
    }

    private async Task ProcessQueueItemAsync(QueueItem item, CancellationToken cancellationToken)
    {
        try
        {
            if (await ShouldSkipAsync(item.ArtistId, cancellationToken))
            {
                CompleteItem(item);
                return;
            }

            if (!await _pathfinderMetadataClient.HasBlobBackedAuthContextAsync(cancellationToken))
            {
                var hasLibrespotAuth = await _pathfinderMetadataClient.HasLibrespotAuthContextAsync(cancellationToken);
                RequeueWithoutRetry(item, TimeSpan.FromSeconds(45));
                MaybeLogAuthUnavailable(hasLibrespotAuth);
                return;
            }

            await WaitForDispatchSlotAsync(cancellationToken);
            var result = await _spotifyArtistService.GetArtistPageAsync(
                item.ArtistId,
                item.ArtistName,
                false,
                false,
                cancellationToken,
                includeDeezerLinking: false);

            if (result == null && !await ShouldSkipAsync(item.ArtistId, cancellationToken))
            {
                var resolvedSpotifyId = await _repository.GetArtistSourceIdAsync(item.ArtistId, SpotifySource, cancellationToken);
                if (string.IsNullOrWhiteSpace(resolvedSpotifyId))
                {
                    var hasLocalSignals = (await _repository.GetArtistSpotifyMatchSignalsAsync(
                        item.ArtistId,
                        limit: 1,
                        cancellationToken)).Count > 0;
                    if (!hasLocalSignals)
                    {
                        _logger.LogWarning(
                            "Spotify artist fetch gave up for {ArtistName}: deterministic no-match (spotify artist id unresolved, no local signals).",
                            item.ArtistName);
                        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                            DateTimeOffset.UtcNow,
                            "warn",
                            $"Spotify fetch gave up for {item.ArtistName}: unresolved Spotify artist id and no local match signals (non-retryable)."));
                        CompleteItem(item);
                        return;
                    }

                    _logger.LogWarning(
                        "Spotify artist fetch unresolved for {ArtistName}: local match signals exist, scheduling retry.",
                        item.ArtistName);
                    _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                        DateTimeOffset.UtcNow,
                        "warn",
                        $"Spotify fetch unresolved for {item.ArtistName}; local match signals exist, retry scheduled."));
                    ScheduleRetry(item, "spotify artist id unresolved");
                    return;
                }

                ScheduleRetry(item, "returned no data");
            }
            else
            {
                CompleteItem(item);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Spotify artist fetch timed out/canceled for {ArtistName}; scheduling retry.", item.ArtistName);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                $"Spotify artist fetch canceled for {item.ArtistName}; retry scheduled."));
            ScheduleRetry(item, "request canceled or timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spotify artist fetch failed for {ArtistName}", item.ArtistName);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "error",
                $"Spotify artist fetch failed for {item.ArtistName}: {ex.Message}"));
            ScheduleRetry(item, ex.Message);
        }
    }

    private void ScheduleRetry(QueueItem item, string reason)
    {
        var nextRetry = item.Retry + 1;
        if (nextRetry > MaxRetries)
        {
            _logger.LogWarning("Giving up on Spotify fetch for {ArtistName} after {MaxRetries} retries", item.ArtistName, MaxRetries);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                $"Spotify fetch gave up for {item.ArtistName} after {MaxRetries} retries ({reason})."));
            CompleteItem(item);
            return;
        }

        var delay = RetryDelays[Math.Min(nextRetry - 1, RetryDelays.Length - 1)];
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Scheduling retry {Retry}/{Max} for {ArtistName} in {Delay}", nextRetry, MaxRetries, item.ArtistName, delay);
        }

        var retryItem = item with { Retry = nextRetry };
        lock (_queueLock)
        {
            _queueItems[retryItem.ArtistId] = retryItem;
            PersistentArtistQueueStore.PersistQueueSnapshot(_queueItems, _queueLock, QueuePath);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            _channel.Writer.TryWrite(retryItem);
        });
    }

    private void RequeueWithoutRetry(QueueItem item, TimeSpan delay)
    {
        lock (_queueLock)
        {
            _queueItems[item.ArtistId] = item;
            PersistentArtistQueueStore.PersistQueueSnapshot(_queueItems, _queueLock, QueuePath);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            _channel.Writer.TryWrite(item);
        });
    }

    private void MaybeLogAuthUnavailable(bool hasLibrespotAuth)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastAuthUnavailableLogUtc < TimeSpan.FromMinutes(2))
        {
            return;
        }

        _lastAuthUnavailableLogUtc = now;
        if (hasLibrespotAuth)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                now,
                "info",
                "Spotify artist queue paused: librespot is connected, but web-player cookies are required for artist metadata."));
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Spotify artist queue paused: librespot is connected, but web-player auth context is required for artist metadata.");
            }
            return;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            now,
            "info",
            "Spotify artist queue paused: waiting for valid Spotify web-player auth context."));
        _logger.LogWarning("Spotify artist queue paused: no valid Spotify web-player auth context.");
    }

    private async Task<bool> ShouldSkipAsync(long artistId, CancellationToken cancellationToken)
    {
        var artist = await _repository.GetArtistAsync(artistId, cancellationToken);
        if (artist == null)
        {
            return true;
        }

        var spotifyId = await _repository.GetArtistSourceIdAsync(artistId, "spotify", cancellationToken);
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            return false;
        }

        var cached = await _cacheRepository.TryGetAsync("spotify", spotifyId, cancellationToken);
        if (cached == null)
        {
            return false;
        }

        return _cacheRepository.IsFresh(cached.FetchedUtc);
    }

    private bool TryEnqueue(QueueItem item)
    {
        return PersistentArtistQueueStore.TryEnqueue(
            item,
            _channel,
            _queueItems,
            _queueLock,
            QueuePath,
            static queuedItem => queuedItem.ArtistId);
    }

    private void CompleteItem(QueueItem item)
    {
        PersistentArtistQueueStore.CompleteItem(
            item,
            _queueItems,
            _queueLock,
            QueuePath,
            static queuedItem => queuedItem.ArtistId);
    }

    private async Task WaitForDispatchSlotAsync(CancellationToken cancellationToken)
    {
        await _dispatchGate.WaitAsync(cancellationToken);
        try
        {
            var sinceLastDispatch = DateTimeOffset.UtcNow - _lastDispatchUtc;
            if (sinceLastDispatch < DispatchInterval)
            {
                await Task.Delay(DispatchInterval - sinceLastDispatch, cancellationToken);
            }

            _lastDispatchUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    private int ResolveConfiguredBatchSize()
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            var configured = settings.SpotifyArtistMetadataFetchBatchSize;
            if (configured < MinBatchSize)
            {
                return DefaultBatchSize;
            }

            return Math.Clamp(configured, MinBatchSize, MaxBatchSize);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read Spotify artist metadata batch size setting; using default.");
            return DefaultBatchSize;
        }
    }

    private static int ResolveParallelWorkers(int batchSize)
    {
        if (batchSize <= 1)
        {
            return 1;
        }

        // Keep concurrency bounded while still allowing parallel in-flight fetches.
        return Math.Clamp(Math.Min(DefaultParallelWorkers, batchSize), 1, MaxParallelWorkers);
    }

    private sealed record QueueItem(long ArtistId, string ArtistName, int Retry = 0);
}
