using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeezSpoTag.Web.Services;

public sealed class LastFmTagService
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly IConfiguration _configuration;
    private readonly PlatformAuthService _authService;
    private readonly ILogger<LastFmTagService> _logger;
    private string? _cachedApiKey;
    private DateTimeOffset _cachedApiKeyLoadedAtUtc;
    private static readonly TimeSpan ApiKeyCacheTtl = TimeSpan.FromSeconds(5);

    // In-memory cache with TTL
    private static readonly TimeSpan TagCacheTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan SimilarCacheTtl = TimeSpan.FromDays(7);
    private readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<string>?>> _tagCache = new();
    private readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<string>?>> _similarArtistCache = new();
    private readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<LastFmSimilarTrack>?>> _similarTrackCache = new();

    private static readonly HashSet<string> JunkTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "seen live"
    };

    public LastFmTagService(
        IHttpClientFactory clientFactory,
        IConfiguration configuration,
        PlatformAuthService authService,
        ILogger<LastFmTagService> logger)
    {
        _clientFactory = clientFactory;
        _configuration = configuration;
        _authService = authService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>?> GetTrackTagsAsync(string artistName, string trackTitle, CancellationToken cancellationToken)
    {
        var cacheKey = $"tags:{NormalizeCacheKey(artistName)}:{NormalizeCacheKey(trackTitle)}";
        if (_tagCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
        {
            return cached.Value;
        }

        var apiKey = await ResolveApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        try
        {
            var client = _clientFactory.CreateClient();
            var uri = $"https://ws.audioscrobbler.com/2.0/?method=track.gettoptags&artist={Uri.EscapeDataString(artistName)}&track={Uri.EscapeDataString(trackTitle)}&api_key={Uri.EscapeDataString(apiKey)}&format=json&autocorrect=1";
            var response = await client.GetFromJsonAsync<LastFmTrackTagsResponse>(uri, cancellationToken);
            if (response is null)
            {
                return null;
            }

            if (response.Error.HasValue)
            {
                // Last.fm sometimes returns HTTP 200 with an error payload. Don't poison the cache for auth/transient errors.
                var cacheable = IsCacheableLastFmError(response.Error.Value);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Last.fm track tags returned error {Error}: {Message} (cacheable={Cacheable}) for {Artist} - {Track}",
                        response.Error,
                        response.Message,
                        cacheable,
                        artistName,
                        trackTitle);
                }

                if (!cacheable)
                {
                    // Force key re-resolution on next attempt (logout / key rotation / etc).
                    _cachedApiKey = null;
                    _cachedApiKeyLoadedAtUtc = DateTimeOffset.MinValue;
                    return null;
                }

                _tagCache[cacheKey] = new CacheEntry<IReadOnlyList<string>?>(null, TagCacheTtl);
                return null;
            }

            var tags = response.Toptags?.Tag?
                .Select(tag => new { Name = NormalizeDisplayValue(tag.Name), Count = tag.Count })
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .Where(entry => !IsJunkTag(entry.Name!))
                .OrderByDescending(entry => entry.Count ?? 0)
                .Select(entry => entry.Name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();

            var result = tags?.Count > 0 ? tags : null;
            _tagCache[cacheKey] = new CacheEntry<IReadOnlyList<string>?>(result, TagCacheTtl);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Last.fm tag lookup failed for {Artist} - {Track}", artistName, trackTitle);
            return null;
        }
    }

    public async Task<IReadOnlyList<string>?> GetSimilarArtistsAsync(string artistName, int limit = 30, CancellationToken cancellationToken = default)
    {
        if (IsInvalidArtistName(artistName))
        {
            return null;
        }

        var cacheKey = $"similar-artist:{NormalizeCacheKey(artistName)}";
        if (_similarArtistCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
        {
            return cached.Value;
        }

        var apiKey = await ResolveApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        try
        {
            var client = _clientFactory.CreateClient();
            var uri = $"https://ws.audioscrobbler.com/2.0/?method=artist.getsimilar&artist={Uri.EscapeDataString(artistName)}&limit={limit}&api_key={Uri.EscapeDataString(apiKey)}&format=json&autocorrect=1";
            var response = await client.GetFromJsonAsync<LastFmSimilarArtistsResponse>(uri, cancellationToken);
            if (response is null)
            {
                return null;
            }

            if (response.Error.HasValue)
            {
                var cacheable = IsCacheableLastFmError(response.Error.Value);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Last.fm similar artists returned error {Error}: {Message} (cacheable={Cacheable}) for {Artist}",
                        response.Error,
                        response.Message,
                        cacheable,
                        artistName);
                }

                if (!cacheable)
                {
                    _cachedApiKey = null;
                    _cachedApiKeyLoadedAtUtc = DateTimeOffset.MinValue;
                    return null;
                }

                _similarArtistCache[cacheKey] = new CacheEntry<IReadOnlyList<string>?>(null, SimilarCacheTtl);
                return null;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var names = response.Similarartists?.Artist?
                .Select(a => new { Name = NormalizeDisplayValue(a.Name), Match = a.Match })
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .Where(a => !IsInvalidArtistName(a.Name))
                .Where(a => a.Match is null || a.Match >= 0.15d)
                .Select(a => a.Name!)
                .Where(name => seen.Add(NormalizeKey(name)))
                .Take(limit)
                .ToList();

            var result = names?.Count > 0 ? names : null;
            _similarArtistCache[cacheKey] = new CacheEntry<IReadOnlyList<string>?>(result, SimilarCacheTtl);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Last.fm similar artists lookup failed for {Artist}", artistName);
            return null;
        }
    }

    public async Task<IReadOnlyList<LastFmSimilarTrack>?> GetSimilarTracksAsync(string artistName, string trackName, int limit = 20, CancellationToken cancellationToken = default)
    {
        if (IsInvalidArtistName(artistName))
        {
            return null;
        }

        var cacheKey = $"similar-track:{NormalizeCacheKey(artistName)}:{NormalizeCacheKey(trackName)}";
        if (_similarTrackCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
        {
            return cached.Value;
        }

        var apiKey = await ResolveApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        try
        {
            var client = _clientFactory.CreateClient();
            var uri = $"https://ws.audioscrobbler.com/2.0/?method=track.getsimilar&artist={Uri.EscapeDataString(artistName)}&track={Uri.EscapeDataString(trackName)}&limit={limit}&api_key={Uri.EscapeDataString(apiKey)}&format=json&autocorrect=1";
            var response = await client.GetFromJsonAsync<LastFmSimilarTracksResponse>(uri, cancellationToken);
            if (response is null)
            {
                return null;
            }

            if (response.Error.HasValue)
            {
                var cacheable = IsCacheableLastFmError(response.Error.Value);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Last.fm similar tracks returned error {Error}: {Message} (cacheable={Cacheable}) for {Artist} - {Track}",
                        response.Error,
                        response.Message,
                        cacheable,
                        artistName,
                        trackName);
                }

                if (!cacheable)
                {
                    _cachedApiKey = null;
                    _cachedApiKeyLoadedAtUtc = DateTimeOffset.MinValue;
                    return null;
                }

                _similarTrackCache[cacheKey] = new CacheEntry<IReadOnlyList<LastFmSimilarTrack>?>(null, SimilarCacheTtl);
                return null;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var tracks = response.Similartracks?.Track?
                .Where(t => !string.IsNullOrWhiteSpace(t.Name) && t.Artist is not null && !IsInvalidArtistName(t.Artist!.Name))
                .Where(t => (t.Match ?? 0d) >= 0.15d)
                .Select(t => new LastFmSimilarTrack(NormalizeDisplayValue(t.Name), NormalizeDisplayValue(t.Artist!.Name), t.Match ?? 0d))
                .Where(t => !string.IsNullOrWhiteSpace(t.TrackName) && !string.IsNullOrWhiteSpace(t.ArtistName))
                .Where(t => seen.Add(NormalizeKey($"{t.ArtistName}:{t.TrackName}")))
                .Take(limit)
                .ToList();

            var result = tracks?.Count > 0 ? tracks : null;
            _similarTrackCache[cacheKey] = new CacheEntry<IReadOnlyList<LastFmSimilarTrack>?>(result, SimilarCacheTtl);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Last.fm similar tracks lookup failed for {Artist} - {Track}", artistName, trackName);
            return null;
        }
    }

    private async Task<string?> ResolveApiKeyAsync()
    {
        var configKey = _configuration["Lastfm:ApiKey"];
        if (!string.IsNullOrWhiteSpace(configKey))
        {
            _cachedApiKey = configKey;
            return configKey;
        }

        if (!string.IsNullOrWhiteSpace(_cachedApiKey) && DateTimeOffset.UtcNow - _cachedApiKeyLoadedAtUtc <= ApiKeyCacheTtl)
        {
            return _cachedApiKey;
        }

        var state = await _authService.LoadAsync();
        _cachedApiKey = state.LastFm?.ApiKey;
        _cachedApiKeyLoadedAtUtc = DateTimeOffset.UtcNow;
        return _cachedApiKey;
    }

    private static string NormalizeDisplayValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        return string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeCacheKey(string? value)
    {
        return NormalizeKey(value);
    }

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsInvalidArtistName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var normalized = name.Trim().ToLowerInvariant();
        return normalized is "unknown" or "various artists";
    }

    private static bool IsJunkTag(string tag)
    {
        return JunkTags.Contains(tag.Trim());
    }

    private static bool IsCacheableLastFmError(int errorCode)
    {
        // Cache "not found / invalid params" errors to avoid hammering Last.fm for content that doesn't exist there.
        // Do NOT cache auth/transient errors (invalid key, rate limits, temporary errors), since that would hide recovery.
        return errorCode is 6 or 7;
    }

    // --- Cache infrastructure ---

    private sealed class CacheEntry<T>
    {
        public T Value { get; }
        private readonly DateTimeOffset _expiresAt;
        public bool IsExpired => DateTimeOffset.UtcNow >= _expiresAt;

        public CacheEntry(T value, TimeSpan ttl)
        {
            Value = value;
            _expiresAt = DateTimeOffset.UtcNow.Add(ttl);
        }
    }

    // --- Last.fm response models ---

    private sealed class LastFmTrackTagsResponse
    {
        [JsonPropertyName("toptags")]
        public LastFmTagsContainer? Toptags { get; init; }

        [JsonPropertyName("error")]
        public int? Error { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }

    private sealed class LastFmTagsContainer
    {
        [JsonPropertyName("tag")]
        public List<LastFmTag>? Tag { get; init; }
    }

    private sealed class LastFmTag
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("count")]
        [JsonConverter(typeof(FlexibleNullableIntConverter))]
        public int? Count { get; init; }
    }

    private sealed class LastFmSimilarArtistsResponse
    {
        [JsonPropertyName("similarartists")]
        public LastFmSimilarArtistsContainer? Similarartists { get; init; }

        [JsonPropertyName("error")]
        public int? Error { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }

    private sealed class LastFmSimilarArtistsContainer
    {
        [JsonPropertyName("artist")]
        public List<LastFmArtistEntry>? Artist { get; init; }
    }

    private sealed class LastFmArtistEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("match")]
        [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
        public double? Match { get; init; }
    }

    private sealed class LastFmSimilarTracksResponse
    {
        [JsonPropertyName("similartracks")]
        public LastFmSimilarTracksContainer? Similartracks { get; init; }

        [JsonPropertyName("error")]
        public int? Error { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }

    private sealed class LastFmSimilarTracksContainer
    {
        [JsonPropertyName("track")]
        public List<LastFmTrackEntry>? Track { get; init; }
    }

    private sealed class LastFmTrackEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("artist")]
        public LastFmTrackArtist? Artist { get; init; }

        [JsonPropertyName("match")]
        [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
        public double? Match { get; init; }
    }

    private sealed class LastFmTrackArtist
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;
    }

    private sealed class FlexibleNullableIntConverter : JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetInt32(out var intValue))
                {
                    return intValue;
                }

                if (reader.TryGetInt64(out var longValue) && longValue <= int.MaxValue && longValue >= int.MinValue)
                {
                    return (int)longValue;
                }

                return null;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                var raw = reader.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                return null;
            }

            using var _ = JsonDocument.ParseValue(ref reader);
            return null;
        }

        public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                writer.WriteNumberValue(value.Value);
                return;
            }

            writer.WriteNullValue();
        }
    }

    private sealed class FlexibleNullableDoubleConverter : JsonConverter<double?>
    {
        public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetDouble(out var parsed))
                {
                    return parsed;
                }

                return null;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                var raw = reader.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                return null;
            }

            using var _ = JsonDocument.ParseValue(ref reader);
            return null;
        }

        public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                writer.WriteNumberValue(value.Value);
                return;
            }

            writer.WriteNullValue();
        }
    }
}

public sealed record LastFmSimilarTrack(string TrackName, string ArtistName, double Match);
