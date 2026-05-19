using System.Text.Json;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Queue;

public sealed class DownloadStagingCleanupService
{
    public const string CompletedStatus = "completed";
    public const string FailedStatus = "failed";
    public const string SkippedStatus = "skipped";
    private static readonly string[] RootPathProperties =
    [
        "filePath"
    ];
    private static readonly string[] FileArrayProperties =
    [
        "files"
    ];
    private static readonly string[] FileObjectPathProperties =
    [
        "path",
        "filePath",
        "outputPath"
    ];
    private static readonly string[] DirectoryProperties =
    [
        "extrasPath"
    ];
    private static readonly string[] RelatedFileExtensions =
    [
        ".lrc",
        ".ttml",
        ".txt"
    ];
    private static readonly string[] RelatedPathSuffixes =
    [
        ".tmp",
        ".part",
        ".download"
    ];

    private readonly DeezSpoTagSettingsService? _settingsService;
    private readonly ILogger<DownloadStagingCleanupService> _logger;
    private readonly string? _downloadRootOverride;

    public DownloadStagingCleanupService(
        ILogger<DownloadStagingCleanupService> logger,
        DeezSpoTagSettingsService? settingsService = null,
        string? downloadRootOverride = null)
    {
        _logger = logger;
        _settingsService = settingsService;
        _downloadRootOverride = downloadRootOverride;
    }

    public Task<DownloadStagingCleanupResult> CleanupAsync(
        string queueUuid,
        string? payloadJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueUuid))
        {
            return Task.FromResult(DownloadStagingCleanupResult.Skipped("queue uuid is empty"));
        }

        var downloadRoot = ResolveDownloadRoot();
        if (string.IsNullOrWhiteSpace(downloadRoot))
        {
            return Task.FromResult(DownloadStagingCleanupResult.Skipped("download root is not configured"));
        }

        if (!TryResolveFullPath(downloadRoot, out var rootPath))
        {
            return Task.FromResult(DownloadStagingCleanupResult.Skipped("download root is not a local path"));
        }

        var candidates = ExtractCandidates(payloadJson);
        if (candidates.FilePaths.Count == 0 && candidates.DirectoryPaths.Count == 0)
        {
            return Task.FromResult(DownloadStagingCleanupResult.Skipped("no staging paths were recorded"));
        }

        return Task.FromResult(CleanupCore(queueUuid, rootPath, candidates, cancellationToken));
    }

    private string? ResolveDownloadRoot()
    {
        if (!string.IsNullOrWhiteSpace(_downloadRootOverride))
        {
            return _downloadRootOverride;
        }

        return _settingsService?.LoadSettings().DownloadLocation;
    }

    private DownloadStagingCleanupResult CleanupCore(
        string queueUuid,
        string rootPath,
        StagingCleanupCandidates candidates,
        CancellationToken cancellationToken)
    {
        var deletedFiles = 0;
        var deletedDirectories = 0;
        var skippedPaths = 0;
        var errors = new List<string>();
        var parentDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawFilePath in candidates.FilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expandedPaths = ExpandRelatedFileCandidates(rawFilePath);
            var resolvedAny = false;
            foreach (var expandedPath in expandedPaths)
            {
                if (!TryResolveOwnedChildPath(expandedPath, rootPath, out var fullPath))
                {
                    continue;
                }

                resolvedAny = true;
                if (File.Exists(fullPath))
                {
                    try
                    {
                        File.Delete(fullPath);
                        deletedFiles++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        errors.Add($"{fullPath}: {ex.Message}");
                        continue;
                    }
                }

                AddParentDirectory(fullPath, rootPath, parentDirectories);
            }

            if (!resolvedAny)
            {
                skippedPaths++;
            }
        }

        foreach (var rawDirectoryPath in candidates.DirectoryPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveOwnedChildPath(rawDirectoryPath, rootPath, out var fullPath))
            {
                skippedPaths++;
                continue;
            }

            if (Directory.Exists(fullPath))
            {
                parentDirectories.Add(fullPath);
            }
        }

        foreach (var directory in parentDirectories.OrderByDescending(static path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            deletedDirectories += DeleteEmptyParents(directory, rootPath, errors);
        }

        if (errors.Count > 0)
        {
            var message = string.Join("; ", errors.Distinct(StringComparer.OrdinalIgnoreCase));
            _logger.LogWarning(
                "Staging cleanup failed for {QueueUuid}: {Message}",
                LogSanitizer.OneLine(queueUuid),
                LogSanitizer.OneLine(message));
            return DownloadStagingCleanupResult.Failed(message, deletedFiles, deletedDirectories, skippedPaths);
        }

        if (deletedFiles == 0 && deletedDirectories == 0)
        {
            if (skippedPaths > 0)
            {
                return DownloadStagingCleanupResult.Failed("recorded staging paths were outside the download folder", 0, 0, skippedPaths);
            }

            return DownloadStagingCleanupResult.Skipped("no existing staging files or empty folders were found", 0);
        }

        _logger.LogInformation(
            "Cleaned staging files for {QueueUuid}: files={DeletedFiles}, folders={DeletedDirectories}, skipped={SkippedPaths}",
            LogSanitizer.OneLine(queueUuid),
            deletedFiles,
            deletedDirectories,
            skippedPaths);
        return DownloadStagingCleanupResult.Completed(deletedFiles, deletedDirectories, skippedPaths);
    }

    private static IEnumerable<string> ExpandRelatedFileCandidates(string rawFilePath)
    {
        yield return rawFilePath;

        var extension = Path.GetExtension(rawFilePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            yield break;
        }

        foreach (var relatedExtension in RelatedFileExtensions)
        {
            yield return Path.ChangeExtension(rawFilePath, relatedExtension);
        }

        foreach (var suffix in RelatedPathSuffixes)
        {
            yield return rawFilePath + suffix;
        }
    }

    private static int DeleteEmptyParents(string startDirectory, string rootPath, ICollection<string> errors)
    {
        var deleted = 0;
        var current = startDirectory;
        while (!string.IsNullOrWhiteSpace(current)
               && IsStrictChildOfRoot(current, rootPath)
               && Directory.Exists(current))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(current).Any())
                {
                    break;
                }

                Directory.Delete(current);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{current}: {ex.Message}");
                break;
            }

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        return deleted;
    }

    private static void AddParentDirectory(string fullPath, string rootPath, ISet<string> parentDirectories)
    {
        var parent = Directory.GetParent(fullPath)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent) && IsStrictChildOfRoot(parent, rootPath))
        {
            parentDirectories.Add(parent);
        }
    }

    private static bool TryResolveOwnedChildPath(string rawPath, string rootPath, out string fullPath)
    {
        fullPath = string.Empty;
        if (!TryResolveFullPath(rawPath, out var resolvedPath)
            || !IsStrictChildOfRoot(resolvedPath, rootPath)
            || TraversesReparsePoint(resolvedPath, rootPath))
        {
            return false;
        }

        fullPath = resolvedPath;
        return true;
    }

    private static bool TraversesReparsePoint(string path, string rootPath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(rootPath);
        var current = Directory.GetParent(path)?.FullName;
        while (!string.IsNullOrWhiteSpace(current) && IsStrictChildOfRoot(current, normalizedRoot))
        {
            if (Directory.Exists(current) && HasReparsePoint(current))
            {
                return true;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        return File.Exists(path) && HasReparsePoint(path);
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsStrictChildOfRoot(string path, string rootPath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(rootPath);
        var normalizedPath = Path.TrimEndingDirectorySeparator(path);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveFullPath(string path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ioPath = DownloadPathResolver.ResolveIoPath(path);
        if (DownloadPathResolver.IsSmbPath(ioPath))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(ioPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static StagingCleanupCandidates ExtractCandidates(string? payloadJson)
    {
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new StagingCleanupCandidates(filePaths, directoryPaths);
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new StagingCleanupCandidates(filePaths, directoryPaths);
            }

            AddRootPathCandidates(document.RootElement, filePaths);
            AddFileArrayCandidates(document.RootElement, filePaths);
            AddDirectoryCandidates(document.RootElement, directoryPaths);
        }
        catch (JsonException)
        {
            return new StagingCleanupCandidates(filePaths, directoryPaths);
        }

        return new StagingCleanupCandidates(filePaths, directoryPaths);
    }

    private static void AddRootPathCandidates(JsonElement root, ISet<string> filePaths)
    {
        foreach (var propertyName in RootPathProperties)
        {
            AddStringProperty(root, propertyName, filePaths);
        }
    }

    private static void AddFileArrayCandidates(JsonElement root, ISet<string> filePaths)
    {
        foreach (var propertyName in FileArrayProperties)
        {
            if (!TryGetPropertyIgnoreCase(root, propertyName, out var files) || files.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var pathPropertyName in FileObjectPathProperties)
                {
                    AddStringProperty(file, pathPropertyName, filePaths);
                }
            }
        }
    }

    private static void AddDirectoryCandidates(JsonElement root, ISet<string> directoryPaths)
    {
        foreach (var propertyName in DirectoryProperties)
        {
            AddStringProperty(root, propertyName, directoryPaths);
        }
    }

    private static void AddStringProperty(JsonElement element, string propertyName, ISet<string> target)
    {
        if (TryGetPropertyIgnoreCase(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var path = value.GetString();
            if (!string.IsNullOrWhiteSpace(path))
            {
                target.Add(path);
            }
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed record StagingCleanupCandidates(
        HashSet<string> FilePaths,
        HashSet<string> DirectoryPaths);
}

public sealed record DownloadStagingCleanupResult(
    string Status,
    string? Error,
    int DeletedFiles,
    int DeletedDirectories,
    int SkippedPaths)
{
    public static DownloadStagingCleanupResult Completed(int deletedFiles, int deletedDirectories, int skippedPaths)
        => new(DownloadStagingCleanupService.CompletedStatus, null, deletedFiles, deletedDirectories, skippedPaths);

    public static DownloadStagingCleanupResult Failed(string error, int deletedFiles, int deletedDirectories, int skippedPaths)
        => new(DownloadStagingCleanupService.FailedStatus, error, deletedFiles, deletedDirectories, skippedPaths);

    public static DownloadStagingCleanupResult Skipped(string reason, int skippedPaths = 0)
        => new(DownloadStagingCleanupService.SkippedStatus, reason, 0, 0, skippedPaths);
}
