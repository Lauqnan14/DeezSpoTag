using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Web.Services;

public static class KnownLibraryFilePathSet
{
    public static Dictionary<long, List<string>> NormalizeByFolder(
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

    public static bool IsExistingAudioFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension is ".mp3" or ".flac" or ".m4a" or ".m4b" or ".wav" or ".ogg" or ".opus" or ".aiff" or ".aif" or ".alac" or ".aac";
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
}
