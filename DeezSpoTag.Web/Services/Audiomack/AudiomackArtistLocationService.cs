using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeezSpoTag.Services.Library;
using DeezSpoTag.Core.Security;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Web.Services.Audiomack;

/// <summary>
/// Resolves an artist's location (city + country) from the artist's public
/// Audiomack profile. The Audiomack artist identity is resolved through the
/// first-party web search API (anonymous: no cookies, no user tokens) and
/// persisted in artist_source (source "audiomack") so later loads — and user
/// corrections from the library artist page — target an exact profile instead
/// of re-guessing a slug from the artist name. Lookups are keyed by the
/// canonical slug and cached in memory plus persisted through
/// <see cref="ArtistPageCacheRepository"/> so a restart does not re-fetch. A
/// miss (no hometown set) is cached as well: an unknown location never
/// triggers repeated fetching.
/// </summary>
public sealed class AudiomackArtistLocationService
{
    public const string LocationCacheSource = "audiomack-location";
    private const string ArtistPageBaseUrl = "https://audiomack.com/";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MemoryCacheTtl = TimeSpan.FromDays(7);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ArtistPageCacheRepository _artistPageCache;
    private readonly AudiomackApiClient _apiClient;
    private readonly LibraryRepository? _libraryRepository;
    private readonly ILogger<AudiomackArtistLocationService> _logger;
    private readonly ConcurrentDictionary<string, (AudiomackLocationResult? Location, DateTimeOffset FetchedUtc)> _memoryCache =
        new(StringComparer.OrdinalIgnoreCase);

    public AudiomackArtistLocationService(
        IHttpClientFactory httpClientFactory,
        ArtistPageCacheRepository artistPageCache,
        AudiomackApiClient apiClient,
        ILogger<AudiomackArtistLocationService> logger,
        LibraryRepository? libraryRepository = null)
    {
        _httpClientFactory = httpClientFactory;
        _artistPageCache = artistPageCache;
        _apiClient = apiClient;
        _logger = logger;
        _libraryRepository = libraryRepository;
    }

    /// <summary>
    /// Resolve for a library artist: prefers the stored Audiomack mapping in
    /// artist_source (set once via the anonymous search API, correctable by the
    /// user from the library artist page), discovers it only when absent, and
    /// never re-searches behind a stored value.
    /// </summary>
    public async Task<AudiomackLocationResult?> ResolveAsync(long artistId, string? artistName, CancellationToken cancellationToken = default)
    {
        var storedSlug = await TryGetStoredSlugAsync(artistId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(storedSlug))
        {
            // The stored mapping is authoritative (auto-discovered once, or user-set).
            // A null here is a cached negative for that slug; rediscovery would burn a
            // search on every page load, so it stops here — the user can correct the
            // mapping from the library page instead.
            return await ResolveBySlugAsync(storedSlug, artistName, cancellationToken).ConfigureAwait(false);
        }

        var candidate = await _apiClient.SearchArtistAsync(artistName, cancellationToken).ConfigureAwait(false);
        var slug = candidate?.UrlSlug ?? BuildUrlSlug(artistName);
        if (slug == null)
        {
            return null;
        }

        var result = await ResolveBySlugAsync(slug, artistName, cancellationToken).ConfigureAwait(false);
        if (result != null || candidate != null)
        {
            // Persist the identity as soon as it is validated (search name-match, or the
            // page parse confirmed the artist name) — including when Audiomack simply has
            // no hometown, so the search cost is paid once.
            await TryStoreSlugAsync(artistId, slug, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Name-based resolution for flows without a library artist id.</summary>
    public async Task<AudiomackLocationResult?> ResolveAsync(string? artistName, CancellationToken cancellationToken = default)
    {
        var slug = BuildUrlSlug(artistName);
        if (slug == null)
        {
            return null;
        }

        return await ResolveBySlugAsync(slug, artistName, cancellationToken).ConfigureAwait(false);
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

    private async Task<AudiomackLocationResult?> ResolveBySlugAsync(string slug, string? artistName, CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue(slug, out var cached) && DateTimeOffset.UtcNow - cached.FetchedUtc <= MemoryCacheTtl)
        {
            return cached.Location;
        }

        var (persistentFound, persistentLocation) = await TryReadCachedLocationAsync(slug, cancellationToken).ConfigureAwait(false);
        if (persistentFound)
        {
            _memoryCache[slug] = (persistentLocation, DateTimeOffset.UtcNow);
            return persistentLocation;
        }

        AudiomackLocationResult? resolved = null;
        try
        {
            // BuildUrlSlug returned non-null, which is only possible for a
            // non-whitespace artistName, hence the null-forgiving operator.
            resolved = await FetchLocationAsync(slug, artistName!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Audiomack artist location fetch failed (slug={Slug})",
                LogSanitizer.OneLine(slug));
        }

        _memoryCache[slug] = (resolved, DateTimeOffset.UtcNow);
        await WriteCachedLocationAsync(slug, resolved, cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    private async Task<AudiomackLocationResult?> FetchLocationAsync(string slug, string artistName, CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FetchTimeout);
        using var response = await httpClient.GetAsync(ArtistPageBaseUrl + slug, timeoutCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        var rawLocation = AudiomackArtistPageParser.TryExtractRawLocation(html, slug, artistName);
        return AudiomackLocationNormalizer.Normalize(rawLocation);
    }

    private async Task<string?> TryGetStoredSlugAsync(long artistId, CancellationToken cancellationToken)
    {
        if (_libraryRepository == null || !_libraryRepository.IsConfigured)
        {
            return null;
        }

        try
        {
            return await _libraryRepository.GetArtistSourceIdAsync(artistId, AudiomackApiClient.SourceName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Audiomack source lookup failed for artist {ArtistId}", artistId);
            return null;
        }
    }

    private async Task TryStoreSlugAsync(long artistId, string slug, CancellationToken cancellationToken)
    {
        if (_libraryRepository == null || !_libraryRepository.IsConfigured)
        {
            return;
        }

        try
        {
            await _libraryRepository.UpsertArtistSourceIdAsync(artistId, AudiomackApiClient.SourceName, slug, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Audiomack source persist failed for artist {ArtistId}", artistId);
        }
    }

    private async Task<(bool Found, AudiomackLocationResult? Location)> TryReadCachedLocationAsync(
        string slug,
        CancellationToken cancellationToken)
    {
        var entry = await _artistPageCache.TryGetAsync(LocationCacheSource, slug, cancellationToken).ConfigureAwait(false);
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

        await _artistPageCache.UpsertAsync(LocationCacheSource, slug, payload, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }
}
