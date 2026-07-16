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

        var outputIo = DownloadPathResolver.ResolveIoPath(displayOutput);
        var dir = Path.GetDirectoryName(outputIo);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            var baseName = Path.GetFileNameWithoutExtension(outputIo);
            foreach (var ext in new[] { ".ttml", ".lrc", ".txt" })
            {
                var lyricIo = Path.Join(dir, baseName + ext);
                if (!File.Exists(lyricIo))
                {
                    continue;
                }

                var displayLyric = DownloadPathResolver.NormalizeDisplayPath(lyricIo);
                files.Add(new Dictionary<string, object>
                {
                    ["path"] = displayLyric,
                    ["albumPath"] = albumPath,
                    ["artistPath"] = artistPath
                });

            }
        }
        return files;
    }

}
