using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DeezSpoTag.Services.Download.Shared.Utils;

namespace DeezSpoTag.Web.Services;

public sealed class SpectrogramService
{
    private const int MinWidth = 320;
    private const int MaxWidth = 4096;
    private const int MinHeight = 180;
    private const int MaxHeight = 2160;
    private const int MinSeconds = 10;
    private const int MaxSeconds = 600;
    private const int DefaultWidth = 1400;
    private const int DefaultHeight = 720;
    private const int DefaultSeconds = 120;
    private const int MaxCacheFiles = 500;
    private const long MaxCacheBytes = 2L * 1024 * 1024 * 1024;

    private readonly ILogger<SpectrogramService> _logger;
    private readonly string _cacheRoot;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private static readonly Lazy<string?> FfmpegPath = new(ResolveFfmpegPath);

    public SpectrogramService(ILogger<SpectrogramService> logger, IWebHostEnvironment environment)
    {
        _logger = logger;
        var dataRoot = AppDataPaths.GetDataRoot(environment);
        _cacheRoot = Path.Join(dataRoot, "analysis", "spectrogram-cache");
        Directory.CreateDirectory(_cacheRoot);
    }

    public static SpectrogramRequest NormalizeRequest(int? width, int? height, int? seconds)
    {
        var normalizedWidth = Math.Clamp(width ?? DefaultWidth, MinWidth, MaxWidth);
        var normalizedHeight = Math.Clamp(height ?? DefaultHeight, MinHeight, MaxHeight);
        var normalizedSeconds = Math.Clamp(seconds ?? DefaultSeconds, MinSeconds, MaxSeconds);
        return new SpectrogramRequest(normalizedWidth, normalizedHeight, normalizedSeconds);
    }

    public async Task<SpectrogramResult?> GetOrCreateAsync(
        string filePath,
        SpectrogramRequest request,
        bool forceRegenerate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return SpectrogramResult.Failed("Track file is missing.");
        }

        var ffmpeg = FfmpegPath.Value;
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            _logger.LogWarning("Spectrogram generation skipped for {FilePath}: ffmpeg not available.", filePath);
            return SpectrogramResult.Failed("ffmpeg executable was not found.");
        }

        var fileInfo = new FileInfo(filePath);
        var cacheName = BuildCacheName(fileInfo, request);
        var outputPath = Path.Join(_cacheRoot, $"{cacheName}.png");
        if (!forceRegenerate && File.Exists(outputPath))
        {
            TouchFile(outputPath);
            return SpectrogramResult.Succeeded(outputPath, false);
        }

        var tmpPath = Path.Join(_cacheRoot, $"{cacheName}.{Guid.NewGuid():N}.tmp.png");
        try
        {
            var generationError = await GenerateSpectrogramAsync(ffmpeg, filePath, tmpPath, request, cancellationToken);
            if (!string.IsNullOrWhiteSpace(generationError))
            {
                return SpectrogramResult.Failed(generationError);
            }

            if (!File.Exists(tmpPath))
            {
                return SpectrogramResult.Failed("ffmpeg finished but no spectrogram image was produced.");
            }

            await _cacheLock.WaitAsync(cancellationToken);
            try
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                File.Move(tmpPath, outputPath);
                TouchFile(outputPath);
                EnforceCacheBounds();
            }
            finally
            {
                _cacheLock.Release();
            }

            return SpectrogramResult.Succeeded(outputPath, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to generate spectrogram for {FilePath}", filePath);
            return SpectrogramResult.Failed(ex.Message);
        }
        finally
        {
            TryDelete(tmpPath);
        }
    }

    private static string BuildCacheName(FileInfo fileInfo, SpectrogramRequest request)
    {
        var normalizedPath = Path.GetFullPath(fileInfo.FullName).Replace('\\', '/');
        var material = $"{normalizedPath}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}|{request.Width}|{request.Height}|{request.Seconds}";
        var bytes = Encoding.UTF8.GetBytes(material);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<string?> GenerateSpectrogramAsync(
        string ffmpegPath,
        string inputPath,
        string outputPath,
        SpectrogramRequest request,
        CancellationToken cancellationToken)
    {
        var filter = $"[0:a:0]showspectrumpic=s={request.Width}x{request.Height}:legend=enabled:mode=combined:color=intensity:scale=log:fscale=log[spec]";
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(request.Seconds.ToString());
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("-filter_complex");
        startInfo.ArgumentList.Add(filter);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("[spec]");
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return "Failed to start ffmpeg process.";
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode == 0)
        {
            return null;
        }

        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        _logger.LogWarning("ffmpeg spectrogram failed for {InputPath}: {Error}", inputPath, stderr);
        return string.IsNullOrWhiteSpace(stderr)
            ? "ffmpeg failed to generate a spectrogram image."
            : stderr.Trim();
    }

    private void EnforceCacheBounds()
    {
        Directory.CreateDirectory(_cacheRoot);
        var files = new DirectoryInfo(_cacheRoot)
            .GetFiles("*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();

        var totalBytes = files.Sum(file => file.Length);
        while (files.Count > MaxCacheFiles || totalBytes > MaxCacheBytes)
        {
            var target = files[0];
            files.RemoveAt(0);
            totalBytes -= target.Length;
            TryDelete(target.FullName);
        }
    }

    private static void TouchFile(string filePath)
    {
        try
        {
            File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            // Best effort only.
        }
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            // Best effort only.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            // Best effort only.
        }
    }

    private static string? ResolveFfmpegPath() => ExternalToolResolver.ResolveFfmpegPath();

}

public sealed record SpectrogramRequest(int Width, int Height, int Seconds);

public sealed record SpectrogramResult(string? FilePath, bool GeneratedNow, string? ErrorMessage)
{
    public bool Success => !string.IsNullOrWhiteSpace(FilePath);

    public static SpectrogramResult Succeeded(string filePath, bool generatedNow) =>
        new(filePath, generatedNow, null);

    public static SpectrogramResult Failed(string message) =>
        new(null, false, message);
}
