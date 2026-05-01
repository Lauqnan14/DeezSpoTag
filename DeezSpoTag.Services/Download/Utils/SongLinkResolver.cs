using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Models.Qobuz;
using DeezSpoTag.Integrations.Qobuz;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download;
using DeezSpoTag.Services.Download.Shared.Utils;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Metadata.Qobuz;
using DeezSpoTag.Services.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeezSpoTag.Services.Download.Utils;

/// <summary>
/// Compatibility resolver retained under SongLink naming, but fully backed by native matching logic.
/// No requests are made to song.link.
/// </summary>
public sealed class SongLinkResolver
{
    private const string DeezerPlatform = "deezer";
    private const string SpotifyPlatform = "spotify";
    private const string TidalPlatform = "tidal";
    private const string QobuzPlatform = "qobuz";
    private const string ApplePlatform = "appleMusic";
    private const string AmazonPlatform = "amazonMusic";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly Regex QobuzTrackRegex = new(
        @"qobuz\.com\/(?:[a-z]{2}\/[a-z]{2}\/)?track\/(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);
    private static readonly Regex TidalTrackRegex = new(
        @"tidal\.com\/(?:browse\/)?track\/(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);
    private static readonly Regex AppleMusicTrackRegex = new(
        @"music\.apple\.com\/.+\?i=(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);
    private static readonly Regex HtmlMetaContentRegex = new(
        "<meta\\s+[^>]*?(?:property|name)\\s*=\\s*['\\\"](?<key>[^'\\\"]+)['\\\"][^>]*?content\\s*=\\s*['\\\"](?<content>[^'\\\"]+)['\\\"][^>]*?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);
    private static readonly Regex HtmlMetaContentReverseRegex = new(
        "<meta\\s+[^>]*?content\\s*=\\s*['\\\"](?<content>[^'\\\"]+)['\\\"][^>]*?(?:property|name)\\s*=\\s*['\\\"](?<key>[^'\\\"]+)['\\\"][^>]*?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);
    private static readonly Regex AppleTitleByArtistRegex = new(
        @"^(.+)\s+(?:by|von|de|par|di|door|av|af|przez)\s+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);
    private static readonly string[] QobuzTitleNoiseMarkers =
    [
        "remaster", "remastered", "radio edit", "single version", "album version", "live", "acoustic", "demo"
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IQobuzMetadataService? _qobuzMetadataService;
    private readonly QobuzTrackResolver? _qobuzTrackResolver;
    private readonly QobuzApiConfig _qobuzConfig;
    private readonly SongLinkPersistentCacheStore? _persistentCacheStore;
    private readonly ILogger<SongLinkResolver> _logger;
    private readonly SpotifyTrackMetadataResolver? _spotifyTrackMetadataResolver;
    private readonly ISpotifyIdResolver? _spotifyIdResolver;
    private readonly TidalDownloadService? _tidalDownloadService;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SongLinkResolver(
        IHttpClientFactory httpClientFactory,
        IQobuzMetadataService? qobuzMetadataService,
        QobuzTrackResolver? qobuzTrackResolver,
        IOptions<QobuzApiConfig>? qobuzOptions,
        ILogger<SongLinkResolver> logger,
        SongLinkPersistentCacheStore? persistentCacheStore = null,
        SpotifyTrackMetadataResolver? spotifyTrackMetadataResolver = null,
        ISpotifyIdResolver? spotifyIdResolver = null,
        TidalDownloadService? tidalDownloadService = null)
    {
        _httpClientFactory = httpClientFactory;
        _qobuzMetadataService = qobuzMetadataService;
        _qobuzTrackResolver = qobuzTrackResolver;
        _qobuzConfig = qobuzOptions?.Value ?? new QobuzApiConfig();
        _logger = logger;
        _persistentCacheStore = persistentCacheStore;
        _spotifyTrackMetadataResolver = spotifyTrackMetadataResolver;
        _spotifyIdResolver = spotifyIdResolver;
        _tidalDownloadService = tidalDownloadService;
    }

    public Task<SongLinkResult?> ResolveSpotifyTrackAsync(string spotifyTrackId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spotifyTrackId))
        {
            return Task.FromResult<SongLinkResult?>(null);
        }

        var spotifyUrl = $"https://open.spotify.com/track/{spotifyTrackId.Trim()}";
        return ResolveByUrlAsync(spotifyUrl, cancellationToken);
    }

    public async Task<string?> ResolveDeezerIdFromSpotifyAsync(string spotifyTrackId, CancellationToken cancellationToken)
    {
        var result = await ResolveSpotifyTrackAsync(spotifyTrackId, cancellationToken);
        if (result == null)
        {
            return null;
        }

        if (TrackIdNormalization.TryNormalizeDeezerTrackId(result.DeezerId, out var deezerId))
        {
            return deezerId;
        }

        return TrackIdNormalization.NormalizeDeezerTrackIdOrNull(result.DeezerUrl);
    }

    public Task<SongLinkResult?> ResolveByUrlAsync(string url, CancellationToken cancellationToken)
    {
        return ResolveByUrlAsync(url, userCountry: null, cancellationToken);
    }

    public async Task<SongLinkResult?> ResolveByUrlAsync(string url, string? userCountry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var normalizedUrl = NormalizeCacheUrl(url);

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

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("song.link is deactivated. Using native link regeneration for {Url}", normalizedUrl);
        }

        SongLinkResult? result = null;
        var source = TryParseSource(normalizedUrl);
        if (source is { Platform: SpotifyPlatform })
        {
            result = await ResolveExternalSongLinkAsync(normalizedUrl, source.TrackId, cancellationToken);
            if (result != null && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Resolved Spotify link via external song.link for {Url}", normalizedUrl);
            }
        }

        result ??= await ResolveNativeAsync(normalizedUrl, cancellationToken);
        CacheResult(normalizedUrl, userCountry, result);
        await CacheResultInPersistentStoreAsync(normalizedUrl, userCountry, result, cancellationToken);
        return result;
    }

    public Task<SongLinkResult?> ResolveByDeezerTrackIdAsync(string deezerTrackId, CancellationToken cancellationToken)
    {
        if (!TrackIdNormalization.TryNormalizeDeezerTrackId(deezerTrackId, out var normalized))
        {
            return Task.FromResult<SongLinkResult?>(null);
        }

        var deezerUrl = $"https://www.deezer.com/track/{normalized}";
        return ResolveByUrlAsync(deezerUrl, cancellationToken);
    }

    private async Task<SongLinkResult?> ResolveNativeAsync(string normalizedUrl, CancellationToken cancellationToken)
    {
        var source = TryParseSource(normalizedUrl);
        if (source == null)
        {
            return null;
        }

        var result = new SongLinkResult();
        var metadata = new TrackMetadata();

        switch (source.Platform)
        {
            case DeezerPlatform:
                result.DeezerId = source.TrackId;
                result.DeezerUrl = BuildDeezerTrackUrl(source.TrackId);
                metadata = await ResolveDeezerTrackMetadataByIdAsync(source.TrackId, cancellationToken);
                break;
            case SpotifyPlatform:
                result.SpotifyId = source.TrackId;
                result.SpotifyUrl = BuildSpotifyTrackUrl(source.TrackId);
                metadata = await ResolveSpotifyTrackMetadataByIdAsync(source.TrackId, cancellationToken);
                break;
            case QobuzPlatform:
                result.QobuzUrl = BuildQobuzTrackUrl(source.TrackId);
                break;
            case TidalPlatform:
                result.TidalUrl = BuildTidalTrackUrl(source.TrackId);
                break;
            case ApplePlatform:
                result.AppleMusicUrl = normalizedUrl;
                metadata = await ResolveAppleTrackMetadataByUrlAsync(normalizedUrl, cancellationToken);
                break;
            case AmazonPlatform:
                result.AmazonUrl = normalizedUrl;
                break;
            default:
                break;
        }

        result.SourceType = "song";
        result.SourceTitle = metadata.Title;
        result.SourceArtist = metadata.Artist;
        result.Isrc = metadata.Isrc;

        var resolvedDeezer = result.DeezerId;
        if (string.IsNullOrWhiteSpace(resolvedDeezer))
        {
            resolvedDeezer = await ResolveDeezerIdByMetadataAsync(metadata, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolvedDeezer))
            {
                result.DeezerId = resolvedDeezer;
                result.DeezerUrl = BuildDeezerTrackUrl(resolvedDeezer);
            }
        }

        if (!string.IsNullOrWhiteSpace(result.DeezerId)
            && (string.IsNullOrWhiteSpace(result.SourceTitle)
                || string.IsNullOrWhiteSpace(result.SourceArtist)
                || string.IsNullOrWhiteSpace(result.Isrc)
                || !metadata.DurationMs.HasValue))
        {
            var deezerMetadata = await ResolveDeezerTrackMetadataByIdAsync(result.DeezerId, cancellationToken);
            metadata = metadata.Merge(deezerMetadata);
            result.SourceTitle ??= metadata.Title;
            result.SourceArtist ??= metadata.Artist;
            result.Isrc ??= metadata.Isrc;
        }

        if (string.IsNullOrWhiteSpace(result.SpotifyId))
        {
            result.SpotifyId = await ResolveSpotifyIdByMetadataAsync(metadata, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.SpotifyId))
            {
                result.SpotifyUrl = BuildSpotifyTrackUrl(result.SpotifyId);
            }
        }

        if (string.IsNullOrWhiteSpace(result.TidalUrl))
        {
            result.TidalUrl = await ResolveTidalUrlByMetadataAsync(metadata, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(result.QobuzUrl))
        {
            result.QobuzUrl = await ResolveQobuzUrlFromMetadataAsync(metadata, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(result.DeezerUrl) && !string.IsNullOrWhiteSpace(result.DeezerId))
        {
            result.DeezerUrl = BuildDeezerTrackUrl(result.DeezerId);
        }

        if (string.IsNullOrWhiteSpace(result.SpotifyUrl) && !string.IsNullOrWhiteSpace(result.SpotifyId))
        {
            result.SpotifyUrl = BuildSpotifyTrackUrl(result.SpotifyId);
        }

        return HasAnyResolvedLink(result) ? result : null;
    }

    private static bool HasAnyResolvedLink(SongLinkResult result)
    {
        return !string.IsNullOrWhiteSpace(result.DeezerId)
               || !string.IsNullOrWhiteSpace(result.DeezerUrl)
               || !string.IsNullOrWhiteSpace(result.SpotifyId)
               || !string.IsNullOrWhiteSpace(result.SpotifyUrl)
               || !string.IsNullOrWhiteSpace(result.TidalUrl)
               || !string.IsNullOrWhiteSpace(result.QobuzUrl)
               || !string.IsNullOrWhiteSpace(result.AppleMusicUrl)
               || !string.IsNullOrWhiteSpace(result.AmazonUrl);
    }

    private async Task<SongLinkResult?> ResolveExternalSongLinkAsync(
        string normalizedUrl,
        string spotifyTrackId,
        CancellationToken cancellationToken)
    {
        try
        {
            var apiUrl = $"https://api.song.link/v1-alpha.1/links?url={WebUtility.UrlEncode(normalizedUrl)}";
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(apiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("linksByPlatform", out var linksByPlatform)
                || linksByPlatform.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var deezerUrl = TryReadPlatformUrl(linksByPlatform, DeezerPlatform);
            var spotifyUrl = TryReadPlatformUrl(linksByPlatform, SpotifyPlatform) ?? BuildSpotifyTrackUrl(spotifyTrackId);
            var qobuzUrl = TryReadPlatformUrl(linksByPlatform, QobuzPlatform);
            var tidalUrl = TryReadPlatformUrl(linksByPlatform, TidalPlatform);
            var appleUrl = TryReadPlatformUrl(linksByPlatform, ApplePlatform);
            var amazonUrl = TryReadPlatformUrl(linksByPlatform, AmazonPlatform);
            var deezerId = TrackIdNormalization.NormalizeDeezerTrackIdOrNull(deezerUrl);

            var result = new SongLinkResult
            {
                DeezerId = deezerId,
                DeezerUrl = string.IsNullOrWhiteSpace(deezerId) ? deezerUrl : BuildDeezerTrackUrl(deezerId),
                SpotifyId = spotifyTrackId,
                SpotifyUrl = spotifyUrl,
                QobuzUrl = qobuzUrl,
                TidalUrl = tidalUrl,
                AppleMusicUrl = appleUrl,
                AmazonUrl = amazonUrl
            };

            return HasAnyResolvedLink(result) ? result : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "External song.link lookup failed for {Url}", normalizedUrl);
            }

            return null;
        }
    }

    private static string? TryReadPlatformUrl(JsonElement linksByPlatform, string platform)
    {
        if (!linksByPlatform.TryGetProperty(platform, out var platformNode)
            || platformNode.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!platformNode.TryGetProperty("url", out var urlNode)
            || urlNode.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return urlNode.GetString();
    }

    private async Task<TrackMetadata> ResolveSpotifyTrackMetadataByIdAsync(string spotifyTrackId, CancellationToken cancellationToken)
    {
        if (_spotifyTrackMetadataResolver == null)
        {
            return TrackMetadata.Empty;
        }

        var metadata = await _spotifyTrackMetadataResolver.ResolveTrackAsync(spotifyTrackId, cancellationToken);
        if (metadata == null)
        {
            return TrackMetadata.Empty;
        }

        return new TrackMetadata(
            metadata.Title,
            metadata.Artist,
            metadata.Album,
            metadata.Isrc,
            metadata.DurationMs);
    }

    private async Task<TrackMetadata> ResolveDeezerTrackMetadataByIdAsync(string deezerTrackId, CancellationToken cancellationToken)
    {
        if (!TrackIdNormalization.TryNormalizeDeezerTrackId(deezerTrackId, out var normalizedTrackId))
        {
            return TrackMetadata.Empty;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync($"https://api.deezer.com/track/{WebUtility.UrlEncode(normalizedTrackId)}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return TrackMetadata.Empty;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<DeezerTrackEnvelope>(
                stream,
                CaseInsensitiveJsonOptions,
                cancellationToken);
            if (payload == null || payload.Id <= 0)
            {
                return TrackMetadata.Empty;
            }

            return new TrackMetadata(
                payload.Title,
                payload.Artist?.Name,
                payload.Album?.Title,
                NormalizeIsrc(payload.Isrc),
                payload.Duration > 0 ? payload.Duration * 1000 : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer track metadata lookup failed for {TrackId}", deezerTrackId);
            }

            return TrackMetadata.Empty;
        }
    }

    private async Task<string?> ResolveDeezerIdByMetadataAsync(TrackMetadata metadata, CancellationToken cancellationToken)
    {
        var normalizedIsrc = NormalizeIsrc(metadata.Isrc);
        if (!string.IsNullOrWhiteSpace(normalizedIsrc))
        {
            var deezerIdByIsrc = await ResolveDeezerIdByIsrcAsync(normalizedIsrc, cancellationToken);
            if (!string.IsNullOrWhiteSpace(deezerIdByIsrc))
            {
                return deezerIdByIsrc;
            }
        }

        if (string.IsNullOrWhiteSpace(metadata.Title) || string.IsNullOrWhiteSpace(metadata.Artist))
        {
            return null;
        }

        var bestCandidate = await SearchBestDeezerCandidateAsync(metadata, cancellationToken);
        return bestCandidate?.Id;
    }

    private async Task<string?> ResolveDeezerIdByIsrcAsync(string isrc, CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync($"https://api.deezer.com/track/isrc:{WebUtility.UrlEncode(isrc)}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<DeezerTrackEnvelope>(
                stream,
                CaseInsensitiveJsonOptions,
                cancellationToken);

            return payload != null && payload.Id > 0
                ? payload.Id.ToString(CultureInfo.InvariantCulture)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer ISRC lookup failed for {Isrc}", isrc);
            }

            return null;
        }
    }

    private async Task<DeezerSearchCandidate?> SearchBestDeezerCandidateAsync(TrackMetadata metadata, CancellationToken cancellationToken)
    {
        var queries = BuildDeezerSearchQueries(metadata);
        DeezerSearchCandidate? best = null;

        foreach (var query in queries)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                continue;
            }

            var candidates = await SearchDeezerCandidatesAsync(query, cancellationToken);
            foreach (var candidate in candidates)
            {
                if (IsDerivativeMismatch(metadata.Title, candidate.Title, metadata.Artist, candidate.Artist))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(metadata.Isrc)
                    && !string.IsNullOrWhiteSpace(candidate.Isrc)
                    && string.Equals(metadata.Isrc, candidate.Isrc, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate with { Score = 1.0d };
                }

                var score = ScoreDeezerCandidate(metadata, candidate);
                if (best == null || score > best.Score)
                {
                    best = candidate with { Score = score };
                }
            }
        }

        if (best == null)
        {
            return null;
        }

        return best.Score >= 0.62d ? best : null;
    }

    private static IEnumerable<string> BuildDeezerSearchQueries(TrackMetadata metadata)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(metadata.Artist) && !string.IsNullOrWhiteSpace(metadata.Title))
        {
            var strict = $"artist:\"{metadata.Artist.Trim()}\" track:\"{metadata.Title.Trim()}\"";
            if (seen.Add(strict))
            {
                yield return strict;
            }

            var loose = $"{metadata.Artist.Trim()} {metadata.Title.Trim()}";
            if (seen.Add(loose))
            {
                yield return loose;
            }
        }

        if (!string.IsNullOrWhiteSpace(metadata.Title) && seen.Add(metadata.Title.Trim()))
        {
            yield return metadata.Title.Trim();
        }
    }

    private async Task<List<DeezerSearchCandidate>> SearchDeezerCandidatesAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            var url = $"https://api.deezer.com/search/track?q={WebUtility.UrlEncode(query)}&limit=12";
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new List<DeezerSearchCandidate>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return new List<DeezerSearchCandidate>();
            }

            var candidates = new List<DeezerSearchCandidate>();
            foreach (var item in data.EnumerateArray())
            {
                if (!TryExtractDeezerSearchCandidate(item, out var candidate))
                {
                    continue;
                }

                candidates.Add(candidate);
            }

            return candidates;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Deezer metadata search failed for query \"{Query}\"", query);
            }

            return new List<DeezerSearchCandidate>();
        }
    }

    private static bool TryExtractDeezerSearchCandidate(JsonElement item, out DeezerSearchCandidate candidate)
    {
        candidate = new DeezerSearchCandidate(string.Empty, null, null, null, null, null, 0d);

        var id = item.TryGetProperty("id", out var idElement)
                 && idElement.ValueKind == JsonValueKind.Number
                 && idElement.TryGetInt64(out var idValue)
            ? idValue.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var title = item.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
            ? titleElement.GetString()
            : null;
        var artist = item.TryGetProperty("artist", out var artistElement)
                     && artistElement.ValueKind == JsonValueKind.Object
                     && artistElement.TryGetProperty("name", out var artistName)
                     && artistName.ValueKind == JsonValueKind.String
            ? artistName.GetString()
            : null;
        var album = item.TryGetProperty("album", out var albumElement)
                    && albumElement.ValueKind == JsonValueKind.Object
                    && albumElement.TryGetProperty("title", out var albumTitle)
                    && albumTitle.ValueKind == JsonValueKind.String
            ? albumTitle.GetString()
            : null;
        var isrc = item.TryGetProperty("isrc", out var isrcElement) && isrcElement.ValueKind == JsonValueKind.String
            ? NormalizeIsrc(isrcElement.GetString())
            : null;
        var duration = item.TryGetProperty("duration", out var durationElement)
                       && durationElement.ValueKind == JsonValueKind.Number
                       && durationElement.TryGetInt32(out var durationSeconds)
            ? durationSeconds * 1000
            : (int?)null;

        candidate = new DeezerSearchCandidate(id, title, artist, album, isrc, duration, 0d);
        return true;
    }

    private static double ScoreDeezerCandidate(TrackMetadata source, DeezerSearchCandidate candidate)
    {
        var titleScore = ComputeTokenSimilarity(source.Title, candidate.Title);
        var artistScore = ComputeTokenSimilarity(source.Artist, candidate.Artist);
        var albumScore = ComputeTokenSimilarity(source.Album, candidate.Album);
        var durationScore = ComputeDurationScore(source.DurationMs, candidate.DurationMs);

        return (titleScore * 0.45d)
               + (artistScore * 0.35d)
               + (albumScore * 0.10d)
               + (durationScore * 0.10d);
    }

    private async Task<string?> ResolveSpotifyIdByMetadataAsync(TrackMetadata metadata, CancellationToken cancellationToken)
    {
        if (_spotifyIdResolver == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(metadata.Title) && string.IsNullOrWhiteSpace(metadata.Isrc))
        {
            return null;
        }

        try
        {
            return await _spotifyIdResolver.ResolveTrackIdAsync(
                metadata.Title ?? string.Empty,
                metadata.Artist ?? string.Empty,
                metadata.Album,
                metadata.Isrc,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Spotify ID regeneration failed for {Title} - {Artist}", metadata.Title, metadata.Artist);
            }

            return null;
        }
    }

    private async Task<string?> ResolveTidalUrlByMetadataAsync(TrackMetadata metadata, CancellationToken cancellationToken)
    {
        if (_tidalDownloadService == null
            || string.IsNullOrWhiteSpace(metadata.Title)
            || string.IsNullOrWhiteSpace(metadata.Artist))
        {
            return null;
        }

        try
        {
            var expectedDuration = metadata.DurationMs.HasValue && metadata.DurationMs.Value > 0
                ? (int)Math.Round(metadata.DurationMs.Value / 1000d)
                : 0;

            return await _tidalDownloadService.ResolveTrackUrlAsync(
                metadata.Title,
                metadata.Artist,
                metadata.Isrc ?? string.Empty,
                expectedDuration,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Tidal URL regeneration failed for {Title} - {Artist}", metadata.Title, metadata.Artist);
            }

            return null;
        }
    }

    private async Task<string?> ResolveQobuzUrlFromMetadataAsync(TrackMetadata metadata, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Isrc))
        {
            var isrcResolved = await ResolveQobuzUrlByIsrcAsync(metadata.Isrc, cancellationToken);
            if (!string.IsNullOrWhiteSpace(isrcResolved))
            {
                return isrcResolved;
            }
        }

        if (string.IsNullOrWhiteSpace(metadata.Title) || string.IsNullOrWhiteSpace(metadata.Artist))
        {
            return null;
        }

        return await ResolveQobuzUrlByMetadataAsync(
            metadata.Title,
            metadata.Artist,
            metadata.DurationMs,
            cancellationToken);
    }

    public async Task<string?> ResolveQobuzUrlByIsrcAsync(string isrc, CancellationToken cancellationToken)
    {
        var normalizedIsrc = NormalizeIsrc(isrc);
        if (string.IsNullOrWhiteSpace(normalizedIsrc))
        {
            return null;
        }

        var resolverResult = await TryResolveQobuzUrlViaResolverAsync(normalizedIsrc, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolverResult))
        {
            return resolverResult;
        }

        var metadataResult = await TryResolveQobuzUrlViaMetadataServiceAsync(normalizedIsrc, cancellationToken);
        if (!string.IsNullOrWhiteSpace(metadataResult))
        {
            return metadataResult;
        }

        return await TryResolveQobuzUrlViaPublicSearchAsync(normalizedIsrc, cancellationToken);
    }

    public async Task<string?> ResolveQobuzUrlByMetadataAsync(
        string title,
        string artist,
        int? durationMs,
        CancellationToken cancellationToken)
    {
        if (_qobuzMetadataService == null && _qobuzTrackResolver == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var expectedDurationSec = durationMs.HasValue && durationMs.Value > 0
            ? (int)Math.Round(durationMs.Value / 1000d)
            : 0;

        var resolverResult = await TryResolveQobuzUrlViaResolverAsync(
            isrc: null,
            title,
            artist,
            album: null,
            durationMs,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolverResult))
        {
            return resolverResult;
        }

        if (_qobuzMetadataService == null)
        {
            return null;
        }

        var queries = BuildQobuzQueries(title, artist);
        var candidates = await SearchQobuzCandidatesByQueriesAsync(queries, cancellationToken);
        var best = PickBestQobuzCandidate(candidates, title, artist, expectedDurationSec);

        return best.HasValue
            ? BuildQobuzTrackUrl(best.Value)
            : null;
    }

    private async Task<string?> TryResolveQobuzUrlViaResolverAsync(
        string? isrc,
        string? title,
        string? artist,
        string? album,
        int? durationMs,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_qobuzTrackResolver == null)
            {
                return null;
            }

            return await _qobuzTrackResolver.ResolveTrackUrlAsync(
                isrc,
                title,
                artist,
                album,
                durationMs,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Qobuz resolver lookup failed for {Isrc}", isrc);
            }

            return null;
        }
    }

    private async Task<string?> TryResolveQobuzUrlViaResolverAsync(string isrc, CancellationToken cancellationToken)
    {
        return await TryResolveQobuzUrlViaResolverAsync(isrc, null, null, null, null, cancellationToken);
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
                return BuildQobuzTrackUrl(track.Id);
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Qobuz metadata ISRC lookup failed for {Isrc}", isrc);
            }

            return null;
        }
    }

    private async Task<string?> TryResolveQobuzUrlViaPublicSearchAsync(string isrc, CancellationToken cancellationToken)
    {
        try
        {
            var searchUrl =
                $"https://www.qobuz.com/api.json/0.2/track/search?query={WebUtility.UrlEncode(isrc)}&limit=1&app_id=798273057";
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(searchUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!TryGetQobuzTrackItems(document.RootElement, out var items) || items.GetArrayLength() == 0)
            {
                return null;
            }

            return TryExtractQobuzTrackId(items[0], out var trackId)
                ? BuildQobuzTrackUrl(trackId)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Qobuz public ISRC lookup failed for {Isrc}", isrc);
            }

            return null;
        }
    }

    private async Task<List<QobuzTrack>> SearchQobuzCandidatesByQueriesAsync(
        IEnumerable<string> queries,
        CancellationToken cancellationToken)
    {
        if (_qobuzMetadataService == null)
        {
            return new List<QobuzTrack>();
        }

        var results = new Dictionary<int, QobuzTrack>();
        foreach (var query in queries)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                continue;
            }

            var searchResults = await _qobuzMetadataService.SearchTracks(query, cancellationToken);
            foreach (var track in searchResults.Where(static track => track.Id > 0))
            {
                results[track.Id] = track;
            }

            foreach (var store in ResolveQobuzStores())
            {
                var autosuggestResults = await _qobuzMetadataService.SearchTracksAutosuggest(query, store, cancellationToken);
                foreach (var track in autosuggestResults.Where(static track => track.Id > 0))
                {
                    results[track.Id] = track;
                }
            }
        }

        return results.Values.ToList();
    }

    private IEnumerable<string> ResolveQobuzStores()
    {
        var configuredStores = _qobuzConfig.PreferredStores ?? new List<string>();
        if (configuredStores.Count == 0)
        {
            yield return string.IsNullOrWhiteSpace(_qobuzConfig.DefaultStore) ? "us-en" : _qobuzConfig.DefaultStore;
            yield break;
        }

        foreach (var store in configuredStores
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return store;
        }
    }

    private static List<string> BuildQobuzQueries(string title, string artist)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queries = new List<string>();
        var normalizedTitle = NormalizeQobuzTitle(title);
        var normalizedArtist = NormalizeQobuzArtist(artist);

        if (!string.IsNullOrWhiteSpace(normalizedArtist) && !string.IsNullOrWhiteSpace(normalizedTitle))
        {
            AddQuery($"{normalizedArtist} {normalizedTitle}", seen, queries);
            AddQuery($"{normalizedTitle} {normalizedArtist}", seen, queries);
        }

        AddQuery(normalizedTitle, seen, queries);
        AddQuery(normalizedArtist, seen, queries);
        AddQuery(title, seen, queries);
        AddQuery(artist, seen, queries);

        return queries;
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

    private static long? PickBestQobuzCandidate(
        IEnumerable<QobuzTrack> candidates,
        string title,
        string artist,
        int expectedDurationSec)
    {
        QobuzTrack? best = null;
        var bestScore = double.MinValue;

        foreach (var candidate in candidates.Where(static candidate => candidate != null && candidate.Id > 0))
        {
            var candidateArtist = candidate.Performer?.Name
                                  ?? candidate.Album?.Artists?.FirstOrDefault()?.Name
                                  ?? string.Empty;

            var titleScore = ComputeTokenSimilarity(title, candidate.Title);
            var artistScore = ComputeTokenSimilarity(artist, candidateArtist);
            var durationScore = ComputeDurationScore(
                expectedDurationSec > 0 ? expectedDurationSec * 1000 : null,
                candidate.Duration > 0 ? candidate.Duration * 1000 : null);
            var hiresBonus = candidate.MaximumBitDepth >= 24 || candidate.MaximumSamplingRate >= 96 ? 0.08d : 0d;

            var score = (titleScore * 0.50d) + (artistScore * 0.35d) + (durationScore * 0.15d) + hiresBonus;
            if (score <= bestScore)
            {
                continue;
            }

            best = candidate;
            bestScore = score;
        }

        if (best == null)
        {
            return null;
        }

        return bestScore >= 0.58d ? best.Id : null;
    }

    private static string BuildQobuzTrackUrl(long trackId)
    {
        return $"https://play.qobuz.com/track/{trackId}";
    }

    private static string BuildQobuzTrackUrl(string qobuzTrackId)
    {
        return $"https://play.qobuz.com/track/{qobuzTrackId}";
    }

    private static bool TryGetQobuzTrackItems(JsonElement rootElement, out JsonElement items)
    {
        items = default;
        if (!rootElement.TryGetProperty("tracks", out var tracks)
            || tracks.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (tracks.TryGetProperty("total", out var total)
            && total.ValueKind == JsonValueKind.Number
            && total.GetInt32() <= 0)
        {
            return false;
        }

        return tracks.TryGetProperty("items", out items)
               && items.ValueKind == JsonValueKind.Array;
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

    private static bool IsDerivativeMismatch(string? sourceTitle, string? candidateTitle, string? sourceArtist, string? candidateArtist)
    {
        var sourceDerivative = ContainsDerivativeMarker(sourceTitle) || ContainsDerivativeMarker(sourceArtist);
        var candidateDerivative = ContainsDerivativeMarker(candidateTitle) || ContainsDerivativeMarker(candidateArtist);
        return !sourceDerivative && candidateDerivative;
    }

    private static bool ContainsDerivativeMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = NormalizeForComparison(value);
        return normalized.Contains(" cover ", StringComparison.Ordinal)
               || normalized.Contains(" karaoke ", StringComparison.Ordinal)
               || normalized.Contains(" tribute ", StringComparison.Ordinal)
               || normalized.Contains(" instrumental ", StringComparison.Ordinal)
               || normalized.Contains(" re recorded ", StringComparison.Ordinal)
               || normalized.StartsWith("cover ", StringComparison.Ordinal)
               || normalized.EndsWith(" cover", StringComparison.Ordinal);
    }

    private static double ComputeDurationScore(int? expectedDurationMs, int? candidateDurationMs)
    {
        if (!expectedDurationMs.HasValue || expectedDurationMs.Value <= 0
            || !candidateDurationMs.HasValue || candidateDurationMs.Value <= 0)
        {
            return 0.45d;
        }

        var delta = Math.Abs(expectedDurationMs.Value - candidateDurationMs.Value);
        return delta switch
        {
            <= 2000 => 1.0d,
            <= 5000 => 0.85d,
            <= 10000 => 0.65d,
            <= 20000 => 0.35d,
            _ => 0.05d
        };
    }

    private static double ComputeTokenSimilarity(string? expected, string? actual)
    {
        var normalizedExpected = NormalizeForComparison(expected);
        var normalizedActual = NormalizeForComparison(actual);
        if (string.IsNullOrWhiteSpace(normalizedExpected) || string.IsNullOrWhiteSpace(normalizedActual))
        {
            return 0d;
        }

        if (string.Equals(normalizedExpected, normalizedActual, StringComparison.Ordinal))
        {
            return 1.0d;
        }

        if (normalizedExpected.Contains(normalizedActual, StringComparison.Ordinal)
            || normalizedActual.Contains(normalizedExpected, StringComparison.Ordinal))
        {
            return 0.9d;
        }

        var expectedTokens = SplitTokens(normalizedExpected);
        var actualTokens = SplitTokens(normalizedActual);
        if (expectedTokens.Count == 0 || actualTokens.Count == 0)
        {
            return 0d;
        }

        var intersection = expectedTokens.Intersect(actualTokens, StringComparer.Ordinal).Count();
        if (intersection == 0)
        {
            return 0d;
        }

        var union = expectedTokens.Union(actualTokens, StringComparer.Ordinal).Count();
        var jaccard = union > 0 ? (double)intersection / union : 0d;
        var overlap = Math.Max(
            (double)intersection / expectedTokens.Count,
            (double)intersection / actualTokens.Count);

        return Math.Max(jaccard, overlap);
    }

    private static HashSet<string> SplitTokens(string normalized)
    {
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length > 1)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string NormalizeForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalizedForm = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalizedForm.Length + 2);
        builder.Append(' ');

        foreach (var character in normalizedForm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(' ');
            }
        }

        builder.Append(' ');
        var normalized = builder.ToString();
        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        return normalized;
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
                _logger.LogDebug(ex, "Persistent link cache lookup failed for key {CacheKey}", cacheKey);
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
                _logger.LogDebug(ex, "Persistent link cache upsert failed for key {CacheKey}", cacheKey);
            }
        }
    }

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

        if (!string.IsNullOrEmpty(builder.Path) && builder.Path.Length > 1)
        {
            builder.Path = builder.Path.TrimEnd('/');
        }

        return builder.Uri.AbsoluteUri;
    }

    private static SourceDescriptor? TryParseSource(string normalizedUrl)
    {
        if (TrackIdNormalization.TryNormalizeDeezerTrackId(normalizedUrl, out var deezerId)
            && !string.IsNullOrWhiteSpace(deezerId))
        {
            return new SourceDescriptor(DeezerPlatform, deezerId);
        }

        if (TrackIdNormalization.TryNormalizeSpotifyTrackId(normalizedUrl, out var spotifyId)
            && !string.IsNullOrWhiteSpace(spotifyId))
        {
            return new SourceDescriptor(SpotifyPlatform, spotifyId);
        }

        if (TryMatchTrackId(QobuzTrackRegex, normalizedUrl, out var qobuzId))
        {
            return new SourceDescriptor(QobuzPlatform, qobuzId!);
        }

        if (TryMatchTrackId(TidalTrackRegex, normalizedUrl, out var tidalId))
        {
            return new SourceDescriptor(TidalPlatform, tidalId!);
        }

        if (TryMatchTrackId(AppleMusicTrackRegex, normalizedUrl, out var appleTrackId))
        {
            return new SourceDescriptor(ApplePlatform, appleTrackId!);
        }

        if (TryExtractAppleTrackId(normalizedUrl) is { Length: > 0 } extractedAppleTrackId)
        {
            return new SourceDescriptor(ApplePlatform, extractedAppleTrackId);
        }

        var amazonTrackId = EngineLinkParser.TryExtractAmazonTrackId(normalizedUrl, RegexTimeout);
        if (!string.IsNullOrWhiteSpace(amazonTrackId))
        {
            return new SourceDescriptor(AmazonPlatform, amazonTrackId!);
        }

        return null;
    }

    private static bool TryMatchTrackId(Regex regex, string input, out string? id)
    {
        id = null;
        var match = regex.Match(input);
        if (!match.Success)
        {
            return false;
        }

        var groupValue = match.Groups["id"].Value;
        if (string.IsNullOrWhiteSpace(groupValue))
        {
            return false;
        }

        id = groupValue.Trim();
        return true;
    }

    private static string BuildDeezerTrackUrl(string deezerTrackId)
    {
        return $"https://www.deezer.com/track/{deezerTrackId}";
    }

    private static string BuildSpotifyTrackUrl(string spotifyTrackId)
    {
        return $"https://open.spotify.com/track/{spotifyTrackId}";
    }

    private static string BuildTidalTrackUrl(string tidalTrackId)
    {
        return $"https://listen.tidal.com/track/{tidalTrackId}";
    }

    private async Task<TrackMetadata> ResolveAppleTrackMetadataByUrlAsync(string appleUrl, CancellationToken cancellationToken)
    {
        var trackId = TryExtractAppleTrackId(appleUrl);
        if (!string.IsNullOrWhiteSpace(trackId))
        {
            var apiMetadata = await TryResolveAppleTrackMetadataViaLookupAsync(trackId, cancellationToken);
            if (apiMetadata != TrackMetadata.Empty)
            {
                return apiMetadata;
            }
        }

        return await TryResolveAppleTrackMetadataViaPageAsync(appleUrl, cancellationToken);
    }

    private async Task<TrackMetadata> TryResolveAppleTrackMetadataViaLookupAsync(string appleTrackId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(appleTrackId, out _))
        {
            return TrackMetadata.Empty;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            var lookupUrl = $"https://itunes.apple.com/lookup?id={WebUtility.UrlEncode(appleTrackId)}&entity=song";
            using var response = await client.GetAsync(lookupUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return TrackMetadata.Empty;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<AppleLookupEnvelope>(
                stream,
                CaseInsensitiveJsonOptions,
                cancellationToken);
            if (payload?.Results == null || payload.Results.Count == 0)
            {
                return TrackMetadata.Empty;
            }

            var match = payload.Results.FirstOrDefault(candidate =>
                    candidate.TrackId.HasValue
                    && string.Equals(
                        candidate.TrackId.Value.ToString(CultureInfo.InvariantCulture),
                        appleTrackId,
                        StringComparison.Ordinal))
                ?? payload.Results.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.TrackName));

            if (match == null || string.IsNullOrWhiteSpace(match.TrackName) || string.IsNullOrWhiteSpace(match.ArtistName))
            {
                return TrackMetadata.Empty;
            }

            return new TrackMetadata(
                match.TrackName,
                match.ArtistName,
                match.CollectionName,
                NormalizeIsrc(match.Isrc),
                match.TrackTimeMillis);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple lookup metadata resolution failed for {AppleTrackId}", appleTrackId);
            }

            return TrackMetadata.Empty;
        }
    }

    private async Task<TrackMetadata> TryResolveAppleTrackMetadataViaPageAsync(string appleUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(appleUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return TrackMetadata.Empty;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var ogTitle = TryExtractHtmlMetaContent(html, "og:title");
            if (string.IsNullOrWhiteSpace(ogTitle))
            {
                return TrackMetadata.Empty;
            }

            var cleaned = Regex.Replace(
                ogTitle,
                @"\s+(?:on|bei|en|sur|su|no|op|på|w)\s+Apple\s+Music$",
                string.Empty,
                RegexOptions.IgnoreCase,
                RegexTimeout).Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return TrackMetadata.Empty;
            }

            var match = AppleTitleByArtistRegex.Match(cleaned);
            if (match.Success)
            {
                var title = match.Groups[1].Value.Trim();
                var artist = match.Groups[2].Value.Trim();
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
                {
                    return new TrackMetadata(title, artist, null, null, null);
                }
            }

            return new TrackMetadata(cleaned, null, null, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Apple page metadata resolution failed for {Url}", appleUrl);
            }

            return TrackMetadata.Empty;
        }
    }

    private static string? TryExtractHtmlMetaContent(string html, string key)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (Match match in HtmlMetaContentRegex.Matches(html))
        {
            if (!match.Success)
            {
                continue;
            }

            var foundKey = WebUtility.HtmlDecode(match.Groups["key"].Value);
            if (!string.Equals(foundKey, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return WebUtility.HtmlDecode(match.Groups["content"].Value)?.Trim();
        }

        foreach (Match match in HtmlMetaContentReverseRegex.Matches(html))
        {
            if (!match.Success)
            {
                continue;
            }

            var foundKey = WebUtility.HtmlDecode(match.Groups["key"].Value);
            if (!string.Equals(foundKey, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return WebUtility.HtmlDecode(match.Groups["content"].Value)?.Trim();
        }

        return null;
    }

    private static string? TryExtractAppleTrackId(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var parsed = AppleIdParser.TryExtractFromUrl(url);
        if (string.IsNullOrWhiteSpace(parsed))
        {
            return null;
        }

        return long.TryParse(parsed, out _) ? parsed : null;
    }

    private static string NormalizeQobuzTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var normalized = title.Trim();
        normalized = Regex.Replace(normalized, @"\((?:feat|ft)\.?[^)]*\)", string.Empty, RegexOptions.IgnoreCase, RegexTimeout);
        normalized = Regex.Replace(normalized, @"\[(?:feat|ft)\.?[^\]]*\]", string.Empty, RegexOptions.IgnoreCase, RegexTimeout);

        foreach (var marker in QobuzTitleNoiseMarkers)
        {
            normalized = Regex.Replace(
                normalized,
                $@"\s*[-–]\s*{Regex.Escape(marker)}\s*$",
                string.Empty,
                RegexOptions.IgnoreCase,
                RegexTimeout);
            normalized = Regex.Replace(
                normalized,
                $@"\(({Regex.Escape(marker)})\)\s*$",
                string.Empty,
                RegexOptions.IgnoreCase,
                RegexTimeout);
            normalized = Regex.Replace(
                normalized,
                $@"\[({Regex.Escape(marker)})\]\s*$",
                string.Empty,
                RegexOptions.IgnoreCase,
                RegexTimeout);
        }

        return Regex.Replace(normalized, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
    }

    private static string NormalizeQobuzArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return string.Empty;
        }

        var normalized = artist.Trim();
        normalized = Regex.Replace(normalized, @"\b(feat|ft)\.?\b.*$", string.Empty, RegexOptions.IgnoreCase, RegexTimeout);
        return Regex.Replace(normalized, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
    }

    private static string? NormalizeIsrc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().ToUpperInvariant();
        return trimmed.Length >= 8 ? trimmed : null;
    }

    private sealed record CacheEntry(DateTimeOffset Stamp, SongLinkResult? Result);

    private sealed record SourceDescriptor(string Platform, string TrackId);

    private sealed record TrackMetadata(
        string? Title = null,
        string? Artist = null,
        string? Album = null,
        string? Isrc = null,
        int? DurationMs = null)
    {
        public static TrackMetadata Empty { get; } = new();

        public TrackMetadata Merge(TrackMetadata other)
        {
            return new TrackMetadata(
                Title ?? other.Title,
                Artist ?? other.Artist,
                Album ?? other.Album,
                Isrc ?? other.Isrc,
                DurationMs ?? other.DurationMs);
        }
    }

    private sealed record DeezerSearchCandidate(
        string Id,
        string? Title,
        string? Artist,
        string? Album,
        string? Isrc,
        int? DurationMs,
        double Score);

    private sealed record DeezerTrackEnvelope(
        long Id,
        string? Title,
        string? Isrc,
        int Duration,
        DeezerArtistEnvelope? Artist,
        DeezerAlbumEnvelope? Album);

    private sealed record DeezerArtistEnvelope(string? Name);

    private sealed record DeezerAlbumEnvelope(string? Title);

    private sealed record AppleLookupEnvelope(List<AppleLookupItem>? Results);

    private sealed record AppleLookupItem(
        long? TrackId,
        string? TrackName,
        string? ArtistName,
        string? CollectionName,
        string? Isrc,
        int? TrackTimeMillis);
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
