using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeezSpoTag.Web.Controllers.Api;

[ApiController]
[Route("api/external/playlist/tracklist")]
[Authorize]
public sealed partial class ExternalPlaylistTracklistApiController : ControllerBase
{
    private const string TidalSource = "tidal";
    private const string QobuzSource = "qobuz";
    private const string BandcampSource = "bandcamp";
    private const string TidalAuthHost = "auth.tidal.com";
    private const string TidalAuthTokenPath = "/v1/oauth2/token";
    private const string MetadataTitleKey = "title";
    private const string MetadataDescriptionKey = "description";
    private const string MetadataImageKey = "image";
    private const string EncodedClientId = "NkJEU1JkcEs5aHFFQlRnVQ==";
    private const string EncodedClientSecret = "eGV1UG1ZN25icFo5SUliTEFjUTkzc2hrYTFWTmhlVUFxTjZJY3N6alRHOD0=";
    private const int DefaultPageSize = 100;
    private static readonly SemaphoreSlim TokenLock = new(1, 1);

    private static string _cachedToken = string.Empty;
    private static DateTimeOffset _cachedTokenExpiresUtc = DateTimeOffset.MinValue;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ExternalPlaylistTracklistApiController> _logger;

    public ExternalPlaylistTracklistApiController(
        IHttpClientFactory httpClientFactory,
        ILogger<ExternalPlaylistTracklistApiController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? source,
        [FromQuery] string? url,
        [FromQuery] string? id = null,
        [FromQuery] string type = "playlist",
        CancellationToken cancellationToken = default)
    {
        var normalizedType = (type ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.Equals(normalizedType, "playlist", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { available = false, error = $"Unsupported type '{type}'." });
        }

        var normalizedSource = NormalizeSource(source);
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            return BadRequest(new
            {
                available = false,
                error = "Only Tidal, Qobuz, and Bandcamp playlist links are supported in this external playlist endpoint."
            });
        }

        var playlistUrl = (url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(playlistUrl))
        {
            return BadRequest(new { available = false, error = "External playlist URL is required." });
        }

        try
        {
            object? payload;
            if (string.Equals(normalizedSource, TidalSource, StringComparison.Ordinal))
            {
                var resolvedPlaylistId = ResolveTidalPlaylistId(id, playlistUrl);
                if (string.IsNullOrWhiteSpace(resolvedPlaylistId))
                {
                    return BadRequest(new { available = false, error = "Tidal playlist id is required." });
                }

                payload = await BuildTidalPlaylistTracklistAsync(resolvedPlaylistId, playlistUrl, cancellationToken);
            }
            else if (string.Equals(normalizedSource, QobuzSource, StringComparison.Ordinal))
            {
                payload = await BuildQobuzPlaylistTracklistAsync(id, playlistUrl, cancellationToken);
            }
            else
            {
                payload = await BuildBandcampAlbumTracklistAsync(playlistUrl, cancellationToken);
            }

            if (payload is null)
            {
                return NotFound(new
                {
                    available = false,
                    error = $"{normalizedSource.ToUpperInvariant()} playlist unavailable."
                });
            }

            return Ok(new
            {
                available = true,
                tracklist = payload
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "{Source} external playlist tracklist fetch failed for {Url}.",
                normalizedSource,
                playlistUrl);
            return StatusCode(500, new { available = false, error = "Failed to load external playlist." });
        }
    }

    private async Task<object?> BuildTidalPlaylistTracklistAsync(
        string playlistId,
        string playlistUrl,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        var metadata = await FetchTidalPlaylistMetadataAsync(playlistId, token, cancellationToken);
        if (metadata is null)
        {
            return null;
        }

        var tracks = await FetchTidalPlaylistTracksAsync(playlistId, token, cancellationToken);
        if (tracks.Count == 0)
        {
            return null;
        }

        var title = metadata.GetValueOrDefault(MetadataTitleKey);
        var description = metadata.GetValueOrDefault(MetadataDescriptionKey);
        var coverId = metadata.GetValueOrDefault("squareImage");
        if (string.IsNullOrWhiteSpace(coverId))
        {
            coverId = metadata.GetValueOrDefault(MetadataImageKey);
        }

        var coverUrl = BuildTidalImageUrl(coverId);
        var creatorName = metadata.GetValueOrDefault("creatorName");
        if (string.IsNullOrWhiteSpace(creatorName))
        {
            creatorName = "Tidal";
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Tidal Playlist";
        }

        var normalizedPlaylistUrl = BuildTidalPlaylistUrl(playlistId, playlistUrl);
        return new
        {
            id = playlistId,
            title,
            description,
            cover_big = coverUrl,
            cover_xl = coverUrl,
            picture_big = coverUrl,
            picture_xl = coverUrl,
            link = normalizedPlaylistUrl,
            sourceUrl = normalizedPlaylistUrl,
            creator = new { name = creatorName },
            nb_tracks = tracks.Count,
            tracks
        };
    }

    private async Task<object?> BuildQobuzPlaylistTracklistAsync(
        string? idHint,
        string playlistUrl,
        CancellationToken cancellationToken)
    {
        var html = await FetchHtmlAsync(playlistUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var document = new HtmlDocument();
        document.LoadHtml(html);
        var playlistNode = FindJsonLdByType(document, "MusicPlaylist");
        var title = playlistNode.HasValue
            ? GetString(playlistNode.Value, "name")
            : string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = GetFirstNodeText(
                document,
                "//h1[contains(@class,'album-meta__title')]",
                MetadataTitleKey);
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Qobuz Playlist";
        }

        var description = playlistNode.HasValue
            ? GetString(playlistNode.Value, "description")
            : string.Empty;
        if (string.IsNullOrWhiteSpace(description))
        {
            description = document.DocumentNode
                .SelectSingleNode("//meta[@name='description']")
                ?.GetAttributeValue("content", string.Empty)
                ?? string.Empty;
        }

        var coverUrl = ResolveQobuzPlaylistCoverUrl(document, playlistNode);

        var creatorName = playlistNode.HasValue
            ? GetNestedName(playlistNode.Value, "author")
            : string.Empty;
        if (string.IsNullOrWhiteSpace(creatorName))
        {
            creatorName = GetFirstNodeText(
                document,
                "//h2[contains(@class,'album-meta__artist')]",
                MetadataTitleKey);
        }

        if (string.IsNullOrWhiteSpace(creatorName))
        {
            creatorName = "Qobuz";
        }

        var totalTracks = GetQobuzPlaylistTotalTrackCount(document);
        var resolvedId = ResolveQobuzPlaylistId(idHint, playlistUrl);
        var tracks = new List<object>();
        var dedupeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AppendQobuzTracksFromRows(document, coverUrl, tracks, dedupeKeys);

        AppendMissingQobuzTracksFromJsonLd(
            playlistNode,
            playlistUrl,
            resolvedId,
            tracks,
            dedupeKeys);

        if (tracks.Count == 0)
        {
            return null;
        }

        var normalizedUrl = BuildAbsoluteUrl(playlistUrl);
        return new
        {
            id = resolvedId,
            title,
            description,
            cover_big = coverUrl,
            cover_xl = coverUrl,
            picture_big = coverUrl,
            picture_xl = coverUrl,
            link = normalizedUrl,
            sourceUrl = normalizedUrl,
            creator = new { name = creatorName },
            nb_tracks = totalTracks > 0 ? totalTracks : tracks.Count,
            tracks
        };
    }

    private static void AppendQobuzTracksFromRows(
        HtmlDocument document,
        string coverUrl,
        List<object> tracks,
        HashSet<string> dedupeKeys)
    {
        var position = 0;
        var trackRows = document.DocumentNode
            .SelectNodes("//div[contains(@class,'track') and contains(@class,'track--playlist') and @data-track]");
        if (trackRows == null || trackRows.Count == 0)
        {
            return;
        }

        foreach (var row in trackRows)
        {
            position++;
            if (!TryBuildQobuzTrackFromRow(row, position, coverUrl, out var track, out var dedupeKey))
            {
                continue;
            }

            dedupeKeys.Add(dedupeKey);
            tracks.Add(track);
        }
    }

    private static bool TryBuildQobuzTrackFromRow(
        HtmlNode row,
        int fallbackPosition,
        string coverUrl,
        out object track,
        out string dedupeKey)
    {
        track = null!;
        dedupeKey = string.Empty;

        var trackId = row.GetAttributeValue("data-track", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return false;
        }

        var trackPosition = ParseTrackPosition(
            GetFirstNodeText(row, ".//div[contains(@class,'track__item--number')]/span[1]", string.Empty));
        if (trackPosition <= 0)
        {
            trackPosition = fallbackPosition;
        }

        var trackTitle = GetNodeTextWithFallback(
            row,
            [(".//div[contains(@class,'track__item--name')]", MetadataTitleKey), (".//div[contains(@class,'track__item--name')]/span[1]", string.Empty)]);
        if (string.IsNullOrWhiteSpace(trackTitle))
        {
            return false;
        }

        var artistName = GetNodeTextWithFallback(
            row,
            [(".//span[contains(@class,'track__item--artist')]", MetadataTitleKey), (".//span[contains(@class,'track__item--artist')]//a[1]", string.Empty)]);
        if (string.IsNullOrWhiteSpace(artistName))
        {
            artistName = "Unknown Artist";
        }

        var albumTitle = GetNodeTextWithFallback(
            row,
            [(".//span[contains(@class,'track__item--album')]", MetadataTitleKey), (".//span[contains(@class,'track__item--album')]//a[1]", string.Empty)]);
        var durationText = GetFirstNodeText(row, ".//span[contains(@class,'track__item--duration')]", string.Empty);
        var duration = ParseClockDurationSeconds(durationText);
        var trackUrl = $"https://open.qobuz.com/track/{Uri.EscapeDataString(trackId)}";
        dedupeKey = BuildQobuzTrackDedupeKey(trackTitle, artistName, albumTitle, duration);
        track = new
        {
            id = trackId,
            title = trackTitle,
            duration,
            track_position = trackPosition,
            link = trackUrl,
            sourceUrl = trackUrl,
            isrc = string.Empty,
            artist = new { id = string.Empty, name = artistName },
            album = new
            {
                id = string.Empty,
                title = string.IsNullOrWhiteSpace(albumTitle) ? string.Empty : albumTitle,
                cover_medium = coverUrl
            }
        };
        return true;
    }

    private static string GetNodeTextWithFallback(HtmlNode row, (string XPath, string AttributeName)[] selectors)
    {
        foreach (var selector in selectors)
        {
            var value = GetFirstNodeText(row, selector.XPath, selector.AttributeName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private async Task<object?> BuildBandcampAlbumTracklistAsync(
        string albumUrl,
        CancellationToken cancellationToken)
    {
        var html = await FetchHtmlAsync(albumUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var document = new HtmlDocument();
        document.LoadHtml(html);

        var trAlbumJson = document.DocumentNode
            .SelectSingleNode("//script[@data-tralbum]")
            ?.GetAttributeValue("data-tralbum", string.Empty);
        if (string.IsNullOrWhiteSpace(trAlbumJson))
        {
            return null;
        }

        trAlbumJson = HtmlEntity.DeEntitize(trAlbumJson);
        using var trAlbumDoc = JsonDocument.Parse(trAlbumJson);
        var root = trAlbumDoc.RootElement;

        var title = ResolveBandcampTitle(root);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Bandcamp Album";
        }

        var creatorName = GetString(root, "artist");
        if (string.IsNullOrWhiteSpace(creatorName))
        {
            creatorName = "Bandcamp";
        }

        var coverUrl = ResolveBandcampCoverUrl(document, root);
        var tracks = BuildBandcampTracks(root, albumUrl, creatorName, title, coverUrl);

        if (tracks.Count == 0)
        {
            return null;
        }

        var normalizedUrl = BuildAbsoluteUrl(albumUrl);
        var resolvedId = ResolveBandcampCollectionId(albumUrl);
        return new
        {
            id = resolvedId,
            title,
            description = string.Empty,
            cover_big = coverUrl,
            cover_xl = coverUrl,
            picture_big = coverUrl,
            picture_xl = coverUrl,
            link = normalizedUrl,
            sourceUrl = normalizedUrl,
            creator = new { name = creatorName },
            nb_tracks = tracks.Count,
            tracks
        };
    }

    private static string ResolveBandcampTitle(JsonElement root)
    {
        if (root.TryGetProperty("current", out var currentNode) && currentNode.ValueKind == JsonValueKind.Object)
        {
            return GetString(currentNode, MetadataTitleKey);
        }

        return string.Empty;
    }

    private static List<object> BuildBandcampTracks(
        JsonElement root,
        string albumUrl,
        string creatorName,
        string albumTitle,
        string coverUrl)
    {
        var tracks = new List<object>();
        if (!root.TryGetProperty("trackinfo", out var trackInfoNode) || trackInfoNode.ValueKind != JsonValueKind.Array)
        {
            return tracks;
        }

        var fallbackPosition = 0;
        foreach (var trackNode in trackInfoNode.EnumerateArray())
        {
            fallbackPosition++;
            if (!TryBuildBandcampTrack(trackNode, fallbackPosition, albumUrl, creatorName, albumTitle, coverUrl, out var track))
            {
                continue;
            }

            tracks.Add(track);
        }

        return tracks;
    }

    private static bool TryBuildBandcampTrack(
        JsonElement trackNode,
        int fallbackPosition,
        string albumUrl,
        string creatorName,
        string albumTitle,
        string coverUrl,
        out object track)
    {
        track = null!;
        var trackTitle = GetString(trackNode, MetadataTitleKey);
        if (string.IsNullOrWhiteSpace(trackTitle))
        {
            return false;
        }

        var trackNumber = GetInt(trackNode, "track_num");
        if (trackNumber <= 0)
        {
            trackNumber = fallbackPosition;
        }

        var duration = ParseDurationSeconds(trackNode, "duration");
        var trackId = GetAnyString(trackNode, "track_id");
        if (string.IsNullOrWhiteSpace(trackId))
        {
            trackId = trackNumber.ToString(CultureInfo.InvariantCulture);
        }

        var titleLink = GetString(trackNode, "title_link");
        var trackUrl = BuildBandcampTrackUrl(albumUrl, titleLink, trackNumber);
        var isrc = GetString(trackNode, "isrc");
        track = new
        {
            id = trackId,
            title = trackTitle,
            duration,
            track_position = trackNumber,
            link = trackUrl,
            sourceUrl = trackUrl,
            isrc,
            artist = new { id = string.Empty, name = creatorName },
            album = new
            {
                id = string.Empty,
                title = albumTitle,
                cover_medium = coverUrl
            }
        };
        return true;
    }

    private async Task<Dictionary<string, string>> FetchTidalPlaylistMetadataAsync(
        string playlistId,
        string token,
        CancellationToken cancellationToken)
    {
        using var response = await SendTidalRequestAsync(
            $"https://api.tidal.com/v1/playlists/{Uri.EscapeDataString(playlistId)}?countryCode=US",
            token,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;

        var creatorName = string.Empty;
        if (root.TryGetProperty("creator", out var creatorElement) && creatorElement.ValueKind == JsonValueKind.Object)
        {
            creatorName = GetString(creatorElement, "name");
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataTitleKey] = GetString(root, MetadataTitleKey),
            [MetadataDescriptionKey] = GetString(root, MetadataDescriptionKey),
            ["squareImage"] = GetString(root, "squareImage"),
            [MetadataImageKey] = GetString(root, MetadataImageKey),
            ["creatorName"] = creatorName
        };
    }

    private async Task<List<object>> FetchTidalPlaylistTracksAsync(
        string playlistId,
        string token,
        CancellationToken cancellationToken)
    {
        var tracks = new List<object>();
        var offset = 0;
        var total = int.MaxValue;
        var position = 0;

        while (offset < total)
        {
            using var response = await SendTidalRequestAsync(
                $"https://api.tidal.com/v1/playlists/{Uri.EscapeDataString(playlistId)}/items?countryCode=US&limit={DefaultPageSize}&offset={offset}",
                token,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            total = ResolveTidalTotalCount(root, total);
            if (!TryGetTidalItems(root, out var itemsElement))
            {
                break;
            }

            var appended = AppendTidalTracks(itemsElement, tracks, ref position);

            if (appended == 0)
            {
                break;
            }

            offset += itemsElement.GetArrayLength();
        }

        return tracks;
    }

    private static int ResolveTidalTotalCount(JsonElement root, int fallbackTotal)
    {
        var pageTotal = GetInt(root, "totalNumberOfItems");
        return pageTotal > 0 ? pageTotal : fallbackTotal;
    }

    private static bool TryGetTidalItems(JsonElement root, out JsonElement itemsElement)
    {
        if (!root.TryGetProperty("items", out itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return itemsElement.GetArrayLength() > 0;
    }

    private static int AppendTidalTracks(JsonElement itemsElement, List<object> tracks, ref int position)
    {
        var appended = 0;
        foreach (var wrapper in itemsElement.EnumerateArray())
        {
            var trackNode = ResolveTidalTrackNode(wrapper);
            if (!TryBuildTidalTrack(trackNode, ++position, out var track))
            {
                position--;
                continue;
            }

            tracks.Add(track);
            appended++;
        }

        return appended;
    }

    private static JsonElement ResolveTidalTrackNode(JsonElement wrapper)
    {
        if (wrapper.TryGetProperty("item", out var itemNode) && itemNode.ValueKind == JsonValueKind.Object)
        {
            return itemNode;
        }

        return wrapper;
    }

    private static bool TryBuildTidalTrack(JsonElement trackNode, int position, out object track)
    {
        track = null!;
        var trackId = GetAnyString(trackNode, "id");
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return false;
        }

        var trackTitle = ComposeTitle(GetString(trackNode, MetadataTitleKey), GetString(trackNode, "version"));
        var duration = GetInt(trackNode, "duration");
        var isrc = GetString(trackNode, "isrc");
        var trackUrl = NormalizeTidalTrackUrl(GetString(trackNode, "url"), trackId);
        var artistName = ResolveArtistName(trackNode);
        var albumMetadata = ResolveTidalAlbumMetadata(trackNode);
        track = new
        {
            id = trackId,
            title = string.IsNullOrWhiteSpace(trackTitle) ? $"Track {position}" : trackTitle,
            duration,
            track_position = position,
            link = trackUrl,
            sourceUrl = trackUrl,
            isrc,
            artist = new { id = string.Empty, name = artistName },
            album = new
            {
                id = albumMetadata.Id,
                title = albumMetadata.Title,
                cover_medium = albumMetadata.Cover
            }
        };
        return true;
    }

    private static string NormalizeTidalTrackUrl(string trackUrl, string trackId)
    {
        if (string.IsNullOrWhiteSpace(trackUrl))
        {
            return $"https://tidal.com/browse/track/{Uri.EscapeDataString(trackId)}";
        }

        if (Uri.TryCreate(trackUrl, UriKind.Absolute, out var parsedTrackUrl)
            && string.Equals(parsedTrackUrl.Host, "www.tidal.com", StringComparison.OrdinalIgnoreCase))
        {
            return trackUrl.Replace("https://www.tidal.com", "https://tidal.com/browse", StringComparison.OrdinalIgnoreCase);
        }

        return trackUrl;
    }

    private static (string Id, string Title, string Cover) ResolveTidalAlbumMetadata(JsonElement trackNode)
    {
        if (!trackNode.TryGetProperty("album", out var albumNode) || albumNode.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        return (
            GetAnyString(albumNode, "id"),
            GetString(albumNode, MetadataTitleKey),
            BuildTidalImageUrl(GetString(albumNode, "cover")));
    }

    private async Task<HttpResponseMessage> SendTidalRequestAsync(
        string url,
        string token,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, cancellationToken);
    }

    private static string ResolveArtistName(JsonElement trackNode)
    {
        if (trackNode.TryGetProperty("artist", out var artistNode) && artistNode.ValueKind == JsonValueKind.Object)
        {
            var primary = GetString(artistNode, "name");
            if (!string.IsNullOrWhiteSpace(primary))
            {
                return primary;
            }
        }

        if (trackNode.TryGetProperty("artists", out var artistsNode) && artistsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var artist in artistsNode.EnumerateArray())
            {
                if (artist.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = GetString(artist, "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        return string.Empty;
    }

    private static string BuildTidalPlaylistUrl(string playlistId, string providedUrl)
    {
        if (Uri.TryCreate(providedUrl, UriKind.Absolute, out var uri))
        {
            var absolute = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
            if (!string.IsNullOrWhiteSpace(absolute))
            {
                return absolute;
            }
        }

        return $"https://tidal.com/browse/playlist/{Uri.EscapeDataString(playlistId)}";
    }

    private static string BuildTidalImageUrl(string? coverId)
    {
        var normalized = (coverId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = normalized.Replace('-', '/');
        return $"https://resources.tidal.com/images/{normalized}/750x750.jpg";
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedToken) && _cachedTokenExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return _cachedToken;
        }

        await TokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedToken) && _cachedTokenExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(30))
            {
                return _cachedToken;
            }

            var clientId = Encoding.UTF8.GetString(Convert.FromBase64String(EncodedClientId));
            var clientSecret = Encoding.UTF8.GetString(Convert.FromBase64String(EncodedClientSecret));
            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

            var authUri = new UriBuilder(Uri.UriSchemeHttps, TidalAuthHost)
            {
                Path = TidalAuthTokenPath
            }.Uri;
            using var request = new HttpRequestMessage(HttpMethod.Post, authUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["grant_type"] = "client_credentials"
                })
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);

            var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Tidal auth failed with status {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var token = GetString(doc.RootElement, "access_token");
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Tidal auth did not return an access token.");
            }

            var expiresIn = GetInt(doc.RootElement, "expires_in");
            SetCachedToken(token, DateTimeOffset.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 300));
            return _cachedToken;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private static void SetCachedToken(string token, DateTimeOffset expiresUtc)
    {
        _cachedToken = token;
        _cachedTokenExpiresUtc = expiresUtc;
    }

    private static string ResolveTidalPlaylistId(string? id, string? url)
    {
        var explicitId = (id ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(explicitId) && explicitId.Contains('-'))
        {
            return explicitId;
        }

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return explicitId;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("playlist", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return explicitId;
    }

    private async Task<string> FetchHtmlAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var requestUri))
        {
            return string.Empty;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (DeezSpoTag)");
        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static JsonElement? FindJsonLdByType(HtmlDocument document, string expectedType)
    {
        var scripts = document.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
        if (scripts == null || scripts.Count == 0)
        {
            return null;
        }

        foreach (var script in scripts)
        {
            var json = HtmlEntity.DeEntitize(script.InnerText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var candidate in EnumerateJsonNodes(doc.RootElement))
                {
                    if (IsJsonNodeType(candidate, expectedType))
                    {
                        return candidate.Clone();
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore malformed JSON-LD blocks.
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateJsonNodes(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var node in EnumerateObjectNodes(element))
                {
                    yield return node;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in EnumerateJsonNodes(item))
                    {
                        yield return nested;
                    }
                }
                break;
        }
    }

    private static IEnumerable<JsonElement> EnumerateObjectNodes(JsonElement element)
    {
        if (element.TryGetProperty("@graph", out var graphNode) && graphNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in graphNode.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
            {
                yield return item;
            }
            yield break;
        }

        yield return element;
    }

    private static bool IsJsonNodeType(JsonElement node, string expectedType)
    {
        if (!node.TryGetProperty("@type", out var typeNode))
        {
            return false;
        }

        if (typeNode.ValueKind == JsonValueKind.String)
        {
            var value = typeNode.GetString() ?? string.Empty;
            return value.Equals(expectedType, StringComparison.OrdinalIgnoreCase);
        }

        if (typeNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typeNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (string.Equals(item.GetString(), expectedType, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string GetNestedName(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var node))
        {
            return string.Empty;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            return GetString(node, "name");
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var value = GetString(item, "name");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        if (node.ValueKind == JsonValueKind.String)
        {
            return node.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string GetJsonImageUrl(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var node))
        {
            return string.Empty;
        }

        return node.ValueKind switch
        {
            JsonValueKind.String => node.GetString() ?? string.Empty,
            JsonValueKind.Array => GetJsonImageUrlFromArray(node),
            JsonValueKind.Object => GetUrlOrContentUrl(node),
            _ => string.Empty
        };
    }

    private static string GetJsonImageUrlFromArray(JsonElement node)
    {
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                return item.GetString() ?? string.Empty;
            }

            if (item.ValueKind == JsonValueKind.Object)
            {
                var url = GetUrlOrContentUrl(item);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }
        }

        return string.Empty;
    }

    private static string GetUrlOrContentUrl(JsonElement node)
    {
        var url = GetString(node, "url");
        if (!string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        return GetString(node, "contentUrl");
    }

    private static string GetFirstNodeText(HtmlDocument document, string xpath, string attributeName)
    {
        var node = document.DocumentNode.SelectSingleNode(xpath);
        if (node == null)
        {
            return string.Empty;
        }

        return GetNodeText(node, attributeName);
    }

    private static string GetFirstNodeText(HtmlNode root, string xpath, string attributeName)
    {
        var node = root.SelectSingleNode(xpath);
        if (node == null)
        {
            return string.Empty;
        }

        return GetNodeText(node, attributeName);
    }

    private static string GetNodeText(HtmlNode node, string attributeName)
    {
        if (!string.IsNullOrWhiteSpace(attributeName))
        {
            var value = node.GetAttributeValue(attributeName, string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return HtmlEntity.DeEntitize(value);
            }
        }

        var inner = node.InnerText?.Trim() ?? string.Empty;
        return HtmlEntity.DeEntitize(inner);
    }

    private static int GetQobuzPlaylistTotalTrackCount(HtmlDocument document)
    {
        var container = document.DocumentNode.SelectSingleNode("//div[@id='playerTracks']");
        if (container == null)
        {
            return 0;
        }

        var totalRaw = container.GetAttributeValue("data-nbtracks", string.Empty).Trim();
        return int.TryParse(totalRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            ? Math.Max(0, total)
            : 0;
    }

    private static int ParseTrackPosition(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(0, parsed)
            : 0;
    }

    private static int ParseClockDurationSeconds(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (TimeSpan.TryParse(value.Trim(), CultureInfo.InvariantCulture, out var parsed))
        {
            return (int)Math.Round(parsed.TotalSeconds, MidpointRounding.AwayFromZero);
        }

        return 0;
    }

    private static int ParseDurationSeconds(JsonElement node, string propertyName)
    {
        if (!node.TryGetProperty(propertyName, out var durationNode))
        {
            return 0;
        }

        if (durationNode.ValueKind == JsonValueKind.Number)
        {
            if (durationNode.TryGetDouble(out var numericSeconds) && numericSeconds > 0)
            {
                return (int)Math.Round(numericSeconds, MidpointRounding.AwayFromZero);
            }
            return 0;
        }

        if (durationNode.ValueKind != JsonValueKind.String)
        {
            return 0;
        }

        var durationText = durationNode.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(durationText))
        {
            return 0;
        }

        if (double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var plainSeconds) && plainSeconds > 0)
        {
            return (int)Math.Round(plainSeconds, MidpointRounding.AwayFromZero);
        }

        if (TimeSpan.TryParse(durationText, CultureInfo.InvariantCulture, out var parsedTime))
        {
            return (int)Math.Round(parsedTime.TotalSeconds, MidpointRounding.AwayFromZero);
        }

        var match = IsoDurationRegex().Match(durationText);
        if (!match.Success)
        {
            return 0;
        }

        var hours = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var seconds = match.Groups["s"].Success
            ? double.Parse(match.Groups["s"].Value, NumberStyles.Float, CultureInfo.InvariantCulture)
            : 0d;

        var total = (hours * 3600d) + (minutes * 60d) + seconds;
        return total > 0d
            ? (int)Math.Round(total, MidpointRounding.AwayFromZero)
            : 0;
    }

    private static void AppendMissingQobuzTracksFromJsonLd(
        JsonElement? playlistNode,
        string playlistUrl,
        string resolvedPlaylistId,
        List<object> tracks,
        HashSet<string> dedupeKeys)
    {
        if (!playlistNode.HasValue
            || !playlistNode.Value.TryGetProperty("track", out var trackNode)
            || trackNode.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var normalizedPlaylistUrl = BuildAbsoluteUrl(playlistUrl);
        var fallbackIndex = 0;
        foreach (var item in trackNode.EnumerateArray())
        {
            fallbackIndex++;

            var title = GetString(item, "name");
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var artistName = GetJsonLdArtistName(item);
            if (string.IsNullOrWhiteSpace(artistName))
            {
                artistName = "Unknown Artist";
            }

            var albumTitle = GetString(item, "inAlbum");
            var duration = ParseDurationSeconds(item, "duration");
            var dedupeKey = BuildQobuzTrackDedupeKey(title, artistName, albumTitle, duration);
            if (dedupeKeys.Contains(dedupeKey))
            {
                continue;
            }

            dedupeKeys.Add(dedupeKey);
            var fallbackTrackId = BuildQobuzFallbackTrackId(resolvedPlaylistId, fallbackIndex);
            var fallbackTrackUrl = $"{normalizedPlaylistUrl}#track-{fallbackIndex}";
            tracks.Add(new
            {
                id = fallbackTrackId,
                title,
                duration,
                track_position = fallbackIndex,
                link = fallbackTrackUrl,
                sourceUrl = fallbackTrackUrl,
                isrc = string.Empty,
                artist = new { id = string.Empty, name = artistName },
                album = new
                {
                    id = string.Empty,
                    title = albumTitle,
                    cover_medium = string.Empty
                }
            });
        }
    }

    private static string GetJsonLdArtistName(JsonElement trackNode)
    {
        if (!trackNode.TryGetProperty("byArtist", out var artistNode))
        {
            return string.Empty;
        }

        return artistNode.ValueKind switch
        {
            JsonValueKind.String => artistNode.GetString() ?? string.Empty,
            JsonValueKind.Object => GetString(artistNode, "name"),
            JsonValueKind.Array => string.Join(
                ", ",
                artistNode.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : GetString(item, "name"))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!.Trim())),
            _ => string.Empty
        };
    }

    private static string BuildQobuzFallbackTrackId(string playlistId, int position)
    {
        var normalizedPlaylistId = string.IsNullOrWhiteSpace(playlistId) ? "qobuz-playlist" : playlistId.Trim();
        var normalizedPosition = Math.Max(1, position);
        return $"{normalizedPlaylistId}-jsonld-{normalizedPosition}";
    }

    private static string BuildQobuzTrackDedupeKey(string title, string artist, string album, int durationSeconds)
    {
        var normalizedTitle = (title ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedArtist = (artist ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedAlbum = (album ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedDuration = Math.Max(0, durationSeconds);
        return $"{normalizedTitle}|{normalizedArtist}|{normalizedAlbum}|{normalizedDuration}";
    }

    private static string ResolveQobuzPlaylistCoverUrl(HtmlDocument document, JsonElement? playlistNode)
    {
        var jsonLdCover = playlistNode.HasValue
            ? GetJsonImageUrl(playlistNode.Value, "image")
            : string.Empty;

        var pageCover = GetQobuzPageCoverUrl(document);
        var ogCover = document.DocumentNode
            .SelectSingleNode("//meta[@property='og:image']")
            ?.GetAttributeValue("content", string.Empty)
            ?? string.Empty;

        return SelectPreferredQobuzCover(pageCover, jsonLdCover, ogCover);
    }

    private static string GetQobuzPageCoverUrl(HtmlDocument document)
    {
        var coverImageNode = document.DocumentNode
            .SelectSingleNode("//div[contains(@class,'album-cover')]//img");
        if (coverImageNode == null)
        {
            return string.Empty;
        }

        var dataSourceUrl = coverImageNode.GetAttributeValue("data-src", string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(dataSourceUrl))
        {
            return HtmlEntity.DeEntitize(dataSourceUrl);
        }

        var sourceUrl = coverImageNode.GetAttributeValue("src", string.Empty).Trim();
        return string.IsNullOrWhiteSpace(sourceUrl)
            ? string.Empty
            : HtmlEntity.DeEntitize(sourceUrl);
    }

    private static string SelectPreferredQobuzCover(params string[] candidates)
    {
        return candidates.FirstOrDefault(candidate =>
                   !string.IsNullOrWhiteSpace(candidate) && !IsQobuzLogoCover(candidate))
               ?? candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))
               ?? string.Empty;
    }

    private static bool IsQobuzLogoCover(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("/assets-static/img/logo/qobuz_logo_og", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildAbsoluteUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return url;
        }

        return parsed.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
    }

    private static string ResolveQobuzPlaylistId(string? idHint, string url)
    {
        var explicitId = (idHint ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(explicitId) && !explicitId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return explicitId;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return explicitId;
        }

        var path = uri.AbsolutePath;
        var match = QobuzPlaylistIdRegex().Match(path);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return ExtractTerminalPathSegment(url);
    }

    private static string ExtractTerminalPathSegment(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var segment = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return segment ?? string.Empty;
    }

    private static string ResolveBandcampCoverUrl(HtmlDocument document, JsonElement trAlbumRoot)
    {
        var artId = 0L;
        if (trAlbumRoot.TryGetProperty("art_id", out var artNode) && artNode.ValueKind == JsonValueKind.Number)
        {
            artId = artNode.GetInt64();
        }
        else if (trAlbumRoot.TryGetProperty("current", out var currentNode)
                 && currentNode.ValueKind == JsonValueKind.Object
                 && currentNode.TryGetProperty("art_id", out var currentArtNode)
                 && currentArtNode.ValueKind == JsonValueKind.Number)
        {
            artId = currentArtNode.GetInt64();
        }

        if (artId > 0)
        {
            return $"https://f4.bcbits.com/img/a{artId}_16.jpg";
        }

        return document.DocumentNode
            .SelectSingleNode("//meta[@property='og:image']")
            ?.GetAttributeValue("content", string.Empty)
            ?? string.Empty;
    }

    private static string BuildBandcampTrackUrl(string albumUrl, string titleLink, int trackNumber)
    {
        if (Uri.TryCreate(titleLink, UriKind.Absolute, out var absolute))
        {
            return absolute.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        }

        if (!Uri.TryCreate(albumUrl, UriKind.Absolute, out var albumUri))
        {
            return albumUrl;
        }

        if (!string.IsNullOrWhiteSpace(titleLink))
        {
            var normalizedPath = titleLink.StartsWith('/') ? titleLink : $"/{titleLink}";
            if (Uri.TryCreate(albumUri, normalizedPath, out var combined))
            {
                return combined.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
            }
        }

        return trackNumber > 0
            ? $"{albumUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)}#t{trackNumber}"
            : albumUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
    }

    private static string ResolveBandcampCollectionId(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return "bandcamp";
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var albumIndex = Array.FindIndex(
            segments,
            segment => segment.Equals("album", StringComparison.OrdinalIgnoreCase));

        if (albumIndex >= 0 && albumIndex + 1 < segments.Length)
        {
            return segments[albumIndex + 1];
        }

        return segments.LastOrDefault() ?? "bandcamp";
    }

    private static string NormalizeSource(string? source)
    {
        var normalized = (source ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            TidalSource => TidalSource,
            QobuzSource => QobuzSource,
            BandcampSource => BandcampSource,
            _ => string.Empty
        };
    }

    private static string ComposeTitle(string title, string version)
    {
        var first = (title ?? string.Empty).Trim();
        var second = (version ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(second) ? first : $"{first} {second}".Trim();
    }

    private static string GetAnyString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return string.Empty;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString() ?? string.Empty,
            JsonValueKind.Number => prop.TryGetInt64(out var value) ? value.ToString() : string.Empty,
            _ => string.Empty
        };
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return 0;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(prop.GetString(), out var value) => value,
            _ => 0
        };
    }

    [GeneratedRegex(@"^PT(?:(?<h>\d+)H)?(?:(?<m>\d+)M)?(?:(?<s>\d+(?:\.\d+)?)S)?$", RegexOptions.IgnoreCase)]
    private static partial Regex IsoDurationRegex();

    [GeneratedRegex(@"/playlists?/[^/]+/([^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex QobuzPlaylistIdRegex();
}
