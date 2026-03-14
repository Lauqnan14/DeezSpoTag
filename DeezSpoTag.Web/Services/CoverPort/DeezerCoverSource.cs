using System.Text.Json;

namespace DeezSpoTag.Web.Services.CoverPort;

public sealed class DeezerCoverSource : ICoverSource
{
    private readonly record struct AlbumCoverContext(string? Artist, string? Album, double Confidence, bool Fuzzy);

    private readonly CoverSourceHttpService _httpService;
    private readonly ILogger<DeezerCoverSource> _logger;

    public DeezerCoverSource(CoverSourceHttpService httpService, ILogger<DeezerCoverSource> logger)
    {
        _httpService = httpService;
        _logger = logger;
    }

    public CoverSourceName Name => CoverSourceName.Deezer;

    public async Task<IReadOnlyList<CoverCandidate>> SearchAsync(CoverSearchQuery query, CancellationToken cancellationToken)
    {
        var requestQuery = $"artist:\"{query.Artist}\" album:\"{query.Album}\"";
        var requestUrl = $"https://api.deezer.com/search/album?q={Uri.EscapeDataString(requestQuery)}&limit=25";

        try
        {
            using var document = await _httpService.GetJsonDocumentAsync(Name, requestUrl, cancellationToken);
            if (document == null)
            {
                return Array.Empty<CoverCandidate>();
            }
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CoverCandidate>();
            }

            var candidates = new List<CoverCandidate>();
            var seenAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rank = 0;
            foreach (var row in data.EnumerateArray())
            {
                if (!TryBuildAlbumCoverContext(row, query, seenAlbums, out var context))
                {
                    continue;
                }

                var artworkOptions = EnumerateArtworks(row).ToList();
                if (artworkOptions.Count == 0)
                {
                    continue;
                }

                AddArtworkCandidates(candidates, context, artworkOptions, rank);
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
            _logger.LogDebug(ex, "Deezer cover search errored for {Artist} - {Album}", query.Artist, query.Album);
            return Array.Empty<CoverCandidate>();
        }
    }

    private static bool TryBuildAlbumCoverContext(
        JsonElement row,
        CoverSearchQuery query,
        HashSet<string> seenAlbums,
        out AlbumCoverContext context)
    {
        context = default;
        var artist = TryReadNestedString(row, "artist", "name");
        var album = row.TryGetProperty("title", out var albumEl) ? albumEl.GetString() : null;
        var artistId = TryReadNestedString(row, "artist", "id");
        var albumId = row.TryGetProperty("id", out var albumIdEl) ? albumIdEl.GetRawText() : null;
        var key = $"{artistId}:{albumId}";
        if (!string.IsNullOrWhiteSpace(key) && !seenAlbums.Add(key))
        {
            return false;
        }

        var confidence = CoverTextNormalizer.ComputeMatchConfidence(query, artist, album);
        var fuzzy = !string.Equals(
                        CoverTextNormalizer.Normalize(query.Artist),
                        CoverTextNormalizer.Normalize(artist),
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        CoverTextNormalizer.Normalize(query.Album),
                        CoverTextNormalizer.Normalize(album),
                        StringComparison.Ordinal);
        context = new AlbumCoverContext(artist, album, confidence, fuzzy);
        return true;
    }

    private CoverCandidate BuildCoverCandidate(AlbumCoverContext context, (string url, int size) artwork, int rank)
    {
        return new CoverCandidate(
            Source: Name,
            Url: artwork.url,
            Width: artwork.size,
            Height: artwork.size,
            Format: "jpg",
            SourceReliability: 0.9d,
            MatchConfidence: context.Confidence,
            Artist: context.Artist,
            Album: context.Album,
            Rank: rank,
            Relevance: new CoverRelevance(
                Fuzzy: context.Fuzzy,
                OnlyFrontCovers: true,
                UnrelatedRisk: false));
    }

    private void AddArtworkCandidates(
        List<CoverCandidate> candidates,
        AlbumCoverContext context,
        IReadOnlyList<(string url, int size)> artworkOptions,
        int rank)
    {
        foreach (var artwork in artworkOptions)
        {
            candidates.Add(BuildCoverCandidate(context, artwork, rank));
        }
    }

    private static IEnumerable<(string url, int size)> EnumerateArtworks(JsonElement row)
    {
        var fields = new (string property, int size)[]
        {
            ("cover_medium", 250),
            ("cover_big", 500),
            ("cover_xl", 1000),
            ("cover_small", 56),
        };

        foreach (var field in fields)
        {
            if (!row.TryGetProperty(field.property, out var imageEl))
            {
                continue;
            }

            var url = imageEl.GetString();
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            yield return (url, field.size);
        }
    }

    private static string? TryReadNestedString(JsonElement element, string objectProperty, string valueProperty)
    {
        if (!element.TryGetProperty(objectProperty, out var objectEl) || objectEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!objectEl.TryGetProperty(valueProperty, out var valueEl))
        {
            return null;
        }

        return valueEl.ValueKind switch
        {
            JsonValueKind.String => valueEl.GetString(),
            JsonValueKind.Number => valueEl.GetRawText(),
            _ => null
        };
    }
}
