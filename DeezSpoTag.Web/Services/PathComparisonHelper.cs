using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Web.Services;

internal static class PathComparisonHelper
{
    public static bool IsPathUnderRoot(string rootPath, string candidatePath)
    {
        if (!TryResolveComparableIoPaths(rootPath, candidatePath, out var rootIo, out var candidateIo))
        {
            return false;
        }

        var normalizedRoot = rootIo.Replace('\\', '/').TrimEnd('/');
        var normalizedCandidate = candidateIo.Replace('\\', '/').TrimEnd('/');
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetRelativePathUnderRoot(string rootPath, string candidatePath, out string relative)
    {
        relative = string.Empty;
        if (!TryResolveComparableIoPaths(rootPath, candidatePath, out var rootIo, out var candidateIo))
        {
            return false;
        }

        var normalizedRoot = rootIo.Replace('\\', '/').TrimEnd('/') + "/";
        var normalizedCandidate = candidateIo.Replace('\\', '/');
        if (normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = normalizedCandidate[normalizedRoot.Length..].TrimStart('/');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return false;
            }

            relative = trimmed.Replace('/', Path.DirectorySeparatorChar);
            return true;
        }

        var fallback = Path.GetRelativePath(rootIo, candidateIo);
        if (fallback.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        relative = fallback;
        return true;
    }

    public static bool TryResolveComparableIoPaths(
        string rootPath,
        string candidatePath,
        out string rootIo,
        out string candidateIo)
    {
        rootIo = string.Empty;
        candidateIo = string.Empty;

        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        rootIo = DownloadPathResolver.ResolveIoPath(rootPath);
        candidateIo = DownloadPathResolver.ResolveIoPath(candidatePath);
        if (string.IsNullOrWhiteSpace(rootIo) || string.IsNullOrWhiteSpace(candidateIo))
        {
            return false;
        }

        try
        {
            if (!DownloadPathResolver.IsSmbPath(rootIo))
            {
                rootIo = Path.GetFullPath(rootIo);
            }

            if (!DownloadPathResolver.IsSmbPath(candidateIo))
            {
                candidateIo = Path.GetFullPath(candidateIo);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return false;
        }

        return true;
    }
}
