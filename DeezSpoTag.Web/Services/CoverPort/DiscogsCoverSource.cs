using DeezSpoTag.Web.Services;
using System.Text.Json;

namespace DeezSpoTag.Web.Services.CoverPort;

public sealed class DiscogsCoverSource : ICoverSource
{
    private readonly record struct DiscogsImageCandidate(string? Url, int Width, int Height, bool IsPrimary);

    private readonly CoverSourceHttpService _httpService;
    private readonly PlatformAuthService _platformAuthService;
    private readonly ILogger<DiscogsCoverSource> _logger;

    public DiscogsCoverSource(
        CoverSourceHttpService httpService,
        PlatformAuthService platformAuthService,
        ILogger<DiscogsCoverSource> logger)
    {
        _httpService = httpService;
        _platformAuthService = platformAuthService;
        _logger = logger;
    }

    public CoverSourceName Name => CoverSourceName.Discogs;

    public async Task<IReadOnlyList<CoverCandidate>> SearchAsync(CoverSearchQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var auth = await _platformAuthService.LoadAsync();
            var token = auth.Discogs?.Token?.Trim();
            var headers = BuildHeaders(token);
            var searchUrl =
                $"https://api.discogs.com/database/search?type=release&artist={Uri.EscapeDataString(query.Artist)}&release_title={Uri.EscapeDataString(query.Album)}";
            using var searchDoc = await _httpService.GetJsonDocumentAsync(Name, searchUrl, cancellationToken, headers);
            if (searchDoc == null ||
                !searchDoc.RootElement.TryGetProperty("results", out var resultsElement) ||
                resultsElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CoverCandidate>();
            }

            var candidates = new List<CoverCandidate>();
            var rank = 0;
            foreach (var result in resultsElement.EnumerateArray().Take(8))
            {
                if (!TryExtractDiscogsSearchResult(result, out var releaseId, out var releaseType, out var title))
                {
                    continue;
                }

                var releaseUrl = releaseType == "master"
                    ? $"https://api.discogs.com/masters/{releaseId}"
                    : $"https://api.discogs.com/releases/{releaseId}";
                using var releaseDoc = await _httpService.GetJsonDocumentAsync(Name, releaseUrl, cancellationToken, headers);
                if (releaseDoc == null)
                {
                    continue;
                }

                var image = SelectReleaseImage(releaseDoc.RootElement);
                if (string.IsNullOrWhiteSpace(image.url))
                {
                    continue;
                }

                var parsed = ParseDiscogsTitle(title);
                var confidence = CoverTextNormalizer.ComputeMatchConfidence(query, parsed.artist, parsed.album);
                candidates.Add(new CoverCandidate(
                    Source: Name,
                    Url: image.url!,
                    Width: image.width,
                    Height: image.height,
                    Format: "jpg",
                    SourceReliability: 0.82d,
                    MatchConfidence: confidence,
                    Artist: parsed.artist,
                    Album: parsed.album,
                    Rank: rank,
                    Relevance: new CoverRelevance(
                        Fuzzy: false,
                        OnlyFrontCovers: false,
                        UnrelatedRisk: false)));
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
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Discogs cover search failed for {Artist} - {Album}", query.Artist, query.Album);
            }
            return Array.Empty<CoverCandidate>();
        }
    }

    private static Dictionary<string, string> BuildHeaders(string? token)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = "DeezSpoTag/1.0 (Discogs)"
        };

        if (!string.IsNullOrWhiteSpace(token))
        {
            headers["Authorization"] = $"Discogs token={token}";
        }

        return headers;
    }

    private static bool TryExtractDiscogsSearchResult(
        JsonElement result,
        out long releaseId,
        out string releaseType,
        out string? title)
    {
        releaseId = 0;
        releaseType = "release";
        title = null;
        if (!result.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out releaseId))
        {
            return false;
        }

        releaseType = result.TryGetProperty("type", out var typeEl) ? (typeEl.GetString() ?? "release") : "release";
        title = result.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;

        if (!result.TryGetProperty("thumb", out var thumbEl) || string.IsNullOrWhiteSpace(thumbEl.GetString()))
        {
            return false;
        }

        if (result.TryGetProperty("format", out var formatEl) && formatEl.ValueKind == JsonValueKind.Array)
        {
            var hasCd = formatEl.EnumerateArray()
                .Any(item => string.Equals(item.GetString(), "CD", StringComparison.OrdinalIgnoreCase));
            if (!hasCd)
            {
                return false;
            }
        }

        return true;
    }

    private static (string? url, int width, int height) SelectReleaseImage(JsonElement release)
    {
        if (!release.TryGetProperty("images", out var imagesElement) || imagesElement.ValueKind != JsonValueKind.Array)
        {
            return (null, 0, 0);
        }

        var candidates = imagesElement
            .EnumerateArray()
            .Select(ParseImageCandidate)
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.Url))
            .ToList();
        if (candidates.Count == 0)
        {
            return (null, 0, 0);
        }

        var selected = candidates.FirstOrDefault(static candidate => candidate.IsPrimary);
        if (string.IsNullOrWhiteSpace(selected.Url))
        {
            selected = candidates[0];
        }

        return (selected.Url, selected.Width, selected.Height);
    }

    private static DiscogsImageCandidate ParseImageCandidate(JsonElement image)
    {
        var type = image.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
        var url = image.TryGetProperty("uri", out var urlEl) ? urlEl.GetString() : null;
        var width = image.TryGetProperty("width", out var widthEl) && widthEl.TryGetInt32(out var parsedWidth) ? parsedWidth : 0;
        var height = image.TryGetProperty("height", out var heightEl) && heightEl.TryGetInt32(out var parsedHeight) ? parsedHeight : 0;
        var isPrimary = string.Equals(type, "primary", StringComparison.OrdinalIgnoreCase);
        return new DiscogsImageCandidate(url, width, height, isPrimary);
    }

    private static (string? artist, string? album) ParseDiscogsTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var parts = value.Split(" - ", 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            return (parts[0], parts[1]);
        }

        return (null, value);
    }
}
