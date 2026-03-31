using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Services.Download.Queue;

public static class DownloadDestinationGuard
{
    private const string ModeStereo = "stereo";
    private const string ModeAtmos = "atmos";
    private const string ModeVideo = "video";
    private const string ModePodcast = "podcast";

    public static async Task<(bool Ok, string? Error)> ValidateAsync(
        long? destinationFolderId,
        string? downloadRoot,
        LibraryRepository libraryRepository,
        CancellationToken cancellationToken,
        string? contentType = null)
    {
        if (!destinationFolderId.HasValue)
        {
            return (false, "Destination folder is required.");
        }

        if (!libraryRepository.IsConfigured)
        {
            return (false, "Library folders are not configured.");
        }

        var normalizedContentType = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        var requiresAutoTagFolder = !string.Equals(normalizedContentType, ModeVideo, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalizedContentType, ModePodcast, StringComparison.OrdinalIgnoreCase);

        var folders = await libraryRepository.GetFoldersAsync(cancellationToken);
        var folder = folders.FirstOrDefault(item =>
            item.Id == destinationFolderId.Value
            && item.Enabled
            && (!requiresAutoTagFolder || item.AutoTagEnabled));
        if (folder == null)
        {
            return (false, "Destination folder not found or disabled.");
        }

        var folderMode = ResolveFolderMode(folder.DesiredQuality);
        if (!IsCompatibleMode(folderMode, normalizedContentType))
        {
            return (false, $"Destination folder is configured for {folderMode} content, not {normalizedContentType}.");
        }

        if (requiresAutoTagFolder && string.IsNullOrWhiteSpace(folder.AutoTagProfileId))
        {
            return (false, "Destination music folder requires an AutoTag profile.");
        }

        if (string.IsNullOrWhiteSpace(downloadRoot))
        {
            return (false, "Download location is required.");
        }

        var destinationRoot = folder.RootPath;
        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            return (false, "Destination folder path is missing.");
        }

        if (requiresAutoTagFolder && IsSameOrDescendantPath(destinationRoot, downloadRoot))
        {
            return (false, "Destination music folder cannot be the download location or a subfolder of it.");
        }

        return (true, null);
    }

    private static bool IsSameOrDescendantPath(string candidatePath, string rootPath)
    {
        var normalizedRoot = NormalizeRoot(rootPath);
        var normalizedCandidate = NormalizeRoot(candidatePath);
        if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            || normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string path)
    {
        if (DownloadPathResolver.IsSmbPath(path))
        {
            return path.TrimEnd('/');
        }

        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ResolveFolderMode(string? desiredQuality)
    {
        var normalized = (desiredQuality ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized == ModeVideo)
        {
            return ModeVideo;
        }

        if (normalized == ModePodcast)
        {
            return ModePodcast;
        }

        return normalized == ModeAtmos ? ModeAtmos : ModeStereo;
    }

    private static bool IsCompatibleMode(string folderMode, string requestedMode)
    {
        if (string.IsNullOrWhiteSpace(requestedMode))
        {
            return true;
        }

        if (requestedMode == ModeVideo)
        {
            return folderMode == ModeVideo;
        }

        if (requestedMode == ModePodcast)
        {
            return folderMode == ModePodcast;
        }

        return folderMode != ModeVideo && folderMode != ModePodcast;
    }
}
