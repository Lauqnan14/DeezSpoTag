using System.Text.Json;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Library;

namespace DeezSpoTag.Web.Services;

public sealed class SpotifyArtistImageCacheService
{
    private readonly LibraryRepository _repository;
    private readonly ArtistPageCacheRepository _cacheRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LibraryConfigStore _configStore;
    private readonly ILogger<SpotifyArtistImageCacheService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _cacheRoot;

    public SpotifyArtistImageCacheService(
        LibraryRepository repository,
        ArtistPageCacheRepository cacheRepository,
        IHttpClientFactory httpClientFactory,
        IWebHostEnvironment environment,
        LibraryConfigStore configStore,
        ILogger<SpotifyArtistImageCacheService> logger)
    {
        _repository = repository;
        _cacheRepository = cacheRepository;
        _httpClientFactory = httpClientFactory;
        _configStore = configStore;
        _logger = logger;
        _cacheRoot = Path.Join(AppDataPaths.GetDataRoot(environment), "library-artist-images", "spotify");
    }

    public async Task CacheFromSpotifyCacheAsync(CancellationToken cancellationToken)
    {
        if (!_repository.IsConfigured)
        {
            return;
        }

        var artists = await _repository.GetArtistsAsync("all", cancellationToken);
        if (artists.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(_cacheRoot);
        var downloaded = 0;
        var skipped = 0;

        foreach (var artist in artists)
        {
            if (string.IsNullOrWhiteSpace(artist.Name))
            {
                continue;
            }

            var spotifyId = await _repository.GetArtistSourceIdAsync(artist.Id, "spotify", cancellationToken);
            if (string.IsNullOrWhiteSpace(spotifyId))
            {
                skipped++;
                continue;
            }

            var imageUrl = await ResolveImageUrlAsync(spotifyId, artist.Name, cancellationToken);
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                skipped++;
                continue;
            }

            var destinationPath = BuildDestinationPath(artist.Name, spotifyId, imageUrl);
            if (File.Exists(destinationPath))
            {
                skipped++;
                continue;
            }

            var wasDownloaded = await TryDownloadImageAsync(imageUrl, destinationPath, artist.Name, cancellationToken);
            if (wasDownloaded)
            {
                downloaded++;
            }
            else
            {
                skipped++;
            }
        }

        _configStore.AddLog(new LibraryConfigStore.LibraryLogEntry(
            DateTimeOffset.UtcNow,
            "info",
            $"Spotify artist image cache saved ({downloaded} new, {skipped} skipped)."));
    }

    private async Task<string?> ResolveImageUrlAsync(string spotifyId, string artistName, CancellationToken cancellationToken)
    {
        var cached = await _cacheRepository.TryGetAsync("spotify", spotifyId, cancellationToken);
        if (cached is null)
        {
            return null;
        }

        SpotifyArtistPageResult? page;
        try
        {
            page = JsonSerializer.Deserialize<SpotifyArtistPageResult>(cached.PayloadJson, _jsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to parse cached Spotify artist payload for {ArtistName}", artistName);
            return null;
        }

        return page?.Artist?.Images?
            .Where(image => !string.IsNullOrWhiteSpace(image.Url))
            .OrderByDescending(image => image.Width ?? image.Height ?? 0)
            .ThenByDescending(image => image.Height ?? 0)
            .FirstOrDefault()
            ?.Url;
    }

    private async Task<bool> TryDownloadImageAsync(
        string imageUrl,
        string destinationPath,
        string artistName,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(imageUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = File.Create(destinationPath);
            await stream.CopyToAsync(file, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to download Spotify image for {ArtistName}", artistName);
            return false;
        }
    }

    private string BuildDestinationPath(string artistName, string spotifyId, string imageUrl)
    {
        var extension = GetImageExtension(imageUrl);
        var fileName = $"{SanitizeFileName(artistName)}-{spotifyId}{extension}";
        return Path.Join(_cacheRoot, fileName);
    }

    private static string SanitizeFileName(string value)
    {
        var sanitized = CjkFilenameSanitizer.SanitizeSegment(
            value,
            fallback: "unknown",
            replacement: "_",
            collapseWhitespace: true,
            trimTrailingDotsAndSpaces: true);

        return sanitized.Replace(' ', '_');
    }

    private static string GetImageExtension(string url)
    {
        try
        {
            var extension = Path.GetExtension(new Uri(url).AbsolutePath);
            if (!string.IsNullOrWhiteSpace(extension))
            {
                return extension;
            }
        }
        catch (UriFormatException)
        {
            return ".jpg";
        }
        catch (ArgumentException)
        {
            return ".jpg";
        }

        return ".jpg";
    }
}
