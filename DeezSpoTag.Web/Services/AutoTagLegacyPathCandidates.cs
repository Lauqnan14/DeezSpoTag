namespace DeezSpoTag.Web.Services;

internal static class AutoTagLegacyPathCandidates
{
    public static IEnumerable<string> Enumerate(
        string contentRoot,
        string dataRoot,
        string currentPath,
        string fileName,
        string autoTagFolderName)
    {
        var candidates = new[]
        {
            Path.Join(contentRoot, "Data", autoTagFolderName, fileName),
            Path.Join(Directory.GetCurrentDirectory(), "Data", autoTagFolderName, fileName),
            Path.Join(Directory.GetCurrentDirectory(), "DeezSpoTag.Web", "Data", autoTagFolderName, fileName)
        };

        var activeDirectory = Path.GetFullPath(Path.Join(dataRoot, autoTagFolderName));
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(candidate);
            if (string.Equals(fullPath, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fullDirectory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(fullDirectory)
                && string.Equals(fullDirectory, activeDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return fullPath;
        }
    }
}
