using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Shared.Utils;
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

    public static (List<Dictionary<string, object>> Files, string LyricsStatus) BuildAudioFiles(
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
        var hasTtml = false;
        var hasLrc = false;
        var hasTxt = false;
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

                switch (ext)
                {
                    case ".ttml":
                        hasTtml = true;
                        break;
                    case ".lrc":
                        hasLrc = true;
                        break;
                    case ".txt":
                        hasTxt = true;
                        break;
                }
            }
        }

        var status = new List<string>();
        if (hasTtml)
        {
            status.Add("time-synced");
        }

        if (hasLrc)
        {
            status.Add("synced");
        }

        if (hasTxt)
        {
            status.Add("unsynced");
        }

        return (files, string.Join(",", status));
    }
}
