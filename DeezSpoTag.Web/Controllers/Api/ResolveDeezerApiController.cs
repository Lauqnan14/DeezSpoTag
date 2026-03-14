using System.Text.RegularExpressions;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/resolve/deezer")]
[Authorize]
public sealed class ResolveDeezerApiController : ControllerBase
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex DeezerTrackRegex =
        CreateRegex(@"deezer\.com\/track\/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BoomplayOfficialSuffixRegex =
        CreateRegex(@"\s*[\(\[]?\s*official\s+(?:audio|video|lyrics?)\s*[\)\]]?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BoomplayFinalSuffixRegex =
        CreateRegex(@"\s*(?:final|finished)\s*\d*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BoomplayAudioExtSuffixRegex =
        CreateRegex(@"\s*\.(?:mp3|wav|m4a|aac)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BoomplayMasterSuffixRegex =
        CreateRegex(@"\s*(?:[-_:]\s*)?(?:\(\s*)?master(?:\s*\))?\s*(?:\(\s*\d+\s*\)|\d+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BoomplayFeaturingTailRegex =
        CreateRegex(@"\s*(?:feat\.?|ft\.?|featuring|with|x)\s+.+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FeaturedArtistsCaptureRegex =
        CreateRegex(@"(?:feat\.?|ft\.?|featuring|with|x)\s+(?<artists>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BoomplayArtistDashPrefixRegex =
        CreateRegex(@"^\s*(?<artist>[^-]{2,80}?)\s*[-–]\s*(?<title>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BoomplayMultiWhitespaceRegex =
        CreateRegex(@"\s+", RegexOptions.Compiled);
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
        "as made famous by"
    };
    private static Regex CreateRegex(string pattern, RegexOptions options)
        => new(pattern, options, RegexTimeout);
    private static string ReplaceWithTimeout(string input, string pattern, string replacement, RegexOptions options = RegexOptions.None)
        => Regex.Replace(input, pattern, replacement, options, RegexTimeout);
    private static string[] SplitWithTimeout(string input, string pattern, RegexOptions options = RegexOptions.None)
        => Regex.Split(input, pattern, options, RegexTimeout);

    private readonly SongLinkResolver _songLinkResolver;
    private readonly DeezerClient _deezerClient;
    private readonly BoomplayMetadataService _boomplayMetadataService;
    private readonly ILogger<ResolveDeezerApiController> _logger;

    public ResolveDeezerApiController(
        SongLinkResolver songLinkResolver,
        DeezerClient deezerClient,
        BoomplayMetadataService boomplayMetadataService,
        ILogger<ResolveDeezerApiController> logger)
    {
        _songLinkResolver = songLinkResolver;
        _deezerClient = deezerClient;
        _boomplayMetadataService = boomplayMetadataService;
        _logger = logger;
    }

    public sealed class ResolveDeezerRequest
    {
        public string? Url { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Isrc { get; set; }
        public int? DurationMs { get; set; }
        public bool? IncludeMeta { get; set; }
    }

    private sealed class ResolveRequestContext
    {
        public required string Url { get; init; }
        public required bool IncludeMeta { get; init; }
        public required bool IsBoomplaySource { get; init; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Isrc { get; set; }
        public int? DurationMs { get; set; }
        public BoomplayTrackMetadata? BoomplayTrack { get; set; }
        public bool HasStreamTagMetadata => BoomplayTrack?.HasStreamTagMetadata == true;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] ResolveDeezerRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest(new { error = "URL is required." });
        }

        var context = CreateResolveRequestContext(request);
        var directId = TryExtractDeezerTrackId(context.Url);
        if (!string.IsNullOrWhiteSpace(directId))
        {
            return Ok(await BuildResolveResponseAsync(directId, context.IncludeMeta));
        }

        await EnrichBoomplayMetadataAsync(context, cancellationToken);
        var deezerId = await ResolveDeezerIdAsync(context, cancellationToken);
        if (string.IsNullOrWhiteSpace(deezerId))
        {
            return Ok(new { available = false });
        }

        return Ok(await BuildResolveResponseAsync(deezerId, context.IncludeMeta));
    }

    private static ResolveRequestContext CreateResolveRequestContext(ResolveDeezerRequest request)
    {
        var normalizedUrl = request.Url!.Trim();
        return new ResolveRequestContext
        {
            Url = normalizedUrl,
            IncludeMeta = request.IncludeMeta == true,
            IsBoomplaySource = BoomplayMetadataService.IsBoomplayUrl(normalizedUrl),
            Title = Normalize(request.Title),
            Artist = Normalize(request.Artist),
            Album = Normalize(request.Album),
            Isrc = Normalize(request.Isrc),
            DurationMs = request.DurationMs.HasValue && request.DurationMs.Value > 0
                ? request.DurationMs.Value
                : (int?)null
        };
    }

    private async Task EnrichBoomplayMetadataAsync(ResolveRequestContext context, CancellationToken cancellationToken)
    {
        if (!context.IsBoomplaySource
            || !BoomplayMetadataService.TryParseBoomplayUrl(context.Url, out var type, out var trackId)
            || !string.Equals(type, "track", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(trackId))
        {
            return;
        }

        try
        {
            var boomplayTrack = await _boomplayMetadataService.GetSongAsync(trackId, cancellationToken);
            if (boomplayTrack == null)
            {
                return;
            }

            context.BoomplayTrack = boomplayTrack;
            context.Title ??= Normalize(boomplayTrack.Title);
            context.Artist ??= Normalize(boomplayTrack.Artist);
            context.Album ??= Normalize(boomplayTrack.Album);
            context.Isrc ??= Normalize(boomplayTrack.Isrc);
            if (!context.DurationMs.HasValue && boomplayTrack.DurationMs > 0)
            {
                context.DurationMs = boomplayTrack.DurationMs;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed enriching Boomplay metadata while resolving Deezer ID for Url");
        }
    }

    private async Task<string?> ResolveDeezerIdAsync(ResolveRequestContext context, CancellationToken cancellationToken)
    {
        if (context.IsBoomplaySource)
        {
            var boomplayIsrcId = await TryResolveBoomplayIsrcFirstAsync(context, cancellationToken);
            if (!string.IsNullOrWhiteSpace(boomplayIsrcId))
            {
                return boomplayIsrcId;
            }
        }

        var metadataId = await TryResolveMetadataCandidatesAsync(context, cancellationToken);
        if (!string.IsNullOrWhiteSpace(metadataId))
        {
            return metadataId;
        }

        if (context.IsBoomplaySource)
        {
            var directBoomplayId = await TryResolveBoomplayDirectMetadataAsync(context, cancellationToken);
            if (!string.IsNullOrWhiteSpace(directBoomplayId))
            {
                return directBoomplayId;
            }

            var fallbackSearchId = await TryResolveBoomplaySearchFallbackAsync(context, cancellationToken);
            if (!string.IsNullOrWhiteSpace(fallbackSearchId))
            {
                return fallbackSearchId;
            }
        }
        else
        {
            var nonBoomplayIsrcId = await TryResolveNonBoomplayIsrcAsync(context, cancellationToken);
            if (!string.IsNullOrWhiteSpace(nonBoomplayIsrcId))
            {
                return nonBoomplayIsrcId;
            }
        }

        return await TryResolveBySongLinkAsync(context, cancellationToken);
    }

    private async Task<string?> TryResolveBoomplayIsrcFirstAsync(ResolveRequestContext context, CancellationToken cancellationToken)
    {
        if (!context.IsBoomplaySource || string.IsNullOrWhiteSpace(context.Isrc))
        {
            return null;
        }

        try
        {
            var isrcSummary = CreateTrackSummary(context, context.Title ?? string.Empty, context.Isrc);
            var deezerId = await SpotifyTracklistResolver.ResolveDeezerTrackIdAsync(
                _deezerClient,
                _songLinkResolver,
                isrcSummary,
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
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "ISRC-first Deezer resolve failed for Boomplay Url");
            return null;
        }
    }

    private async Task<string?> TryResolveMetadataCandidatesAsync(ResolveRequestContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Title) || string.IsNullOrWhiteSpace(context.Artist))
        {
            return null;
        }

        var titleCandidates = context.IsBoomplaySource
            ? BuildBoomplayTitleCandidates(context.Title, context.Album, context.Artist)
            : new[] { context.Title };
        var metadataStrictMode = context.IsBoomplaySource && !context.HasStreamTagMetadata;

        foreach (var titleCandidate in titleCandidates)
        {
            var resolvedId = await TryResolveMetadataForTitleCandidateAsync(
                context,
                titleCandidate,
                metadataStrictMode,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolvedId))
            {
                return resolvedId;
            }
        }

        return null;
    }

    private async Task<string?> TryResolveMetadataForTitleCandidateAsync(
        ResolveRequestContext context,
        string titleCandidate,
        bool metadataStrictMode,
        CancellationToken cancellationToken)
    {
        try
        {
            var summary = CreateTrackSummary(context, titleCandidate, isrc: null);
            var strictResult = await SpotifyTracklistResolver.ResolveDeezerTrackAsync(
                _deezerClient,
                _songLinkResolver,
                summary,
                CreateResolveOptions(
                    allowFallbackSearch: true,
                    preferIsrcOnly: false,
                    strictMode: metadataStrictMode,
                    bypassNegativeCanonicalCache: false,
                    cancellationToken));
            var strictId = await ValidateResolvedCandidateAsync(
                strictResult.DeezerId,
                context,
                titleCandidate,
                context.Artist,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(strictId) || !metadataStrictMode)
            {
                return strictId;
            }

            var relaxedResult = await SpotifyTracklistResolver.ResolveDeezerTrackAsync(
                _deezerClient,
                _songLinkResolver,
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
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Metadata Deezer resolve failed for Url");
            return null;
        }
    }

    private async Task<string?> TryResolveBoomplayDirectMetadataAsync(ResolveRequestContext context, CancellationToken cancellationToken)
    {
        if (!context.IsBoomplaySource
            || string.IsNullOrWhiteSpace(context.Title)
            || string.IsNullOrWhiteSpace(context.Artist))
        {
            return null;
        }

        var titleCandidates = BuildBoomplayTitleCandidates(context.Title, context.Album, context.Artist);
        foreach (var titleCandidate in titleCandidates)
        {
            var directId = await TryResolveSingleDirectBoomplayTitleAsync(
                context,
                titleCandidate,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(directId))
            {
                return directId;
            }
        }

        return null;
    }

    private async Task<string?> TryResolveSingleDirectBoomplayTitleAsync(
        ResolveRequestContext context,
        string titleCandidate,
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
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(validated))
            {
                return validated;
            }

            var primaryArtist = StripFeaturingFromArtist(context.Artist);
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
                    cancellationToken);
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Direct Boomplay Deezer resolve failed for Url");
            return null;
        }
    }

    private async Task<string?> TryResolveBoomplaySearchFallbackAsync(ResolveRequestContext context, CancellationToken cancellationToken)
    {
        if (!context.IsBoomplaySource || string.IsNullOrWhiteSpace(context.Title))
        {
            return null;
        }

        return await ResolveFromDeezerSearchFallbackAsync(
            context.Title,
            context.Artist,
            context.Album,
            context.Isrc,
            context.DurationMs,
            cancellationToken);
    }

    private async Task<string?> TryResolveNonBoomplayIsrcAsync(ResolveRequestContext context, CancellationToken cancellationToken)
    {
        if (context.IsBoomplaySource || string.IsNullOrWhiteSpace(context.Isrc))
        {
            return null;
        }

        try
        {
            var isrcSummary = CreateTrackSummary(context, context.Title ?? string.Empty, context.Isrc);
            return await SpotifyTracklistResolver.ResolveDeezerTrackIdAsync(
                _deezerClient,
                _songLinkResolver,
                isrcSummary,
                CreateResolveOptions(
                    allowFallbackSearch: false,
                    preferIsrcOnly: true,
                    strictMode: false,
                    bypassNegativeCanonicalCache: false,
                    cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "ISRC Deezer resolve failed for Url");
            return null;
        }
    }

    private async Task<string?> TryResolveBySongLinkAsync(ResolveRequestContext context, CancellationToken cancellationToken)
    {
        var songLink = await _songLinkResolver.ResolveByUrlAsync(context.Url, cancellationToken);
        var deezerId = songLink?.DeezerId ?? TryExtractDeezerTrackId(songLink?.DeezerUrl);
        if (string.IsNullOrWhiteSpace(deezerId))
        {
            return null;
        }

        if (!context.IsBoomplaySource)
        {
            return deezerId;
        }

        if (!HasAnySourceMetadata(context))
        {
            return null;
        }

        return await ValidateResolvedCandidateAsync(
            deezerId,
            context,
            context.Title,
            context.Artist,
            cancellationToken);
    }

    private static bool HasAnySourceMetadata(ResolveRequestContext context)
    {
        return !string.IsNullOrWhiteSpace(context.Title)
               || !string.IsNullOrWhiteSpace(context.Artist)
               || !string.IsNullOrWhiteSpace(context.Album)
               || !string.IsNullOrWhiteSpace(context.Isrc);
    }

    private async Task<string?> ValidateResolvedCandidateAsync(
        string? deezerId,
        ResolveRequestContext context,
        string? title,
        string? artist,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deezerId) || deezerId == "0")
        {
            return null;
        }

        var plausible = await IsPlausibleBoomplayDirectMatchAsync(
            deezerId,
            title,
            artist,
            context.Album,
            context.Isrc,
            context.DurationMs,
            cancellationToken);
        return plausible ? deezerId : null;
    }

    private static SpotifyTrackSummary CreateTrackSummary(
        ResolveRequestContext context,
        string trackTitle,
        string? isrc)
    {
        return new SpotifyTrackSummary(
            Id: string.Empty,
            Name: trackTitle,
            Artists: context.Artist,
            Album: context.Album,
            DurationMs: context.DurationMs,
            SourceUrl: context.Url,
            ImageUrl: null,
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
            UseSongLink: false,
            StrictMode: strictMode,
            BypassNegativeCanonicalCache: bypassNegativeCanonicalCache,
            Logger: _logger,
            CancellationToken: cancellationToken);
    }

    private async Task<object> BuildResolveResponseAsync(
        string deezerId,
        bool includeMeta)
    {
        if (!includeMeta)
        {
            return new { available = true, deezerId };
        }

        try
        {
            var trackData = await _deezerClient.GetTrack(deezerId);
            if (trackData != null)
            {
                var durationMs = trackData.Duration > 0 ? trackData.Duration * 1000 : (int?)null;
                var title = trackData.Title ?? string.Empty;
                var artist = trackData.Artist?.Name ?? string.Empty;
                var album = trackData.Album?.Title ?? string.Empty;
                var coverMedium = trackData.Album?.CoverMedium ?? string.Empty;

                return new
                {
                    available = true,
                    deezerId,
                    durationMs,
                    title,
                    artist,
                    album,
                    coverMedium
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to hydrate Deezer metadata for DeezerId");
        }

        return new { available = true, deezerId };
    }

    private Task<bool> IsPlausibleBoomplayDirectMatchAsync(
        string deezerId,
        string? sourceTitle,
        string? sourceArtist,
        string? sourceAlbum,
        string? sourceIsrc,
        int? sourceDurationMs,
        CancellationToken cancellationToken)
    {
        var source = new DeezerCandidateSource(
            sourceTitle,
            sourceArtist,
            sourceAlbum,
            sourceIsrc,
            sourceDurationMs);
        return DeezerCandidateMatchHelper.IsPlausibleCandidateAsync(
            deezerId,
            source,
            new DeezerCandidateValidationHandlers(
                TryGetValidationCandidateAsync,
                SourceAllowsDerivative,
                IsDerivativeCandidate,
                static _ => false),
            _logger,
            new DeezerCandidateValidationOptions(
                MinimumArtistScore: 0.24d,
                RejectDerivativeArtistName: false,
                ApplyVeryLowAlbumGuard: false,
                FailureLogMessage: "Failed to validate Boomplay direct Deezer candidate {DeezerId}"),
            cancellationToken);
    }

    private Task<string?> ResolveFromDeezerSearchFallbackAsync(
        string? sourceTitle,
        string? sourceArtist,
        string? sourceAlbum,
        string? sourceIsrc,
        int? sourceDurationMs,
        CancellationToken cancellationToken)
    {
        var source = new DeezerCandidateSource(
            sourceTitle,
            sourceArtist,
            sourceAlbum,
            sourceIsrc,
            sourceDurationMs);
        return DeezerCandidateMatchHelper.ResolveFromSearchFallbackAsync(
            _deezerClient,
            source,
            new DeezerFallbackSearchHandlers(
                (candidateId, token) => IsPlausibleBoomplayDirectMatchAsync(
                    candidateId,
                    source.SourceTitle,
                    source.SourceArtist,
                    source.SourceAlbum,
                    source.SourceIsrc,
                    source.SourceDurationMs,
                    token),
                (candidateId, album, token) => GetAlbumMatchScoreAsync(candidateId, album, token),
                TryGetValidationCandidateAsync,
                SourceAllowsDerivative,
                static _ => false),
            _logger,
            new DeezerFallbackSearchOptions(
                ExcludeDerivativeArtistCandidates: false,
                PreferBestAlbumMatch: false,
                SearchFailureLogMessage: "ResolveDeezer fallback search failed for query {Query}"),
            cancellationToken);
    }

    internal static string NormalizeFallbackSearchTitle(string? value)
    {
        var normalized = Normalize(value) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = ReplaceWithTimeout(
            normalized,
            @"\b(remix|mix|edit|version|official|audio|video|lyrics?|final|master)\b",
            " ",
            RegexOptions.IgnoreCase);
        normalized = BoomplayMultiWhitespaceRegex.Replace(normalized, " ").Trim(' ', '-', '_', ':', '.');
        return normalized;
    }

    internal static string ExtractLeadFallbackTitle(string? value)
    {
        var normalized = Normalize(value) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var dashIndex = normalized.IndexOf(" - ", StringComparison.OrdinalIgnoreCase);
        if (dashIndex > 0)
        {
            return normalized[..dashIndex].Trim();
        }

        return normalized;
    }

    internal static string NormalizeRelaxedTitleToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var relaxed = ReplaceWithTimeout(
            value,
            @"\b(remix|mix|edit|version|official|audio|video|lyrics?|final|master)\b",
            " ",
            RegexOptions.IgnoreCase);
        return BoomplayMultiWhitespaceRegex.Replace(relaxed, " ").Trim();
    }

    private Task<double> GetAlbumMatchScoreAsync(
        string deezerId,
        string? sourceAlbum,
        CancellationToken cancellationToken)
    {
        return DeezerCandidateMatchHelper.GetAlbumMatchScoreAsync(
            deezerId,
            sourceAlbum,
            TryGetValidationCandidateAsync,
            cancellationToken);
    }

    internal static IEnumerable<string> EnumerateSearchResultTrackIds(DeezerSearchResult result)
    {
        if (result.Data == null || result.Data.Length == 0)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in result.Data)
        {
            var id = TryGetTrackIdFromSearchResultItem(item);
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
            {
                yield return id;
            }
        }
    }

    private static string? TryGetTrackIdFromSearchResultItem(object? item)
    {
        return item switch
        {
            JsonElement element => TryGetTrackIdFromJsonElement(element),
            JObject jObject => NormalizeTrackId(jObject["id"]?.ToString()),
            _ => null
        };
    }

    private static string? TryGetTrackIdFromJsonElement(JsonElement element)
    {
        if (!element.TryGetProperty("id", out var idProperty))
        {
            return null;
        }

        var id = idProperty.ValueKind == JsonValueKind.Number
            ? idProperty.ToString()
            : idProperty.GetString();
        return NormalizeTrackId(id);
    }

    private static string? NormalizeTrackId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    internal static double GetBestArtistScore(string sourceArtistNorm, DeezSpoTag.Core.Models.Deezer.ApiTrack candidate)
    {
        if (string.IsNullOrWhiteSpace(sourceArtistNorm))
        {
            return 0d;
        }

        var best = 0d;
        foreach (var artistName in EnumerateCandidateArtists(candidate))
        {
            var score = ComputeSimilarity(sourceArtistNorm, NormalizeGuardArtist(artistName));
            if (score > best)
            {
                best = score;
            }
        }

        return best;
    }

    internal static double GetBestFeaturedArtistScore(string? sourceTitle, DeezSpoTag.Core.Models.Deezer.ApiTrack candidate)
    {
        var featuredArtists = ExtractFeaturedArtists(sourceTitle);
        if (featuredArtists.Count == 0)
        {
            return 0d;
        }

        var best = 0d;
        foreach (var sourceFeatured in featuredArtists)
        {
            foreach (var candidateArtist in EnumerateCandidateArtists(candidate))
            {
                var score = ComputeSimilarity(sourceFeatured, NormalizeGuardArtist(candidateArtist));
                if (score > best)
                {
                    best = score;
                }
            }
        }

        return best;
    }

    internal static IReadOnlyList<string> ExtractFeaturedArtists(string? sourceTitle)
    {
        var title = Normalize(sourceTitle) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return Array.Empty<string>();
        }

        var featured = new HashSet<string>(StringComparer.Ordinal);

        void AddParts(string? rawArtists)
        {
            if (string.IsNullOrWhiteSpace(rawArtists))
            {
                return;
            }

            var parts = SplitWithTimeout(rawArtists, @"\s*(?:,|&| and | x |;|/)\s*", RegexOptions.IgnoreCase);
            foreach (var normalized in parts
                         .Select(NormalizeGuardArtist)
                         .Where(normalized => !string.IsNullOrWhiteSpace(normalized)))
            {
                featured.Add(normalized);
            }
        }

        var match = FeaturedArtistsCaptureRegex.Match(title);
        if (match.Success)
        {
            AddParts(match.Groups["artists"].Value);
        }

        var dashIndex = title.IndexOf(" - ", StringComparison.OrdinalIgnoreCase);
        if (dashIndex > 0 && dashIndex < Math.Min(80, title.Length - 3))
        {
            var right = title[(dashIndex + 3)..];
            if (right.Contains('&') || right.Contains(',') || right.Contains(" x ", StringComparison.OrdinalIgnoreCase))
            {
                AddParts(right);
            }
        }

        return featured.Count == 0
            ? Array.Empty<string>()
            : featured.ToList();
    }

    internal static IEnumerable<string> EnumerateCandidateArtists(DeezSpoTag.Core.Models.Deezer.ApiTrack candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Artist?.Name))
        {
            yield return candidate.Artist.Name;
        }

        if (candidate.Contributors == null || candidate.Contributors.Count == 0)
        {
            yield break;
        }

        foreach (var contributorName in candidate.Contributors
                     .Where(contributor => !string.IsNullOrWhiteSpace(contributor?.Name))
                     .Select(contributor => contributor!.Name!))
        {
            yield return contributorName;
        }
    }

    private Task<(bool fetched, DeezSpoTag.Core.Models.Deezer.ApiTrack? track)> TryGetValidationCandidateAsync(
        string deezerId,
        CancellationToken cancellationToken)
    {
        return DeezerCandidateMatchHelper.TryGetValidationCandidateAsync(
            _deezerClient,
            _logger,
            deezerId,
            "Failed to load Deezer candidate {DeezerId} for resolve validation",
            cancellationToken);
    }

    internal static string? TryExtractDeezerTrackId(string? deezerUrl)
    {
        if (string.IsNullOrWhiteSpace(deezerUrl))
        {
            return null;
        }

        var match = DeezerTrackRegex.Match(deezerUrl);
        return match.Success ? match.Groups["id"].Value : null;
    }

    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    internal static IReadOnlyList<string> BuildBoomplayTitleCandidates(
        string? title,
        string? album,
        string? artist)
    {
        var candidates = new List<string>();
        void Add(string? value)
        {
            var normalized = Normalize(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (!candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(normalized);
            }
        }

        var normalizedTitle = Normalize(title);
        Add(normalizedTitle);

        if (!string.IsNullOrWhiteSpace(normalizedTitle))
        {
            var cleaned = CleanBoomplayNoisyTitle(normalizedTitle, artist);
            Add(cleaned);

            var withXTrimmed = RemoveXFeaturingTail(cleaned);
            Add(withXTrimmed);
        }

        if (IsLikelyNoisyBoomplayTitle(normalizedTitle))
        {
            Add(album);
        }

        if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(album))
        {
            Add(album);
        }

        return candidates.Count == 0
            ? Array.Empty<string>()
            : candidates;
    }

    internal static string CleanBoomplayNoisyTitle(string title, string? artist)
    {
        var cleaned = Normalize(title) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        cleaned = BoomplayAudioExtSuffixRegex.Replace(cleaned, string.Empty).Trim();
        cleaned = BoomplayOfficialSuffixRegex.Replace(cleaned, string.Empty).Trim();
        cleaned = BoomplayFinalSuffixRegex.Replace(cleaned, string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(artist))
        {
            var artistName = Normalize(artist) ?? string.Empty;
            if (cleaned.StartsWith(artistName + " - ", StringComparison.OrdinalIgnoreCase)
                || cleaned.StartsWith(artistName + " – ", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[(artistName.Length + 3)..].Trim();
            }
            else
            {
                var match = BoomplayArtistDashPrefixRegex.Match(cleaned);
                if (match.Success)
                {
                    var prefixArtist = Normalize(match.Groups["artist"].Value) ?? string.Empty;
                    if (prefixArtist.Equals(artistName, StringComparison.OrdinalIgnoreCase))
                    {
                        cleaned = Normalize(match.Groups["title"].Value) ?? cleaned;
                    }
                }
            }

            if (cleaned.StartsWith(artistName + " ", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[artistName.Length..].Trim();
            }
        }

        cleaned = BoomplayFeaturingTailRegex.Replace(cleaned, string.Empty).Trim();
        cleaned = BoomplayMultiWhitespaceRegex.Replace(cleaned, " ").Trim();
        return cleaned.Trim('-', '|', ':', '_', '&', '.', ' ');
    }

    internal static string RemoveXFeaturingTail(string? title)
    {
        var normalized = Normalize(title);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var split = normalized.Split(" x ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (split.Length >= 2)
        {
            return split[0];
        }

        split = normalized.Split(" X ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return split.Length >= 2 ? split[0] : normalized;
    }

    internal static string NormalizeGuardTitle(string? value)
    {
        var cleaned = Normalize(value) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        cleaned = BoomplayOfficialSuffixRegex.Replace(cleaned, string.Empty).Trim();
        cleaned = BoomplayFinalSuffixRegex.Replace(cleaned, string.Empty).Trim();
        cleaned = BoomplayMasterSuffixRegex.Replace(cleaned, string.Empty).Trim();
        cleaned = BoomplayAudioExtSuffixRegex.Replace(cleaned, string.Empty).Trim();
        cleaned = BoomplayFeaturingTailRegex.Replace(cleaned, string.Empty).Trim();
        return NormalizeGuardToken(cleaned);
    }

    internal static string NormalizeGuardArtist(string? value)
    {
        var cleaned = Normalize(value) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        cleaned = BoomplayFeaturingTailRegex.Replace(cleaned, string.Empty).Trim();
        var separators = new[] { ",", "&", " and ", " with ", " x " };
        foreach (var separator in separators)
        {
            var idx = cleaned.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                cleaned = cleaned[..idx].Trim();
            }
        }

        return NormalizeGuardToken(cleaned);
    }

    internal static string NormalizeGuardToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decoded = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decoded.Length);
        foreach (var ch in decoded.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark))
        {
            sb.Append(ch);
        }

        var normalized = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        normalized = ReplaceWithTimeout(normalized, @"[^\p{L}\p{Nd}]+", " ");
        normalized = BoomplayMultiWhitespaceRegex.Replace(normalized, " ").Trim();
        return normalized;
    }

    internal static string? NormalizeIsrc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace("-", string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length != 12)
        {
            return null;
        }

        if (!normalized.All(char.IsLetterOrDigit))
        {
            return null;
        }

        return normalized;
    }

    private static bool SourceAllowsDerivative(string? title, string? artist, string? album)
    {
        var combined = NormalizeGuardToken($"{title} {artist} {album}");
        if (string.IsNullOrWhiteSpace(combined))
        {
            return false;
        }

        return DerivativeMarkers.Any(marker => ContainsWholeMarker(combined, marker));
    }

    private static bool IsDerivativeCandidate(DeezSpoTag.Core.Models.Deezer.ApiTrack candidate)
    {
        if (candidate == null)
        {
            return false;
        }

        var combined = NormalizeGuardToken($"{candidate.Title} {candidate.TitleVersion} {candidate.Album?.Title} {candidate.Artist?.Name}");
        if (string.IsNullOrWhiteSpace(combined))
        {
            return false;
        }

        return DerivativeMarkers.Any(marker => ContainsWholeMarker(combined, marker));
    }

    private static bool ContainsWholeMarker(string text, string marker)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(marker))
        {
            return false;
        }

        var normalizedMarker = NormalizeGuardToken(marker);
        if (string.IsNullOrWhiteSpace(normalizedMarker))
        {
            return false;
        }

        var paddedText = $" {text} ";
        var paddedMarker = $" {normalizedMarker} ";
        return paddedText.Contains(paddedMarker, StringComparison.Ordinal);
    }

    internal static double ComputeSimilarity(string source, string candidate)
    {
        return TextMatchUtils.ComputeNormalizedSimilarity(source, candidate);
    }

    internal static int LevenshteinDistance(string s1, string s2)
    {
        return TextMatchUtils.LevenshteinDistance(s1, s2);
    }

    internal static bool IsLikelyNoisyBoomplayTitle(string? title)
    {
        var normalized = Normalize(title);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        return BoomplayOfficialSuffixRegex.IsMatch(normalized)
               || BoomplayFinalSuffixRegex.IsMatch(normalized)
               || BoomplayAudioExtSuffixRegex.IsMatch(normalized)
               || normalized.Contains("official audio", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("official video", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("finished", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex FeaturingArtistRegex = CreateRegex(
        @"\s*(?:\(|\[)?\s*(?:feat\.?|ft\.?|featuring|with|x)\s+.+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static string StripFeaturingFromArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return string.Empty;
        }

        var stripped = FeaturingArtistRegex.Replace(artist, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(stripped) ? artist.Trim() : stripped;
    }
}
