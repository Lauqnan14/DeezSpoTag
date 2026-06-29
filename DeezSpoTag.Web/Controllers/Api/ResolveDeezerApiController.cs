using System.Text.RegularExpressions;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Core.Utils;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Utils;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/resolve/deezer")]
[Authorize]
public sealed class ResolveDeezerApiController : ControllerBase
{
    private static readonly TimeSpan SpotifyHydrationCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SpotifyHydrationCacheEntry> SpotifyHydrationCache =
        new(StringComparer.OrdinalIgnoreCase);
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
    private static Regex CreateRegex(string pattern, RegexOptions options)
        => new(pattern, options, RegexTimeout);
    private static string ReplaceWithTimeout(string input, string pattern, string replacement, RegexOptions options = RegexOptions.None)
        => Regex.Replace(input, pattern, replacement, options, RegexTimeout);
    private static string[] SplitWithTimeout(string input, string pattern, RegexOptions options = RegexOptions.None)
        => Regex.Split(input, pattern, options, RegexTimeout);

    private readonly DeezerClient _deezerClient;
    private readonly BoomplayDeezerMatchService _boomplayDeezerMatchService;
    private readonly SpotifyMetadataService _spotifyMetadataService;
    private readonly ILogger<ResolveDeezerApiController> _logger;

    public ResolveDeezerApiController(
        DeezerClient deezerClient,
        BoomplayDeezerMatchService boomplayDeezerMatchService,
        SpotifyMetadataService spotifyMetadataService,
        ILogger<ResolveDeezerApiController> logger)
    {
        _deezerClient = deezerClient;
        _boomplayDeezerMatchService = boomplayDeezerMatchService;
        _spotifyMetadataService = spotifyMetadataService;
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
        public required bool IsSpotifyTrackSource { get; init; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Isrc { get; set; }
        public int? DurationMs { get; set; }
        public string? SpotifyTrackId { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] ResolveDeezerRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest(new { available = false, reasonCode = "missing_url", error = "URL is required." });
        }

        var context = CreateResolveRequestContext(request);
        var directId = TryExtractDeezerTrackId(context.Url);
        if (!string.IsNullOrWhiteSpace(directId))
        {
            return Ok(await BuildResolveResponseAsync(directId, context.IncludeMeta));
        }

        await HydrateSpotifyTrackContextAsync(context, cancellationToken);

        if (context.IsBoomplaySource)
        {
            var boomplayMatch = await _boomplayDeezerMatchService.ResolveAsync(
                new BoomplayDeezerMatchRequest(
                    context.Url,
                    context.Title,
                    context.Artist,
                    context.Album,
                    context.Isrc,
                    context.DurationMs),
                cancellationToken);
            if (boomplayMatch == null)
            {
                return Ok(new { available = false, reasonCode = "no_match" });
            }

            return Ok(BuildBoomplayResolveResponse(boomplayMatch, context.IncludeMeta));
        }

        var deezerId = await ResolveDeezerIdAsync(context, cancellationToken);
        if (string.IsNullOrWhiteSpace(deezerId))
        {
            return Ok(new { available = false, reasonCode = "no_match" });
        }

        return Ok(await BuildResolveResponseAsync(deezerId, context.IncludeMeta));
    }

    private static object BuildBoomplayResolveResponse(BoomplayDeezerMatchResult match, bool includeMeta)
    {
        if (!includeMeta)
        {
            return new { available = true, deezerId = match.DeezerId };
        }

        return new
        {
            available = true,
            deezerId = match.DeezerId,
            durationMs = match.DurationMs,
            title = match.Title,
            artist = match.Artist,
            album = match.Album,
            coverMedium = match.CoverMedium
        };
    }

    private static ResolveRequestContext CreateResolveRequestContext(ResolveDeezerRequest request)
    {
        var normalizedUrl = request.Url!.Trim();
        var spotifyTrackId = TrackIdNormalization.ExtractSpotifyTrackIdFromUrl(normalizedUrl);
        return new ResolveRequestContext
        {
            Url = normalizedUrl,
            IncludeMeta = request.IncludeMeta == true,
            IsBoomplaySource = BoomplayMetadataService.IsBoomplayUrl(normalizedUrl),
            IsSpotifyTrackSource = !string.IsNullOrWhiteSpace(spotifyTrackId),
            Title = Normalize(request.Title),
            Artist = Normalize(request.Artist),
            Album = Normalize(request.Album),
            Isrc = Normalize(request.Isrc),
            DurationMs = request.DurationMs.HasValue && request.DurationMs.Value > 0
                ? request.DurationMs.Value
                : (int?)null,
            SpotifyTrackId = spotifyTrackId
        };
    }

    private async Task<string?> ResolveDeezerIdAsync(ResolveRequestContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Title)
            && string.IsNullOrWhiteSpace(context.Artist)
            && string.IsNullOrWhiteSpace(context.Isrc))
        {
            return null;
        }

        try
        {
            var summary = CreateTrackSummary(context);
            var result = await SpotifyTracklistResolver.ResolveDeezerTrackAsync(
                _deezerClient,
                summary,
                CreateResolveOptions(
                    allowFallbackSearch: true,
                    preferIsrcOnly: false,
                    strictMode: true,
                    bypassNegativeCanonicalCache: false,
                    cancellationToken));
            return result.DeezerId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Metadata Deezer resolve failed for Url");
            return null;
        }
    }

    private async Task HydrateSpotifyTrackContextAsync(
        ResolveRequestContext context,
        CancellationToken cancellationToken)
    {
        if (!context.IsSpotifyTrackSource || !NeedsSpotifyHydration(context))
        {
            return;
        }

        try
        {
            var spotifyTrackId = context.SpotifyTrackId?.Trim();
            if (!string.IsNullOrWhiteSpace(spotifyTrackId)
                && TryGetCachedSpotifyHydration(spotifyTrackId, out var cachedTrack))
            {
                ApplySpotifyHydration(context, cachedTrack);
                return;
            }

            var metadata = await _spotifyMetadataService.FetchByUrlAsync(
                context.Url,
                cancellationToken,
                hydrateTracks: true);
            var track = metadata?.TrackList.FirstOrDefault();
            if (track == null)
            {
                return;
            }

            ApplySpotifyHydration(context, track);
            if (!string.IsNullOrWhiteSpace(spotifyTrackId))
            {
                CacheSpotifyHydration(spotifyTrackId, track);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Spotify metadata hydration failed for Deezer resolve Url");
        }
    }

    private static bool NeedsSpotifyHydration(ResolveRequestContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Title) || string.IsNullOrWhiteSpace(context.Artist))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(context.Isrc) && context.DurationMs is not > 0;
    }

    private static bool TryGetCachedSpotifyHydration(string spotifyTrackId, out SpotifyTrackSummary track)
    {
        track = null!;
        if (!SpotifyHydrationCache.TryGetValue(spotifyTrackId, out var entry))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - entry.Stamp > SpotifyHydrationCacheTtl)
        {
            SpotifyHydrationCache.TryRemove(spotifyTrackId, out _);
            return false;
        }

        track = entry.Track;
        return true;
    }

    private static void CacheSpotifyHydration(string spotifyTrackId, SpotifyTrackSummary track)
    {
        SpotifyHydrationCache[spotifyTrackId] = new SpotifyHydrationCacheEntry(DateTimeOffset.UtcNow, track);
        if (SpotifyHydrationCache.Count <= 512)
        {
            return;
        }

        foreach (var key in SpotifyHydrationCache
                     .OrderBy(pair => pair.Value.Stamp)
                     .Take(Math.Max(1, SpotifyHydrationCache.Count - 512))
                     .Select(pair => pair.Key))
        {
            SpotifyHydrationCache.TryRemove(key, out _);
        }
    }

    private static void ApplySpotifyHydration(
        ResolveRequestContext context,
        SpotifyTrackSummary track)
    {
        context.Title = FirstNonEmpty(track.Name, context.Title);
        context.Artist = FirstNonEmpty(context.Artist, track.Artists);
        context.Album = FirstNonEmpty(track.Album, context.Album);
        context.Isrc = FirstNonEmpty(track.Isrc, context.Isrc);
        context.DurationMs = track.DurationMs is > 0 ? track.DurationMs : context.DurationMs;
        context.SpotifyTrackId = FirstNonEmpty(track.Id, context.SpotifyTrackId);
    }

    private static string? FirstNonEmpty(string? preferred, string? fallback)
        => !string.IsNullOrWhiteSpace(preferred) ? preferred.Trim() : Normalize(fallback);

    private sealed record SpotifyHydrationCacheEntry(DateTimeOffset Stamp, SpotifyTrackSummary Track);

    private static SpotifyTrackSummary CreateTrackSummary(ResolveRequestContext context)
    {
        return new SpotifyTrackSummary(
            Id: context.SpotifyTrackId ?? string.Empty,
            Name: context.Title ?? string.Empty,
            Artists: context.Artist,
            Album: context.Album,
            DurationMs: context.DurationMs,
            SourceUrl: context.Url,
            ImageUrl: null,
            Isrc: context.Isrc);
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

    internal static IEnumerable<string> EnumerateSearchResultTrackIds(DeezerSearchResult result)
    {
        if (result.Data == null || result.Data.Length == 0)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in result.Data
            .Select(TryGetTrackIdFromSearchResultItem)
            .Where(id => !string.IsNullOrWhiteSpace(id) && seen.Add(id))
            .Select(id => id!))
        {
            yield return id;
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
