using System.Security.Cryptography;

namespace DeezSpoTag.Web.Services;

public sealed class ArtistVisualCacheService
{
    private const string LibraryArtistImagesPath = "library-artist-images";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ArtistVisualCacheService> _logger;

    public ArtistVisualCacheService(
        IHttpClientFactory httpClientFactory,
        IWebHostEnvironment environment,
        ILogger<ArtistVisualCacheService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _environment = environment;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CachedArtistVisual>> CacheAsync(
        long artistId,
        IReadOnlyList<ArtistVisualCacheCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (artistId <= 0 || candidates.Count == 0)
        {
            return Array.Empty<CachedArtistVisual>();
        }

        var results = new List<CachedArtistVisual>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cached = await CacheCandidateAsync(artistId, candidate, cancellationToken);
            if (cached is not null)
            {
                results.Add(cached);
            }
        }

        return results;
    }

    private async Task<CachedArtistVisual?> CacheCandidateAsync(
        long artistId,
        ArtistVisualCacheCandidate candidate,
        CancellationToken cancellationToken)
    {
        var source = NormalizeSource(candidate.Source);
        var label = string.IsNullOrWhiteSpace(candidate.Label) ? source : candidate.Label.Trim();
        var existingPath = TryNormalizeManagedPath(candidate.Path);
        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            return new CachedArtistVisual(source, label, BuildLibraryImageUrl(existingPath), existingPath, candidate.Identity);
        }

        var url = candidate.Url?.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var cacheDir = Path.Join(
            AppDataPaths.GetDataRoot(_environment),
            LibraryArtistImagesPath,
            source,
            "artists",
            artistId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(cacheDir);

        var baseFileName = ComputeHash(uri.AbsoluteUri);
        var existing = Directory.GetFiles(cacheDir, $"{baseFileName}.*", SearchOption.TopDirectoryOnly)
            .Where(File.Exists)
            .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return new CachedArtistVisual(source, label, BuildLibraryImageUrl(existing), Path.GetFullPath(existing), candidate.Identity);
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var extension = ImageFileExtensionResolver.ResolveStandardImageExtension(
                response.Content.Headers.ContentType?.MediaType,
                uri.AbsoluteUri);
            var targetPath = Path.Join(cacheDir, $"{baseFileName}{extension}");
            await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var targetStream = File.Create(targetPath);
            await sourceStream.CopyToAsync(targetStream, cancellationToken);
            return new CachedArtistVisual(source, label, BuildLibraryImageUrl(targetPath), Path.GetFullPath(targetPath), candidate.Identity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to cache artist visual for artist {ArtistId} from {Source}.", artistId, source);
            return null;
        }
    }

    private string? TryNormalizeManagedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var cacheRoot = Path.GetFullPath(Path.Join(AppDataPaths.GetDataRoot(_environment), LibraryArtistImagesPath));
            return File.Exists(fullPath) && IsPathWithinRoot(fullPath, cacheRoot) ? fullPath : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static string NormalizeSource(string? source)
    {
        var normalized = (source ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "spotify" or "apple" or "deezer" or "lastfm" or "qobuz" or "tidal" or "selected" => normalized,
            "last.fm" => "lastfm",
            _ => "external"
        };
    }

    private static string BuildLibraryImageUrl(string path)
        => $"/api/library/image?path={Uri.EscapeDataString(Path.GetFullPath(path))}&size=640";

    private static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed record ArtistVisualCacheCandidate(
    string? Source,
    string? Label,
    string? Url,
    string? Path,
    string? Identity);

public sealed record CachedArtistVisual(
    string Source,
    string Label,
    string Url,
    string Path,
    string? Identity);
