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

}
