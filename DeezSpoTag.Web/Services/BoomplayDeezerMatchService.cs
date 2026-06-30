using System.Collections.Concurrent;
using System.Net;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Web.Controllers.Api;
using Microsoft.Extensions.Caching.Memory;

namespace DeezSpoTag.Web.Services;

public sealed record BoomplayDeezerMatchRequest(
    string? Url,
    string? Title,
    string? Artist,
    string? Album,
    string? Isrc,
    int? DurationMs,
    BoomplayTrackMetadata? Track = null);

public sealed record BoomplayDeezerMatchResult(
    string DeezerId,
    string Title,
    string Artist,
    string Album,
    string CoverMedium,
    int? DurationSeconds)
{
    public int? DurationMs => DurationSeconds is > 0 ? DurationSeconds.Value * 1000 : null;
}

public sealed class BoomplayDeezerMatchService
{
    private const string DeezerResolutionCacheKeyPrefix = "boomplay:deezer:resolved:";
    private const string DeezerResolutionMissCacheKeyPrefix = "boomplay:deezer:miss:";
    private const string BoomplayIsrcCacheKeyPrefix = "boomplay:isrc:";
    private static readonly TimeSpan DeezerResolutionCacheTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan DeezerResolutionMissCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DeezerValidationTrackCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan BoomplayIsrcCacheTtl = TimeSpan.FromHours(12);

    private static readonly string[] DerivativeMarkers =
    {
        "cover",
        "covers",
        "parody",
        "parodies",
        "karaoke",
        "tribute",
        "instrumental",
        "instrumentals",
        "remix",
        "remake",
        "re recorded",
        "as made famous by",
        "originally performed by",
        "in the style of",
        "made popular by",
        "made famous by",
        "backing track",
        "backing tracks",
        "sing along",
        "singalong",
        "midi",
        "8 bit",
        "8bit",
        "music box",
        "lullaby version",
        "piano version",
        "acoustic cover",
        "sped up",
        "slowed down",
        "nightcore"
    };

    private static readonly string[] DerivativeArtistKeywords =
    {
        "karaoke", "tribute", "cover", "covers", "instrumental",
        "backing track", "sing along", "singalong", "midi",
        "music box", "lullaby", "8 bit", "8bit", "nightcore",
        "sped up", "originally performed"
    };

    private readonly BoomplayMetadataService _boomplayMetadataService;
    private readonly DeezerClient _deezerClient;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<BoomplayDeezerMatchService> _logger;

    public BoomplayDeezerMatchService(
        BoomplayMetadataService boomplayMetadataService,
        DeezerClient deezerClient,
        IMemoryCache memoryCache,
        ILogger<BoomplayDeezerMatchService> logger)
    {
        _boomplayMetadataService = boomplayMetadataService;
        _deezerClient = deezerClient;
        _memoryCache = memoryCache;
        _logger = logger;
    }

    public async Task<BoomplayDeezerMatchResult?> ResolveTrackAsync(
        BoomplayTrackMetadata track,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (TryGetCachedDeezerResolution(track, out var cached))
        {
            return cached;
        }

        if (HasRecentResolutionMiss(track))
        {
            return null;
        }

        var result = await ResolveAsync(
            new BoomplayDeezerMatchRequest(
                string.IsNullOrWhiteSpace(track.Url) ? $"https://www.boomplay.com/songs/{track.Id}" : track.Url,
                track.Title,
                track.Artist,
                track.Album,
                track.Isrc,
                track.DurationMs > 0 ? track.DurationMs : null,
                track),
            cancellationToken,
            includeMeta: true);

        if (result != null)
        {
            CacheDeezerResolution(track, result);
        }
        else
        {
            CacheDeezerResolutionMiss(track);
        }

        return result;
    }

    public async Task<BoomplayDeezerMatchResult?> ResolveAsync(
        BoomplayDeezerMatchRequest request,
        CancellationToken cancellationToken,
        bool includeMeta = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = CreateContext(request);
        if (!HasAnySourceMetadata(context))
        {
            await EnrichBoomplayMetadataAsync(context, cancellationToken);
        }

        var validationTrackCache = new ConcurrentDictionary<string, ApiTrack?>(StringComparer.Ordinal);
        var deezerId = await ResolveDeezerIdAsync(context, validationTrackCache, cancellationToken);
        if (string.IsNullOrWhiteSpace(deezerId))
        {
            return null;
        }

        return includeMeta
            ? await HydrateDeezerMetadataAsync(deezerId, context, validationTrackCache, cancellationToken)
            : new BoomplayDeezerMatchResult(deezerId, string.Empty, string.Empty, string.Empty, string.Empty, null);
    }

    private static BoomplayMatchContext CreateContext(BoomplayDeezerMatchRequest request)
    {
        var track = request.Track;
        var url = Normalize(request.Url)
            ?? (track != null && !string.IsNullOrWhiteSpace(track.Id)
                ? $"https://www.boomplay.com/songs/{track.Id}"
                : string.Empty);

        return new BoomplayMatchContext
        {
            Url = url,
            Title = Normalize(request.Title) ?? Normalize(track?.Title),
            Artist = Normalize(request.Artist) ?? Normalize(track?.Artist),
            Album = Normalize(request.Album) ?? Normalize(track?.Album),
            Isrc = Normalize(request.Isrc) ?? Normalize(track?.Isrc),
            DurationMs = ResolveDurationMs(request, track),
            Track = track
        };
    }

    private static int? ResolveDurationMs(BoomplayDeezerMatchRequest request, BoomplayTrackMetadata? track)
    {
        if (request.DurationMs is > 0)
        {
            return request.DurationMs;
        }

        return track?.DurationMs is > 0 ? track.DurationMs : null;
    }

    private async Task EnrichBoomplayMetadataAsync(BoomplayMatchContext context, CancellationToken cancellationToken)
    {
        if (context.Track != null
            || string.IsNullOrWhiteSpace(context.Url)
            || !BoomplayMetadataService.TryParseBoomplayUrl(context.Url, out var type, out var trackId)
            || !string.Equals(type, "track", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(trackId))
        {
            return;
        }

        try
        {
            var track = await _boomplayMetadataService.GetSongAsync(trackId, cancellationToken);
            if (track == null)
            {
                return;
            }

            context.Track = track;
            context.Title ??= Normalize(track.Title);
            context.Artist ??= Normalize(track.Artist);
            context.Album ??= Normalize(track.Album);
            context.Isrc ??= Normalize(track.Isrc);
            if (!context.DurationMs.HasValue && track.DurationMs > 0)
            {
                context.DurationMs = track.DurationMs;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed enriching Boomplay metadata while resolving Deezer match.");
        }
    }

    private async Task<string?> ResolveDeezerIdAsync(
        BoomplayMatchContext context,
        ConcurrentDictionary<string, ApiTrack?> validationTrackCache,
        CancellationToken cancellationToken)
    {
        var isrcId = await TryResolveIsrcFirstAsync(context, validationTrackCache, cancellationToken);
        if (!string.IsNullOrWhiteSpace(isrcId))
        {
            return isrcId;
        }

        var metadataId = await TryResolveMetadataCandidatesAsync(context, validationTrackCache, cancellationToken);
        if (!string.IsNullOrWhiteSpace(metadataId))
        {
            return metadataId;
        }

        var directId = await TryResolveDirectMetadataAsync(context, validationTrackCache, cancellationToken);
        if (!string.IsNullOrWhiteSpace(directId))
        {
            return directId;
        }

        var fallbackId = await TryResolveSearchFallbackAsync(context, validationTrackCache, cancellationToken);
        if (!string.IsNullOrWhiteSpace(fallbackId))
        {
            return fallbackId;
        }

        var enrichedIsrcId = await TryResolveByEnrichedIsrcAsync(context, validationTrackCache, cancellationToken);
        if (!string.IsNullOrWhiteSpace(enrichedIsrcId))
        {
            return enrichedIsrcId;
        }

        return null;
    }

    private async Task<string?> TryResolveByEnrichedIsrcAsync(
        BoomplayMatchContext context,
        ConcurrentDictionary<string, ApiTrack?> validationTrackCache,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(context.Isrc)
            || string.IsNullOrWhiteSpace(context.Url)
            || !BoomplayMetadataService.TryParseBoomplayUrl(context.Url, out var type, out var trackId)
            || !string.Equals(type, "track", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        var isrc = await ResolveBoomplayIsrcAfterMetadataMissAsync(trackId, cancellationToken);
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        context.Isrc = Normalize(isrc);
        if (string.IsNullOrWhiteSpace(context.Isrc))
        {
            return null;
        }

        return await TryResolveIsrcFirstAsync(context, validationTrackCache, cancellationToken);
    }

    private async Task<string?> ResolveBoomplayIsrcAfterMetadataMissAsync(
        string trackId,
        CancellationToken cancellationToken)
    {
        var normalizedTrackId = trackId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTrackId))
        {
            return null;
        }

        var cacheKey = BuildBoomplayIsrcCacheKey(normalizedTrackId);
        if (_memoryCache.TryGetValue(cacheKey, out string? cachedIsrc))
        {
            return cachedIsrc;
        }

        try
        {
            var track = await _boomplayMetadataService.GetSongAsync(normalizedTrackId, cancellationToken);
            var isrc = Normalize(track?.Isrc);
            if (!string.IsNullOrWhiteSpace(isrc))
            {
                _memoryCache.Set(cacheKey, isrc, BoomplayIsrcCacheTtl);
                return isrc;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed enriching Boomplay ISRC after Deezer metadata miss.");
        }

        return null;
    }

    private async Task<string?> TryResolveIsrcFirstAsync(
        BoomplayMatchContext context,
        ConcurrentDictionary<string, ApiTrack?> validationTrackCache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Isrc))
        {
            return null;
        }

        try
        {
            var summary = CreateTrackSummary(context, context.Title ?? string.Empty, context.Isrc);
            var deezerId = await SpotifyTracklistResolver.ResolveDeezerTrackIdAsync(
                _deezerClient,
                summary,
                CreateResolveOptions(
                    allowFallbackSearch: false,
                    preferIsrcOnly: true,
                    strictMode: false,
                    bypassNegativeCanonicalCache: false,
                    cancellationToken));
            return await ValidateResolvedCandidateAsync(
                deezerId,
                context,
                context.Title,
                context.Artist,
                validationTrackCache,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "ISRC-first Deezer resolve failed for Boomplay track.");
            return null;
        }
    }

    private async Task<string?> TryResolveMetadataCandidatesAsync(
        BoomplayMatchContext context,
        ConcurrentDictionary<string, ApiTrack?> validationTrackCache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Title) || string.IsNullOrWhiteSpace(context.Artist))
        {
            return null;
        }

        var strictMode = context.Track?.HasStreamTagMetadata != true;
        foreach (var titleCandidate in ResolveDeezerApiController.BuildBoomplayTitleCandidates(context.Title, context.Album, context.Artist))
        {
            var resolved = await TryResolveMetadataForTitleCandidateAsync(
                context,
                titleCandidate,
                strictMode,
                validationTrackCache,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private async Task<string?> TryResolveMetadataForTitleCandidateAsync(
        BoomplayMatchContext context,
        string titleCandidate,
        bool strictMode,
        ConcurrentDictionary<string, ApiTrack?> validationTrackCache,
        CancellationToken cancellationToken)
    {
        try
        {
            var summary = CreateTrackSummary(context, titleCandidate, isrc: null);
            var strictResult = await SpotifyTracklistResolver.ResolveDeezerTrackAsync(
                _deezerClient,
                summary,
                CreateResolveOptions(
                    allowFallbackSearch: true,
                    preferIsrcOnly: false,
                    strictMode: strictMode,
                    bypassNegativeCanonicalCache: false,
                    cancellationToken));
            var strictId = await ValidateResolvedCandidateAsync(
                strictResult.DeezerId,
                context,
                titleCandidate,
                context.Artist,
                validationTrackCache,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(strictId) || !strictMode)
            {
                return strictId;
            }

            var relaxedResult = await SpotifyTracklistResolver.ResolveDeezerTrackAsync(
                _deezerClient,
                summary,
                CreateResolveOptions(
                    allowFallbackSearch: true,
                    preferIsrcOnly: false,
                    strictMode: false,
                    bypassNegativeCanonicalCache: true,
                    cancellationToken));
            return await ValidateResolvedCandidateAsync(
                relaxedResult.DeezerId,
                context,
                titleCandidate,
                context.Artist,
                validationTrackCache,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Metadata Deezer resolve failed for Boomplay track.");
            return null;
        }
    }

    private async Task<string?> TryResolveDirectMetadataAsync(
        BoomplayMatchContext context,
        ConcurrentDictionary<string, ApiTrack?> validationTrackCache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Title) || string.IsNullOrWhiteSpace(context.Artist))
        {
            return null;
        }

        foreach (var titleCandidate in ResolveDeezerApiController.BuildBoomplayTitleCandidates(context.Title, context.Album, context.Artist))
        {
            var directId = await TryResolveSingleDirectTitleAsync(context, titleCandidate, validationTrackCache, cancellationToken);
            if (!string.IsNullOrWhiteSpace(directId))
            {
                return directId;
            }
        }

        return null;
    }

    private async Task<string?> TryResolveSingleDirectTitleAsync(
        BoomplayMatchContext context,
        string titleCandidate,
        ConcurrentDictionary<string, ApiTrack?> validationTrackCache,
        CancellationToken cancellationToken)
    {
        try
        {
            var directId = await _deezerClient.GetTrackIdFromMetadataAsync(
                context.Artist!,
                titleCandidate,
                context.Album ?? string.Empty,
                context.DurationMs);
            var validated = await ValidateResolvedCandidateAsync(
                directId,
                context,
                titleCandidate,
                context.Artist,
                validationTrackCache,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(validated))
            {
                return validated;
            }

            var fastId = await _deezerClient.GetTrackIdFromMetadataFastAsync(
                context.Artist!,
                titleCandidate,
                context.DurationMs);
            validated = await ValidateResolvedCandidateAsync(
                fastId,
                context,
                titleCandidate,
                context.Artist,
                validationTrackCache,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(validated))
            {
                return validated;
            }

            var primaryArtist = ResolveDeezerApiController.StripFeaturingFromArtist(context.Artist);
            if (!string.Equals(primaryArtist, context.Artist, StringComparison.OrdinalIgnoreCase))
            {
                var strippedId = await _deezerClient.GetTrackIdFromMetadataFastAsync(
                    primaryArtist,
                    titleCandidate,
                    context.DurationMs);
                return await ValidateResolvedCandidateAsync(
                    strippedId,
                    context,
                    titleCandidate,
                    primaryArtist,
                    validationTrackCache,
                    cancellationToken);
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Direct Boomplay Deezer resolve failed.");
            return null;
        }
    }

    private Task<string?> TryResolveSearchFallbackAsync(
        BoomplayMatchContext context,
        ConcurrentDictionary<string, ApiTrack?> validationTrackCache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Title))
        {
            return Task.FromResult<string?>(null);
        }

        return ResolveFromDeezerSearchFallbackAsync(
            context.Title,
            context.Artist,
            context.Album,
            context.Isrc,
            context.DurationMs,
            validationTrackCache,
            cancellationToken);
    }

    private async Task<string?> ValidateResolvedCandidateAsync(
        string? deezerId,
        BoomplayMatchContext context,
        string? title,
        string? artist,
        ConcurrentDictionary<string, ApiTrack?> validationTrackCache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deezerId) || deezerId == "0")
        {
            return null;
        }

        var source = new DeezerCandidateSource(
            title,
            artist,
            context.Album,
            context.Isrc,
            context.DurationMs);
        var plausible = await IsPlausibleCandidateAsync(
            deezerId,
            source,
            validationTrackCache,
            cancellationToken);
        return plausible ? deezerId : null;
    }

    private Task<bool> IsPlausibleCandidateAsync(
        string deezerId,
        DeezerCandidateSource source,
        ConcurrentDictionary<string, ApiTrack?> validationTrackCache,
        CancellationToken cancellationToken)
    {
        return DeezerCandidateMatchHelper.IsPlausibleCandidateAsync(
            deezerId,
            source,
            new DeezerCandidateValidationHandlers(
                (candidateId, token) => TryGetValidationCandidateAsync(candidateId, validationTrackCache, token),
                SourceAllowsDerivative,
                IsDerivativeCandidate,
                IsDerivativeArtistName),
            _logger,
            new DeezerCandidateValidationOptions(
                MinimumArtistScore: 0.36d,
                RejectDerivativeArtistName: true,
                ApplyVeryLowAlbumGuard: true,
                FailureLogMessage: "Failed to validate Boomplay candidate {DeezerId}"),
            cancellationToken);
    }

    private Task<string?> ResolveFromDeezerSearchFallbackAsync(
        string? sourceTitle,
        string? sourceArtist,
        string? sourceAlbum,
        string? sourceIsrc,
        int? sourceDurationMs,
        ConcurrentDictionary<string, ApiTrack?> validationTrackCache,
        CancellationToken cancellationToken)
    {
        var source = new DeezerCandidateSource(sourceTitle, sourceArtist, sourceAlbum, sourceIsrc, sourceDurationMs);
        return DeezerCandidateMatchHelper.ResolveFromSearchFallbackAsync(
            _deezerClient,
            source,
            new DeezerFallbackSearchHandlers(
                (candidateId, token) => IsPlausibleCandidateAsync(
                    candidateId,
                    source,
                    validationTrackCache,
                    token),
                (candidateId, album, token) => GetAlbumMatchScoreAsync(candidateId, album, validationTrackCache, token),
                (candidateId, token) => TryGetValidationCandidateAsync(candidateId, validationTrackCache, token),
                SourceAllowsDerivative,
                IsDerivativeArtistName),
            _logger,
            new DeezerFallbackSearchOptions(
                ExcludeDerivativeArtistCandidates: true,
                PreferBestAlbumMatch: true,
                SearchFailureLogMessage: "Boomplay fallback Deezer search failed for query {Query}"),
            cancellationToken);
    }

    private Task<double> GetAlbumMatchScoreAsync(
        string deezerId,
        string? sourceAlbum,
        ConcurrentDictionary<string, ApiTrack?> validationTrackCache,
        CancellationToken cancellationToken)
    {
        return DeezerCandidateMatchHelper.GetAlbumMatchScoreAsync(
            deezerId,
            sourceAlbum,
            (candidateId, token) => TryGetValidationCandidateAsync(candidateId, validationTrackCache, token),
            cancellationToken);
    }

    private async Task<BoomplayDeezerMatchResult> HydrateDeezerMetadataAsync(
        string deezerId,
        BoomplayMatchContext context,
        ConcurrentDictionary<string, ApiTrack?> validationTrackCache,
        CancellationToken cancellationToken)
    {
        var fallback = context.Track;
        var title = WebUtility.HtmlDecode(context.Title ?? fallback?.Title ?? string.Empty).Trim();
        var artist = WebUtility.HtmlDecode(context.Artist ?? fallback?.Artist ?? string.Empty).Trim();
        var album = WebUtility.HtmlDecode(context.Album ?? fallback?.Album ?? string.Empty).Trim();
        var coverMedium = WebUtility.HtmlDecode(fallback?.CoverUrl ?? string.Empty).Trim();
        int? durationSeconds = context.DurationMs is > 0
            ? (int)Math.Round(context.DurationMs.Value / 1000d)
            : null;

        if (TryGetValidationTrackFromCache(deezerId, validationTrackCache, out var cachedTrack) && cachedTrack != null)
        {
            return BuildResultFromApiTrack(deezerId, cachedTrack, title, artist, album, coverMedium, durationSeconds);
        }

        try
        {
            var trackData = await _deezerClient.GetTrackAsync(deezerId, cancellationToken);
            if (trackData != null)
            {
                title = FirstNonEmpty(GetString(trackData, "SNG_TITLE"), title);
                artist = FirstNonEmpty(GetString(trackData, "ART_NAME"), artist);
                album = FirstNonEmpty(GetString(trackData, "ALB_TITLE"), album);

                var pictureHash = GetString(trackData, "ALB_PICTURE");
                if (!string.IsNullOrWhiteSpace(pictureHash))
                {
                    coverMedium = $"https://cdns-images.dzcdn.net/images/cover/{pictureHash}/250x250-000000-80-0-0.jpg";
                }
                var deezerDuration = GetInt(trackData, "DURATION");
                if (deezerDuration > 0)
                {
                    durationSeconds = deezerDuration;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed hydrating Deezer metadata for Boomplay match {DeezerId}", DeezSpoTag.Core.Security.LogSanitizer.OneLine(deezerId));
            }
        }

        return new BoomplayDeezerMatchResult(deezerId, title, artist, album, coverMedium, durationSeconds);
    }

    private static BoomplayDeezerMatchResult BuildResultFromApiTrack(
        string deezerId,
        ApiTrack track,
        string fallbackTitle,
        string fallbackArtist,
        string fallbackAlbum,
        string fallbackCover,
        int? fallbackDurationSeconds)
    {
        return new BoomplayDeezerMatchResult(
            deezerId,
            FirstNonEmpty(track.Title ?? string.Empty, fallbackTitle),
            FirstNonEmpty(track.Artist?.Name ?? string.Empty, fallbackArtist),
            FirstNonEmpty(track.Album?.Title ?? string.Empty, fallbackAlbum),
            FirstNonEmpty(track.Album?.CoverMedium ?? string.Empty, fallbackCover),
            track.Duration > 0 ? track.Duration : fallbackDurationSeconds);
    }

    private async Task<(bool fetched, ApiTrack? track)> TryGetValidationCandidateAsync(
        string deezerId,
        ConcurrentDictionary<string, ApiTrack?> validationTrackCache,
        CancellationToken cancellationToken)
    {
        if (TryGetValidationTrackFromCache(deezerId, validationTrackCache, out var cachedTrack))
        {
            return (true, cachedTrack);
        }

        var resolved = await DeezerCandidateMatchHelper.TryGetValidationCandidateAsync(
            _deezerClient,
            _logger,
            deezerId,
            "Failed to load Deezer candidate {DeezerId} for Boomplay validation",
            cancellationToken);

        if (resolved.fetched)
        {
            validationTrackCache[deezerId] = resolved.track;
            if (resolved.track != null)
            {
                _memoryCache.Set(BuildValidationTrackCacheKey(deezerId), resolved.track, DeezerValidationTrackCacheTtl);
            }
        }

        return resolved;
    }

    private bool TryGetValidationTrackFromCache(
        string deezerId,
        ConcurrentDictionary<string, ApiTrack?> validationTrackCache,
        out ApiTrack? track)
    {
        track = null;
        if (string.IsNullOrWhiteSpace(deezerId))
        {
            return false;
        }

        if (validationTrackCache.TryGetValue(deezerId, out var inRequestTrack))
        {
            track = inRequestTrack;
            return true;
        }

        if (_memoryCache.TryGetValue(BuildValidationTrackCacheKey(deezerId), out ApiTrack? sharedTrack))
        {
            validationTrackCache[deezerId] = sharedTrack;
            track = sharedTrack;
            return true;
        }

        return false;
    }

    private bool TryGetCachedDeezerResolution(BoomplayTrackMetadata track, out BoomplayDeezerMatchResult resolved)
    {
        resolved = null!;
        if (string.IsNullOrWhiteSpace(track.Id))
        {
            return false;
        }

        if (!_memoryCache.TryGetValue(BuildDeezerResolutionCacheKey(track.Id), out BoomplayDeezerMatchResult? cached)
            || cached == null)
        {
            return false;
        }

        resolved = cached;
        return true;
    }

    private void CacheDeezerResolution(BoomplayTrackMetadata track, BoomplayDeezerMatchResult result)
    {
        if (!string.IsNullOrWhiteSpace(track.Id))
        {
            _memoryCache.Set(BuildDeezerResolutionCacheKey(track.Id), result, DeezerResolutionCacheTtl);
        }
    }

    private bool HasRecentResolutionMiss(BoomplayTrackMetadata track)
    {
        return !string.IsNullOrWhiteSpace(track.Id)
            && _memoryCache.TryGetValue(BuildDeezerResolutionMissCacheKey(track.Id), out bool missed)
            && missed;
    }

    private void CacheDeezerResolutionMiss(BoomplayTrackMetadata track)
    {
        if (!string.IsNullOrWhiteSpace(track.Id))
        {
            _memoryCache.Set(BuildDeezerResolutionMissCacheKey(track.Id), true, DeezerResolutionMissCacheTtl);
        }
    }

    private static SpotifyTrackSummary CreateTrackSummary(BoomplayMatchContext context, string title, string? isrc)
    {
        return new SpotifyTrackSummary(
            Id: string.Empty,
            Name: title,
            Artists: context.Artist,
            Album: context.Album,
            DurationMs: context.DurationMs,
            SourceUrl: context.Url,
            ImageUrl: context.Track?.CoverUrl,
            Isrc: isrc);
    }

    private SpotifyTrackResolveOptions CreateResolveOptions(
        bool allowFallbackSearch,
        bool preferIsrcOnly,
        bool strictMode,
        bool bypassNegativeCanonicalCache,
        CancellationToken cancellationToken)
    {
        return new SpotifyTrackResolveOptions(
            AllowFallbackSearch: allowFallbackSearch,
            PreferIsrcOnly: preferIsrcOnly,
            StrictMode: strictMode,
            BypassNegativeCanonicalCache: bypassNegativeCanonicalCache,
            Logger: _logger,
            CancellationToken: cancellationToken);
    }

    private static bool HasAnySourceMetadata(BoomplayMatchContext context)
    {
        return !string.IsNullOrWhiteSpace(context.Title)
               || !string.IsNullOrWhiteSpace(context.Artist)
               || !string.IsNullOrWhiteSpace(context.Album)
               || !string.IsNullOrWhiteSpace(context.Isrc);
    }

    private static bool SourceAllowsDerivative(string? title, string? artist, string? album)
    {
        var combined = ResolveDeezerApiController.NormalizeGuardToken($"{title} {artist} {album}");
        return !string.IsNullOrWhiteSpace(combined)
            && DerivativeMarkers.Any(marker => ContainsWholeMarker(combined, marker));
    }

    private static bool IsDerivativeCandidate(ApiTrack candidate)
    {
        var combined = ResolveDeezerApiController.NormalizeGuardToken(
            $"{candidate.Title} {candidate.TitleVersion} {candidate.Album?.Title} {candidate.Artist?.Name}");
        return !string.IsNullOrWhiteSpace(combined)
            && DerivativeMarkers.Any(marker => ContainsWholeMarker(combined, marker));
    }

    private static bool IsDerivativeArtistName(string? artistName)
    {
        var normalized = ResolveDeezerApiController.NormalizeGuardToken(artistName);
        return !string.IsNullOrWhiteSpace(normalized)
            && DerivativeArtistKeywords.Any(keyword => $" {normalized} ".Contains($" {keyword} ", StringComparison.Ordinal));
    }

    private static bool ContainsWholeMarker(string text, string marker)
    {
        var normalizedMarker = ResolveDeezerApiController.NormalizeGuardToken(marker);
        return !string.IsNullOrWhiteSpace(text)
            && !string.IsNullOrWhiteSpace(normalizedMarker)
            && $" {text} ".Contains($" {normalizedMarker} ", StringComparison.Ordinal);
    }

    private static string? Normalize(string? value)
        => ResolveDeezerApiController.Normalize(value);

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string GetString(Dictionary<string, object> values, string key)
    {
        return values.TryGetValue(key, out var raw) && raw != null
            ? raw.ToString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt(Dictionary<string, object> values, string key)
    {
        if (!values.TryGetValue(key, out var raw) || raw == null)
        {
            return 0;
        }

        return raw switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            _ => int.TryParse(raw.ToString(), out var parsed) ? parsed : 0
        };
    }

    private static string BuildDeezerResolutionCacheKey(string boomplayTrackId)
        => $"{DeezerResolutionCacheKeyPrefix}{boomplayTrackId.Trim()}";

    private static string BuildDeezerResolutionMissCacheKey(string boomplayTrackId)
        => $"{DeezerResolutionMissCacheKeyPrefix}{boomplayTrackId.Trim()}";

    private static string BuildBoomplayIsrcCacheKey(string boomplayTrackId)
        => $"{BoomplayIsrcCacheKeyPrefix}{boomplayTrackId.Trim()}";

    private static string BuildValidationTrackCacheKey(string deezerId)
        => $"boomplay:deezer:track:{deezerId.Trim()}";

    private sealed class BoomplayMatchContext
    {
        public string Url { get; init; } = string.Empty;
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Isrc { get; set; }
        public int? DurationMs { get; set; }
        public BoomplayTrackMetadata? Track { get; set; }
    }
}
