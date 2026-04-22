using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text.Json;
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Services.Download.Qobuz;
using DeezSpoTag.Services.Metadata.Qobuz;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeezSpoTag.Services.Download.Utils;

/// <summary>
/// Song.link resolver used to pivot a Spotify (or other) URL into other providers.
/// Ports the lightweight Redomi song.link client and extends it for the providers we support.
/// </summary>
public sealed class SongLinkResolver
{
    private const int MaxRequestsPerMinute = 9;
    private const string DeezerPlatform = "deezer";
    private const string SpotifyPlatform = "spotify";
    private const int MaxApiRetries = 3;
    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly char[] QueryAndFragmentSeparators = ['?', '#'];
    private static readonly string[] QobuzVersionPatterns =
    {
        "remaster", "remastered", "deluxe", "bonus", "single",
        "album version", "radio edit", "original mix", "extended",
        "club mix", "remix", "live", "acoustic", "demo"
    };
    private static readonly string[] QobuzDashPatterns =
    {
        " - remaster", " - remastered", " - single version", " - radio edit",
        " - live", " - acoustic", " - demo", " - remix"
    };
    private static readonly (int Start, int End)[] ExtendedLatinRanges =
    {
        (0x0100, 0x024F),
        (0x1E00, 0x1EFF),
        (0x00C0, 0x00FF)
    };
    private static readonly (int Start, int End)[] NonLatinScriptRanges =
    {
        (0x4E00, 0x9FFF),
        (0x3040, 0x309F),
        (0x30A0, 0x30FF),
        (0xAC00, 0xD7AF),
        (0x0600, 0x06FF),
        (0x0400, 0x04FF)
    };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DeezSpoTag.Services.Metadata.Qobuz.IQobuzMetadataService? _qobuzMetadataService;
    private readonly QobuzTrackResolver? _qobuzTrackResolver;
    private readonly QobuzApiConfig _qobuzConfig;
    private readonly SongLinkPersistentCacheStore? _persistentCacheStore;
    private readonly ILogger<SongLinkResolver> _logger;
    private readonly SemaphoreSlim _rateGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;
    private DateTimeOffset _windowStart = DateTimeOffset.UtcNow;
    private int _windowCount;

    public SongLinkResolver(
        IHttpClientFactory httpClientFactory,
        DeezSpoTag.Services.Metadata.Qobuz.IQobuzMetadataService? qobuzMetadataService,
        QobuzTrackResolver? qobuzTrackResolver,
        IOptions<QobuzApiConfig>? qobuzOptions,
        ILogger<SongLinkResolver> logger,
        SongLinkPersistentCacheStore? persistentCacheStore = null)
    {
        _httpClientFactory = httpClientFactory;
        _qobuzMetadataService = qobuzMetadataService;
        _qobuzTrackResolver = qobuzTrackResolver;
        _qobuzConfig = qobuzOptions?.Value ?? new QobuzApiConfig();
        _logger = logger;
        _persistentCacheStore = persistentCacheStore;
    }

    /// <summary>
    /// Resolve a Spotify track ID to cross-provider URLs (Deezer, Tidal, Amazon, Qobuz) and metadata.
    /// </summary>
    public Task<SongLinkResult?> ResolveSpotifyTrackAsync(string spotifyTrackId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyTrackId))
        {
            return Task.FromResult<SongLinkResult?>(null);
        }

        var spotifyUrl = $"https://open.spotify.com/track/{spotifyTrackId}";
        return ResolveByUrlAsync(spotifyUrl, cancellationToken);
    }

    public async Task<string?> ResolveDeezerIdFromSpotifyAsync(string spotifyTrackId, CancellationToken cancellationToken)
    {
        var result = await ResolveSpotifyTrackAsync(spotifyTrackId, cancellationToken);
        if (result == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(result.DeezerId))
        {
            return result.DeezerId;
        }

        if (!string.IsNullOrWhiteSpace(result.DeezerUrl))
        {
            return ExtractId(DeezerPlatform, null, result.DeezerUrl);
        }

        return null;
    }

    /// <summary>
    /// Resolve an arbitrary music URL via song.link.
    /// </summary>
    public async Task<SongLinkResult?> ResolveByUrlAsync(string url, CancellationToken cancellationToken)
    {
        return await ResolveByUrlAsync(url, userCountry: null, cancellationToken);
    }

    public async Task<SongLinkResult?> ResolveByUrlAsync(string url, string? userCountry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var normalizedUrl = NormalizeCacheUrl(url);
        var deezerTrackIdFromUrl = ExtractId(DeezerPlatform, null, normalizedUrl);

        if (TryGetFromCache(normalizedUrl, userCountry, out var cached))
        {
            return cached;
        }

        var persistentCached = await TryGetFromPersistentCacheAsync(normalizedUrl, userCountry, cancellationToken);
        if (persistentCached != null)
        {
            CacheResult(normalizedUrl, userCountry, persistentCached);
            return persistentCached;
        }

        await EnforceRateLimitAsync(cancellationToken);
        using var client = _httpClientFactory.CreateClient("SongLink");
        var requestOutcome = await FetchSongLinkPayloadAsync(
            client,
            BuildSongLinkApiUrl(normalizedUrl, userCountry),
            cancellationToken);

        var payload = requestOutcome.Payload;
        if (payload?.LinksByPlatform == null || payload.EntitiesByUniqueId == null)
        {
            return await HandleMissingPayloadAsync(
                normalizedUrl,
                userCountry,
                deezerTrackIdFromUrl,
                requestOutcome.SkipNegativeCache,
                cancellationToken);
        }

        var result = await BuildSongLinkResultAsync(payload, cancellationToken);
        CacheResult(normalizedUrl, userCountry, result);
        await CacheResultInPersistentStoreAsync(normalizedUrl, userCountry, result, cancellationToken);
        return result;
    }

    private static string BuildSongLinkApiUrl(string url, string? userCountry)
    {
        var countryParam = string.IsNullOrWhiteSpace(userCountry)
            ? string.Empty
            : $"&userCountry={WebUtility.UrlEncode(userCountry)}";

        return $"https://api.song.link/v1-alpha.1/links?url={WebUtility.UrlEncode(url)}{countryParam}";
    }

    private async Task<SongLinkRequestOutcome> FetchSongLinkPayloadAsync(
        HttpClient client,
        string apiUrl,
        CancellationToken cancellationToken)
    {
        var skipNegativeCache = false;
        for (var attempt = 1; attempt <= MaxApiRetries; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(apiUrl, cancellationToken);
                if ((int)response.StatusCode == 429)
                {
                    skipNegativeCache = true;
                    var hasRetry = attempt < MaxApiRetries;
                    await HandleSongLinkRateLimitAsync(attempt, hasRetry, cancellationToken);
                    if (hasRetry)
                    {
                        continue;
                    }

                    return new SongLinkRequestOutcome(null, skipNegativeCache);
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "song.link failed: status={Status}",
                        (int)response.StatusCode);
                    return new SongLinkRequestOutcome(null, skipNegativeCache);
                }

                var payload = await ParseSongLinkPayloadAsync(response, cancellationToken);
                return new SongLinkRequestOutcome(payload, skipNegativeCache);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "song.link request timed out");
                return new SongLinkRequestOutcome(null, skipNegativeCache);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < MaxApiRetries)
                {
                    _logger.LogWarning(
                        ex,
                        "song.link request failed (attempt {Attempt}/{MaxAttempts}); retrying in {Delay}s",
                        attempt,
                        MaxApiRetries,
                        RetryDelay.TotalSeconds);
                    await Task.Delay(RetryDelay, cancellationToken);
                    continue;
                }

                _logger.LogWarning(
                    ex,
                    "song.link request failed after {Attempts} attempts",
                    attempt);
                return new SongLinkRequestOutcome(null, skipNegativeCache);
            }
        }

        return new SongLinkRequestOutcome(null, skipNegativeCache);
    }

    private async Task<SongLinkResult?> HandleMissingPayloadAsync(
        string url,
        string? userCountry,
        string? deezerTrackIdFromUrl,
        bool skipNegativeCache,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(deezerTrackIdFromUrl))
        {
            var deezerOnly = await BuildDeezerOnlyResultAsync(deezerTrackIdFromUrl, cancellationToken);
            CacheResult(url, userCountry, deezerOnly);
            await CacheResultInPersistentStoreAsync(url, userCountry, deezerOnly, cancellationToken);
            return deezerOnly;
        }

        if (!skipNegativeCache)
        {
            CacheResult(url, userCountry, null);
        }

        return null;
    }

    private async Task<SongLinkResult?> TryGetFromPersistentCacheAsync(
        string normalizedUrl,
        string? userCountry,
        CancellationToken cancellationToken)
    {
        if (_persistentCacheStore == null)
        {
            return null;
        }

        var cacheKey = BuildCacheKey(normalizedUrl, userCountry);
        try
        {
            return await _persistentCacheStore.TryGetAsync(cacheKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Persistent song-link cache lookup failed for key {CacheKey}", cacheKey);
            }
            return null;
        }
    }

    private async Task CacheResultInPersistentStoreAsync(
        string normalizedUrl,
        string? userCountry,
        SongLinkResult? result,
        CancellationToken cancellationToken)
    {
        if (_persistentCacheStore == null || result == null)
        {
            return;
        }

        var cacheKey = BuildCacheKey(normalizedUrl, userCountry);
        try
        {
            await _persistentCacheStore.UpsertAsync(cacheKey, normalizedUrl, userCountry, result, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Persistent song-link cache upsert failed for key {CacheKey}", cacheKey);
            }
        }
    }

    private async Task HandleSongLinkRateLimitAsync(
        int attempt,
        bool hasRetry,
        CancellationToken cancellationToken)
    {
        if (hasRetry)
        {
            _logger.LogWarning(
                "song.link rate-limited (attempt {Attempt}/{MaxAttempts}); retrying in {Delay}s",
                attempt,
                MaxApiRetries,
                RetryDelay.TotalSeconds);
            await Task.Delay(RetryDelay, cancellationToken);
            return;
        }

        _logger.LogWarning(
            "song.link failed with 429 after {Attempts} attempts",
            attempt);
    }

    private async Task<SongLinkEnvelope?> ParseSongLinkPayloadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            _logger.LogWarning("song.link empty response body");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SongLinkEnvelope>(body, CaseInsensitiveJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "song.link JSON parse failed; response_length={ResponseLength}",
                body.Length);
            return null;
        }
    }

    private async Task<SongLinkResult> BuildSongLinkResultAsync(
        SongLinkEnvelope payload,
        CancellationToken cancellationToken)
    {
        var result = new SongLinkResult();
        PopulatePlatformUrls(result, payload.LinksByPlatform!);

        var deezerEntity = TryGetDeezerEntity(payload);
        PopulatePrimaryEntityMetadata(result, payload.EntitiesByUniqueId!, payload.EntityUniqueId);
        PopulateSpotifyId(result, payload);
        PopulateDeezerId(result, deezerEntity);
        await PopulateResultIsrcAsync(result, payload.EntitiesByUniqueId!, deezerEntity, cancellationToken);
        await PopulateQobuzUrlAsync(result, payload.LinksByPlatform!, cancellationToken);
        return result;
    }

    private static void PopulatePlatformUrls(
        SongLinkResult result,
        IReadOnlyDictionary<string, SongLinkPlatform> linksByPlatform)
    {
        result.TidalUrl = linksByPlatform.TryGetValue("tidal", out var tidal) ? tidal.Url : null;
        result.AmazonUrl = linksByPlatform.TryGetValue("amazonMusic", out var amazon) ? amazon.Url : null;
        result.DeezerUrl = linksByPlatform.TryGetValue(DeezerPlatform, out var deezer) ? deezer.Url : null;
        result.AppleMusicUrl = linksByPlatform.TryGetValue("appleMusic", out var apple) ? apple.Url : null;
        result.SpotifyUrl = linksByPlatform.TryGetValue(SpotifyPlatform, out var spotify) ? spotify.Url : null;
    }

    private static SongLinkEntity? TryGetDeezerEntity(SongLinkEnvelope payload)
    {
        if (!payload.LinksByPlatform!.TryGetValue(DeezerPlatform, out var deezerLink))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(deezerLink.EntityUniqueId))
        {
            return null;
        }

        return payload.EntitiesByUniqueId!.TryGetValue(deezerLink.EntityUniqueId, out var deezerEntity)
            ? deezerEntity
            : null;
    }

    private static void PopulatePrimaryEntityMetadata(
        SongLinkResult result,
        IReadOnlyDictionary<string, SongLinkEntity> entities,
        string? entityUniqueId)
    {
        if (string.IsNullOrWhiteSpace(entityUniqueId) || !entities.TryGetValue(entityUniqueId, out var primaryEntity))
        {
            return;
        }

        result.Isrc = primaryEntity.Isrc;
        result.SourceType = primaryEntity.Type;
        result.SourceTitle = primaryEntity.Title;
        result.SourceArtist = primaryEntity.ArtistName;
        if (string.Equals(primaryEntity.Platform, DeezerPlatform, StringComparison.OrdinalIgnoreCase))
        {
            result.DeezerId = ExtractId(primaryEntity.Platform, primaryEntity.Id, primaryEntity.Link);
        }
    }

    private static void PopulateSpotifyId(SongLinkResult result, SongLinkEnvelope payload)
    {
        if (TryExtractSpotifyId(payload, out var spotifyId))
        {
            result.SpotifyId = spotifyId;
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.SpotifyUrl))
        {
            result.SpotifyId = ExtractId(SpotifyPlatform, null, result.SpotifyUrl);
        }
    }

    private static bool TryExtractSpotifyId(SongLinkEnvelope payload, out string? spotifyId)
    {
        spotifyId = null;

        if (!payload.LinksByPlatform!.TryGetValue(SpotifyPlatform, out var spotifyLink)
            || string.IsNullOrWhiteSpace(spotifyLink.EntityUniqueId))
        {
            return false;
        }

        if (!payload.EntitiesByUniqueId!.TryGetValue(spotifyLink.EntityUniqueId, out var spotifyEntity))
        {
            return false;
        }

        spotifyId = ExtractId(SpotifyPlatform, spotifyEntity.Id, spotifyEntity.Link ?? spotifyLink.Url);
        return !string.IsNullOrWhiteSpace(spotifyId);
    }

    private static void PopulateDeezerId(SongLinkResult result, SongLinkEntity? deezerEntity)
    {
        if (!string.IsNullOrWhiteSpace(result.DeezerId))
        {
            return;
        }

        if (deezerEntity != null)
        {
            result.DeezerId = ExtractId(DeezerPlatform, deezerEntity.Id, deezerEntity.Link ?? result.DeezerUrl);
        }

        if (string.IsNullOrWhiteSpace(result.DeezerId) && !string.IsNullOrWhiteSpace(result.DeezerUrl))
        {
            result.DeezerId = ExtractId(DeezerPlatform, null, result.DeezerUrl);
        }
    }

    private async Task PopulateResultIsrcAsync(
        SongLinkResult result,
        IReadOnlyDictionary<string, SongLinkEntity> entitiesByUniqueId,
        SongLinkEntity? deezerEntity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.Isrc) && !string.IsNullOrWhiteSpace(deezerEntity?.Isrc))
        {
            result.Isrc = deezerEntity.Isrc;
        }

        if (string.IsNullOrWhiteSpace(result.Isrc))
        {
            result.Isrc = entitiesByUniqueId.Values.FirstOrDefault(static entity => !string.IsNullOrWhiteSpace(entity.Isrc))?.Isrc;
        }

        if (string.IsNullOrWhiteSpace(result.Isrc) && !string.IsNullOrWhiteSpace(result.DeezerId))
        {
            result.Isrc = await ResolveDeezerIsrcAsync(result.DeezerId, cancellationToken);
        }
    }

    private async Task PopulateQobuzUrlAsync(
        SongLinkResult result,
        IReadOnlyDictionary<string, SongLinkPlatform> linksByPlatform,
        CancellationToken cancellationToken)
    {
        result.QobuzUrl = linksByPlatform.TryGetValue("qobuz", out var qobuzLink) ? qobuzLink.Url : null;
        if (string.IsNullOrWhiteSpace(result.QobuzUrl) && !string.IsNullOrWhiteSpace(result.Isrc))
        {
            result.QobuzUrl = await ResolveQobuzUrlByIsrcAsync(result.Isrc, cancellationToken);
        }
    }

    private async Task<SongLinkResult> BuildDeezerOnlyResultAsync(string deezerTrackId, CancellationToken cancellationToken)
    {
        return new SongLinkResult
        {
            DeezerId = deezerTrackId,
            DeezerUrl = $"https://www.deezer.com/track/{deezerTrackId}",
            Isrc = await ResolveDeezerIsrcAsync(deezerTrackId, cancellationToken)
        };
    }


    public Task<SongLinkResult?> ResolveByDeezerTrackIdAsync(string deezerTrackId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deezerTrackId))
        {
            return Task.FromResult<SongLinkResult?>(null);
        }

        var deezerUrl = $"https://www.deezer.com/track/{deezerTrackId}";
        return ResolveByUrlAsync(deezerUrl, cancellationToken);
    }

    public async Task<string?> ResolveQobuzUrlByIsrcAsync(string isrc, CancellationToken cancellationToken)
    {
        var resolverResult = await TryResolveQobuzUrlViaResolverAsync(isrc, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolverResult))
        {
            return resolverResult;
        }

        var metadataResult = await TryResolveQobuzUrlViaMetadataServiceAsync(isrc, cancellationToken);
        if (!string.IsNullOrWhiteSpace(metadataResult))
        {
            return metadataResult;
        }

        return await TryResolveQobuzUrlViaPublicSearchAsync(isrc, cancellationToken);
    }

    private async Task<string?> TryResolveQobuzUrlViaResolverAsync(string isrc, CancellationToken cancellationToken)
    {
        try
        {
            if (_qobuzTrackResolver == null)
            {
                return null;
            }

            var resolved = await _qobuzTrackResolver.ResolveTrackUrlAsync(
                isrc,
                title: null,
                artist: null,
                album: null,
                durationMs: null,
                cancellationToken);
            return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Qobuz resolver lookup failed for {Isrc}", isrc);            }
            return null;
        }
    }

    private async Task<string?> TryResolveQobuzUrlViaMetadataServiceAsync(string isrc, CancellationToken cancellationToken)
    {
        try
        {
            if (_qobuzMetadataService == null)
            {
                return null;
            }

            var track = await _qobuzMetadataService.FindTrackByISRC(isrc, cancellationToken);
            if (track != null && track.Id > 0)
            {
                return $"https://play.qobuz.com/track/{track.Id}";
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Qobuz metadata ISRC lookup failed for {Isrc}", isrc);            }
            return null;
        }
    }

    private async Task<string?> TryResolveQobuzUrlViaPublicSearchAsync(string isrc, CancellationToken cancellationToken)
    {
        try
        {
            var searchUrl = $"https://www.qobuz.com/api.json/0.2/track/search?query={WebUtility.UrlEncode(isrc)}&limit=1&app_id=798273057";
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(searchUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!TryGetQobuzTrackItems(doc.RootElement, out var items) || items.GetArrayLength() == 0)
            {
                return null;
            }

            var first = items[0];
            return TryExtractQobuzTrackId(first, out var trackId)
                ? $"https://play.qobuz.com/track/{trackId}"
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Qobuz public ISRC lookup failed for {Isrc}", isrc);            }
            return null;
        }
    }

    private async Task<string?> ResolveDeezerIsrcAsync(string deezerTrackId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deezerTrackId))
        {
            return null;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync($"https://api.deezer.com/track/{WebUtility.UrlEncode(deezerTrackId)}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<DeezerTrackEnvelope>(
                stream,
                CaseInsensitiveJsonOptions,
                cancellationToken);
            return string.IsNullOrWhiteSpace(payload?.Isrc) ? null : payload.Isrc;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer ISRC lookup failed for track {TrackId}", deezerTrackId);            }
            return null;
        }
    }

    public async Task<string?> ResolveQobuzUrlByMetadataAsync(
        string title,
        string artist,
        int? durationMs,
        CancellationToken cancellationToken)
    {
        if (_qobuzMetadataService == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var expectedDuration = durationMs.HasValue && durationMs.Value > 0
            ? (int)Math.Round(durationMs.Value / 1000d)
            : 0;
        var queries = BuildQobuzQueries(title, artist);

        var resolved = await TryResolveQobuzUrlWithMetadataStrategyAsync(title, artist, durationMs, expectedDuration, queries, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        resolved = await TryResolveQobuzUrlByCandidateSearchAsync(
            title,
            artist,
            expectedDuration,
            "autosuggest lookup",
            () => SearchQobuzAutosuggestAcrossStoresAsync(queries, cancellationToken));
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        return await TryResolveQobuzUrlByCandidateSearchAsync(
            title,
            artist,
            expectedDuration,
            "public search",
            () => SearchQobuzPublicByQueriesAsync(queries, cancellationToken));
    }

    private async Task<string?> TryResolveQobuzUrlWithMetadataStrategyAsync(
        string title,
        string artist,
        int? durationMs,
        int expectedDuration,
        IReadOnlyList<string> queries,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_qobuzTrackResolver != null)
            {
                var resolved = await _qobuzTrackResolver.ResolveTrackUrlAsync(
                    isrc: null,
                    title,
                    artist,
                    album: null,
                    durationMs,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }

            var candidates = await SearchQobuzByQueriesAsync(queries, cancellationToken);
            return BuildQobuzTrackUrl(PickBestQobuzCandidate(candidates, title, artist, expectedDuration));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Qobuz metadata lookup failed for {Title} - {Artist}", title, artist);
            }

            return null;
        }
    }

    private async Task<string?> TryResolveQobuzUrlByCandidateSearchAsync(
        string title,
        string artist,
        int expectedDuration,
        string strategyLabel,
        Func<Task<List<QobuzTrack>>> candidateFactory)
    {
        try
        {
            var candidates = await candidateFactory();
            return BuildQobuzTrackUrl(PickBestQobuzCandidate(candidates, title, artist, expectedDuration));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Qobuz {Strategy} failed for {Title} - {Artist}", strategyLabel, title, artist);
            }

            return null;
        }
    }

    private static string? BuildQobuzTrackUrl(long? bestId)
    {
        return bestId.HasValue
            ? $"https://play.qobuz.com/track/{bestId.Value}"
            : null;
    }

    private static string? ExtractId(string? platform, string? entityId, string? link)
    {
        if (!string.IsNullOrWhiteSpace(entityId))
        {
            return entityId;
        }

        if (!string.IsNullOrWhiteSpace(link))
        {
            var last = link.Split('/').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(last))
            {
                var queryTrimmed = last.Split(QueryAndFragmentSeparators, StringSplitOptions.None)[0];
                if (!string.IsNullOrWhiteSpace(queryTrimmed))
                {
                    return queryTrimmed;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            var parts = platform.Split(':');
            if (parts.Length >= 3)
            {
                return parts[^1];
            }
        }

        return null;
    }

    private static int ScoreQobuzCandidate(QobuzTrack candidate, string title, string artist, int expectedDurationSec)
    {
        var score = 0;
        var candidateTitle = candidate.Title ?? string.Empty;
        var candidateArtist = GetQobuzTrackArtist(candidate);

        if (QobuzTitlesMatch(title, candidateTitle))
        {
            score += 6;
        }

        if (QobuzArtistsMatch(artist, candidateArtist))
        {
            score += 4;
        }

        if (expectedDurationSec > 0 && candidate.Duration > 0)
        {
            var delta = Math.Abs(candidate.Duration - expectedDurationSec);
            if (delta <= 3)
            {
                score += 3;
            }
            else if (delta <= 10)
            {
                score += 1;
            }
        }

        if (candidate.MaximumBitDepth >= 24)
        {
            score += 2;
        }

        if (candidate.MaximumSamplingRate >= 96)
        {
            score += 1;
        }

        return score;
    }

    private static string GetQobuzTrackArtist(QobuzTrack track)
    {
        if (track.Performer?.Name is { Length: > 0 } performerName)
        {
            return performerName;
        }

        var albumArtist = track.Album?.Artists?.FirstOrDefault()?.Name;
        if (!string.IsNullOrWhiteSpace(albumArtist))
        {
            return albumArtist;
        }

        return string.Empty;
    }

    private long? PickBestQobuzCandidate(IEnumerable<QobuzTrack> candidates, string title, string artist, int expectedDurationSec)
    {
        var materialized = MaterializeValidQobuzCandidates(candidates);
        if (materialized.Count == 0)
        {
            return null;
        }

        var (pool, hasTitleMatches) = BuildQobuzCandidatePool(materialized, title, expectedDurationSec);
        var (best, bestScore) = SelectBestQobuzCandidate(pool, title, artist, expectedDurationSec);

        if (!PassesStrictQobuzFallback(best, title, artist))
        {
            return null;
        }

        var minScore = hasTitleMatches ? 6 : 4;
        if (best != null && bestScore >= minScore && best.Id > 0)
        {
            return best.Id;
        }

        LogLowConfidenceQobuzCandidate(best, bestScore);

        return null;
    }

    private static List<QobuzTrack> MaterializeValidQobuzCandidates(IEnumerable<QobuzTrack>? candidates)
    {
        if (candidates == null)
        {
            return new List<QobuzTrack>();
        }

        return candidates.Where(static c => c != null && c.Id > 0).ToList();
    }

    private static (List<QobuzTrack> Pool, bool HasTitleMatches) BuildQobuzCandidatePool(
        List<QobuzTrack> candidates,
        string title,
        int expectedDurationSec)
    {
        var titleMatches = candidates
            .Where(c => QobuzTitlesMatch(title, c.Title ?? string.Empty))
            .ToList();

        var pool = titleMatches.Count > 0 ? titleMatches : candidates;
        if (expectedDurationSec > 0)
        {
            var durationMatches = pool
                .Where(c => c.Duration > 0 && Math.Abs(c.Duration - expectedDurationSec) <= 10)
                .ToList();
            if (durationMatches.Count > 0)
            {
                pool = durationMatches;
            }
        }

        return (pool, titleMatches.Count > 0);
    }

    private static (QobuzTrack? Best, int BestScore) SelectBestQobuzCandidate(
        IEnumerable<QobuzTrack> candidates,
        string title,
        string artist,
        int expectedDurationSec)
    {
        QobuzTrack? best = null;
        var bestScore = 0;

        foreach (var candidate in candidates)
        {
            var score = ScoreQobuzCandidate(candidate, title, artist, expectedDurationSec);
            if (score <= bestScore)
            {
                continue;
            }

            best = candidate;
            bestScore = score;
        }

        return (best, bestScore);
    }

    private bool PassesStrictQobuzFallback(QobuzTrack? best, string title, string artist)
    {
        if (!_qobuzConfig.StrictMatchFallback)
        {
            return true;
        }

        if (best == null)
        {
            return false;
        }

        var strictTitleMatch = QobuzTitlesMatch(title, best.Title ?? string.Empty);
        var strictArtistMatch = QobuzArtistsMatch(artist, GetQobuzTrackArtist(best));
        return strictTitleMatch && strictArtistMatch;
    }

    private void LogLowConfidenceQobuzCandidate(QobuzTrack? best, int bestScore)
    {
        if (best == null)
        {
            return;
        }

        var bestArtist = GetQobuzTrackArtist(best);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Qobuz best score {Score} (candidate_duration={CandDur}s, candidate_artist_present={HasArtist})",
                bestScore,
                best.Duration,
                !string.IsNullOrWhiteSpace(bestArtist));        }
    }

    private static List<string> BuildQobuzQueries(string title, string artist)
    {
        var queries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
        {
            AddQuery($"{artist} {title}", seen, queries);
        }

        AddQuery(title, seen, queries);
        AddJapaneseRomajiQueries(title, artist, seen, queries);
        AddQuery(artist, seen, queries);

        return queries;
    }

    private static void AddJapaneseRomajiQueries(
        string title,
        string artist,
        HashSet<string> seen,
        List<string> queries)
    {
        if (!QobuzRomajiHelper.ContainsJapanese(title) && !QobuzRomajiHelper.ContainsJapanese(artist))
        {
            return;
        }

        var romajiTitle = QobuzRomajiHelper.CleanToAscii(QobuzRomajiHelper.JapaneseToRomaji(title));
        var romajiArtist = QobuzRomajiHelper.CleanToAscii(QobuzRomajiHelper.JapaneseToRomaji(artist));
        if (!string.IsNullOrWhiteSpace(romajiArtist) && !string.IsNullOrWhiteSpace(romajiTitle))
        {
            AddQuery($"{romajiArtist} {romajiTitle}", seen, queries);
        }

        if (!string.IsNullOrWhiteSpace(romajiTitle) &&
            !string.Equals(romajiTitle, title, StringComparison.OrdinalIgnoreCase))
        {
            AddQuery(romajiTitle, seen, queries);
        }

        AddQuery(romajiArtist, seen, queries);
    }

    private static void AddQuery(string? value, HashSet<string> seen, List<string> queries)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (seen.Add(trimmed))
        {
            queries.Add(trimmed);
        }
    }

    private async Task<List<QobuzTrack>> SearchQobuzByQueriesAsync(
        IEnumerable<string> queries,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<int, QobuzTrack>();
        foreach (var query in queries)
        {
            var response = await _qobuzMetadataService!.SearchTracks(query, cancellationToken);
            foreach (var track in response.Where(static track => track.Id > 0))
            {
                results[track.Id] = track;
            }
        }

        return results.Values.ToList();
    }

    private async Task<List<QobuzTrack>> SearchQobuzAutosuggestAcrossStoresAsync(
        IEnumerable<string> queries,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<int, QobuzTrack>();
        var stores = _qobuzConfig.PreferredStores?.Count > 0
            ? _qobuzConfig.PreferredStores
            : new List<string> { _qobuzConfig.DefaultStore };

        foreach (var query in queries)
        {
            foreach (var store in stores.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var response = await _qobuzMetadataService!.SearchTracksAutosuggest(query, store, cancellationToken);
                foreach (var track in response.Where(static track => track.Id > 0))
                {
                    results[track.Id] = track;
                }
            }
        }

        return results.Values.ToList();
    }

    private async Task<List<QobuzTrack>> SearchQobuzPublicByQueriesAsync(
        IEnumerable<string> queries,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<int, QobuzTrack>();
        foreach (var query in queries)
        {
            var response = await SearchQobuzPublicAsync(query, cancellationToken);
            foreach (var track in response.Where(static track => track.Id > 0))
            {
                results[track.Id] = track;
            }
        }

        return results.Values.ToList();
    }

    private static bool QobuzArtistsMatch(string expectedArtist, string foundArtist)
    {
        var normExpected = expectedArtist.Trim().ToLowerInvariant();
        var normFound = foundArtist.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normExpected) || string.IsNullOrWhiteSpace(normFound))
        {
            return false;
        }

        if (HasDirectArtistMatch(normExpected, normFound))
        {
            return true;
        }

        if (HasSplitArtistMatch(normExpected, normFound))
        {
            return true;
        }

        return QobuzIsLatinScript(expectedArtist) != QobuzIsLatinScript(foundArtist);
    }

    private static bool HasDirectArtistMatch(string expected, string found)
    {
        return expected == found
            || expected.Contains(found, StringComparison.Ordinal)
            || found.Contains(expected, StringComparison.Ordinal);
    }

    private static bool HasSplitArtistMatch(string expected, string found)
    {
        var expectedArtists = QobuzSplitArtists(expected);
        var foundArtists = QobuzSplitArtists(found);
        return expectedArtists.Any(expectedArtist =>
            foundArtists.Any(foundArtist =>
                HasDirectArtistMatch(expectedArtist, foundArtist)
                || QobuzSameWordsUnordered(expectedArtist, foundArtist)));
    }

    private static List<string> QobuzSplitArtists(string artists)
    {
        var normalized = artists
            .Replace(" feat. ", "|", StringComparison.Ordinal)
            .Replace(" feat ", "|", StringComparison.Ordinal)
            .Replace(" ft. ", "|", StringComparison.Ordinal)
            .Replace(" ft ", "|", StringComparison.Ordinal)
            .Replace(" & ", "|", StringComparison.Ordinal)
            .Replace(" and ", "|", StringComparison.Ordinal)
            .Replace(", ", "|", StringComparison.Ordinal)
            .Replace(" x ", "|", StringComparison.Ordinal);

        return normalized.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static bool QobuzSameWordsUnordered(string a, string b)
    {
        var wordsA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wordsB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (wordsA.Length == 0 || wordsA.Length != wordsB.Length)
        {
            return false;
        }

        Array.Sort(wordsA, StringComparer.Ordinal);
        Array.Sort(wordsB, StringComparer.Ordinal);

        for (var i = 0; i < wordsA.Length; i++)
        {
            if (!string.Equals(wordsA[i], wordsB[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool QobuzTitlesMatch(string expectedTitle, string foundTitle)
    {
        var normExpected = expectedTitle.Trim().ToLowerInvariant();
        var normFound = foundTitle.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normExpected) || string.IsNullOrWhiteSpace(normFound))
        {
            return false;
        }

        if (normExpected == normFound)
        {
            return true;
        }

        if (normExpected.Contains(normFound, StringComparison.Ordinal) ||
            normFound.Contains(normExpected, StringComparison.Ordinal))
        {
            return true;
        }

        var cleanExpected = QobuzCleanTitle(normExpected);
        var cleanFound = QobuzCleanTitle(normFound);

        if (cleanExpected == cleanFound)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(cleanExpected)
            && !string.IsNullOrWhiteSpace(cleanFound)
            && (cleanExpected.Contains(cleanFound, StringComparison.Ordinal)
                || cleanFound.Contains(cleanExpected, StringComparison.Ordinal)))
        {
            return true;
        }

        var coreExpected = QobuzTitleHelpers.ExtractCoreTitle(normExpected);
        var coreFound = QobuzTitleHelpers.ExtractCoreTitle(normFound);
        if (!string.IsNullOrWhiteSpace(coreExpected) && coreExpected == coreFound)
        {
            return true;
        }

        var expectedLatin = QobuzIsLatinScript(expectedTitle);
        var foundLatin = QobuzIsLatinScript(foundTitle);
        if (expectedLatin != foundLatin)
        {
            return true;
        }

        return false;
    }

    private static string QobuzCleanTitle(string title)
    {
        var cleaned = title;
        cleaned = RemoveTrailingVersionSection(cleaned, '(', ')');
        cleaned = RemoveTrailingVersionSection(cleaned, '[', ']');
        cleaned = RemoveDashVersionSuffixes(cleaned);

        while (cleaned.Contains("  ", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        }

        return cleaned.Trim();
    }

    private static string RemoveTrailingVersionSection(string value, char startChar, char endChar)
    {
        var cleaned = value;
        while (true)
        {
            var startIdx = cleaned.LastIndexOf(startChar);
            var endIdx = cleaned.LastIndexOf(endChar);
            if (startIdx < 0 || endIdx <= startIdx)
            {
                break;
            }

            var content = cleaned[(startIdx + 1)..endIdx].ToLowerInvariant();
            if (!QobuzVersionPatterns.Any(p => content.Contains(p, StringComparison.Ordinal)))
            {
                break;
            }

            cleaned = (cleaned[..startIdx] + cleaned[(endIdx + 1)..]).Trim();
        }

        return cleaned;
    }

    private static string RemoveDashVersionSuffixes(string value)
    {
        var cleaned = value;
        while (true)
        {
            var matchingSuffixLength = QobuzDashPatterns
                .Where(pattern => cleaned.EndsWith(pattern, StringComparison.OrdinalIgnoreCase))
                .Select(pattern => pattern.Length)
                .FirstOrDefault();
            if (matchingSuffixLength == 0)
            {
                return cleaned;
            }

            cleaned = cleaned[..^matchingSuffixLength];
        }
    }

    private static bool QobuzIsLatinScript(string value)
    {
        foreach (var r in value)
        {
            if (r < 128)
            {
                continue;
            }

            if (IsExtendedLatinCharacter(r))
            {
                continue;
            }

            if (IsNonLatinScriptCharacter(r))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsExtendedLatinCharacter(int codePoint)
    {
        return IsInAnyRange(codePoint, ExtendedLatinRanges);
    }

    private static bool IsNonLatinScriptCharacter(int codePoint)
    {
        return IsInAnyRange(codePoint, NonLatinScriptRanges);
    }

    private static bool IsInAnyRange(int codePoint, (int Start, int End)[] ranges)
    {
        return ranges.Any(range => codePoint >= range.Start && codePoint <= range.End);
    }

    private async Task<List<QobuzTrack>> SearchQobuzPublicAsync(string query, CancellationToken cancellationToken)
    {
        var searchUrl = $"https://www.qobuz.com/api.json/0.2/track/search?query={WebUtility.UrlEncode(query)}&limit=25&app_id=798273057";
        using var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync(searchUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<QobuzTrack>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!TryGetQobuzTrackItems(doc.RootElement, out var items))
        {
            return new List<QobuzTrack>();
        }

        var results = new List<QobuzTrack>();
        foreach (var item in items.EnumerateArray())
        {
            var track = ParseQobuzPublicTrack(item);
            if (track == null)
            {
                continue;
            }

            results.Add(track);
        }

        if (results.Count == 0 && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Qobuz public search returned 0 candidates for {Query}", query);
        }

        return results;
    }

    private static bool TryGetQobuzTrackItems(JsonElement rootElement, out JsonElement items)
    {
        items = default;
        if (!rootElement.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (tracks.TryGetProperty("total", out var totalElement)
            && totalElement.ValueKind == JsonValueKind.Number
            && totalElement.GetInt32() <= 0)
        {
            return false;
        }

        return tracks.TryGetProperty("items", out items) && items.ValueKind == JsonValueKind.Array;
    }

    private static QobuzTrack? ParseQobuzPublicTrack(JsonElement item)
    {
        if (!TryExtractQobuzTrackId(item, out var trackId))
        {
            return null;
        }

        return new QobuzTrack
        {
            Id = trackId,
            Title = item.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
                ? titleEl.GetString()
                : null,
            Duration = item.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number
                ? durEl.GetInt32()
                : 0,
            Performer = new QobuzArtist
            {
                Name = ExtractQobuzArtistName(item)
            }
        };
    }

    private static bool TryExtractQobuzTrackId(JsonElement item, out int trackId)
    {
        trackId = 0;
        if (!item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        trackId = idElement.GetInt32();
        return trackId > 0;
    }

    private static string? ExtractQobuzArtistName(JsonElement item)
    {
        if (item.TryGetProperty("performer", out var performer)
            && performer.ValueKind == JsonValueKind.Object
            && performer.TryGetProperty("name", out var perfName)
            && perfName.ValueKind == JsonValueKind.String)
        {
            return perfName.GetString();
        }

        if (item.TryGetProperty("artist", out var artist)
            && artist.ValueKind == JsonValueKind.Object
            && artist.TryGetProperty("name", out var artName)
            && artName.ValueKind == JsonValueKind.String)
        {
            return artName.GetString();
        }

        if (item.TryGetProperty("album", out var album)
            && album.ValueKind == JsonValueKind.Object
            && album.TryGetProperty("artist", out var albumArtist)
            && albumArtist.ValueKind == JsonValueKind.Object
            && albumArtist.TryGetProperty("name", out var albumArtistName)
            && albumArtistName.ValueKind == JsonValueKind.String)
        {
            return albumArtistName.GetString();
        }

        return null;
    }

    private async Task EnforceRateLimitAsync(CancellationToken cancellationToken)
    {
        await _rateGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _windowStart >= TimeSpan.FromMinutes(1))
            {
                _windowStart = now;
                _windowCount = 0;
            }

            if (_windowCount >= MaxRequestsPerMinute)
            {
                var wait = TimeSpan.FromMinutes(1) - (now - _windowStart);
                if (wait > TimeSpan.Zero)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("song.link rate limit hit; waiting {Delay}", wait);                    }
                    await Task.Delay(wait, cancellationToken);
                    _windowStart = DateTimeOffset.UtcNow;
                    _windowCount = 0;
                }
            }

            if (_lastCall != DateTimeOffset.MinValue)
            {
                var since = now - _lastCall;
                if (since < MinDelay)
                {
                    var wait = MinDelay - since;
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("song.link spacing delay {Delay}", wait);                    }
                    await Task.Delay(wait, cancellationToken);
                }
            }

            _lastCall = DateTimeOffset.UtcNow;
            _windowCount++;
        }
        finally
        {
            _rateGate.Release();
        }
    }

    private sealed record SongLinkEnvelope(
        string? EntityUniqueId,
        Dictionary<string, SongLinkPlatform>? LinksByPlatform,
        Dictionary<string, SongLinkEntity>? EntitiesByUniqueId);

    private sealed record SongLinkPlatform(string? Url, string? EntityUniqueId);

    private sealed record SongLinkEntity(
        string? Id,
        string? Platform,
        string? Type,
        string? Title,
        string? ArtistName,
        string? Link,
        string? Isrc,
        string? ThumbnailUrl);

    private sealed record DeezerTrackEnvelope(string? Isrc);

    private sealed record CacheEntry(DateTimeOffset Stamp, SongLinkResult? Result);

    private bool TryGetFromCache(string url, string? userCountry, out SongLinkResult? result)
    {
        result = null;
        var key = BuildCacheKey(url, userCountry);
        if (!_cache.TryGetValue(key, out var entry))
        {
            return false;
        }

        var ttl = entry.Result is null ? NegativeCacheTtl : CacheTtl;
        if (DateTimeOffset.UtcNow - entry.Stamp > ttl)
        {
            _cache.TryRemove(key, out _);
            return false;
        }

        result = entry.Result;
        return true;
    }

    private void CacheResult(string url, string? userCountry, SongLinkResult? result)
    {
        var key = BuildCacheKey(url, userCountry);
        _cache[key] = new CacheEntry(DateTimeOffset.UtcNow, result);
    }

    private static string BuildCacheKey(string url, string? userCountry)
    {
        var country = userCountry?.Trim().ToUpperInvariant() ?? string.Empty;
        return $"{country}|{url.Trim()}";
    }

    private static string NormalizeCacheUrl(string url)
    {
        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed;
        }

        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Fragment = string.Empty
        };
        if ((builder.Scheme == "http" && builder.Port == 80)
            || (builder.Scheme == "https" && builder.Port == 443))
        {
            builder.Port = -1;
        }

        var path = builder.Path;
        if (!string.IsNullOrEmpty(path) && path.Length > 1)
        {
            builder.Path = path.TrimEnd('/');
        }

        return builder.Uri.AbsoluteUri;
    }

    private sealed record SongLinkRequestOutcome(SongLinkEnvelope? Payload, bool SkipNegativeCache);
}

public sealed class SongLinkResult
{
    public string? TidalUrl { get; set; }
    public string? AmazonUrl { get; set; }
    public string? QobuzUrl { get; set; }
    public string? DeezerUrl { get; set; }
    public string? DeezerId { get; set; }
    public string? AppleMusicUrl { get; set; }
    public string? SpotifyUrl { get; set; }
    public string? SpotifyId { get; set; }
    public string? Isrc { get; set; }
    public string? SourceType { get; set; }
    public string? SourceTitle { get; set; }
    public string? SourceArtist { get; set; }
}
