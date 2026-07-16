using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Apple;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using System.Linq;
using System.Threading.Channels;
using DeezSpoTag.Services.Download.Shared;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryArtistImageQueueService : BackgroundService
{
    private static readonly HttpClient ImageHttpClient = new();
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly AppleMusicCatalogService _appleCatalogService;
    private readonly ISpotifyArtworkResolver _spotifyArtworkResolver;
    private readonly ILastFmArtistImageResolver _lastFmArtistImageResolver;
    private readonly DeezSpoTag.Integrations.Deezer.DeezerClient _deezerClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ILogger<LibraryArtistImageQueueService> _logger;
    private readonly Channel<QueueItem> _channel = Channel.CreateUnbounded<QueueItem>();
    private readonly Dictionary<long, QueueItem> _queueItems = new();
    private readonly object _queueLock = new();
    private readonly string _dataRoot;
    private string QueuePath => Path.Join(_dataRoot, "artist-image-queue.json");
    private string ImageCacheDir => Path.Join(_dataRoot, "library-artist-images");

    public LibraryArtistImageQueueService(
        LibraryArtistImageQueueDependencies dependencies,
        ILogger<LibraryArtistImageQueueService> logger)
    {
        _repository = dependencies.Repository;
        _configStore = dependencies.ConfigStore;
        _appleCatalogService = dependencies.Providers.AppleCatalogService;
        _spotifyArtworkResolver = dependencies.Providers.SpotifyArtworkResolver;
        _lastFmArtistImageResolver = dependencies.Providers.LastFmArtistImageResolver;
        _deezerClient = dependencies.Providers.DeezerClient;
        _httpClientFactory = dependencies.Providers.HttpClientFactory;
        _settingsService = dependencies.SettingsService;
        _logger = logger;
        _dataRoot = AppDataPaths.GetDataRoot(dependencies.Environment);
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

        var enqueued = await PersistentArtistQueueStore.EnqueueArtistsAsync(
            missing,
            static artist => artist.Id,
            static artist => artist.Name,
            static (_, _) => ValueTask.FromResult(false),
            static (artistId, artistName) => new QueueItem(artistId, artistName),
            TryEnqueue,
            cancellationToken);

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
        var settings = _settingsService.LoadSettings();
        var appleArtistId = await _repository.GetArtistSourceIdAsync(item.ArtistId, "apple", cancellationToken)
            ?? await _repository.GetArtistSourceIdAsync(item.ArtistId, "applemusic", cancellationToken)
            ?? await _repository.GetArtistSourceIdAsync(item.ArtistId, "itunes", cancellationToken);
        var deezerArtistId = await _repository.GetArtistSourceIdAsync(item.ArtistId, "deezer", cancellationToken);
        var spotifyArtistId = await _repository.GetArtistSourceIdAsync(item.ArtistId, "spotify", cancellationToken);
        var resolution = await DownloadEngineArtworkHelper.ResolveArtistArtworkAsync(
            new DownloadEngineArtworkHelper.ArtistImageResolveRequest(
                _appleCatalogService,
                _httpClientFactory,
                settings,
                _deezerClient,
                _spotifyArtworkResolver,
                _lastFmArtistImageResolver,
                null,
                null,
                null,
                item.ArtistName,
                _logger)
            {
                AppleArtistId = appleArtistId,
                DeezerArtistId = deezerArtistId,
                SpotifyArtistId = spotifyArtistId
            },
            cancellationToken);
        if (resolution == null)
        {
            return null;
        }

        var imagePath = await DownloadImageAsync(item.ArtistId, resolution.Url, cancellationToken, resolution.Provider);
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var dimensions = await DownloadEngineArtworkHelper.ReadSquareArtistArtworkDimensionsAsync(imagePath, cancellationToken);
        if (dimensions == null)
        {
            File.Delete(imagePath);
            return null;
        }

        await _repository.UpsertArtistArtworkCacheAsync(
            new ArtistArtworkCacheUpsertInput(
                item.ArtistId,
                "avatar",
                $"{resolution.Provider}:{resolution.ProviderArtistId ?? item.ArtistName}",
                resolution.Provider,
                resolution.Url,
                imagePath,
                null,
                dimensions.Value.Width,
                dimensions.Value.Height,
                "not_scanned",
                null,
                false,
                false),
            cancellationToken);
        return imagePath;
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
        if (string.IsNullOrWhiteSpace(artist.PreferredImagePath) || !File.Exists(artist.PreferredImagePath))
        {
            return true;
        }

        var dimensions = await DownloadEngineArtworkHelper.ReadSquareArtistArtworkDimensionsAsync(
            artist.PreferredImagePath,
            cancellationToken);
        if (dimensions == null)
        {
            return true;
        }

        var settings = _settingsService.LoadSettings();
        var preferredProvider = ArtworkFallbackHelper.ResolveArtistOrder(settings).FirstOrDefault();
        var provenance = await _repository.GetArtistArtworkProvenanceAsync(
            item.ArtistId,
            "avatar",
            artist.PreferredImagePath,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(provenance?.Source)
            && string.Equals(provenance.Source, preferredProvider, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (provenance == null)
        {
            await _repository.UpsertArtistArtworkCacheAsync(
                new ArtistArtworkCacheUpsertInput(
                    item.ArtistId,
                    "avatar",
                    $"legacy:{Path.GetFileName(artist.PreferredImagePath)}",
                    "unknown",
                    null,
                    artist.PreferredImagePath,
                    null,
                    dimensions.Value.Width,
                    dimensions.Value.Height,
                    "not_scanned",
                    null,
                    false,
                    false),
                cancellationToken);
        }

        return DownloadEngineArtworkHelper.ShouldRefreshExistingArtistArtwork(
            provenance?.Source,
            preferredProvider,
            settings.OverwriteFile);
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

    private async Task<string?> DownloadImageAsync(
        long artistId,
        string imageUrl,
        CancellationToken cancellationToken,
        string? source = null)
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

        var cacheDir = string.IsNullOrWhiteSpace(source)
            ? ImageCacheDir
            : Path.Join(ImageCacheDir, source.Trim().ToLowerInvariant());
        Directory.CreateDirectory(cacheDir);
        var extension = ResolveExtension(response.Content.Headers.ContentType?.MediaType);
        var fileName = $"{artistId}{extension}";
        var targetPath = Path.Join(cacheDir, fileName);
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
