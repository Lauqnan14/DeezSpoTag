using System.Diagnostics;
using DeezSpoTag.Services.Download.Shared.Utils;

namespace DeezSpoTag.Web.Services;

internal static class YtDlpPlaylistJsonFetcher
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(45);

    public static async Task<string?> FetchAsync(
        string playlistUrl,
        string sourceLabel,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var ytDlpPath = ResolveYtDlpPath();
        if (string.IsNullOrWhiteSpace(ytDlpPath))
        {
            logger.LogWarning(
                "yt-dlp is not available in a trusted fixed path. Skipping {SourceLabel} playlist fetch for {PlaylistUrl}.",
                sourceLabel,
                playlistUrl);
            return null;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add("--flat-playlist");
        process.StartInfo.ArgumentList.Add("--dump-single-json");
        process.StartInfo.ArgumentList.Add("--no-warnings");
        process.StartInfo.ArgumentList.Add("--no-call-home");
        process.StartInfo.ArgumentList.Add(playlistUrl);

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FetchTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            logger.LogWarning(ex, "yt-dlp timed out while fetching {SourceLabel} playlist {PlaylistUrl}.", sourceLabel, playlistUrl);
            return null;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            logger.LogWarning(
                "yt-dlp failed for {PlaylistUrl}. exitCode={ExitCode} stderr={StdErr}",
                playlistUrl,
                process.ExitCode,
                TrimStdErr(stderr));
            return null;
        }

        return string.IsNullOrWhiteSpace(stdout) ? null : stdout;
    }

    private static string? ResolveYtDlpPath()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DEEZSPOTAG_YT_DLP_PATH"),
            Environment.GetEnvironmentVariable("YT_DLP_PATH"),
            "/usr/bin/yt-dlp",
            "/usr/local/bin/yt-dlp",
            "/bin/yt-dlp"
        };

        foreach (var candidate in candidates)
        {
            if (TryResolveTrustedExecutable(candidate, out var resolvedPath))
            {
                return resolvedPath;
            }
        }

        return null;
    }

    private static bool TryResolveTrustedExecutable(string? candidate, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var normalizedPath = candidate.Trim();
        if (!Path.IsPathRooted(normalizedPath) || !File.Exists(normalizedPath))
        {
            return false;
        }

        if (!ExternalToolResolver.TryExecuteVersion(normalizedPath))
        {
            return false;
        }

        resolvedPath = normalizedPath;
        return true;
    }

    private static void TryKillProcess(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static string TrimStdErr(string? stderr)
    {
        var value = (stderr ?? string.Empty).Trim();
        return value.Length <= 400 ? value : value[..400];
    }
}
