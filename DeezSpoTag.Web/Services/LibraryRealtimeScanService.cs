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
                    string.Equals(existing.NormalizedRootPath, state.NormalizedRootPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                existing?.Dispose();
                _watchers[folderId] = CreateWatcher(state.Folder, state.NormalizedRootPath);
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

    private WatchedFolder CreateWatcher(FolderDto folder, string normalizedRootPath)
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
            watcher.Created += (_, args) => OnFileEvent(folderId, args.FullPath);
            watcher.Changed += (_, args) => OnFileEvent(folderId, args.FullPath);
            watcher.Renamed += (_, args) => OnFileEvent(folderId, args.FullPath);
            watcher.Error += (_, args) => OnWatcherError(folderId, args.GetException());
            watcher.EnableRaisingEvents = true;

            _logger.LogInformation("Watching library folder for realtime scans: {Path}", normalizedRootPath);
            return new WatchedFolder(normalizedRootPath, watcher);
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

        RequeueFolder(folderId, SettleDelay);
        if (_taggingJobQueue != null)
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

    private sealed record WatchedFolder(string NormalizedRootPath, FileSystemWatcher Watcher) : IDisposable
    {
        public void Dispose()
        {
            Watcher.EnableRaisingEvents = false;
            Watcher.Dispose();
        }
    }

    private sealed record FolderState(FolderDto Folder, string NormalizedRootPath);
}
