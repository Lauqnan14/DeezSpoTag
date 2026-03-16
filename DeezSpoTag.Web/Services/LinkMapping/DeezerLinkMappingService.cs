using DeezSpoTag.Services.Download.Utils;
using System.Net;
using System.Text;
using System.Text.Json;
using DeezSpoTag.Core.Utils;

namespace DeezSpoTag.Web.Services.LinkMapping;

public sealed class DeezerLinkMappingService
{
    private const int DeezerSearchLimit = 4;
    private const double MinMetadataSearchScore = 0.30d;
    private const string TrackSearchType = "track";
    private const string AlbumSearchType = "album";
    private const string ArtistSearchType = "artist";
    private const string PlaylistSearchType = "playlist";
    private const string SongSourceType = "song";
    private const string ChannelSourceType = "channel";

    private readonly SongLinkResolver _songLinkResolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeezerLinkMappingService> _logger;

    public DeezerLinkMappingService(
        SongLinkResolver songLinkResolver,
        IHttpClientFactory httpClientFactory,
        ILogger<DeezerLinkMappingService> logger)
    {
        _songLinkResolver = songLinkResolver;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<DeezerLinkMappingResult> MapToDeezerAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return DeezerLinkMappingResult.Unavailable(ExternalLinkSource.Unknown, "URL is required.");
        }

        if (!TryNormalizeHttpUrl(url, out var normalizedUrl))
        {
            return DeezerLinkMappingResult.Unavailable(ExternalLinkSource.Unknown, "Invalid URL.");
        }

        var source = ExternalLinkClassifier.Classify(normalizedUrl);
        if (source != ExternalLinkSource.Deezer && IsPlaylistUrl(normalizedUrl))
        {
            return DeezerLinkMappingResult.Unavailable(
                source,
                "Playlist links are resolved per-track and must be opened using their source playlist flow.");
        }

        if (DeezerLinkParser.TryParse(normalizedUrl, out var directDeezer))
        {
            return DeezerLinkMappingResult.Success(source, directDeezer);
        }

        SongLinkResult? mapped = null;
        try
        {
            mapped = await _songLinkResolver.ResolveByUrlAsync(normalizedUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Song.link mapping failed for url {Url}", normalizedUrl);
        }

        if (DeezerLinkParser.TryParse(mapped?.DeezerUrl, out var mappedDeezer))
        {
            return DeezerLinkMappingResult.Success(source, mappedDeezer);
        }

        var searchedDeezer = await TryMapByMetadataSearchAsync(normalizedUrl, source, mapped, cancellationToken);
        if (searchedDeezer != null)
        {
            return DeezerLinkMappingResult.Success(source, searchedDeezer);
        }

        return DeezerLinkMappingResult.Unavailable(source, "No Deezer mapping found.");
    }

    private async Task<DeezerLinkDescriptor?> TryMapByMetadataSearchAsync(
        string normalizedUrl,
        ExternalLinkSource source,
        SongLinkResult? mapped,
        CancellationToken cancellationToken)
    {
        if (mapped == null
            || !TryBuildMetadataSearchRequest(normalizedUrl, mapped, out var searchType, out var query))
        {
            return null;
        }

        var candidate = await SearchBestDeezerCandidateAsync(searchType, query, cancellationToken);
        if (candidate == null)
        {
            return null;
        }

        if (DeezerLinkParser.TryParse(candidate.Url, out var descriptor))
        {
            _logger.LogInformation(
                "Deezer metadata mapping matched {Source} to {DeezerUrl} using query \"{Query}\".",
                source.ToClientValue(),
                descriptor.Url,
                query);
            return descriptor;
        }

        return null;
    }

    private static bool TryBuildMetadataSearchRequest(
        string normalizedUrl,
        SongLinkResult mapped,
        out string searchType,
        out string query)
    {
        searchType = string.Empty;
        query = string.Empty;

        var sourceTitle = NormalizeWhitespace(mapped.SourceTitle);
        if (string.IsNullOrWhiteSpace(sourceTitle))
        {
            return false;
        }

        searchType = InferSearchType(normalizedUrl, mapped.SourceType);
        if (string.IsNullOrWhiteSpace(searchType))
        {
            return false;
        }

        var sourceArtist = NormalizeWhitespace(mapped.SourceArtist);
        query = searchType switch
        {
            TrackSearchType or AlbumSearchType or ArtistSearchType when !string.IsNullOrWhiteSpace(sourceArtist)
                => $"{sourceTitle} {sourceArtist}",
            _ => sourceTitle
        };

        return !string.IsNullOrWhiteSpace(query);
    }

    private static string InferSearchType(string normalizedUrl, string? sourceType)
    {
        if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.ToLowerInvariant();
            if (path.Contains("/playlist", StringComparison.Ordinal)
                || uri.Query.Contains("list=", StringComparison.OrdinalIgnoreCase))
            {
                return PlaylistSearchType;
            }

            if (path.Contains("/album", StringComparison.Ordinal))
            {
                return AlbumSearchType;
            }

            if (path.Contains("/artist", StringComparison.Ordinal)
                || path.Contains("/channel", StringComparison.Ordinal))
            {
                return ArtistSearchType;
            }

            if (path.Contains("/track", StringComparison.Ordinal)
                || path.Contains("/song", StringComparison.Ordinal)
                || uri.Query.Contains("v=", StringComparison.OrdinalIgnoreCase))
            {
                return TrackSearchType;
            }
        }

        var normalizedType = NormalizeWhitespace(sourceType).ToLowerInvariant();
        return normalizedType switch
        {
            SongSourceType or TrackSearchType => TrackSearchType,
            AlbumSearchType => AlbumSearchType,
            PlaylistSearchType => PlaylistSearchType,
            ArtistSearchType or ChannelSourceType => ArtistSearchType,
            _ => string.Empty
        };
    }

    private async Task<DeezerSearchCandidate?> SearchBestDeezerCandidateAsync(
        string searchType,
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            var url =
                $"https://api.deezer.com/search/{searchType}?q={WebUtility.UrlEncode(query)}&limit={DeezerSearchLimit}";
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!doc.RootElement.TryGetProperty("data", out var dataElement)
                || dataElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var normalizedQuery = NormalizeForSimilarity(query);
            var bestScore = 0d;
            DeezerSearchCandidate? best = null;

            foreach (var candidateElement in dataElement.EnumerateArray())
            {
                var urlCandidate = GetJsonString(candidateElement, "link");
                if (string.IsNullOrWhiteSpace(urlCandidate))
                {
                    continue;
                }

                var title = GetJsonString(candidateElement, "title");
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = GetJsonString(candidateElement, "name");
                }

                var artist = candidateElement.TryGetProperty("artist", out var artistElement)
                    ? GetJsonString(artistElement, "name")
                    : string.Empty;

                var candidateText = NormalizeForSimilarity($"{title} {artist}");
                var score = TextMatchUtils.ComputeNormalizedSimilarity(normalizedQuery, candidateText);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new DeezerSearchCandidate(urlCandidate, score);
                }
            }

            if (best == null || best.Score < MinMetadataSearchScore)
            {
                return null;
            }

            return best;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer metadata search mapping failed for query {Query}.", query);
            return null;
        }
    }

    private static string NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(" ", value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string NormalizeForSimilarity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer.Append(char.ToLowerInvariant(ch));
            }
            else if (char.IsWhiteSpace(ch))
            {
                buffer.Append(' ');
            }
        }

        return NormalizeWhitespace(buffer.ToString());
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool IsPlaylistUrl(string normalizedUrl)
    {
        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        return path.Contains("/playlist", StringComparison.Ordinal)
               || uri.Query.Contains("list=", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/sets/", StringComparison.Ordinal) // SoundCloud
               || path.Contains("/mix/", StringComparison.Ordinal) // Tidal mix
               || (host.Contains("bandcamp.com", StringComparison.Ordinal) && path.Contains("/album/", StringComparison.Ordinal)); // Bandcamp album behaves as collection
    }

    private static bool TryNormalizeHttpUrl(string value, out string normalized)
    {
        normalized = string.Empty;

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalized = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        return true;
    }

    private sealed record DeezerSearchCandidate(string Url, double Score);
}

public sealed record DeezerLinkMappingResult(
    bool Available,
    string Source,
    string DeezerType,
    string DeezerId,
    string DeezerUrl,
    string Reason)
{
    public static DeezerLinkMappingResult Success(ExternalLinkSource source, DeezerLinkDescriptor deezer)
        => new(
            Available: true,
            Source: source.ToClientValue(),
            DeezerType: deezer.Type,
            DeezerId: deezer.Id,
            DeezerUrl: deezer.Url,
            Reason: string.Empty);

    public static DeezerLinkMappingResult Unavailable(ExternalLinkSource source, string reason)
        => new(
            Available: false,
            Source: source.ToClientValue(),
            DeezerType: string.Empty,
            DeezerId: string.Empty,
            DeezerUrl: string.Empty,
            Reason: reason);
}
