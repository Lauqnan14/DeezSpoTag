using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Services.Settings;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Controllers.Api;

internal static class AutoTagFolderScopeHelper
{
    public static async Task<IReadOnlyList<FolderDto>> ResolveLibraryFoldersAsync(
        LibraryRepository libraryRepository,
        LibraryConfigStore libraryConfigStore,
        CancellationToken cancellationToken)
    {
        try
        {
            return libraryRepository.IsConfigured
                ? await libraryRepository.GetFoldersAsync(cancellationToken)
                : libraryConfigStore.GetFolders();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return libraryConfigStore.GetFolders();
        }
    }

    public static async Task<IReadOnlyList<string>> ResolveAllowedLibraryRootsAsync(
        LibraryRepository libraryRepository,
        LibraryConfigStore libraryConfigStore,
        CancellationToken cancellationToken)
    {
        var folders = await ResolveLibraryFoldersAsync(libraryRepository, libraryConfigStore, cancellationToken);
        return LibraryFolderRootResolver.ResolveAccessibleRoots(folders);
    }

    public static async Task<IReadOnlyList<string>> ResolveAllowedAutoTagStartRootsAsync(
        LibraryRepository libraryRepository,
        LibraryConfigStore libraryConfigStore,
        DeezSpoTagSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var roots = new List<string>();
        roots.AddRange(await ResolveAllowedLibraryRootsAsync(libraryRepository, libraryConfigStore, cancellationToken));

        try
        {
            var settings = settingsService.LoadSettings();
            var downloadLocation = settings?.DownloadLocation?.Trim();
            if (!string.IsNullOrWhiteSpace(downloadLocation))
            {
                var normalized = LibraryFolderRootResolver.SafeGetFullPath(downloadLocation);
                if (!string.IsNullOrWhiteSpace(normalized) && Directory.Exists(normalized))
                {
                    roots.Add(normalized);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep library roots as fallback.
        }

        return roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsPathInAllowedRoots(string path, IReadOnlyList<string> allowedRoots)
    {
        return allowedRoots.Any(root => IsPathUnderRoot(path, root));
    }

    public static IReadOnlyList<long> ParseFolderIdsQuery(string? folderIds)
    {
        if (string.IsNullOrWhiteSpace(folderIds))
        {
            return Array.Empty<long>();
        }

        return folderIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => long.TryParse(token, out var parsed) ? parsed : 0L)
            .Where(value => value > 0)
            .Distinct()
            .ToList();
    }

    public static IReadOnlyList<long> NormalizeFolderIds(
        IReadOnlyList<long>? folderIds,
        long? legacyFolderId,
        IReadOnlyList<FolderDto> enabledFolders)
    {
        var selected = (folderIds ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        if (selected.Count == 0 && legacyFolderId.HasValue && legacyFolderId.Value > 0)
        {
            selected.Add(legacyFolderId.Value);
        }

        if (selected.Count == 0)
        {
            return Array.Empty<long>();
        }

        var enabledIds = enabledFolders.Select(folder => folder.Id).ToHashSet();
        return selected.Where(enabledIds.Contains).ToList();
    }

    public static bool IsPathUnderRoot(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSlash = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase);
    }
}
