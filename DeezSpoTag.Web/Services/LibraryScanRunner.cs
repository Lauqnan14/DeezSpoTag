using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Utils;
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

    private sealed record IncrementalScanResult(
        int ArtistCount,
        int AlbumCount,
        int TrackCount,
        Dictionary<string, List<string>> ArtistGenres);

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
                var scanResult = await ScanAndIngestIncrementally(enabledFolders, activeCts.Token);
                PersistScanInfo(scanResult.ArtistCount, scanResult.AlbumCount, scanResult.TrackCount);
                await SyncRepositoryArtifactsAsync(
                    enabledFolders,
                    scanResult.ArtistGenres,
                    skipSpotifyFetch,
                    refreshImages,
                    cacheSpotifyImages,
                    activeCts.Token);
                AddInfoLog($"Library scan completed ({scanResult.ArtistCount} artists, {scanResult.AlbumCount} albums, {scanResult.TrackCount} tracks).");
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

    private async Task<IncrementalScanResult> ScanAndIngestIncrementally(
        List<FolderDto> enabledFolders,
        CancellationToken cancellationToken)
    {
        var aggregatedGenres = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var processedOffset = 0;
        var totalOffset = 0;
        var errorOffset = 0;

        for (var i = 0; i < enabledFolders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = enabledFolders[i];
            AddInfoLog($"Scanning folder {i + 1}/{enabledFolders.Count}: {folder.DisplayName}.");

            var folderSnapshot = ScanSingleFolderSnapshot(
                folder,
                processedOffset,
                totalOffset,
                errorOffset,
                out var folderProcessed,
                out var folderTotal,
                out var folderErrors,
                cancellationToken);

            processedOffset += folderProcessed;
            totalOffset += folderTotal;
            errorOffset += folderErrors;

            MergeGenres(aggregatedGenres, folderSnapshot.ArtistGenres);

            if (_repository.IsConfigured)
            {
                await IngestSnapshotAsync(enabledFolders, folderSnapshot, reset: false, cancellationToken);
                var liveStats = await _repository.GetLibraryStatsAsync(cancellationToken);
                _status = _status with
                {
                    ArtistsDetected = liveStats.TotalArtists,
                    AlbumsDetected = liveStats.TotalAlbums,
                    TracksDetected = liveStats.TotalTracks,
                    CurrentFile = null
                };
                AddInfoLog($"Folder indexed ({i + 1}/{enabledFolders.Count}): {folder.DisplayName}.");
            }
        }

        var finalCounts = await ResolveFinalCountsAsync(cancellationToken);
        _status = _status with
        {
            ArtistsDetected = finalCounts.Artists,
            AlbumsDetected = finalCounts.Albums,
            TracksDetected = finalCounts.Tracks,
            CurrentFile = null
        };

        AddInfoLog($"Library scan snapshot complete (artists={finalCounts.Artists}, albums={finalCounts.Albums}, tracks={finalCounts.Tracks}).");
        return new IncrementalScanResult(
            finalCounts.Artists,
            finalCounts.Albums,
            finalCounts.Tracks,
            aggregatedGenres.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase));
    }

    private LibraryConfigStore.LocalLibrarySnapshot ScanSingleFolderSnapshot(
        FolderDto folder,
        int processedOffset,
        int totalOffset,
        int errorOffset,
        out int folderProcessed,
        out int folderTotal,
        out int folderErrors,
        CancellationToken cancellationToken)
    {
        var latestProcessed = 0;
        var latestTotal = 0;
        var latestErrors = 0;
        var progress = new Progress<LocalLibraryScanner.ScanProgress>(progressUpdate =>
        {
            latestProcessed = progressUpdate.ProcessedFiles;
            latestTotal = progressUpdate.TotalFiles;
            latestErrors = progressUpdate.ErrorCount;

            var currentStatus = _status;
            _status = currentStatus with
            {
                ProcessedFiles = processedOffset + progressUpdate.ProcessedFiles,
                TotalFiles = totalOffset + progressUpdate.TotalFiles,
                ErrorCount = errorOffset + progressUpdate.ErrorCount,
                CurrentFile = progressUpdate.CurrentFile,
                ArtistsDetected = Math.Max(currentStatus.ArtistsDetected, progressUpdate.ArtistsDetected),
                AlbumsDetected = Math.Max(currentStatus.AlbumsDetected, progressUpdate.AlbumsDetected),
                TracksDetected = Math.Max(currentStatus.TracksDetected, progressUpdate.TracksDetected)
            };
        });

        var snapshot = _scanner.Scan([folder], progress, cancellationToken);
        folderProcessed = latestProcessed;
        folderTotal = latestTotal;
        folderErrors = latestErrors;
        return snapshot;
    }

    private static void MergeGenres(
        Dictionary<string, HashSet<string>> target,
        IReadOnlyDictionary<string, List<string>> source)
    {
        foreach (var pair in source)
        {
            if (!target.TryGetValue(pair.Key, out var genreSet))
            {
                genreSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                target[pair.Key] = genreSet;
            }

            foreach (var genre in pair.Value)
            {
                if (!string.IsNullOrWhiteSpace(genre))
                {
                    genreSet.Add(genre.Trim());
                }
            }
        }
    }

    private async Task<(int Artists, int Albums, int Tracks)> ResolveFinalCountsAsync(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return (_status.ArtistsDetected, _status.AlbumsDetected, _status.TracksDetected);
        }

        var stats = await _repository.GetLibraryStatsAsync(cancellationToken);
        return (stats.TotalArtists, stats.TotalAlbums, stats.TotalTracks);
    }

    private void PersistScanInfo(int artistCount, int albumCount, int trackCount)
    {
        _configStore.SaveLastScanInfo(new LibraryConfigStore.LastScanInfo(
            DateTimeOffset.UtcNow,
            artistCount,
            albumCount,
            trackCount));
    }

    private async Task SyncRepositoryArtifactsAsync(
        List<FolderDto> enabledFolders,
        Dictionary<string, List<string>> artistGenres,
        bool skipSpotifyFetch,
        bool refreshImages,
        bool cacheSpotifyImages,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return;
        }

        await StoreLocalGenresAsync(artistGenres, cancellationToken);
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

    private async Task StoreLocalGenresAsync(
        IReadOnlyDictionary<string, List<string>> artistGenres,
        CancellationToken cancellationToken)
    {
        if (artistGenres.Count == 0)
        {
            return;
        }

        foreach (var (artistName, genres) in artistGenres)
        {
            await _artistCacheRepository.UpsertGenresAsync("local", artistName, genres, cancellationToken);
        }

        AddInfoLog($"Local genres stored ({artistGenres.Count} artists).");
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
        var dataRoot = AppDataPathResolver.ResolveDataRootOrDefault(Path.Join(_environment.ContentRootPath, "Data"));
        var thumbPath = Path.Join(dataRoot, "library-thumbs");
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
