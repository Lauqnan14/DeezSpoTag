using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeezSpoTag.Services.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Apple;

public sealed class AppleMusicCatalogService
{
    public sealed record AppleSearchOptions(
        string? TypesOverride = null,
        int Offset = 0,
        bool IncludeRelationshipsTracks = true);

    private sealed record AppleSearchContext(
        string Term,
        int Limit,
        string Storefront,
        string Language,
        string Types,
        int Offset,
        bool IncludeRelationshipsTracks);

    private const string AppleMusicScheme = "https";
    private const string AppleMusicHost = "music.apple.com";
    private const string AppleMusicCatalogApiHost = "amp-api.music.apple.com";
    private const string DefaultStorefront = "us";
    private const string MediaUserTokenHeader = "Media-User-Token";
    private const string UserAgentHeader = "User-Agent";
    private const string AcceptLanguageHeader = "Accept-Language";
    private const string DefaultAcceptLanguage = "en-US,en;q=0.9";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex JwtRegex = CreateRegex(@"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", RegexOptions.Compiled);
    private static readonly SemaphoreSlim CatalogSemaphore = new(8, 8);
    private static string GetRandomUserAgent() => AppleUserAgentPool.GetRandomUserAgent();
    private static Regex CreateRegex(string pattern, RegexOptions options)
        => new(pattern, options, RegexTimeout);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AppleMusicCatalogService> _logger;
    private readonly IMemoryCache _cache;
    private readonly DeezSpoTagSettingsService _settingsService;

    private readonly object _tokenLock = new();
    private readonly SemaphoreSlim _tokenRefreshLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiresAt = DateTimeOffset.MinValue;

    public AppleMusicCatalogService(
        IHttpClientFactory httpClientFactory,
        DeezSpoTagSettingsService settingsService,
        ILogger<AppleMusicCatalogService> logger,
        IMemoryCache cache)
    {
        _httpClientFactory = httpClientFactory;
        _settingsService = settingsService;
        _logger = logger;
        _cache = cache;
    }

    public Task<string> GetAuthorizationTokenAsync(CancellationToken cancellationToken)
        => GetTokenAsync(cancellationToken);

    public async Task<string> ResolveStorefrontAsync(
        string? configuredStorefront,
        string? mediaUserToken,
        CancellationToken cancellationToken)
    {
        var storefront = string.IsNullOrWhiteSpace(configuredStorefront) ? DefaultStorefront : configuredStorefront.Trim();
        if (string.IsNullOrWhiteSpace(mediaUserToken))
        {
            return storefront;
        }

        try
        {
            var accountStorefront = await GetAccountStorefrontAsync(mediaUserToken, cancellationToken);
            if (!string.IsNullOrWhiteSpace(accountStorefront))
            {
                return accountStorefront;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple storefront resolution from account failed; falling back to configured storefront.");
        }

        return storefront;
    }

    public async Task<JsonDocument> SearchAsync(
        string term,
        int limit,
        string storefront,
        string language,
        CancellationToken cancellationToken,
        AppleSearchOptions? options = null)
    {
        options ??= new AppleSearchOptions();
        var types = string.IsNullOrWhiteSpace(options.TypesOverride)
            ? "songs,albums,artists,music-videos"
            : options.TypesOverride;
        var context = new AppleSearchContext(
            term,
            limit,
            storefront,
            language,
            types,
            options.Offset,
            options.IncludeRelationshipsTracks);
        return await SearchWithTokenAsync(context, cancellationToken);
    }

    public async Task<JsonDocument> GetAlbumAsync(
        string id,
        string storefront,
        string language,
        CancellationToken cancellationToken)
    {
        var url =
            $"{BuildCatalogApiBaseUrl()}/v1/catalog/{Uri.EscapeDataString(storefront)}/albums/{Uri.EscapeDataString(id)}" +
            $"?include=tracks&extend=editorialVideo&l={Uri.EscapeDataString(language)}";
        var cacheKey = $"apple:album:{storefront}:{id}";
        return await GetCachedJsonAsync(cacheKey, TimeSpan.FromMinutes(30), () => SendWithTokenRetryRawAsync(HttpMethod.Get, url, cancellationToken));
    }

    public async Task<JsonDocument> GetSongAsync(
        string id,
        string storefront,
        string language,
        CancellationToken cancellationToken,
        string? mediaUserToken = null)
    {
        var url =
            $"{BuildCatalogApiBaseUrl()}/v1/catalog/{Uri.EscapeDataString(storefront)}/songs/{Uri.EscapeDataString(id)}" +
            $"?extend=editorialVideo,extendedAssetUrls&l={Uri.EscapeDataString(language)}";
        // Versioned cache key so older cached song payloads (without extendedAssetUrls) are not reused.
        var cacheKey = $"apple:song:v2:{storefront}:{id}";
        return await GetCachedJsonAsync(
            cacheKey,
            TimeSpan.FromMinutes(15),
            () => SendWithTokenRetryRawAsync(
                HttpMethod.Get,
                url,
                cancellationToken,
                request => ApplyMediaUserHeaders(request, mediaUserToken, storefront)));
    }

    public async Task<JsonDocument> GetSongByIsrcAsync(
        string isrc,
        string storefront,
        string language,
        CancellationToken cancellationToken,
        string? mediaUserToken = null)
    {
        var url =
            $"{BuildCatalogApiBaseUrl()}/v1/catalog/{Uri.EscapeDataString(storefront)}/songs?filter[isrc]={Uri.EscapeDataString(isrc)}&extend=extendedAssetUrls&l={Uri.EscapeDataString(language)}";
        // Versioned cache key so ISRC lookups include extendedAssetUrls for enhanced-HLS resolution.
        var cacheKey = $"apple:isrc:v2:{storefront}:{isrc}";
        return await GetCachedJsonAsync(
            cacheKey,
            TimeSpan.FromMinutes(30),
            () => SendWithTokenRetryRawAsync(
                HttpMethod.Get,
                url,
                cancellationToken,
                request => ApplyMediaUserHeaders(request, mediaUserToken, storefront)));
    }

    public async Task<JsonDocument> GetSongLyricsAsync(
        string id,
        string storefront,
        string language,
        CancellationToken cancellationToken,
        string? mediaUserToken = null)
    {
        var url =
            $"{BuildCatalogApiBaseUrl()}/v1/catalog/{Uri.EscapeDataString(storefront)}/songs/{Uri.EscapeDataString(id)}/lyrics" +
            $"?l={Uri.EscapeDataString(language)}&extend=ttmlLocalizations";
        var cacheKey = $"apple:lyrics:{storefront}:{id}:{language}";
        return await GetCachedJsonAsync(
            cacheKey,
            TimeSpan.FromMinutes(10),
            () => SendWithTokenRetryRawAsync(
                HttpMethod.Get,
                url,
                cancellationToken,
                request => ApplyMediaUserHeaders(request, mediaUserToken, storefront)));
    }

    public async Task<string?> GetAccountStorefrontAsync(string mediaUserToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mediaUserToken))
        {
            return null;
        }

        // Cache by token hash to avoid hammering /v1/me/account on every track download.
        // Uncached calls here were triggering Apple 2FA because each request looked like a
        // new session (especially combined with rotating User-Agents).
        var tokenHash = mediaUserToken.Length > 16 ? mediaUserToken[^16..] : mediaUserToken;
        var cacheKey = $"apple:account-storefront:{tokenHash}";
        if (_cache.TryGetValue(cacheKey, out string? cachedStorefront))
        {
            return cachedStorefront;
        }

        var url = $"{BuildCatalogApiBaseUrl()}/v1/me/account?meta=subscription";
        var payload = await SendWithTokenRetryRawAsync(
            HttpMethod.Get,
            url,
            cancellationToken,
            request => ApplyMediaUserHeaders(request, mediaUserToken, null));

        using var doc = JsonDocument.Parse(payload);
        string? storefront = null;
        if (doc.RootElement.TryGetProperty("meta", out var meta)
            && meta.TryGetProperty("subscription", out var subscription)
            && subscription.TryGetProperty("storefront", out var storefrontElement))
        {
            storefront = storefrontElement.GetString();
            if (string.IsNullOrWhiteSpace(storefront))
            {
                storefront = null;
            }
        }

        // Cache indefinitely — storefront is a static account property (country/region) that
        // virtually never changes. The cache key includes the token suffix, so a new token
        // naturally invalidates it, and IMemoryCache clears on app restart.
        _cache.Set(cacheKey, storefront, TimeSpan.FromDays(365));
        return storefront;
    }

    public async Task<JsonDocument> GetMusicVideoAsync(
        string id,
        string storefront,
        string language,
        CancellationToken cancellationToken)
    {
        var url = $"{BuildCatalogApiBaseUrl()}/v1/catalog/{Uri.EscapeDataString(storefront)}/music-videos/{Uri.EscapeDataString(id)}";
        var cacheKey = $"apple:video:{storefront}:{id}";
        return await GetCachedJsonAsync(cacheKey, TimeSpan.FromMinutes(15), () => SendWithTokenRetryRawAsync(HttpMethod.Get, url, cancellationToken));
    }

    public async Task<JsonDocument> GetPlaylistAsync(
        string id,
        string storefront,
        string language,
        CancellationToken cancellationToken)
    {
        var url =
            $"{BuildCatalogApiBaseUrl()}/v1/catalog/{Uri.EscapeDataString(storefront)}/playlists/{Uri.EscapeDataString(id)}" +
            $"?include=tracks&extend=editorialVideo&l={Uri.EscapeDataString(language)}";
        var cacheKey = $"apple:playlist:{storefront}:{id}";
        return await GetCachedJsonAsync(cacheKey, TimeSpan.FromMinutes(15), () => SendWithTokenRetryRawAsync(HttpMethod.Get, url, cancellationToken));
    }

    public async Task<JsonDocument> GetArtistAlbumsAsync(
        string id,
        string storefront,
        string language,
        int limit,
        int offset,
        CancellationToken cancellationToken) =>
        await GetArtistResourceAsync(
            new ArtistResourceRequest(
                id,
                storefront,
                language,
                limit,
                offset,
                "albums",
                "artist-albums",
                TimeSpan.FromMinutes(30)),
            cancellationToken);

    public async Task<JsonDocument> GetArtistMusicVideosAsync(
        string id,
        string storefront,
        string language,
        int limit,
        int offset,
        CancellationToken cancellationToken) =>
        await GetArtistResourceAsync(
            new ArtistResourceRequest(
                id,
                storefront,
                language,
                limit,
                offset,
                "music-videos",
                "artist-videos",
                TimeSpan.FromMinutes(15)),
            cancellationToken);

    private Task<JsonDocument> GetArtistResourceAsync(ArtistResourceRequest request, CancellationToken cancellationToken)
    {
        var url =
            $"{BuildCatalogApiBaseUrl()}/v1/catalog/{Uri.EscapeDataString(request.Storefront)}/artists/{Uri.EscapeDataString(request.Id)}/{request.ResourceSegment}?limit={request.Limit}&offset={request.Offset}&l={Uri.EscapeDataString(request.Language)}";
        var cacheKey = $"apple:{request.CacheSegment}:{request.Storefront}:{request.Id}:{request.Limit}:{request.Offset}";
        return GetCachedJsonAsync(cacheKey, request.CacheDuration, () => SendWithTokenRetryRawAsync(HttpMethod.Get, url, cancellationToken));
    }

    private sealed record ArtistResourceRequest(
        string Id,
        string Storefront,
        string Language,
        int Limit,
        int Offset,
        string ResourceSegment,
        string CacheSegment,
        TimeSpan CacheDuration);

    public async Task<JsonDocument> GetArtistAsync(
        string id,
        string storefront,
        string language,
        CancellationToken cancellationToken)
    {
        var url =
            $"{BuildCatalogApiBaseUrl()}/v1/catalog/{Uri.EscapeDataString(storefront)}/artists/{Uri.EscapeDataString(id)}?l={Uri.EscapeDataString(language)}";
        var cacheKey = $"apple:artist:{storefront}:{id}";
        return await GetCachedJsonAsync(cacheKey, TimeSpan.FromMinutes(30), () => SendWithTokenRetryRawAsync(HttpMethod.Get, url, cancellationToken));
    }

    public async Task<JsonDocument> GetStationAsync(
        string id,
        string storefront,
        string language,
        CancellationToken cancellationToken)
    {
        var url =
            $"{BuildCatalogApiBaseUrl()}/v1/catalog/{Uri.EscapeDataString(storefront)}/stations/{Uri.EscapeDataString(id)}?omit[resource]=autos&extend=editorialVideo&l={Uri.EscapeDataString(language)}";
        var cacheKey = $"apple:station:{storefront}:{id}";
        return await GetCachedJsonAsync(cacheKey, TimeSpan.FromMinutes(10), () => SendWithTokenRetryRawAsync(HttpMethod.Get, url, cancellationToken));
    }

    public async Task<JsonDocument> GetStationAssetsAsync(
        string id,
        string mediaUserToken,
        CancellationToken cancellationToken)
    {
        var url =
            $"{BuildCatalogApiBaseUrl()}/v1/play/assets?id={Uri.EscapeDataString(id)}&kind=radioStation&keyFormat=web";
        var cacheKey = $"apple:station-assets:{id}";
        return await GetCachedJsonAsync(
            cacheKey,
            TimeSpan.FromMinutes(5),
            () => SendWithTokenRetryRawAsync(
                HttpMethod.Get,
                url,
                cancellationToken,
                request => request.Headers.TryAddWithoutValidation(MediaUserTokenHeader, mediaUserToken)));
    }

    public async Task<JsonDocument> GetStationNextTracksAsync(
        string id,
        string mediaUserToken,
        string language,
        CancellationToken cancellationToken)
    {
        var url =
            $"{BuildCatalogApiBaseUrl()}/v1/me/stations/next-tracks/{Uri.EscapeDataString(id)}?omit[resource]=autos&include[songs]=artists,albums&limit=10&extend=editorialVideo,extendedAssetUrls&l={Uri.EscapeDataString(language)}";
        return await SendWithTokenRetryAsync(
            HttpMethod.Post,
            url,
            cancellationToken,
            request => request.Headers.TryAddWithoutValidation(MediaUserTokenHeader, mediaUserToken));
    }

    public Task<string> GetCatalogTokenAsync(CancellationToken cancellationToken)
    {
        return GetTokenAsync(cancellationToken);
    }

    private async Task<JsonDocument> SearchWithTokenAsync(
        AppleSearchContext context,
        CancellationToken cancellationToken)
    {
        var types = context.Types;
        if (IsSingleCatalogType(types))
        {
            var singleType = types.Trim();
            var paged = await SearchSingleTypeWithPaginationAsync(
                context,
                singleType,
                cancellationToken);
            if (!context.Storefront.Equals(DefaultStorefront, StringComparison.OrdinalIgnoreCase)
                && IsSearchTypeEmpty(paged.RootElement, singleType))
            {
                paged.Dispose();
                return await SearchSingleTypeWithPaginationAsync(
                    context with { Storefront = DefaultStorefront },
                    singleType,
                    cancellationToken);
            }

            return paged;
        }

        var url = BuildCatalogSearchUrl(
            context.Term,
            types,
            context.Limit,
            context.Storefront,
            context.Language,
            context.Offset,
            context.IncludeRelationshipsTracks);
        var cacheKey = $"apple:search:{context.Storefront}:{context.Language}:{context.Term}:{types}:{context.Limit}:{context.Offset}:{context.IncludeRelationshipsTracks}";
        var payload = await GetCachedPayloadAsync(cacheKey, TimeSpan.FromMinutes(5), () => SendWithTokenRetryRawAsync(HttpMethod.Get, url, cancellationToken));
        if (!context.Storefront.Equals(DefaultStorefront, StringComparison.OrdinalIgnoreCase) && IsSearchEmpty(payload))
        {
            var fallbackUrl = BuildCatalogSearchUrl(
                context.Term,
                types,
                context.Limit,
                DefaultStorefront,
                context.Language,
                context.Offset,
                context.IncludeRelationshipsTracks);
            var fallbackPayload = await SendWithTokenRetryRawAsync(HttpMethod.Get, fallbackUrl, cancellationToken);
            if (!IsSearchEmpty(fallbackPayload))
            {
                return JsonDocument.Parse(fallbackPayload);
            }
        }

        return JsonDocument.Parse(payload);
    }

    private async Task<JsonDocument> SearchSingleTypeWithPaginationAsync(
        AppleSearchContext context,
        string type,
        CancellationToken cancellationToken)
    {
        var requested = Math.Max(context.Limit, 1);
        var pageLimit = Math.Clamp(requested, 1, 25);
        var nextRequestUrl = BuildCatalogSearchUrl(
            context.Term,
            type,
            pageLimit,
            context.Storefront,
            context.Language,
            context.Offset,
            context.IncludeRelationshipsTracks);
        var nextResultPath = string.Empty;
        var data = new List<JsonElement>(requested);

        while (!string.IsNullOrWhiteSpace(nextRequestUrl) && data.Count < requested)
        {
            var payload = await SendWithTokenRetryRawAsync(HttpMethod.Get, nextRequestUrl, cancellationToken);
            using var doc = JsonDocument.Parse(payload);
            if (!TryProcessSearchPage(type, doc.RootElement, data, requested, out nextResultPath))
            {
                break;
            }

            nextRequestUrl = ToAbsoluteAppleUrl(nextResultPath);
        }

        return BuildSingleTypeSearchDocument(type, data, nextResultPath);
    }

    private static bool TryProcessSearchPage(
        string type,
        JsonElement root,
        List<JsonElement> data,
        int requested,
        out string nextResultPath)
    {
        nextResultPath = string.Empty;
        if (!TryGetSearchBucket(root, type, out var bucket))
        {
            return false;
        }

        if (bucket.TryGetProperty("data", out var pageData) && pageData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pageData.EnumerateArray())
            {
                data.Add(item.Clone());
                if (data.Count >= requested)
                {
                    break;
                }
            }
        }

        if (!bucket.TryGetProperty("next", out var nextEl) || nextEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        nextResultPath = nextEl.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(nextResultPath);
    }

    private static string BuildCatalogSearchUrl(
        string term,
        string types,
        int limit,
        string storefront,
        string language,
        int offset,
        bool includeRelationshipsTracks)
    {
        var include = includeRelationshipsTracks ? "&include=relationships.tracks" : string.Empty;
        return
            $"{BuildCatalogApiBaseUrl()}/v1/catalog/{Uri.EscapeDataString(storefront)}/search?term={Uri.EscapeDataString(term)}&types={Uri.EscapeDataString(types)}&limit={limit}&l={Uri.EscapeDataString(language)}&offset={offset}{include}";
    }

    private static bool IsSingleCatalogType(string types)
    {
        if (string.IsNullOrWhiteSpace(types))
        {
            return false;
        }

        return !types.Contains(',', StringComparison.Ordinal);
    }

    private static bool TryGetSearchBucket(JsonElement root, string type, out JsonElement bucket)
    {
        bucket = default;
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!results.TryGetProperty(type, out bucket) || bucket.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return true;
    }

    private static bool IsSearchTypeEmpty(JsonElement root, string type)
    {
        if (!TryGetSearchBucket(root, type, out var bucket))
        {
            return true;
        }

        if (!bucket.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        return data.GetArrayLength() == 0;
    }

    private static string ToAbsoluteAppleUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var normalized = DecodeAppleSearchNextUrl(url.Trim());
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absolute)
            && (absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || absolute.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return absolute.ToString();
        }

        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            return $"https:{normalized}";
        }

        if (normalized.Length == 0 || normalized[0] != '/')
        {
            normalized = "/" + normalized;
        }

        return $"{BuildCatalogApiBaseUrl()}{normalized}";
    }

    private static string DecodeAppleSearchNextUrl(string url)
    {
        // Apple sometimes returns the "next" path with encoded query delimiters.
        // Decode only delimiters so already-encoded query values remain untouched.
        return url
            .Replace("%3F", "?", StringComparison.OrdinalIgnoreCase)
            .Replace("%26", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("%3D", "=", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonDocument BuildSingleTypeSearchDocument(string type, IReadOnlyCollection<JsonElement> data, string? next)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("results");
            writer.WriteStartObject();
            writer.WritePropertyName(type);
            writer.WriteStartObject();
            writer.WritePropertyName("data");
            writer.WriteStartArray();
            foreach (var item in data)
            {
                item.WriteTo(writer);
            }
            writer.WriteEndArray();
            if (string.IsNullOrWhiteSpace(next))
            {
                writer.WriteNull("next");
            }
            else
            {
                writer.WriteString("next", next);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        stream.Position = 0;
        return JsonDocument.Parse(stream);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var cachedToken = GetCachedToken(requireFresh: true);
        if (!string.IsNullOrWhiteSpace(cachedToken))
        {
            return cachedToken;
        }

        await _tokenRefreshLock.WaitAsync(cancellationToken);
        try
        {
            cachedToken = GetCachedToken(requireFresh: true);
            if (!string.IsNullOrWhiteSpace(cachedToken))
            {
                return cachedToken;
            }

            var configuredToken = GetConfiguredToken();
            if (!string.IsNullOrWhiteSpace(configuredToken) && JwtRegex.IsMatch(configuredToken))
            {
                _logger.LogInformation("Apple dev token resolved from settings.");
                CacheToken(configuredToken, ResolveTokenExpiry(configuredToken, DateTimeOffset.UtcNow.AddHours(6)));
                return configuredToken;
            }

            if (!string.IsNullOrWhiteSpace(configuredToken))
            {
                _logger.LogWarning("Configured Apple dev token is not a JWT; ignoring configured token.");
            }

            var envToken = Environment.GetEnvironmentVariable("APPLE_DEV_TOKEN") ?? Environment.GetEnvironmentVariable("DEV_TOKEN");
            if (!string.IsNullOrWhiteSpace(envToken) && JwtRegex.IsMatch(envToken))
            {
                _logger.LogInformation("Apple dev token resolved from environment.");
                CacheToken(envToken, ResolveTokenExpiry(envToken, DateTimeOffset.UtcNow.AddHours(6)));
                return envToken;
            }

            Exception? lastTokenError = null;
            const int tokenAttempts = 3;
            for (var attempt = 0; attempt < tokenAttempts; attempt++)
            {
                try
                {
                    var token = await FetchTokenByScrapingAsync(cancellationToken);
                    _logger.LogInformation("Apple dev token resolved from Apple Music web scrape.");
                    CacheToken(token, ResolveTokenExpiry(token, DateTimeOffset.UtcNow.AddMinutes(90)));
                    return token;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastTokenError = ex;
                    _logger.LogWarning(ex, "Apple dev token fetch attempt {Attempt}/{MaxAttempts} failed.", attempt + 1, tokenAttempts);
                    if (attempt < tokenAttempts - 1)
                    {
                        await Task.Delay(GetBackoffDelay(attempt), cancellationToken);
                    }
                }
            }

            var staleToken = GetCachedToken(requireFresh: false);
            if (!string.IsNullOrWhiteSpace(staleToken))
            {
                _logger.LogWarning(lastTokenError, "Using stale Apple dev token after refresh failed.");
                return staleToken;
            }

            throw new InvalidOperationException("Unable to fetch Apple Music catalog token.", lastTokenError);
        }
        finally
        {
            _tokenRefreshLock.Release();
        }
    }

    private string? GetCachedToken(bool requireFresh)
    {
        lock (_tokenLock)
        {
            if (string.IsNullOrWhiteSpace(_cachedToken))
            {
                return null;
            }

            if (!requireFresh)
            {
                return _cachedToken;
            }

            return DateTimeOffset.UtcNow < _cachedTokenExpiresAt
                ? _cachedToken
                : null;
        }
    }

    private static DateTimeOffset ResolveTokenExpiry(string token, DateTimeOffset fallback)
    {
        var parsed = TryGetJwtExpiry(token);
        if (parsed.HasValue && parsed.Value > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return parsed.Value;
        }

        return fallback;
    }

    private static DateTimeOffset? TryGetJwtExpiry(string token)
    {
        if (!TryDecodeJwtPayload(token, out var payloadJson) || string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            if (!payload.RootElement.TryGetProperty("exp", out var expElement)
                || expElement.ValueKind != JsonValueKind.Number
                || !expElement.TryGetInt64(out var expUnix))
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds(expUnix).Subtract(TimeSpan.FromMinutes(5));
        }
        catch
        {
            return null;
        }
    }

    private string? GetConfiguredToken()
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            var token = string.IsNullOrWhiteSpace(settings.AppleMusic?.AuthorizationToken)
                ? settings.AuthorizationToken
                : settings.AppleMusic.AuthorizationToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            token = token.Trim();
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = token.Substring("Bearer ".Length).Trim();
            }

            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Apple configured authorization token lookup failed.");
            return null;
        }
    }

    private void CacheToken(string token, DateTimeOffset expiresAt)
    {
        lock (_tokenLock)
        {
            _cachedToken = token;
            _cachedTokenExpiresAt = expiresAt;
        }
    }

    private void InvalidateToken()
    {
        lock (_tokenLock)
        {
            _cachedToken = null;
            _cachedTokenExpiresAt = DateTimeOffset.MinValue;
        }
    }

    private async Task<string> FetchTokenByScrapingAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();

        // Try a couple of known pages to reduce regional flakiness.
        var homeUrls = new[]
        {
            $"{BuildAppleMusicBaseUrl()}/us/browse",
            $"{BuildAppleMusicBaseUrl()}/us/home",
            $"{BuildAppleMusicBaseUrl()}/ca/home",
            $"{BuildAppleMusicBaseUrl()}/gb/browse"
        };

        foreach (var homeUrl in homeUrls)
        {
            var html = await TryGetHtmlAsync(client, homeUrl, cancellationToken);
            if (!string.IsNullOrWhiteSpace(html))
            {
                var jwt = FindJwt(html);
                if (!string.IsNullOrWhiteSpace(jwt))
                {
                    return jwt;
                }

                // Fallback: fetch a few JS bundles referenced by the HTML and scan them for a JWT.
                var jsUrls = ExtractAppleMusicJsBundleUrls(html)
                    .Take(12)
                    .ToList();

                for (var jsIndex = 0; jsIndex < jsUrls.Count; jsIndex++)
                {
                    var js = await TryGetTextAsync(client, jsUrls[jsIndex], cancellationToken);
                    jwt = FindJwt(js);
                    if (!string.IsNullOrWhiteSpace(jwt))
                    {
                        return jwt;
                    }
                }
            }
        }

        var guiToken = await TryGetGuiStyleTokenAsync(client, cancellationToken);
        if (!string.IsNullOrWhiteSpace(guiToken))
        {
            return guiToken;
        }

        throw new InvalidOperationException("Unable to fetch Apple Music catalog token (no JWT found).");
    }

    private static string? FindJwt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var matches = JwtRegex.Matches(text);
        if (matches.Count == 0)
        {
            return null;
        }

        string? fallback = null;
        for (var index = 0; index < matches.Count; index++)
        {
            var candidate = matches[index].Value;
            if (string.IsNullOrWhiteSpace(fallback))
            {
                fallback = candidate;
            }

            if (IsLikelyCatalogJwt(candidate))
            {
                return candidate;
            }
        }

        return fallback;
    }

    private static bool IsLikelyCatalogJwt(string token)
    {
        if (!TryDecodeJwtPayload(token, out var payloadJson) || string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            if (!payload.RootElement.TryGetProperty("iss", out var issuerElement)
                || issuerElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var issuer = issuerElement.GetString() ?? string.Empty;
            if (!issuer.Contains("AMP", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (payload.RootElement.TryGetProperty("exp", out var expElement)
                && expElement.ValueKind == JsonValueKind.Number
                && expElement.TryGetInt64(out var expUnix))
            {
                var exp = DateTimeOffset.FromUnixTimeSeconds(expUnix);
                if (exp <= DateTimeOffset.UtcNow)
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDecodeJwtPayload(string token, out string payloadJson)
    {
        payloadJson = string.Empty;
        var parts = token.Split('.');
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        try
        {
            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            var pad = 4 - (payload.Length % 4);
            if (pad is > 0 and < 4)
            {
                payload = payload + new string('=', pad);
            }

            var bytes = Convert.FromBase64String(payload);
            payloadJson = Encoding.UTF8.GetString(bytes);
            return !string.IsNullOrWhiteSpace(payloadJson);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> ExtractAppleMusicJsBundleUrls(string html)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var regexes = new[]
        {
            CreateRegex("(?:src|href|data-src)=[\"'](?<url>/assets/index~[^\"']+\\.js)[\"']", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            CreateRegex("(?:src|href|data-src)=[\"'](?<url>/assets/index-legacy~[^\"']+\\.js)[\"']", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            CreateRegex("(?:src|href|data-src)=[\"'](?<url>/assets/[^\"']+\\.js)[\"']", RegexOptions.Compiled | RegexOptions.IgnoreCase)
        };

        foreach (var matches in regexes.Select(rx => rx.Matches(html)))
        {
            for (var matchIndex = 0; matchIndex < matches.Count; matchIndex++)
            {
                var path = matches[matchIndex].Groups["url"].Value;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var full = BuildAppleMusicBaseUrl() + path;
                if (seen.Add(full))
                {
                    yield return full;
                }
            }
        }
    }

    private async Task<string?> TryGetGuiStyleTokenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var homeUrl = BuildAppleMusicBaseUrl();
        var html = await TryGetTextAsync(client, homeUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var indexRegex = CreateRegex(@"/assets/index~[^/]+\.js", RegexOptions.Compiled);
        var match = indexRegex.Match(html);
        if (!match.Success)
        {
            return null;
        }

        var jsUrl = $"{BuildAppleMusicBaseUrl()}{match.Value}";
        var js = await TryGetTextAsync(client, jsUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(js))
        {
            return null;
        }

        var jwtRegex = CreateRegex(@"eyJh[^""]*", RegexOptions.Compiled);
        var jwt = jwtRegex.Match(js);
        return jwt.Success ? jwt.Value : null;
    }

    private async Task<string?> TryGetHtmlAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(UserAgentHeader, GetRandomUserAgent());
        request.Headers.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation(AcceptLanguageHeader, DefaultAcceptLanguage);
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Apple token HTML fetch failed: {Url} status {Status}", url, response.StatusCode);            }
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<string?> TryGetTextAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(UserAgentHeader, GetRandomUserAgent());
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Apple token fetch failed: {Url} status {Status}", url, response.StatusCode);                }
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple token fetch failed: {Url}", url);            }
            return null;
        }
    }

    private static void ApplyHeaders(HttpRequestMessage request, string token)
    {
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        // Keep a stable client fingerprint for catalog calls to reduce challenge responses.
        request.Headers.TryAddWithoutValidation(UserAgentHeader, AppleUserAgentPool.GetAuthenticatedUserAgent());
        request.Headers.TryAddWithoutValidation("Origin", BuildAppleMusicBaseUrl());
        request.Headers.TryAddWithoutValidation("Referer", $"{BuildAppleMusicBaseUrl()}/");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation(AcceptLanguageHeader, DefaultAcceptLanguage);
    }

    private static void ApplyMediaUserHeaders(HttpRequestMessage request, string? mediaUserToken, string? storefront)
    {
        if (string.IsNullOrWhiteSpace(mediaUserToken))
        {
            return;
        }

        request.Headers.TryAddWithoutValidation(MediaUserTokenHeader, mediaUserToken);

        var cookie = $"media-user-token={mediaUserToken}";
        if (!string.IsNullOrWhiteSpace(storefront))
        {
            cookie += $"; itua={storefront}";
        }

        request.Headers.TryAddWithoutValidation("Cookie", cookie);
    }

    /// <summary>
    /// If the request carries a Media-User-Token, replace the random UA with a pinned one
    /// so Apple doesn't see each authenticated call as a different device/browser session.
    /// </summary>
    private static void PinUserAgentIfAuthenticated(HttpRequestMessage request)
    {
        if (!request.Headers.Contains(MediaUserTokenHeader))
        {
            return;
        }

        request.Headers.Remove(UserAgentHeader);
        request.Headers.TryAddWithoutValidation(UserAgentHeader, AppleUserAgentPool.GetAuthenticatedUserAgent());
    }

    private static string BuildAppleMusicBaseUrl()
        => new UriBuilder(AppleMusicScheme, AppleMusicHost).Uri.ToString().TrimEnd('/');

    private static string BuildCatalogApiBaseUrl()
        => new UriBuilder(AppleMusicScheme, AppleMusicCatalogApiHost).Uri.ToString().TrimEnd('/');

    private static bool IsSearchEmpty(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("results", out var results))
            {
                return true;
            }

            foreach (var category in results.EnumerateObject().Select(static category => category.Value))
            {
                if (category.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!category.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                if (data.GetArrayLength() > 0)
                {
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        return true;
    }

    private async Task<JsonDocument> SendWithTokenRetryAsync(
        HttpMethod method,
        string url,
        CancellationToken cancellationToken,
        Action<HttpRequestMessage>? configure = null)
    {
        var payload = await SendWithTokenRetryRawAsync(method, url, cancellationToken, configure);
        return JsonDocument.Parse(payload);
    }

    private async Task<string> SendWithTokenRetryRawAsync(
        HttpMethod method,
        string url,
        CancellationToken cancellationToken,
        Action<HttpRequestMessage>? configure = null)
    {
        const int maxAttempts = 3;
        var client = _httpClientFactory.CreateClient();

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var token = await GetTokenAsync(cancellationToken);
            await CatalogSemaphore.WaitAsync(cancellationToken);
            try
            {
                using var request = BuildCatalogRequest(method, url, token, configure);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var retryAction = await EvaluateRetryAsync(response, attempt, maxAttempts, url, cancellationToken);
                if (retryAction == RetryAction.Retry)
                {
                    continue;
                }

                var payload = await EnsureJsonPayloadAsync(response, attempt, maxAttempts, url, cancellationToken);
                if (payload == null)
                {
                    continue;
                }

                return payload;
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts - 1)
            {
                if (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    throw;
                }
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Apple catalog request retry due to HTTP failure: {Url}", url);                }
                await Task.Delay(GetBackoffDelay(attempt), cancellationToken);
            }
            finally
            {
                CatalogSemaphore.Release();
            }
        }

        throw new InvalidOperationException($"Apple catalog request failed after {maxAttempts} attempts.");
    }

    private static HttpRequestMessage BuildCatalogRequest(
        HttpMethod method,
        string url,
        string token,
        Action<HttpRequestMessage>? configure)
    {
        var request = new HttpRequestMessage(method, url);
        ApplyHeaders(request, token);
        configure?.Invoke(request);
        PinUserAgentIfAuthenticated(request);
        return request;
    }

    private async Task<RetryAction> EvaluateRetryAsync(
        HttpResponseMessage response,
        int attempt,
        int maxAttempts,
        string url,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            InvalidateToken();
            if (attempt < maxAttempts - 1)
            {
                return RetryAction.Retry;
            }
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxAttempts - 1)
        {
            await Task.Delay(GetRetryDelay(response, attempt), cancellationToken);
            return RetryAction.Retry;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Apple catalog request failed: {StatusCode} {Url}", response.StatusCode, url);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }

        response.EnsureSuccessStatusCode();
        return RetryAction.None;
    }

    private async Task<string?> EnsureJsonPayloadAsync(
        HttpResponseMessage response,
        int attempt,
        int maxAttempts,
        string url,
        CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (LooksLikeJsonPayload(payload))
        {
            return payload;
        }

        _logger.LogWarning("Apple catalog request returned non-JSON payload from {Url}", url);
        InvalidateToken();
        if (attempt < maxAttempts - 1)
        {
            await Task.Delay(GetBackoffDelay(attempt), cancellationToken);
            return null;
        }

        throw new HttpRequestException("Apple catalog request returned a non-JSON payload.", null, HttpStatusCode.BadGateway);
    }

    private enum RetryAction
    {
        None,
        Retry
    }

    private static bool LooksLikeJsonPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        var trimmed = payload.TrimStart();
        return trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[');
    }

    private async Task<JsonDocument> GetCachedJsonAsync(string cacheKey, TimeSpan ttl, Func<Task<string>> fetch)
    {
        if (_cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return JsonDocument.Parse(cached);
        }

        var payload = await fetch();
        _cache.Set(cacheKey, payload, ttl);
        return JsonDocument.Parse(payload);
    }

    private async Task<string> GetCachedPayloadAsync(string cacheKey, TimeSpan ttl, Func<Task<string>> fetch)
    {
        if (_cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var payload = await fetch();
        _cache.Set(cacheKey, payload, ttl);
        return payload;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.Zero;
        var backoff = GetBackoffDelay(attempt);
        var effective = retryAfter > backoff ? retryAfter : backoff;
        return effective;
    }

    private static TimeSpan GetBackoffDelay(int attempt)
    {
        var baseDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
        return baseDelay + jitter;
    }

}
