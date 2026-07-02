using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeezSpoTag.Services.Download.Fallback;
using DeezSpoTag.Services.Matching;

namespace DeezSpoTag.Web.Services;

public sealed class AmazonMusicMetadataService : IAmazonFallbackTrackResolver
{
    private const string DefaultHost = "music.amazon.com";
    private const string DefaultLocale = "en_US";
    private const string SkillApiBaseUrl = "https://na.mesk.skill.music.a2z.com/api";
    private const string SkillArtistApiUrl = "https://na.mesk.skill.music.a2z.com/api/explore/v1/showCatalogArtist";
    private const string SkillTrackApiUrl = "https://na.mesk.skill.music.a2z.com/api/cosmicTrack/displayCatalogTrack";
    private const string DeviceFamily = "WebPlayer";
    private const string DeviceModel = "WEBPLAYER";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PlatformAuthService _platformAuthService;
    private readonly ILogger<AmazonMusicMetadataService> _logger;

    public AmazonMusicMetadataService(
        IHttpClientFactory httpClientFactory,
        PlatformAuthService platformAuthService,
        ILogger<AmazonMusicMetadataService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _platformAuthService = platformAuthService;
        _logger = logger;
    }

    public async Task<AmazonSearchPayload> SearchAsync(
        string query,
        string type,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return AmazonSearchPayload.Empty;
        }

        var session = await CreateSessionAsync(cancellationToken);
        var keyword = query.Trim();
        var pageUrl = $"https://{session.Host}/search/{Uri.EscapeDataString(keyword)}";
        var body = new Dictionary<string, string>
        {
            ["filter"] = JsonSerializer.Serialize(new { IsLibrary = new[] { "false" } }, JsonOptions),
            ["keyword"] = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["interface"] = "Web.TemplatesInterface.v1_0.Touch.SearchTemplateInterface.SearchKeywordClientInformation",
                ["keyword"] = keyword
            }, JsonOptions),
            ["suggestedKeyword"] = keyword,
            ["userHash"] = JsonSerializer.Serialize(new { level = "LIBRARY_MEMBER" }, JsonOptions),
            ["headers"] = BuildAmazonSkillHeaders(session, pageUrl)
        };

        using var document = await PostSkillJsonAsync(session, $"{SkillApiBaseUrl}/showSearch", pageUrl, body, cancellationToken);
        var items = ExtractSearchCatalogItems(document.RootElement, type).ToArray();
        return new AmazonSearchPayload(
            FilterByType(items, "track", type, keyword, limit).ToArray(),
            FilterByType(items, "album", type, keyword, limit).ToArray(),
            FilterByType(items, "artist", type, keyword, limit).ToArray(),
            FilterByType(items, "playlist", type, keyword, limit).ToArray());
    }

    public async Task<AmazonTracklistPayload?> GetTracklistAsync(
        string id,
        string type,
        string? url,
        CancellationToken cancellationToken)
    {
        var normalizedType = NormalizeType(type);
        var targetUrl = NormalizeAmazonUrl(url) ?? BuildCatalogUrl(id, normalizedType);
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return null;
        }

        var session = await CreateSessionAsync(cancellationToken);
        using var document = normalizedType == "track"
            ? await FetchTrackDocumentAsync(session, id, targetUrl, cancellationToken)
            : await FetchHomeDocumentAsync(session, targetUrl, cancellationToken);

        var collectionItems = ExtractCatalogItems(document.RootElement).ToArray();
        IReadOnlyList<AmazonCatalogItem> trackItems = ExtractTracklistTrackItems(document.RootElement).ToArray();
        var structuredAlbum = ReadStructuredAlbum(document.RootElement, ExtractAsin(id) ?? id);
        if (trackItems.Count == 0)
        {
            trackItems = collectionItems
                .Where(static item => item.Type == "track")
                .ToArray();
        }

        var collection = ReadCollectionProfile(document.RootElement, id, normalizedType, targetUrl)
            ?? ResolveCollection(collectionItems, id, normalizedType, targetUrl);
        if (structuredAlbum is not null)
        {
            collection = MergeStructuredAlbumCollection(collection, structuredAlbum);
        }

        if (structuredAlbum is not null)
        {
            trackItems = MergeStructuredAlbumTracks(trackItems, structuredAlbum);
        }

        trackItems = MergeCollectionIntoTracks(trackItems, collection);
        var tracks = trackItems
            .Where(static item => item.Type == "track" && !string.IsNullOrWhiteSpace(item.Id))
            .Select((item, index) => item.ToTrack(index + 1))
            .ToArray();
        if (normalizedType == "track" && tracks.Length == 0)
        {
            var track = MergeCollectionIntoTracks(
                    collectionItems.Where(static item => item.Type == "track").ToArray(),
                    collection)
                .FirstOrDefault();
            if (track is not null)
            {
                tracks = [track.ToTrack(1)];
            }
        }

        return new AmazonTracklistPayload(collection, tracks);
    }

    public async Task<AmazonArtistPagePayload?> GetArtistPageAsync(string id, CancellationToken cancellationToken)
    {
        var targetUrl = BuildCatalogUrl(id, "artist");
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            return null;
        }

        var session = await CreateSessionAsync(cancellationToken);
        using var document = await FetchArtistDocumentAsync(session, id, targetUrl, cancellationToken);
        var sectionItems = ExtractArtistSectionItems(document.RootElement).ToArray();
        var items = sectionItems.Select(static item => item.Item).ToArray();
        var artistId = ExtractAsin(id) ?? id;
        var artist = items.FirstOrDefault(item => item.Type == "artist" && string.Equals(item.Id, artistId, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault(item => item.Type == "artist")
            ?? ReadArtistProfile(document.RootElement, artistId, targetUrl);
        if (artist is null)
        {
            return null;
        }

        var releases = sectionItems
            .Where(static item => item.Section is "releases" or "top_albums" && item.Item.Type == "album")
            .Select(static item => item.Item)
            .GroupBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        var topTracks = sectionItems
            .Where(static item => item.Section == "top_songs" && item.Item.Type != "artist")
            .Select(static item => NormalizeAmazonTopSongItem(item.Item))
            .GroupBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        var related = sectionItems
            .Where(item => item.Section == "similar_artists"
                && item.Item.Type == "artist"
                && !string.Equals(item.Item.Id, artist.Id, StringComparison.OrdinalIgnoreCase))
            .Select(static item => item.Item)
            .GroupBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();

        var appearsOn = sectionItems
            .Where(static item => item.Section == "appears_on" && item.Item.Type == "album")
            .Select(static item => item.Item)
            .GroupBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();

        return new AmazonArtistPagePayload(artist, releases, topTracks, related, appearsOn);
    }

    public async Task<AmazonCatalogItem?> GetTrackAsync(string amazonId, CancellationToken cancellationToken)
    {
        var asin = ExtractAsin(amazonId) ?? amazonId.Trim();
        if (string.IsNullOrWhiteSpace(asin))
        {
            return null;
        }

        var session = await CreateSessionAsync(cancellationToken);
        using var document = await FetchTrackDocumentAsync(
            session,
            asin,
            $"https://{session.Host}/tracks/{asin}",
            cancellationToken);
        var exact = ExtractCatalogItems(document.RootElement)
            .Where(static item => item.Type == "track")
            .FirstOrDefault(item => string.Equals(item.Id, asin, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        return ExtractCatalogItems(document.RootElement)
            .FirstOrDefault(static item => item.Type == "track" && !string.IsNullOrWhiteSpace(item.Id));
    }

    public async Task<AmazonCatalogItem?> ResolveTrackAsync(
        string title,
        string artist,
        string? album,
        int? durationMs,
        CancellationToken cancellationToken)
    {
        var query = BuildTrackSearchQuery(title, artist, album);
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var search = await SearchAsync(query, "track", 25, cancellationToken);
        foreach (var candidate in search.Tracks.Where(IsDownloadableMusicTrackResult))
        {
            var validation = TrackCandidateValidator.Validate(
                new TrackMatchSource(
                    Isrc: null,
                    Title: title,
                    Artist: artist,
                    Album: album,
                    DurationMs: durationMs),
                new TrackMatchCandidate(
                    ProviderId: candidate.Id,
                    Isrc: candidate.Isrc,
                    Title: candidate.Title,
                    Artist: candidate.Artist,
                    Album: candidate.Album,
                    DurationMs: candidate.DurationMs),
                new TrackCandidateValidationOptions(
                    StrictWithoutIsrc: true,
                    AllowMissingCandidateArtist: false,
                    RequireCandidateDurationWhenSourceHasDuration: false,
                    MaxIsrcDurationDifferenceMs: 20_000,
                    MaxMetadataDurationDifferenceMs: 8_000));
            if (validation.Accepted)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string BuildTrackSearchQuery(string title, string artist, string? album)
        => string.Join(' ', new[] { title, artist, album }.Where(static value => !string.IsNullOrWhiteSpace(value)));

    private static bool IsDownloadableMusicTrackResult(AmazonCatalogItem item)
        => item.Type == "track"
           && !string.IsNullOrWhiteSpace(item.Id)
           && !string.IsNullOrWhiteSpace(item.Title)
           && !string.IsNullOrWhiteSpace(item.Artist);

    async Task<AmazonFallbackTrackResolution?> IAmazonFallbackTrackResolver.ResolveTrackAsync(
        string title,
        string artist,
        string? album,
        int? durationMs,
        CancellationToken cancellationToken)
    {
        var item = await ResolveTrackAsync(title, artist, album, durationMs, cancellationToken);
        return item == null || string.IsNullOrWhiteSpace(item.Id)
            ? null
            : new AmazonFallbackTrackResolution(item.Id, item.Url);
    }

    private async Task<AmazonSession> CreateSessionAsync(CancellationToken cancellationToken)
    {
        var auth = (await _platformAuthService.LoadAsync()).AmazonMusic;
        var host = NormalizeHost(auth?.Host);
        var locale = string.IsNullOrWhiteSpace(auth?.Locale) ? DefaultLocale : auth.Locale.Trim();
        var client = _httpClientFactory.CreateClient(nameof(AmazonMusicMetadataService));
        client.Timeout = TimeSpan.FromSeconds(30);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/config.json");
        AddAmazonHeaders(request, host, auth?.Cookie);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var sessionId = ReadString(document.RootElement, "sessionId");
        var deviceId = ReadString(document.RootElement, "deviceId");
        var csrf = ReadString(document.RootElement, "csrf");
        var csrfTimestamp = ReadNestedString(document.RootElement, "csrf", "ts");
        var csrfNonce = ReadNestedString(document.RootElement, "csrf", "rnd");
        var version = ReadString(document.RootElement, "version");
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(csrf))
        {
            throw new InvalidOperationException("Amazon Music metadata session could not be initialized.");
        }

        return new AmazonSession(client, host, locale, sessionId, deviceId, csrf, csrfTimestamp, csrfNonce, version, auth?.Cookie);
    }

    private async Task<JsonDocument> FetchHomeDocumentAsync(AmazonSession session, string url, CancellationToken cancellationToken)
    {
        var deeplink = ToAmazonDeeplink(url);
        var pageUrl = $"https://{session.Host}{deeplink}";
        var payload = new Dictionary<string, string>
        {
            ["deeplink"] = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["interface"] = "DeeplinkInterface.v1_0.DeeplinkClientInformation",
                ["deeplink"] = deeplink
            }, JsonOptions),
            ["headers"] = BuildAmazonSkillHeaders(session, pageUrl)
        };
        return await PostSkillJsonAsync(session, $"{SkillApiBaseUrl}/showHome", pageUrl, payload, cancellationToken);
    }

    private async Task<JsonDocument> FetchTrackDocumentAsync(
        AmazonSession session,
        string id,
        string url,
        CancellationToken cancellationToken)
    {
        var asin = ExtractAsin(id) ?? ExtractAsin(url);
        if (string.IsNullOrWhiteSpace(asin))
        {
            return await FetchHomeDocumentAsync(session, url, cancellationToken);
        }

        var pageUrl = $"https://{session.Host}/tracks/{asin}";
        var payload = new Dictionary<string, string>
        {
            ["id"] = asin,
            ["userHash"] = JsonSerializer.Serialize(new { level = "LIBRARY_MEMBER" }, JsonOptions),
            ["headers"] = BuildAmazonSkillHeaders(session, pageUrl)
        };
        return await PostSkillJsonAsync(session, SkillTrackApiUrl, pageUrl, payload, cancellationToken);
    }

    private async Task<JsonDocument> FetchArtistDocumentAsync(
        AmazonSession session,
        string id,
        string url,
        CancellationToken cancellationToken)
    {
        var asin = ExtractAsin(id) ?? ExtractAsin(url);
        if (string.IsNullOrWhiteSpace(asin))
        {
            return await FetchHomeDocumentAsync(session, url, cancellationToken);
        }

        var pageUrl = $"https://{session.Host}/artists/{asin}";
        var payload = new Dictionary<string, string>
        {
            ["id"] = asin,
            ["userHash"] = JsonSerializer.Serialize(new { level = "LIBRARY_MEMBER" }, JsonOptions),
            ["headers"] = BuildAmazonSkillHeaders(session, pageUrl)
        };
        return await PostSkillJsonAsync(session, SkillArtistApiUrl, pageUrl, payload, cancellationToken);
    }

    private async Task<JsonDocument> PostSkillJsonAsync(
        AmazonSession session,
        string url,
        string pageUrl,
        object payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        AddAmazonHeaders(request, session.Host, session.Cookie);
        request.Headers.Referrer = new Uri(pageUrl);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "text/plain");
        using var response = await session.Client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Amazon Music metadata call {Url} failed with HTTP {Status}: {Body}", url, (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        return JsonDocument.Parse(body);
    }

    private static string BuildAmazonSkillHeaders(AmazonSession session, string pageUrl)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var csrf = new Dictionary<string, string>
        {
            ["interface"] = "CSRFInterface.v1_0.CSRFHeaderElement",
            ["token"] = session.Csrf,
            ["timestamp"] = string.IsNullOrWhiteSpace(session.CsrfTimestamp) ? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() : session.CsrfTimestamp,
            ["rndNonce"] = string.IsNullOrWhiteSpace(session.CsrfNonce) ? Random.Shared.Next(1_000_000, int.MaxValue).ToString() : session.CsrfNonce
        };
        var auth = new Dictionary<string, string>
        {
            ["interface"] = "ClientAuthenticationInterface.v1_0.ClientTokenElement",
            ["accessToken"] = string.Empty
        };
        var headers = new Dictionary<string, string>
        {
            ["x-amzn-authentication"] = JsonSerializer.Serialize(auth, JsonOptions),
            ["x-amzn-device-model"] = DeviceModel,
            ["x-amzn-device-width"] = "1920",
            ["x-amzn-device-family"] = DeviceFamily,
            ["x-amzn-device-id"] = session.DeviceId,
            ["x-amzn-user-agent"] = UserAgent,
            ["x-amzn-session-id"] = session.SessionId,
            ["x-amzn-device-height"] = "1080",
            ["x-amzn-request-id"] = $"{Guid.NewGuid():N}-{now}",
            ["x-amzn-device-language"] = session.Locale,
            ["x-amzn-currency-of-preference"] = ResolveCurrency(session.Host),
            ["x-amzn-os-version"] = "1.0",
            ["x-amzn-application-version"] = session.Version ?? string.Empty,
            ["x-amzn-device-time-zone"] = TimeZoneInfo.Local.Id,
            ["x-amzn-timestamp"] = now,
            ["x-amzn-csrf"] = JsonSerializer.Serialize(csrf, JsonOptions),
            ["x-amzn-music-domain"] = session.Host,
            ["x-amzn-referer"] = string.Empty,
            ["x-amzn-affiliate-tags"] = string.Empty,
            ["x-amzn-ref-marker"] = string.Empty,
            ["x-amzn-page-url"] = pageUrl,
            ["x-amzn-weblab-id-overrides"] = string.Empty,
            ["x-amzn-video-player-token"] = string.Empty,
            ["x-amzn-feature-flags"] = string.Empty,
            ["x-amzn-has-profile-id"] = string.Empty,
            ["x-amzn-age-band"] = string.Empty
        };
        return JsonSerializer.Serialize(headers, JsonOptions);
    }

    private static void AddAmazonHeaders(HttpRequestMessage request, string host, string? cookie)
    {
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Referrer = new Uri($"https://{host}/");
        request.Headers.TryAddWithoutValidation("Origin", $"https://{host}");
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie.Trim());
        }
    }

    private static IEnumerable<AmazonCatalogItem> ExtractCatalogItems(JsonElement root)
    {
        foreach (var node in WalkObjects(root))
        {
            var item = ReadCatalogItem(node);
            if (item is not null)
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<AmazonCatalogItem> ExtractSearchCatalogItems(JsonElement root, string requestedType)
    {
        var normalizedRequest = NormalizeType(requestedType);
        var requestedSection = normalizedRequest == "all" ? null : normalizedRequest;
        foreach (var item in WalkSearchCatalogItems(root, currentSection: requestedSection))
        {
            if (normalizedRequest != "all" && item.Type != normalizedRequest)
            {
                continue;
            }

            yield return item;
        }
    }

    private static IEnumerable<AmazonCatalogItem> ExtractTracklistTrackItems(JsonElement root)
    {
        foreach (var item in WalkSearchCatalogItems(root, currentSection: "track"))
        {
            if (item.Type == "track")
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<AmazonSectionCatalogItem> ExtractArtistSectionItems(JsonElement root)
    {
        foreach (var item in WalkArtistSectionItems(root, currentSection: null))
        {
            yield return item;
        }
    }

    private static IEnumerable<AmazonSectionCatalogItem> WalkArtistSectionItems(JsonElement element, string? currentSection)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var nextSection = ResolveArtistSection(element) ?? currentSection;
            var item = ReadCatalogItem(element);
            if (item is not null && !string.IsNullOrWhiteSpace(nextSection) && IsSearchResultCard(element))
            {
                yield return new AmazonSectionCatalogItem(nextSection, item);
            }

            foreach (var property in element.EnumerateObject())
            {
                var propertySection = InferArtistSectionFromPropertyName(property.Name) ?? nextSection;
                foreach (var child in WalkArtistSectionItems(property.Value, propertySection))
                {
                    yield return child;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var child in WalkArtistSectionItems(item, currentSection))
                {
                    yield return child;
                }
            }
        }
    }

    private static string? ResolveArtistSection(JsonElement node)
    {
        if (!HasArrayChild(node))
        {
            return null;
        }

        foreach (var property in node.EnumerateObject())
        {
            var propertySection = InferArtistSectionFromPropertyName(property.Name);
            if (!string.IsNullOrWhiteSpace(propertySection))
            {
                return propertySection;
            }

            if (!LooksLikeSectionLabelProperty(property.Name))
            {
                continue;
            }

            var label = ReadText(property.Value);
            var labelSection = InferArtistSectionFromLabel(label);
            if (!string.IsNullOrWhiteSpace(labelSection))
            {
                return labelSection;
            }
        }

        return null;
    }

    private static IEnumerable<AmazonCatalogItem> WalkSearchCatalogItems(JsonElement element, string? currentSection)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var nextSection = ResolveSearchSectionType(element) ?? currentSection;
            var item = ReadCatalogItem(element);
            if (item is not null
                && string.Equals(nextSection, item.Type, StringComparison.OrdinalIgnoreCase)
                && IsSearchResultCard(element))
            {
                yield return item;
            }

            foreach (var property in element.EnumerateObject())
            {
                var propertySection = InferSectionFromPropertyName(property.Name) ?? nextSection;
                foreach (var child in WalkSearchCatalogItems(property.Value, propertySection))
                {
                    yield return child;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var child in WalkSearchCatalogItems(item, currentSection))
                {
                    yield return child;
                }
            }
        }
    }

    private static string? ResolveSearchSectionType(JsonElement node)
    {
        if (!HasArrayChild(node))
        {
            return null;
        }

        foreach (var property in node.EnumerateObject())
        {
            var propertySection = InferSectionFromPropertyName(property.Name);
            if (!string.IsNullOrWhiteSpace(propertySection))
            {
                return propertySection;
            }

            if (!LooksLikeSectionLabelProperty(property.Name))
            {
                continue;
            }

            var label = ReadText(property.Value);
            var labelSection = InferSectionFromLabel(label);
            if (!string.IsNullOrWhiteSpace(labelSection))
            {
                return labelSection;
            }
        }

        return null;
    }

    private static bool IsSearchResultCard(JsonElement node)
    {
        if (node.TryGetProperty("primaryText", out _)
            || node.TryGetProperty("primaryTextLink", out _)
            || node.TryGetProperty("primaryLink", out _)
            || node.TryGetProperty("secondaryText1", out _)
            || node.TryGetProperty("secondaryText", out _))
        {
            return true;
        }

        return false;
    }

    private static bool HasArrayChild(JsonElement node)
    {
        foreach (var property in node.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeSectionLabelProperty(string propertyName)
    {
        return propertyName.Contains("header", StringComparison.OrdinalIgnoreCase)
            || propertyName.Contains("heading", StringComparison.OrdinalIgnoreCase)
            || propertyName.Contains("section", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("primaryText", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("secondaryText", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("secondaryText1", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("title", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("text", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("label", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("name", StringComparison.OrdinalIgnoreCase);
    }

    private static string? InferSectionFromPropertyName(string propertyName)
    {
        var normalized = propertyName.ToLowerInvariant();
        if (normalized.Contains("tracklist", StringComparison.Ordinal)
            || normalized.Contains("songlist", StringComparison.Ordinal)
            || normalized is "tracks" or "songs")
        {
            return "track";
        }

        if (normalized.Contains("albumlist", StringComparison.Ordinal)
            || normalized is "albums")
        {
            return "album";
        }

        if (normalized.Contains("artistlist", StringComparison.Ordinal)
            || normalized is "artists")
        {
            return "artist";
        }

        if (normalized.Contains("playlistlist", StringComparison.Ordinal)
            || normalized is "playlists")
        {
            return "playlist";
        }

        return null;
    }

    private static string? InferSectionFromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var normalized = Regex.Replace(label.ToLowerInvariant(), "[^a-z0-9]+", " ", RegexOptions.None, RegexTimeout).Trim();
        return normalized switch
        {
            "tracks" or "track" or "songs" or "song" => "track",
            "albums" or "album" => "album",
            "artists" or "artist" => "artist",
            "playlists" or "playlist" => "playlist",
            _ => null
        };
    }

    private static string? InferArtistSectionFromPropertyName(string propertyName)
    {
        var normalized = propertyName.ToLowerInvariant();
        if (normalized.Contains("highlight", StringComparison.Ordinal))
        {
            return "highlights";
        }

        if (normalized.Contains("topsong", StringComparison.Ordinal)
            || normalized.Contains("toptrack", StringComparison.Ordinal))
        {
            return "top_songs";
        }

        if (normalized.Contains("topalbum", StringComparison.Ordinal))
        {
            return "top_albums";
        }

        if (normalized.Contains("appears", StringComparison.Ordinal))
        {
            return "appears_on";
        }

        if (normalized.Contains("similar", StringComparison.Ordinal)
            || normalized.Contains("related", StringComparison.Ordinal))
        {
            return "similar_artists";
        }

        if (normalized.Contains("release", StringComparison.Ordinal)
            || normalized.Contains("albumlist", StringComparison.Ordinal))
        {
            return "releases";
        }

        return null;
    }

    private static string? InferArtistSectionFromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var normalized = Regex.Replace(label.ToLowerInvariant(), "[^a-z0-9]+", " ", RegexOptions.None, RegexTimeout).Trim();
        return normalized switch
        {
            "highlights" or "highlight" => "highlights",
            "top songs" or "top song" or "top tracks" or "top track" => "top_songs",
            "top albums" or "top album" => "top_albums",
            "appears on" or "featured on" => "appears_on",
            "similar artists" or "related artists" => "similar_artists",
            "releases" or "release" or "discography" => "releases",
            _ => null
        };
    }

    private static AmazonCatalogItem? ReadCatalogItem(JsonElement node)
    {
        var deeplink = ReadDeepLink(node);
        var (type, id) = ParseCatalogDeeplink(deeplink);
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var title = CleanAmazonDisplayText(FirstText(node, "primaryText", "title", "name"));
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var artist = type == "track"
            ? CleanAmazonDisplayText(FirstText(node, "secondaryText2", "artistName", "artist", "secondaryText1", "secondaryText"))
            : CleanAmazonDisplayText(FirstText(node, "secondaryText1", "secondaryText", "artistName", "artist"));
        var album = type == "track"
            ? CleanAmazonDisplayText(ReadAlbumTitleFromContextMenu(node))
            : CleanAmazonDisplayText(FirstText(node, "secondaryText2", "tertiaryText", "albumTitle", "album"));
        var durationMs = ReadInt(node, "duration")
            ?? ReadInt(node, "durationMs")
            ?? ParseClockDurationMs(FirstText(node, "secondaryText3", "tertiaryText"));

        return new AmazonCatalogItem(
            Id: id,
            Type: type,
            Title: title,
            Artist: artist,
            Album: album,
            Url: BuildCatalogUrl(id, type) ?? deeplink ?? string.Empty,
            CoverUrl: NormalizeAmazonImageUrl(FirstImage(node)),
            DurationMs: durationMs,
            Isrc: FirstText(node, "isrc", "ISRC"));
    }

    private static AmazonCatalogItem? ReadArtistProfile(JsonElement root, string id, string url)
    {
        foreach (var node in WalkObjects(root))
        {
            var title = CleanAmazonDisplayText(FirstText(node, "artistName", "name", "title", "primaryText"));
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var explicitId = FirstText(node, "asin", "id", "artistAsin", "catalogId");
            if (!string.IsNullOrWhiteSpace(explicitId))
            {
                var normalizedId = ExtractAsin(explicitId);
                if (!string.IsNullOrWhiteSpace(normalizedId)
                    && !string.Equals(normalizedId, id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            return new AmazonCatalogItem(
                Id: id,
                Type: "artist",
                Title: title,
                Artist: string.Empty,
                Album: string.Empty,
                Url: url,
                CoverUrl: NormalizeAmazonImageUrl(FirstImage(node)),
                DurationMs: null,
                Isrc: string.Empty);
        }

        return null;
    }

    private static AmazonCollection? ReadCollectionProfile(JsonElement root, string id, string type, string url)
    {
        var targetId = ExtractAsin(id) ?? id;
        foreach (var node in WalkObjects(root))
        {
            var rawTitle = FirstText(node, "headline", "header", "title", "name", "primaryText");
            var title = CleanAmazonCollectionTitle(rawTitle);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var explicitId = ExtractAsin(FirstText(node, "asin", "id", "albumAsin", "playlistAsin", "artistAsin", "catalogId"));
            var parsed = ParseCatalogDeeplink(ReadDeepLink(node));
            var exactMatch = string.Equals(explicitId, targetId, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(parsed.Id, targetId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(parsed.Type, type, StringComparison.OrdinalIgnoreCase));
            var headerMatch = rawTitle.TrimStart().StartsWith("Play ", StringComparison.OrdinalIgnoreCase)
                && type is "album" or "playlist";
            if (!exactMatch && !headerMatch)
            {
                continue;
            }

            return new AmazonCollection(
                Id: targetId,
                Type: type,
                Title: title,
                Artist: CleanAmazonDisplayText(FirstText(node, "secondaryText1", "secondaryText", "artistName", "artist")),
                Url: url,
                CoverUrl: NormalizeAmazonImageUrl(FirstImage(node)));
        }

        return null;
    }

    private static AmazonStructuredAlbum? ReadStructuredAlbum(JsonElement root, string albumId)
    {
        AmazonStructuredAlbum? bestMatch = null;
        foreach (var script in ReadJsonLdObjects(root))
        {
            if (!script.TryGetProperty("@type", out var typeElement)
                || !string.Equals(ReadText(typeElement), "MusicAlbum", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = ExtractAsin(FirstText(script, "@id", "url")) ?? albumId;
            if (!string.IsNullOrWhiteSpace(albumId)
                && !string.Equals(id, albumId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = CleanAmazonDisplayText(FirstText(script, "name"));
            var artist = ReadStructuredArtistName(script);
            var cover = NormalizeAmazonImageUrl(FirstImage(script));
            if (string.IsNullOrWhiteSpace(cover))
            {
                cover = ReadHeaderImage(root, id);
            }
            var tracks = new List<AmazonStructuredTrack>();
            if (script.TryGetProperty("track", out var trackElement) && trackElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in trackElement.EnumerateArray())
                {
                    var trackId = ExtractAsin(FirstText(item, "@id", "url"));
                    var trackTitle = CleanAmazonDisplayText(FirstText(item, "name"));
                    if (string.IsNullOrWhiteSpace(trackId) || string.IsNullOrWhiteSpace(trackTitle))
                    {
                        continue;
                    }

                    tracks.Add(new AmazonStructuredTrack(
                        Id: trackId,
                        Title: trackTitle,
                        Position: ReadInt(item, "position") ?? tracks.Count + 1,
                        DurationMs: ParseIsoDurationMs(FirstText(item, "duration"))));
                }
            }

            var candidate = new AmazonStructuredAlbum(
                Id: id,
                Title: title,
                Artist: artist,
                CoverUrl: cover,
                Tracks: tracks);
            if (bestMatch is null
                || candidate.Tracks.Count > bestMatch.Tracks.Count
                || (candidate.Tracks.Count == bestMatch.Tracks.Count
                    && string.IsNullOrWhiteSpace(bestMatch.CoverUrl)
                    && !string.IsNullOrWhiteSpace(candidate.CoverUrl)))
            {
                bestMatch = candidate;
            }
        }

        return bestMatch;
    }

    private static IReadOnlyList<AmazonCatalogItem> MergeStructuredAlbumTracks(
        IReadOnlyList<AmazonCatalogItem> tracks,
        AmazonStructuredAlbum album)
    {
        if (album.Tracks.Count == 0)
        {
            return tracks;
        }

        var byId = album.Tracks.ToDictionary(static track => track.Id, StringComparer.OrdinalIgnoreCase);
        return tracks
            .Select(track =>
            {
                if (!byId.TryGetValue(track.Id, out var structured))
                {
                    return track;
                }

                return track with
                {
                    Title = string.IsNullOrWhiteSpace(track.Title) ? structured.Title : track.Title,
                    Artist = string.IsNullOrWhiteSpace(track.Artist) ? album.Artist : track.Artist,
                    Album = string.IsNullOrWhiteSpace(track.Album) || string.Equals(track.Album, track.Title, StringComparison.OrdinalIgnoreCase)
                        ? album.Title
                        : track.Album,
                    CoverUrl = string.IsNullOrWhiteSpace(track.CoverUrl) ? album.CoverUrl : track.CoverUrl,
                    DurationMs = track.DurationMs is > 0 ? track.DurationMs : structured.DurationMs
                };
            })
            .OrderBy(track => byId.TryGetValue(track.Id, out var structured) ? structured.Position : int.MaxValue)
            .ToArray();
    }

    private static AmazonCollection MergeStructuredAlbumCollection(AmazonCollection collection, AmazonStructuredAlbum album)
        => collection with
        {
            Title = string.IsNullOrWhiteSpace(collection.Title) || collection.Title == "Amazon Music" ? album.Title : collection.Title,
            Artist = string.IsNullOrWhiteSpace(collection.Artist) || collection.Artist == "Amazon Music" ? album.Artist : collection.Artist,
            CoverUrl = string.IsNullOrWhiteSpace(collection.CoverUrl) ? album.CoverUrl : collection.CoverUrl
        };

    private static IReadOnlyList<AmazonCatalogItem> MergeCollectionIntoTracks(
        IReadOnlyList<AmazonCatalogItem> tracks,
        AmazonCollection collection)
    {
        if (tracks.Count == 0)
        {
            return tracks;
        }

        return tracks
            .Select(track => track.Type != "track"
                ? track
                : track with
                {
                    Artist = string.IsNullOrWhiteSpace(track.Artist) ? collection.Artist : track.Artist,
                    Album = string.IsNullOrWhiteSpace(track.Album) || string.Equals(track.Album, track.Artist, StringComparison.OrdinalIgnoreCase)
                        ? collection.Title
                        : track.Album,
                    CoverUrl = string.IsNullOrWhiteSpace(track.CoverUrl) ? collection.CoverUrl : track.CoverUrl
                })
            .ToArray();
    }

    private static string ReadAlbumTitleFromContextMenu(JsonElement node)
    {
        if (!node.TryGetProperty("contextMenu", out var contextMenu)
            || !contextMenu.TryGetProperty("options", out var options)
            || options.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var option in options.EnumerateArray())
        {
            var text = FirstText(option, "text");
            if (!text.Contains("album", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = FirstText(option, "headerText", "primaryText", "title", "name");
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            foreach (var child in WalkObjects(option))
            {
                title = FirstText(child, "headerText", "primaryText", "title", "name");
                if (!string.IsNullOrWhiteSpace(title))
                {
                    return title;
                }
            }
        }

        return string.Empty;
    }

    private static string ReadHeaderImage(JsonElement root, string targetId)
    {
        var firstHeaderImage = string.Empty;
        var matchingPlaceholder = string.Empty;
        foreach (var node in WalkObjects(root))
        {
            var headerImage = NormalizeAmazonImageUrl(FirstText(node, "headerImage", "backgroundImage"));
            if (string.IsNullOrWhiteSpace(headerImage))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(firstHeaderImage))
            {
                firstHeaderImage = headerImage;
            }

            var explicitId = ExtractAsin(FirstText(node, "asin", "id", "albumAsin", "playlistAsin", "catalogId"));
            var parsed = ParseCatalogDeeplink(ReadDeepLink(node));
            if (string.Equals(explicitId, targetId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(parsed.Id, targetId, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsAmazonPlaceholderImage(headerImage))
                {
                    return headerImage;
                }

                matchingPlaceholder = headerImage;
            }
        }

        return !string.IsNullOrWhiteSpace(matchingPlaceholder)
            ? matchingPlaceholder
            : firstHeaderImage;
    }

    private static bool IsAmazonPlaceholderImage(string value)
        => value.Contains("placeholder_", StringComparison.OrdinalIgnoreCase)
           || value.Contains("placeholder.", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<JsonElement> WalkObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
            {
                foreach (var child in WalkObjects(property.Value))
                {
                    yield return child;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var child in WalkObjects(item))
                {
                    yield return child;
                }
            }
        }
    }

    private static IEnumerable<JsonElement> ReadJsonLdObjects(JsonElement root)
    {
        foreach (var node in WalkObjects(root))
        {
            if (!string.Equals(FirstText(node, "interface"), "Web.PageInterface.v1_0.SEOHeadLDJSONScriptElement", StringComparison.Ordinal)
                || !node.TryGetProperty("innerHTML", out var inner)
                || inner.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var json = inner.GetString();
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            using var document = JsonDocument.Parse(json);
            yield return document.RootElement.Clone();
        }
    }

    private static string ReadStructuredArtistName(JsonElement script)
    {
        if (!script.TryGetProperty("byArtist", out var artistElement))
        {
            return string.Empty;
        }

        if (artistElement.ValueKind == JsonValueKind.Array)
        {
            var names = artistElement.EnumerateArray()
                .Select(static item => CleanAmazonDisplayText(FirstText(item, "name")))
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return string.Join(", ", names);
        }

        return CleanAmazonDisplayText(FirstText(artistElement, "name"));
    }

    private static IEnumerable<AmazonCatalogItem> FilterByType(
        IEnumerable<AmazonCatalogItem> items,
        string itemType,
        string requestedType,
        string query,
        int limit)
    {
        var normalized = NormalizeType(requestedType);
        if (normalized != "all" && normalized != itemType)
        {
            return [];
        }

        return items
            .Where(item => item.Type == itemType)
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(item => IsExactSearchMatch(item, query) ? 0 : 1)
            .Take(Math.Clamp(limit, 1, 50));
    }

    private static bool IsExactSearchMatch(AmazonCatalogItem item, string query)
        => NormalizeSearchText(item.Title) == NormalizeSearchText(query);

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", " ", RegexOptions.None, RegexTimeout).Trim();
    }

    private static AmazonCollection ResolveCollection(
        IReadOnlyList<AmazonCatalogItem> items,
        string id,
        string type,
        string url)
    {
        var collection = items.FirstOrDefault(item => item.Type == type)
            ?? items.FirstOrDefault(item => item.Type != "track")
            ?? items.FirstOrDefault();
        return new AmazonCollection(
            Id: id,
            Type: type,
            Title: collection?.Title ?? "Amazon Music",
            Artist: collection?.Artist ?? "Amazon Music",
            Url: url,
            CoverUrl: collection?.CoverUrl ?? string.Empty);
    }

    private static string NormalizeHost(string? value)
    {
        var host = (value ?? DefaultHost).Trim();
        host = host.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim('/');
        return string.IsNullOrWhiteSpace(host) ? DefaultHost : host;
    }

    private static string NormalizeType(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "albums" => "album",
            "artists" => "artist",
            "playlists" => "playlist",
            "tracks" or "song" or "songs" => "track",
            "" => "track",
            var other => other
        };

    private static string? NormalizeAmazonUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Host.Contains("amazon.", StringComparison.OrdinalIgnoreCase) ? uri.ToString() : null;
    }

    private static string? BuildCatalogUrl(string? id, string? type)
    {
        var asin = ExtractAsin(id);
        if (string.IsNullOrWhiteSpace(asin))
        {
            return null;
        }

        return NormalizeType(type) switch
        {
            "album" => $"https://{DefaultHost}/albums/{asin}",
            "artist" => $"https://{DefaultHost}/artists/{asin}",
            "playlist" => $"https://{DefaultHost}/playlists/{asin}",
            _ => $"https://{DefaultHost}/tracks/{asin}"
        };
    }

    private static string? ExtractAsin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, "(B[0-9A-Z]{9})", RegexOptions.IgnoreCase, RegexTimeout);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static (string Type, string Id) ParseCatalogDeeplink(string? value)
    {
        var id = ExtractAsin(value);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(value))
        {
            return (string.Empty, string.Empty);
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
        {
            var trackId = ExtractQueryAsin(absoluteUri.Query, "trackAsin")
                ?? ExtractQueryAsin(absoluteUri.Query, "trackasin");
            if (!string.IsNullOrWhiteSpace(trackId))
            {
                return ("track", trackId);
            }
        }
        else if (Uri.TryCreate($"https://{DefaultHost}{(value.StartsWith('/') ? value : "/" + value)}", UriKind.Absolute, out var relativeUri))
        {
            var trackId = ExtractQueryAsin(relativeUri.Query, "trackAsin")
                ?? ExtractQueryAsin(relativeUri.Query, "trackasin");
            if (!string.IsNullOrWhiteSpace(trackId))
            {
                return ("track", trackId);
            }
        }

        var lower = value.ToLowerInvariant();
        if (lower.Contains("/albums/")) return ("album", id);
        if (lower.Contains("/artists/")) return ("artist", id);
        if (lower.Contains("/playlists/")) return ("playlist", id);
        return ("track", id);
    }

    private static string? ExtractQueryAsin(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var trimmed = query.TrimStart('?');
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length != 2 || !string.Equals(pieces[0], key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return ExtractAsin(Uri.UnescapeDataString(pieces[1]));
        }

        return null;
    }

    private static string? ReadDeepLink(JsonElement node)
    {
        if (node.TryGetProperty("primaryTextLink", out var primaryTextLink))
        {
            var deeplink = FirstText(primaryTextLink, "deeplink", "url", "href");
            if (!string.IsNullOrWhiteSpace(deeplink))
            {
                return deeplink;
            }
        }

        if (node.TryGetProperty("primaryLink", out var primaryLink))
        {
            return FirstText(primaryLink, "deeplink", "url", "href");
        }

        return FirstText(node, "deeplink", "url", "href");
    }

    private static string FirstText(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            if (!node.TryGetProperty(name, out var value))
            {
                continue;
            }

            var text = ReadText(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static string ReadText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "text", "displayText", "title", "label", "value" })
            {
                if (value.TryGetProperty(key, out var nested))
                {
                    var text = ReadText(nested);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
        }

        return string.Empty;
    }

    private static string FirstImage(JsonElement node)
    {
        foreach (var property in node.EnumerateObject())
        {
            if (!IsImageProperty(property.Name))
            {
                continue;
            }

            var image = ReadImage(property.Value);
            if (!string.IsNullOrWhiteSpace(image))
            {
                return image;
            }
        }

        return string.Empty;
    }

    private static bool IsImageProperty(string name)
        => name.Contains("image", StringComparison.OrdinalIgnoreCase)
           || name.Equals("art", StringComparison.OrdinalIgnoreCase)
           || name.Equals("artwork", StringComparison.OrdinalIgnoreCase)
           || name.Equals("cover", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("Art", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("Artwork", StringComparison.OrdinalIgnoreCase);

    private static string ReadImage(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "url", "image", "src" })
            {
                if (value.TryGetProperty(key, out var nested))
                {
                    var text = ReadText(nested);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var image = ReadImage(item);
                if (!string.IsNullOrWhiteSpace(image))
                {
                    return image;
                }
            }
        }

        return string.Empty;
    }

    private static string CleanAmazonDisplayText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = Regex.Replace(value.Trim(), "\\s+on\\s+Amazon\\s+Music(?:\\s+Unlimited)?\\s*$", string.Empty, RegexOptions.IgnoreCase, RegexTimeout);
        cleaned = Regex.Replace(cleaned, "\\s+Music\\s+Unlimited\\s*$", string.Empty, RegexOptions.IgnoreCase, RegexTimeout);
        return cleaned.Trim();
    }

    private static string CleanAmazonCollectionTitle(string? value)
    {
        var cleaned = CleanAmazonDisplayText(value);
        var playMatch = Regex.Match(cleaned, "^Play\\s+(.+?)\\s+by\\s+.+$", RegexOptions.IgnoreCase, RegexTimeout);
        return playMatch.Success ? playMatch.Groups[1].Value.Trim() : cleaned;
    }

    private static AmazonCatalogItem NormalizeAmazonTopSongItem(AmazonCatalogItem item)
    {
        var title = Regex.Replace(item.Title, "^\\s*\\d+\\.\\s+", string.Empty, RegexOptions.None, RegexTimeout).Trim();
        return string.Equals(title, item.Title, StringComparison.Ordinal) ? item : item with { Title = title };
    }

    private static string NormalizeAmazonImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "data"))
        {
            return trimmed;
        }

        return string.Empty;
    }

    private static string? ReadString(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("token", out var token)
            && token.ValueKind == JsonValueKind.String)
        {
            return token.GetString();
        }

        return null;
    }

    private static string? ReadNestedString(JsonElement node, string propertyName, string nestedName)
    {
        if (!node.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(nestedName, out var nested)
            || nested.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return nested.GetString();
    }

    private static string ToAmazonDeeplink(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.IsNullOrWhiteSpace(uri.Query) ? uri.AbsolutePath : $"{uri.AbsolutePath}{uri.Query}";
        }

        return url.StartsWith('/') ? url : $"/{url.TrimStart('/')}";
    }

    private static string ResolveCurrency(string host)
    {
        var normalized = host.ToLowerInvariant();
        if (normalized.Contains(".co.jp", StringComparison.Ordinal)) return "JPY";
        if (normalized.Contains(".co.uk", StringComparison.Ordinal)) return "GBP";
        if (normalized.Contains(".de", StringComparison.Ordinal)
            || normalized.Contains(".fr", StringComparison.Ordinal)
            || normalized.Contains(".it", StringComparison.Ordinal)
            || normalized.Contains(".es", StringComparison.Ordinal)) return "EUR";
        if (normalized.Contains(".in", StringComparison.Ordinal)) return "INR";
        if (normalized.Contains(".com.br", StringComparison.Ordinal)) return "BRL";
        if (normalized.Contains(".com.mx", StringComparison.Ordinal)) return "MXN";
        if (normalized.Contains(".com.au", StringComparison.Ordinal)) return "AUD";
        return "USD";
    }

    private static int? ReadInt(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
        {
            return number;
        }

        return null;
    }

    private static int? ParseIsoDurationMs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value.Trim(), "^PT(?:(\\d+)H)?(?:(\\d+)M)?(?:(\\d+)S)?$", RegexOptions.IgnoreCase, RegexTimeout);
        if (!match.Success)
        {
            return null;
        }

        var hours = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0;
        var minutes = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        var seconds = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        return (int)TimeSpan.FromSeconds(hours * 3600 + minutes * 60 + seconds).TotalMilliseconds;
    }

    private static int? ParseClockDurationMs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3 || parts.Any(part => !int.TryParse(part, out _)))
        {
            return null;
        }

        var numbers = parts.Select(int.Parse).ToArray();
        var seconds = numbers.Length == 3
            ? numbers[0] * 3600 + numbers[1] * 60 + numbers[2]
            : numbers[0] * 60 + numbers[1];
        return (int)TimeSpan.FromSeconds(seconds).TotalMilliseconds;
    }

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private sealed record AmazonSession(
        HttpClient Client,
        string Host,
        string Locale,
        string SessionId,
        string DeviceId,
        string Csrf,
        string? CsrfTimestamp,
        string? CsrfNonce,
        string? Version,
        string? Cookie);

    private sealed record AmazonStructuredAlbum(
        string Id,
        string Title,
        string Artist,
        string CoverUrl,
        IReadOnlyList<AmazonStructuredTrack> Tracks);

    private sealed record AmazonStructuredTrack(
        string Id,
        string Title,
        int Position,
        int? DurationMs);
}

public sealed record AmazonSearchPayload(
    IReadOnlyList<AmazonCatalogItem> Tracks,
    IReadOnlyList<AmazonCatalogItem> Albums,
    IReadOnlyList<AmazonCatalogItem> Artists,
    IReadOnlyList<AmazonCatalogItem> Playlists)
{
    public static AmazonSearchPayload Empty { get; } = new([], [], [], []);
}

public sealed record AmazonTracklistPayload(AmazonCollection Collection, IReadOnlyList<AmazonTrack> Tracks);

public sealed record AmazonArtistPagePayload(
    AmazonCatalogItem Artist,
    IReadOnlyList<AmazonCatalogItem> Releases,
    IReadOnlyList<AmazonCatalogItem> TopTracks,
    IReadOnlyList<AmazonCatalogItem> Related,
    IReadOnlyList<AmazonCatalogItem> AppearsOn);

public sealed record AmazonSectionCatalogItem(string Section, AmazonCatalogItem Item);

public sealed record AmazonCollection(string Id, string Type, string Title, string Artist, string Url, string CoverUrl);

public sealed record AmazonTrack(
    string Id,
    string Title,
    string Artist,
    string Album,
    string SourceUrl,
    string Cover,
    int DurationMs,
    int Position,
    string Isrc,
    string AmazonId);

public sealed record AmazonCatalogItem(
    string Id,
    string Type,
    string Title,
    string Artist,
    string Album,
    string Url,
    string CoverUrl,
    int? DurationMs,
    string Isrc)
{
    public AmazonTrack ToTrack(int position)
        => new(
            Id,
            Title,
            Artist,
            string.IsNullOrWhiteSpace(Album) ? Title : Album,
            Url,
            CoverUrl,
            DurationMs ?? 0,
            position,
            Isrc,
            Id);
}
