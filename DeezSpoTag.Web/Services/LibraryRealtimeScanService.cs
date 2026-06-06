using System.Collections.Generic;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Runtime;
using DeezSpoTag.Services.Tagging;
using Microsoft.Extensions.Hosting;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryRealtimeScanService : BackgroundService
{
    private const string RealtimeWatchersEnabledEnv = "DEEZSPOTAG_LIBRARY_REALTIME_WATCHERS_ENABLED";
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".flac",
        ".m4a",
        ".m4b",
        ".wav",
        ".ogg",
        ".opus",
        ".aiff",
        ".alac",
        ".aac"
    };

    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly LibraryScanRunner _scanRunner;
    private readonly MediaServerLibraryRefreshService _mediaServerRefreshService;
    private readonly ITaggingJobQueue? _taggingJobQueue;
    private readonly BackgroundWorkCoordinator _workCoordinator;
    private readonly ILogger<LibraryRealtimeScanService> _logger;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _stateLock = new();
    private readonly Dictionary<long, WatchedFolder> _watchers = new();
    private readonly Dictionary<long, PendingFolderScan> _pendingScans = new();
    private readonly HashSet<long> _bootstrappingFolders = new();

    private DateTimeOffset _nextRefreshUtc = DateTimeOffset.MinValue;
    private bool _refreshRequested = true;
    private bool _watchersDisabledForProcess;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(5);

    public LibraryRealtimeScanService(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        LibraryScanRunner scanRunner,
        MediaServerLibraryRefreshService mediaServerRefreshService,
        ILogger<LibraryRealtimeScanService> logger,
        BackgroundWorkCoordinator workCoordinator,
        ITaggingJobQueue? taggingJobQueue = null)
    {
        _repository = repository;
        _configStore = configStore;
        _scanRunner = scanRunner;
        _mediaServerRefreshService = mediaServerRefreshService;
        _logger = logger;
        _workCoordinator = workCoordinator;
        _taggingJobQueue = taggingJobQueue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Library realtime scan watcher started.");
        if (!RealtimeWatchersEnabled())
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Library realtime scan watchers are disabled by {EnvironmentVariable}.",
                    RealtimeWatchersEnabledEnv);
            }
            return;
        }

        await _workCoordinator.WaitForStartupGraceAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await EnsureWatchersAsync(stoppingToken);
                await ProcessDueScansAsync(stoppingToken);
                await WaitForWorkAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
        finally
        {
            DisposeAllWatchers();
            _logger.LogInformation("Library realtime scan watcher stopped.");
        }
    }

    public void BeginFolderBootstrap(long folderId)
    {
        lock (_stateLock)
        {
            _bootstrappingFolders.Add(folderId);
            if (_watchers.TryGetValue(folderId, out var watchedFolder))
            {
                watchedFolder.BeginBootstrap();
            }
        }
    }

    public void CompleteFolderBootstrap(long folderId)
    {
        lock (_stateLock)
        {
            _bootstrappingFolders.Remove(folderId);
            if (_watchers.TryGetValue(folderId, out var watchedFolder))
            {
                watchedFolder.CompleteBootstrap();
            }
        }
    }

    private async Task EnsureWatchersAsync(CancellationToken cancellationToken)
    {
        if (_watchersDisabledForProcess || !_workCoordinator.CanRunLibraryWatchers())
        {
            DisposeAllWatchers();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        bool shouldRefresh;
        lock (_stateLock)
        {
            shouldRefresh = _refreshRequested || now >= _nextRefreshUtc;
            if (shouldRefresh)
            {
                _refreshRequested = false;
                _nextRefreshUtc = now.Add(RefreshInterval);
            }
        }

        if (!shouldRefresh)
        {
            return;
        }

        var enabled = await BuildEnabledFolderStatesAsync(cancellationToken);

        IOException? watcherResourceException;
        lock (_stateLock)
        {
            watcherResourceException = ApplyWatcherStates(enabled);
        }

        if (watcherResourceException != null)
        {
            EnterWatcherDegradedMode(watcherResourceException);
        }
    }

    private async Task<Dictionary<long, FolderState>> BuildEnabledFolderStatesAsync(CancellationToken cancellationToken)
    {
        var folders = _repository.IsConfigured
            ? await _repository.GetFoldersAsync(cancellationToken)
            : await _configStore.GetFoldersAsync();

        var enabledCandidates = folders
            .Where(folder => folder.Enabled)
            .Where(folder => !IsExcludedFromRealtimeLibrary(folder))
            .Select(folder => new
            {
                Folder = folder,
                NormalizedRoot = NormalizePath(folder.RootPath)
            })
            .Where(item => item.NormalizedRoot != null && Directory.Exists(item.NormalizedRoot))
            .ToList();

        var enabled = new Dictionary<long, FolderState>();
        foreach (var item in enabledCandidates)
        {
            var baselineFiles = ShouldRefreshWatcher(item.Folder.Id, item.NormalizedRoot!, item.Folder.AutoTagEnabled)
                ? await BuildBaselineAudioFilesAsync(item.Folder.Id, item.NormalizedRoot!, cancellationToken)
                : null;
            enabled[item.Folder.Id] = new FolderState(item.Folder, item.NormalizedRoot!, baselineFiles);
        }

        return enabled;
    }

    private IOException? ApplyWatcherStates(IReadOnlyDictionary<long, FolderState> enabled)
    {
        RemoveDisabledWatchers(enabled);

        foreach (var exception in enabled
            .Select(entry => RefreshWatcher(entry.Key, entry.Value))
            .Where(exception => exception != null))
        {
            return exception;
        }

        return null;
    }

    private void RemoveDisabledWatchers(IReadOnlyDictionary<long, FolderState> enabled)
    {
        var removedIds = _watchers.Keys.Where(id => !enabled.ContainsKey(id)).ToList();
        foreach (var folderId in removedIds)
        {
            _watchers[folderId].Dispose();
            _watchers.Remove(folderId);
            _pendingScans.Remove(folderId);
        }
    }

    private IOException? RefreshWatcher(long folderId, FolderState state)
    {
        if (HasReusableWatcher(folderId, state))
        {
            return null;
        }

        var existing = _watchers.GetValueOrDefault(folderId);
        try
        {
            existing?.Dispose();

            var replacement = CreateWatcher(
                state.Folder,
                state.NormalizedRootPath,
                state.BaselineFiles ?? new Dictionary<string, FileBaselineState>(StringComparer.OrdinalIgnoreCase),
                _bootstrappingFolders.Contains(folderId));
            try
            {
                _watchers[folderId] = replacement;
                return null;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                replacement.Dispose();
                throw;
            }
        }
        catch (IOException ex)
        {
            if (!IsWatcherResourceLimit(ex))
            {
                throw;
            }

            _watchers.Remove(folderId);
            return ex;
        }
    }

    private bool HasReusableWatcher(long folderId, FolderState state)
    {
        return _watchers.TryGetValue(folderId, out var existing)
            && string.Equals(existing.NormalizedRootPath, state.NormalizedRootPath, StringComparison.OrdinalIgnoreCase)
            && existing.AutoTagEnabled == state.Folder.AutoTagEnabled;
    }

    private async Task ProcessDueScansAsync(CancellationToken cancellationToken)
    {
        List<KeyValuePair<long, PendingFolderScan>> dueScans;
        var now = DateTimeOffset.UtcNow;

        lock (_stateLock)
        {
            dueScans = _pendingScans
                .Where(item => item.Value.DueUtc <= now)
                .ToList();

            foreach (var scan in dueScans)
            {
                _pendingScans.Remove(scan.Key);
            }
        }

        if (dueScans.Count == 0)
        {
            return;
        }

        foreach (var (folderId, pendingScan) in dueScans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existingPaths = new List<string>();
            var missingPaths = new HashSet<string>(pendingScan.DeletedFilePaths, StringComparer.OrdinalIgnoreCase);
            foreach (var path in pendingScan.ChangedFilePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (FileExists(path))
                {
                    existingPaths.Add(path);
                }
                else
                {
                    missingPaths.Add(path);
                }
            }

            if (existingPaths.Count == 0 && missingPaths.Count == 0)
            {
                continue;
            }

            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Realtime targeted library scan triggered for folder id={folderId} ({existingPaths.Count} changed file(s), {missingPaths.Count} removed file(s))."));

            if (_repository.IsConfigured && missingPaths.Count > 0)
            {
                await _repository.RemoveLocalAudioFilesByPathAsync(folderId, missingPaths.ToList(), cancellationToken);
            }

            LibraryScanRunner.ChangedFileIngestionSummary? ingestion = null;
            if (existingPaths.Count > 0)
            {
                ingestion = await _scanRunner.RunChangedFilesAndWaitForIngestionAsync(
                    new Dictionary<long, List<string>>
                    {
                        [folderId] = existingPaths
                    },
                    skipSpotifyFetch: false,
                    cancellationToken: cancellationToken);
            }

            await TriggerMediaServerRefreshAfterRealtimeChangeAsync(folderId, missingPaths.Count, ingestion, cancellationToken);
            RefreshWatcherBaseline(folderId, existingPaths.Concat(missingPaths));
        }
    }

    private async Task TriggerMediaServerRefreshAfterRealtimeChangeAsync(
        long folderId,
        int removedFileCount,
        LibraryScanRunner.ChangedFileIngestionSummary? ingestion,
        CancellationToken cancellationToken)
    {
        if (ingestion is { IsComplete: false })
        {
            var message = removedFileCount == 0
                ? $"Realtime media server scan skipped for folder id={folderId} because changed-file ingestion is incomplete ({ingestion.IngestedFilePaths.Count}/{ingestion.RequestedFileCount})."
                : $"Realtime changed-file ingestion is incomplete for folder id={folderId} ({ingestion.IngestedFilePaths.Count}/{ingestion.RequestedFileCount}); media server scan will still run for {removedFileCount} removed file(s).";
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warning",
                message));
            if (removedFileCount == 0)
            {
                return;
            }
        }

        var ingestedFileCount = ingestion?.IngestedFilePaths.Count ?? 0;
        if (ingestedFileCount == 0 && removedFileCount == 0)
        {
            return;
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Realtime media server scan triggered for folder id={folderId} after {ingestedFileCount} ingested file(s), {removedFileCount} removed file(s)."));
        try
        {
            await _mediaServerRefreshService.RefreshAsync(service: null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                   && DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            _logger.LogWarning(ex, "Realtime media server scan failed for folder id={FolderId}.", folderId);
            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "warning",
                $"Realtime media server scan failed for folder id={folderId}: {ex.Message}"));
        }
    }

    private async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _signal.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stop requested.
        }
    }

    private WatchedFolder CreateWatcher(
        FolderDto folder,
        string normalizedRootPath,
        Dictionary<string, FileBaselineState> baselineFiles,
        bool isBootstrapping)
    {
        var folderId = folder.Id;
        var watcher = new FileSystemWatcher(normalizedRootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.CreationTime |
                           NotifyFilters.Size,
            Filter = "*.*"
        };

        try
        {
            watcher.Created += (_, args) => OnFileChanged(folderId, args.FullPath);
            watcher.Changed += (_, args) => OnFileChanged(folderId, args.FullPath);
            watcher.Deleted += (_, args) => OnFileDeleted(folderId, args.FullPath);
            watcher.Renamed += (_, args) => OnFileRenamed(folderId, args.OldFullPath, args.FullPath);
            watcher.Error += (_, args) => OnWatcherError(folderId, args.GetException());

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Watching library folder for realtime scans: {Path}", normalizedRootPath);
            }
            var watchedFolder = new WatchedFolder(
                normalizedRootPath,
                watcher,
                folder.AutoTagEnabled,
                baselineFiles,
                isBootstrapping);
            watcher.EnableRaisingEvents = true;
            return watchedFolder;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            watcher.Dispose();
            throw;
        }
    }

    private void OnFileChanged(long folderId, string fullPath)
    {
        if (!IsAudioFilePath(fullPath))
        {
            return;
        }

        WatchedFolder? watchedFolder;
        lock (_stateLock)
        {
            _watchers.TryGetValue(folderId, out watchedFolder);
        }

        var shouldQueueScan = watchedFolder?.ShouldQueueScan(fullPath) ?? true;
        if (!shouldQueueScan)
        {
            return;
        }

        RequeueFolder(folderId, SettleDelay, changedFilePath: fullPath);
        if (_taggingJobQueue != null
            && watchedFolder is not null
            && watchedFolder.AutoTagEnabled)
        {
            _ = QueueRetagAsync(fullPath);
        }
    }

    private void OnFileDeleted(long folderId, string fullPath)
    {
        if (!IsAudioFilePath(fullPath))
        {
            return;
        }

        RequeueFolder(folderId, SettleDelay, changedFilePaths: [], deletedFilePaths: [fullPath]);
    }

    private void OnFileRenamed(long folderId, string oldFullPath, string fullPath)
    {
        if (IsAudioFilePath(oldFullPath))
        {
            RequeueFolder(folderId, SettleDelay, changedFilePaths: [], deletedFilePaths: [oldFullPath]);
        }

        if (IsAudioFilePath(fullPath))
        {
            OnFileChanged(folderId, fullPath);
        }
    }

    private async Task QueueRetagAsync(string fullPath)
    {
        try
        {
            await _taggingJobQueue!.EnqueueAsync(new TaggingJobEnqueueRequest(
                FilePath: fullPath,
                TrackId: null,
                Operation: "retag"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to enqueue realtime retag job for {Path}", fullPath);
            }
        }
    }

    private void OnWatcherError(long folderId, Exception? exception)
    {
        if (exception != null)
        {
            if (IsWatcherResourceLimit(exception))
            {
                EnterWatcherDegradedMode(exception);
                return;
            }

            _logger.LogWarning(exception, "Library file watcher error for folder id={FolderId}. Watchers will refresh.", folderId);
        }
        else
        {
            _logger.LogWarning("Library file watcher error for folder id={FolderId}. Watchers will refresh.", folderId);
        }

        lock (_stateLock)
        {
            _refreshRequested = true;
        }
        _signal.Release();
    }

    private void EnterWatcherDegradedMode(Exception exception)
    {
        const string message = "Library realtime watchers are disabled for this process because the host inotify watch limit was reached.";
        _logger.LogWarning(exception, "{Message}", message);
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "warning",
            $"{message} Incremental library scans can still run from explicit scan requests."));
        _watchersDisabledForProcess = true;
        _workCoordinator.MarkLibraryWatchersDegraded(message);
        DisposeAllWatchers();
    }

    private void RequeueFolder(long folderId, TimeSpan delay, string? changedFilePath)
        => RequeueFolder(
            folderId,
            delay,
            NormalizePath(changedFilePath) is { } path ? [path] : [],
            deletedFilePaths: []);

    private void RequeueFolder(
        long folderId,
        TimeSpan delay,
        IEnumerable<string> changedFilePaths,
        IEnumerable<string> deletedFilePaths)
    {
        lock (_stateLock)
        {
            var dueUtc = DateTimeOffset.UtcNow.Add(delay);
            if (!_pendingScans.TryGetValue(folderId, out var pending))
            {
                pending = new PendingFolderScan(
                    dueUtc,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                _pendingScans[folderId] = pending;
            }

            pending.DueUtc = dueUtc;
            foreach (var normalizedPath in changedFilePaths
                .Select(NormalizePath)
                .Where(normalizedPath => normalizedPath != null))
            {
                pending.ChangedFilePaths.Add(normalizedPath!);
            }

            foreach (var normalizedPath in deletedFilePaths
                .Select(NormalizePath)
                .Where(normalizedPath => normalizedPath != null))
            {
                pending.DeletedFilePaths.Add(normalizedPath!);
            }
        }
        _signal.Release();
    }

    private void RefreshWatcherBaseline(long folderId, IEnumerable<string> filePaths)
    {
        WatchedFolder? watchedFolder;
        lock (_stateLock)
        {
            _watchers.TryGetValue(folderId, out watchedFolder);
        }

        watchedFolder?.RefreshBaseline(filePaths);
    }

    private void DisposeAllWatchers()
    {
        lock (_stateLock)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }
            _watchers.Clear();
            _pendingScans.Clear();
        }
    }

    private static bool IsAudioFilePath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        var extension = Path.GetExtension(fullPath);
        return !string.IsNullOrWhiteSpace(extension) && AudioExtensions.Contains(extension);
    }

    private static bool RealtimeWatchersEnabled()
    {
        var configured = Environment.GetEnvironmentVariable(RealtimeWatchersEnabledEnv);
        return string.IsNullOrWhiteSpace(configured)
            || configured.Equals("1", StringComparison.OrdinalIgnoreCase)
            || configured.Equals("true", StringComparison.OrdinalIgnoreCase)
            || configured.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool IsWatcherResourceLimit(Exception exception)
    {
        var message = exception.Message;
        return message.Contains("inotify", StringComparison.OrdinalIgnoreCase)
            || message.Contains("configured user limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("failed to allocate a required resource", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsExcludedFromRealtimeLibrary(FolderDto folder)
    {
        var desiredQuality = folder.DesiredQuality?.Trim();
        return string.Equals(desiredQuality, "video", StringComparison.OrdinalIgnoreCase)
            || string.Equals(desiredQuality, "podcast", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, FileBaselineState>> BuildBaselineAudioFilesAsync(
        long folderId,
        string normalizedRootPath,
        CancellationToken cancellationToken)
    {
        var baselineFiles = new Dictionary<string, FileBaselineState>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (_repository.IsConfigured)
            {
                var existingFiles = await _repository.GetLocalScanFileStatesAsync(folderId, cancellationToken);
                foreach (var (path, state) in existingFiles)
                {
                    baselineFiles[path] = new FileBaselineState(state.LastWriteUtc, state.Size);
                }

                return baselineFiles;
            }

            foreach (var filePath in Directory.EnumerateFiles(normalizedRootPath, "*.*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsAudioFilePath(filePath))
                {
                    continue;
                }

                AddFileBaselineIfReadable(filePath, baselineFiles);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            // A partial baseline is still enough to suppress most attach-time noise.
            return baselineFiles;
        }

        return baselineFiles;
    }

    private bool ShouldRefreshWatcher(long folderId, string normalizedRootPath, bool autoTagEnabled)
    {
        lock (_stateLock)
        {
            return !_watchers.TryGetValue(folderId, out var existing)
                || !string.Equals(existing.NormalizedRootPath, normalizedRootPath, StringComparison.OrdinalIgnoreCase)
                || existing.AutoTagEnabled != autoTagEnabled;
        }
    }

    private static void AddFileBaselineIfReadable(
        string filePath,
        Dictionary<string, FileBaselineState> baselineFiles)
    {
        var normalizedPath = NormalizePath(filePath);
        if (normalizedPath is null)
        {
            return;
        }

        if (TryReadFileBaselineState(normalizedPath, out var baselineState))
        {
            baselineFiles[normalizedPath] = baselineState;
        }
    }

    private static bool TryReadFileBaselineState(string normalizedPath, out FileBaselineState baselineState)
    {
        baselineState = default;

        try
        {
            var fileInfo = new FileInfo(normalizedPath);
            if (!fileInfo.Exists)
            {
                return false;
            }

            baselineState = new FileBaselineState(fileInfo.LastWriteTimeUtc, fileInfo.Length);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return false;
        }
    }

    private readonly record struct FileBaselineState(DateTime LastWriteUtc, long Length);

    private sealed class WatchedFolder : IDisposable
    {
        private readonly object _baselineLock = new();
        private readonly Dictionary<string, FileBaselineState> _baselineFiles;
        private bool _isBootstrapping;

        public WatchedFolder(
            string normalizedRootPath,
            FileSystemWatcher watcher,
            bool autoTagEnabled,
            Dictionary<string, FileBaselineState> baselineFiles,
            bool isBootstrapping)
        {
            NormalizedRootPath = normalizedRootPath;
            Watcher = watcher;
            AutoTagEnabled = autoTagEnabled;
            _baselineFiles = baselineFiles;
            _isBootstrapping = isBootstrapping;
        }

        public string NormalizedRootPath { get; }
        public FileSystemWatcher Watcher { get; }
        public bool AutoTagEnabled { get; }

        public void BeginBootstrap()
        {
            lock (_baselineLock)
            {
                _isBootstrapping = true;
            }
        }

        public void CompleteBootstrap()
        {
            lock (_baselineLock)
            {
                _isBootstrapping = false;
            }
        }

        public bool ShouldQueueScan(string fullPath)
        {
            var normalizedPath = NormalizePath(fullPath);
            if (normalizedPath is null)
            {
                return false;
            }

            lock (_baselineLock)
            {
                if (_isBootstrapping)
                {
                    if (TryReadFileBaselineState(normalizedPath, out var bootstrapState))
                    {
                        _baselineFiles[normalizedPath] = bootstrapState;
                    }
                    else
                    {
                        _baselineFiles.Remove(normalizedPath);
                    }

                    return false;
                }

                if (!_baselineFiles.TryGetValue(normalizedPath, out var baselineState))
                {
                    return true;
                }

                if (!TryReadFileBaselineState(normalizedPath, out var currentState))
                {
                    _baselineFiles.Remove(normalizedPath);
                    return false;
                }

                if (currentState == baselineState)
                {
                    return false;
                }

                return true;
            }
        }

        public void RefreshBaseline(IEnumerable<string> filePaths)
        {
            lock (_baselineLock)
            {
                foreach (var normalizedPath in filePaths
                    .Select(NormalizePath)
                    .Where(normalizedPath => normalizedPath is not null))
                {
                    if (TryReadFileBaselineState(normalizedPath!, out var currentState))
                    {
                        _baselineFiles[normalizedPath!] = currentState;
                    }
                    else
                    {
                        _baselineFiles.Remove(normalizedPath!);
                    }
                }
            }
        }

        public void Dispose()
        {
            Watcher.EnableRaisingEvents = false;
            Watcher.Dispose();
        }
    }

    private sealed record FolderState(
        FolderDto Folder,
        string NormalizedRootPath,
        Dictionary<string, FileBaselineState>? BaselineFiles);

    private sealed class PendingFolderScan
    {
        public PendingFolderScan(
            DateTimeOffset dueUtc,
            HashSet<string> changedFilePaths,
            HashSet<string> deletedFilePaths)
        {
            DueUtc = dueUtc;
            ChangedFilePaths = changedFilePaths;
            DeletedFilePaths = deletedFilePaths;
        }

        public DateTimeOffset DueUtc { get; set; }
        public HashSet<string> ChangedFilePaths { get; }
        public HashSet<string> DeletedFilePaths { get; }
    }
}
