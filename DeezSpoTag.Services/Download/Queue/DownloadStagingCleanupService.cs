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
    private static readonly string[] FileObjectDirectoryProperties =
    [
        "albumPath",
        "artistPath"
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
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac",
        ".mp3",
        ".m4a",
        ".mp4",
        ".aac",
        ".alac",
        ".wav",
        ".aif",
        ".aiff",
        ".ogg",
        ".oga",
        ".opus",
        ".wma",
        ".mka",
        ".webm"
    };

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
        => CleanupAsync(queueUuid, payloadJson, Array.Empty<string>(), cancellationToken);

    public Task<DownloadStagingCleanupResult> CleanupAsync(
        string queueUuid,
        string? payloadJson,
        IReadOnlyCollection<string> protectedPaths,
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

        var protectedStagingPaths = ResolveProtectedPaths(protectedPaths, rootPath);
        return Task.FromResult(CleanupCore(queueUuid, rootPath, candidates, protectedStagingPaths, cancellationToken));
    }

    public Task<DownloadStagingCleanupResult> CleanupOrphanSidecarDirectoriesAsync(
        IReadOnlyCollection<string> protectedPaths,
        CancellationToken cancellationToken = default)
    {
        var rootPath = ResolveDownloadRoot();
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return Task.FromResult(DownloadStagingCleanupResult.Skipped("download staging root is not configured"));
        }

        var protectedStagingPaths = ResolveProtectedPaths(protectedPaths, rootPath);
        var errors = new List<string>();
        var skippedPaths = 0;
        var deletedDirectories = 0;
        var candidates = Directory
            .EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories)
            .OrderByDescending(static path => path.Length)
            .ToList();

        foreach (var directory in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
            {
                continue;
            }

            if (ContainsProtectedPath(directory, protectedStagingPaths)
                || ContainsAudioFile(directory)
                || TraversesReparsePoint(directory, rootPath))
            {
                skippedPaths++;
                continue;
            }

            if (TryDeleteDirectoryTree(directory, rootPath, errors))
            {
                deletedDirectories++;
            }
        }

        return Task.FromResult(BuildCleanupResult(
            "orphan-sidecar-cleanup",
            deletedFiles: 0,
            deletedDirectories,
            skippedPaths,
            errors));
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
        IReadOnlyCollection<string> protectedPaths,
        CancellationToken cancellationToken)
    {
        var deletedFiles = 0;
        var skippedPaths = 0;
        var errors = new List<string>();
        var parentDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        deletedFiles = DeleteCandidateFiles(candidates.FilePaths, rootPath, parentDirectories, errors, ref skippedPaths, cancellationToken);
        CollectCandidateDirectories(candidates.DirectoryPaths, rootPath, parentDirectories, ref skippedPaths, cancellationToken);
        var deletedDirectories = DeleteOwnedRemnantDirectories(parentDirectories, rootPath, protectedPaths, errors, ref skippedPaths, cancellationToken);
        deletedDirectories += DeleteEmptyCandidateDirectories(parentDirectories, rootPath, errors, cancellationToken);

        return BuildCleanupResult(queueUuid, deletedFiles, deletedDirectories, skippedPaths, errors);
    }

    private static int DeleteCandidateFiles(
        IEnumerable<string> filePaths,
        string rootPath,
        HashSet<string> parentDirectories,
        List<string> errors,
        ref int skippedPaths,
        CancellationToken cancellationToken)
    {
        var deletedFiles = 0;
        foreach (var rawFilePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            deletedFiles += DeleteExpandedFileCandidates(rawFilePath, rootPath, parentDirectories, errors, ref skippedPaths);
        }

        return deletedFiles;
    }

    private static int DeleteExpandedFileCandidates(
        string rawFilePath,
        string rootPath,
        HashSet<string> parentDirectories,
        List<string> errors,
        ref int skippedPaths)
    {
        var deletedFiles = 0;
        var resolvedAny = false;
        foreach (var expandedPath in ExpandRelatedFileCandidates(rawFilePath))
        {
            if (!TryResolveOwnedChildPath(expandedPath, rootPath, out var fullPath))
            {
                continue;
            }

            resolvedAny = true;
            deletedFiles += DeleteFileIfExists(fullPath, errors);
            AddParentDirectory(fullPath, rootPath, parentDirectories);
        }

        if (!resolvedAny)
        {
            skippedPaths++;
        }

        return deletedFiles;
    }

    private static int DeleteFileIfExists(string fullPath, List<string> errors)
    {
        if (!File.Exists(fullPath))
        {
            return 0;
        }

        try
        {
            File.Delete(fullPath);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{fullPath}: {ex.Message}");
            return 0;
        }
    }

    private static void CollectCandidateDirectories(
        IEnumerable<string> directoryPaths,
        string rootPath,
        HashSet<string> parentDirectories,
        ref int skippedPaths,
        CancellationToken cancellationToken)
    {
        foreach (var rawDirectoryPath in directoryPaths)
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
    }

    private static int DeleteEmptyCandidateDirectories(
        HashSet<string> parentDirectories,
        string rootPath,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var deletedDirectories = 0;
        foreach (var directory in parentDirectories.OrderByDescending(static path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            deletedDirectories += DeleteEmptyParents(directory, rootPath, errors);
        }

        return deletedDirectories;
    }

    private static int DeleteOwnedRemnantDirectories(
        HashSet<string> parentDirectories,
        string rootPath,
        IReadOnlyCollection<string> protectedPaths,
        List<string> errors,
        ref int skippedPaths,
        CancellationToken cancellationToken)
    {
        var deletedDirectories = 0;
        foreach (var directory in parentDirectories.OrderByDescending(static path => path.Length).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
            {
                continue;
            }

            if (ContainsProtectedPath(directory, protectedPaths))
            {
                skippedPaths++;
                continue;
            }

            if (ContainsAudioFile(directory))
            {
                skippedPaths++;
                continue;
            }

            if (TryDeleteDirectoryTree(directory, rootPath, errors))
            {
                deletedDirectories++;
                parentDirectories.Remove(directory);
                AddAncestorDirectories(directory, rootPath, parentDirectories);
            }
        }

        return deletedDirectories;
    }

    private static bool TryDeleteDirectoryTree(string directory, string rootPath, List<string> errors)
    {
        if (!IsStrictChildOfRoot(directory, rootPath)
            || TraversesReparsePoint(directory, rootPath)
            || !Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{directory}: {ex.Message}");
            return false;
        }
    }

    private static bool ContainsAudioFile(string directory)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Any(IsPrimaryMediaFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsPrimaryMediaFile(string path)
    {
        if (!AudioExtensions.Contains(Path.GetExtension(path)))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        var normalized = name
            .Replace('-', ' ')
            .Replace('_', ' ')
            .Trim()
            .ToLowerInvariant();
        return !normalized.Contains("cover", StringComparison.Ordinal)
            && !normalized.Contains("artwork", StringComparison.Ordinal);
    }

    private static bool ContainsProtectedPath(string directory, IReadOnlyCollection<string> protectedPaths)
    {
        return protectedPaths.Any(path =>
            string.Equals(path, directory, StringComparison.OrdinalIgnoreCase)
            || IsSameOrDescendantPath(path, directory));
    }

    private static bool IsSameOrDescendantPath(string path, string directory)
    {
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(directory);
        var normalizedPath = Path.TrimEndingDirectorySeparator(path);
        if (string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.StartsWith(
            normalizedDirectory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void AddAncestorDirectories(
        string directory,
        string rootPath,
        HashSet<string> parentDirectories)
    {
        var parent = Directory.GetParent(directory)?.FullName;
        while (!string.IsNullOrWhiteSpace(parent) && IsStrictChildOfRoot(parent, rootPath))
        {
            parentDirectories.Add(parent);
            parent = Directory.GetParent(parent)?.FullName;
        }
    }

    private DownloadStagingCleanupResult BuildCleanupResult(
        string queueUuid,
        int deletedFiles,
        int deletedDirectories,
        int skippedPaths,
        List<string> errors)
    {
        if (errors.Count > 0)
        {
            return BuildFailedCleanupResult(queueUuid, deletedFiles, deletedDirectories, skippedPaths, errors);
        }

        if (deletedFiles == 0 && deletedDirectories == 0)
        {
            return skippedPaths > 0
                ? DownloadStagingCleanupResult.Failed("recorded staging paths were outside the download folder", 0, 0, skippedPaths)
                : DownloadStagingCleanupResult.Skipped("no existing staging files or empty folders were found", 0);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Cleaned staging files for {QueueUuid}: files={DeletedFiles}, folders={DeletedDirectories}, skipped={SkippedPaths}",
                LogSanitizer.OneLine(queueUuid),
                deletedFiles,
                deletedDirectories,
                skippedPaths);
        }
        return DownloadStagingCleanupResult.Completed(deletedFiles, deletedDirectories, skippedPaths);
    }

    private DownloadStagingCleanupResult BuildFailedCleanupResult(
        string queueUuid,
        int deletedFiles,
        int deletedDirectories,
        int skippedPaths,
        List<string> errors)
    {
        var message = string.Join("; ", errors.Distinct(StringComparer.OrdinalIgnoreCase));
        _logger.LogWarning(
            "Staging cleanup failed for {QueueUuid}: {Message}",
            LogSanitizer.OneLine(queueUuid),
            LogSanitizer.OneLine(message));
        return DownloadStagingCleanupResult.Failed(message, deletedFiles, deletedDirectories, skippedPaths);
    }

    private static IEnumerable<string> ExpandRelatedFileCandidates(string rawFilePath)
    {
        yield return rawFilePath;

        var extension = Path.GetExtension(rawFilePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            foreach (var audioPath in AudioExtensions.Select(audioExtension => rawFilePath + audioExtension))
            {
                yield return audioPath;
                foreach (var suffix in RelatedPathSuffixes)
                {
                    yield return audioPath + suffix;
                }
            }

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

        var directory = Path.GetDirectoryName(rawFilePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(rawFilePath);
        yield return Path.Join(directory, $"{stem}.part{extension}");
        for (var candidateIndex = 1; candidateIndex <= 20; candidateIndex++)
        {
            yield return Path.Join(directory, $"{stem}.candidate-{candidateIndex}.part{extension}");
        }
    }

    private static int DeleteEmptyParents(string startDirectory, string rootPath, List<string> errors)
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

    private static void AddParentDirectory(string fullPath, string rootPath, HashSet<string> parentDirectories)
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

    private static List<string> ResolveProtectedPaths(IReadOnlyCollection<string> protectedPaths, string rootPath)
    {
        var resolved = new List<string>();
        foreach (var fullPath in protectedPaths
            .Select(protectedPath => TryResolveOwnedChildPath(protectedPath, rootPath, out var fullPath) ? fullPath : null)
            .Where(static fullPath => !string.IsNullOrWhiteSpace(fullPath)))
        {
            resolved.Add(fullPath!);
        }

        return resolved
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            AddFileArrayCandidates(document.RootElement, filePaths, directoryPaths);
            AddDirectoryCandidates(document.RootElement, directoryPaths);
            AddSiblingDirectoryCandidates(filePaths, directoryPaths);
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

    private static void AddFileArrayCandidates(
        JsonElement root,
        HashSet<string> filePaths,
        HashSet<string> directoryPaths)
    {
        foreach (var propertyName in FileArrayProperties)
        {
            if (!TryGetPropertyIgnoreCase(root, propertyName, out var files) || files.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var file in files.EnumerateArray())
            {
                AddFileArrayCandidate(file, filePaths, directoryPaths);
            }
        }
    }

    private static void AddFileArrayCandidate(
        JsonElement file,
        HashSet<string> filePaths,
        HashSet<string> directoryPaths)
    {
        if (file.ValueKind == JsonValueKind.String)
        {
            AddStringValue(file, filePaths);
            return;
        }

        if (file.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var pathPropertyName in FileObjectPathProperties)
        {
            AddStringProperty(file, pathPropertyName, filePaths);
        }

        foreach (var directoryPropertyName in FileObjectDirectoryProperties)
        {
            AddStringProperty(file, directoryPropertyName, directoryPaths);
        }
    }

    private static void AddStringValue(JsonElement element, HashSet<string> values)
    {
        var rawValue = element.GetString();
        if (!string.IsNullOrWhiteSpace(rawValue))
        {
            values.Add(rawValue);
        }
    }

    private static void AddSiblingDirectoryCandidates(
        IEnumerable<string> filePaths,
        HashSet<string> directoryPaths)
    {
        foreach (var directory in filePaths
            .Select(DownloadPathResolver.ResolveIoPath)
            .Where(static ioPath => !string.IsNullOrWhiteSpace(ioPath) && !DownloadPathResolver.IsSmbPath(ioPath))
            .Select(Path.GetDirectoryName)
            .Where(static directory => !string.IsNullOrWhiteSpace(directory)))
        {
            directoryPaths.Add(directory!);
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
            var property = element.EnumerateObject()
                .FirstOrDefault(candidate => string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase));
            if (property.Value.ValueKind != JsonValueKind.Undefined)
            {
                value = property.Value;
                return true;
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
