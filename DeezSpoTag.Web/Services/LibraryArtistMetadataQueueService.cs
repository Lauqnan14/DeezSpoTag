using System.Threading.Channels;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryArtistMetadataQueueService : BackgroundService
{
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly ArtistMetadataCacheRefreshService _cacheRefresh;
    private readonly ArtistPageCacheRepository _artistPageCache;
    private readonly ILogger<LibraryArtistMetadataQueueService> _logger;
    private readonly Channel<QueueItem> _channel = Channel.CreateUnbounded<QueueItem>();
    private readonly Dictionary<long, QueueItem> _queueItems = new();
    private readonly object _queueLock = new();
    private readonly string _queuePath;

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
        _artistPageCache = artistPageCache;
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

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        PersistentArtistQueueStore.RestoreAndReplaySnapshot(
            _channel,
            _queueItems,
            _queueLock,
            _queuePath,
            static item => item.ArtistId,
            static item => !string.IsNullOrWhiteSpace(item.ArtistName),
            _logger);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _cacheRefresh.RefreshArtistAsync(
                    item.ArtistId,
                    item.ArtistName,
                    "auto",
                    includePopularSongs: true,
                    stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Artist metadata fetch failed for {ArtistName}.", item.ArtistName);
            }
            finally
            {
                PersistentArtistQueueStore.CompleteItem(
                    item,
                    _queueItems,
                    _queueLock,
                    _queuePath,
                    static queued => queued.ArtistId);
            }
        }
    }

    private async ValueTask<bool> ShouldSkipAsync(ArtistDto artist, CancellationToken cancellationToken)
    {
        var biographies = await _repository.GetArtistBiographyRowsAsync(artist.Id, cancellationToken);
        if (biographies.Count == 0)
        {
            return false;
        }

        var apple = await _artistPageCache.TryGetAsync(
            ArtistMediaExtrasCacheService.CacheSource,
            $"{artist.Id}:apple",
            cancellationToken);
        var tidal = await _artistPageCache.TryGetAsync(
            ArtistMediaExtrasCacheService.CacheSource,
            $"{artist.Id}:tidal",
            cancellationToken);
        return apple is not null && tidal is not null;
    }

    private bool TryEnqueue(QueueItem item)
        => PersistentArtistQueueStore.TryEnqueue(
            item,
            _channel,
            _queueItems,
            _queueLock,
            _queuePath,
            static queued => queued.ArtistId);

    private sealed record QueueItem(long ArtistId, string ArtistName);
}
