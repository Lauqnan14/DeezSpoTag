using System.Runtime.InteropServices;

namespace DeezSpoTag.Web.Services;

internal static class FfmpegPathResolver
{
    public static string? ResolveExecutable()
    {
        var configuredPath = Environment.GetEnvironmentVariable("DEEZSPOTAG_FFMPEG_PATH");
        if (IsUsableExecutable(configuredPath))
        {
            return Path.GetFullPath(configuredPath!);
        }

        return GetCandidates().FirstOrDefault(IsUsableExecutable);
    }

    private static IEnumerable<string> GetCandidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return
            [
                Path.Combine(programFiles, "ffmpeg", "bin", "ffmpeg.exe"),
                Path.Combine(programFilesX86, "ffmpeg", "bin", "ffmpeg.exe"),
                @"C:\ffmpeg\bin\ffmpeg.exe"
            ];
        }

        return
        [
            "/usr/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/bin/ffmpeg"
        ];
    }

    private static bool IsUsableExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
