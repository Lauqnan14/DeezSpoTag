using DeezSpoTag.Services.Library;
using System.Buffers;
using System.Security.Cryptography;

namespace DeezSpoTag.Web.Services;

public sealed class PlaylistVisualService
{
    private static readonly TimeSpan VisualMaterializationTimeout = TimeSpan.FromSeconds(10);
    private static readonly SearchValues<char> QueryOrFragmentDelimiters = SearchValues.Create("?#");
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<PlaylistVisualService> _logger;

    public PlaylistVisualService(
        IHttpClientFactory httpClientFactory,
        IWebHostEnvironment environment,
        ILogger<PlaylistVisualService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _environment = environment;
        _logger = logger;
    }

    public async Task<string?> ResolveManagedVisualUrlAsync(
        string source,
        string sourceId,
        string? playlistName,
        string? remoteUrl,
        bool reuseSavedArtwork,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (!ShouldManageVisual(source, playlistName))
        {
            return remoteUrl;
        }

        var existing = GetStoredVisual(source, sourceId);
        var existingUrl = TryBuildExistingVisualUrl(source, sourceId, existing);
        if (!forceRefresh && !string.IsNullOrWhiteSpace(existingUrl))
        {
            return existingUrl;
        }

        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return existingUrl;
        }

        return await MaterializeVisualAsync(
            source,
            sourceId,
            remoteUrl,
            reuseSavedArtwork,
            existingUrl,
            cancellationToken);
    }

    private async Task<string?> MaterializeVisualAsync(
        string source,
        string sourceId,
        string remoteUrl,
        bool reuseSavedArtwork,
        string? existingUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryNormalizeRemoteImageUri(remoteUrl, out var normalizedRemoteUri))
            {
                return ResolveUnmaterializedVisualUrl(remoteUrl, reuseSavedArtwork, existingUrl);
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(VisualMaterializationTimeout);
            var materializationToken = timeoutSource.Token;
            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(normalizedRemoteUri, materializationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ResolveUnmaterializedVisualUrl(remoteUrl, reuseSavedArtwork, existingUrl);
            }

            var visualDir = GetVisualDirectory(source, sourceId);
            Directory.CreateDirectory(visualDir);

            var bytes = await response.Content.ReadAsByteArrayAsync(materializationToken);
            if (bytes.Length == 0)
            {
                return ResolveUnmaterializedVisualUrl(remoteUrl, reuseSavedArtwork, existingUrl);
            }

            var extension = ResolveImageExtension(response.Content.Headers.ContentType?.MediaType, normalizedRemoteUri);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var fileName = $"cover-{hash}{extension}";
            var targetPath = Path.Join(visualDir, fileName);
            if (!File.Exists(targetPath))
            {
                await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken);
            }

            if (!reuseSavedArtwork)
            {
                SetActiveVisual(source, sourceId, fileName);
            }

            return BuildVisualUrl(source, sourceId, File.GetLastWriteTimeUtc(targetPath));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Timed out while materializing playlist visual for {Source}:{SourceId}", source, sourceId);
            return ResolveUnmaterializedVisualUrl(remoteUrl, reuseSavedArtwork, existingUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to materialize playlist visual for {Source}:{SourceId}", source, sourceId);
            return ResolveUnmaterializedVisualUrl(remoteUrl, reuseSavedArtwork, existingUrl);
        }
    }

    public StoredPlaylistVisual? GetStoredVisual(string source, string sourceId)
    {
        var visualDir = GetVisualDirectory(source, sourceId);
        if (!Directory.Exists(visualDir))
        {
            return null;
        }

        var activeFileName = ReadActiveFileName(visualDir);
        var file = !string.IsNullOrWhiteSpace(activeFileName)
            ? Path.Join(visualDir, activeFileName)
            : EnumerateVisualFiles(visualDir)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        if (!File.Exists(file))
        {
            return null;
        }

        return new StoredPlaylistVisual(
            file,
            ResolveContentType(file),
            true,
            BuildVisualVariantUrl(source, sourceId, Path.GetFileName(file), File.GetLastWriteTimeUtc(file)));
    }

    public StoredPlaylistVisual? GetStoredVisualFromManagedUrl(string source, string sourceId, string? managedUrl)
    {
        if (!IsManagedVisualUrl(managedUrl))
        {
            return null;
        }

        var fileName = TryExtractManagedVisualFileName(managedUrl);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return GetStoredVisual(source, sourceId);
        }

        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            return null;
        }

        var visualDir = GetVisualDirectory(source, sourceId);
        var filePath = Path.Join(visualDir, fileName);
        if (!File.Exists(filePath))
        {
            return null;
        }

        var activeFileName = ResolveActiveFileName(visualDir);
        return new StoredPlaylistVisual(
            filePath,
            ResolveContentType(filePath),
            string.Equals(fileName, activeFileName, StringComparison.OrdinalIgnoreCase),
            BuildVisualVariantUrl(source, sourceId, fileName, File.GetLastWriteTimeUtc(filePath)));
    }

    public IReadOnlyList<StoredPlaylistVisual> GetStoredVisuals(string source, string sourceId)
    {
        var visualDir = GetVisualDirectory(source, sourceId);
        if (!Directory.Exists(visualDir))
        {
            return Array.Empty<StoredPlaylistVisual>();
        }

        var activeFileName = ResolveActiveFileName(visualDir);
        return EnumerateVisualFiles(visualDir)
            .OrderByDescending(path => string.Equals(Path.GetFileName(path), activeFileName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(path => File.GetLastWriteTimeUtc(path))
            .Select(path => new StoredPlaylistVisual(
                path,
                ResolveContentType(path),
                string.Equals(Path.GetFileName(path), activeFileName, StringComparison.OrdinalIgnoreCase),
                BuildVisualVariantUrl(source, sourceId, Path.GetFileName(path), File.GetLastWriteTimeUtc(path))))
            .ToList();
    }

    public void DeleteStoredVisuals(string source, string sourceId)
    {
        var visualDir = GetVisualDirectory(source, sourceId);
        if (!Directory.Exists(visualDir))
        {
            return;
        }

        try
        {
            Directory.Delete(visualDir, recursive: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to remove playlist visuals for {Source}:{SourceId}", source, sourceId);
        }
    }

    public async Task<string?> StoreUploadedVisualAsync(
        string source,
        string sourceId,
        byte[] bytes,
        string? contentType,
        CancellationToken cancellationToken)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        var visualDir = GetVisualDirectory(source, sourceId);
        Directory.CreateDirectory(visualDir);

        var extension = ResolveImageExtension(contentType, null);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var fileName = $"cover-{hash}{extension}";
        var targetPath = Path.Join(visualDir, fileName);
        if (!File.Exists(targetPath))
        {
            await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken);
        }

        SetActiveVisual(source, sourceId, fileName);
        return BuildVisualUrl(source, sourceId, File.GetLastWriteTimeUtc(targetPath));
    }

    public static bool IsManagedVisualUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("/api/library/playlists/", StringComparison.OrdinalIgnoreCase)
            && value.Contains("/visual", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateVisualFiles(string visualDir)
    {
        return Directory.EnumerateFiles(visualDir, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.StartsWith("cover-", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("cover.jpg", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("cover.jpeg", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("cover.png", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("cover.webp", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("cover.gif", StringComparison.OrdinalIgnoreCase);
            });
    }

    public bool SetActiveVisual(string source, string sourceId, string fileName)
    {
        var visualDir = GetVisualDirectory(source, sourceId);
        if (!Directory.Exists(visualDir))
        {
            return false;
        }

        var targetPath = Path.Join(visualDir, fileName);
        if (!File.Exists(targetPath))
        {
            return false;
        }

        File.WriteAllText(Path.Join(visualDir, "active.txt"), fileName);
        return true;
    }

    private string GetVisualDirectory(string source, string sourceId)
    {
        var dataRoot = AppDataPaths.GetDataRoot(_environment);
        return Path.Join(
            dataRoot,
            "playlist-visuals",
            SanitizeSegment(source),
            SanitizeSegment(sourceId));
    }

    private static bool ShouldManageVisual(string source, string? playlistName)
    {
        if (string.Equals(source, LibraryRecommendationService.RecommendationSource, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedName = (playlistName ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedName.Contains("top songs", StringComparison.Ordinal)
            || normalizedName.Contains("trending songs", StringComparison.Ordinal)
            || normalizedName.Contains("trending song", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static string BuildVisualUrl(string source, string sourceId, DateTime lastWriteUtc)
    {
        var version = new DateTimeOffset(lastWriteUtc).ToUnixTimeSeconds();
        return $"/api/library/playlists/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(sourceId)}/visual?v={version}";
    }

    private static string BuildVisualVariantUrl(string source, string sourceId, string fileName, DateTime lastWriteUtc)
    {
        var version = new DateTimeOffset(lastWriteUtc).ToUnixTimeSeconds();
        return $"/api/library/playlists/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(sourceId)}/visual?file={Uri.EscapeDataString(fileName)}&v={version}";
    }

    private static string? ReadActiveFileName(string visualDir)
    {
        var markerPath = Path.Join(visualDir, "active.txt");
        if (!File.Exists(markerPath))
        {
            return null;
        }

        var value = (File.ReadAllText(markerPath) ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? ResolveActiveFileName(string visualDir)
    {
        var activeFileName = ReadActiveFileName(visualDir);
        if (!string.IsNullOrWhiteSpace(activeFileName)
            && File.Exists(Path.Join(visualDir, activeFileName)))
        {
            return activeFileName;
        }

        var latest = EnumerateVisualFiles(visualDir)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(latest) ? null : Path.GetFileName(latest);
    }

    private static string? ResolveUnmaterializedVisualUrl(
        string remoteUrl,
        bool reuseSavedArtwork,
        string? existingUrl)
    {
        if (reuseSavedArtwork && !string.IsNullOrWhiteSpace(existingUrl))
        {
            return existingUrl;
        }

        return remoteUrl;
    }

    private static string? TryBuildExistingVisualUrl(string source, string sourceId, StoredPlaylistVisual? existing)
    {
        if (existing == null || !File.Exists(existing.FilePath))
        {
            return null;
        }

        return BuildVisualUrl(source, sourceId, File.GetLastWriteTimeUtc(existing.FilePath));
    }

    private static string? TryExtractManagedVisualFileName(string? managedUrl)
    {
        if (string.IsNullOrWhiteSpace(managedUrl))
        {
            return null;
        }

        var queryIndex = managedUrl.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0 || queryIndex == managedUrl.Length - 1)
        {
            return null;
        }

        foreach (var pair in managedUrl[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);
            var key = separatorIndex >= 0 ? pair[..separatorIndex] : pair;
            if (!string.Equals(Uri.UnescapeDataString(key), "file", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = separatorIndex >= 0 ? pair[(separatorIndex + 1)..] : string.Empty;
            return string.IsNullOrWhiteSpace(value) ? null : Uri.UnescapeDataString(value);
        }

        return null;
    }

    private static string ResolveImageExtension(string? mediaType, string? url)
    {
        var normalized = (mediaType ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Contains("png", StringComparison.Ordinal))
        {
            return ".png";
        }

        if (normalized.Contains("webp", StringComparison.Ordinal))
        {
            return ".webp";
        }

        if (normalized.Contains("gif", StringComparison.Ordinal))
        {
            return ".gif";
        }

        var normalizedUrl = url ?? string.Empty;
        var queryOrFragmentIndex = normalizedUrl.AsSpan().IndexOfAny(QueryOrFragmentDelimiters);
        var pathOnlyUrl = queryOrFragmentIndex >= 0
            ? normalizedUrl[..queryOrFragmentIndex]
            : normalizedUrl;
        var ext = Path.GetExtension(pathOnlyUrl);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
                ? ext.ToLowerInvariant()
                : ".jpg";
    }

    private static string ResolveContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg"
        };
    }

    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Trim()
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray();
        var sanitized = new string(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    public sealed record StoredPlaylistVisual(string FilePath, string ContentType, bool IsActive = false, string? Url = null);

    private static bool TryNormalizeRemoteImageUri(string value, out string normalizedUri)
    {
        normalizedUri = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalizedUri = uri.AbsoluteUri;
        return true;
    }
}
