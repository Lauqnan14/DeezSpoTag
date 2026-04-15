using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Apple;
using System.Linq;
using System.Threading.Channels;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryArtistImageQueueService : BackgroundService
{
    private static readonly HttpClient ImageHttpClient = new();
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly DeezerArtistImageService _imageService;
    private readonly AppleMusicCatalogService _appleCatalogService;
    private readonly ISpotifyArtworkResolver _spotifyArtworkResolver;
    private readonly ILogger<LibraryArtistImageQueueService> _logger;
    private readonly Channel<QueueItem> _channel = Channel.CreateUnbounded<QueueItem>();
    private readonly Dictionary<long, QueueItem> _queueItems = new();
    private readonly object _queueLock = new();
    private readonly string _dataRoot;
    private string QueuePath => Path.Join(_dataRoot, "artist-image-queue.json");
    private string ImageCacheDir => Path.Join(_dataRoot, "library-artist-images");

    public LibraryArtistImageQueueService(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        DeezerArtistImageService imageService,
        AppleMusicCatalogService appleCatalogService,
        ISpotifyArtworkResolver spotifyArtworkResolver,
        IWebHostEnvironment environment,
        ILogger<LibraryArtistImageQueueService> logger)
    {
        _repository = repository;
        _configStore = configStore;
        _imageService = imageService;
        _appleCatalogService = appleCatalogService;
        _spotifyArtworkResolver = spotifyArtworkResolver;
        _logger = logger;
        _dataRoot = AppDataPaths.GetDataRoot(environment);
    }

    public async Task EnqueueMissingAsync(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return;
        }

        var missing = await _repository.GetArtistsMissingImageAsync(cancellationToken);
        if (!missing.Any())
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                "Artist image fetch skipped; no missing images."));
            return;
        }

        var enqueued = 0;
        foreach (var artist in missing)
        {
            if (string.IsNullOrWhiteSpace(artist.Name))
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
                $"Artist image fetch queued ({enqueued} artists)."));
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

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessQueueItemAsync(item, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Artist image fetch failed for {ArtistName}", item.ArtistName);
                _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                    DateTimeOffset.UtcNow,
                    "error",
                    $"Artist image fetch failed for {item.ArtistName}: {ex.Message}"));
            }
            finally
            {
                CompleteItem(item);
            }
        }
    }

    private async Task ProcessQueueItemAsync(QueueItem item, CancellationToken cancellationToken)
    {
        if (!await ShouldFetchAsync(item, cancellationToken))
        {
            return;
        }

        var imagePath = await ResolveImagePathAsync(item, cancellationToken);
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            await _repository.UpdateArtistImagePathAsync(item.ArtistId, imagePath, cancellationToken);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Artist image saved for {item.ArtistName}."));
            return;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "warn",
            $"Artist image not found for {item.ArtistName}."));
    }

    private async Task<string?> ResolveImagePathAsync(QueueItem item, CancellationToken cancellationToken)
    {
        var appleUrl = await TryResolveAppleArtistImageAsync(item.ArtistName, cancellationToken);
        if (!string.IsNullOrWhiteSpace(appleUrl))
        {
            var applePath = await DownloadImageAsync(item.ArtistId, appleUrl, cancellationToken);
            if (!string.IsNullOrWhiteSpace(applePath))
            {
                return applePath;
            }
        }

        var deezerPath = await _imageService.DownloadArtistImageAsync(
            item.ArtistId,
            item.ArtistName,
            ImageCacheDir,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(deezerPath))
        {
            return deezerPath;
        }

        var spotifyUrl = await _spotifyArtworkResolver.ResolveArtistImageByNameAsync(item.ArtistName, cancellationToken);
        if (string.IsNullOrWhiteSpace(spotifyUrl))
        {
            return null;
        }

        return await DownloadImageAsync(item.ArtistId, spotifyUrl, cancellationToken);
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

    private async Task<bool> ShouldFetchAsync(QueueItem item, CancellationToken cancellationToken)
    {
        var artist = await _repository.GetArtistAsync(item.ArtistId, cancellationToken);
        if (artist is null)
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(artist.PreferredImagePath) && File.Exists(artist.PreferredImagePath))
        {
            return false;
        }
        return true;
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

    private async Task<string?> TryResolveAppleArtistImageAsync(string artistName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        try
        {
            return await AppleQueueHelpers.ResolveAppleArtistImageAsync(
                _appleCatalogService,
                artistName,
                "us",
                1200,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple artist lookup failed for {ArtistName}", artistName);
            }
            return null;
        }
    }

    private async Task<string?> DownloadImageAsync(long artistId, string imageUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        using var response = await ImageHttpClient.GetAsync(imageUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        Directory.CreateDirectory(ImageCacheDir);
        var extension = ResolveExtension(response.Content.Headers.ContentType?.MediaType);
        var fileName = $"{artistId}{extension}";
        var targetPath = Path.Join(ImageCacheDir, fileName);
        await using var targetStream = File.Create(targetPath);
        await response.Content.CopyToAsync(targetStream, cancellationToken);
        return targetPath;
    }

    private static string ResolveExtension(string? mediaType)
        => mediaType switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => ".jpg"
        };

    private sealed record QueueItem(long ArtistId, string ArtistName);
}
