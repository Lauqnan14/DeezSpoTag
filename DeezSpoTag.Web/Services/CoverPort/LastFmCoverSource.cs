using System.Text.Json;
using DeezSpoTag.Web.Services;

namespace DeezSpoTag.Web.Services.CoverPort;

public sealed class LastFmCoverSource : ICoverSource
{
    private readonly record struct LastFmAlbumContext(string? Artist, string? Album, double Confidence);

    private readonly CoverSourceHttpService _httpService;
    private readonly IConfiguration _configuration;
    private readonly PlatformAuthService _platformAuthService;
    private readonly ILogger<LastFmCoverSource> _logger;
    private string? _cachedApiKey;
    private static readonly string[] PlaceholderFragments =
    {
        // Known Last.fm placeholder image hash.
        "2a96cbd8b46e442fc41c2b86b821562f"
    };

    public LastFmCoverSource(
        CoverSourceHttpService httpService,
        IConfiguration configuration,
        PlatformAuthService platformAuthService,
        ILogger<LastFmCoverSource> logger)
    {
        _httpService = httpService;
        _configuration = configuration;
        _platformAuthService = platformAuthService;
        _logger = logger;
    }

    public CoverSourceName Name => CoverSourceName.LastFm;

    public async Task<IReadOnlyList<CoverCandidate>> SearchAsync(CoverSearchQuery query, CancellationToken cancellationToken)
    {
        var apiKey = await ResolveApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Array.Empty<CoverCandidate>();
        }

        var requestUrl =
            $"https://ws.audioscrobbler.com/2.0/?method=album.getinfo&artist={Uri.EscapeDataString(query.Artist)}&album={Uri.EscapeDataString(query.Album)}&api_key={Uri.EscapeDataString(apiKey)}&format=json&autocorrect=1";

        try
        {
            using var document = await _httpService.GetJsonDocumentAsync(Name, requestUrl, cancellationToken);
            if (document == null)
            {
                return Array.Empty<CoverCandidate>();
            }
            if (!TryGetAlbumElement(document.RootElement, query, out var albumElement))
            {
                return Array.Empty<CoverCandidate>();
            }

            if (!albumElement.TryGetProperty("image", out var imagesElement) || imagesElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CoverCandidate>();
            }

            var context = BuildAlbumContext(query, albumElement);
            return BuildCandidates(imagesElement, context, Name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Last.fm cover search failed for {Artist} - {Album}", query.Artist, query.Album);
            return Array.Empty<CoverCandidate>();
        }
    }

    private bool TryGetAlbumElement(JsonElement root, CoverSearchQuery query, out JsonElement albumElement)
    {
        albumElement = default;
        if (root.TryGetProperty("error", out var errorEl)
            && errorEl.ValueKind == JsonValueKind.Number
            && errorEl.TryGetInt32(out var errorCode))
        {
            var message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
            _logger.LogDebug("Last.fm cover search returned error {Error}: {Message} for {Artist} - {Album}", errorCode, message, query.Artist, query.Album);
            if (errorCode == 10)
            {
                _cachedApiKey = null;
            }

            return false;
        }

        return root.TryGetProperty("album", out albumElement) && albumElement.ValueKind == JsonValueKind.Object;
    }

    private static LastFmAlbumContext BuildAlbumContext(CoverSearchQuery query, JsonElement albumElement)
    {
        var albumArtist = albumElement.TryGetProperty("artist", out var artistEl) ? artistEl.GetString() : null;
        var albumName = albumElement.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        var confidence = CoverTextNormalizer.ComputeMatchConfidence(query, albumArtist, albumName);
        return new LastFmAlbumContext(albumArtist, albumName, confidence);
    }

    private static List<CoverCandidate> BuildCandidates(
        JsonElement imagesElement,
        LastFmAlbumContext context,
        CoverSourceName source)
    {
        var candidates = new List<CoverCandidate>();
        foreach (var imageElement in imagesElement.EnumerateArray())
        {
            if (!TryBuildCandidate(source, imageElement, context, candidates.Count, out var candidate))
            {
                continue;
            }

            candidates.Add(candidate);
        }

        return candidates;
    }

    private static bool TryBuildCandidate(
        CoverSourceName source,
        JsonElement imageElement,
        LastFmAlbumContext context,
        int rank,
        out CoverCandidate candidate)
    {
        candidate = default!;
        if (!imageElement.TryGetProperty("#text", out var urlEl))
        {
            return false;
        }

        var url = urlEl.GetString();
        if (string.IsNullOrWhiteSpace(url) || IsPlaceholder(url))
        {
            return false;
        }

        var sizeName = imageElement.TryGetProperty("size", out var sizeEl) ? sizeEl.GetString() : null;
        var size = ResolveSize(sizeName);
        candidate = new CoverCandidate(
            Source: source,
            Url: url,
            Width: size,
            Height: size,
            Format: GuessFormat(url),
            SourceReliability: 0.65d,
            MatchConfidence: context.Confidence,
            Artist: context.Artist,
            Album: context.Album,
            Rank: rank,
            Relevance: new CoverRelevance(
                Fuzzy: false,
                OnlyFrontCovers: true,
                UnrelatedRisk: false),
            IsSizeKnown: !string.Equals(sizeName, "mega", StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(sizeName),
            IsFormatKnown: true);
        return true;
    }

    private static bool IsPlaceholder(string url)
    {
        return PlaceholderFragments.Any(fragment => url.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> ResolveApiKeyAsync()
    {
        var configKey = _configuration["Lastfm:ApiKey"];
        if (!string.IsNullOrWhiteSpace(configKey))
        {
            _cachedApiKey = configKey;
            return configKey;
        }

        if (!string.IsNullOrWhiteSpace(_cachedApiKey))
        {
            return _cachedApiKey;
        }

        var authState = await _platformAuthService.LoadAsync();
        _cachedApiKey = authState.LastFm?.ApiKey;
        return _cachedApiKey;
    }

    private static int ResolveSize(string? sizeName)
    {
        return sizeName?.Trim().ToLowerInvariant() switch
        {
            "small" => 34,
            "medium" => 64,
            "large" => 174,
            "extralarge" => 300,
            "mega" => 600,
            _ => 0
        };
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
