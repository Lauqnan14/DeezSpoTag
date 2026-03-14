using System.Text.Json;

namespace DeezSpoTag.Web.Services.CoverPort;

public sealed class CoverArtArchiveCoverSource : ICoverSource
{
    private readonly CoverSourceHttpService _httpService;
    private readonly ILogger<CoverArtArchiveCoverSource> _logger;

    public CoverArtArchiveCoverSource(CoverSourceHttpService httpService, ILogger<CoverArtArchiveCoverSource> logger)
    {
        _httpService = httpService;
        _logger = logger;
    }

    public CoverSourceName Name => CoverSourceName.CoverArtArchive;

    public async Task<IReadOnlyList<CoverCandidate>> SearchAsync(CoverSearchQuery query, CancellationToken cancellationToken)
    {
        var queryText = $"artist:\"{query.Artist}\" AND release:\"{query.Album}\"";
        var musicBrainzUrl =
            $"https://musicbrainz.org/ws/2/release/?query={Uri.EscapeDataString(queryText)}&fmt=json&limit=6";

        try
        {
            using var searchDoc = await _httpService.GetJsonDocumentAsync(Name, musicBrainzUrl, cancellationToken);
            if (searchDoc == null)
            {
                return Array.Empty<CoverCandidate>();
            }

            if (!searchDoc.RootElement.TryGetProperty("releases", out var releasesElement) || releasesElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CoverCandidate>();
            }

            var candidates = new List<CoverCandidate>();
        var releaseRank = 0;
        foreach (var release in releasesElement.EnumerateArray())
        {
                var releaseId = release.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(releaseId))
                {
                    continue;
                }

                var releaseAlbum = release.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
                var releaseArtist = ExtractReleaseArtist(release);
            var confidence = CoverTextNormalizer.ComputeMatchConfidence(query, releaseArtist, releaseAlbum);
            var fuzzy = !string.Equals(
                            CoverTextNormalizer.Normalize(query.Artist),
                            CoverTextNormalizer.Normalize(releaseArtist),
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            CoverTextNormalizer.Normalize(query.Album),
                            CoverTextNormalizer.Normalize(releaseAlbum),
                            StringComparison.Ordinal);

            var releaseCoverCandidates = await FetchReleaseCoversAsync(
                releaseId!,
                confidence,
                releaseArtist,
                releaseAlbum,
                rank: releaseRank,
                fuzzy,
                cancellationToken);
            candidates.AddRange(releaseCoverCandidates);
            releaseRank++;
        }

            return candidates;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "CoverArtArchive search failed for {Artist} - {Album}", query.Artist, query.Album);
            return Array.Empty<CoverCandidate>();
        }
    }

    private async Task<IReadOnlyList<CoverCandidate>> FetchReleaseCoversAsync(
        string releaseId,
        double confidence,
        string? artist,
        string? album,
        int rank,
        bool fuzzy,
        CancellationToken cancellationToken)
    {
        var requestUrl = $"https://coverartarchive.org/release/{Uri.EscapeDataString(releaseId)}";
        using var document = await _httpService.GetJsonDocumentAsync(Name, requestUrl, cancellationToken);
        if (document == null)
        {
            return Array.Empty<CoverCandidate>();
        }
        if (!document.RootElement.TryGetProperty("images", out var imagesElement) || imagesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CoverCandidate>();
        }

        var candidates = new List<CoverCandidate>();
        foreach (var image in imagesElement.EnumerateArray())
        {
            var url = ResolveImageUrl(image);
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var width = image.TryGetProperty("width", out var widthEl) && widthEl.TryGetInt32(out var parsedWidth) ? parsedWidth : 0;
            var height = image.TryGetProperty("height", out var heightEl) && heightEl.TryGetInt32(out var parsedHeight) ? parsedHeight : 0;
            var format = GuessFormat(url!);
            var isKnown = width > 0 && height > 0;

            candidates.Add(new CoverCandidate(
                Source: Name,
                Url: url!,
                Width: width,
                Height: height,
                Format: format,
                SourceReliability: 0.93d,
                MatchConfidence: confidence,
                Artist: artist,
                Album: album,
                Rank: rank,
                Relevance: new CoverRelevance(
                    Fuzzy: fuzzy,
                    OnlyFrontCovers: true,
                    UnrelatedRisk: false),
                IsSizeKnown: isKnown,
                IsFormatKnown: !string.IsNullOrWhiteSpace(format)));
        }

        return candidates;
    }

    private static string? ResolveImageUrl(JsonElement image)
    {
        if (image.TryGetProperty("image", out var imageEl))
        {
            var imageUrl = imageEl.GetString();
            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                return imageUrl;
            }
        }

        if (!image.TryGetProperty("thumbnails", out var thumbnailsEl) || thumbnailsEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var preferredOrder = new[] { "1200", "500", "large", "small", "250" };
        return preferredOrder
            .Select(key =>
                thumbnailsEl.TryGetProperty(key, out var thumbEl)
                    ? thumbEl.GetString()
                    : null)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
    }

    private static string? ExtractReleaseArtist(JsonElement release)
    {
        if (!release.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var credit in credits.EnumerateArray())
        {
            if (!credit.TryGetProperty("name", out var nameEl))
            {
                continue;
            }

            var name = nameEl.GetString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return null;
    }

    private static string GuessFormat(string url)
    {
        var ext = Path.GetExtension(url).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "png" => "png",
            "jpg" => "jpg",
            "jpeg" => "jpg",
            _ => "jpg"
        };
    }
}
