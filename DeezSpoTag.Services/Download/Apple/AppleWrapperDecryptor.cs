using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Apple;

public sealed class AppleWrapperDecryptor
{
    private readonly ILogger<AppleWrapperDecryptor> _logger;

    public AppleWrapperDecryptor(ILogger<AppleWrapperDecryptor> logger)
    {
        _logger = logger;
    }

    public async Task<bool> TryDecryptAsync(
        string playlistUrl,
        string outputPath,
        string adamId,
        string decryptPort,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlistUrl)
            || string.IsNullOrWhiteSpace(outputPath)
            || string.IsNullOrWhiteSpace(adamId)
            || string.IsNullOrWhiteSpace(decryptPort))
        {
            return false;
        }

        var toolPath = ResolveToolPath();
        if (string.IsNullOrWhiteSpace(toolPath) || !File.Exists(toolPath))
        {
            toolPath = await TryBuildHelperAsync(cancellationToken) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(toolPath) || !File.Exists(toolPath))
            {
                _logger.LogWarning("Apple wrapper helper not found. Set APPLE_WRAPPER_RUNV2 to the helper path.");
                return false;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? string.Empty);

        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            WorkingDirectory = Path.GetDirectoryName(toolPath) ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--adam-id");
        startInfo.ArgumentList.Add(adamId);
        startInfo.ArgumentList.Add("--playlist-url");
        startInfo.ArgumentList.Add(playlistUrl);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add("--decrypt-port");
        startInfo.ArgumentList.Add(decryptPort);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("Apple wrapper decrypt failed: {Error}", stderr);
            return false;
        }

        _logger.LogInformation("Apple wrapper decrypt helper completed successfully using {ToolPath}.", toolPath);
        return File.Exists(outputPath);
    }

    private static string ResolveToolPath()
    {
        var envPath = Environment.GetEnvironmentVariable("APPLE_WRAPPER_RUNV2");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return envPath;
        }

        var exeName = OperatingSystem.IsWindows() ? "apple-wrapper-runv2.exe" : "apple-wrapper-runv2";
        var baseCandidates = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            ResolveRepoRoot()
        };

        foreach (var baseDir in baseCandidates)
        {
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                continue;
            }

            var toolPath = Path.Join(baseDir, "Tools", "AppleMusicWrapper", "runv2", exeName);
            if (File.Exists(toolPath))
            {
                return toolPath;
            }
        }

        return string.Empty;
    }

    private async Task<string?> TryBuildHelperAsync(CancellationToken cancellationToken)
    {
        var repoRoot = ResolveRepoRoot();
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return null;
        }

        var runv2Dir = Path.Join(repoRoot, "Tools", "AppleMusicWrapper", "runv2");
        if (!Directory.Exists(runv2Dir))
        {
            return null;
        }

        var exeName = OperatingSystem.IsWindows() ? "apple-wrapper-runv2.exe" : "apple-wrapper-runv2";
        var outputPath = Path.Join(runv2Dir, exeName);
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        var goExecutable = ResolveGoExecutablePath();
        if (string.IsNullOrWhiteSpace(goExecutable))
        {
            _logger.LogWarning("Go executable not found. Set DEEZSPOTAG_GO_PATH to build apple-wrapper-runv2.");
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = goExecutable,
            WorkingDirectory = runv2Dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add(".");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Failed to build apple-wrapper-runv2: {Error}", stderr);
                return null;
            }

            return File.Exists(outputPath) ? outputPath : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to run go build for apple-wrapper-runv2.");
            return null;
        }
    }

    private static string? ResolveRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 8; i++)
        {
            var parent = Directory.GetParent(dir);
            if (parent is null)
            {
                break;
            }

            var candidate = parent.FullName;
            if (File.Exists(Path.Join(candidate, "src.sln")) ||
                Directory.Exists(Path.Join(candidate, "Tools")))
            {
                return candidate;
            }

            dir = parent.FullName;
        }

        return null;
    }

    private static string? ResolveGoExecutablePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("DEEZSPOTAG_GO_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullExplicitPath = Path.GetFullPath(explicitPath);
            if (File.Exists(fullExplicitPath))
            {
                return fullExplicitPath;
            }
        }

        var candidates = OperatingSystem.IsWindows()
            ? new[] { "C:\\Program Files\\Go\\bin\\go.exe" }
            : new[] { "/usr/bin/go", "/usr/local/go/bin/go" };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }
}
