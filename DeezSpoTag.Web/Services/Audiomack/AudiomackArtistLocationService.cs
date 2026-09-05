using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services.Audiomack;

/// <summary>
/// Resolves an artist's location (city + country) from the artist's public
/// Audiomack profile. Lookups are keyed by the slugified artist name, cached in
/// memory and persisted through <see cref="ArtistPageCacheRepository"/> so a
/// restart does not re-fetch. A miss (artist absent, page moved, no hometown)
/// is cached as well: an unknown location never triggers repeated fetching.
/// </summary>
public sealed class AudiomackArtistLocationService
{
    private const string PersistentCacheSource = "audiomack-location";
    private const string ArtistPageBaseUrl = "https://audiomack.com/";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MemoryCacheTtl = TimeSpan.FromDays(7);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ArtistPageCacheRepository _artistPageCache;
    private readonly ILogger<AudiomackArtistLocationService> _logger;
    private readonly ConcurrentDictionary<string, (AudiomackLocationResult? Location, DateTimeOffset FetchedUtc)> _memoryCache =
        new(StringComparer.OrdinalIgnoreCase);

    public AudiomackArtistLocationService(
        IHttpClientFactory httpClientFactory,
        ArtistPageCacheRepository artistPageCache,
        ILogger<AudiomackArtistLocationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _artistPageCache = artistPageCache;
        _logger = logger;
    }

    public async Task<AudiomackLocationResult?> ResolveAsync(string? artistName, CancellationToken cancellationToken = default)
    {
        var slug = BuildUrlSlug(artistName);
        if (slug == null)
        {
            return null;
        }

        if (_memoryCache.TryGetValue(slug, out var cached) && DateTimeOffset.UtcNow - cached.FetchedUtc <= MemoryCacheTtl)
        {
            return cached.Location;
        }

        var (persistentFound, persistentLocation) = await TryReadCachedLocationAsync(slug, cancellationToken);
        if (persistentFound)
        {
            _memoryCache[slug] = (persistentLocation, DateTimeOffset.UtcNow);
            return persistentLocation;
        }

        AudiomackLocationResult? resolved = null;
        try
        {
            resolved = await FetchLocationAsync(slug, artistName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Audiomack artist location fetch failed (slug={Slug})",
                DeezSpoTag.Core.Security.LogSanitizer.OneLine(slug));
        }

        _memoryCache[slug] = (resolved, DateTimeOffset.UtcNow);
        await WriteCachedLocationAsync(slug, resolved, cancellationToken);
        return resolved;
    }

    internal static string? BuildUrlSlug(string? artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return null;
        }

        var lowered = artistName.Trim().ToLowerInvariant();
        var slugChars = lowered
            .Select(ch => char.IsLetterOrDigit(ch)
                ? ch
                : ch is ' ' or '-' or '.' or '_' or '/' ? '-' : '\0')
            .Where(ch => ch != '\0');
        var slug = new string(slugChars.ToArray());
        slug = System.Text.RegularExpressions.Regex.Replace(slug, "-{2,}", "-").Trim('-');
        return slug.Length == 0 ? null : slug;
    }

    private async Task<AudiomackLocationResult?> FetchLocationAsync(string slug, string artistName, CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FetchTimeout);
        using var response = await httpClient.GetAsync(ArtistPageBaseUrl + slug, timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        var rawLocation = AudiomackArtistPageParser.TryExtractRawLocation(html, slug, artistName);
        return AudiomackLocationNormalizer.Normalize(rawLocation);
    }

    private async Task<(bool Found, AudiomackLocationResult? Location)> TryReadCachedLocationAsync(
        string slug,
        CancellationToken cancellationToken)
    {
        var entry = await _artistPageCache.TryGetAsync(PersistentCacheSource, slug, cancellationToken);
        if (entry == null || !_artistPageCache.IsUsable(entry.FetchedUtc))
        {
            return (false, null);
        }

        try
        {
            using var document = JsonDocument.Parse(entry.PayloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (true, null);
            }

            if (!root.TryGetProperty("raw_location", out var rawElement) || rawElement.ValueKind != JsonValueKind.String)
            {
                // Negative cache entry: the artist had no usable location.
                return (true, null);
            }

            return (true, AudiomackLocationNormalizer.Normalize(rawElement.GetString()));
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    private async Task WriteCachedLocationAsync(string slug, AudiomackLocationResult? location, CancellationToken cancellationToken)
    {
        var payload = location == null
            ? "{}"
            : JsonSerializer.Serialize(new
            {
                city = location.City,
                country = location.Country,
                country_code = location.CountryCode,
                location_source = "audiomack",
                raw_location = location.RawLocation
            });

        await _artistPageCache.UpsertAsync(PersistentCacheSource, slug, payload, DateTimeOffset.UtcNow, cancellationToken);
    }
}
