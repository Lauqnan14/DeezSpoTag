using System.Text.Json.Nodes;
using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;

namespace DeezSpoTag.Services.Download.Queue;

public static class QueuePayloadFileHelper
{
    public static List<Dictionary<string, object>> BuildSingleOutputFile(string outputPath)
    {
        var displayOutput = DownloadPathResolver.NormalizeDisplayPath(outputPath);
        var albumPath = DownloadPathResolver.NormalizeDisplayPath(Path.GetDirectoryName(outputPath) ?? outputPath);
        var artistPath = DownloadPathResolver.NormalizeDisplayPath(Path.GetDirectoryName(albumPath) ?? albumPath);

        return new List<Dictionary<string, object>>
        {
            new()
            {
                ["path"] = displayOutput,
                ["albumPath"] = albumPath,
                ["artistPath"] = artistPath
            }
        };
    }

    public static List<Dictionary<string, object>> BuildAudioFiles(
        PathGenerationResult pathResult,
        string outputPath)
    {
        var displayOutput = DownloadPathResolver.NormalizeDisplayPath(outputPath);
        var albumPath = DownloadPathResolver.NormalizeDisplayPath(pathResult.FilePath);
        var artistPath = DownloadPathResolver.NormalizeDisplayPath(pathResult.ArtistPath ?? pathResult.FilePath);

        var files = new List<Dictionary<string, object>>
        {
            new()
            {
                ["path"] = displayOutput,
                ["albumPath"] = albumPath,
                ["artistPath"] = artistPath
            }
        };

        return files;
    }

    public static void AddLyricsArtifactFiles(
        List<Dictionary<string, object>> files,
        LyricsArtifactState lyricsArtifacts,
        PathGenerationResult pathResult)
    {
        foreach (var path in lyricsArtifacts.FilesByFormat.Values
                     .Where(static path => !string.IsNullOrWhiteSpace(path))
                     .Select(DownloadPathResolver.NormalizeDisplayPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (files.Any(file => file.TryGetValue("path", out var existing)
                                  && string.Equals(
                                      DownloadPathResolver.NormalizeDisplayPath(existing?.ToString() ?? string.Empty),
                                      path,
                                      StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            files.Add(new Dictionary<string, object>
            {
                ["path"] = path,
                ["albumPath"] = DownloadPathResolver.NormalizeDisplayPath(pathResult.FilePath),
                ["artistPath"] = DownloadPathResolver.NormalizeDisplayPath(pathResult.ArtistPath ?? pathResult.FilePath)
            });
        }
    }

    public static bool TryRewriteDestinationFolderId(
        string payloadJson,
        long? destinationFolderId,
        out string updatedPayloadJson)
    {
        updatedPayloadJson = payloadJson;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(payloadJson) as JsonObject;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        if (root is null || !TrySetNullableInt64Property(root, "destinationFolderId", destinationFolderId))
        {
            return false;
        }

        updatedPayloadJson = root.ToJsonString();
        return true;
    }

    private static bool TrySetNullableInt64Property(JsonObject root, string propertyName, long? value)
    {
        var key = root.Select(property => property.Key)
                      .FirstOrDefault(existing => string.Equals(existing, propertyName, StringComparison.OrdinalIgnoreCase))
                  ?? propertyName;
        root.TryGetPropertyValue(key, out var existingNode);
        var existingValue = TryReadNullableInt64(existingNode);
        if (existingValue == value)
        {
            return false;
        }

        root[key] = value.HasValue ? JsonValue.Create(value.Value) : null;
        return true;
    }

    private static long? TryReadNullableInt64(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var number))
            {
                return number;
            }

            if (value.TryGetValue<string>(out var text)
                && long.TryParse(text, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

}
