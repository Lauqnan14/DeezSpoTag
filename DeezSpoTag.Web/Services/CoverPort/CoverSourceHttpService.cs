using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeezSpoTag.Web.Services.CoverPort;

public sealed class CoverSourceHttpService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CoverSourceHttpService> _logger;
    private readonly string _cacheRoot;
    private readonly ConcurrentDictionary<CoverSourceName, SourceThrottle> _sourceThrottles = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _cacheLocks = new(StringComparer.Ordinal);

    public CoverSourceHttpService(
        IHttpClientFactory httpClientFactory,
        IWebHostEnvironment environment,
        ILogger<CoverSourceHttpService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cacheRoot = Path.Join(AppDataPaths.GetDataRoot(environment), "cover-port", "http-cache");
        Directory.CreateDirectory(_cacheRoot);
    }

    public async Task<JsonDocument?> GetJsonDocumentAsync(
        CoverSourceName source,
        string url,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var bytes = await GetBytesAsync(
            source,
            url,
            CoverSourcePolicies.GetJsonCacheTtl(source),
            kind: "json",
            headers,
            cancellationToken);
        if (bytes == null || bytes.Length == 0)
        {
            return null;
        }

        return JsonDocument.Parse(bytes);
    }

    public async Task<byte[]?> GetImageBytesAsync(
        CoverSourceName source,
        string url,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        return await GetBytesAsync(
            source,
            url,
            CoverSourcePolicies.GetImageCacheTtl(source),
            kind: "image",
            headers,
            cancellationToken);
    }

    public async Task<bool> ProbeUrlExistsAsync(
        CoverSourceName source,
        string url,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var path = ResolveCachePath(source, url, "head", headers);
        var pathLock = _cacheLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await pathLock.WaitAsync(cancellationToken);
        try
        {
            var ttl = TimeSpan.FromDays(7);
            var cached = await TryReadFreshCacheAsync(path, ttl, cancellationToken);
            if (cached != null && cached.Length > 0)
            {
                return cached[0] == (byte)'1';
            }

            var stale = await TryReadStaleCacheAsync(path, cancellationToken);
            try
            {
                await ApplySourceRateLimitAsync(source, cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                request.Headers.TryAddWithoutValidation("User-Agent", "DeezSpoTag/1.0 (+https://github.com/edzoh)");
                if (headers != null)
                {
                    foreach (var pair in headers)
                    {
                        request.Headers.Remove(pair.Key);
                        request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                    }
                }

                using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
                var ok = response.IsSuccessStatusCode;
                await WriteCacheAsync(path, new[] { ok ? (byte)'1' : (byte)'0' }, cancellationToken);
                return ok;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                if (stale != null && stale.Length > 0)
                {
                    return stale[0] == (byte)'1';
                }

                return false;
            }
        }
        finally
        {
            pathLock.Release();
        }
    }

    private async Task<byte[]?> GetBytesAsync(
        CoverSourceName source,
        string url,
        TimeSpan ttl,
        string kind,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        var path = ResolveCachePath(source, url, kind, headers);
        var pathLock = _cacheLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await pathLock.WaitAsync(cancellationToken);
        try
        {
            var cached = await TryReadFreshCacheAsync(path, ttl, cancellationToken);
            if (cached != null)
            {
                return cached;
            }

            var stale = await TryReadStaleCacheAsync(path, cancellationToken);
            try
            {
                await ApplySourceRateLimitAsync(source, cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", "DeezSpoTag/1.0 (+https://github.com/edzoh)");
                if (headers != null)
                {
                    foreach (var pair in headers)
                    {
                        request.Headers.Remove(pair.Key);
                        request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                    }
                }
                using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Cover source request failed ({StatusCode}) for {Url}", response.StatusCode, url);
                    return stale;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                await WriteCacheAsync(path, bytes, cancellationToken);
                return bytes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Cover source request failed for {Url}", url);
                return stale;
            }
        }
        finally
        {
            pathLock.Release();
        }
    }

    private static async Task<byte[]?> TryReadFreshCacheAsync(string path, TimeSpan ttl, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path);
        if (age > ttl)
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private static async Task<byte[]?> TryReadStaleCacheAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private static async Task WriteCacheAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.tmp";
        await File.WriteAllBytesAsync(tempPath, data, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    private string ResolveCachePath(
        CoverSourceName source,
        string url,
        string kind,
        IReadOnlyDictionary<string, string>? headers)
    {
        var headerSignature = string.Empty;
        if (headers != null && headers.Count > 0)
        {
            headerSignature = string.Join(
                '|',
                headers
                    .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kvp => $"{kvp.Key}={kvp.Value}"));
        }

        var hash = ComputeHash($"{source}:{kind}:{url}:{headerSignature}");
        var sourceDir = Path.Join(_cacheRoot, source.ToString().ToLowerInvariant(), kind);
        return Path.Join(sourceDir, $"{hash}.cache");
    }

    private static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    private async Task ApplySourceRateLimitAsync(CoverSourceName source, CancellationToken cancellationToken)
    {
        var throttle = _sourceThrottles.GetOrAdd(source, _ => new SourceThrottle());
        await throttle.Gate.WaitAsync(cancellationToken);
        try
        {
            var minInterval = CoverSourcePolicies.GetMinInterval(source);
            var elapsed = DateTimeOffset.UtcNow - throttle.LastRequestUtc;
            var remaining = minInterval - elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken);
            }

            throttle.LastRequestUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            throttle.Gate.Release();
        }
    }

    private sealed class SourceThrottle
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset LastRequestUtc { get; set; } = DateTimeOffset.MinValue;
    }
}
