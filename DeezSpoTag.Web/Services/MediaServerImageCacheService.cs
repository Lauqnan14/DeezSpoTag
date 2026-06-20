using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DeezSpoTag.Web.Services;

public sealed record MediaServerImageResult(byte[]? Bytes, string ContentType, HttpStatusCode StatusCode)
{
    public bool Success => Bytes is { Length: > 0 } && StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices;
}

public sealed class MediaServerImageCacheService
{
    private const string OctetStreamContentType = "application/octet-stream";
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(125);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MediaServerImageCacheService> _logger;
    private readonly string _cacheRoot;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _cacheLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _upstreamGate = new(2, 2);
    private readonly SemaphoreSlim _requestIntervalGate = new(1, 1);
    private DateTimeOffset _lastRequestUtc = DateTimeOffset.MinValue;

    public MediaServerImageCacheService(
        IHttpClientFactory httpClientFactory,
        IWebHostEnvironment environment,
        ILogger<MediaServerImageCacheService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cacheRoot = Path.Join(AppDataPaths.GetDataRoot(environment), "media-server", "image-cache");
        Directory.CreateDirectory(_cacheRoot);
    }

    public async Task<MediaServerImageResult> GetAsync(
        string serverType,
        string sourcePath,
        string targetUrl,
        CancellationToken cancellationToken)
    {
        var cachePath = ResolveCachePath(serverType, sourcePath);
        var cacheLock = _cacheLocks.GetOrAdd(cachePath, static _ => new SemaphoreSlim(1, 1));
        await cacheLock.WaitAsync(cancellationToken);
        try
        {
            var cached = await TryReadCacheAsync(cachePath, requireFresh: true, cancellationToken);
            if (cached != null)
            {
                return cached;
            }

            var stale = await TryReadCacheAsync(cachePath, requireFresh: false, cancellationToken);
            try
            {
                var fetched = await FetchWithRateLimitRetryAsync(targetUrl, cancellationToken);
                if (fetched.Success)
                {
                    await WriteCacheAsync(cachePath, fetched.Bytes!, cancellationToken);
                    return fetched;
                }

                return stale ?? fetched;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Media-server image request failed for {ServerType}.", serverType);
                }

                return stale ?? new MediaServerImageResult(null, OctetStreamContentType, HttpStatusCode.BadGateway);
            }
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private async Task<MediaServerImageResult> FetchWithRateLimitRetryAsync(
        string targetUrl,
        CancellationToken cancellationToken)
    {
        await _upstreamGate.WaitAsync(cancellationToken);
        try
        {
            MediaServerImageResult? lastResult = null;
            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                await WaitForRequestSlotAsync(cancellationToken);
                using var response = await _httpClientFactory.CreateClient().GetAsync(targetUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    return new MediaServerImageResult(
                        bytes,
                        ResolveContentType(bytes, response.Content.Headers.ContentType?.MediaType),
                        response.StatusCode);
                }

                lastResult = new MediaServerImageResult(null, OctetStreamContentType, response.StatusCode);
                if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == MaximumAttempts)
                {
                    return lastResult;
                }

                var retryDelay = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromMilliseconds(500 * attempt);
                await Task.Delay(ClampRetryDelay(retryDelay), cancellationToken);
            }

            return lastResult ?? new MediaServerImageResult(null, OctetStreamContentType, HttpStatusCode.BadGateway);
        }
        finally
        {
            _upstreamGate.Release();
        }
    }

    private async Task WaitForRequestSlotAsync(CancellationToken cancellationToken)
    {
        await _requestIntervalGate.WaitAsync(cancellationToken);
        try
        {
            var remaining = MinimumRequestInterval - (DateTimeOffset.UtcNow - _lastRequestUtc);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken);
            }
            _lastRequestUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _requestIntervalGate.Release();
        }
    }

    private async Task<MediaServerImageResult?> TryReadCacheAsync(
        string cachePath,
        bool requireFresh,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        var info = new FileInfo(cachePath);
        if (info.Length <= 0 || (requireFresh && DateTimeOffset.UtcNow - info.LastWriteTimeUtc > CacheLifetime))
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(cachePath, cancellationToken);
            return new MediaServerImageResult(bytes, ResolveContentType(bytes, null), HttpStatusCode.OK);
        }
        catch (IOException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed reading media-server image cache entry {CachePath}.", cachePath);
            }

            return null;
        }
    }

    private static async Task WriteCacheAsync(string cachePath, byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(cachePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string ResolveCachePath(string serverType, string sourcePath)
    {
        var key = $"{serverType.Trim().ToLowerInvariant()}\n{sourcePath.Trim()}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Join(_cacheRoot, hash[..2], $"{hash}.image");
    }

    private static TimeSpan ClampRetryDelay(TimeSpan value)
    {
        if (value < TimeSpan.FromMilliseconds(250))
        {
            return TimeSpan.FromMilliseconds(250);
        }
        return value > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : value;
    }

    private static string ResolveContentType(byte[] bytes, string? declaredContentType)
    {
        if (!string.IsNullOrWhiteSpace(declaredContentType)
            && declaredContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return declaredContentType;
        }
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }
        if (bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP")
        {
            return "image/webp";
        }
        if (bytes.Length >= 6 && Encoding.ASCII.GetString(bytes, 0, 3) == "GIF")
        {
            return "image/gif";
        }
        return OctetStreamContentType;
    }
}
