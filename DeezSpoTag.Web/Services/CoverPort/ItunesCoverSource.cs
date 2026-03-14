using System.Text.Json;

namespace DeezSpoTag.Web.Services.CoverPort;

public sealed class ItunesCoverSource : ICoverSource
{
    private static readonly int[] CandidateSizes = { 5000, 1200, 600 };
    private static readonly string[] CandidateFormats = { "png", "jpg" };
    private readonly record struct ItunesSearchCandidate(
        string? Artist,
        string? Album,
        string ArtworkUrl,
        double Confidence,
        bool Fuzzy,
        int Rank);

    private readonly CoverSourceHttpService _httpService;
    private readonly ILogger<ItunesCoverSource> _logger;

    public ItunesCoverSource(CoverSourceHttpService httpService, ILogger<ItunesCoverSource> logger)
    {
        _httpService = httpService;
        _logger = logger;
    }

    public CoverSourceName Name => CoverSourceName.Itunes;

    public async Task<IReadOnlyList<CoverCandidate>> SearchAsync(CoverSearchQuery query, CancellationToken cancellationToken)
    {
        var normalizedArtist = NormalizeForItunes(query.Artist);
        var normalizedAlbum = NormalizeForItunes(query.Album);
        var term = $"{normalizedArtist} {normalizedAlbum}".Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            return Array.Empty<CoverCandidate>();
        }

        var requestUrl = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(term)}&media=music&entity=album&limit=25";
        try
        {
            using var document = await _httpService.GetJsonDocumentAsync(Name, requestUrl, cancellationToken);
            if (document == null)
            {
                return Array.Empty<CoverCandidate>();
            }

            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CoverCandidate>();
            }

            var candidates = new List<CoverCandidate>();
            var rank = 0;
            foreach (var result in results.EnumerateArray())
            {
                if (!TryBuildSearchCandidate(result, query, normalizedArtist, normalizedAlbum, rank, out var candidate))
                {
                    continue;
                }

                await AddSizedCandidatesAsync(candidates, candidate, cancellationToken);
                rank++;
            }

            return candidates;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "iTunes cover search errored for {Artist} - {Album}", query.Artist, query.Album);
            return Array.Empty<CoverCandidate>();
        }
    }

    private static bool TryBuildSearchCandidate(
        JsonElement result,
        CoverSearchQuery query,
        string normalizedArtist,
        string normalizedAlbum,
        int rank,
        out ItunesSearchCandidate candidate)
    {
        candidate = default;
        var artist = result.TryGetProperty("artistName", out var artistEl) ? artistEl.GetString() : null;
        var album = result.TryGetProperty("collectionName", out var albumEl) ? albumEl.GetString() : null;
        var artworkUrl = ExtractArtworkUrl(result);
        if (string.IsNullOrWhiteSpace(artworkUrl))
        {
            return false;
        }

        var normalizedResultArtist = NormalizeForItunes(artist);
        var normalizedResultAlbum = NormalizeForItunes(album);
        if (!normalizedResultAlbum.StartsWith(normalizedAlbum, StringComparison.Ordinal) ||
            !string.Equals(normalizedResultArtist, normalizedArtist, StringComparison.Ordinal))
        {
            return false;
        }

        var fuzzy = !string.Equals(normalizedResultAlbum, normalizedAlbum, StringComparison.Ordinal);
        candidate = new ItunesSearchCandidate(
            Artist: artist,
            Album: album,
            ArtworkUrl: artworkUrl,
            Confidence: CoverTextNormalizer.ComputeMatchConfidence(query, artist, album),
            Fuzzy: fuzzy,
            Rank: rank);
        return true;
    }

    private async Task AddSizedCandidatesAsync(
        List<CoverCandidate> candidates,
        ItunesSearchCandidate searchCandidate,
        CancellationToken cancellationToken)
    {
        foreach (var candidateSize in CandidateSizes)
        {
            foreach (var candidateFormat in CandidateFormats)
            {
                var candidateUrl = BuildCandidateUrl(searchCandidate.ArtworkUrl, candidateSize, candidateFormat);
                if (string.IsNullOrWhiteSpace(candidateUrl))
                {
                    continue;
                }

                var exists = await _httpService.ProbeUrlExistsAsync(Name, candidateUrl, cancellationToken);
                if (!exists)
                {
                    continue;
                }

                candidates.Add(new CoverCandidate(
                    Source: Name,
                    Url: candidateUrl,
                    Width: candidateSize,
                    Height: candidateSize,
                    Format: candidateFormat == "png" ? "png" : "jpg",
                    SourceReliability: 0.88d,
                    MatchConfidence: searchCandidate.Confidence,
                    Artist: searchCandidate.Artist,
                    Album: searchCandidate.Album,
                    Rank: searchCandidate.Rank,
                    Relevance: new CoverRelevance(
                        Fuzzy: searchCandidate.Fuzzy,
                        OnlyFrontCovers: true,
                        UnrelatedRisk: false)));
            }
        }
    }

    private static string? ExtractArtworkUrl(JsonElement result)
    {
        if (result.TryGetProperty("artworkUrl60", out var artwork60))
        {
            return artwork60.GetString();
        }

        if (result.TryGetProperty("artworkUrl100", out var artwork100))
        {
            return artwork100.GetString();
        }

        return null;
    }

    private static string? BuildCandidateUrl(string url, int size, string format)
    {
        if (string.IsNullOrWhiteSpace(url) || size <= 0)
        {
            return null;
        }

        var split = url.LastIndexOf('/');
        if (split < 0)
        {
            return null;
        }

        var baseUrl = url.Substring(0, split);
        var suffix = string.Equals(format, "png", StringComparison.OrdinalIgnoreCase) ? ".png" : "-100.jpg";
        return $"{baseUrl}/{size}x{size}{suffix}";
    }

    private static string NormalizeForItunes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = CoverTextNormalizer.Normalize(value);
        return new string(normalized.Where(static ch => ch != ' ').ToArray());
    }
}
