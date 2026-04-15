using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Apple;

public sealed class AppleExternalToolRunner
{
    private static readonly string[] Mp4BoxAliases = ["mp4box"];
    private static readonly char[] LineSeparators = ['\r', '\n'];
    private static readonly string[] WindowsPathExtensions = ["", ".exe", ".cmd", ".bat"];
    private static readonly string[] DefaultPathExtensions = [""];

    private readonly ILogger<AppleExternalToolRunner> _logger;

    public AppleExternalToolRunner(ILogger<AppleExternalToolRunner> logger)
    {
        _logger = logger;
    }

    public static bool HasMp4Decrypt() => HasTool("mp4decrypt", Array.Empty<string>(), "DEEZSPOTAG_APPLE_MP4DECRYPT_PATH");

    public static bool HasMp4Box() => HasTool("MP4Box", Mp4BoxAliases, "DEEZSPOTAG_APPLE_MP4BOX_PATH");

    public async Task<bool> RunMp4DecryptAsync(string key, string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        var toolPath = ResolveToolPath("mp4decrypt", Array.Empty<string>(), "DEEZSPOTAG_APPLE_MP4DECRYPT_PATH");
        if (toolPath == null)
        {
            _logger.LogWarning("mp4decrypt executable not found.");
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            WorkingDirectory = Directory.Exists(outputDir) ? outputDir! : Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--key");
        startInfo.ArgumentList.Add(key);
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputReadTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorReadTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        await outputReadTask;
        var stderr = await errorReadTask;

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("mp4decrypt failed: {Error}", stderr);
            return false;
        }

        return true;
    }

    public async Task<bool> RunMp4BoxMuxAsync(string videoPath, string audioPath, string outputPath, IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || string.IsNullOrWhiteSpace(audioPath) || string.IsNullOrWhiteSpace(outputPath))
        {
            return false;
        }

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        var toolPath = ResolveToolPath("MP4Box", Mp4BoxAliases, "DEEZSPOTAG_APPLE_MP4BOX_PATH");
        if (toolPath == null)
        {
            _logger.LogWarning("MP4Box executable not found.");
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            WorkingDirectory = Directory.Exists(outputDir) ? outputDir! : Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (tags.Count > 0)
        {
            var tagString = string.Join(":", tags);
            startInfo.ArgumentList.Add("-itags");
            startInfo.ArgumentList.Add(tagString);
        }

        startInfo.ArgumentList.Add("-quiet");
        startInfo.ArgumentList.Add("-add");
        startInfo.ArgumentList.Add(videoPath);
        startInfo.ArgumentList.Add("-add");
        startInfo.ArgumentList.Add(audioPath);
        startInfo.ArgumentList.Add("-keep-utc");
        startInfo.ArgumentList.Add("-new");
        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("MP4Box mux failed: {Error}", stderr);
            return false;
        }

        return true;
    }

    public static async Task<bool> HasAudioTrackAsync(string mediaPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
        {
            return false;
        }

        var ffprobeResult = await TryRunToolAsync("ffprobe", Array.Empty<string>(), null, cancellationToken, "-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", mediaPath);
        if (ffprobeResult.Success)
        {
            return ffprobeResult.Output
                .Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(line => !string.IsNullOrWhiteSpace(line));
        }

        if (!HasMp4Box())
        {
            return false;
        }

        var mp4boxResult = await TryRunToolAsync("MP4Box", Mp4BoxAliases, "DEEZSPOTAG_APPLE_MP4BOX_PATH", cancellationToken, "-info", mediaPath);
        if (!mp4boxResult.Success)
        {
            return false;
        }

        return mp4boxResult.Output.Contains("Type \"soun", StringComparison.OrdinalIgnoreCase)
            || mp4boxResult.Output.Contains("Media Type: soun:", StringComparison.OrdinalIgnoreCase)
            || mp4boxResult.Output.Contains("Audio Info", StringComparison.OrdinalIgnoreCase)
            || mp4boxResult.Output.Contains("MPEG-4 Audio", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(bool Success, string Output)> TryRunToolAsync(
        string toolName,
        IReadOnlyList<string> aliases,
        string? explicitPathEnvVar,
        CancellationToken cancellationToken,
        params string[] args)
    {
        var resolvedToolPath = ResolveToolPath(toolName, aliases, explicitPathEnvVar);
        if (resolvedToolPath == null)
        {
            return (false, string.Empty);
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = resolvedToolPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var output = string.Join(
                Environment.NewLine,
                new[] { stdout, stderr }.Where(part => !string.IsNullOrWhiteSpace(part)));
            return (process.ExitCode == 0, output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, string.Empty);
        }
    }

    private static bool HasTool(string toolName, IReadOnlyList<string> aliases, string? explicitPathEnvVar)
    {
        var resolved = ResolveToolPath(toolName, aliases, explicitPathEnvVar);
        return !string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved);
    }

    private static string? ResolveToolPath(
        string primaryToolName,
        IReadOnlyList<string> aliases,
        string? explicitPathEnvVar = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPathEnvVar))
        {
            var explicitPath = Environment.GetEnvironmentVariable(explicitPathEnvVar);
            if (IsExecutableFile(explicitPath))
            {
                return explicitPath;
            }
        }

        var allNames = new[] { primaryToolName }
            .Concat(aliases ?? Array.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var root = AppContext.BaseDirectory;
        for (var i = 0; i < 6; i++)
        {
            var toolsBin = Path.Join(root, "tools", "bin");
            foreach (var name in allNames)
            {
                if (TryResolveInDirectory(toolsBin, name, out var localPath))
                {
                    return localPath;
                }
            }

            root = Path.GetDirectoryName(root) ?? root;
        }

        var fromPath = ResolveFromSystemPath(allNames);
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return fromPath;
        }

        return ResolveFromExtraToolDirs(allNames);
    }

    private static string? ResolveFromSystemPath(string[] allNames)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var extensions = OperatingSystem.IsWindows()
            ? WindowsPathExtensions
            : DefaultPathExtensions;

        return path.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(dir =>
                allNames.SelectMany(toolName =>
                    extensions.Select(ext => Path.Join(dir, toolName + ext))))
            .FirstOrDefault(IsExecutableFile);
    }

    private static string? ResolveFromExtraToolDirs(string[] toolNames)
    {
        var additionalDirs = EnumerateAdditionalToolDirs().Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in additionalDirs)
        {
            foreach (var name in toolNames)
            {
                if (TryResolveInDirectory(dir, name, out var resolved))
                {
                    return resolved;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAdditionalToolDirs()
    {
        var configured = Environment.GetEnvironmentVariable("DEEZSPOTAG_TOOL_DIRS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var separator = OperatingSystem.IsWindows() ? ';' : ':';
            foreach (var dir in configured.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return dir;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Join(home, "bin");
            yield return Path.Join(home, ".local", "bin");
            yield return Path.Join(home, "tools", "bin");

            // Mirror common GUI launcher layouts where tools live under <something>/tools/bin.
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(home).ToArray();
            }
            catch (Exception)
            {
                yield break;
            }

            foreach (var child in children)
            {
                yield return Path.Join(child, "tools", "bin");
            }
        }
    }

    private static bool TryResolveInDirectory(string directory, string toolName, out string? resolvedPath)
    {
        resolvedPath = null;
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        if (!Directory.Exists(directory))
        {
            return false;
        }

        var candidates = OperatingSystem.IsWindows()
            ? new[] { Path.Join(directory, toolName), Path.Join(directory, toolName + ".exe") }
            : new[] { Path.Join(directory, toolName) };
        var firstExecutable = candidates.FirstOrDefault(IsExecutableFile);
        if (!string.IsNullOrWhiteSpace(firstExecutable))
        {
            resolvedPath = firstExecutable;
            return true;
        }

        return false;
    }

    private static bool IsExecutableFile(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }
}
