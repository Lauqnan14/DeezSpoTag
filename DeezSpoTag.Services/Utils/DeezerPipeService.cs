using System.Text.Json;
using Microsoft.Extensions.Logging;
using DeezSpoTag.Services.Download;

namespace DeezSpoTag.Services.Utils;

public sealed class DeezerPipeService
{
    private readonly record struct DeezerAuthContext(string Arl, string? Sid, string JwtToken);
    private const string TrackMetadataQuery = """
                query TrackMetadata($trackId: String!) {
                  track(trackId: $trackId) {
                    id
                    title
                    duration
                    isrc
                    artist { id name }
                    album { id title cover { md5 } }
                  }
                }
                """;

    private const string HttpsScheme = "https";
    private const string PipeHost = "pipe.deezer.com";
    private const string DeezerCdnHost = "e-cdns-images.dzcdn.net";
    private static readonly string PipeApiUrl = BuildUrl(PipeHost, "/api/");
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeezerPipeService> _logger;
    private readonly JwtTokenService _jwtTokenService;
    private readonly AuthenticatedDeezerService _authenticatedDeezerService;

    public DeezerPipeService(
        IHttpClientFactory httpClientFactory,
        JwtTokenService jwtTokenService,
        AuthenticatedDeezerService authenticatedDeezerService,
        ILogger<DeezerPipeService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _jwtTokenService = jwtTokenService;
        _authenticatedDeezerService = authenticatedDeezerService;
        _logger = logger;
    }

    private static string BuildUrl(string host, string pathAndQuery)
    {
        var authority = new UriBuilder(HttpsScheme, host).Uri.GetLeftPart(UriPartial.Authority);
        return $"{authority}{pathAndQuery}";
    }

    public async Task<DeezerPipeTrackMetadata?> TryGetTrackMetadataAsync(string trackId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        try
        {
            var authContext = await TryResolveAuthContextAsync(cancellationToken);
            if (!authContext.HasValue)
            {
                return null;
            }

            var context = authContext.Value;
            using var httpClient = _httpClientFactory.CreateClient("LyricsService");
            using var request = BuildMetadataRequest(trackId, context);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Deezer pipe metadata request failed: {StatusCode}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            return TryParseTrackMetadata(content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Deezer pipe metadata lookup failed for track {TrackId}", trackId);
            return null;
        }
    }

    private async Task<DeezerAuthContext?> TryResolveAuthContextAsync(CancellationToken cancellationToken)
    {
        var arl = await _authenticatedDeezerService.GetArlAsync();
        if (string.IsNullOrWhiteSpace(arl))
        {
            _logger.LogDebug("No ARL available for Deezer pipe metadata lookup");
            return null;
        }

        var sid = await _authenticatedDeezerService.GetSidAsync();
        var jwtToken = await _jwtTokenService.GetJsonWebTokenAsync(arl, sid, cancellationToken);
        if (string.IsNullOrWhiteSpace(jwtToken))
        {
            _logger.LogDebug("Failed to obtain JWT for Deezer pipe metadata lookup");
            return null;
        }

        return new DeezerAuthContext(arl, sid, jwtToken);
    }

    private static HttpRequestMessage BuildMetadataRequest(string trackId, DeezerAuthContext context)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, PipeApiUrl);
        request.Headers.Add("Authorization", $"Bearer {context.JwtToken}");
        request.Headers.Add("Accept", "*/*");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
        request.Headers.Add("Cookie", BuildCookieHeader(context));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                operationName = "TrackMetadata",
                variables = new { trackId },
                query = TrackMetadataQuery
            }),
            System.Text.Encoding.UTF8,
            "application/json");
        return request;
    }

    private static string BuildCookieHeader(DeezerAuthContext context)
    {
        return $"arl={context.Arl}" + (string.IsNullOrWhiteSpace(context.Sid) ? string.Empty : $"; sid={context.Sid}");
    }

    private static DeezerPipeTrackMetadata? TryParseTrackMetadata(string content)
    {
        using var jsonDoc = JsonDocument.Parse(content);
        if (!TryGetTrackElement(jsonDoc.RootElement, out var track))
        {
            return null;
        }

        var title = ReadString(track, "title");
        var duration = ReadInt32(track, "duration");
        var isrc = ReadString(track, "isrc");
        var artist = ParseArtist(track);
        var album = ParseAlbum(track);
        return new DeezerPipeTrackMetadata(
            title,
            duration,
            isrc,
            artist.ArtistId,
            artist.ArtistName,
            album.AlbumId,
            album.AlbumTitle,
            album.AlbumCoverUrl);
    }

    private static bool TryGetTrackElement(JsonElement root, out JsonElement track)
    {
        track = default;
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("track", out track))
        {
            return false;
        }

        return track.ValueKind != JsonValueKind.Null;
    }

    private static (string? ArtistId, string? ArtistName) ParseArtist(JsonElement track)
    {
        if (!track.TryGetProperty("artist", out var artistElement) || artistElement.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var artistId = artistElement.TryGetProperty("id", out var artistIdElement) ? artistIdElement.ToString() : null;
        var artistName = artistElement.TryGetProperty("name", out var artistNameElement) ? artistNameElement.GetString() : null;
        return (artistId, artistName);
    }

    private static (string? AlbumId, string? AlbumTitle, string? AlbumCoverUrl) ParseAlbum(JsonElement track)
    {
        if (!track.TryGetProperty("album", out var albumElement) || albumElement.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        var albumId = albumElement.TryGetProperty("id", out var albumIdElement) ? albumIdElement.ToString() : null;
        var albumTitle = albumElement.TryGetProperty("title", out var albumTitleElement) ? albumTitleElement.GetString() : null;
        var coverUrl = BuildAlbumCoverUrl(albumElement);
        return (albumId, albumTitle, coverUrl);
    }

    private static string? BuildAlbumCoverUrl(JsonElement albumElement)
    {
        if (!albumElement.TryGetProperty("cover", out var coverElement) || coverElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var md5 = coverElement.TryGetProperty("md5", out var md5Element) ? md5Element.GetString() : null;
        return string.IsNullOrWhiteSpace(md5)
            ? null
            : BuildUrl(DeezerCdnHost, $"/images/cover/{md5}/1000x1000-000000-80-0-0.jpg");
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
    }

    private static int? ReadInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }
}

public sealed record DeezerPipeTrackMetadata(
    string? Title,
    int? Duration,
    string? Isrc,
    string? ArtistId,
    string? ArtistName,
    string? AlbumId,
    string? AlbumTitle,
    string? AlbumCoverUrl);
