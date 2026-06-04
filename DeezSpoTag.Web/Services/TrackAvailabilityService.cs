using DeezSpoTag.Services.Download.Shared.Models;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Download;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace DeezSpoTag.Web.Services;

public sealed class TrackAvailabilityService
{
    private static readonly TimeSpan AppleSearchSuccessTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan AppleSearchMissTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AppleSearchRateLimitTtl = TimeSpan.FromMinutes(2);
    private static readonly SemaphoreSlim AppleSearchGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, AppleSearchCacheEntry> AppleSearchCache = new(StringComparer.Ordinal);
    private static long _appleSearchPausedUntilUtcTicks;

    private readonly DownloadIntentService _downloadIntentService;
    private readonly ResolveProxyClient _resolveProxyClient;
    private readonly ISpotifyIdResolver _spotifyIdResolver;
    private readonly SpotifySearchService _spotifySearchService;
    private readonly DeezSpoTagSearchService _searchService;

    public TrackAvailabilityService(
        DownloadIntentService downloadIntentService,
        ResolveProxyClient resolveProxyClient,
        ISpotifyIdResolver spotifyIdResolver,
        SpotifySearchService spotifySearchService,
        DeezSpoTagSearchService searchService)
    {
        _downloadIntentService = downloadIntentService;
        _resolveProxyClient = resolveProxyClient;
        _spotifyIdResolver = spotifyIdResolver;
        _spotifySearchService = spotifySearchService;
        _searchService = searchService;
    }

    public async Task<TrackAvailabilityResult> ResolveAsync(
        TrackAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        var input = BuildInput(request);
        if (!input.HasLookupSignal)
        {
            return TrackAvailabilityResult.Failed("spotifyId, url, isrc, deezerId, appleId, tidalId, qobuzId, or amazonId is required.");
        }

        var proxy = await ResolveProxyAvailabilityAsync(input, cancellationToken);
        var lookup = await _downloadIntentService.LookupAvailabilityAsync(BuildLookupIntent(input), cancellationToken);
        return await BuildResultAsync(input, proxy, lookup, cancellationToken);
    }

    private async Task<ProxyAvailabilityResult> ResolveProxyAvailabilityAsync(
        AvailabilityInput input,
        CancellationToken cancellationToken)
    {
        var combined = new SongLinkResult
        {
            SpotifyId = input.SpotifyId,
            SpotifyUrl = BuildSpotifyUrl(input.SpotifyId),
            DeezerId = input.NormalizedDeezerId,
            DeezerUrl = BuildDeezerUrl(input.NormalizedDeezerId),
            TidalUrl = BuildTidalUrl(input.TidalId),
            QobuzUrl = BuildQobuzUrl(input.QobuzId),
            AppleMusicUrl = BuildAppleUrl(input.AppleId),
            Isrc = input.Isrc
        };
        var attempted = false;
        var completed = false;
        string? error = null;

        if (!string.IsNullOrWhiteSpace(input.Url))
        {
            var result = await _resolveProxyClient.ResolveUrlWithStatusAsync(input.Url, cancellationToken);
            attempted = attempted || result.Attempted;
            completed = completed || result.Completed;
            error ??= result.Error;
            MergeSongLink(combined, ExtractSongLink(result));
        }

        foreach (var lookup in BuildPlatformLookups(input))
        {
            var result = await _resolveProxyClient.ResolvePlatformIdWithStatusAsync(
                lookup.Platform,
                "song",
                lookup.Id,
                cancellationToken);
            attempted = attempted || result.Attempted;
            completed = completed || result.Completed;
            error ??= result.Error;
            MergeSongLink(combined, ExtractSongLink(result));
        }

        return new ProxyAvailabilityResult(combined, attempted, completed, error);
    }

    private async Task<TrackAvailabilityResult> BuildResultAsync(
        AvailabilityInput input,
        ProxyAvailabilityResult proxy,
        DownloadIntentService.AvailabilityLookupResult lookup,
        CancellationToken cancellationToken)
    {
        var spotifyId = LooksLikeSpotifyId(proxy.SongLink.SpotifyId) ? proxy.SongLink.SpotifyId : lookup.SpotifyId;
        spotifyId = LooksLikeSpotifyId(spotifyId) ? spotifyId : input.SpotifyId;
        if (!LooksLikeSpotifyId(spotifyId))
        {
            spotifyId = await ResolveSpotifyIdByMetadataAsync(input, lookup, cancellationToken);
        }
        if (!LooksLikeSpotifyId(spotifyId))
        {
            spotifyId = await ResolveSpotifyIdBySearchAsync(input, cancellationToken);
        }

        var deezerId = FirstNonEmpty(proxy.SongLink.DeezerId, input.NormalizedDeezerId);
        var appleUrl = FirstNonEmpty(proxy.SongLink.AppleMusicUrl, lookup.AppleMusicUrl);
        var appleId = FirstNonEmpty(input.AppleId, ExtractAppleId(proxy.SongLink.AppleMusicUrl), ExtractAppleId(appleUrl));
        var appleUnknown = false;
        if (IsFabricatedAppleIdentity(deezerId, appleId, appleUrl))
        {
            appleId = null;
            appleUrl = null;
        }
        if (string.IsNullOrWhiteSpace(appleId) && string.IsNullOrWhiteSpace(appleUrl))
        {
            var appleSearch = await ResolveAppleBySearchAsync(input, cancellationToken);
            appleId = appleSearch.Candidate?.Id;
            appleUrl = appleSearch.Candidate?.Url;
            appleUnknown = appleSearch.Unknown;
        }

        var tidalId = FirstNonEmpty(input.TidalId, ExtractTidalId(proxy.SongLink.TidalUrl), ExtractTidalId(lookup.TidalUrl));
        var qobuzId = FirstNonEmpty(input.QobuzId, ExtractQobuzId(proxy.SongLink.QobuzUrl), ExtractQobuzId(lookup.QobuzUrl));
        var amazonId = input.AmazonId;

        var spotifyUrl = FirstNonEmpty(proxy.SongLink.SpotifyUrl, lookup.SpotifyUrl, BuildSpotifyUrl(spotifyId));
        var deezerUrl = FirstNonEmpty(proxy.SongLink.DeezerUrl, lookup.DeezerUrl, BuildDeezerUrl(deezerId));
        var tidalUrl = FirstNonEmpty(proxy.SongLink.TidalUrl, lookup.TidalUrl, BuildTidalUrl(tidalId));
        var amazonUrl = FirstNonEmpty(proxy.SongLink.AmazonUrl, lookup.AmazonUrl);
        var qobuzUrl = FirstNonEmpty(proxy.SongLink.QobuzUrl, lookup.QobuzUrl, BuildQobuzUrl(qobuzId));
        appleUrl = FirstNonEmpty(appleUrl, BuildAppleUrl(appleId));
        if (IsFabricatedAppleIdentity(deezerId, appleId, appleUrl))
        {
            appleId = null;
            appleUrl = null;
            appleUnknown = false;
        }

        var spotify = IsAvailable(spotifyId, spotifyUrl, input.Url, "spotify");
        var deezer = IsAvailable(deezerId, deezerUrl, input.Url, "deezer");
        var tidal = IsAvailable(tidalId, tidalUrl, input.Url, "tidal");
        var amazon = IsAvailable(amazonId, amazonUrl, input.Url, "amazon");
        var qobuz = IsAvailable(qobuzId, qobuzUrl, input.Url, "qobuz");
        bool? apple = appleUnknown ? null : IsAvailable(appleId, appleUrl, input.Url, "apple");

        return new TrackAvailabilityResult
        {
            Available = spotify || deezer || tidal || amazon || qobuz || apple == true,
            Resolved = true,
            ResolverAttempted = proxy.Attempted,
            ResolverResolved = proxy.Completed,
            ResolverError = proxy.Error,
            Spotify = spotify,
            SpotifyId = spotifyId,
            SpotifyUrl = spotifyUrl,
            Isrc = FirstNonEmpty(proxy.SongLink.Isrc, lookup.Isrc, input.Isrc),
            Deezer = deezer,
            DeezerId = deezerId,
            DeezerUrl = deezerUrl,
            Tidal = tidal,
            TidalId = tidalId,
            TidalUrl = tidalUrl,
            Amazon = amazon,
            AmazonId = amazonId,
            AmazonUrl = amazonUrl,
            Qobuz = qobuz,
            QobuzId = qobuzId,
            QobuzUrl = qobuzUrl,
            Apple = apple,
            AppleId = appleId,
            AppleUrl = appleUrl
        };
    }

    private static DownloadIntent BuildLookupIntent(AvailabilityInput input)
    {
        var sourceUrl = input.Url;
        if (string.IsNullOrWhiteSpace(sourceUrl) && !string.IsNullOrWhiteSpace(input.NormalizedDeezerId))
        {
            sourceUrl = BuildDeezerUrl(input.NormalizedDeezerId);
        }

        return new DownloadIntent
        {
            SourceUrl = sourceUrl ?? string.Empty,
            SpotifyId = input.SpotifyId ?? string.Empty,
            DeezerId = input.NormalizedDeezerId ?? string.Empty,
            Isrc = input.Isrc ?? string.Empty,
            Title = input.Title ?? string.Empty,
            Artist = input.Artist ?? string.Empty,
            Album = input.Album ?? string.Empty,
            DurationMs = input.DurationMs ?? 0,
            AppleId = input.AppleId ?? string.Empty
        };
    }

    private async Task<string?> ResolveSpotifyIdByMetadataAsync(
        AvailabilityInput input,
        DownloadIntentService.AvailabilityLookupResult lookup,
        CancellationToken cancellationToken)
    {
        var title = input.Title?.Trim();
        var artist = input.Artist?.Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var spotifyId = await _spotifyIdResolver.ResolveTrackIdAsync(
            title,
            artist,
            input.Album,
            FirstNonEmpty(input.Isrc, lookup.Isrc),
            cancellationToken);
        return LooksLikeSpotifyId(spotifyId) ? spotifyId : null;
    }

    private async Task<string?> ResolveSpotifyIdBySearchAsync(
        AvailabilityInput input,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Title) || string.IsNullOrWhiteSpace(input.Artist))
        {
            return null;
        }

        var query = $"{input.Title} {input.Artist}";
        if (!string.IsNullOrWhiteSpace(input.Album)
            && !input.Album.Equals(input.Title, StringComparison.OrdinalIgnoreCase))
        {
            query = $"{query} {input.Album}";
        }

        var response = await _spotifySearchService.SearchByTypeAsync(query, "track", 10, 0, cancellationToken);
        if (response?.Items == null || response.Items.Count == 0)
        {
            return null;
        }

        var targetTitle = NormalizeForMatch(input.Title);
        var targetArtist = NormalizeForMatch(input.Artist);
        var targetAlbum = NormalizeForMatch(input.Album);

        var best = response.Items
            .Select(item => new
            {
                Item = item,
                Score = ScoreSpotifyCandidate(item, targetTitle, targetArtist, targetAlbum)
            })
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();

        return best is { Score: >= 6 } && LooksLikeSpotifyId(best.Item.Id)
            ? best.Item.Id
            : null;
    }

    private async Task<AppleSearchOutcome> ResolveAppleBySearchAsync(
        AvailabilityInput input,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Title) || string.IsNullOrWhiteSpace(input.Artist))
        {
            return AppleSearchOutcome.Miss;
        }

        var query = $"{input.Title} {input.Artist}";
        if (!string.IsNullOrWhiteSpace(input.Album)
            && !input.Album.Equals(input.Title, StringComparison.OrdinalIgnoreCase))
        {
            query = $"{query} {input.Album}";
        }

        var cacheKey = NormalizeForMatch(query);
        if (TryGetCachedAppleSearch(cacheKey, out var cached))
        {
            return cached;
        }

        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        if (IsAppleSearchPaused(nowTicks))
        {
            return AppleSearchOutcome.RateLimited;
        }

        await AppleSearchGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCachedAppleSearch(cacheKey, out cached))
            {
                return cached;
            }

            nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            if (IsAppleSearchPaused(nowTicks))
            {
                return AppleSearchOutcome.RateLimited;
            }

            var response = await _searchService.SearchByTypeAsync("apple", query, "track", 5, 0, cancellationToken);
            var outcome = SelectAppleCandidate(input, response);
            CacheAppleSearch(cacheKey, outcome);
            return outcome;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var outcome = AppleSearchOutcome.RateLimited;
            PauseAppleSearchUntil(DateTimeOffset.UtcNow.Add(AppleSearchRateLimitTtl));
            CacheAppleSearch(cacheKey, outcome);
            return outcome;
        }
        finally
        {
            AppleSearchGate.Release();
        }
    }

    private static bool IsAppleSearchPaused(long nowUtcTicks)
        => Volatile.Read(ref _appleSearchPausedUntilUtcTicks) > nowUtcTicks;

    private static void PauseAppleSearchUntil(DateTimeOffset pausedUntilUtc)
        => Volatile.Write(ref _appleSearchPausedUntilUtcTicks, pausedUntilUtc.UtcTicks);

    private static AppleSearchOutcome SelectAppleCandidate(
        AvailabilityInput input,
        DeezSpoTagSearchTypeResponse? response)
    {
        if (response?.Items == null || response.Items.Count == 0)
        {
            return AppleSearchOutcome.Miss;
        }

        var targetTitle = NormalizeForMatch(input.Title);
        var targetArtist = NormalizeForMatch(input.Artist);
        var targetAlbum = NormalizeForMatch(input.Album);

        AppleCandidate? best = null;
        var bestScore = -1;
        foreach (var candidate in response.Items
            .Select(TryReadAppleCandidate)
            .Where(candidate => candidate is not null))
        {
            var score = ScoreAppleCandidate(candidate!, targetTitle, targetArtist, targetAlbum);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore >= 6 && best != null
            ? AppleSearchOutcome.Hit(best)
            : AppleSearchOutcome.Miss;
    }

    private static bool TryGetCachedAppleSearch(string cacheKey, out AppleSearchOutcome outcome)
    {
        if (AppleSearchCache.TryGetValue(cacheKey, out var entry)
            && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            outcome = entry.Outcome;
            return true;
        }

        AppleSearchCache.TryRemove(cacheKey, out _);
        outcome = AppleSearchOutcome.Miss;
        return false;
    }

    private static void CacheAppleSearch(string cacheKey, AppleSearchOutcome outcome)
    {
        var ttl = outcome switch
        {
            { Unknown: true } => AppleSearchRateLimitTtl,
            { Candidate: not null } => AppleSearchSuccessTtl,
            _ => AppleSearchMissTtl
        };

        AppleSearchCache[cacheKey] = new AppleSearchCacheEntry(outcome, DateTimeOffset.UtcNow.Add(ttl));
    }

    private static AppleCandidate? TryReadAppleCandidate(object item)
    {
        var element = JsonSerializer.SerializeToElement(item);
        var id = ReadJsonString(element, "appleId");
        var url = ReadJsonString(element, "appleUrl");
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return new AppleCandidate(
            id,
            url,
            ReadJsonString(element, "name"),
            ReadJsonString(element, "artist"),
            ReadJsonString(element, "album"));
    }

    private static string? ReadJsonString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ScoreAppleCandidate(
        AppleCandidate item,
        string targetTitle,
        string targetArtist,
        string targetAlbum)
    {
        var score = 0;
        var title = NormalizeForMatch(item.Title);
        var artist = NormalizeForMatch(item.Artist);
        var album = NormalizeForMatch(item.Album);

        if (!string.IsNullOrWhiteSpace(targetTitle) && title == targetTitle)
        {
            score += 5;
        }
        else if (!string.IsNullOrWhiteSpace(targetTitle) && ContainsEitherWay(title, targetTitle))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(targetArtist) && artist == targetArtist)
        {
            score += 4;
        }
        else if (!string.IsNullOrWhiteSpace(targetArtist) && ContainsEitherWay(artist, targetArtist))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(targetAlbum) && album == targetAlbum)
        {
            score += 2;
        }

        return score;
    }

    private static bool IsFabricatedAppleIdentity(
        string? deezerId,
        string? appleId,
        string? appleUrl)
    {
        return !string.IsNullOrWhiteSpace(deezerId)
            && !string.IsNullOrWhiteSpace(appleId)
            && string.Equals(appleId, deezerId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(appleUrl)
                || appleUrl.Contains("/song/", StringComparison.OrdinalIgnoreCase));
    }

    private static int ScoreSpotifyCandidate(
        SpotifySearchItem item,
        string targetTitle,
        string targetArtist,
        string targetAlbum)
    {
        var title = NormalizeForMatch(item.Name);
        var subtitle = item.Subtitle ?? string.Empty;
        var subtitleParts = subtitle
            .Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var artist = NormalizeForMatch(subtitleParts.ElementAtOrDefault(0));
        var album = NormalizeForMatch(subtitleParts.ElementAtOrDefault(1));

        var score = 0;
        if (!string.IsNullOrWhiteSpace(targetTitle) && title == targetTitle)
        {
            score += 5;
        }
        else if (!string.IsNullOrWhiteSpace(targetTitle) && ContainsEitherWay(title, targetTitle))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(targetArtist) && artist == targetArtist)
        {
            score += 4;
        }
        else if (!string.IsNullOrWhiteSpace(targetArtist) && ContainsEitherWay(artist, targetArtist))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(targetAlbum) && album == targetAlbum)
        {
            score += 2;
        }

        return score;
    }

    private static bool ContainsEitherWay(string value, string target)
        => !string.IsNullOrWhiteSpace(value)
           && !string.IsNullOrWhiteSpace(target)
           && (value.Contains(target, StringComparison.Ordinal)
               || target.Contains(value, StringComparison.Ordinal));

    private static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
            .ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static IEnumerable<PlatformIdLookup> BuildPlatformLookups(AvailabilityInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.SpotifyId))
        {
            yield return new PlatformIdLookup("spotify", input.SpotifyId);
        }
        if (!string.IsNullOrWhiteSpace(input.NormalizedDeezerId))
        {
            yield return new PlatformIdLookup("deezer", input.NormalizedDeezerId);
        }
        if (!string.IsNullOrWhiteSpace(input.TidalId))
        {
            yield return new PlatformIdLookup("tidal", input.TidalId);
        }
        if (!string.IsNullOrWhiteSpace(input.QobuzId))
        {
            yield return new PlatformIdLookup("qobuz", input.QobuzId);
        }
        if (!string.IsNullOrWhiteSpace(input.AppleId))
        {
            yield return new PlatformIdLookup("appleMusic", input.AppleId);
        }
        if (!string.IsNullOrWhiteSpace(input.AmazonId))
        {
            yield return new PlatformIdLookup("amazonMusic", input.AmazonId);
        }
    }

    private static void MergeSongLink(SongLinkResult target, SongLinkResult? source)
    {
        if (source == null)
        {
            return;
        }

        target.SpotifyId ??= source.SpotifyId;
        target.SpotifyUrl ??= source.SpotifyUrl;
        target.DeezerId ??= source.DeezerId;
        target.DeezerUrl ??= source.DeezerUrl;
        target.TidalUrl ??= source.TidalUrl;
        target.AmazonUrl ??= source.AmazonUrl;
        target.QobuzUrl ??= source.QobuzUrl;
        target.AppleMusicUrl ??= source.AppleMusicUrl;
        target.Isrc ??= source.Isrc;
    }

    private static AvailabilityInput BuildInput(TrackAvailabilityRequest request)
    {
        var spotifyId = FirstNonEmpty(request.SpotifyId, ExtractSpotifyId(request.Url));
        var normalizedDeezerId = NormalizeDeezerId(request.DeezerId);
        if (string.IsNullOrWhiteSpace(normalizedDeezerId)
            && TryExtractDeezerId(request.Url, out var deezerIdFromUrl))
        {
            normalizedDeezerId = deezerIdFromUrl;
        }
        if (string.IsNullOrWhiteSpace(normalizedDeezerId)
            && LooksLikeSpotifyId(request.DeezerId)
            && string.IsNullOrWhiteSpace(spotifyId))
        {
            spotifyId = request.DeezerId;
        }

        return new AvailabilityInput
        {
            SpotifyId = spotifyId,
            Url = request.Url,
            Isrc = request.Isrc,
            NormalizedDeezerId = normalizedDeezerId,
            AppleId = FirstNonEmpty(request.AppleId, ExtractAppleId(request.Url)),
            TidalId = FirstNonEmpty(request.TidalId, ExtractTidalId(request.Url)),
            QobuzId = FirstNonEmpty(request.QobuzId, ExtractQobuzId(request.Url)),
            AmazonId = request.AmazonId,
            Title = request.Title,
            Artist = request.Artist,
            Album = request.Album,
            DurationMs = request.DurationMs
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? BuildSpotifyUrl(string? spotifyId)
        => LooksLikeSpotifyId(spotifyId) ? $"https://open.spotify.com/track/{spotifyId}" : null;

    private static string? BuildDeezerUrl(string? deezerId)
        => string.IsNullOrWhiteSpace(deezerId) ? null : $"https://www.deezer.com/track/{deezerId}";

    private static string? BuildTidalUrl(string? tidalId)
        => string.IsNullOrWhiteSpace(tidalId) ? null : $"https://listen.tidal.com/track/{tidalId}";

    private static string? BuildQobuzUrl(string? qobuzId)
        => string.IsNullOrWhiteSpace(qobuzId) ? null : $"https://open.qobuz.com/track/{qobuzId}";

    private static string? BuildAppleUrl(string? appleId)
        => string.IsNullOrWhiteSpace(appleId) ? null : $"https://music.apple.com/song/{appleId}?i={appleId}";

    private static bool IsAvailable(string? id, string? mappedUrl, string? sourceUrl, string platform)
    {
        return !string.IsNullOrWhiteSpace(id)
            || !string.IsNullOrWhiteSpace(mappedUrl)
            || IsSourceUrlForPlatform(sourceUrl, platform);
    }

    private static bool IsSourceUrlForPlatform(string? sourceUrl, string platform)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return false;
        }

        var normalized = sourceUrl.ToLowerInvariant();
        return platform switch
        {
            "spotify" => normalized.Contains("open.spotify.com/track/", StringComparison.Ordinal)
                         || normalized.StartsWith("spotify:track:", StringComparison.Ordinal),
            "deezer" => normalized.Contains("deezer.com/track/", StringComparison.Ordinal),
            "tidal" => normalized.Contains("tidal.com/track/", StringComparison.Ordinal)
                       || normalized.Contains("tidal.com/browse/track/", StringComparison.Ordinal),
            "qobuz" => normalized.Contains("qobuz.com/", StringComparison.Ordinal)
                       && normalized.Contains("/track/", StringComparison.Ordinal),
            "apple" => normalized.Contains("music.apple.com/", StringComparison.Ordinal)
                       && (normalized.Contains("/song/", StringComparison.Ordinal)
                           || normalized.Contains("?i=", StringComparison.Ordinal)),
            "amazon" => normalized.Contains("music.amazon.", StringComparison.Ordinal),
            _ => false
        };
    }

    private static string? ExtractSpotifyId(string? value)
        => ExtractIdByRegex(value, @"open\.spotify\.com\/track\/(?<id>[A-Za-z0-9]+)");

    private static bool TryExtractDeezerId(string? value, out string? deezerId)
    {
        deezerId = ExtractIdByRegex(value, @"deezer\.com\/(?:[a-z]{2}\/)?track\/(?<id>\d+)");
        return !string.IsNullOrWhiteSpace(deezerId);
    }

    private static string? ExtractAppleId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var queryMatch = System.Text.RegularExpressions.Regex.Match(
            value,
            @"[?&]i=(?<id>\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(250));
        if (queryMatch.Success)
        {
            return queryMatch.Groups["id"].Value;
        }

        var pathMatch = System.Text.RegularExpressions.Regex.Match(
            value,
            @"\/(?<id>\d+)(?:[/?#]|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(250));
        return pathMatch.Success ? pathMatch.Groups["id"].Value : null;
    }

    private static string? ExtractTidalId(string? value)
        => ExtractIdByRegex(value, @"tidal\.com\/(?:browse\/)?track\/(?<id>\d+)");

    private static string? ExtractQobuzId(string? value)
        => ExtractIdByRegex(value, @"qobuz\.com\/(?:[a-z]{2}\/[a-z]{2}\/)?track\/(?<id>\d+)");

    private static string? ExtractIdByRegex(string? value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            value,
            pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(250));
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static bool LooksLikeSpotifyId(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length == 22
           && value.All(char.IsAsciiLetterOrDigit);

    private static string? NormalizeDeezerId(string? value)
        => !string.IsNullOrWhiteSpace(value) && long.TryParse(value, out _) ? value : null;

    private sealed record PlatformIdLookup(string Platform, string Id);
    private sealed record AppleCandidate(string? Id, string? Url, string? Title, string? Artist, string? Album);
    private sealed record AppleSearchCacheEntry(AppleSearchOutcome Outcome, DateTimeOffset ExpiresAtUtc);

    private sealed record AppleSearchOutcome(AppleCandidate? Candidate, bool Unknown)
    {
        public static AppleSearchOutcome Miss { get; } = new(null, false);
        public static AppleSearchOutcome RateLimited { get; } = new(null, true);

        public static AppleSearchOutcome Hit(AppleCandidate candidate)
            => new(candidate, false);
    }

    private sealed record ProxyAvailabilityResult(
        SongLinkResult SongLink,
        bool Attempted,
        bool Completed,
        string? Error);

    private sealed class AvailabilityInput
    {
        public string? SpotifyId { get; init; }
        public string? Url { get; init; }
        public string? Isrc { get; init; }
        public string? NormalizedDeezerId { get; init; }
        public string? AppleId { get; init; }
        public string? TidalId { get; init; }
        public string? QobuzId { get; init; }
        public string? AmazonId { get; init; }
        public string? Title { get; init; }
        public string? Artist { get; init; }
        public string? Album { get; init; }
        public int? DurationMs { get; init; }

        public bool HasLookupSignal =>
            !string.IsNullOrWhiteSpace(SpotifyId)
            || !string.IsNullOrWhiteSpace(Url)
            || !string.IsNullOrWhiteSpace(Isrc)
            || !string.IsNullOrWhiteSpace(NormalizedDeezerId)
            || !string.IsNullOrWhiteSpace(AppleId)
            || !string.IsNullOrWhiteSpace(TidalId)
            || !string.IsNullOrWhiteSpace(QobuzId)
            || !string.IsNullOrWhiteSpace(AmazonId);
    }

    private static SongLinkResult ExtractSongLink(ResolveProxyLookupResult lookupResult)
    {
        return lookupResult.GetType()
            .GetProperty("Result")
            ?.GetValue(lookupResult) as SongLinkResult
            ?? new SongLinkResult();
    }
}

public sealed class TrackAvailabilityRequest
{
    public string? SpotifyId { get; set; }
    public string? Url { get; set; }
    public string? Isrc { get; set; }
    public string? DeezerId { get; set; }
    public string? AppleId { get; set; }
    public string? TidalId { get; set; }
    public string? QobuzId { get; set; }
    public string? AmazonId { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public int? DurationMs { get; set; }
}

public sealed class TrackAvailabilityResult
{
    public string? Error { get; init; }
    public bool Available { get; init; }
    public bool Resolved { get; init; }
    public bool ResolverAttempted { get; init; }
    public bool ResolverResolved { get; init; }
    public string? ResolverError { get; init; }
    public bool Spotify { get; init; }
    public string? SpotifyId { get; init; }
    public string? SpotifyUrl { get; init; }
    public string? Isrc { get; init; }
    public bool Deezer { get; init; }
    public string? DeezerId { get; init; }
    public string? DeezerUrl { get; init; }
    public bool Tidal { get; init; }
    public string? TidalId { get; init; }
    public string? TidalUrl { get; init; }
    public bool Amazon { get; init; }
    public string? AmazonId { get; init; }
    public string? AmazonUrl { get; init; }
    public bool Qobuz { get; init; }
    public string? QobuzId { get; init; }
    public string? QobuzUrl { get; init; }
    public bool? Apple { get; init; }
    public string? AppleId { get; init; }
    public string? AppleUrl { get; init; }

    public static TrackAvailabilityResult Failed(string error)
    {
        return new TrackAvailabilityResult { Error = error };
    }
}
