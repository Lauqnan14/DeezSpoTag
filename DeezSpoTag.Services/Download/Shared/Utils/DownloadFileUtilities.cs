using DeezSpoTag.Services.Download.Utils;
using IOFile = System.IO.File;

namespace DeezSpoTag.Services.Download.Shared.Utils;

internal static class DownloadFileUtilities
{
    public static string SanitizeFilename(string? value, string fallback = "unknown")
    {
        return CjkFilenameSanitizer.SanitizeSegment(
            value ?? string.Empty,
            fallback: fallback,
            replacement: "_",
            collapseWhitespace: true,
            trimTrailingDotsAndSpaces: true);
    }

    public static void TryDeleteFile(string path)
    {
        try
        {
            if (IOFile.Exists(path))
            {
                IOFile.Delete(path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort cleanup.
        }
    }

    public static string? ResolveExecutablePath(IEnumerable<string> candidates, string? explicitEnvironmentVariable = null)
    {
        var explicitPath = TryResolveExplicitExecutablePath(explicitEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var searchDirs = GetSearchDirectories();

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var resolvedPath = ResolveCandidateExecutablePath(candidate, searchDirs);
            if (!string.IsNullOrWhiteSpace(resolvedPath))
            {
                return resolvedPath;
            }
        }

        return null;
    }

    public static string TruncateForLog(string? value, int maxLength = 400)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : $"{value[..maxLength]}...";
    }

    private static string? TryResolveExplicitExecutablePath(string? explicitEnvironmentVariable)
    {
        if (string.IsNullOrWhiteSpace(explicitEnvironmentVariable))
        {
            return null;
        }

        var explicitPath = Environment.GetEnvironmentVariable(explicitEnvironmentVariable);
        return TryGetExistingPath(explicitPath);
    }

    private static string[] GetSearchDirectories()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separators = OperatingSystem.IsWindows() ? ';' : ':';
        return pathValue.Split(separators, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? ResolveCandidateExecutablePath(string candidate, IEnumerable<string> searchDirs)
    {
        if (Path.IsPathRooted(candidate))
        {
            return TryGetExistingPath(candidate);
        }

        var fromPath = searchDirs
            .Select(dir => Path.Join(dir, candidate))
            .Select(TryGetExistingPath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return fromPath;
        }

        return TryGetExistingPath(Path.Join(AppContext.BaseDirectory, candidate));
    }

    private static string? TryGetExistingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        return IOFile.Exists(fullPath) ? fullPath : null;
    }
}
