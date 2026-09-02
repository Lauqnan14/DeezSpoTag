using System.Text.Json;
using DeezSpoTag.Services.Download.Shared;
using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Services.Download.Queue;

public static class DownloadStagingFileOwnership
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac",
        ".wav",
        ".aiff",
        ".aif",
        ".alac",
        ".m4a",
        ".m4b",
        ".mp4",
        ".aac",
        ".mp3",
        ".wma",
        ".ogg",
        ".opus",
        ".oga",
        ".ape",
        ".wv",
        ".mp2",
        ".mp1",
        ".tta",
        ".dsf",
        ".dff",
        ".mka"
    };

    public static IReadOnlyList<string> ResolveOwnedStagingAudioFiles(
        DownloadQueueItem item,
        string downloadRoot,
        bool requireExisting = true)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectOwnedAudioPaths(item.PayloadJson, item.FinalDestinationsJson, downloadRoot, requireExisting, files);
        return files
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool HasOwnedStagingAudio(DownloadQueueItem item, string downloadRoot)
        => ResolveOwnedStagingAudioFiles(item, downloadRoot).Count > 0;

    public static bool OwnsAnyPath(
        DownloadQueueItem item,
        string downloadRoot,
        IReadOnlySet<string> allowedPaths)
    {
        if (allowedPaths.Count == 0)
        {
            return false;
        }

        foreach (var path in ResolveOwnedStagingAudioFiles(item, downloadRoot, requireExisting: false))
        {
            if (allowedPaths.Contains(NormalizePath(path)))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasAcquiredAudio(DownloadQueueItem item)
    {
        if (string.IsNullOrWhiteSpace(item.PayloadJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (TryReadBoolean(root, "AudioAcquired", "audioAcquired"))
            {
                return true;
            }

            return TryReadString(root, "AcquiredAudioPath", "acquiredAudioPath", out var acquiredPath)
                   && FileExists(acquiredPath);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static long? ReadDestinationFolderId(DownloadQueueItem item)
    {
        if (item.DestinationFolderId is > 0)
        {
            return item.DestinationFolderId;
        }

        if (string.IsNullOrWhiteSpace(item.PayloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (TryReadInt64(root, "DestinationFolderId", "destinationFolderId", "destination_folder_id", out var destinationFolderId)
                && destinationFolderId > 0)
            {
                return destinationFolderId;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static void CollectOwnedAudioPaths(
        string? payloadJson,
        string? finalDestinationsJson,
        string downloadRoot,
        bool requireExisting,
        ISet<string> files)
    {
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    AddAudioPath(root, "filePath", downloadRoot, requireExisting, files);
                    AddAudioPath(root, "FilePath", downloadRoot, requireExisting, files);
                    AddAudioPath(root, "acquiredAudioPath", downloadRoot, requireExisting, files);
                    AddAudioPath(root, "AcquiredAudioPath", downloadRoot, requireExisting, files);
                    AddPayloadFileEntries(root, "files", downloadRoot, requireExisting, files);
                    AddPayloadFileEntries(root, "Files", downloadRoot, requireExisting, files);
                }
            }
            catch (JsonException)
            {
                // Durable final-destination ownership remains available when the payload is malformed.
            }
        }

        AddFinalDestinationAudioPaths(finalDestinationsJson, downloadRoot, requireExisting, files);
    }

    private static void AddPayloadFileEntries(
        JsonElement root,
        string propertyName,
        string downloadRoot,
        bool requireExisting,
        ISet<string> files)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var filesElement)
            || filesElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var fileElement in filesElement.EnumerateArray())
        {
            if (fileElement.ValueKind == JsonValueKind.String)
            {
                AddAudioPathValue(fileElement.GetString(), downloadRoot, requireExisting, files);
                continue;
            }

            if (fileElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (TryReadString(fileElement, "type", "Type", out var type)
                && string.Equals(type, "artwork", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddAudioPath(fileElement, "path", downloadRoot, requireExisting, files);
            AddAudioPath(fileElement, "Path", downloadRoot, requireExisting, files);
        }
    }

    private static void AddFinalDestinationAudioPaths(
        string? finalDestinationsJson,
        string downloadRoot,
        bool requireExisting,
        ISet<string> files)
    {
        if (string.IsNullOrWhiteSpace(finalDestinationsJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(finalDestinationsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                AddAudioPathValue(property.Name, downloadRoot, requireExisting, files);
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    AddAudioPathValue(property.Value.GetString(), downloadRoot, requireExisting, files);
                }
            }
        }
        catch (JsonException)
        {
            // Malformed durable mappings provide no safe file ownership evidence.
        }
    }

    private static void AddAudioPath(
        JsonElement source,
        string propertyName,
        string downloadRoot,
        bool requireExisting,
        ISet<string> files)
    {
        if (!TryGetPropertyIgnoreCase(source, propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        AddAudioPathValue(value.GetString(), downloadRoot, requireExisting, files);
    }

    private static void AddAudioPathValue(
        string? path,
        string downloadRoot,
        bool requireExisting,
        ISet<string> files)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !IsPathUnderRoot(downloadRoot, path)
            || !AudioExtensions.Contains(Path.GetExtension(path))
            || AnimatedArtworkNaming.IsAnimatedArtworkSidecar(path))
        {
            return;
        }

        var ioPath = DownloadPathResolver.ResolveIoPath(path);
        if (string.IsNullOrWhiteSpace(ioPath) || requireExisting && !FileExists(ioPath))
        {
            return;
        }

        var normalized = NormalizePath(ioPath);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            files.Add(normalized);
        }
    }

    private static bool IsPathUnderRoot(string rootPath, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        var rootIo = DownloadPathResolver.ResolveIoPath(rootPath);
        var candidateIo = DownloadPathResolver.ResolveIoPath(candidatePath);
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
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var normalizedRoot = rootIo.Replace('\\', '/').TrimEnd('/');
        var normalizedCandidate = candidateIo.Replace('\\', '/').TrimEnd('/');
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || normalizedCandidate.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var ioPath = DownloadPathResolver.ResolveIoPath(path);
            if (string.IsNullOrWhiteSpace(ioPath))
            {
                return string.Empty;
            }

            if (DownloadPathResolver.IsSmbPath(ioPath))
            {
                return ioPath.Trim().TrimEnd('/', '\\');
            }

            return Path.GetFullPath(ioPath.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static bool FileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ioPath = DownloadPathResolver.ResolveIoPath(path);
        return !string.IsNullOrWhiteSpace(ioPath) && File.Exists(ioPath);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement source, string propertyName, out JsonElement value)
    {
        if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryReadString(JsonElement source, string primary, string alternate, out string value)
    {
        if (TryGetPropertyIgnoreCase(source, primary, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        if (TryGetPropertyIgnoreCase(source, alternate, out element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadBoolean(JsonElement source, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(source, name, out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }

        return false;
    }

    private static bool TryReadInt64(JsonElement source, string primary, string secondary, string tertiary, out long value)
    {
        foreach (var name in new[] { primary, secondary, tertiary })
        {
            if (!TryGetPropertyIgnoreCase(source, name, out var element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value) && value > 0)
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.String
                && long.TryParse(element.GetString(), out value)
                && value > 0)
            {
                return true;
            }
        }

        value = 0;
        return false;
    }
}
