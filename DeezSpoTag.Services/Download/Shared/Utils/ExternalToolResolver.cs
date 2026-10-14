using System.Diagnostics;

namespace DeezSpoTag.Services.Download.Shared.Utils;

public static class ExternalToolResolver
{
    public static string? ResolveFfmpegPath() => ResolveExecutablePath("ffmpeg");

    public static string? ResolveFfprobePath() => ResolveExecutablePath("ffprobe");

    public static string? ResolveExecutablePath(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        var envName = executableName.ToUpperInvariant();
        var configuredPath = Environment.GetEnvironmentVariable($"DEEZSPOTAG_{envName}_PATH");
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = Environment.GetEnvironmentVariable($"{envName}_PATH");
        }

        if (!string.IsNullOrWhiteSpace(configuredPath) && TryExecuteVersion(configuredPath))
        {
            return configuredPath;
        }

        var candidates = OperatingSystem.IsWindows()
            ? new[] { $"{executableName}.exe", executableName }
            : new[] { $"/usr/bin/{executableName}", $"/usr/local/bin/{executableName}", $"/bin/{executableName}", executableName };

        return candidates.FirstOrDefault(TryExecuteVersion);
    }

    public static bool TryExecuteVersion(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-version" }
            });

            if (process is null) return false;
            // Drain both pipes while waiting: a verbose version banner can otherwise fill
            // either pipe and prevent a valid executable from exiting.
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
