using System.Collections.Generic;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Tagging;
using Microsoft.Extensions.Hosting;

namespace DeezSpoTag.Web.Services;

public sealed class LibraryRealtimeScanService : BackgroundService
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".flac",
        ".m4a",
        ".m4b",
        ".wav",
        ".ogg",
        ".aiff",
        ".alac",
        ".aac"
    };

    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly LibraryScanRunner _scanRunner;
    private readonly ITaggingJobQueue? _taggingJobQueue;
    private readonly ILogger<LibraryRealtimeScanService> _logger;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _stateLock = new();
    private readonly Dictionary<long, WatchedFolder> _watchers = new();
    private readonly Dictionary<long, DateTimeOffset> _pendingScans = new();
    private readonly HashSet<long> _bootstrappingFolders = new();

    private DateTimeOffset _nextRefreshUtc = DateTimeOffset.MinValue;
    private bool _refreshRequested = true;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(2);

    public LibraryRealtimeScanService(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        LibraryScanRunner scanRunner,
        ILogger<LibraryRealtimeScanService> logger,
        ITaggingJobQueue? taggingJobQueue = null)
    {
        _repository = repository;
        _configStore = configStore;
        _scanRunner = scanRunner;
        _logger = logger;
        _taggingJobQueue = taggingJobQueue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Library realtime scan watcher started.");

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

        var folders = _repository.IsConfigured
            ? await _repository.GetFoldersAsync(cancellationToken)
            : _configStore.GetFolders();

        var enabled = folders
            .Where(folder => folder.Enabled)
            .Where(folder => !IsExcludedFromRealtimeLibrary(folder))
            .Select(folder => new
            {
                Folder = folder,
                NormalizedRoot = NormalizePath(folder.RootPath)
            })
            .Where(item => item.NormalizedRoot != null && Directory.Exists(item.NormalizedRoot))
            .ToDictionary(item => item.Folder.Id, item => new FolderState(item.Folder, item.NormalizedRoot!), comparer: EqualityComparer<long>.Default);

        lock (_stateLock)
        {
            var removedIds = _watchers.Keys.Where(id => !enabled.ContainsKey(id)).ToList();
            foreach (var folderId in removedIds)
            {
                _watchers[folderId].Dispose();
                _watchers.Remove(folderId);
                _pendingScans.Remove(folderId);
            }

            foreach (var entry in enabled)
            {
                var folderId = entry.Key;
                var state = entry.Value;
                if (_watchers.TryGetValue(folderId, out var existing) &&
                    string.Equals(existing.NormalizedRootPath, state.NormalizedRootPath, StringComparison.OrdinalIgnoreCase) &&
                    existing.AutoTagEnabled == state.Folder.AutoTagEnabled)
                {
                    continue;
                }

                existing?.Dispose();
                _watchers[folderId] = CreateWatcher(
                    state.Folder,
                    state.NormalizedRootPath,
                    _bootstrappingFolders.Contains(folderId));
            }
        }
    }

    private async Task ProcessDueScansAsync(CancellationToken cancellationToken)
    {
        List<long> dueFolderIds;
        var now = DateTimeOffset.UtcNow;

        lock (_stateLock)
        {
            dueFolderIds = _pendingScans
                .Where(item => item.Value <= now)
                .Select(item => item.Key)
                .ToList();

            foreach (var folderId in dueFolderIds)
            {
                _pendingScans.Remove(folderId);
            }
        }

        if (dueFolderIds.Count == 0)
        {
            return;
        }

        foreach (var folderId in dueFolderIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_scanRunner.GetStatus().IsRunning)
            {
                RequeueFolder(folderId, BusyRetryDelay);
                continue;
            }

            _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
                DateTimeOffset.UtcNow,
                "info",
                $"Realtime library scan triggered for folder id={folderId}."));

            await _scanRunner.RunAsync(
                refreshImages: false,
                reset: false,
                folderId: folderId,
                skipSpotifyFetch: false,
                cacheSpotifyImages: false,
                cancellationToken: cancellationToken);
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

    private WatchedFolder CreateWatcher(FolderDto folder, string normalizedRootPath, bool isBootstrapping)
    {
        var folderId = folder.Id;
        var baselineFiles = BuildBaselineAudioFiles(normalizedRootPath);
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
            watcher.Created += (_, args) => OnFileEvent(folderId, args.FullPath);
            watcher.Changed += (_, args) => OnFileEvent(folderId, args.FullPath);
            watcher.Renamed += (_, args) => OnFileEvent(folderId, args.FullPath);
            watcher.Error += (_, args) => OnWatcherError(folderId, args.GetException());

            _logger.LogInformation("Watching library folder for realtime scans: {Path}", normalizedRootPath);
            var watchedFolder = new WatchedFolder(
                normalizedRootPath,
                watcher,
                folder.AutoTagEnabled,
                baselineFiles,
                isBootstrapping);
            watcher.EnableRaisingEvents = true;
            return watchedFolder;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
    }

    private void OnFileEvent(long folderId, string fullPath)
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

        RequeueFolder(folderId, SettleDelay);
        if (_taggingJobQueue != null
            && watchedFolder is not null
            && watchedFolder.AutoTagEnabled
            && watchedFolder.ShouldQueueRetag(fullPath))
        {
            _ = QueueRetagAsync(fullPath);
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
            _logger.LogDebug(ex, "Failed to enqueue realtime retag job for {Path}", fullPath);
        }
    }

    private void OnWatcherError(long folderId, Exception? exception)
    {
        if (exception != null)
        {
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

    private void RequeueFolder(long folderId, TimeSpan delay)
    {
        lock (_stateLock)
        {
            _pendingScans[folderId] = DateTimeOffset.UtcNow.Add(delay);
        }
        _signal.Release();
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
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return null;
        }
    }

    private static bool IsExcludedFromRealtimeLibrary(FolderDto folder)
    {
        var desiredQuality = folder.DesiredQuality?.Trim();
        return string.Equals(desiredQuality, "video", StringComparison.OrdinalIgnoreCase)
            || string.Equals(desiredQuality, "podcast", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, FileBaselineState> BuildBaselineAudioFiles(string normalizedRootPath)
    {
        var baselineFiles = new Dictionary<string, FileBaselineState>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(normalizedRootPath, "*.*", SearchOption.AllDirectories))
            {
                if (!IsAudioFilePath(filePath))
                {
                    continue;
                }

                var normalizedPath = NormalizePath(filePath);
                if (normalizedPath is null)
                {
                    continue;
                }

                if (TryReadFileBaselineState(normalizedPath, out var baselineState))
                {
                    baselineFiles[normalizedPath] = baselineState;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A partial baseline is still enough to suppress most attach-time noise.
        }

        return baselineFiles;
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
        catch (Exception)
        {
            return false;
        }
    }

    private readonly record struct FileBaselineState(DateTime LastWriteUtc, long Length);

    private sealed class WatchedFolder : IDisposable
    {
        private readonly object _baselineLock = new();
        private Dictionary<string, FileBaselineState> _baselineFiles;
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
                _baselineFiles = BuildBaselineAudioFiles(NormalizedRootPath);
                _isBootstrapping = true;
            }
        }

        public void CompleteBootstrap()
        {
            lock (_baselineLock)
            {
                _baselineFiles = BuildBaselineAudioFiles(NormalizedRootPath);
                _isBootstrapping = false;
            }
        }

        public bool ShouldQueueRetag(string fullPath)
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

                _baselineFiles.Remove(normalizedPath);
                return true;
            }
        }

        public void Dispose()
        {
            Watcher.EnableRaisingEvents = false;
            Watcher.Dispose();
        }
    }

    private sealed record FolderState(FolderDto Folder, string NormalizedRootPath);
}
