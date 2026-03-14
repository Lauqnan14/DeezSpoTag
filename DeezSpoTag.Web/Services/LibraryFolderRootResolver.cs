using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

internal static class LibraryFolderRootResolver
{
    public static IReadOnlyList<string> ResolveAccessibleRoots(
        IReadOnlyList<FolderDto> folders,
        bool throwWhenNone = false)
    {
        var normalized = folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder.RootPath))
            .Select(folder => new
            {
                Root = SafeGetFullPath(folder.RootPath),
                folder.Enabled
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Root))
            .ToList();

        var enabled = normalized
            .Where(item => item.Enabled && Directory.Exists(item.Root!))
            .Select(item => item.Root!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (enabled.Count > 0)
        {
            return enabled;
        }

        var existing = normalized
            .Where(item => Directory.Exists(item.Root!))
            .Select(item => item.Root!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (throwWhenNone && existing.Count == 0)
        {
            throw new InvalidOperationException("No accessible library folders are configured.");
        }

        return existing;
    }

    public static string SafeGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }
}
