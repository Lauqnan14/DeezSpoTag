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
public sealed class ExternalPlaylistTracklistApiController : ControllerBase
{
    private const string TidalSource = "tidal";
    private const string QobuzSource = "qobuz";
    private const string BandcampSource = "bandcamp";
    private const string EncodedClientId = "NkJEU1JkcEs5aHFFQlRnVQ==";
    private const string EncodedClientSecret = "eGV1UG1ZN25icFo5SUliTEFjUTkzc2hrYTFWTmhlVUFxTjZJY3N6alRHOD0=";
    private const string AuthUrl = "https://auth.tidal.com/v1/oauth2/token";
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

        var title = metadata.GetValueOrDefault("title");
        var description = metadata.GetValueOrDefault("description");
        var coverId = metadata.GetValueOrDefault("squareImage");
        if (string.IsNullOrWhiteSpace(coverId))
        {
            coverId = metadata.GetValueOrDefault("image");
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
                "title");
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
                "title");
        }

        if (string.IsNullOrWhiteSpace(creatorName))
        {
            creatorName = "Qobuz";
        }

        var totalTracks = GetQobuzPlaylistTotalTrackCount(document);
        var resolvedId = ResolveQobuzPlaylistId(idHint, playlistUrl);
        var tracks = new List<object>();
        var dedupeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        var trackRows = document.DocumentNode.SelectNodes("//div[contains(@class,'track') and contains(@class,'track--playlist') and @data-track]");
        if (trackRows != null && trackRows.Count > 0)
        {
            foreach (var row in trackRows)
            {
                var trackId = row.GetAttributeValue("data-track", string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(trackId))
                {
                    continue;
                }

                position++;

                var trackPosition = ParseTrackPosition(
                    GetFirstNodeText(row, ".//div[contains(@class,'track__item--number')]/span[1]", string.Empty));
                if (trackPosition <= 0)
                {
                    trackPosition = position;
                }

                var trackTitle = GetFirstNodeText(row, ".//div[contains(@class,'track__item--name')]", "title");
                if (string.IsNullOrWhiteSpace(trackTitle))
                {
                    trackTitle = GetFirstNodeText(row, ".//div[contains(@class,'track__item--name')]/span[1]", string.Empty);
                }
                if (string.IsNullOrWhiteSpace(trackTitle))
                {
                    continue;
                }

                var artistName = GetFirstNodeText(row, ".//span[contains(@class,'track__item--artist')]", "title");
                if (string.IsNullOrWhiteSpace(artistName))
                {
                    artistName = GetFirstNodeText(row, ".//span[contains(@class,'track__item--artist')]//a[1]", string.Empty);
                }
                if (string.IsNullOrWhiteSpace(artistName))
                {
                    artistName = "Unknown Artist";
                }

                var albumTitle = GetFirstNodeText(row, ".//span[contains(@class,'track__item--album')]", "title");
                if (string.IsNullOrWhiteSpace(albumTitle))
                {
                    albumTitle = GetFirstNodeText(row, ".//span[contains(@class,'track__item--album')]//a[1]", string.Empty);
                }

                var durationText = GetFirstNodeText(row, ".//span[contains(@class,'track__item--duration')]", string.Empty);
                var duration = ParseClockDurationSeconds(durationText);

                var trackUrl = $"https://open.qobuz.com/track/{Uri.EscapeDataString(trackId)}";
                dedupeKeys.Add(BuildQobuzTrackDedupeKey(trackTitle, artistName, albumTitle, duration));
                tracks.Add(new
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
                });
            }
        }

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

        string title;
        if (root.TryGetProperty("current", out var currentNode) && currentNode.ValueKind == JsonValueKind.Object)
        {
            title = GetString(currentNode, "title");
        }
        else
        {
            title = string.Empty;
        }
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
        var tracks = new List<object>();
        if (root.TryGetProperty("trackinfo", out var trackInfoNode) && trackInfoNode.ValueKind == JsonValueKind.Array)
        {
            var fallbackPosition = 0;
            foreach (var trackNode in trackInfoNode.EnumerateArray())
            {
                fallbackPosition++;
                var trackTitle = GetString(trackNode, "title");
                if (string.IsNullOrWhiteSpace(trackTitle))
                {
                    continue;
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
                    trackId = trackNumber.ToString();
                }

                var titleLink = GetString(trackNode, "title_link");
                var trackUrl = BuildBandcampTrackUrl(albumUrl, titleLink, trackNumber);
                var isrc = GetString(trackNode, "isrc");

                tracks.Add(new
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
                        title,
                        cover_medium = coverUrl
                    }
                });
            }
        }

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
            ["title"] = GetString(root, "title"),
            ["description"] = GetString(root, "description"),
            ["squareImage"] = GetString(root, "squareImage"),
            ["image"] = GetString(root, "image"),
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

            var pageTotal = GetInt(root, "totalNumberOfItems");
            if (pageTotal > 0)
            {
                total = pageTotal;
            }

            if (!root.TryGetProperty("items", out var itemsElement)
                || itemsElement.ValueKind != JsonValueKind.Array
                || itemsElement.GetArrayLength() == 0)
            {
                break;
            }

            var appended = 0;
            foreach (var wrapper in itemsElement.EnumerateArray())
            {
                var trackNode = wrapper;
                if (wrapper.TryGetProperty("item", out var itemNode) && itemNode.ValueKind == JsonValueKind.Object)
                {
                    trackNode = itemNode;
                }

                var trackId = GetAnyString(trackNode, "id");
                if (string.IsNullOrWhiteSpace(trackId))
                {
                    continue;
                }

                position++;
                appended++;

                var trackTitle = ComposeTitle(GetString(trackNode, "title"), GetString(trackNode, "version"));
                var duration = GetInt(trackNode, "duration");
                var isrc = GetString(trackNode, "isrc");
                var trackUrl = GetString(trackNode, "url");
                if (string.IsNullOrWhiteSpace(trackUrl))
                {
                    trackUrl = $"https://tidal.com/browse/track/{Uri.EscapeDataString(trackId)}";
                }
                else if (Uri.TryCreate(trackUrl, UriKind.Absolute, out var parsedTrackUrl)
                    && string.Equals(parsedTrackUrl.Host, "www.tidal.com", StringComparison.OrdinalIgnoreCase))
                {
                    trackUrl = trackUrl.Replace("https://www.tidal.com", "https://tidal.com/browse", StringComparison.OrdinalIgnoreCase);
                }

                var artistName = ResolveArtistName(trackNode);
                var albumTitle = string.Empty;
                var albumCover = string.Empty;
                var albumId = string.Empty;
                if (trackNode.TryGetProperty("album", out var albumNode) && albumNode.ValueKind == JsonValueKind.Object)
                {
                    albumTitle = GetString(albumNode, "title");
                    albumCover = BuildTidalImageUrl(GetString(albumNode, "cover"));
                    albumId = GetAnyString(albumNode, "id");
                }

                tracks.Add(new
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
                        id = albumId,
                        title = albumTitle,
                        cover_medium = albumCover
                    }
                });
            }

            if (appended == 0)
            {
                break;
            }

            offset += itemsElement.GetArrayLength();
        }

        return tracks;
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

            using var request = new HttpRequestMessage(HttpMethod.Post, AuthUrl)
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
            _cachedToken = token;
            _cachedTokenExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 300);
            return _cachedToken;
        }
        finally
        {
            TokenLock.Release();
        }
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
                continue;
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateJsonNodes(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("@graph", out var graphNode) && graphNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in graphNode.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        yield return item;
                    }
                }
                yield break;
            }

            yield return element;
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumerateJsonNodes(item))
                {
                    yield return nested;
                }
            }
        }
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

        if (node.ValueKind == JsonValueKind.String)
        {
            return node.GetString() ?? string.Empty;
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    return item.GetString() ?? string.Empty;
                }

                if (item.ValueKind == JsonValueKind.Object)
                {
                    var url = GetString(item, "url");
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }

                    url = GetString(item, "contentUrl");
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }
            }
            return string.Empty;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            var url = GetString(node, "url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            url = GetString(node, "contentUrl");
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return string.Empty;
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

        if (TimeSpan.TryParse(durationText, out var parsedTime))
        {
            return (int)Math.Round(parsedTime.TotalSeconds, MidpointRounding.AwayFromZero);
        }

        var match = Regex.Match(
            durationText,
            @"^PT(?:(?<h>\d+)H)?(?:(?<m>\d+)M)?(?:(?<s>\d+(?:\.\d+)?)S)?$",
            RegexOptions.IgnoreCase);
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
        ICollection<object> tracks,
        ISet<string> dedupeKeys)
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

        if (artistNode.ValueKind == JsonValueKind.String)
        {
            return artistNode.GetString() ?? string.Empty;
        }

        if (artistNode.ValueKind == JsonValueKind.Object)
        {
            var artistName = GetString(artistNode, "name");
            if (!string.IsNullOrWhiteSpace(artistName))
            {
                return artistName;
            }
        }

        if (artistNode.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var item in artistNode.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.String
                    ? item.GetString() ?? string.Empty
                    : GetString(item, "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name.Trim());
                }
            }

            if (names.Count > 0)
            {
                return string.Join(", ", names);
            }
        }

        return string.Empty;
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
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || IsQobuzLogoCover(candidate))
            {
                continue;
            }

            return candidate;
        }

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
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
        var match = Regex.Match(path, @"/playlists?/[^/]+/([^/?#]+)", RegexOptions.IgnoreCase);
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
            var normalizedPath = titleLink.StartsWith("/", StringComparison.Ordinal) ? titleLink : $"/{titleLink}";
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
}
