using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using System.Threading;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryScanRunner
{
    private readonly record struct FolderScanSnapshotResult(
        LibraryConfigStore.LocalLibrarySnapshot Snapshot,
        int ProcessedFiles,
        int TotalFiles,
        int ErrorCount);
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
    private readonly object _changedFileScanLock = new();
    private readonly Dictionary<long, HashSet<string>> _pendingChangedFileScans = new();
    private readonly Dictionary<long, PendingScanRequest> _pendingFolderScans = new();
    private CancellationTokenSource? _activeScanCts;
    private TaskCompletionSource<object?>? _activeScanCompletion;
    private TaskCompletionSource<object?>? _changedFileScanDrainCompletion;
    private PendingScanRequest? _pendingFullScan;
    private bool _changedFileScanDrainRunning;
    private bool _pendingChangedFileScanRequiresSpotifyFetch;
    private ScanScope? _activeScanScope;
    private long? _activeScanFolderId;
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

    private enum ScanScope
    {
        Full,
        Folder,
        ChangedFiles
    }

    private sealed record PendingScanRequest(
        bool RefreshImages,
        bool Reset,
        long? FolderId,
        bool SkipSpotifyFetch,
        bool CacheSpotifyImages)
    {
        public static PendingScanRequest Merge(PendingScanRequest existing, PendingScanRequest incoming)
        {
            return existing with
            {
                RefreshImages = existing.RefreshImages || incoming.RefreshImages,
                Reset = existing.Reset || incoming.Reset,
                SkipSpotifyFetch = existing.SkipSpotifyFetch && incoming.SkipSpotifyFetch,
                CacheSpotifyImages = existing.CacheSpotifyImages || incoming.CacheSpotifyImages
            };
        }
    }

    public sealed record ChangedFileIngestionSummary(
        int RequestedFileCount,
        int ExistingAudioFileCount,
        IReadOnlyList<string> IngestedFilePaths,
        IReadOnlyList<string> MissingFilePaths)
    {
        public bool IsComplete => MissingFilePaths.Count == 0;
    }

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

    public async Task WaitForCurrentScanAsync(CancellationToken cancellationToken)
    {
        Task? activeScanTask;
        lock (_scanLock)
        {
            activeScanTask = _activeScanCompletion?.Task;
        }

        if (activeScanTask == null)
        {
            return;
        }

        await activeScanTask.WaitAsync(cancellationToken);
    }

    public Task EnqueueAsync(
        bool refreshImages,
        bool reset,
        long? folderId,
        bool skipSpotifyFetch,
        bool cacheSpotifyImages)
    {
        return RunAsync(
            refreshImages,
            reset,
            folderId,
            skipSpotifyFetch,
            cacheSpotifyImages,
            CancellationToken.None);
    }

    public async Task RunChangedFoldersAsync(
        IEnumerable<long> folderIds,
        bool skipSpotifyFetch,
        CancellationToken cancellationToken)
    {
        var changedFolderIds = folderIds
            .Where(folderId => folderId > 0)
            .Distinct()
            .OrderBy(folderId => folderId)
            .ToList();
        if (changedFolderIds.Count == 0)
        {
            AddInfoLog("Post-download library scan skipped (no changed destination folders).");
            return;
        }

        foreach (var folderId in changedFolderIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunFolderScanAndWaitAsync(folderId, skipSpotifyFetch, cancellationToken);
        }
    }

    public async Task RunFolderScanAndWaitAsync(
        long folderId,
        bool skipSpotifyFetch,
        CancellationToken cancellationToken)
    {
        if (folderId <= 0)
        {
            return;
        }

        await RunAsync(
            refreshImages: false,
            reset: false,
            folderId: folderId,
            skipSpotifyFetch: skipSpotifyFetch,
            cacheSpotifyImages: false,
            cancellationToken: cancellationToken);
        await WaitForScheduledScansIdleAsync(cancellationToken);
    }

    public async Task<ChangedFileIngestionSummary> RunChangedFilesAndWaitForIngestionAsync(
        IReadOnlyDictionary<long, List<string>> changedFilesByFolder,
        bool skipSpotifyFetch,
        CancellationToken cancellationToken)
    {
        var pending = KnownLibraryFilePathSet.NormalizeByFolder(changedFilesByFolder);
        if (pending.Count == 0)
        {
            AddInfoLog("Targeted library ingestion verification skipped (no changed files).");
            return new ChangedFileIngestionSummary(0, 0, [], []);
        }

        await RunChangedFilesAsync(pending, skipSpotifyFetch, cancellationToken);
        return await VerifyChangedFilesIngestedAsync(pending, cancellationToken);
    }

    public async Task RunChangedFilesAsync(
        IReadOnlyDictionary<long, List<string>> changedFilesByFolder,
        bool skipSpotifyFetch,
        CancellationToken cancellationToken)
    {
        var pending = KnownLibraryFilePathSet.NormalizeByFolder(changedFilesByFolder);
        if (pending.Count == 0)
        {
            AddInfoLog("Targeted library scan skipped (no changed files).");
            return;
        }

        var fullScanAbsorption = TryAbsorbChangedFilesIntoFullScan(pending);
        if (fullScanAbsorption.Absorbed)
        {
            if (fullScanAbsorption.WaitTask is not null)
            {
                await fullScanAbsorption.WaitTask.WaitAsync(cancellationToken);
            }

            await WaitForScheduledScansIdleAsync(cancellationToken);
            return;
        }

        Dictionary<long, List<string>> folderAbsorbed;
        lock (_scanLock)
        {
            folderAbsorbed = pending
                .Where(pair => IsCoveredByActiveOrPendingFolderScan(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        if (folderAbsorbed.Count > 0)
        {
            foreach (var folderId in folderAbsorbed.Keys)
            {
                pending.Remove(folderId);
            }

            AddInfoLog($"Targeted library scan partly absorbed by folder scan ({folderAbsorbed.Sum(pair => pair.Value.Count)} file(s)).");
        }

        if (pending.Count == 0)
        {
            await WaitForScheduledScansIdleAsync(cancellationToken);
            return;
        }

        var drain = EnqueueChangedFileScanDrain(pending, skipSpotifyFetch);

        if (!drain.OwnsDrain)
        {
            AddInfoLog($"Targeted library scan merged into pending queue ({pending.Sum(pair => pair.Value.Count)} file(s)).");
            if (drain.WaitTask != null)
            {
                await drain.WaitTask.WaitAsync(cancellationToken);
            }
            return;
        }

        await DrainChangedFileScansAsync(cancellationToken);
    }

    private sealed record FullScanAbsorption(bool Absorbed, Task? WaitTask);

    private FullScanAbsorption TryAbsorbChangedFilesIntoFullScan(Dictionary<long, List<string>> pending)
    {
        lock (_scanLock)
        {
            if (_pendingFullScan is null)
            {
                return new FullScanAbsorption(false, null);
            }

            AddInfoLog($"Targeted library scan absorbed by full library scan ({pending.Sum(pair => pair.Value.Count)} file(s)).");
            return new FullScanAbsorption(true, _activeScanCompletion?.Task);
        }
    }

    private sealed record ChangedFileDrainState(bool OwnsDrain, Task? WaitTask);

    private ChangedFileDrainState EnqueueChangedFileScanDrain(Dictionary<long, List<string>> pending, bool skipSpotifyFetch)
    {
        lock (_changedFileScanLock)
        {
            AddPendingChangedFileScans(pending);
            _pendingChangedFileScanRequiresSpotifyFetch |= !skipSpotifyFetch;
            if (_changedFileScanDrainRunning)
            {
                return new ChangedFileDrainState(false, _changedFileScanDrainCompletion?.Task);
            }

            _changedFileScanDrainRunning = true;
            _changedFileScanDrainCompletion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return new ChangedFileDrainState(true, null);
        }
    }

    private void AddPendingChangedFileScans(Dictionary<long, List<string>> pending)
    {
        foreach (var (folderId, paths) in pending)
        {
            if (!_pendingChangedFileScans.TryGetValue(folderId, out var existing))
            {
                existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _pendingChangedFileScans[folderId] = existing;
            }

            foreach (var path in paths)
            {
                existing.Add(path);
            }
        }
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
            var request = new PendingScanRequest(refreshImages, reset, folderId, skipSpotifyFetch, cacheSpotifyImages);
            if (!TryStartScan(
                    folderId.HasValue ? ScanScope.Folder : ScanScope.Full,
                    folderId,
                    cancellationToken,
                    ref cts,
                    ref ownsActiveScan))
            {
                QueuePendingScan(request);
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
                await PersistScanInfoAsync(scanResult.ArtistCount, scanResult.AlbumCount, scanResult.TrackCount);
                await SyncRepositoryArtifactsAsync(
                    enabledFolders,
                    scanResult.ArtistGenres,
                    skipSpotifyFetch,
                    refreshImages,
                    cacheSpotifyImages,
                    activeCts.Token);
                ClearScanCheckpoint();
                AddInfoLog($"Library scan completed ({scanResult.ArtistCount} artists, {scanResult.AlbumCount} albums, {scanResult.TrackCount} tracks).");
                await PublishLibraryUpdatedAsync(scanResult, folderId, activeCts.Token);
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
                    _activeScanScope = null;
                    _activeScanFolderId = null;
                    _activeScanCompletion?.TrySetResult(null);
                    _activeScanCompletion = null;
                    _status = _status with { IsRunning = false, CurrentFile = null };
                }
            }

            if (ShouldDrainPendingAfterRun(ownsActiveScan, cts, cancellationToken))
            {
                await DrainPendingScheduledScansAsync(CancellationToken.None);
            }
        }
    }

    private async Task DrainChangedFileScansAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<object?>? completion = null;
        try
        {
            while (true)
            {
                Dictionary<long, List<string>> batch;
                bool skipSpotifyFetch;
                lock (_changedFileScanLock)
                {
                    if (_pendingChangedFileScans.Count == 0)
                    {
                        _changedFileScanDrainRunning = false;
                        _pendingChangedFileScanRequiresSpotifyFetch = false;
                        completion = _changedFileScanDrainCompletion;
                        _changedFileScanDrainCompletion = null;
                        completion?.TrySetResult(null);
                        return;
                    }

                    batch = _pendingChangedFileScans.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList());
                    _pendingChangedFileScans.Clear();
                    skipSpotifyFetch = !_pendingChangedFileScanRequiresSpotifyFetch;
                    _pendingChangedFileScanRequiresSpotifyFetch = false;
                }

                await WaitForCurrentScanAsync(cancellationToken);
                if (HasPendingFullScan())
                {
                    AddInfoLog($"Targeted library scan batch absorbed by pending full library scan ({batch.Sum(pair => pair.Value.Count)} file(s)).");
                    await WaitForScheduledScansIdleAsync(cancellationToken);
                    continue;
                }

                await RunChangedFilesBatchAsync(batch, skipSpotifyFetch, cancellationToken);
            }
        }
        catch (OperationCanceledException ex)
        {
            lock (_changedFileScanLock)
            {
                _changedFileScanDrainRunning = false;
                _pendingChangedFileScanRequiresSpotifyFetch = false;
                completion = _changedFileScanDrainCompletion;
                _changedFileScanDrainCompletion = null;
            }

            completion?.TrySetCanceled(ex.CancellationToken);
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            lock (_changedFileScanLock)
            {
                _changedFileScanDrainRunning = false;
                _pendingChangedFileScanRequiresSpotifyFetch = false;
                completion = _changedFileScanDrainCompletion;
                _changedFileScanDrainCompletion = null;
            }

            completion?.TrySetException(ex);
            throw;
        }
    }

    private async Task<ChangedFileIngestionSummary> VerifyChangedFilesIngestedAsync(
        IReadOnlyDictionary<long, List<string>> changedFilesByFolder,
        CancellationToken cancellationToken)
    {
        var requested = changedFilesByFolder
            .SelectMany(pair => pair.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existingAudioFiles = requested
            .Where(KnownLibraryFilePathSet.IsExistingAudioFile)
            .ToList();
        if (existingAudioFiles.Count == 0 || !_repository.IsConfigured)
        {
            return new ChangedFileIngestionSummary(requested.Count, existingAudioFiles.Count, [], []);
        }

        var ingested = await _repository.GetTrackIdsByFilePathsAsync(existingAudioFiles, cancellationToken);
        var comparison = KnownLibraryFilePathSet.CompareIngestedPaths(existingAudioFiles, ingested);
        var ingestedPaths = comparison.IngestedPaths;
        var missingPaths = comparison.MissingPaths;

        if (missingPaths.Count > 0)
        {
            AddWarnLog($"Targeted library ingestion incomplete ({missingPaths.Count}/{existingAudioFiles.Count} audio file(s) missing from DB).");
        }
        else
        {
            AddInfoLog($"Targeted library ingestion verified ({ingestedPaths.Count} audio file(s) present in DB).");
        }

        return new ChangedFileIngestionSummary(
            requested.Count,
            existingAudioFiles.Count,
            ingestedPaths,
            missingPaths);
    }

    private async Task RunChangedFilesBatchAsync(
        Dictionary<long, List<string>> changedFilesByFolder,
        bool skipSpotifyFetch,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? cts = null;
        var ownsActiveScan = false;
        try
        {
            if (!TryStartScan(ScanScope.ChangedFiles, folderId: null, cancellationToken, ref cts, ref ownsActiveScan))
            {
                RequeueChangedFiles(changedFilesByFolder, skipSpotifyFetch);
                return;
            }

            var activeCts = cts!;
            using (activeCts)
            {
                var enabledFolders = await LoadEnabledFoldersAsync(folderId: null, activeCts.Token);
                if (enabledFolders is null)
                {
                    return;
                }

                var foldersById = enabledFolders.ToDictionary(static folder => folder.Id);
                var aggregatedGenres = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var processedFolders = 0;
                var totalFiles = changedFilesByFolder.Sum(pair => pair.Value.Count);
                _status = _status with { TotalFiles = totalFiles };
                AddInfoLog($"Targeted library scan started ({totalFiles} changed file(s)).");

                foreach (var (folderId, filePaths) in changedFilesByFolder.OrderBy(pair => pair.Key))
                {
                    activeCts.Token.ThrowIfCancellationRequested();
                    if (!foldersById.TryGetValue(folderId, out var folder) || !folder.Enabled)
                    {
                        AddWarnLog($"Targeted library scan skipped unknown or disabled folder id={folderId}.");
                        continue;
                    }

                    var existingFiles = _repository.IsConfigured
                        ? await _repository.GetLocalScanFileStatesAsync(folder.Id, activeCts.Token)
                        : null;
                    var folderSnapshot = ScanChangedFileSnapshot(folder, filePaths, existingFiles, activeCts.Token);
                    MergeGenres(aggregatedGenres, folderSnapshot.ArtistGenres);

                    if (_repository.IsConfigured && folderSnapshot.Tracks.Count > 0)
                    {
                        await IngestSnapshotAsync(
                            [folder],
                            folderSnapshot,
                            reset: false,
                            logCompletion: true,
                            cancellationToken: activeCts.Token);
                    }

                    processedFolders++;
                    AddInfoLog($"Targeted folder indexed ({processedFolders}/{changedFilesByFolder.Count}): {folder.DisplayName}.");
                }

                var finalCounts = await ResolveFinalCountsAsync(activeCts.Token);
                _status = _status with
                {
                    ArtistsDetected = finalCounts.Artists,
                    AlbumsDetected = finalCounts.Albums,
                    TracksDetected = finalCounts.Tracks,
                    CurrentFile = null
                };

                var artistGenres = aggregatedGenres.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase);
                await StoreLocalGenresAsync(artistGenres, activeCts.Token);
                if (!skipSpotifyFetch)
                {
                    await EnqueueSpotifyArtistMetadataAsync(activeCts.Token);
                }

                await PersistScanInfoAsync(finalCounts.Artists, finalCounts.Albums, finalCounts.Tracks);
                AddInfoLog($"Targeted library scan completed ({finalCounts.Artists} artists, {finalCounts.Albums} albums, {finalCounts.Tracks} tracks).");
                await PublishLibraryUpdatedAsync(
                    new IncrementalScanResult(finalCounts.Artists, finalCounts.Albums, finalCounts.Tracks, artistGenres),
                    folderId: null,
                    activeCts.Token);
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Targeted library scan cancelled.");
            AddWarnLog("Targeted library scan cancelled.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Targeted library scan failed.");
            AddErrorLog($"Targeted library scan failed: {ex.Message}");
        }
        finally
        {
            lock (_scanLock)
            {
                if (ownsActiveScan && cts != null && ReferenceEquals(_activeScanCts, cts))
                {
                    _activeScanCts = null;
                    _activeScanScope = null;
                    _activeScanFolderId = null;
                    _activeScanCompletion?.TrySetResult(null);
                    _activeScanCompletion = null;
                    _status = _status with { IsRunning = false, CurrentFile = null };
                }
            }

            if (ShouldDrainPendingAfterRun(ownsActiveScan, cts, cancellationToken))
            {
                await DrainPendingScheduledScansAsync(CancellationToken.None);
            }
        }
    }

    private LibraryConfigStore.LocalLibrarySnapshot ScanChangedFileSnapshot(
        FolderDto folder,
        IReadOnlyCollection<string> filePaths,
        IReadOnlyDictionary<string, LocalScanFileState>? existingFiles,
        CancellationToken cancellationToken)
    {
        var progress = new Progress<LocalLibraryScanner.ScanProgress>(progressUpdate =>
        {
            var currentStatus = _status;
            _status = currentStatus with
            {
                ProcessedFiles = progressUpdate.ProcessedFiles,
                TotalFiles = Math.Max(currentStatus.TotalFiles, progressUpdate.TotalFiles),
                ErrorCount = progressUpdate.ErrorCount,
                CurrentFile = progressUpdate.CurrentFile,
                ArtistsDetected = Math.Max(currentStatus.ArtistsDetected, progressUpdate.ArtistsDetected),
                AlbumsDetected = Math.Max(currentStatus.AlbumsDetected, progressUpdate.AlbumsDetected),
                TracksDetected = Math.Max(currentStatus.TracksDetected, progressUpdate.TracksDetected)
            };
        });

        return _scanner.ScanFiles(folder, filePaths, progress, cancellationToken, existingFiles);
    }

    private void RequeueChangedFiles(IReadOnlyDictionary<long, List<string>> changedFilesByFolder, bool skipSpotifyFetch)
    {
        lock (_changedFileScanLock)
        {
            foreach (var (folderId, paths) in changedFilesByFolder)
            {
                if (!_pendingChangedFileScans.TryGetValue(folderId, out var existing))
                {
                    existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _pendingChangedFileScans[folderId] = existing;
                }

                foreach (var path in paths)
                {
                    existing.Add(path);
                }
            }

            _pendingChangedFileScanRequiresSpotifyFetch |= !skipSpotifyFetch;
        }
    }

    private async Task PublishLibraryUpdatedAsync(
        IncrementalScanResult scanResult,
        long? folderId,
        CancellationToken cancellationToken)
    {
        var syncService = _serviceProvider.GetService<CrossDeviceSyncService>();
        if (syncService is null)
        {
            return;
        }

        await syncService.PublishLibraryUpdatedAsync(
            scanResult.ArtistCount,
            scanResult.AlbumCount,
            scanResult.TrackCount,
            folderId,
            cancellationToken);

        TriggerWatchlistAfterLibraryUpdate();
    }

    private void TriggerWatchlistAfterLibraryUpdate()
    {
        var settingsService = _serviceProvider.GetService<DeezSpoTagSettingsService>();
        if (settingsService?.LoadSettings().WatchEnabled != true)
        {
            return;
        }

        var watchlist = _serviceProvider.GetService<WatchlistRunCoordinator>();
        if (watchlist is null)
        {
            return;
        }

        _ = TriggerWatchlistAfterLibraryUpdateAsync(watchlist);
    }

    private async Task TriggerWatchlistAfterLibraryUpdateAsync(WatchlistRunCoordinator watchlist)
    {
        try
        {
            await watchlist.TriggerRunOnceAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Watchlist trigger after library update failed.");
        }
    }

    private bool TryStartScan(
        ScanScope scope,
        long? folderId,
        CancellationToken cancellationToken,
        ref CancellationTokenSource? cts,
        ref bool ownsActiveScan)
    {
        lock (_scanLock)
        {
            if (_activeScanCts != null)
            {
                AddInfoLog("Library scan already running; new scan request will be coalesced.");
                return false;
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeScanCts = cts;
            _activeScanCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeScanScope = scope;
            _activeScanFolderId = folderId;
            if (scope == ScanScope.Full)
            {
                _pendingFolderScans.Clear();
                ClearPendingChangedFileScansLocked();
            }
            else if (scope == ScanScope.Folder && folderId.HasValue)
            {
                RemovePendingChangedFileScanLocked(folderId.Value);
            }
            ownsActiveScan = true;
            _status = new ScanStatus(true, DateTimeOffset.UtcNow, 0, 0, 0, null, 0, 0, 0);
            return true;
        }
    }

    private void QueuePendingScan(PendingScanRequest request)
    {
        lock (_scanLock)
        {
            if (!request.FolderId.HasValue)
            {
                if (_activeScanScope == ScanScope.Full
                    && !request.Reset
                    && !request.RefreshImages
                    && !request.CacheSpotifyImages)
                {
                    _pendingFolderScans.Clear();
                    ClearPendingChangedFileScansLocked();
                    AddInfoLog("Library full scan request absorbed by active full library scan.");
                    return;
                }

                _pendingFullScan = _pendingFullScan is null
                    ? request
                    : PendingScanRequest.Merge(_pendingFullScan, request);
                _pendingFolderScans.Clear();
                ClearPendingChangedFileScansLocked();
                AddInfoLog("Library full scan request coalesced; pending targeted scans were absorbed.");
                return;
            }

            if (_pendingFullScan is not null || _activeScanScope == ScanScope.Full)
            {
                AddInfoLog($"Library folder scan request for folder id={request.FolderId.Value} absorbed by full library scan.");
                return;
            }

            if (_activeScanScope == ScanScope.Folder && _activeScanFolderId == request.FolderId)
            {
                AddInfoLog($"Library folder scan request for folder id={request.FolderId.Value} absorbed by active folder scan.");
                return;
            }

            _pendingFolderScans[request.FolderId.Value] = _pendingFolderScans.TryGetValue(request.FolderId.Value, out var existing)
                ? PendingScanRequest.Merge(existing, request)
                : request;
            RemovePendingChangedFileScanLocked(request.FolderId.Value);
            AddInfoLog($"Library folder scan request coalesced for folder id={request.FolderId.Value}.");
        }
    }

    private async Task DrainPendingScheduledScansAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            PendingScanRequest? next;
            lock (_scanLock)
            {
                if (_activeScanCts != null)
                {
                    return;
                }

                if (_pendingFullScan is not null)
                {
                    next = _pendingFullScan;
                    _pendingFullScan = null;
                    _pendingFolderScans.Clear();
                    ClearPendingChangedFileScansLocked();
                }
                else if (_pendingFolderScans.Count > 0)
                {
                    var first = _pendingFolderScans
                        .OrderBy(pair => pair.Key)
                        .First();
                    _pendingFolderScans.Remove(first.Key);
                    RemovePendingChangedFileScanLocked(first.Key);
                    next = first.Value;
                }
                else
                {
                    return;
                }
            }

            await RunAsync(
                next.RefreshImages,
                next.Reset,
                next.FolderId,
                next.SkipSpotifyFetch,
                next.CacheSpotifyImages,
                cancellationToken);
        }
    }

    private async Task WaitForScheduledScansIdleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? activeScanTask;
            var shouldDrainPending = false;
            lock (_scanLock)
            {
                activeScanTask = _activeScanCompletion?.Task;
                if (activeScanTask is null
                    && (_pendingFullScan is not null || _pendingFolderScans.Count > 0))
                {
                    shouldDrainPending = true;
                }
                else if (activeScanTask is null)
                {
                    return;
                }
            }

            if (shouldDrainPending)
            {
                await DrainPendingScheduledScansAsync(CancellationToken.None);
                continue;
            }

            await activeScanTask!.WaitAsync(cancellationToken);
        }
    }

    private static bool ShouldDrainPendingAfterRun(
        bool ownsActiveScan,
        CancellationTokenSource? cts,
        CancellationToken callerCancellationToken)
    {
        return ownsActiveScan
            && cts?.IsCancellationRequested != true
            && !callerCancellationToken.IsCancellationRequested;
    }

    private bool IsCoveredByActiveOrPendingFolderScan(long folderId)
    {
        return _pendingFolderScans.ContainsKey(folderId);
    }

    private bool HasPendingFullScan()
    {
        lock (_scanLock)
        {
            return _pendingFullScan is not null;
        }
    }

    private void ClearPendingChangedFileScansLocked()
    {
        lock (_changedFileScanLock)
        {
            _pendingChangedFileScans.Clear();
            _pendingChangedFileScanRequiresSpotifyFetch = false;
        }
    }

    private void RemovePendingChangedFileScanLocked(long folderId)
    {
        lock (_changedFileScanLock)
        {
            _pendingChangedFileScans.Remove(folderId);
            if (_pendingChangedFileScans.Count == 0)
            {
                _pendingChangedFileScanRequiresSpotifyFetch = false;
            }
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
            : await _configStore.GetFoldersAsync();
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

            var existingFiles = _repository.IsConfigured
                ? await _repository.GetLocalScanFileStatesAsync(folder.Id, cancellationToken)
                : null;

            var folderSnapshotResult = ScanSingleFolderSnapshot(
                folder,
                progressOffset,
                livePreviewIngestEnabled,
                existingFiles,
                cancellationToken);
            var folderSnapshot = folderSnapshotResult.Snapshot;

            progressOffset = new ScanProgressOffset(
                progressOffset.ProcessedFiles + folderSnapshotResult.ProcessedFiles,
                progressOffset.TotalFiles + folderSnapshotResult.TotalFiles,
                progressOffset.ErrorCount + folderSnapshotResult.ErrorCount);

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

    private FolderScanSnapshotResult ScanSingleFolderSnapshot(
        FolderDto folder,
        ScanProgressOffset progressOffset,
        bool livePreviewIngestEnabled,
        IReadOnlyDictionary<string, LocalScanFileState>? existingFiles,
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
            cancellationToken,
            existingFiles);
        return new FolderScanSnapshotResult(snapshot, latestProcessed, latestTotal, latestErrors);
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

    private async Task PersistScanInfoAsync(int artistCount, int albumCount, int trackCount)
    {
        await _configStore.SaveLastScanInfoAsync(new LibraryConfigStore.LastScanInfo(
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
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
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
