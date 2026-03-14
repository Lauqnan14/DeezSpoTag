using DeezSpoTag.Services.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Threading;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryScanRunner
{
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly LocalLibraryScanner _scanner;
    private readonly IHostEnvironment _environment;
    private readonly IServiceProvider _serviceProvider;
    private readonly ArtistPageCacheRepository _artistCacheRepository;
    private readonly object _scanLock = new();
    private CancellationTokenSource? _activeScanCts;
    private ScanStatus _status = new(false, null, 0, 0, 0, null, 0, 0, 0);

    public LibraryScanRunner(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        LocalLibraryScanner scanner,
        IHostEnvironment environment,
        IServiceProvider serviceProvider,
        ArtistPageCacheRepository artistCacheRepository)
    {
        _repository = repository;
        _configStore = configStore;
        _scanner = scanner;
        _environment = environment;
        _serviceProvider = serviceProvider;
        _artistCacheRepository = artistCacheRepository;
    }

    public sealed record ScanStatus(
        bool IsRunning,
        DateTimeOffset? StartedAtUtc,
        int ProcessedFiles,
        int TotalFiles,
        int ErrorCount,
        string? CurrentFile,
        int ArtistsDetected,
        int AlbumsDetected,
        int TracksDetected);

    public ScanStatus GetStatus() => _status;

    public bool TryCancel()
    {
        lock (_scanLock)
        {
            if (_activeScanCts == null)
            {
                return false;
            }
            _activeScanCts.Cancel();
            return true;
        }
    }

    public Task EnqueueAsync(
        bool refreshImages,
        bool reset,
        long? folderId,
        bool skipSpotifyFetch,
        bool cacheSpotifyImages)
    {
        return Task.Run(() => RunAsync(
            refreshImages,
            reset,
            folderId,
            skipSpotifyFetch,
            cacheSpotifyImages,
            CancellationToken.None));
    }

    public async Task RunAsync(
        bool refreshImages,
        bool reset,
        long? folderId,
        bool skipSpotifyFetch,
        bool cacheSpotifyImages,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? cts = null;
        var ownsActiveScan = false;
        try
        {
            if (!TryStartScan(cancellationToken, ref cts, ref ownsActiveScan))
            {
                return;
            }

            var activeCts = cts!;
            using (activeCts)
            {
                await ResetLibraryIfRequestedAsync(reset, activeCts.Token);
                var enabledFolders = await LoadEnabledFoldersAsync(folderId, activeCts.Token);
                if (enabledFolders is null)
                {
                    return;
                }

                if (refreshImages)
                {
                    ClearThumbnailCache();
                    AddInfoLog("Thumbnail cache cleared.");
                }

                AddInfoLog($"Library scan started ({enabledFolders.Count} folders).");
                var snapshot = ScanSnapshot(enabledFolders, activeCts.Token);
                PersistSnapshot(snapshot);
                await SyncRepositoryArtifactsAsync(
                    enabledFolders,
                    snapshot,
                    reset,
                    skipSpotifyFetch,
                    refreshImages,
                    cacheSpotifyImages,
                    activeCts.Token);
                AddInfoLog($"Library scan completed ({snapshot.Artists.Count} artists, {snapshot.Albums.Count} albums, {snapshot.Tracks.Count} tracks).");
            }
        }
        catch (OperationCanceledException)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                "Library scan cancelled."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "error",
                $"Library scan failed: {ex.Message}"));
        }
        finally
        {
            lock (_scanLock)
            {
                // Only the invocation that created the active CTS can dispose/clear it.
                // Otherwise, an ignored concurrent scan request can cancel a running scan
                // by disposing that shared CTS.
                if (ownsActiveScan && cts != null && ReferenceEquals(_activeScanCts, cts))
                {
                    _activeScanCts = null;
                    _status = _status with { IsRunning = false, CurrentFile = null };
                }
            }
        }
    }

    private bool TryStartScan(CancellationToken cancellationToken, ref CancellationTokenSource? cts, ref bool ownsActiveScan)
    {
        lock (_scanLock)
        {
            if (_activeScanCts != null)
            {
                AddWarnLog("Library scan already running; new scan request ignored.");
                return false;
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeScanCts = cts;
            ownsActiveScan = true;
            _status = new ScanStatus(true, DateTimeOffset.UtcNow, 0, 0, 0, null, 0, 0, 0);
            return true;
        }
    }

    private async Task ResetLibraryIfRequestedAsync(bool reset, CancellationToken cancellationToken)
    {
        if (!reset || !_repository.IsConfigured)
        {
            return;
        }

        var cleared = await _repository.ClearLibraryDataAsync(cancellationToken);
        AddInfoLog($"Library data reset before scan (artists={cleared.ArtistsRemoved}, albums={cleared.AlbumsRemoved}, tracks={cleared.TracksRemoved}).");
    }

    private async Task<List<FolderDto>?> LoadEnabledFoldersAsync(long? folderId, CancellationToken cancellationToken)
    {
        var folders = _repository.IsConfigured
            ? await _repository.GetFoldersAsync(cancellationToken)
            : _configStore.GetFolders();
        var enabledFolders = folders
            .Where(folder => folder.Enabled)
            .ToList();

        if (!folderId.HasValue)
        {
            return enabledFolders;
        }

        var selected = enabledFolders.FirstOrDefault(folder => folder.Id == folderId.Value);
        if (selected is null)
        {
            AddErrorLog($"Library scan failed: folder {folderId.Value} not found or disabled.");
            return null;
        }

        return new List<FolderDto> { selected };
    }

    private LibraryConfigStore.LocalLibrarySnapshot ScanSnapshot(List<FolderDto> enabledFolders, CancellationToken cancellationToken)
    {
        var progress = new Progress<LocalLibraryScanner.ScanProgress>(progressUpdate =>
        {
            _status = _status with
            {
                ProcessedFiles = progressUpdate.ProcessedFiles,
                TotalFiles = progressUpdate.TotalFiles,
                ErrorCount = progressUpdate.ErrorCount,
                CurrentFile = progressUpdate.CurrentFile,
                ArtistsDetected = progressUpdate.ArtistsDetected,
                AlbumsDetected = progressUpdate.AlbumsDetected,
                TracksDetected = progressUpdate.TracksDetected
            };
        });
        var snapshot = _scanner.Scan(enabledFolders, progress, cancellationToken);
        AddInfoLog($"Library scan snapshot complete (artists={snapshot.Artists.Count}, albums={snapshot.Albums.Count}, tracks={snapshot.Tracks.Count}).");
        return snapshot;
    }

    private void PersistSnapshot(LibraryConfigStore.LocalLibrarySnapshot snapshot)
    {
        _configStore.SaveLocalLibrary(snapshot);
        _configStore.SaveLastScanInfo(new LibraryConfigStore.LastScanInfo(
            DateTimeOffset.UtcNow,
            snapshot.Artists.Count,
            snapshot.Albums.Count,
            snapshot.Tracks.Count));
    }

    private async Task SyncRepositoryArtifactsAsync(
        List<FolderDto> enabledFolders,
        LibraryConfigStore.LocalLibrarySnapshot snapshot,
        bool reset,
        bool skipSpotifyFetch,
        bool refreshImages,
        bool cacheSpotifyImages,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return;
        }

        await IngestSnapshotAsync(enabledFolders, snapshot, reset, cancellationToken);
        await StoreLocalGenresAsync(snapshot, cancellationToken);
        if (!skipSpotifyFetch)
        {
            await EnqueueSpotifyArtistMetadataAsync(cancellationToken);
        }

        if (refreshImages)
        {
            await EnqueueArtistImagesAsync(cancellationToken);
        }

        if (cacheSpotifyImages)
        {
            await CacheSpotifyImagesAsync(cancellationToken);
        }

        await EnqueueBackgroundShazamRefreshAsync(enabledFolders, cancellationToken);
    }

    private async Task IngestSnapshotAsync(
        List<FolderDto> enabledFolders,
        LibraryConfigStore.LocalLibrarySnapshot snapshot,
        bool reset,
        CancellationToken cancellationToken)
    {
        var ingestPayload = LocalLibrarySnapshotMapper.BuildIngestPayload(snapshot);
        await _repository.IngestLocalScanAsync(
            enabledFolders,
            ingestPayload.Artists,
            ingestPayload.Albums,
            ingestPayload.Tracks,
            reset,
            cancellationToken);
        AddInfoLog($"SQLite ingest completed ({ingestPayload.Artists.Count} artists, {ingestPayload.Albums.Count} albums, {ingestPayload.Tracks.Count} tracks).");
    }

    private async Task StoreLocalGenresAsync(LibraryConfigStore.LocalLibrarySnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.ArtistGenres.Count == 0)
        {
            return;
        }

        foreach (var (artistName, genres) in snapshot.ArtistGenres)
        {
            await _artistCacheRepository.UpsertGenresAsync("local", artistName, genres, cancellationToken);
        }

        AddInfoLog($"Local genres stored ({snapshot.ArtistGenres.Count} artists).");
    }

    private async Task EnqueueSpotifyArtistMetadataAsync(CancellationToken cancellationToken)
    {
        var spotifyQueueService = _serviceProvider.GetService<LibrarySpotifyArtistQueueService>();
        if (spotifyQueueService is null)
        {
            AddWarnLog("Spotify artist queue service not registered; skipping Spotify metadata fetch enqueue.");
            return;
        }

        await spotifyQueueService.EnqueueMissingAsync(cancellationToken);
    }

    private async Task EnqueueArtistImagesAsync(CancellationToken cancellationToken)
    {
        var imageQueueService = _serviceProvider.GetService<LibraryArtistImageQueueService>();
        if (imageQueueService is null)
        {
            AddWarnLog("Artist image queue service not registered; skipping image refresh enqueue.");
            return;
        }

        await imageQueueService.EnqueueMissingAsync(cancellationToken);
    }

    private async Task CacheSpotifyImagesAsync(CancellationToken cancellationToken)
    {
        var spotifyImageCacheService = _serviceProvider.GetService<SpotifyArtistImageCacheService>();
        if (spotifyImageCacheService is null)
        {
            AddWarnLog("Spotify image cache service not registered; skipping Spotify image cache step.");
            return;
        }

        await spotifyImageCacheService.CacheFromSpotifyCacheAsync(cancellationToken);
    }

    private async Task EnqueueBackgroundShazamRefreshAsync(List<FolderDto> enabledFolders, CancellationToken cancellationToken)
    {
        var recommendationService = _serviceProvider.GetService<LibraryRecommendationService>();
        if (recommendationService is null)
        {
            AddWarnLog("Library recommendation service not registered; skipping background Shazam refresh enqueue.");
            return;
        }

        var shazamQueued = 0;
        var shazamSkipped = 0;
        var scopedFolders = enabledFolders
            .Where(folder => folder.LibraryId.HasValue && folder.LibraryId.Value > 0)
            .Select(folder => new { LibraryId = folder.LibraryId!.Value, folder.Id })
            .Distinct()
            .ToList();

        foreach (var scope in scopedFolders)
        {
            var queued = await recommendationService.TriggerFullLibraryShazamScanAsync(
                scope.LibraryId,
                scope.Id,
                force: false,
                cancellationToken);

            if (queued)
            {
                shazamQueued++;
            }
            else
            {
                shazamSkipped++;
            }
        }

        if (scopedFolders.Count > 0)
        {
            AddInfoLog($"Background Shazam refresh queued for {shazamQueued} folder scope(s); skipped {shazamSkipped}.");
        }
    }

    private void AddInfoLog(string message)
        => _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, "info", message));

    private void AddWarnLog(string message)
        => _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, "warn", message));

    private void AddErrorLog(string message)
        => _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, "error", message));

    private void ClearThumbnailCache()
    {
        var thumbPath = Path.Join(_environment.ContentRootPath, "Data", "library-thumbs");
        try
        {
            if (Directory.Exists(thumbPath))
            {
                Directory.Delete(thumbPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            // Best-effort cleanup; scan can still proceed.
        }
    }
}
