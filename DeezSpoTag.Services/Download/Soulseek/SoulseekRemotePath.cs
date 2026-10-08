namespace DeezSpoTag.Services.Download.Soulseek;

public static class SoulseekRemotePath
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var segments = path
            .Trim()
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return string.Empty;
        }

        var prefix = path.TrimStart().StartsWith('/') ? "/" : string.Empty;
        return prefix + string.Join('/', segments);
    }

    public static string GetDirectory(string? path)
    {
        var normalized = Normalize(path);
        var separatorIndex = normalized.LastIndexOf('/');

        return separatorIndex <= 0
            ? string.Empty
            : normalized[..separatorIndex];
    }

    public static string GetLeaf(string? path)
    {
        var normalized = Normalize(path);
        var separatorIndex = normalized.LastIndexOf('/');

        return separatorIndex < 0
            ? normalized
            : normalized[(separatorIndex + 1)..];
    }

    public static bool IsDirectChildOf(string? path, string? directory)
    {
        var normalizedPath = Normalize(path);
        var normalizedDirectory = Normalize(directory);

        if (normalizedPath.Length == 0 || normalizedDirectory.Length == 0)
        {
            return false;
        }

        return string.Equals(
            GetDirectory(normalizedPath),
            normalizedDirectory,
            StringComparison.OrdinalIgnoreCase);
    }
}
