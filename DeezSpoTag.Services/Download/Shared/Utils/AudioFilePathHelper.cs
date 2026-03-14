using IOFile = System.IO.File;

namespace DeezSpoTag.Services.Download.Shared.Utils;

internal static class AudioFilePathHelper
{
    private const string DefaultAudioExtension = ".flac";

    public sealed class AudioPathContext
    {
        public required string OutputDir { get; init; }
        public required string Title { get; init; }
        public required string Artist { get; init; }
        public required string Album { get; init; }
        public required string AlbumArtist { get; init; }
        public required string ReleaseDate { get; init; }
        public required int TrackNumber { get; init; }
        public required int DiscNumber { get; init; }
        public required string FilenameFormat { get; init; }
        public required bool IncludeTrackNumber { get; init; }
        public required int Position { get; init; }
        public required bool UseAlbumTrackNumber { get; init; }
        public required Func<string, string> Sanitize { get; init; }
    }

    public static bool TryFindExistingByIsrc(string outputDir, string isrc, out string path, params string[] extensions)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(outputDir) || string.IsNullOrWhiteSpace(isrc))
        {
            return false;
        }

        var normalizedExtensions = extensions.Length > 0 ? extensions : [DefaultAudioExtension];

        try
        {
            foreach (var extension in normalizedExtensions)
            {
                var pattern = $"*{NormalizeAudioExtension(extension, DefaultAudioExtension)}";
                var files = Directory.GetFiles(outputDir, pattern, SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    try
                    {
                        using var tagFile = TagLib.File.Create(file);
                        if (!string.IsNullOrWhiteSpace(tagFile.Tag.ISRC)
                            && string.Equals(tagFile.Tag.ISRC, isrc, StringComparison.OrdinalIgnoreCase))
                        {
                            path = file;
                            return true;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        System.Diagnostics.Trace.TraceWarning(
                            "Failed to inspect ISRC for '{0}': {1}",
                            file,
                            ex.Message);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        return false;
    }

    public static string BuildFilename(AudioPathContext context, string extension = DefaultAudioExtension)
    {
        if (context.FilenameFormat.StartsWith("literal:", StringComparison.OrdinalIgnoreCase))
        {
            var literal = context.FilenameFormat["literal:".Length..];
            return $"{context.Sanitize(literal)}{NormalizeAudioExtension(extension, DefaultAudioExtension)}";
        }

        var numberToUse = context.UseAlbumTrackNumber && context.TrackNumber > 0 ? context.TrackNumber : context.Position;
        var year = context.ReleaseDate.Length >= 4 ? context.ReleaseDate[..4] : string.Empty;
        var filename = context.FilenameFormat;

        if (context.FilenameFormat.Contains('{'))
        {
            filename = filename.Replace("{title}", context.Sanitize(context.Title), StringComparison.OrdinalIgnoreCase)
                .Replace("{artist}", context.Sanitize(context.Artist), StringComparison.OrdinalIgnoreCase)
                .Replace("{album}", context.Sanitize(context.Album), StringComparison.OrdinalIgnoreCase)
                .Replace("{album_artist}", context.Sanitize(context.AlbumArtist), StringComparison.OrdinalIgnoreCase)
                .Replace("{year}", year, StringComparison.OrdinalIgnoreCase)
                .Replace("{disc}", context.DiscNumber > 0 ? context.DiscNumber.ToString("D2") : string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{track}", numberToUse > 0 ? numberToUse.ToString("D2") : string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            filename = context.FilenameFormat switch
            {
                "artist-title" => $"{context.Sanitize(context.Artist)} - {context.Sanitize(context.Title)}",
                "title" => context.Sanitize(context.Title),
                _ => $"{context.Sanitize(context.Title)} - {context.Sanitize(context.Artist)}"
            };

            if (context.IncludeTrackNumber && numberToUse > 0)
            {
                filename = $"{numberToUse:D2}. {filename}";
            }
        }

        return $"{filename}{NormalizeAudioExtension(extension, DefaultAudioExtension)}";
    }

    public static string BuildOutputPath(AudioPathContext context, string extension = DefaultAudioExtension)
    {
        return Path.Join(
            context.OutputDir,
            BuildFilename(context, extension));
    }

    public static string NormalizeAudioExtension(string extension, string fallback)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return fallback;
        }

        if (!extension.StartsWith('.'))
        {
            extension = $".{extension}";
        }

        return extension.ToLowerInvariant() switch
        {
            ".m4a" => ".m4a",
            ".mp4" => ".m4a",
            ".flac" => ".flac",
            _ => fallback
        };
    }

    public static List<string> BuildExpectedPaths(AudioPathContext context, params string[] extensions)
    {
        var paths = new List<string>();
        if (string.IsNullOrWhiteSpace(context.Title) || string.IsNullOrWhiteSpace(context.Artist))
        {
            return paths;
        }

        var normalizedExtensions = extensions.Length > 0 ? extensions : [DefaultAudioExtension];
        foreach (var extension in normalizedExtensions)
        {
            paths.Add(BuildOutputPath(context, extension));
        }

        return paths;
    }

    public static void EnsureIsrcMatchOrThrow(string filePath, string isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return;
        }

        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var actualIsrc = tagFile.Tag.ISRC;
            if (string.IsNullOrWhiteSpace(actualIsrc))
            {
                throw new InvalidOperationException($"ISRC mismatch: missing ISRC on downloaded file ({isrc})");
            }

            if (!string.Equals(actualIsrc, isrc, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"ISRC mismatch: expected {isrc} but found {actualIsrc}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                if (IOFile.Exists(filePath))
                {
                    IOFile.Delete(filePath);
                }
            }
            catch (Exception cleanupEx) when (cleanupEx is not OperationCanceledException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Failed to cleanup file after ISRC mismatch '{0}': {1}",
                    filePath,
                    cleanupEx.Message);
            }

            throw;
        }
    }
}
