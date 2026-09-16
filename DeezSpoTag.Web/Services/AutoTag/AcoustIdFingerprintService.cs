using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DeezSpoTag.Core.Security;

namespace DeezSpoTag.Web.Services.AutoTag;

/// <summary>
/// Runs the Chromaprint <c>fpcalc</c> binary to produce an AcoustID fingerprint for an
/// audio file — the same tool Picard uses. Resolution order: an explicitly configured
/// path, the DEEZSPOTAG_FPCALC_PATH environment variable, then well-known locations.
/// Returns null when fpcalc is unavailable or the fingerprint cannot be produced; every
/// caller must degrade gracefully.
/// </summary>
public sealed class AcoustIdFingerprintService
{
    private const string DefaultAnalysisLengthSeconds = "120";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(120);

    private readonly ILogger<AcoustIdFingerprintService> _logger;
    private volatile string? _resolvedPath;

    public AcoustIdFingerprintService(ILogger<AcoustIdFingerprintService> logger)
    {
        _logger = logger;
    }

    public sealed record AcoustIdFingerprint(int DurationSeconds, string Fingerprint);

    public async Task<AcoustIdFingerprint?> FingerprintAsync(string filePath, string? configuredPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        var fpcalcPath = ResolveExecutable(configuredPath);
        if (fpcalcPath == null)
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fpcalcPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-json");
            startInfo.ArgumentList.Add("-length");
            startInfo.ArgumentList.Add("120");
            startInfo.ArgumentList.Add(filePath);

            using var process = new Process { StartInfo = startInfo };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProcessTimeout);
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                _logger.LogWarning("fpcalc exited with code {ExitCode} for {FilePath}", process.ExitCode, LogSanitizer.OneLine(filePath));
                return null;
            }

            return ParseFingerprint(await outputTask, filePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "fpcalc fingerprint calculation failed for {FilePath}", LogSanitizer.OneLine(filePath));
            return null;
        }
    }

    public string? ResolveExecutable(string? configuredPath)
    {
        var resolved = _resolvedPath;
        if (resolved != null && File.Exists(resolved))
        {
            return resolved;
        }

        var candidate = ResolveExecutableUncached(configuredPath);
        if (candidate != null)
        {
            _resolvedPath = candidate;
        }

        return candidate;
    }

    private string? ResolveExecutableUncached(string? configuredPath)
    {
        if (IsUsableExecutable(configuredPath))
        {
            return Path.GetFullPath(configuredPath!);
        }

        var environmentPath = Environment.GetEnvironmentVariable("DEEZSPOTAG_FPCALC_PATH");
        if (IsUsableExecutable(environmentPath))
        {
            return Path.GetFullPath(environmentPath!);
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
                Path.Join(programFiles, "Chromaprint", "fpcalc.exe"),
                Path.Join(programFilesX86, "Chromaprint", "fpcalc.exe"),
                @"C:\chromaprint\bin\fpcalc.exe"
            ];
        }

        return
        [
            "/usr/bin/fpcalc",
            "/usr/local/bin/fpcalc",
            "/bin/fpcalc",
            "/opt/homebrew/bin/fpcalc"
        ];
    }

    private AcoustIdFingerprint? ParseFingerprint(string? output, string filePath)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var hasDuration = document.RootElement.TryGetProperty("duration", out var durationElement)
                && durationElement.TryGetInt32(out var parsedDuration);
            var fingerprint = document.RootElement.TryGetProperty("fingerprint", out var fingerprintElement)
                && fingerprintElement.ValueKind == JsonValueKind.String
                ? fingerprintElement.GetString()
                : null;
            if (!hasDuration || string.IsNullOrWhiteSpace(fingerprint))
            {
                _logger.LogWarning("fpcalc produced an unusable fingerprint document for {FilePath}", LogSanitizer.OneLine(filePath));
                return null;
            }

            return new AcoustIdFingerprint(durationElement.GetInt32(), fingerprint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to parse fpcalc output for {FilePath}", LogSanitizer.OneLine(filePath));
            return null;
        }
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
