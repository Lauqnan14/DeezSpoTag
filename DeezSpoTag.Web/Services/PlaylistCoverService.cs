using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DeezSpoTag.Web.Services;

public sealed class PlaylistCoverService
{
    private const string OutputFileName = "background.jpg";
    private const string MetaFileName = "meta.json";
    private const string SourceFileName = "source";
    private const string DefaultAspectRatio = "16:9";
    private const int DefaultQuality = 85;
    private const string CacheVersion = "v3";
    private const int SaliencySampleLimit = 96;
    private const string JpegContentType = "image/jpeg";

    private readonly HttpClient _httpClient;
    private readonly ILogger<PlaylistCoverService> _logger;
    private readonly string _cacheRoot;
    private static int _startupStatusLogged;

    public PlaylistCoverService(
        IWebHostEnvironment environment,
        HttpClient httpClient,
        ILogger<PlaylistCoverService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _cacheRoot = Path.Join(AppDataPaths.GetDataRoot(environment), "playlist-covers");
        Directory.CreateDirectory(_cacheRoot);
    }

    public async Task<PlaylistCoverResult?> GetBackgroundAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var sourceUri) || !IsAllowedScheme(sourceUri))
        {
            _logger.LogWarning("Rejected playlist cover URL with invalid scheme: {Url}", url);
            return null;
        }

        if (!await IsAllowedRemoteUriAsync(sourceUri, cancellationToken))
        {
            _logger.LogWarning("Rejected playlist cover URL by remote host policy: {Url}", sourceUri);
            return null;
        }

        var normalizedUrl = sourceUri.ToString();

        var cacheKey = ComputeSha256($"{CacheVersion}:{normalizedUrl}");
        var cacheDir = Path.Join(_cacheRoot, cacheKey);
        Directory.CreateDirectory(cacheDir);

        var outputPath = Path.Join(cacheDir, OutputFileName);
        var metaPath = Path.Join(cacheDir, MetaFileName);

        var cachedMeta = await ReadMetaAsync(metaPath, cancellationToken);
        if (File.Exists(outputPath))
        {
            var cached = await TryUseCachedAsync(normalizedUrl, cachedMeta, outputPath, cancellationToken);
            if (cached != null)
            {
                return cached;
            }
        }

        var downloadResult = await DownloadSourceAsync(normalizedUrl, cacheDir, cancellationToken);
        if (downloadResult == null)
        {
            return File.Exists(outputPath) ? new PlaylistCoverResult(outputPath, JpegContentType) : null;
        }

        var backgroundPath = await TryCropAsync(downloadResult.LocalPath, outputPath, cancellationToken);
        if (backgroundPath == null)
        {
            try
            {
                File.Copy(downloadResult.LocalPath, outputPath, true);
                backgroundPath = outputPath;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to store playlist background fallback for Url.");
            }
        }

        await WriteMetaAsync(metaPath, downloadResult, cancellationToken);

        return backgroundPath != null
            ? new PlaylistCoverResult(backgroundPath, JpegContentType)
            : null;
    }

    public static Task<PlaylistCoverPipelineStatus> GetPipelineStatusAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<PlaylistCoverPipelineStatus>(cancellationToken);
        }

        return Task.FromResult(new PlaylistCoverPipelineStatus(
            Available: true,
            ToolFound: true,
            ModelFound: true,
            FallbackActive: false,
            ToolPath: "in-process-csharp",
            ModelPath: "embedded-saliency"));
    }

    public async Task LogStartupStatusAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _startupStatusLogged, 1) == 1)
        {
            return;
        }

        var status = await GetPipelineStatusAsync(cancellationToken);
        _logger.LogInformation(
            "Playlist cover content-aware crop enabled (mode={Mode}, model={Model}).",
            status.ToolPath,
            status.ModelPath);
    }

    private async Task<PlaylistCoverResult?> TryUseCachedAsync(
        string url,
        PlaylistCoverMeta? cachedMeta,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var head = await TryHeadAsync(url, cancellationToken);
        if (head == null)
        {
            return new PlaylistCoverResult(outputPath, JpegContentType);
        }

        if (cachedMeta != null && cachedMeta.Matches(head))
        {
            return new PlaylistCoverResult(outputPath, JpegContentType);
        }

        if (cachedMeta != null && !head.ShouldRefresh())
        {
            return new PlaylistCoverResult(outputPath, JpegContentType);
        }

        return null;
    }

    private async Task<PlaylistCoverMeta?> ReadMetaAsync(string metaPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(metaPath);
            return await JsonSerializer.DeserializeAsync<PlaylistCoverMeta>(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to read playlist cover metadata at {Path}.", metaPath);
            return null;
        }
    }

    private async Task WriteMetaAsync(string metaPath, DownloadResult download, CancellationToken cancellationToken)
    {
        var meta = new PlaylistCoverMeta
        {
            Url = download.Url,
            ETag = download.ETag,
            LastModified = download.LastModified,
            ContentLength = download.ContentLength,
            ContentType = download.ContentType
        };

        try
        {
            await using var stream = File.Create(metaPath);
            await JsonSerializer.SerializeAsync(stream, meta, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to write playlist cover metadata at {Path}.", metaPath);
        }
    }

    private async Task<DownloadResult?> DownloadSourceAsync(string url, string cacheDir, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to download playlist cover Url (Status): {Url}", url);
                return null;
            }

            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri != null && !await IsAllowedRemoteUriAsync(finalUri, cancellationToken))
            {
                _logger.LogWarning("Rejected redirected playlist cover URL by remote host policy: {Url}", finalUri);
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? JpegContentType;
            var extension = GetExtension(contentType, url);
            var sourcePath = Path.Join(cacheDir, $"{SourceFileName}{extension}");

            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = File.Create(sourcePath))
            {
                await stream.CopyToAsync(fileStream, cancellationToken);
            }

            return new DownloadResult
            {
                Url = url,
                LocalPath = sourcePath,
                ETag = response.Headers.ETag?.Tag,
                LastModified = response.Content.Headers.LastModified?.ToString(),
                ContentLength = response.Content.Headers.ContentLength,
                ContentType = contentType
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to download playlist cover Url.");
            return null;
        }
    }

    private async Task<HeadResult?> TryHeadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri != null && !await IsAllowedRemoteUriAsync(finalUri, cancellationToken))
            {
                _logger.LogWarning("Rejected redirected playlist cover HEAD URL by remote host policy: {Url}", finalUri);
                return null;
            }

            return new HeadResult
            {
                ETag = response.Headers.ETag?.Tag,
                LastModified = response.Content.Headers.LastModified?.ToString(),
                ContentLength = response.Content.Headers.ContentLength,
                ContentType = response.Content.Headers.ContentType?.MediaType
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to query playlist cover HEAD metadata for {Url}.", url);
            return null;
        }
    }

    private async Task<string?> TryCropAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var sourceStream = File.OpenRead(inputPath);
            using var image = await Image.LoadAsync<Rgba32>(sourceStream, cancellationToken);
            if (image.Width <= 0 || image.Height <= 0)
            {
                return null;
            }

            var aspect = ParseAspectRatio(DefaultAspectRatio);
            var cropRect = ComputeContentAwareCrop(image, aspect);
            if (cropRect.Width <= 0 || cropRect.Height <= 0)
            {
                return null;
            }

            image.Mutate(ctx => ctx.Crop(cropRect));

            await using var outputStream = File.Create(outputPath);
            var encoder = new JpegEncoder { Quality = DefaultQuality };
            await image.SaveAsJpegAsync(outputStream, encoder, cancellationToken);

            return File.Exists(outputPath) ? outputPath : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "In-process C# content-aware crop failed.");
            return null;
        }
    }

    private static Rectangle ComputeContentAwareCrop(Image<Rgba32> image, double aspectRatio)
    {
        var sourceWidth = image.Width;
        var sourceHeight = image.Height;

        if (aspectRatio <= 0)
        {
            aspectRatio = sourceWidth / (double)Math.Max(1, sourceHeight);
        }

        var targetWidth = sourceWidth;
        var targetHeight = (int)Math.Round(targetWidth / aspectRatio);
        if (targetHeight > sourceHeight)
        {
            targetHeight = sourceHeight;
            targetWidth = (int)Math.Round(targetHeight * aspectRatio);
        }

        targetWidth = Math.Clamp(targetWidth, 1, sourceWidth);
        targetHeight = Math.Clamp(targetHeight, 1, sourceHeight);

        if (targetWidth == sourceWidth && targetHeight == sourceHeight)
        {
            return new Rectangle(0, 0, sourceWidth, sourceHeight);
        }

        var sampledWidth = Math.Min(SaliencySampleLimit, sourceWidth);
        var sampledHeight = Math.Max(1, (int)Math.Round(sourceHeight * (sampledWidth / (double)sourceWidth)));
        if (sampledHeight > SaliencySampleLimit)
        {
            sampledHeight = SaliencySampleLimit;
            sampledWidth = Math.Max(1, (int)Math.Round(sourceWidth * (sampledHeight / (double)sourceHeight)));
        }

        using var sampled = image.Clone(ctx => ctx.Resize(sampledWidth, sampledHeight));
        var saliency = BuildSaliencyMap(sampled);

        var windowWidth = Math.Clamp((int)Math.Round(targetWidth * (sampledWidth / (double)sourceWidth)), 1, sampledWidth);
        var windowHeight = Math.Clamp((int)Math.Round(targetHeight * (sampledHeight / (double)sourceHeight)), 1, sampledHeight);

        var (bestX, bestY) = FindBestWindow(saliency, sampledWidth, sampledHeight, windowWidth, windowHeight);

        var focalX = (bestX + (windowWidth / 2.0)) * (sourceWidth / (double)sampledWidth);
        var focalY = (bestY + (windowHeight / 2.0)) * (sourceHeight / (double)sampledHeight);

        // Keep more artist faces in frame by nudging the crop slightly upward.
        focalY -= targetHeight * 0.08;

        var left = (int)Math.Round(focalX - targetWidth / 2.0);
        var top = (int)Math.Round(focalY - targetHeight / 2.0);

        if (left < 0) left = 0;
        if (top < 0) top = 0;
        if (left + targetWidth > sourceWidth) left = sourceWidth - targetWidth;
        if (top + targetHeight > sourceHeight) top = sourceHeight - targetHeight;

        left = Math.Clamp(left, 0, Math.Max(0, sourceWidth - targetWidth));
        top = Math.Clamp(top, 0, Math.Max(0, sourceHeight - targetHeight));

        return new Rectangle(left, top, targetWidth, targetHeight);
    }

    private static double[] BuildSaliencyMap(Image<Rgba32> sampled)
    {
        var width = sampled.Width;
        var height = sampled.Height;
        var luma = new double[width * height];
        var sat = new double[width * height];
        var map = new double[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = sampled[x, y];
                var idx = y * width + x;
                luma[idx] = (0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B) / 255.0;
                var max = Math.Max(p.R, Math.Max(p.G, p.B));
                var min = Math.Min(p.R, Math.Min(p.G, p.B));
                sat[idx] = (max - min) / 255.0;
            }
        }

        var centerX = (width - 1) / 2.0;
        var centerY = (height - 1) / 2.0;
        var maxDist = Math.Sqrt(centerX * centerX + centerY * centerY);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var idx = y * width + x;
                var left = luma[y * width + Math.Max(0, x - 1)];
                var right = luma[y * width + Math.Min(width - 1, x + 1)];
                var up = luma[Math.Max(0, y - 1) * width + x];
                var down = luma[Math.Min(height - 1, y + 1) * width + x];

                var gradient = Math.Abs(right - left) + Math.Abs(down - up);
                var colorInterest = sat[idx] * 0.45;
                var toneInterest = (1.0 - Math.Abs(luma[idx] - 0.55)) * 0.20;

                var dist = Math.Sqrt(Math.Pow(x - centerX, 2) + Math.Pow(y - centerY, 2));
                var centerBias = maxDist > 0 ? 1.0 - (dist / maxDist) * 0.35 : 1.0;

                var topBias = y < (height * 0.62) ? 1.08 : 0.95;

                map[idx] = (gradient * 0.75 + colorInterest + toneInterest) * centerBias * topBias;
            }
        }

        return map;
    }

    private static (int X, int Y) FindBestWindow(
        double[] saliency,
        int width,
        int height,
        int windowWidth,
        int windowHeight)
    {
        var integral = BuildIntegralImage(saliency, width, height);
        var stride = width + 1;

        var bestX = Math.Max(0, (width - windowWidth) / 2);
        var bestY = Math.Max(0, (height - windowHeight) / 2);
        var bestScore = double.MinValue;

        for (var y = 0; y <= height - windowHeight; y++)
        {
            for (var x = 0; x <= width - windowWidth; x++)
            {
                var score = SumRegion(integral, stride, x, y, windowWidth, windowHeight);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = x;
                    bestY = y;
                }
            }
        }

        return (bestX, bestY);
    }

    private static double[] BuildIntegralImage(double[] values, int width, int height)
    {
        var integral = new double[(width + 1) * (height + 1)];

        for (var y = 1; y <= height; y++)
        {
            double rowSum = 0;
            for (var x = 1; x <= width; x++)
            {
                rowSum += values[(y - 1) * width + (x - 1)];
                integral[y * (width + 1) + x] = integral[(y - 1) * (width + 1) + x] + rowSum;
            }
        }

        return integral;
    }

    private static double SumRegion(double[] integral, int stride, int x, int y, int regionWidth, int regionHeight)
    {
        var ix1 = x;
        var iy1 = y;
        var ix2 = x + regionWidth;
        var iy2 = y + regionHeight;

        return integral[iy2 * stride + ix2]
             - integral[iy1 * stride + ix2]
             - integral[iy2 * stride + ix1]
             + integral[iy1 * stride + ix1];
    }

    private static double ParseAspectRatio(string value)
    {
        var parts = value.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return 16d / 9d;
        }

        if (!double.TryParse(parts[0], out var w) || !double.TryParse(parts[1], out var h) || w <= 0 || h <= 0)
        {
            return 16d / 9d;
        }

        return w / h;
    }

    private static bool IsAllowedScheme(Uri uri)
    {
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsAllowedRemoteUriAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!IsAllowedScheme(uri))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.DnsSafeHost))
        {
            return false;
        }

        var host = uri.DnsSafeHost.Trim().TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IPAddress.TryParse(host, out var directIp))
        {
            return !IsBlockedAddress(directIp);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            if (addresses.Length == 0)
            {
                return false;
            }

            if (addresses.Any(IsBlockedAddress))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve host for playlist cover URL validation: {Host}", host);
            return false;
        }

        return true;
    }

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsBlockedAddress(address.MapToIPv4());
        }

        if (IsLoopbackOrWildcard(address))
        {
            return true;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsBlockedIpv4Address(address),
            AddressFamily.InterNetworkV6 => IsBlockedIpv6Address(address),
            _ => false
        };
    }

    private static bool IsLoopbackOrWildcard(IPAddress address)
    {
        return IPAddress.IsLoopback(address)
               || address.Equals(IPAddress.Any)
               || address.Equals(IPAddress.IPv6Any);
    }

    private static bool IsBlockedIpv4Address(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length < 2)
        {
            return true;
        }

        if (bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127)
        {
            return true;
        }

        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return true;
        }

        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        {
            return true;
        }

        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return true;
        }

        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
        {
            return true;
        }

        return bytes[0] >= 224;
    }

    private static bool IsBlockedIpv6Address(IPAddress address)
    {
        if (address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal
            || address.IsIPv6Teredo)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC;
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    private static string GetExtension(string contentType, string url)
    {
        if (contentType.Contains("png", StringComparison.OrdinalIgnoreCase))
        {
            return ".png";
        }

        if (contentType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) || contentType.Contains("jpg", StringComparison.OrdinalIgnoreCase))
        {
            return ".jpg";
        }

        var ext = Path.GetExtension(url);
        return string.IsNullOrWhiteSpace(ext) ? ".jpg" : ext;
    }

    private sealed class DownloadResult
    {
        public required string Url { get; init; }
        public required string LocalPath { get; init; }
        public string? ETag { get; init; }
        public string? LastModified { get; init; }
        public long? ContentLength { get; init; }
        public string? ContentType { get; init; }
    }

    private sealed class HeadResult
    {
        public string? ETag { get; init; }
        public string? LastModified { get; init; }
        public long? ContentLength { get; init; }
        public string? ContentType { get; init; }

        public bool ShouldRefresh()
        {
            return !string.IsNullOrWhiteSpace(ETag) || !string.IsNullOrWhiteSpace(LastModified) || ContentLength.HasValue;
        }
    }

    private sealed class PlaylistCoverMeta
    {
        public string? Url { get; init; }
        public string? ETag { get; init; }
        public string? LastModified { get; init; }
        public long? ContentLength { get; init; }
        public string? ContentType { get; init; }

        public bool Matches(HeadResult head)
        {
            if (!string.IsNullOrWhiteSpace(ETag) && !string.Equals(ETag, head.ETag, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(LastModified) && !string.Equals(LastModified, head.LastModified, StringComparison.Ordinal))
            {
                return false;
            }

            if (ContentLength.HasValue && head.ContentLength.HasValue && ContentLength.Value != head.ContentLength.Value)
            {
                return false;
            }

            return true;
        }
    }

    public sealed record PlaylistCoverPipelineStatus(
        bool Available,
        bool ToolFound,
        bool ModelFound,
        bool FallbackActive,
        string? ToolPath,
        string? ModelPath);

    public sealed record PlaylistCoverResult(string FilePath, string ContentType);
}
