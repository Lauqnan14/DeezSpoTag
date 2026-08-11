using System.Text.Json;
using System.Net;
using System.Text.RegularExpressions;
using DeezSpoTag.Core.Security;
using DeezSpoTag.Services.Download;

namespace DeezSpoTag.Web.Services;

public sealed class LastFmArtistImageService : ILastFmArtistImageResolver
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex GalleryImageRegex = new(
        @"(?<url>(?:https?:)?//lastfm(?:-img)?\.freetls\.fastly\.net/i/u/[^""'<>\s\\]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly string[] PlaceholderFragments =
    [
        "2a96cbd8b46e442fc41c2b86b821562f"
    ];

    private static readonly Dictionary<string, int> SizeRanks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mega"] = 5,
        ["extralarge"] = 4,
        ["large"] = 3,
        ["medium"] = 2,
        ["small"] = 1
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly PlatformAuthService _platformAuthService;
    private readonly ILogger<LastFmArtistImageService> _logger;
    private string? _cachedApiKey;

    public LastFmArtistImageService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        PlatformAuthService platformAuthService,
        ILogger<LastFmArtistImageService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _platformAuthService = platformAuthService;
        _logger = logger;
    }

    public async Task<string?> ResolveArtistImageByNameAsync(string? artistName, CancellationToken cancellationToken)
    {
        var candidates = await SearchArtistImagesAsync(artistName, 1, cancellationToken);
        return candidates.Count > 0 ? candidates[0].Url : null;
    }

    public async Task<IReadOnlyList<LastFmArtistImageCandidate>> SearchArtistImagesAsync(
        string? artistName,
        int limit,
        CancellationToken cancellationToken)
    {
        var normalizedArtist = NormalizeArtistName(artistName);
        if (string.IsNullOrWhiteSpace(normalizedArtist) || limit <= 0)
        {
            return Array.Empty<LastFmArtistImageCandidate>();
        }

        try
        {
            var candidates = new List<LastFmArtistImageCandidate>();
            var apiKey = await ResolveApiKeyAsync();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                candidates.AddRange(await SearchArtistInfoImagesAsync(normalizedArtist, apiKey, limit, cancellationToken));
            }

            if (candidates.Count < limit)
            {
                candidates.AddRange(await SearchArtistGalleryImagesAsync(
                    normalizedArtist,
                    limit - candidates.Count,
                    cancellationToken));
            }

            return candidates
                .GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(limit)
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Last.fm artist image lookup failed for {ArtistName}", LogSanitizer.OneLine(normalizedArtist));
            }
            return Array.Empty<LastFmArtistImageCandidate>();
        }
    }

    public async Task<LastFmArtistBiography?> GetArtistBiographyAsync(
        string? artistName,
        CancellationToken cancellationToken)
    {
        var normalizedArtist = NormalizeArtistName(artistName);
        if (string.IsNullOrWhiteSpace(normalizedArtist))
        {
            return null;
        }

        var apiKey = await ResolveApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(BuildArtistInfoUri(normalizedArtist, apiKey), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Number
                && error.TryGetInt32(out var errorCode)
                && errorCode == 10)
            {
                _cachedApiKey = null;
                return null;
            }

            if (!doc.RootElement.TryGetProperty("artist", out var artist)
                || artist.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var returnedName = GetString(artist, "name");
            if (!ArtistNamesMatch(normalizedArtist, returnedName))
            {
                return null;
            }

            var biography = ResolveBiographyText(artist);
            return string.IsNullOrWhiteSpace(biography)
                ? null
                : new LastFmArtistBiography(returnedName, biography);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Last.fm artist biography lookup failed for {ArtistName}", LogSanitizer.OneLine(normalizedArtist));
            }
            return null;
        }
    }

    private async Task<IReadOnlyList<LastFmArtistImageCandidate>> SearchArtistInfoImagesAsync(
        string normalizedArtist,
        string apiKey,
        int limit,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildArtistInfoUri(normalizedArtist, apiKey);
        var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync(requestUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<LastFmArtistImageCandidate>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (doc.RootElement.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Number
            && error.TryGetInt32(out var errorCode)
            && errorCode == 10)
        {
            _cachedApiKey = null;
            return Array.Empty<LastFmArtistImageCandidate>();
        }

        if (!doc.RootElement.TryGetProperty("artist", out var artist)
            || artist.ValueKind != JsonValueKind.Object
            || !artist.TryGetProperty("image", out var images)
            || images.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<LastFmArtistImageCandidate>();
        }

        return images.EnumerateArray()
            .Select(ReadImageCandidate)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!.Value)
            .GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Rank).First())
            .OrderByDescending(candidate => candidate.Rank)
            .Take(limit)
            .Select(candidate => new LastFmArtistImageCandidate(candidate.Url, candidate.Label))
            .ToArray();
    }

    private async Task<IReadOnlyList<LastFmArtistImageCandidate>> SearchArtistGalleryImagesAsync(
        string normalizedArtist,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return Array.Empty<LastFmArtistImageCandidate>();
        }

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildArtistGalleryUri(normalizedArtist));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<LastFmArtistImageCandidate>();
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return ExtractGalleryImages(html, limit, normalizedArtist);
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

    private static Uri BuildArtistInfoUri(string artistName, string apiKey)
    {
        var query = string.Join('&',
            "method=artist.getinfo",
            $"artist={Uri.EscapeDataString(artistName)}",
            $"api_key={Uri.EscapeDataString(apiKey)}",
            "format=json",
            "autocorrect=1");
        return new Uri($"https://ws.audioscrobbler.com/2.0/?{query}");
    }

    private static string ResolveBiographyText(JsonElement artist)
    {
        if (!artist.TryGetProperty("bio", out var bio) || bio.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var content = GetString(bio, "content");
        if (string.IsNullOrWhiteSpace(content))
        {
            content = GetString(bio, "summary");
        }

        return NormalizeBiography(content);
    }

    private static string NormalizeBiography(string? value)
    {
        var decoded = WebUtility.HtmlDecode(value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return string.Empty;
        }

        var readMoreIndex = decoded.IndexOf("<a href=", StringComparison.OrdinalIgnoreCase);
        if (readMoreIndex >= 0)
        {
            decoded = decoded[..readMoreIndex];
        }

        decoded = Regex.Replace(decoded, "<.*?>", " ", RegexOptions.Singleline, RegexTimeout);
        return Regex.Replace(decoded, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim()
            : string.Empty;

    private static bool ArtistNamesMatch(string expectedName, string returnedName)
        => NormalizeArtistNameForComparison(expectedName) == NormalizeArtistNameForComparison(returnedName);

    private static string NormalizeArtistNameForComparison(string? value)
        => Regex.Replace(
                WebUtility.HtmlDecode(value ?? string.Empty).Trim().ToLowerInvariant(),
                @"[^\p{L}\p{N}]+",
                " ",
                RegexOptions.None,
                RegexTimeout)
            .Trim();

    private static Uri BuildArtistGalleryUri(string artistName)
    {
        var pathArtist = BuildLastFmArtistPathSegment(artistName);
        return new Uri($"https://www.last.fm/music/{pathArtist}/+images");
    }

    private static (string Url, string Label, int Rank)? ReadImageCandidate(JsonElement image)
    {
        if (image.ValueKind != JsonValueKind.Object
            || !image.TryGetProperty("#text", out var urlElement)
            || urlElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var url = (urlElement.GetString() ?? string.Empty).Trim();
        if (!IsValidImageUrl(url))
        {
            return null;
        }

        var size = image.TryGetProperty("size", out var sizeElement) && sizeElement.ValueKind == JsonValueKind.String
            ? (sizeElement.GetString() ?? string.Empty).Trim()
            : string.Empty;
        var rank = SizeRanks.TryGetValue(size, out var resolvedRank) ? resolvedRank : 0;
        var label = string.IsNullOrWhiteSpace(size) ? "Last.fm" : $"Last.fm {size}";
        return (url, label, rank);
    }

    public static bool IsValidImageUrl(string? url)
    {
        var value = (url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        return !PlaceholderFragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<LastFmArtistImageCandidate> ExtractGalleryImages(string? html, int limit)
        => ExtractGalleryImages(html, limit, artistName: null);

    public static IReadOnlyList<LastFmArtistImageCandidate> ExtractGalleryImages(string? html, int limit, string? artistName)
    {
        if (string.IsNullOrWhiteSpace(html) || limit <= 0)
        {
            return Array.Empty<LastFmArtistImageCandidate>();
        }

        var decoded = WebUtility.HtmlDecode(html);
        var scopedHtml = ExtractPhotosSection(decoded);
        var expectedArtistImagePath = string.IsNullOrWhiteSpace(artistName)
            ? null
            : $"/music/{BuildLastFmArtistPathSegment(NormalizeArtistName(artistName))}/+images";
        return GalleryImageRegex.Matches(scopedHtml)
            .Where(match => IsArtistGalleryImageMatch(scopedHtml, match, expectedArtistImagePath))
            .Select(match => NormalizeGalleryImageUrl(match.Groups["url"].Value))
            .Where(IsValidImageUrl)
            .GroupBy(GetLastFmImageKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(GetGalleryImageRank).First())
            .Take(limit)
            .Select(url => new LastFmArtistImageCandidate(url, "Last.fm gallery"))
            .ToArray();
    }

    private static string ExtractPhotosSection(string html)
    {
        var start = html.IndexOf("subpage-title", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return html;
        }

        var end = FindFirstPositiveIndex(
            html.IndexOf("similar-albums-body", start, StringComparison.OrdinalIgnoreCase),
            html.IndexOf("similar-items", start, StringComparison.OrdinalIgnoreCase),
            html.IndexOf("col-sidebar", start, StringComparison.OrdinalIgnoreCase));

        return end > start ? html[start..end] : html[start..];
    }

    private static bool IsArtistGalleryImageMatch(string scopedHtml, Match match, string? expectedArtistImagePath)
    {
        if (match.Index < 0 || match.Index >= scopedHtml.Length)
        {
            return false;
        }

        var contextStart = Math.Max(0, match.Index - 800);
        var contextLength = Math.Min(scopedHtml.Length - contextStart, match.Length + 1600);
        var context = scopedHtml.Substring(contextStart, contextLength);
        if (context.Contains("similar-items", StringComparison.OrdinalIgnoreCase)
            || context.Contains("similar-artists", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(expectedArtistImagePath))
        {
            return context.Contains("/+images", StringComparison.OrdinalIgnoreCase);
        }

        return context.Contains(expectedArtistImagePath, StringComparison.OrdinalIgnoreCase);
    }

    private static int FindFirstPositiveIndex(params int[] indexes)
    {
        var result = -1;
        foreach (var index in indexes)
        {
            if (index < 0)
            {
                continue;
            }

            result = result < 0 ? index : Math.Min(result, index);
        }

        return result;
    }

    private static string NormalizeGalleryImageUrl(string url)
    {
        var value = (url ?? string.Empty).Trim();
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = $"https:{value}";
        }

        return value;
    }

    private static string GetLastFmImageKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var fileName = uri.Segments.LastOrDefault()?.Trim('/') ?? url;
        var extensionIndex = fileName.LastIndexOf('.');
        return extensionIndex > 0 ? fileName[..extensionIndex] : fileName;
    }

    private static int GetGalleryImageRank(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return 0;
        }

        var path = uri.AbsolutePath;
        if (path.Contains("/ar0/", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (path.Contains("/770x0/", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (path.Contains("/300x300/", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 1;
    }

    private static string BuildLastFmArtistPathSegment(string artistName)
        => Uri.EscapeDataString(artistName).Replace("%20", "+", StringComparison.Ordinal);

    private static string NormalizeArtistName(string? value)
        => string.Join(' ', (value ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

public sealed record LastFmArtistImageCandidate(string Url, string Label, string Source = "lastfm");
public sealed record LastFmArtistBiography(string Name, string Biography);
