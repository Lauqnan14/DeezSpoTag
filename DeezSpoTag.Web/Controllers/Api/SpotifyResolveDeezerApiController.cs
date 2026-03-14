using System.Text.Json;
using System.Linq;
using DeezSpoTag.Core.Models.Deezer;
using DeezSpoTag.Integrations.Deezer;
using DeezSpoTag.Services.Download.Utils;
using DeezSpoTag.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/spotify/resolve-deezer")]
[Authorize]
public class SpotifyResolveDeezerApiController : ControllerBase
{
    private const string TrackType = "track";
    private const string AlbumType = "album";
    private const string ArtistType = "artist";
    private const string TitleKey = "title";
    private const string NameKey = "name";
    private const double ArtistDiscographyThreshold = 0.80;
    private const double ArtistNameOnlyThreshold = 0.98;
    private const double ArtistNameOnlyMinimumGap = 0.15;
    private const int ArtistCandidateEvaluationLimit = 3;
    private readonly SpotifyMetadataService _metadataService;
    private readonly SpotifyDeezerAlbumResolver _spotifyDeezerAlbumResolver;
    private readonly DeezerClient _deezerClient;
    private readonly SongLinkResolver _songLinkResolver;
    private readonly ILogger<SpotifyResolveDeezerApiController> _logger;

    public SpotifyResolveDeezerApiController(
        SpotifyMetadataService metadataService,
        SpotifyDeezerAlbumResolver spotifyDeezerAlbumResolver,
        DeezerClient deezerClient,
        SongLinkResolver songLinkResolver,
        ILogger<SpotifyResolveDeezerApiController> logger)
    {
        _metadataService = metadataService;
        _spotifyDeezerAlbumResolver = spotifyDeezerAlbumResolver;
        _deezerClient = deezerClient;
        _songLinkResolver = songLinkResolver;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "URL is required." });
        }

        var metadata = await _metadataService.FetchByUrlAsync(url, cancellationToken);
        if (metadata == null)
        {
            return Ok(new { available = false });
        }

        switch (metadata.Type)
        {
            case TrackType:
                return Ok(await ResolveTrackAsync(metadata, cancellationToken));
            case AlbumType:
                return Ok(await ResolveAlbumAsync(metadata, cancellationToken));
            case ArtistType:
                return Ok(await ResolveArtistAsync(metadata));
            default:
                return Ok(new { available = false });
        }
    }

    private async Task<object> ResolveTrackAsync(SpotifyUrlMetadata metadata, CancellationToken cancellationToken)
    {
        var track = metadata.TrackList.FirstOrDefault();
        if (track == null)
        {
            return new { available = false };
        }

        try
        {
            var resolved = await SpotifyTracklistResolver.ResolveDeezerTrackAsync(
                _deezerClient,
                _songLinkResolver,
                track,
                new SpotifyTrackResolveOptions(
                    AllowFallbackSearch: true,
                    PreferIsrcOnly: false,
                    UseSongLink: false,
                    StrictMode: true,
                    BypassNegativeCanonicalCache: false,
                    Logger: _logger,
                    CancellationToken: cancellationToken));

            var deezerId = resolved.DeezerId;
            if (string.IsNullOrWhiteSpace(deezerId) || deezerId == "0")
            {
                return new { available = false, reason = resolved.Reason };
            }

            return new
            {
                available = true,
                type = TrackType,
                deezerId,
                deezerUrl = $"https://www.deezer.com/track/{deezerId}"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve Deezer track for SpotifyUrl");
        }

        return new { available = false };
    }

    private async Task<object> ResolveAlbumAsync(SpotifyUrlMetadata metadata, CancellationToken cancellationToken)
    {
        var deezerId = await _spotifyDeezerAlbumResolver.ResolveAlbumIdAsync(metadata, cancellationToken);

        if (string.IsNullOrWhiteSpace(deezerId))
        {
            return new { available = false };
        }

        return new
        {
            available = true,
            type = AlbumType,
            deezerId,
            deezerUrl = $"https://www.deezer.com/album/{deezerId}"
        };
    }

    private async Task<object> ResolveArtistAsync(SpotifyUrlMetadata metadata)
    {
        var query = metadata.Name;
        var match = await ResolveArtistMatchAsync(query, metadata);
        if (match is null || string.IsNullOrWhiteSpace(match.DeezerId))
        {
            return new { available = false };
        }

        var strongDiscographyMatch = match.Confidence >= ArtistDiscographyThreshold
            && match.SpotifyTitleCount > 0
            && match.DeezerTitleCount > 0
            && match.OverlapCount > 0;
        var strongNameOnlyMatch = match.SpotifyTitleCount == 0
            && match.NameScore >= ArtistNameOnlyThreshold
            && match.NameGap >= ArtistNameOnlyMinimumGap;

        if (!strongDiscographyMatch && !strongNameOnlyMatch)
        {
            return new
            {
                available = false,
                type = ArtistType,
                artistName = metadata.Name,
                deezerId = match.DeezerId,
                confidence = match.Confidence,
                nameScore = match.NameScore,
                nameGap = match.NameGap,
                spotifyCoverage = match.SpotifyCoverage,
                deezerCoverage = match.DeezerCoverage,
                overlapCount = match.OverlapCount,
                spotifyTitleCount = match.SpotifyTitleCount,
                deezerTitleCount = match.DeezerTitleCount,
                matchMode = match.MatchMode
            };
        }

        return new
        {
            available = true,
            type = ArtistType,
            artistName = metadata.Name,
            deezerId = match.DeezerId,
            deezerUrl = $"https://www.deezer.com/artist/{match.DeezerId}",
            confidence = match.Confidence,
            nameScore = match.NameScore,
            nameGap = match.NameGap,
            spotifyCoverage = match.SpotifyCoverage,
            deezerCoverage = match.DeezerCoverage,
            overlapCount = match.OverlapCount,
            spotifyTitleCount = match.SpotifyTitleCount,
            deezerTitleCount = match.DeezerTitleCount,
            matchMode = match.MatchMode
        };
    }

    private async Task<ArtistMatchResult?> ResolveArtistMatchAsync(
        string query,
        SpotifyUrlMetadata spotifyMetadata)
    {
        var result = await _deezerClient.SearchArtistAsync(query, new ApiOptions { Limit = 10 });
        var candidates = ParseArtistCandidates(result, spotifyMetadata.Name)
            .Take(ArtistCandidateEvaluationLimit)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var spotifyTitles = ExtractSpotifyDiscographyTitles(spotifyMetadata);
        var secondBestNameScore = candidates.Skip(1).Select(candidate => candidate.NameScore).DefaultIfEmpty(0).Max();
        if (spotifyTitles.Count == 0)
        {
            var bestByName = candidates.FirstOrDefault();
            if (bestByName is null)
            {
                return null;
            }

            var gap = Math.Max(0, bestByName.NameScore - secondBestNameScore);
            return new ArtistMatchResult(
                bestByName.Id,
                bestByName.NameScore,
                bestByName.NameScore,
                gap,
                0,
                0,
                0,
                0,
                0,
                "name");
        }

        ArtistMatchResult? best = null;
        foreach (var candidate in candidates)
        {
            var deezerTitles = await FetchDeezerDiscographyTitlesAsync(candidate.Id);
            if (deezerTitles.Count == 0)
            {
                continue;
            }

            var overlapCount = spotifyTitles.Count(title => deezerTitles.Contains(title));
            var spotifyCoverage = overlapCount / (double)spotifyTitles.Count;
            var deezerCoverage = overlapCount / (double)deezerTitles.Count;
            var overlapCoverage = overlapCount / (double)Math.Min(spotifyTitles.Count, deezerTitles.Count);
            var confidence = (overlapCoverage * 0.7) + (candidate.NameScore * 0.3);
            var current = new ArtistMatchResult(
                candidate.Id,
                confidence,
                candidate.NameScore,
                Math.Max(0, candidate.NameScore - secondBestNameScore),
                spotifyCoverage,
                deezerCoverage,
                overlapCount,
                spotifyTitles.Count,
                deezerTitles.Count,
                "discography");
            if (best is null || current.Confidence > best.Confidence)
            {
                best = current;
            }
        }

        if (best != null)
        {
            return best;
        }

        var fallback = candidates.FirstOrDefault();
        if (fallback is null)
        {
            return null;
        }

        return new ArtistMatchResult(
            fallback.Id,
            fallback.NameScore,
            fallback.NameScore,
            Math.Max(0, fallback.NameScore - secondBestNameScore),
            0,
            0,
            0,
            spotifyTitles.Count,
            0,
            "name");
    }

    private async Task<HashSet<string>> FetchDeezerDiscographyTitlesAsync(string artistId)
    {
        try
        {
            var tabs = await _deezerClient.GetArtistDiscographyTabsAsync(artistId, 100);
            return ExtractDeezerTitlesFromTabs(tabs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to fetch Deezer discography for artist {ArtistId}", artistId);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<ArtistCandidate> ParseArtistCandidates(DeezerSearchResult result, string artistName)
    {
        if (result.Data == null || result.Data.Length == 0)
        {
            return Enumerable.Empty<ArtistCandidate>();
        }

        var target = SpotifyTextNormalizer.NormalizeToken(artistName);
        var candidates = new List<ArtistCandidate>();
        foreach (var item in result.Data)
        {
            if (item is not JsonElement element)
            {
                continue;
            }

            var id = GetString(element, "id");
            var name = GetString(element, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var normalizedName = SpotifyTextNormalizer.NormalizeToken(name);
            var score = string.IsNullOrWhiteSpace(target)
                ? 0
                : SpotifyTextNormalizer.ComputeCoverageScore(target, normalizedName);
            var fans = GetInt(element, "nb_fan") ?? 0;
            candidates.Add(new ArtistCandidate(id, score, fans));
        }

        return candidates
            .OrderByDescending(candidate => candidate.NameScore)
            .ThenByDescending(candidate => candidate.Fans)
            .ThenBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> ExtractSpotifyDiscographyTitles(SpotifyUrlMetadata metadata)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var normalized in metadata.AlbumList
                     .Select(album => SpotifyTextNormalizer.NormalizeTrackToken(album.Name))
                     .Where(static normalized => !string.IsNullOrWhiteSpace(normalized)))
        {
            set.Add(normalized);
        }

        return set;
    }

    private static HashSet<string> ExtractDeezerTitlesFromTabs(Dictionary<string, List<object>> tabs)
    {
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var normalized in tabs.Values
                     .SelectMany(tab => tab)
                     .Select(static item => item switch
                     {
                         Dictionary<string, object> dict => GetDictionaryString(dict, TitleKey) ?? GetDictionaryString(dict, NameKey),
                         JsonElement element => GetString(element, TitleKey) ?? GetString(element, NameKey),
                         _ => null
                     })
                     .Select(SpotifyTextNormalizer.NormalizeTrackToken)
                     .Where(static normalized => !string.IsNullOrWhiteSpace(normalized)))
        {
            titles.Add(normalized);
        }

        return titles;
    }

    private static string? GetDictionaryString(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            string str => str,
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString(),
            _ => value.ToString()
        };
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return value.ValueKind == JsonValueKind.Number
            ? value.ToString()
            : null;
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
        {
            return number;
        }

        return null;
    }

    private sealed record ArtistCandidate(string Id, double NameScore, int Fans);
    private sealed record ArtistMatchResult(
        string DeezerId,
        double Confidence,
        double NameScore,
        double NameGap,
        double SpotifyCoverage,
        double DeezerCoverage,
        int OverlapCount,
        int SpotifyTitleCount,
        int DeezerTitleCount,
        string MatchMode);
}
