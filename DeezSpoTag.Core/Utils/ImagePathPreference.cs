namespace DeezSpoTag.Core.Utils;

public static class ImagePathPreference
{
    public static string? ChooseBetterImage(string? currentPath, string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return currentPath;
        }

        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return candidatePath;
        }

        if (string.Equals(currentPath, candidatePath, StringComparison.OrdinalIgnoreCase))
        {
            return currentPath;
        }

        var currentInfo = TryGetFileInfo(currentPath);
        var candidateInfo = TryGetFileInfo(candidatePath);

        if (candidateInfo is null)
        {
            return currentPath;
        }

        if (currentInfo is null)
        {
            return candidatePath;
        }

        if (candidateInfo.LastWriteTimeUtc > currentInfo.LastWriteTimeUtc)
        {
            return candidatePath;
        }

        if (candidateInfo.LastWriteTimeUtc < currentInfo.LastWriteTimeUtc)
        {
            return currentPath;
        }

        return candidateInfo.Length > currentInfo.Length ? candidatePath : currentPath;
    }

    private static FileInfo? TryGetFileInfo(string path)
    {
        try
        {
            return new FileInfo(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
