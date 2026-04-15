using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using System.Threading;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryScanRunner
{
    private static readonly bool DefaultLivePreviewIngestEnabled = ReadBooleanEnvironmentVariable("DEEZSPOTAG_LIBRARY_LIVE_INGEST", defaultValue: false);
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly LocalLibraryScanner _scanner;
    private readonly IHostEnvironment _environment;
    private readonly IServiceProvider _serviceProvider;
    private readonly ArtistPageCacheRepository _artistCacheRepository;
    private readonly ILogger<LibraryScanRunner> _logger;
    private readonly string _scanCheckpointPath;
    private readonly object _scanLock = new();
    private readonly object _previewIngestLock = new();
    private CancellationTokenSource? _activeScanCts;
    private ScanStatus _status = new(false, null, 0, 0, 0, null, 0, 0, 0);

    public LibraryScanRunner(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        LocalLibraryScanner scanner,
        IHostEnvironment environment,
        IServiceProvider serviceProvider,
        ArtistPageCacheRepository artistCacheRepository,
        ILogger<LibraryScanRunner> logger)
    {
        _repository = repository;
        _configStore = configStore;
        _scanner = scanner;
        _environment = environment;
        _serviceProvider = serviceProvider;
        _artistCacheRepository = artistCacheRepository;
        _logger = logger;
        var dataRoot = AppDataPathResolver.ResolveDataRootOrDefault(Path.Join(environment.ContentRootPath, "Data"));
        var checkpointDirectory = Path.Join(dataRoot, "library-scan");
        Directory.CreateDirectory(checkpointDirectory);
        _scanCheckpointPath = Path.Join(checkpointDirectory, "checkpoint.json");
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

    private sealed record ScanResumeState(
        List<FolderDto> FoldersToScan,
        ScanProgressOffset ProgressOffset,
        Dictionary<string, HashSet<string>> ArtistGenres,
        bool Resumed);

    private sealed class ScanCheckpointState
    {
        public long? FolderId { get; set; }
        public List<long> RemainingFolderIds { get; set; } = new();
        public int ProcessedFiles { get; set; }
        public int TotalFiles { get; set; }
        public int ErrorCount { get; set; }
        public Dictionary<string, List<string>> ArtistGenres { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct ScanProgressOffset(int ProcessedFiles, int TotalFiles, int ErrorCount);

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
                var enabledFolders = await LoadEnabledFoldersAsync(folderId, activeCts.Token);
                if (enabledFolders is null)
                {
                    return;
                }

                await ResetLibraryIfRequestedAsync(reset, folderId, enabledFolders, activeCts.Token);

                if (refreshImages)
                {
                    ClearThumbnailCache();
                    AddInfoLog("Thumbnail cache cleared.");
                }

                var resumeState = BuildScanResumeState(folderId, enabledFolders, reset);
                var livePreviewIngestEnabled = await ResolveLivePreviewIngestEnabledAsync(activeCts.Token);
                if (resumeState.ProgressOffset.ProcessedFiles > 0
                    || resumeState.ProgressOffset.TotalFiles > 0
                    || resumeState.ProgressOffset.ErrorCount > 0)
                {
                    _status = _status with
                    {
                        ProcessedFiles = resumeState.ProgressOffset.ProcessedFiles,
                        TotalFiles = resumeState.ProgressOffset.TotalFiles,
                        ErrorCount = resumeState.ProgressOffset.ErrorCount
                    };
                }

                PersistScanCheckpoint(
                    folderId,
                    resumeState.FoldersToScan.Select(static folder => folder.Id),
                    resumeState.ProgressOffset,
                    resumeState.ArtistGenres);

                AddInfoLog(resumeState.Resumed
                    ? $"Library scan resumed ({resumeState.FoldersToScan.Count}/{enabledFolders.Count} folders remaining)."
                    : $"Library scan started ({enabledFolders.Count} folders).");
                var scanResult = await ScanAndIngestIncrementally(
                    enabledFolders,
                    resumeState.FoldersToScan,
                    resumeState.ProgressOffset,
                    resumeState.ArtistGenres,
                    livePreviewIngestEnabled,
                    folderId,
                    activeCts.Token);
                PersistScanInfo(scanResult.ArtistCount, scanResult.AlbumCount, scanResult.TrackCount);
                await SyncRepositoryArtifactsAsync(
                    enabledFolders,
                    scanResult.ArtistGenres,
                    skipSpotifyFetch,
                    refreshImages,
                    cacheSpotifyImages,
                    activeCts.Token);
                ClearScanCheckpoint();
                AddInfoLog($"Library scan completed ({scanResult.ArtistCount} artists, {scanResult.AlbumCount} albums, {scanResult.TrackCount} tracks).");
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Library scan cancelled.");
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warn",
                "Library scan cancelled."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Library scan failed.");
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

    private async Task ResetLibraryIfRequestedAsync(
        bool reset,
        long? folderId,
        IReadOnlyList<FolderDto> enabledFolders,
        CancellationToken cancellationToken)
    {
        if (!reset || !_repository.IsConfigured)
        {
            return;
        }

        if (folderId.HasValue)
        {
            var selectedFolder = enabledFolders.FirstOrDefault(folder => folder.Id == folderId.Value);
            if (selectedFolder is null)
            {
                return;
            }

            await _repository.ClearFolderLocalContentAsync(folderId.Value, cancellationToken);
            AddInfoLog($"Library data reset before scan for folder {selectedFolder.DisplayName}.");
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
        List<FolderDto> foldersToScan,
        ScanProgressOffset initialProgressOffset,
        Dictionary<string, HashSet<string>> initialArtistGenres,
        bool livePreviewIngestEnabled,
        long? requestedFolderId,
        CancellationToken cancellationToken)
    {
        var aggregatedGenres = CloneArtistGenres(initialArtistGenres);
        var progressOffset = initialProgressOffset;
        var remainingFolderIds = foldersToScan.Select(static folder => folder.Id).ToList();

        for (var i = 0; i < foldersToScan.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = foldersToScan[i];
            AddInfoLog($"Scanning folder {i + 1}/{foldersToScan.Count}: {folder.DisplayName}.");

            var folderSnapshot = ScanSingleFolderSnapshot(
                folder,
                progressOffset,
                livePreviewIngestEnabled,
                out var folderProcessed,
                out var folderTotal,
                out var folderErrors,
                cancellationToken);

            progressOffset = new ScanProgressOffset(
                progressOffset.ProcessedFiles + folderProcessed,
                progressOffset.TotalFiles + folderTotal,
                progressOffset.ErrorCount + folderErrors);

            MergeGenres(aggregatedGenres, folderSnapshot.ArtistGenres);

            if (_repository.IsConfigured)
            {
                await IngestSnapshotAsync(
                    enabledFolders,
                    folderSnapshot,
                    reset: false,
                    logCompletion: true,
                    cancellationToken: cancellationToken);
                var liveStats = await _repository.GetLibraryStatsAsync(cancellationToken);
                _status = _status with
                {
                    ArtistsDetected = liveStats.TotalArtists,
                    AlbumsDetected = liveStats.TotalAlbums,
                    TracksDetected = liveStats.TotalTracks,
                    CurrentFile = null
                };
                AddInfoLog($"Folder indexed ({i + 1}/{foldersToScan.Count}): {folder.DisplayName}.");
            }

            remainingFolderIds.Remove(folder.Id);
            PersistScanCheckpoint(requestedFolderId, remainingFolderIds, progressOffset, aggregatedGenres);
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

    private ScanResumeState BuildScanResumeState(long? folderId, List<FolderDto> enabledFolders, bool resetRequested)
    {
        if (resetRequested)
        {
            ClearScanCheckpoint();
            return new ScanResumeState(
                enabledFolders,
                new ScanProgressOffset(0, 0, 0),
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
                Resumed: false);
        }

        if (enabledFolders.Count == 0)
        {
            ClearScanCheckpoint();
            return new ScanResumeState(
                enabledFolders,
                new ScanProgressOffset(0, 0, 0),
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
                Resumed: false);
        }

        var checkpoint = LoadScanCheckpoint();
        if (checkpoint is null)
        {
            return new ScanResumeState(
                enabledFolders,
                new ScanProgressOffset(0, 0, 0),
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
                Resumed: false);
        }

        if (checkpoint.FolderId != folderId || checkpoint.RemainingFolderIds.Count == 0)
        {
            ClearScanCheckpoint();
            return new ScanResumeState(
                enabledFolders,
                new ScanProgressOffset(0, 0, 0),
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
                Resumed: false);
        }

        var foldersById = enabledFolders.ToDictionary(static folder => folder.Id);
        var foldersToScan = checkpoint.RemainingFolderIds
            .Distinct()
            .Where(foldersById.ContainsKey)
            .Select(id => foldersById[id])
            .ToList();
        if (foldersToScan.Count == 0)
        {
            ClearScanCheckpoint();
            return new ScanResumeState(
                enabledFolders,
                new ScanProgressOffset(0, 0, 0),
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
                Resumed: false);
        }

        var restoredGenres = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in checkpoint.ArtistGenres)
        {
            var values = pair.Value
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (values.Count > 0)
            {
                restoredGenres[pair.Key] = values;
            }
        }

        return new ScanResumeState(
            foldersToScan,
            new ScanProgressOffset(
                Math.Max(0, checkpoint.ProcessedFiles),
                Math.Max(0, checkpoint.TotalFiles),
                Math.Max(0, checkpoint.ErrorCount)),
            restoredGenres,
            Resumed: true);
    }

    private ScanCheckpointState? LoadScanCheckpoint()
    {
        try
        {
            if (!File.Exists(_scanCheckpointPath))
            {
                return null;
            }

            var json = File.ReadAllText(_scanCheckpointPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ScanCheckpointState>(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load library scan checkpoint from {CheckpointPath}.", _scanCheckpointPath);
            return null;
        }
    }

    private void PersistScanCheckpoint(
        long? folderId,
        IEnumerable<long> remainingFolderIds,
        ScanProgressOffset progressOffset,
        IReadOnlyDictionary<string, HashSet<string>> artistGenres)
    {
        try
        {
            var checkpoint = new ScanCheckpointState
            {
                FolderId = folderId,
                RemainingFolderIds = remainingFolderIds.ToList(),
                ProcessedFiles = progressOffset.ProcessedFiles,
                TotalFiles = progressOffset.TotalFiles,
                ErrorCount = progressOffset.ErrorCount,
                ArtistGenres = artistGenres.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase)
            };

            var json = JsonSerializer.Serialize(checkpoint);
            var tempPath = $"{_scanCheckpointPath}.tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _scanCheckpointPath, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to persist library scan checkpoint at {CheckpointPath}.", _scanCheckpointPath);
        }
    }

    private void ClearScanCheckpoint()
    {
        try
        {
            if (File.Exists(_scanCheckpointPath))
            {
                File.Delete(_scanCheckpointPath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to clear library scan checkpoint at {CheckpointPath}.", _scanCheckpointPath);
        }
    }

    private static Dictionary<string, HashSet<string>> CloneArtistGenres(
        IReadOnlyDictionary<string, HashSet<string>> source)
    {
        var clone = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            clone[pair.Key] = new HashSet<string>(pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        return clone;
    }

    private LibraryConfigStore.LocalLibrarySnapshot ScanSingleFolderSnapshot(
        FolderDto folder,
        ScanProgressOffset progressOffset,
        bool livePreviewIngestEnabled,
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
                ProcessedFiles = progressOffset.ProcessedFiles + progressUpdate.ProcessedFiles,
                TotalFiles = progressOffset.TotalFiles + progressUpdate.TotalFiles,
                ErrorCount = progressOffset.ErrorCount + progressUpdate.ErrorCount,
                CurrentFile = progressUpdate.CurrentFile,
                ArtistsDetected = Math.Max(currentStatus.ArtistsDetected, progressUpdate.ArtistsDetected),
                AlbumsDetected = Math.Max(currentStatus.AlbumsDetected, progressUpdate.AlbumsDetected),
                TracksDetected = Math.Max(currentStatus.TracksDetected, progressUpdate.TracksDetected)
            };
        });

        var snapshot = _scanner.Scan(
            [folder],
            progress,
            livePreviewIngestEnabled
                ? partialSnapshot => TryIngestLiveFolderSnapshot(folder, partialSnapshot, cancellationToken)
                : null,
            cancellationToken);
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

            foreach (var genre in pair.Value.Where(static value => !string.IsNullOrWhiteSpace(value)))
            {
                genreSet.Add(genre.Trim());
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

    private async Task<bool> ResolveLivePreviewIngestEnabledAsync(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return DefaultLivePreviewIngestEnabled;
        }

        var settings = await _repository.GetSettingsAsync(cancellationToken);
        return settings.LivePreviewIngest;
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
        bool logCompletion,
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
        if (logCompletion)
        {
            AddInfoLog($"SQLite ingest completed ({ingestPayload.Artists.Count} artists, {ingestPayload.Albums.Count} albums, {ingestPayload.Tracks.Count} tracks).");
        }
    }

    private void TryIngestLiveFolderSnapshot(
        FolderDto folder,
        LibraryConfigStore.LocalLibrarySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured || snapshot.Tracks.Count == 0)
        {
            return;
        }

        lock (_previewIngestLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                IngestSnapshotAsync(
                    [folder],
                    snapshot,
                    reset: false,
                    logCompletion: false,
                    cancellationToken: cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live scan ingest failed for {FolderDisplayName}.", folder.DisplayName);
                AddWarnLog($"Live scan ingest failed for {folder.DisplayName}: {ex.Message}");
            }
        }
    }

    private async Task StoreLocalGenresAsync(
        Dictionary<string, List<string>> artistGenres,
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort cleanup; scan can still proceed.
        }
    }

    private static bool ReadBooleanEnvironmentVariable(string variableName, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }
}
