using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;

namespace DeezSpoTag.Web.Services;

public sealed class KnownLibraryFileIngestionService
{
    private readonly LibraryRepository _repository;
    private readonly LibraryConfigStore _configStore;
    private readonly LocalLibraryScanner _scanner;
    private readonly IServiceProvider _serviceProvider;
    private readonly DeezSpoTagSettingsService _settingsService;
    private readonly ILogger<KnownLibraryFileIngestionService> _logger;

    public KnownLibraryFileIngestionService(
        LibraryRepository repository,
        LibraryConfigStore configStore,
        LocalLibraryScanner scanner,
        IServiceProvider serviceProvider,
        DeezSpoTagSettingsService settingsService,
        ILogger<KnownLibraryFileIngestionService> logger)
    {
        _repository = repository;
        _configStore = configStore;
        _scanner = scanner;
        _serviceProvider = serviceProvider;
        _settingsService = settingsService;
        _logger = logger;
    }

    public sealed record KnownFileIngestionSummary(
        int RequestedFileCount,
        int ExistingAudioFileCount,
        IReadOnlyList<string> IngestedFilePaths,
        IReadOnlyList<string> MissingFilePaths)
    {
        public bool IsComplete => MissingFilePaths.Count == 0;
    }

    public async Task<KnownFileIngestionSummary> IngestAndVerifyAsync(
        IReadOnlyDictionary<long, List<string>> filesByFolder,
        CancellationToken cancellationToken)
    {
        var pending = NormalizeFilesByFolder(filesByFolder);
        if (pending.Count == 0)
        {
            AddInfoLog("Known-file library ingestion skipped (no changed files).");
            return new KnownFileIngestionSummary(0, 0, [], []);
        }

        if (!_repository.IsConfigured)
        {
            AddInfoLog("Known-file library ingestion skipped (library repository is not configured).");
            return new KnownFileIngestionSummary(
                pending.Sum(pair => pair.Value.Count),
                pending.SelectMany(pair => pair.Value).Count(IsExistingAudioFile),
                [],
                []);
        }

        var folders = await _repository.GetFoldersAsync(cancellationToken);
        var foldersById = folders
            .Where(folder => folder.Enabled)
            .ToDictionary(static folder => folder.Id);

        var requestedFiles = pending
            .SelectMany(pair => pair.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existingAudioFiles = requestedFiles
            .Where(IsExistingAudioFile)
            .ToList();

        var ingestedFolderIds = new HashSet<long>();
        foreach (var (folderId, filePaths) in pending.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!foldersById.TryGetValue(folderId, out var folder))
            {
                AddWarnLog($"Known-file library ingestion skipped unknown or disabled folder id={folderId}.");
                continue;
            }

            var folderAudioFiles = filePaths
                .Where(IsExistingAudioFile)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (folderAudioFiles.Count == 0)
            {
                continue;
            }

            var existingFiles = await _repository.GetLocalScanFileStatesAsync(folder.Id, cancellationToken);
            var snapshot = _scanner.ScanFiles(
                folder,
                folderAudioFiles,
                progress: null,
                cancellationToken,
                existingFiles);
            var payload = LocalLibrarySnapshotMapper.BuildIngestPayload(snapshot);
            if (payload.Tracks.Count == 0)
            {
                AddWarnLog($"Known-file library ingestion produced no tracks for folder id={folderId}.");
                continue;
            }

            await _repository.IngestLocalScanAsync(
                [folder],
                payload.Artists,
                payload.Albums,
                payload.Tracks,
                pruneMissingArtists: false,
                cancellationToken);
            ingestedFolderIds.Add(folder.Id);
            AddInfoLog($"Known-file library ingestion completed for folder {folder.DisplayName} ({payload.Tracks.Count} track(s)).");
        }

        var verified = await VerifyIngestedAsync(requestedFiles, existingAudioFiles, cancellationToken);
        if (ingestedFolderIds.Count > 0)
        {
            await PublishLibraryUpdatedAsync(
                ingestedFolderIds.Count == 1 ? ingestedFolderIds.Single() : null,
                cancellationToken);
        }

        return verified;
    }

    public async Task<KnownFileIngestionSummary> VerifyAsync(
        IReadOnlyDictionary<long, List<string>> filesByFolder,
        CancellationToken cancellationToken)
    {
        var pending = NormalizeFilesByFolder(filesByFolder);
        var requestedFiles = pending
            .SelectMany(pair => pair.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existingAudioFiles = requestedFiles
            .Where(IsExistingAudioFile)
            .ToList();
        return await VerifyIngestedAsync(requestedFiles, existingAudioFiles, cancellationToken);
    }

    private async Task<KnownFileIngestionSummary> VerifyIngestedAsync(
        IReadOnlyList<string> requestedFiles,
        IReadOnlyList<string> existingAudioFiles,
        CancellationToken cancellationToken)
    {
        if (existingAudioFiles.Count == 0)
        {
            return new KnownFileIngestionSummary(requestedFiles.Count, 0, [], []);
        }

        var ingested = await _repository.GetTrackIdsByFilePathsAsync(existingAudioFiles, cancellationToken);
        var ingestedPaths = existingAudioFiles
            .Where(path => ingested.ContainsKey(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missingPaths = existingAudioFiles
            .Where(path => !ingested.ContainsKey(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missingPaths.Count > 0)
        {
            AddWarnLog($"Known-file library ingestion incomplete ({missingPaths.Count}/{existingAudioFiles.Count} audio file(s) missing from DB).");
        }
        else
        {
            AddInfoLog($"Known-file library ingestion verified ({ingestedPaths.Count} audio file(s) present in DB).");
        }

        return new KnownFileIngestionSummary(
            requestedFiles.Count,
            existingAudioFiles.Count,
            ingestedPaths,
            missingPaths);
    }

    private async Task PublishLibraryUpdatedAsync(long? folderId, CancellationToken cancellationToken)
    {
        var stats = await _repository.GetLibraryStatsAsync(cancellationToken);
        await _configStore.SaveLastScanInfoAsync(new LibraryConfigStore.LastScanInfo(
            DateTimeOffset.UtcNow,
            stats.TotalArtists,
            stats.TotalAlbums,
            stats.TotalTracks));

        var syncService = _serviceProvider.GetService<CrossDeviceSyncService>();
        if (syncService is not null)
        {
            await syncService.PublishLibraryUpdatedAsync(
                stats.TotalArtists,
                stats.TotalAlbums,
                stats.TotalTracks,
                folderId,
                cancellationToken);
        }

        TriggerWatchlistAfterLibraryUpdate();
    }

    private void TriggerWatchlistAfterLibraryUpdate()
    {
        if (_settingsService.LoadSettings().WatchEnabled != true)
        {
            return;
        }

        var watchlist = _serviceProvider.GetService<PlaylistWatchHostedService>();
        if (watchlist is null)
        {
            return;
        }

        _ = TriggerWatchlistAfterLibraryUpdateAsync(watchlist);
    }

    private async Task TriggerWatchlistAfterLibraryUpdateAsync(PlaylistWatchHostedService watchlist)
    {
        try
        {
            await watchlist.TriggerRunOnceAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Watchlist trigger after known-file library ingestion failed.");
        }
    }

    private void AddInfoLog(string message)
    {
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, "info", message));
    }

    private void AddWarnLog(string message)
    {
        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(DateTimeOffset.UtcNow, "warn", message));
    }

    private static Dictionary<long, List<string>> NormalizeFilesByFolder(
        IReadOnlyDictionary<long, List<string>> filesByFolder)
    {
        var normalized = new Dictionary<long, List<string>>();
        foreach (var (folderId, paths) in filesByFolder)
        {
            if (folderId <= 0 || paths.Count == 0)
            {
                continue;
            }

            var normalizedPaths = paths
                .Select(NormalizeFilePath)
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalizedPaths.Count > 0)
            {
                normalized[folderId] = normalizedPaths!;
            }
        }

        return normalized;
    }

    private static string? NormalizeFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var ioPath = DownloadPathResolver.ResolveIoPath(path);
        if (string.IsNullOrWhiteSpace(ioPath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(ioPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DownloadPathResolver.NormalizeDisplayPath(ioPath);
        }
    }

    private static bool IsExistingAudioFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension is ".mp3" or ".flac" or ".m4a" or ".m4b" or ".wav" or ".ogg" or ".opus" or ".aiff" or ".aif" or ".alac" or ".aac";
    }
}
