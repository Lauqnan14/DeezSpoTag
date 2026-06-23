using System.Text.Json;
using DeezSpoTag.Services.Apple;
using DeezSpoTag.Services.Download.Tidal;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Services.Matching;
using DeezSpoTag.Services.Metadata.Qobuz;
using Microsoft.Extensions.Logging;

namespace DeezSpoTag.Services.Download.Fallback;

public sealed record EngineFallbackSearchRequest(
    string Engine,
    string SourceUrl,
    string SpotifyId,
    string AppleId,
    string QobuzId,
    string TidalId,
    string AmazonId,
    string? Isrc,
    string Title,
    string Artist,
    string Album,
    int? DurationMs,
    string DeezerId,
    string Quality,
    string ContentType,
    string Storefront,
    string Language,
    string? MediaUserToken,
    string UserCountry,
    bool FallbackSearchEnabled);

public sealed record EngineFallbackSearchResult(
    string? ResolvedUrl,
    string ResolutionSource);

public sealed class EngineFallbackSearchService
{
    private const string DeezerEngine = "deezer";
    private const string QobuzEngine = "qobuz";
    private const string AppleEngine = "apple";
    private const string TidalEngine = "tidal";
    private const string DefaultAppleStorefront = "us";
    private const string DefaultLanguage = "en-US";
    private static readonly string[] AppleFallbackStorefronts = ["us", "gb", "ca", "au"];

    private readonly SongLinkResolver _songLinkResolver;
    private readonly AppleMusicCatalogService _appleCatalogService;
    private readonly QobuzTrackResolver? _qobuzTrackResolver;
    private readonly TidalDownloadService? _tidalDownloadService;
    private readonly ILogger<EngineFallbackSearchService> _logger;

    public EngineFallbackSearchService(
        SongLinkResolver songLinkResolver,
        AppleMusicCatalogService appleCatalogService,
        ILogger<EngineFallbackSearchService> logger,
        QobuzTrackResolver? qobuzTrackResolver = null,
        TidalDownloadService? tidalDownloadService = null)
    {
        _songLinkResolver = songLinkResolver;
        _appleCatalogService = appleCatalogService;
        _logger = logger;
        _qobuzTrackResolver = qobuzTrackResolver;
        _tidalDownloadService = tidalDownloadService;
    }

    public async Task<string?> ResolveSpotifyIdAsync(
        string sourceUrl,
        string deezerId,
        string userCountry,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            var sourceSongLink = await _songLinkResolver.ResolveByUrlAsync(sourceUrl, userCountry, cancellationToken);
            if (!string.IsNullOrWhiteSpace(sourceSongLink?.SpotifyId))
            {
                return sourceSongLink.SpotifyId;
            }
        }

        var normalizedDeezerId = NormalizeDeezerTrackId(deezerId);
        if (string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            return null;
        }

        var deezerUrl = $"https://www.deezer.com/track/{normalizedDeezerId}";
        var deezerSongLink = await _songLinkResolver.ResolveByUrlAsync(deezerUrl, userCountry, cancellationToken);
        return deezerSongLink?.SpotifyId;
    }

    public async Task<EngineFallbackSearchResult> ResolveAsync(
        EngineFallbackSearchRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.SourceUrl) && IsServiceUrlMatch(request.SourceUrl, request.Engine))
        {
            return new EngineFallbackSearchResult(request.SourceUrl, "same-engine-url");
        }

        var normalizedDeezerId = NormalizeDeezerTrackId(request.DeezerId);
        if (string.Equals(request.Engine, DeezerEngine, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            return new EngineFallbackSearchResult($"https://www.deezer.com/track/{normalizedDeezerId}", "deezer-id");
        }

        if (string.Equals(request.Engine, QobuzEngine, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(request.QobuzId))
        {
            return new EngineFallbackSearchResult($"https://play.qobuz.com/track/{Uri.EscapeDataString(request.QobuzId.Trim())}", "qobuz-id");
        }

        if (string.Equals(request.Engine, TidalEngine, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(request.TidalId))
        {
            return new EngineFallbackSearchResult($"https://tidal.com/browse/track/{Uri.EscapeDataString(request.TidalId.Trim())}", "tidal-id");
        }

        var appleUrl = await TryBuildAppleFallbackUrlAsync(request, cancellationToken);
        if (!string.IsNullOrWhiteSpace(appleUrl))
        {
            return new EngineFallbackSearchResult(appleUrl, "apple-catalog");
        }

        var songLink = await ResolveSongLinkFromDeezerAsync(normalizedDeezerId, request.UserCountry, cancellationToken);
        var resolvedUrl = GetValidatedEngineUrl(songLink, request);
        if (!string.IsNullOrWhiteSpace(resolvedUrl))
        {
            return new EngineFallbackSearchResult(resolvedUrl, "songlink-deezer");
        }

        (resolvedUrl, songLink) = await TryResolveFromSourceUrlAsync(request, songLink, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolvedUrl))
        {
            return new EngineFallbackSearchResult(resolvedUrl, "songlink-source-url");
        }

        (resolvedUrl, songLink) = await TryResolveFromSpotifyAsync(request, songLink, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolvedUrl))
        {
            return new EngineFallbackSearchResult(resolvedUrl, "songlink-spotify");
        }

        (resolvedUrl, _) = await TryResolveFromSpotifyFallbackSearchAsync(request, songLink, cancellationToken);
        if (!string.IsNullOrWhiteSpace(resolvedUrl))
        {
            return new EngineFallbackSearchResult(resolvedUrl, "songlink-spotify-deezer-search");
        }

        if (string.Equals(request.Engine, QobuzEngine, StringComparison.OrdinalIgnoreCase))
        {
            var qobuzUrl = await ResolveQobuzUrlAsync(request, cancellationToken);
            if (!string.IsNullOrWhiteSpace(qobuzUrl))
            {
                return new EngineFallbackSearchResult(qobuzUrl, "qobuz-metadata-search");
            }
        }

        if (string.Equals(request.Engine, TidalEngine, StringComparison.OrdinalIgnoreCase))
        {
            var tidalUrl = await ResolveTidalUrlAsync(request, cancellationToken);
            if (!string.IsNullOrWhiteSpace(tidalUrl))
            {
                return new EngineFallbackSearchResult(tidalUrl, "tidal-metadata-search");
            }
        }

        return new EngineFallbackSearchResult(null, "unresolved");
    }

    private async Task<string?> ResolveQobuzUrlAsync(
        EngineFallbackSearchRequest request,
        CancellationToken cancellationToken)
    {
        if (_qobuzTrackResolver == null
            || (string.IsNullOrWhiteSpace(request.Isrc)
                && (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Artist))))
        {
            return null;
        }

        var resolution = await _qobuzTrackResolver.ResolveTrackAsync(
            request.Isrc,
            request.Title,
            request.Artist,
            request.Album,
            request.DurationMs,
            cancellationToken);
        return resolution?.Track.Id > 0
            ? $"https://play.qobuz.com/track/{resolution.Track.Id}"
            : null;
    }

    private async Task<string?> ResolveTidalUrlAsync(
        EngineFallbackSearchRequest request,
        CancellationToken cancellationToken)
    {
        if (_tidalDownloadService == null
            || string.IsNullOrWhiteSpace(request.Title)
            || string.IsNullOrWhiteSpace(request.Artist))
        {
            return null;
        }

        var durationSeconds = request.DurationMs.HasValue && request.DurationMs.Value > 0
            ? (int)Math.Round(request.DurationMs.Value / 1000d)
            : 0;
        return IsAtmosRequest(request)
            ? await _tidalDownloadService.ResolveAtmosTrackUrlAsync(
                request.Title,
                request.Artist,
                request.Isrc ?? string.Empty,
                durationSeconds,
                cancellationToken)
            : await _tidalDownloadService.ResolveTrackUrlAsync(
                request.Title,
                request.Artist,
                request.Isrc ?? string.Empty,
                durationSeconds,
                cancellationToken);
    }

    private static bool IsAtmosRequest(EngineFallbackSearchRequest request)
        => string.Equals(request.ContentType?.Trim(), "atmos", StringComparison.OrdinalIgnoreCase)
           || string.Equals(request.Quality?.Trim(), "ATMOS", StringComparison.OrdinalIgnoreCase)
           || string.Equals(request.Quality?.Trim(), "DOLBY_ATMOS", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> TryBuildAppleFallbackUrlAsync(
        EngineFallbackSearchRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Engine, AppleEngine, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var appleId = ResolveSeedAppleId(request);
        var storefront = await ResolveStorefrontOrDefaultAsync(
            request.Storefront,
            request.MediaUserToken,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(appleId))
        {
            (appleId, storefront) = await ResolveAppleIdAcrossCandidatesAsync(
                storefront,
                request.Language,
                (_, candidateStorefront, candidateLanguage, token) => TryResolveAppleIdByIsrcAsync(
                    request.Isrc,
                    candidateStorefront,
                    candidateLanguage,
                    request.MediaUserToken,
                    token),
                request,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(appleId) && request.FallbackSearchEnabled)
        {
            (appleId, storefront) = await ResolveAppleIdAcrossCandidatesAsync(
                storefront,
                request.Language,
                (sourceRequest, candidateStorefront, candidateLanguage, token) => TryResolveAppleIdBySearchAsync(
                    sourceRequest with
                    {
                        Storefront = candidateStorefront,
                        Language = candidateLanguage
                    },
                    token),
                request,
                cancellationToken);
        }

        return BuildAppleMediaUrl(appleId, storefront);
    }

    private async Task<string> ResolveStorefrontOrDefaultAsync(
        string storefront,
        string? mediaUserToken,
        CancellationToken cancellationToken)
    {
        var resolvedStorefront = await _appleCatalogService.ResolveStorefrontAsync(
            storefront,
            mediaUserToken,
            cancellationToken);
        return string.IsNullOrWhiteSpace(resolvedStorefront)
            ? DefaultAppleStorefront
            : resolvedStorefront;
    }

    private static async Task<(string? AppleId, string Storefront)> ResolveAppleIdAcrossCandidatesAsync(
        string primaryStorefront,
        string language,
        Func<EngineFallbackSearchRequest, string, string, CancellationToken, Task<string?>> resolver,
        EngineFallbackSearchRequest request,
        CancellationToken cancellationToken)
    {
        foreach (var storefrontCandidate in BuildStorefrontCandidates(primaryStorefront))
        {
            foreach (var languageCandidate in BuildLanguageCandidates(language))
            {
                var resolvedAppleId = await resolver(request, storefrontCandidate, languageCandidate, cancellationToken);
                if (!string.IsNullOrWhiteSpace(resolvedAppleId))
                {
                    return (resolvedAppleId, storefrontCandidate);
                }
            }
        }

        return (null, primaryStorefront);
    }

    private async Task<string?> TryResolveAppleIdByIsrcAsync(
        string? isrc,
        string storefront,
        string language,
        string? mediaUserToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        try
        {
            using var doc = await _appleCatalogService.GetSongByIsrcAsync(
                isrc,
                storefront,
                language,
                cancellationToken,
                mediaUserToken);
            return TryExtractAppleIdFromCatalog(doc.RootElement);
        }
        catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
        {
            return null;
        }
    }

    private async Task<string?> TryResolveAppleIdBySearchAsync(
        EngineFallbackSearchRequest request,
        CancellationToken cancellationToken)
    {
        foreach (var term in BuildAppleSearchTerms(request.Title, request.Artist, request.Album))
        {
            try
            {
                using var doc = await _appleCatalogService.SearchAsync(
                    term,
                    limit: 10,
                    storefront: request.Storefront,
                    language: request.Language,
                    cancellationToken,
                    new AppleMusicCatalogService.AppleSearchOptions(
                        TypesOverride: "songs",
                        IncludeRelationshipsTracks: false));
                var id = TryExtractAppleSongIdFromSearch(doc.RootElement, request);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return id;
                }
            }
            catch (Exception ex) when (DeezSpoTag.Core.Diagnostics.ExpectedExceptionPolicy.IsRecoverable(ex))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Apple fallback search failed for {Term}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(term));
                }
            }
        }

        return null;
    }

    private static string? TryExtractAppleSongIdFromSearch(JsonElement root, EngineFallbackSearchRequest request)
    {
        if (!root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Object
            || !results.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Object
            || !songs.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var bestScore = int.MinValue;
        string? bestId = null;
        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var score = ScoreAppleCandidate(id, item, request);
            if (score > bestScore)
            {
                bestScore = score;
                bestId = id;
            }
        }

        return bestScore >= 65 ? bestId : null;
    }

    private static int ScoreAppleCandidate(string id, JsonElement item, EngineFallbackSearchRequest request)
    {
        if (!item.TryGetProperty("attributes", out var attrs)
            || attrs.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var title = TryReadString(attrs, "name");
        var artist = TryReadString(attrs, "artistName");
        var album = TryReadString(attrs, "albumName");
        var isrc = TryReadString(attrs, "isrc");
        var durationInMillis = attrs.TryGetProperty("durationInMillis", out var durationProp)
            && durationProp.TryGetInt32(out var parsedDuration)
            ? parsedDuration
            : 0;

        var validation = TrackCandidateValidator.Validate(
            new TrackMatchSource(
                request.Isrc,
                request.Title,
                request.Artist,
                request.Album,
                request.DurationMs),
            new TrackMatchCandidate(
                id,
                isrc,
                title,
                artist,
                album,
                durationInMillis > 0 ? durationInMillis : null),
            new TrackCandidateValidationOptions(
                StrictWithoutIsrc: true,
                AllowMissingCandidateArtist: false,
                RequireCandidateDurationWhenSourceHasDuration: true,
                MaxIsrcDurationDifferenceMs: 20_000,
                MaxMetadataDurationDifferenceMs: 8_000));
        if (!validation.Accepted)
        {
            return 0;
        }

        return (int)Math.Round(validation.Score * 100d);
    }

    private async Task<SongLinkResult?> ResolveSongLinkFromDeezerAsync(
        string? normalizedDeezerId,
        string userCountry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedDeezerId))
        {
            return null;
        }

        var deezerUrl = $"https://www.deezer.com/track/{normalizedDeezerId}";
        return await _songLinkResolver.ResolveByUrlAsync(deezerUrl, userCountry, cancellationToken);
    }

    private async Task<(string? ResolvedUrl, SongLinkResult? SongLink)> TryResolveFromSourceUrlAsync(
        EngineFallbackSearchRequest request,
        SongLinkResult? currentSongLink,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SourceUrl))
        {
            return (null, currentSongLink);
        }

        var sourceUrlSongLink = await _songLinkResolver.ResolveByUrlAsync(request.SourceUrl, request.UserCountry, cancellationToken);
        return PreferSongLinkCandidate(request, currentSongLink, sourceUrlSongLink);
    }

    private async Task<(string? ResolvedUrl, SongLinkResult? SongLink)> TryResolveFromSpotifyAsync(
        EngineFallbackSearchRequest request,
        SongLinkResult? currentSongLink,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            return (null, currentSongLink);
        }

        var spotifySongLink = await _songLinkResolver.ResolveSpotifyTrackAsync(request.SpotifyId, cancellationToken);
        return PreferSongLinkCandidate(request, currentSongLink, spotifySongLink);
    }

    private async Task<(string? ResolvedUrl, SongLinkResult? SongLink)> TryResolveFromSpotifyFallbackSearchAsync(
        EngineFallbackSearchRequest request,
        SongLinkResult? currentSongLink,
        CancellationToken cancellationToken)
    {
        if (!request.FallbackSearchEnabled || string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            return (null, currentSongLink);
        }

        var resolvedDeezerId = await _songLinkResolver.ResolveDeezerIdFromSpotifyAsync(request.SpotifyId, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolvedDeezerId))
        {
            return (null, currentSongLink);
        }

        var deezerUrl = $"https://www.deezer.com/track/{resolvedDeezerId}";
        var resolvedDeezerSongLink = await _songLinkResolver.ResolveByUrlAsync(deezerUrl, request.UserCountry, cancellationToken);
        return PreferSongLinkCandidate(request, currentSongLink, resolvedDeezerSongLink);
    }

    private static (string? ResolvedUrl, SongLinkResult? SongLink) PreferSongLinkCandidate(
        EngineFallbackSearchRequest request,
        SongLinkResult? currentSongLink,
        SongLinkResult? candidateSongLink)
    {
        var candidateUrl = GetValidatedEngineUrl(candidateSongLink, request);
        if (!string.IsNullOrWhiteSpace(candidateUrl) || currentSongLink == null)
        {
            return (candidateUrl, candidateSongLink);
        }

        return (null, currentSongLink);
    }

    private static string? GetValidatedEngineUrl(SongLinkResult? songLink, EngineFallbackSearchRequest request)
    {
        if (songLink == null)
        {
            return null;
        }

        var candidateUrl = GetEngineUrl(songLink, request.Engine);
        if (string.IsNullOrWhiteSpace(candidateUrl))
        {
            return null;
        }

        var validation = TrackCandidateValidator.Validate(
            new TrackMatchSource(
                request.Isrc,
                request.Title,
                request.Artist,
                request.Album,
                request.DurationMs),
            new TrackMatchCandidate(
                candidateUrl,
                songLink.Isrc,
                songLink.SourceTitle,
                songLink.SourceArtist,
                null,
                null),
            new TrackCandidateValidationOptions(
                StrictWithoutIsrc: true,
                AllowMissingCandidateArtist: false,
                RequireCandidateDurationWhenSourceHasDuration: false,
                MaxIsrcDurationDifferenceMs: 20_000,
                MaxMetadataDurationDifferenceMs: 8_000));
        if (!validation.Accepted)
        {
            return null;
        }

        return candidateUrl;
    }

    private static string? GetEngineUrl(SongLinkResult? songLink, string engine)
    {
        if (songLink == null)
        {
            return null;
        }

        return engine switch
        {
            AppleEngine => songLink.AppleMusicUrl,
            TidalEngine => songLink.TidalUrl,
            "amazon" => songLink.AmazonUrl,
            QobuzEngine => songLink.QobuzUrl,
            DeezerEngine => songLink.DeezerUrl,
            _ => null
        };
    }

    private static string? ResolveSeedAppleId(EngineFallbackSearchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AppleId))
        {
            return request.AppleId;
        }

        return IsServiceUrlMatch(request.SourceUrl, AppleEngine)
            ? AppleIdParser.TryExtractFromUrl(request.SourceUrl)
            : null;
    }

    private static string? BuildAppleMediaUrl(string? appleId, string storefront)
    {
        if (string.IsNullOrWhiteSpace(appleId))
        {
            return null;
        }

        return appleId.StartsWith("ra.", StringComparison.OrdinalIgnoreCase)
            ? $"https://music.apple.com/{storefront}/station/{appleId}"
            : $"https://music.apple.com/{storefront}/song/{appleId}?i={appleId}";
    }

    private static string? TryExtractAppleIdFromCatalog(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0)
        {
            return null;
        }

        var first = data[0];
        return first.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    private static List<string> BuildAppleSearchTerms(string? title, string? artist, string? album)
    {
        var terms = new List<string>();
        var normalizedTitle = title?.Trim();
        var normalizedArtist = artist?.Trim();
        var normalizedAlbum = album?.Trim();
        var cleanedTitle = NormalizeForCompare(normalizedTitle ?? string.Empty);
        var cleanedArtist = NormalizeForCompare(normalizedArtist ?? string.Empty);
        var cleanedAlbum = NormalizeForCompare(normalizedAlbum ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(normalizedTitle) && !string.IsNullOrWhiteSpace(normalizedArtist))
        {
            terms.Add($"{normalizedTitle} {normalizedArtist}");
        }

        if (!string.IsNullOrWhiteSpace(normalizedTitle))
        {
            terms.Add(normalizedTitle);
        }

        if (!string.IsNullOrWhiteSpace(normalizedTitle) && !string.IsNullOrWhiteSpace(normalizedAlbum))
        {
            terms.Add($"{normalizedTitle} {normalizedAlbum}");
        }

        if (!string.IsNullOrWhiteSpace(cleanedTitle) && !string.IsNullOrWhiteSpace(cleanedArtist))
        {
            terms.Add($"{cleanedTitle} {cleanedArtist}");
        }

        if (!string.IsNullOrWhiteSpace(cleanedTitle))
        {
            terms.Add(cleanedTitle);
        }

        if (!string.IsNullOrWhiteSpace(cleanedTitle) && !string.IsNullOrWhiteSpace(cleanedAlbum))
        {
            terms.Add($"{cleanedTitle} {cleanedAlbum}");
        }

        return terms
            .Select(term => term.Trim())
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildStorefrontCandidates(string primary)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary))
        {
            candidates.Add(primary.Trim());
        }

        candidates.AddRange(AppleFallbackStorefronts);
        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildLanguageCandidates(string? language)
    {
        var baseLanguage = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.Trim();
        var values = new List<string> { baseLanguage };
        var dashIndex = baseLanguage.IndexOf('-');
        if (dashIndex > 0)
        {
            values.Add(baseLanguage[..dashIndex]);
        }

        if (!baseLanguage.Equals(DefaultLanguage, StringComparison.OrdinalIgnoreCase))
        {
            values.Add(DefaultLanguage);
        }

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeForCompare(string value)
    {
        return new string(value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) ? ch : ' ')
            .ToArray())
            .Trim();
    }

    private static string? TryReadString(JsonElement node, string propertyName)
    {
        return node.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? NormalizeDeezerTrackId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return long.TryParse(value, out _) ? value : null;
    }

    private static bool IsServiceUrlMatch(string url, string engine)
    {
        return engine switch
        {
            DeezerEngine => url.Contains("deezer.com", StringComparison.OrdinalIgnoreCase),
            AppleEngine => url.Contains("music.apple.com", StringComparison.OrdinalIgnoreCase),
            TidalEngine => url.Contains("tidal.com", StringComparison.OrdinalIgnoreCase),
            "amazon" => url.Contains("amazon.", StringComparison.OrdinalIgnoreCase)
                        || url.Contains("music.amazon", StringComparison.OrdinalIgnoreCase),
            QobuzEngine => url.Contains("qobuz.com", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
