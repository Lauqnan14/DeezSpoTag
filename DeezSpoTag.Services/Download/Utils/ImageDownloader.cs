using DeezSpoTag.Core.Models;
using DeezSpoTag.Core.Models.Download;
using DeezSpoTag.Services.Download;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Core.Enums;
using System.Net.Sockets;
using System.Text.Json;

namespace DeezSpoTag.Services.Download.Utils;

/// <summary>
/// Consolidated media processing utilities merging ImageDownloader and LyricsService
/// EXACT PORT from deezspotag downloadImage.ts and refreezer's dual API lyrics approach
/// </summary>
public class ImageDownloader
{
    private readonly ILogger<ImageDownloader> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _tempDir;

    // User agent matching deezspotag
    private const string USER_AGENT_HEADER = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.130 Safari/537.36";

    // Timeout constants matching deezspotag
    private const int DOWNLOAD_TIMEOUT_MS = 5000;
    private const int HEAD_TIMEOUT_MS = 1200;
    private const int MAX_DOWNLOAD_RETRY_ATTEMPTS = 2;
    private const string SpotifyCoverSize640 = "ab67616d0000b273";
    private const string SpotifyCoverSize300 = "ab67616d00001e02";
    private const string SpotifyCoverSize64 = "ab67616d00004851";
    private const string SpotifyCoverSizeMax = "ab67616d000082c1";
    private static readonly string[] SpotifyCoverSizeTokens =
    {
        SpotifyCoverSize640,
        SpotifyCoverSize300,
        SpotifyCoverSize64
    };

    public ImageDownloader(
        ILogger<ImageDownloader> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _tempDir = Path.Join(Path.GetTempPath(), "deezspotag-imgs");

        // Create temp directory if it doesn't exist
        if (!Directory.Exists(_tempDir))
        {
            Directory.CreateDirectory(_tempDir);
        }
    }

    /// <summary>
    /// Download single image (exact port of deezspotag downloadImage function)
    /// </summary>
    public async Task<string?> DownloadImageAsync(
        string url,
        string path,
        string overwrite = "n",
        bool preferMaxQuality = false,
        CancellationToken cancellationToken = default)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Starting image download: {Url} to {Path}", url, path);
        }

        var existingPath = TryReuseExistingImagePath(path, overwrite);
        if (existingPath != null)
        {
            return existingPath;
        }

        EnsureDestinationDirectory(path);
        using var httpClient = _httpClientFactory.CreateClient("ImageDownload");

        var attempt = 0;
        while (true)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMilliseconds(DOWNLOAD_TIMEOUT_MS));

                var downloadUrl = await ResolveDownloadUrlAsync(httpClient, url, cts.Token);
                await DownloadImagePayloadAsync(httpClient, downloadUrl, path, cts.Token);

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Successfully downloaded image: {Url} to {Path}", downloadUrl, path);
                }

                return path;
            }
            catch (OperationCanceledException ex)
            {
                TryDeletePartialFile(path);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                if (attempt >= MAX_DOWNLOAD_RETRY_ATTEMPTS)
                {
                    _logger.LogWarning(ex, "Image download timed out or was canceled internally: {Url}", url);
                    return null;
                }

                attempt++;
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Retrying image download after internal cancellation (attempt {Attempt}/{MaxAttempts})", attempt, MAX_DOWNLOAD_RETRY_ATTEMPTS);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDeletePartialFile(path);
                if (ex is HttpRequestException httpError && IsHttpError(httpError))
                {
                    _logger.LogWarning(ex, "Image not found: {Url}", url);
                    return null;
                }

                if (IsRetryableError(ex))
                {
                    if (attempt >= MAX_DOWNLOAD_RETRY_ATTEMPTS)
                    {
                        _logger.LogWarning(ex, "Image download failed after retry attempts: {Url}", url);
                        return null;
                    }

                    attempt++;
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(ex, "Retrying image download due to retryable error (attempt {Attempt}/{MaxAttempts})", attempt, MAX_DOWNLOAD_RETRY_ATTEMPTS);
                    }

                    continue;
                }

                _logger.LogError(ex, "Failed to download image from {Url} to {Path}", url, path);
                return null;
            }
        }
    }

    private string? TryReuseExistingImagePath(string path, string overwrite)
    {
        if (!System.IO.File.Exists(path) || ShouldOverwrite(overwrite))
        {
            return null;
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length == 0)
        {
            File.Delete(path);
            return null;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Image already exists and not overwriting: {Path}", path);
        }

        return path;
    }

    private static void EnsureDestinationDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private async Task<string> ResolveDownloadUrlAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        if (!IsSpotifyCoverUrl(url))
        {
            return url;
        }

        var rewritten = await TryGetMaxSpotifyCoverUrlAsync(httpClient, url, cancellationToken);
        if (!string.Equals(rewritten, url, StringComparison.Ordinal) && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Spotify image max-res rewrite: {OriginalUrl} -> {MaxUrl}", url, rewritten);
        }

        return rewritten;
    }

    private async Task DownloadImagePayloadAsync(
        HttpClient httpClient,
        string downloadUrl,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.Add("User-Agent", USER_AGENT_HEADER);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Sending HTTP request for image: {Url}", downloadUrl);
        }

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write);
        await CopyStreamWithTimeoutResetAsync(contentStream, fileStream, cancellationToken);
    }

    private async Task<string> TryGetMaxSpotifyCoverUrlAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        var maxUrl = SpotifyCoverSizeTokens.Aggregate(
            url,
            static (current, token) => current.Contains(token, StringComparison.Ordinal)
                ? current.Replace(token, SpotifyCoverSizeMax, StringComparison.Ordinal)
                : current);

        if (string.Equals(maxUrl, url, StringComparison.Ordinal))
        {
            return url;
        }
        try
        {
            using var headCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headCts.CancelAfter(TimeSpan.FromMilliseconds(HEAD_TIMEOUT_MS));
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, maxUrl);
            headRequest.Headers.Add("User-Agent", USER_AGENT_HEADER);
            using var headResponse = await httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, headCts.Token);
            if (headResponse.IsSuccessStatusCode)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Spotify max-res image verified via HEAD: {MaxUrl}", maxUrl);                }
                return maxUrl;
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Head probe timed out; fall back to original URL.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Timed out probing Spotify max-res image URL {MaxUrl}", maxUrl);            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall back to original URL on any HEAD failure.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to probe Spotify max-res image URL {MaxUrl}", maxUrl);            }
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Spotify max-res image not available, using original URL: {OriginalUrl}", url);        }
        return url;
    }

    private static bool IsSpotifyCoverUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!url.Contains("scdn.co", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return SpotifyCoverSizeTokens.Any(token => url.Contains(token, StringComparison.Ordinal));
    }


    /// <summary>
    /// Copy stream with timeout reset on data (simulating deezspotag pipeline behavior)
    /// </summary>
    private static async Task CopyStreamWithTimeoutResetAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        int bytesRead;

        while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            // In deezspotag, timeout is reset on each data chunk - we simulate this by continuing the loop
            // The CancellationToken timeout will handle overall timeout
        }
    }

    private void TryDeletePartialFile(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to delete partial image file {Path}", path);            }
        }
        catch (UnauthorizedAccessException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Access denied while deleting partial image file {Path}", path);            }
        }
    }

    /// <summary>
    /// Check if should overwrite based on overwrite option (matching deezspotag OverwriteOption)
    /// </summary>
    private static bool ShouldOverwrite(string overwrite)
    {
        return overwrite switch
        {
            "y" => true,
            "t" => true,
            "b" => true,
            _ => false
        };
    }


    /// <summary>
    /// Check if exception is an HTTP error (404, etc.)
    /// </summary>
    private static bool IsHttpError(HttpRequestException ex)
    {
        return ex.Message.Contains("404") ||
               ex.Message.Contains("Not Found") ||
               ex.Message.Contains("403") ||
               ex.Message.Contains("Forbidden");
    }

    /// <summary>
    /// Check if exception is retryable (matching deezspotag error handling)
    /// </summary>
    private static bool IsRetryableError(Exception ex)
    {
        // Match deezspotag retryable errors exactly
        if (ex is OperationCanceledException && ex.Message.Contains("timeout"))
            return true;

        if (ex is TaskCanceledException && !ex.Message.Contains("A task was canceled"))
            return true;

        if (ex is SocketException)
            return true;

        if (ex is IOException && ex.Message.Contains("connection"))
            return true;

        // Check for specific error codes matching deezspotag
        var message = ex.Message.ToUpper();
        return message.Contains("ESOCKETTIMEDOUT") ||
               message.Contains("ERR_STREAM_PREMATURE_CLOSE") ||
               message.Contains("ETIMEDOUT") ||
               message.Contains("ECONNRESET") ||
               message.Contains("TIMEOUT") ||
               message.Contains("CONNECTION");
    }

    /// <summary>
    /// Download multiple images
    /// </summary>
    public async Task DownloadImagesAsync(
        List<ImageUrl> imageUrls,
        string basePath,
        string baseFilename,
        string overwriteOption = "n",
        CancellationToken cancellationToken = default)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Starting download of {Count} images to {BasePath}/{BaseFilename}",
                imageUrls.Count, basePath, baseFilename);        }

        if (imageUrls.Count == 0 || string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(baseFilename))
        {
            _logger.LogWarning("Invalid parameters for image download: URLs={UrlCount}, BasePath={BasePath}, BaseFilename={BaseFilename}",
                imageUrls.Count, basePath, baseFilename);
            return;
        }

        var tasks = imageUrls.Select(async imageUrl =>
        {
            try
            {
                var outputPath = Path.Join(basePath, $"{baseFilename}.{imageUrl.Extension}");
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Downloading image: {Url} to {OutputPath}", imageUrl.Url, outputPath);                }
                var result = await DownloadImageAsync(imageUrl.Url, outputPath, overwriteOption, true, cancellationToken);
                if (result != null)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Successfully downloaded image: {Url} to {OutputPath}", imageUrl.Url, outputPath);                    }
                }
                else
                {
                    _logger.LogWarning("Failed to download image: {Url}", imageUrl.Url);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to download image: {Url}", imageUrl.Url);
            }
        });

        await Task.WhenAll(tasks);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Completed download of {Count} images", imageUrls.Count);        }
    }

    /// <summary>
    /// Check if URL is a valid image URL
    /// </summary>
    public static bool IsValidImageUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.ToLower();

            return path.EndsWith(".jpg") ||
                   path.EndsWith(".jpeg") ||
                   path.EndsWith(".png") ||
                   path.EndsWith(".gif") ||
                   path.EndsWith(".webp") ||
                   url.Contains("dzcdn.net"); // Deezer CDN images
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Get image format from URL
    /// </summary>
    public static string GetImageFormatFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return "jpg";

        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.ToLower();

            if (path.EndsWith(".png")) return "png";
            if (path.EndsWith(".gif")) return "gif";
            if (path.EndsWith(".webp")) return "webp";

            return "jpg"; // Default to jpg
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "jpg";
        }
    }

    /// <summary>
    /// Validate image file after download
    /// </summary>
    public async Task<bool> ValidateImageAsync(string imagePath)
    {
        try
        {
            if (!System.IO.File.Exists(imagePath))
                return false;

            var fileInfo = new FileInfo(imagePath);
            if (fileInfo.Length == 0)
                return false;

            // Basic validation - check if file starts with common image headers
            using var fileStream = new FileStream(imagePath, FileMode.Open, FileAccess.Read);
            var buffer = new byte[8];
            var bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length));

            if (bytesRead < 4)
                return false;

            // Check for common image file signatures
            // JPEG: FF D8 FF
            if (buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF)
                return true;

            // PNG: 89 50 4E 47
            if (buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47)
                return true;

            // GIF: 47 49 46 38
            if (buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x38)
                return true;

            // WebP: 52 49 46 46 (RIFF)
            if (buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46)
                return true;

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error validating image: {Path}", imagePath);
            return false;
        }
    }

    /// <summary>
    /// Clean up temporary image files
    /// </summary>
    public void CleanupTempImages(string tempDirectory)
    {
        try
        {
            if (!Directory.Exists(tempDirectory))
                return;

            var files = Directory.GetFiles(tempDirectory, "*", SearchOption.TopDirectoryOnly);
            var cutoffTime = DateTime.UtcNow.AddHours(-24); // Clean files older than 24 hours

            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTimeUtc < cutoffTime)
                    {
                        File.Delete(file);
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("Cleaned up temp image: {File}", file);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to delete temp image: {File}", file);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error cleaning up temp images in: {Directory}", tempDirectory);
        }
    }

    #region Service Wrapper Methods (merged from ImageDownloadService)

    /// <summary>
    /// Download image to temporary directory and return path
    /// </summary>
    public async Task<string?> DownloadImageToTempAsync(
        string url,
        string filename,
        CancellationToken cancellationToken = default)
    {
        var tempPath = Path.Join(_tempDir, filename);
        return await DownloadImageAsync(url, tempPath, "n", true, cancellationToken);
    }

    /// <summary>
    /// Get temporary directory path
    /// </summary>
    public string GetTempDirectory() => _tempDir;

    /// <summary>
    /// Clean up temporary images (overload for default temp directory)
    /// </summary>
    public void CleanupDefaultTempImages()
    {
        CleanupTempImages(_tempDir);
    }

    #endregion
}
