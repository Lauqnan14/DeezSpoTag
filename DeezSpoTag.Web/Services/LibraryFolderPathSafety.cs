using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

internal static class LibraryFolderPathSafety
{
    public static bool IsMusicFolder(FolderDto folder)
    {
        var mode = folder.DesiredQuality?.Trim().ToLowerInvariant();
        return mode is not "video" and not "podcast";
    }

    public static bool IsSameOrDescendantPath(string candidatePath, string rootPath)
    {
        var normalizedRoot = NormalizeRootPath(rootPath);
        var normalizedCandidate = NormalizeRootPath(candidatePath);
        if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = normalizedRoot.EndsWith('/')
            || normalizedRoot.EndsWith('\\')
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRootPath(string path)
    {
        if (DownloadPathResolver.IsSmbPath(path))
        {
            return path.TrimEnd('/');
        }

        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
