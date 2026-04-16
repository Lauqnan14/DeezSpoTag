using DeezSpoTag.Core.Models.Settings;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Library;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Linq;

namespace DeezSpoTag.Services.Download.Utils;

public sealed class DownloadMoveService
{
    private const char SmbPathSeparator = '/';

    private readonly ILogger<DownloadMoveService> _logger;
    private readonly LibraryRepository _libraryRepository;

    public DownloadMoveService(ILogger<DownloadMoveService> logger, LibraryRepository libraryRepository)
    {
        _logger = logger;
        _libraryRepository = libraryRepository;
    }

    public async Task<DownloadMoveResult?> MoveToLibraryAsync(
        DeezSpoTagDownloadObject downloadObject,
        DeezSpoTagSettings settings,
        IEnumerable<string> extraFiles,
        CancellationToken cancellationToken = default)
    {
        var stagingRootDisplay = ResolveStagingRootDisplay(settings, downloadObject);
        if (string.IsNullOrWhiteSpace(stagingRootDisplay))
        {
            return null;
        }

        var destinationRootDisplay = await ResolveDestinationRootAsync(downloadObject.DestinationFolderId, cancellationToken);
        if (string.IsNullOrWhiteSpace(destinationRootDisplay))
        {
            throw new InvalidOperationException("DestinationFolderRequired");
        }

        var stagingRootIo = DownloadPathResolver.ResolveIoPath(stagingRootDisplay);
        var destinationRootIo = DownloadPathResolver.ResolveIoPath(destinationRootDisplay);
        if (IsSameRoot(stagingRootIo, destinationRootIo))
        {
            var stagingFull = DownloadPathResolver.IsSmbPath(stagingRootIo) ? stagingRootIo : Path.GetFullPath(stagingRootIo);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Download move skipped because staging and destination match: {Path}", stagingFull);            }
            return null;
        }

        var trackedPaths = BuildTrackedPaths(downloadObject);
        var sourcePaths = BuildSourceIoPaths(downloadObject, stagingRootDisplay, extraFiles);
        var moveOutcome = MoveFiles(
            sourcePaths,
            trackedPaths,
            stagingRootIo,
            destinationRootIo,
            settings.OverwriteFile,
            cancellationToken);

        if (moveOutcome.Moved.Count == 0)
        {
            return null;
        }

        UpdateDownloadObjectPaths(downloadObject, stagingRootDisplay, destinationRootDisplay, moveOutcome.Moved);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Moved {MovedCount} download files to library destination {Destination} (skipped: {Skipped})",
                moveOutcome.Moved.Count,
                destinationRootDisplay,
                moveOutcome.Skipped);        }

        return new DownloadMoveResult(destinationRootDisplay, moveOutcome.Moved, moveOutcome.Skipped);
    }

    private static string? ResolveStagingRootDisplay(DeezSpoTagSettings settings, DeezSpoTagDownloadObject downloadObject)
    {
        return !string.IsNullOrWhiteSpace(settings.DownloadLocation)
            ? settings.DownloadLocation
            : downloadObject.ExtrasPath;
    }

    private static bool IsSameRoot(string stagingRootIo, string destinationRootIo)
    {
        var stagingFull = DownloadPathResolver.IsSmbPath(stagingRootIo) ? stagingRootIo : Path.GetFullPath(stagingRootIo);
        var destinationFull = DownloadPathResolver.IsSmbPath(destinationRootIo) ? destinationRootIo : Path.GetFullPath(destinationRootIo);
        return string.Equals(stagingFull, destinationFull, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> BuildTrackedPaths(DeezSpoTagDownloadObject downloadObject)
    {
        return downloadObject.Files
            .Select(file => GetEntryString(file, "path"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> BuildSourceIoPaths(
        DeezSpoTagDownloadObject downloadObject,
        string stagingRootDisplay,
        IEnumerable<string> extraFiles)
    {
        var ioPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var moveRoots = GetMoveRoots(downloadObject, stagingRootDisplay);
        foreach (var rootIo in moveRoots
            .Select(root => DownloadPathResolver.ResolveIoPath(root))
            .Where(rootIo => !string.IsNullOrWhiteSpace(rootIo) && Directory.Exists(rootIo)))
        {
            foreach (var file in Directory.EnumerateFiles(rootIo!, "*", SearchOption.AllDirectories))
            {
                var displayPath = DownloadPathResolver.NormalizeDisplayPath(file);
                ioPaths[displayPath] = file;
            }
        }

        foreach (var extra in extraFiles.Where(extra => !string.IsNullOrWhiteSpace(extra)))
        {
            ioPaths[extra] = DownloadPathResolver.ResolveIoPath(extra);
        }

        return ioPaths;
    }

    private static MoveOutcome MoveFiles(
        IReadOnlyDictionary<string, string> sourcePaths,
        HashSet<string> trackedPaths,
        string stagingRootIo,
        string destinationRootIo,
        string trackedOverwritePolicy,
        CancellationToken cancellationToken)
    {
        var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

        foreach (var (sourcePath, sourceIoPath) in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!System.IO.File.Exists(sourceIoPath))
            {
                skipped++;
                continue;
            }

            var overwritePolicy = trackedPaths.Contains(sourcePath) ? trackedOverwritePolicy : "y";
            var movedPath = MoveFileWithPolicy(sourceIoPath, stagingRootIo, destinationRootIo, overwritePolicy);
            if (!string.IsNullOrWhiteSpace(movedPath))
            {
                moved[sourcePath] = DownloadPathResolver.NormalizeDisplayPath(movedPath);
            }
        }

        return new MoveOutcome(moved, skipped);
    }

    private async Task<string?> ResolveDestinationRootAsync(long? destinationFolderId, CancellationToken cancellationToken)
    {
        if (!_libraryRepository.IsConfigured || !destinationFolderId.HasValue)
        {
            return null;
        }

        var folders = await _libraryRepository.GetFoldersAsync(cancellationToken);
        var explicitFolder = folders.FirstOrDefault(folder => folder.Id == destinationFolderId.Value);
        if (explicitFolder != null && explicitFolder.Enabled)
        {
            return explicitFolder.RootPath;
        }

        return null;
    }

    private static List<string> GetMoveRoots(DeezSpoTagDownloadObject downloadObject, string stagingRootDisplay)
    {
        var roots = new List<string>();

        if (!string.IsNullOrWhiteSpace(downloadObject.ExtrasPath))
        {
            roots.Add(downloadObject.ExtrasPath);
        }

        foreach (var file in downloadObject.Files)
        {
            var albumPath = GetEntryString(file, "albumPath");
            if (!string.IsNullOrWhiteSpace(albumPath))
            {
                roots.Add(albumPath);
            }

            var artistPath = GetEntryString(file, "artistPath");
            if (!string.IsNullOrWhiteSpace(artistPath))
            {
                roots.Add(artistPath);
            }
        }

        var normalizedRoots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => DownloadPathResolver.NormalizeDisplayPath(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(root => IsUnderRoot(stagingRootDisplay, root))
            .ToList();

        normalizedRoots.Sort((left, right) => left.Length.CompareTo(right.Length));

        var filtered = new List<string>();
        foreach (var root in normalizedRoots)
        {
            if (filtered.Any(existing => IsUnderRoot(existing, root)))
            {
                continue;
            }

            filtered.Add(root);
        }

        return filtered;
    }

    private static string? MoveFileWithPolicy(string sourcePath, string stagingRoot, string destinationRoot, string overwritePolicy)
    {
        if (!IsUnderRoot(stagingRoot, sourcePath))
        {
            return null;
        }

        var relativePath = Path.GetRelativePath(stagingRoot, sourcePath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal))
        {
            return null;
        }

        var destinationPath = Path.Join(destinationRoot, relativePath);
        var destinationDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        if (System.IO.File.Exists(destinationPath))
        {
            switch (overwritePolicy)
            {
                case "b":
                    destinationPath = GetUniqueDestinationPath(destinationDir ?? destinationRoot, destinationPath);
                    break;
                case "y":
                case "t":
                    break;
                default:
                    System.IO.File.Delete(sourcePath);
                    return destinationPath;
            }
        }

        MoveFileWithFallback(sourcePath, destinationPath);

        return destinationPath;
    }

    private static void MoveFileWithFallback(string sourcePath, string destinationPath)
    {
        var allowCopyFallback = ShouldUseCopyFallback(sourcePath, destinationPath);
        const int maxMoveAttempts = 6;
        for (var attempt = 1; attempt <= maxMoveAttempts; attempt++)
        {
            try
            {
                System.IO.File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < maxMoveAttempts)
            {
                System.Threading.Thread.Sleep(50 * attempt);
            }
            catch (IOException) when (allowCopyFallback)
            {
                MoveFileByCopyWithDeleteGuard(sourcePath, destinationPath);
                return;
            }
        }

        System.IO.File.Move(sourcePath, destinationPath, overwrite: true);
    }

    private static bool ShouldUseCopyFallback(string sourcePath, string destinationPath)
    {
        var sourceIo = DownloadPathResolver.ResolveIoPath(sourcePath);
        var destinationIo = DownloadPathResolver.ResolveIoPath(destinationPath);
        if (string.IsNullOrWhiteSpace(sourceIo) || string.IsNullOrWhiteSpace(destinationIo))
        {
            return false;
        }

        if (DownloadPathResolver.IsSmbPath(sourceIo) || DownloadPathResolver.IsSmbPath(destinationIo))
        {
            return true;
        }

        try
        {
            var sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourceIo));
            var destinationRoot = Path.GetPathRoot(Path.GetFullPath(destinationIo));
            if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(destinationRoot))
            {
                return false;
            }

            return !string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void MoveFileByCopyWithDeleteGuard(string sourcePath, string destinationPath)
    {
        System.IO.File.Copy(sourcePath, destinationPath, overwrite: true);
        if (TryDeleteWithRetries(sourcePath))
        {
            return;
        }

        TryDeleteSilently(destinationPath);
        throw new IOException($"Move fallback copied file but could not delete source: {sourcePath}");
    }

    private static bool TryDeleteWithRetries(string path)
    {
        const int maxDeleteAttempts = 12;
        for (var attempt = 1; attempt <= maxDeleteAttempts; attempt++)
        {
            try
            {
                System.IO.File.Delete(path);
                return true;
            }
            catch (IOException) when (attempt < maxDeleteAttempts)
            {
                System.Threading.Thread.Sleep(50 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxDeleteAttempts)
            {
                System.Threading.Thread.Sleep(50 * attempt);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return !System.IO.File.Exists(path);
    }

    private static void TryDeleteSilently(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }

    private static string GetUniqueDestinationPath(string destinationDir, string destinationPath)
    {
        var filename = Path.GetFileNameWithoutExtension(destinationPath);
        var extension = Path.GetExtension(destinationPath);
        var uniqueFilename = DownloadUtils.GenerateUniqueFilename(destinationDir, filename, extension);
        return Path.Join(destinationDir, uniqueFilename + extension);
    }

    private static void UpdateDownloadObjectPaths(
        DeezSpoTagDownloadObject downloadObject,
        string stagingRoot,
        string destinationRoot,
        Dictionary<string, string> movedPaths)
    {
        foreach (var file in downloadObject.Files)
        {
            var path = GetEntryString(file, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (movedPaths.TryGetValue(path, out var newPath))
            {
                file["path"] = newPath;
                var relativePath = GetRelativePath(destinationRoot, newPath);
                if (!string.IsNullOrWhiteSpace(relativePath))
                {
                    file["filename"] = relativePath;
                }
            }
            else if (IsUnderRoot(stagingRoot, path))
            {
                var relativePath = GetRelativePath(stagingRoot, path);
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    continue;
                }

                var relocatedPath = DownloadPathResolver.Combine(destinationRoot, relativePath);
                file["path"] = relocatedPath;
                var filename = GetRelativePath(destinationRoot, relocatedPath);
                if (!string.IsNullOrWhiteSpace(filename))
                {
                    file["filename"] = filename;
                }
            }

            UpdateEntryPath(file, "albumPath", stagingRoot, destinationRoot);
            UpdateEntryPath(file, "artistPath", stagingRoot, destinationRoot);
        }

        downloadObject.ExtrasPath = destinationRoot;
    }

    private static bool IsUnderRoot(string root, string path)
    {
        if (DownloadPathResolver.IsSmbPath(root) || DownloadPathResolver.IsSmbPath(path))
        {
            var normalizedRootPath = NormalizeSmbRootPrefix(root);
            var normalizedPathValue = DownloadPathResolver.NormalizeDisplayPath(path);
            return normalizedPathValue.StartsWith(normalizedRootPath, StringComparison.OrdinalIgnoreCase);
        }

        var normalizedRootPathLocal = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPathValueLocal = Path.GetFullPath(path);
        return normalizedPathValueLocal.StartsWith(normalizedRootPathLocal, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetEntryString(Dictionary<string, object> entry, string key)
    {
        if (!entry.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string str => str,
            System.Text.Json.JsonElement element => element.ValueKind == System.Text.Json.JsonValueKind.String
                ? element.GetString()
                : element.ToString(),
            _ => value.ToString()
        };
    }

    private static void UpdateEntryPath(Dictionary<string, object> entry, string key, string stagingRoot, string destinationRoot)
    {
        var existing = GetEntryString(entry, key);
        if (string.IsNullOrWhiteSpace(existing) || !IsUnderRoot(stagingRoot, existing))
        {
            return;
        }

        var relativePath = GetRelativePath(stagingRoot, existing);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        entry[key] = DownloadPathResolver.Combine(destinationRoot, relativePath);
    }

    private static string? GetRelativePath(string root, string path)
    {
        if (DownloadPathResolver.IsSmbPath(root) || DownloadPathResolver.IsSmbPath(path))
        {
            var normalizedRoot = NormalizeSmbRootPrefix(root);
            var normalizedPath = DownloadPathResolver.NormalizeDisplayPath(path);
            if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return normalizedPath[normalizedRoot.Length..];
        }

        return Path.GetRelativePath(root, path);
    }

    private static string NormalizeSmbRootPrefix(string path)
    {
        return DownloadPathResolver.NormalizeDisplayPath(path).TrimEnd(SmbPathSeparator) + SmbPathSeparator;
    }

    private sealed record MoveOutcome(Dictionary<string, string> Moved, int Skipped);
}

public sealed record DownloadMoveResult(string DestinationRoot, IReadOnlyDictionary<string, string> MovedPaths, int SkippedCount);
