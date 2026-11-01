using System;
using Microsoft.Extensions.Configuration;
using System.Text.Json.Nodes;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;

namespace DeezSpoTag.Integrations.YouTube;

/// <summary>One song YouTube Music's search returned, in the shape a matcher can score.</summary>
public sealed record YouTubeMusicSearchResult(
    string VideoId,
    string Title,
    string? Artist,
    string? Album,
    int? DurationSeconds,
    string? ConversionQuery = null);

/// <summary>
/// Searches YouTube Music through the same Innertube endpoint the YouTube Music web app calls.
/// <para>
/// This exists because the alternative is <c>search.list</c> on the YouTube Data API, which costs
/// 100 units against a 10,000-unit daily budget for the WHOLE project - roughly a hundred resolved
/// tracks a day across every destination combined. The web app's own search costs nothing: a plain
/// POST to <c>music.youtube.com/youtubei/v1/search</c> as the public WEB_REMIX client, with no
/// account and no OAuth token. The contract below is the one <c>ytmusicapi</c> implements -
/// endpoint, headers, context, the base64 <c>params</c> filter, and the response walk - because
/// that library is the working reference for this endpoint and a divergence from it is a query
/// that quietly returns nothing.
/// </para>
/// <para>
/// It is a search client and nothing else. It writes nothing, reads no user data and carries no
/// credentials, so it can never be confused with the YouTube Data API connection a playlist write
/// needs.
/// </para>
/// </summary>
public sealed record YouTubeNonMusicSegment(string Uuid, double StartSeconds, double EndSeconds);

public sealed class YouTubeMusicSearchClient
{
    private const string VideoIdPattern = "^[A-Za-z0-9_-]{11}$";
    private const string MusicClientName = "WEB_REMIX";
    private const string ClientVersionDateFormat = "yyyyMMdd";
    private const string ClientVersionSuffix = ".01.00";
    private const string JsonContentType = "application/json";
    private const string OriginHeader = "Origin";
    private const string UserAgentHeader = "User-Agent";
    private const string TitleProperty = "title";
    private const string BrowseProperty = "browse";
    private const string ContentsProperty = "contents";
    private const string PlaylistType = "playlist";
    private const string AlbumType = "album";
    private const string VideoIdProperty = "videoId";
    private const string ItemsProperty = "items";
    private const string VideoType = "video";
    private const string TrackType = "track";
    private const string BrowseIdProperty = "browseId";
    private const string SubtitleProperty = "subtitle";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly string Origin = new UriBuilder(Uri.UriSchemeHttps, "music.youtube.com").Uri.GetLeftPart(UriPartial.Authority);
    private readonly string _origin;
    private readonly string _searchUrl;
    private readonly string _sponsorBlockUrl;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:88.0) Gecko/20100101 Firefox/88.0";

    /// <summary>
    /// The base64 filter that scopes a search to songs, assembled from ytmusicapi's
    /// <c>get_search_params</c>: the shared <c>EgWKAQ</c> prefix, <c>II</c> for songs, and the
    /// default (spelling-corrected) suffix. Sending no params at all returns every result type,
    /// which buries the handful of candidates this client wants under playlists and videos.
    /// </summary>
    private const string SongsParams = "EgWKAQIIAWoMEA4QChADEAQQCRAF";

    /// <summary>The same filter for videos. ytmusicapi's caller falls back to it when no song scores.</summary>
    private const string VideosParams = "EgWKAQIQAWoMEA4QChADEAQQCRAF";

    /// <summary>
    /// Bounds on how far a response is walked. Both are far past anything a real body needs; they
    /// exist so a reshaped response is skipped rather than trusted as "there were no results".
    /// </summary>
    private const int MaxNavigationDepth = 6;

    private const int MaxContinuationPages = 4;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly Regex DurationRun = new(@"^(\d+:)*\d+:\d+$", RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex YearRun = new(@"^\d{4}$", RegexOptions.Compiled, RegexTimeout);

    /// <summary>A leading views word, a locale's colon, or a bidi mark, up to the first digit.</summary>
    private static readonly Regex ViewsPrefix =
        new("^\\D*?[\\s:\\uFF1A\\u200E-\\u200F\\u202A-\\u202E]", RegexOptions.Compiled, RegexTimeout);

    private readonly HttpClient _httpClient;
    private readonly ILogger<YouTubeMusicSearchClient>? _logger;

    private readonly IMemoryCache? _metadataCache;

    public YouTubeMusicSearchClient(HttpClient httpClient, ILogger<YouTubeMusicSearchClient>? logger = null, IMemoryCache? metadataCache = null, IConfiguration? configuration = null)
    {
        _origin = configuration?["YouTube:MusicOrigin"] ?? Origin;
        _searchUrl = configuration?["YouTube:MusicSearchUrl"] ?? _origin + "/youtubei/v1/search?alt=json&key=AIzaSyC9XL3ZjWddXya6X74dJoCTL-WEYFDNX30";
        _sponsorBlockUrl = configuration?["YouTube:SponsorBlockUrl"] ?? new UriBuilder(Uri.UriSchemeHttps, "sponsor.ajay.app") { Path = "/api/skipSegments" }.Uri.AbsoluteUri;
        _metadataCache = metadataCache;
        _httpClient = httpClient;
        _logger = logger;
    }

    public static string? GetVideoId(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var id = uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsolutePath.Trim('/') : ReadWatchQueryId(uri);
        return id is not null && Regex.IsMatch(id, VideoIdPattern, RegexOptions.CultureInvariant, RegexTimeout) ? id : null;
    }

    private static string? ReadWatchQueryId(Uri uri)
    {
        if (!new[] { "music.youtube.com", "www.youtube.com", "youtube.com" }.Contains(uri.Host.ToLowerInvariant()) || uri.AbsolutePath != "/watch") return null;
        string? id = null;
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || pair[0] != "v") continue;
            if (id is not null) return null;
            id = Uri.UnescapeDataString(pair[1]);
        }
        return id;
    }

    public async Task<YouTubeMusicSearchResult> GetQueueSongMetadataAsync(string videoId, string? country, string language, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(videoId, VideoIdPattern, RegexOptions.CultureInvariant, RegexTimeout)) throw new ArgumentException("Invalid YouTube video ID.");
        if ((country is not null && !Regex.IsMatch(country, "^[A-Z]{2}$", RegexOptions.CultureInvariant, RegexTimeout))
            || !Regex.IsMatch(language, "^[a-zA-Z]{2,3}(?:-[a-zA-Z]{2,4})?$", RegexOptions.CultureInvariant, RegexTimeout)) throw new ArgumentException("Invalid YouTube Music market or language.");
        var key = $"youtube:queue-metadata:v1:{country}:{language}:{videoId}";
        if (_metadataCache?.TryGetValue<YouTubeMusicSearchResult>(key, out var cached) == true && cached is not null) return cached;
        using var request = new HttpRequestMessage(HttpMethod.Post, _origin + "/youtubei/v1/music/get_queue?alt=json");
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = MusicClientName, clientVersion = "1." + DateTime.UtcNow.ToString(ClientVersionDateFormat, CultureInfo.InvariantCulture) + ClientVersionSuffix, gl = country, hl = language } },
            videoIds = new[] { videoId }
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }), Encoding.UTF8, JsonContentType);
        request.Headers.TryAddWithoutValidation(OriginHeader, _origin);
        request.Headers.TryAddWithoutValidation(UserAgentHeader, UserAgent);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var result = ParseQueueSongMetadata(document.RootElement, videoId);
        _metadataCache?.Set(key, result, TimeSpan.FromHours(4));
        return result;
    }

    public static YouTubeMusicSearchResult ParseQueueSongMetadata(JsonElement root, string videoId)
    {
        var renderer = BrowseNodes(root, "playlistPanelVideoRenderer").FirstOrDefault(row => row.TryGetProperty(VideoIdProperty, out var id) && id.GetString() == videoId);
        if (renderer.ValueKind != JsonValueKind.Object) throw new JsonException("YouTube song identity unavailable.");
        var runs = ReadRunList(renderer, "longBylineText");
        var info = runs is null ? null : ParseMetadataRuns(runs);
        return new YouTubeMusicSearchResult(videoId, renderer.TryGetProperty(TitleProperty, out var title) ? BrowseText(title) : "", JoinNames(runs?.Where(run => run.TryGetValue(BrowseProperty, out var browse) && browse.StartsWith("UC", StringComparison.Ordinal)).Select(run => run["text"]).ToList()), info?.Album, renderer.TryGetProperty("lengthText", out var length) ? ParseDurationSeconds(BrowseText(length)) : null);
    }

    // Adapted from Vivi Music 6.0.8 SponsorBlockApi.kt, vivimusic Project (C) 2026,
    // GPL-3.0. Non-music timestamps only; never applied to downloaded audio.
    public async Task<IReadOnlyList<YouTubeNonMusicSegment>> GetNonMusicSegmentsAsync(string videoId, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(videoId, VideoIdPattern, RegexOptions.CultureInvariant, RegexTimeout)) throw new ArgumentException("Invalid YouTube video ID.");
        var key = "youtube:non-music:v1:" + videoId;
        if (_metadataCache?.TryGetValue<IReadOnlyList<YouTubeNonMusicSegment>>(key, out var cached) == true && cached is not null) return cached;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await _httpClient.GetAsync(_sponsorBlockUrl + "?videoID=" + videoId + "&categories=" + Uri.EscapeDataString("[\"music_offtopic\"]"), timeout.Token);
        var segments = new List<YouTubeNonMusicSegment>();
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object
                    || !row.TryGetProperty("category", out var category) || category.ValueKind != JsonValueKind.String || category.GetString() != "music_offtopic"
                    || !row.TryGetProperty("actionType", out var action) || action.ValueKind != JsonValueKind.String || action.GetString() != "skip"
                    || !row.TryGetProperty("segment", out var bounds) || bounds.ValueKind != JsonValueKind.Array || bounds.GetArrayLength() != 2
                    || bounds[0].ValueKind != JsonValueKind.Number || !bounds[0].TryGetDouble(out var start)
                    || bounds[1].ValueKind != JsonValueKind.Number || !bounds[1].TryGetDouble(out var end)
                    || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start
                    || !row.TryGetProperty("UUID", out var uuid) || uuid.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(uuid.GetString())) continue;
                segments.Add(new(uuid.GetString()!, start, end));
            }
        }
        IReadOnlyList<YouTubeNonMusicSegment> result = segments.OrderBy(segment => segment.StartSeconds).ToArray();
        _metadataCache?.Set(key, result, TimeSpan.FromHours(1));
        return result;
    }

    public async Task<YouTubeMusicSearchResult> GetSongMetadataAsync(string videoId, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(videoId, VideoIdPattern, RegexOptions.CultureInvariant, RegexTimeout)) throw new ArgumentException("Invalid YouTube video ID.");
        var cacheKey = "youtube:recording-metadata:queue-v3:" + videoId;
        if (_metadataCache?.TryGetValue<YouTubeMusicSearchResult>(cacheKey, out var cached) == true && cached is not null) return cached;
        using var request = new HttpRequestMessage(HttpMethod.Post, _origin + "/youtubei/v1/player?alt=json");
        request.Content = new StringContent(JsonSerializer.Serialize(new { context = new { client = new { clientName = "WEB", clientVersion = "2." + DateTime.UtcNow.ToString(ClientVersionDateFormat, CultureInfo.InvariantCulture) + ClientVersionSuffix } }, videoId }), Encoding.UTF8, JsonContentType);
        request.Headers.TryAddWithoutValidation(UserAgentHeader, UserAgent);
        request.Headers.TryAddWithoutValidation(OriginHeader, _origin);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var hasDetails = document.RootElement.TryGetProperty("videoDetails", out var details);
        if (hasDetails && (details.ValueKind != JsonValueKind.Object
            || !details.TryGetProperty(VideoIdProperty, out var returnedId) || returnedId.GetString() != videoId))
            throw new JsonException("YouTube song identity unavailable.");
        var seconds = hasDetails && details.TryGetProperty("lengthSeconds", out var length) && int.TryParse(length.GetString(), out var duration) ? duration : (int?)null;
        // Exact Music queue credits identify the recording; video descriptions may
        // contain production staff rather than the recording artist.
        var queue = await GetQueueSongMetadataAsync(videoId, null, "en", cancellationToken);
        if (seconds is > 0 && queue.DurationSeconds is > 0 && Math.Abs(seconds.Value - queue.DurationSeconds.Value) > 2)
            throw new InvalidOperationException("YouTube recording duration could not be verified.");
        var title = queue.Title;
        var artist = queue.Artist;
        seconds = queue.DurationSeconds is > 0 ? queue.DurationSeconds : seconds;
        var album = queue.Album;
        var description = title + " · " + artist;
        var metadata = new YouTubeMusicSearchResult(videoId, title, artist, album, seconds, BuildConversionQuery(title, description));
        if (!string.IsNullOrWhiteSpace(artist) && seconds is > 0) _metadataCache?.Set(cacheKey, metadata, TimeSpan.FromHours(4));
        return metadata;
    }

    /* Ported from idonthavespotify 1.9.0: src/parsers/youtube.ts, utils/query.ts,
       utils/compare.ts (string-similarity Dice coefficient).
       MIT License — Copyright (c) 2024 Juan Rodriguez Donado
       Permission is hereby granted, free of charge, to any person obtaining a copy
       of this software and associated documentation files (the "Software"), to deal
       in the Software without restriction, including without limitation the rights
       to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
       copies of the Software, and to permit persons to whom the Software is
       furnished to do so, subject to the following conditions:
       The above copyright notice and this permission notice shall be included in all
       copies or substantial portions of the Software.
       THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
       IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
       FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
       AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
       LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
       OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
       SOFTWARE. */
    public static string? ReadConversionArtist(string? description)
    {
        var match = Regex.Match(description ?? "", @"(?:·|&)\s*([^·&℗]+)", RegexOptions.None, RegexTimeout);
        return match.Success ? match.Groups[1].Value.Trim().Split(new[] { '\n', '\r' }, StringSplitOptions.None)[0].Trim() : null;
    }

    public static string BuildConversionQuery(string title, string? description)
    {
        // Match the supplied YouTube parser's ASCII title processing and first credited artist.
        var processed = Regex.Replace(title, @"[^A-Za-z0-9_\s]", " ", RegexOptions.None, RegexTimeout);
        var credit = Regex.Match(description ?? "", @"(?:·|&)\s*([^·&℗]+)", RegexOptions.None, RegexTimeout);
        var artist = credit.Success ? credit.Groups[1].Value.Trim() : null;
        return CleanConversionQuery(processed + (artist is null ? "" : " " + artist));
    }

    public static string CleanConversionQuery(string query)
    {
        query = Regex.Replace(query, "[\u201E\u201C\u201D\u00AB\u00BB\u2039\u203A\u300E\u300F\u300C\u300D]", "", RegexOptions.None, RegexTimeout);
        query = Regex.Replace(query, "[\u2018\u2019\u201A\u201B]", "'", RegexOptions.None, RegexTimeout).Replace('·', ' ');
        query = Regex.Replace(query, "[\u2013\u2014\u2015]", "-", RegexOptions.None, RegexTimeout);
        query = Regex.Replace(query, "[\u200B-\u200D\uFEFF]", "", RegexOptions.None, RegexTimeout);
        return Regex.Replace(query, @"\s+", " ", RegexOptions.None, RegexTimeout).Trim();
    }

    public static double ConversionScore(string title, string? artist, string query)
    {
        var left = Regex.Replace(CleanConversionQuery(title + (string.IsNullOrWhiteSpace(artist) ? "" : " " + artist)).ToLowerInvariant(), @"\s+", "", RegexOptions.None, RegexTimeout);
        var right = Regex.Replace(CleanConversionQuery(query).ToLowerInvariant(), @"\s+", "", RegexOptions.None, RegexTimeout);
        if (left == right) return 1;
        if (left.Length < 2 || right.Length < 2) return 0;
        var pairs = new Dictionary<string, int>();
        for (var i = 0; i < left.Length - 1; i++) { var pair = left.Substring(i, 2); pairs[pair] = pairs.GetValueOrDefault(pair) + 1; }
        var intersection = 0;
        for (var i = 0; i < right.Length - 1; i++)
        {
            var pair = right.Substring(i, 2);
            if (pairs.GetValueOrDefault(pair) > 0) { pairs[pair]--; intersection++; }
        }
        return 2d * intersection / (left.Length + right.Length - 2);
    }

    // The reference returns the highest similarity link. Reject tied distinct IDs instead of
    // trusting its isVerified label; callers validate the selected recording before accepting it.
    public static T? ConvertCandidate<T>(IEnumerable<T> candidates, string query, Func<T, string> title, Func<T, string?> artist, Func<T, string> id) where T : class
    {
        var ranked = candidates.Select(item => (Item: item, Score: ConversionScore(title(item), artist(item), query)))
            .OrderByDescending(item => item.Score).ToList();
        if (ranked.Count == 0 || ranked[0].Score < 0.3) return null;
        if (ranked.Where(item => Math.Abs(item.Score - ranked[0].Score) < 0.000001).Select(item => id(item.Item)).Distinct().Count() > 1) return null;
        return ranked[0].Item;
    }

    public async Task<JsonDocument> BrowseAsync(string browseId, string country, string language, string? parameters, CancellationToken cancellationToken, string? chartSelection = null)
    {
        if (chartSelection is not null && (browseId != "FEmusic_charts" || !Regex.IsMatch(chartSelection, "^[A-Z]{2}$", RegexOptions.CultureInvariant, RegexTimeout))) throw new ArgumentException("Invalid chart selection.");
        if (!Regex.IsMatch(country, "^[A-Z]{2}$", RegexOptions.CultureInvariant, RegexTimeout)
            || !Regex.IsMatch(browseId, "^(FEmusic_[a-z_]+|VL[A-Za-z0-9_-]+|MPRE[A-Za-z0-9_-]+)$", RegexOptions.CultureInvariant, RegexTimeout)
            || (parameters is not null && (parameters.Length > 4096 || !Regex.IsMatch(parameters, "^[A-Za-z0-9_%=+-]*$", RegexOptions.CultureInvariant, RegexTimeout))))
            throw new ArgumentException("Unsupported YouTube Music country or browse destination.");
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["context"] = new System.Text.Json.Nodes.JsonObject
            {
                ["client"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["clientName"] = MusicClientName,
                    ["clientVersion"] = "1." + DateTime.UtcNow.ToString(ClientVersionDateFormat, CultureInfo.InvariantCulture) + ClientVersionSuffix,
                    ["gl"] = country,
                    ["hl"] = language
                }
            },
            [BrowseIdProperty] = browseId
        };
        if (!string.IsNullOrWhiteSpace(parameters)) body["params"] = parameters;
        if (browseId == "FEmusic_charts") body["formData"] = new System.Text.Json.Nodes.JsonObject { ["selectedValues"] = new System.Text.Json.Nodes.JsonArray(chartSelection ?? country) };
        using var request = new HttpRequestMessage(HttpMethod.Post, _origin + "/youtubei/v1/browse?alt=json") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, JsonContentType) };
        request.Headers.TryAddWithoutValidation(OriginHeader, _origin);
        request.Headers.TryAddWithoutValidation(UserAgentHeader, UserAgent);
        request.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAI");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        try
        {
            if (!document.RootElement.TryGetProperty(ContentsProperty, out _))
            {
                throw new JsonException("YouTube Music browse content unavailable.");
            }

            if (browseId == "FEmusic_charts") ValidateChartCountry(document.RootElement, chartSelection ?? country);

            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void ValidateChartCountry(JsonElement root, string expectedCountry)
    {
        var selected = BrowseNodes(root, "musicMultiSelectMenuItemRenderer")
            .FirstOrDefault(item => !item.TryGetProperty("selectedCommand", out _) && item.TryGetProperty("formItemEntityKey", out _));
        var selectedCountry = "";
        if (selected.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var entity = Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(selected.GetProperty("formItemEntityKey").GetString()!)));
                selectedCountry = Regex.Match(entity, @"explore_charts_country_menu_[0-9]+([A-Z]{2}) ", RegexOptions.CultureInvariant, RegexTimeout).Groups[1].Value;
            }
            catch (FormatException)
            {
                // A malformed entity key just means the country cannot be read from the menu
                // label; selectedCountry stays empty and the comparison below reports it.
            }
        }
        if (selectedCountry != expectedCountry)
        {
            throw new JsonException("YouTube Music charts are unavailable for the requested country.");
        }
    }

    public static string NormalizeCollectionId(string id, string type)
    {
        if (type is not (PlaylistType or AlbumType)) throw new ArgumentException("Unsupported YouTube Music collection type.");
        id = id.Trim();
        if (type == PlaylistType && !id.StartsWith("VL", StringComparison.Ordinal)) id = "VL" + id;
        if (!Regex.IsMatch(id, type == AlbumType ? "^MPRE[A-Za-z0-9_-]+$" : "^VL[A-Za-z0-9_-]{10,}$", RegexOptions.CultureInvariant, RegexTimeout))
            throw new ArgumentException("Invalid YouTube Music collection ID.");
        return id;
    }

    public async Task<System.Text.Json.Nodes.JsonObject> GetCollectionAsync(string id, string type, string country, string language, CancellationToken cancellationToken)
    {
        id = NormalizeCollectionId(id, type);
        using var first = await BrowseAsync(id, country, language, null, cancellationToken);
        var root = first.RootElement;
        var header = BrowseNodes(root, "musicResponsiveHeaderRenderer").Concat(BrowseNodes(root, "musicDetailHeaderRenderer")).FirstOrDefault();
        var title = header.ValueKind == JsonValueKind.Object && header.TryGetProperty(TitleProperty, out var titleNode) ? BrowseText(titleNode) : "";
        if (string.IsNullOrWhiteSpace(title)) throw new JsonException("YouTube Music collection header unavailable.");
        var shelves = BrowseNodes(root, "musicPlaylistShelfRenderer").Concat(BrowseNodes(root, "musicShelfRenderer")).ToArray();
        if (shelves.Length == 0) throw new JsonException("YouTube Music collection tracks unavailable.");
        var rows = new System.Text.Json.Nodes.JsonArray();
        var coverNode = BrowseNodes(header, "thumbnails").FirstOrDefault();
        var cover = coverNode.ValueKind == JsonValueKind.Array && coverNode.GetArrayLength() > 0 ? coverNode[coverNode.GetArrayLength() - 1].GetProperty("url").GetString() : "";
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;
        foreach (var shelf in shelves) continuation = AppendCollectionRows(shelf, rows, cover, type, title);
        while (!string.IsNullOrWhiteSpace(continuation))
        {
            ValidateContinuation(tokens, continuation);
            var body = new System.Text.Json.Nodes.JsonObject
            {
                ["context"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["client"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["clientName"] = MusicClientName,
                        ["clientVersion"] = "1." + DateTime.UtcNow.ToString(ClientVersionDateFormat, CultureInfo.InvariantCulture) + ClientVersionSuffix,
                        ["gl"] = country,
                        ["hl"] = language
                    }
                },
                ["continuation"] = continuation
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, _origin + "/youtubei/v1/browse?alt=json") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, JsonContentType) };
            request.Headers.TryAddWithoutValidation(OriginHeader, _origin); request.Headers.TryAddWithoutValidation(UserAgentHeader, UserAgent); request.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAI");
            using var response = await _httpClient.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode();
            using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var contents = BrowseNodes(page.RootElement, "musicPlaylistShelfContinuation").Concat(BrowseNodes(page.RootElement, "musicShelfContinuation")).Concat(BrowseNodes(page.RootElement, "appendContinuationItemsAction")).Concat(BrowseNodes(page.RootElement, "reloadContinuationItemsCommand")).ToArray();
            if (contents.Length != 1) throw new JsonException("YouTube Music collection continuation unavailable.");
            continuation = AppendCollectionRows(contents[0], rows, cover, type, title);
        }
        return new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = id,
            [TitleProperty] = title,
            ["source_url"] = type == PlaylistType ? Origin + "/playlist?list=" + id[2..] : Origin + "/browse/" + id,
            ["description"] = BrowseText(BrowseNodes(header, "musicDescriptionShelfRenderer").Select(n => n.TryGetProperty("description", out var value) ? value : default).FirstOrDefault()),
            ["creator"] = new System.Text.Json.Nodes.JsonObject { ["name"] = header.TryGetProperty("straplineText", out var creator) ? BrowseText(creator) : "YouTube Music" },
            ["cover_big"] = cover,
            ["nb_tracks"] = rows.Count,
            ["tracks"] = rows,
            ["country"] = country,
            ["language"] = language,
            ["complete"] = true
        };
    }

    private static void ValidateContinuation(HashSet<string> tokens, string continuation)
    {
        if (!tokens.Add(continuation) || tokens.Count > 100)
            throw new JsonException("YouTube Music collection pagination did not finish.");
    }

    private static string? AppendCollectionRows(JsonElement page, JsonArray rows, string? cover, string type, string title)
    {
        foreach (var row in BrowseNodes(page, "musicResponsiveListItemRenderer"))
        {
            var song = ParseResponsiveRow(row);
            ValidateCollectionSong(song);
            var thumbnail = BrowseNodes(row, "thumbnails").FirstOrDefault();
            var image = thumbnail.ValueKind == JsonValueKind.Array && thumbnail.GetArrayLength() > 0 ? thumbnail[thumbnail.GetArrayLength() - 1].GetProperty("url").GetString() : cover;
            var album = song.Album;
            if (album is null) album = type == AlbumType ? title : "";
            rows.Add(new JsonObject
            {
                ["id"] = "",
                [VideoIdProperty] = song.VideoId,
                [TitleProperty] = song.Title,
                ["artist"] = new JsonObject { ["name"] = song.Artist ?? "" },
                [AlbumType] = new JsonObject { [TitleProperty] = album, ["cover_medium"] = image, ["cover_big"] = image },
                ["duration"] = song.DurationSeconds ?? ParseCollectionDuration(row),
                ["link"] = Origin + "/watch?v=" + song.VideoId,
                ["preview"] = "/api/youtube/browse/stream?id=" + song.VideoId
            });
        }
        return BrowseNodes(page, "nextContinuationData").Select(n => n.TryGetProperty("continuation", out var value) ? value.GetString() : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            ?? BrowseNodes(page, "continuationItemRenderer").SelectMany(n => BrowseNodes(n, "continuationCommand")).Select(n => n.TryGetProperty("token", out var value) ? value.GetString() : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static void ValidateCollectionSong([System.Diagnostics.CodeAnalysis.NotNull] YouTubeMusicSearchResult? song)
    {
        if (song is null || !Regex.IsMatch(song.VideoId, VideoIdPattern, RegexOptions.CultureInvariant, RegexTimeout))
            throw new JsonException("YouTube Music collection contains an unavailable recording.");
    }

    private static int ParseCollectionDuration(JsonElement row)
    {
        var text = BrowseText(BrowseNodes(row, "musicResponsiveListItemFixedColumnRenderer").Select(n => n.TryGetProperty("text", out var value) ? value : default).FirstOrDefault());
        var parts = text.Split(':');
        var duration = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var number) || number < 0) return 0;
            duration = duration * 60 + number;
        }
        return duration;
    }

    public static string? FindGlobalChartSelection(JsonElement root)
    {
        foreach (var item in BrowseNodes(root, "musicMultiSelectMenuItemRenderer"))
        {
            if (!item.TryGetProperty(TitleProperty, out var title) || !BrowseText(title).Equals("Global", StringComparison.OrdinalIgnoreCase)
                || !item.TryGetProperty("formItemEntityKey", out var key)) continue;
            var entity = Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(key.GetString()!)));
            var match = Regex.Match(entity, @"explore_charts_country_menu_[0-9]+([A-Z]{2}) ", RegexOptions.CultureInvariant, RegexTimeout);
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    public static JsonArray MapBrowseSections(JsonElement root, bool categoriesOnly = false)
    {
        if (categoriesOnly) return MapCategorySections(root);
        var sections = new JsonArray();
        foreach (var shelf in BrowseNodes(root, "musicCarouselShelfRenderer"))
        {
            if (!shelf.TryGetProperty("header", out var header)) continue;
            var title = BrowseText(BrowseNodes(header, TitleProperty).FirstOrDefault());
            var items = MapShelfItems(shelf, title);
            if (items.Count > 0) sections.Add(new JsonObject { [TitleProperty] = title, ["role"] = GetSectionRole(title, items), [ItemsProperty] = items });
        }
        if (sections.Count > 0) return sections;
        var tracks = new JsonArray();
        foreach (var renderer in BrowseNodes(root, "musicResponsiveListItemRenderer"))
        {
            if (MapBrowseItem(renderer, TrackType) is { } item) tracks.Add(item);
        }
        if (tracks.Count > 0) sections.Add(new JsonObject { [TitleProperty] = "Tracks", [ItemsProperty] = tracks });
        return sections;
    }

    private static JsonArray MapCategorySections(JsonElement root)
    {
        var sections = new JsonArray();
        foreach (var grid in BrowseNodes(root, "gridRenderer"))
        {
            if (!grid.TryGetProperty("header", out var header) || !grid.TryGetProperty(ItemsProperty, out var entries)) continue;
            var title = BrowseText(BrowseNodes(header, TitleProperty).FirstOrDefault());
            if (string.IsNullOrWhiteSpace(title)) continue;
            var categories = MapCategoryItems(entries);
            if (categories.Count > 0) sections.Add(new JsonObject { [TitleProperty] = title, [ItemsProperty] = categories });
        }
        return sections;
    }

    private static JsonArray MapCategoryItems(JsonElement entries)
    {
        var categories = new JsonArray();
        foreach (var navigation in BrowseNodes(entries, "musicNavigationButtonRenderer"))
        {
            if (MapBrowseItem(navigation, "browse-category") is not { } item) continue;
            if (navigation.TryGetProperty("solid", out var solid) && solid.TryGetProperty("leftStripeColor", out var color) && color.TryGetInt64(out var number)) item["stripeColor"] = number;
            categories.Add(item);
        }
        return categories;
    }

    private static JsonArray MapShelfItems(JsonElement shelf, string title)
    {
        var items = new JsonArray();
        if (!shelf.TryGetProperty(ContentsProperty, out var contents)) return items;
        var forcedType = title.Contains(VideoType, StringComparison.OrdinalIgnoreCase) ? VideoType : null;
        foreach (var entry in contents.EnumerateArray())
        {
            foreach (var property in entry.EnumerateObject())
            {
                if (MapBrowseItem(property.Value, forcedType) is { } item) items.Add(item);
            }
        }
        return items;
    }

    private static string GetSectionRole(string title, JsonArray items)
    {
        if (title.Contains(VideoType, StringComparison.OrdinalIgnoreCase))
            return items.All(item => item?["type"]?.GetValue<string>() == PlaylistType) ? "video-charts" : "videos";
        return title.Equals("Trending", StringComparison.OrdinalIgnoreCase) ? "trending" : "";
    }

    private static JsonObject? MapBrowseItem(JsonElement item, string? forcedType)
    {
        var name = BrowseText(ReadBrowseTitle(item));
        var watch = BrowseNodes(item, "watchEndpoint").FirstOrDefault(e => e.TryGetProperty(VideoIdProperty, out _));
        var browse = ReadBrowseEndpoint(item);
        var id = ReadBrowseIdentity(watch, browse);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return null;
        if (!IsSupportedBrowseIdentity(id, watch)) return null;
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant, RegexTimeout)) return null;
        var type = GetBrowseItemType(id, watch, forcedType);
        var subtitle = item.TryGetProperty(SubtitleProperty, out var sub) ? BrowseText(sub) : "";
        var song = item.TryGetProperty("flexColumns", out _) ? ParseResponsiveRow(item) : null;
        var metadata = ReadBrowseMetadata(item, type, song);
        var artist = song?.Artist ?? JoinNames(metadata?.Artists);
        if (type is TrackType or VideoType && !string.IsNullOrWhiteSpace(artist)) subtitle = artist;
        if (song is not null) name = song.Title;
        return new JsonObject
        {
            ["source"] = "ytmusic",
            ["id"] = id,
            [TitleProperty] = name,
            [SubtitleProperty] = subtitle,
            ["artist"] = artist,
            ["albumName"] = song?.Album ?? metadata?.Album,
            ["durationSeconds"] = song?.DurationSeconds ?? metadata?.DurationSeconds,
            ["type"] = type,
            ["coverUrl"] = ReadBrowseImage(item),
            [BrowseIdProperty] = ReadOptionalBrowseString(browse, BrowseIdProperty),
            ["params"] = ReadOptionalBrowseString(browse, "params"),
            ["url"] = BuildBrowseItemUrl(id, type)
        };
    }

    private static ParsedRuns? ReadBrowseMetadata(JsonElement item, string type, YouTubeMusicSearchResult? song)
    {
        if (song is null && type is TrackType or VideoType && ReadRunList(item, SubtitleProperty) is { } runs)
            return ParseMetadataRuns(SkipTypeSpecifier(runs));
        return null;
    }

    private static JsonElement ReadBrowseTitle(JsonElement item)
    {
        if (item.TryGetProperty(TitleProperty, out var title)) return title;
        if (item.TryGetProperty("buttonText", out var button)) return button;
        return BrowseNodes(item, "text").FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
    }

    private static JsonElement ReadBrowseEndpoint(JsonElement item)
    {
        if (item.TryGetProperty("navigationEndpoint", out var navigation) && navigation.TryGetProperty("browseEndpoint", out var direct)) return direct;
        return BrowseNodes(item, "browseEndpoint").FirstOrDefault();
    }

    private static string? ReadBrowseIdentity(JsonElement watch, JsonElement browse)
    {
        if (watch.ValueKind == JsonValueKind.Object) return watch.GetProperty(VideoIdProperty).GetString();
        return ReadOptionalBrowseString(browse, BrowseIdProperty);
    }

    private static string? ReadOptionalBrowseString(JsonElement browse, string property)
        => browse.ValueKind == JsonValueKind.Object && browse.TryGetProperty(property, out var value) ? value.GetString() : null;

    private static bool IsSupportedBrowseIdentity(string id, JsonElement watch)
        => watch.ValueKind == JsonValueKind.Object || id.StartsWith("VL", StringComparison.Ordinal)
            || id.StartsWith("MPRE", StringComparison.Ordinal) || id.StartsWith("FEmusic_", StringComparison.Ordinal);

    private static string GetBrowseItemType(string id, JsonElement watch, string? forcedType)
    {
        if (watch.ValueKind == JsonValueKind.Object) return forcedType == VideoType ? VideoType : TrackType;
        if (id.StartsWith("VL", StringComparison.Ordinal)) return PlaylistType;
        if (forcedType == "browse-category" || id.StartsWith("FEmusic_", StringComparison.Ordinal)) return "browse-category";
        return AlbumType;
    }

    private static string? ReadBrowseImage(JsonElement item)
    {
        var thumbnail = BrowseNodes(item, "thumbnails").FirstOrDefault();
        if (thumbnail.ValueKind == JsonValueKind.Array && thumbnail.GetArrayLength() > 0 && thumbnail[thumbnail.GetArrayLength() - 1].TryGetProperty("url", out var image)) return image.GetString();
        return "";
    }

    private static string BuildBrowseItemUrl(string id, string type)
    {
        if (type is TrackType or VideoType) return Origin + "/watch?v=" + Uri.EscapeDataString(id);
        if (type == PlaylistType) return Origin + "/playlist?list=" + Uri.EscapeDataString(id[2..]);
        return Origin + "/browse/" + Uri.EscapeDataString(id);
    }

    private static string BrowseText(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return "";
        if (value.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
            return string.Concat(runs.EnumerateArray().Select(run => run.TryGetProperty("text", out var text) ? text.GetString() : ""));
        return value.TryGetProperty("simpleText", out var simple) ? simple.GetString() ?? "" : "";
    }

    private static IEnumerable<JsonElement> BrowseNodes(JsonElement value, string name, int depth = 0)
    {
        if (depth > 24) yield break;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var found in BrowseObjectNodes(value, name, depth)) yield return found;
            yield break;
        }
        if (value.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in value.EnumerateArray())
        {
            foreach (var found in BrowseNodes(item, name, depth + 1)) yield return found;
        }
    }

    private static IEnumerable<JsonElement> BrowseObjectNodes(JsonElement value, string name, int depth)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name == name) yield return property.Value;
            else foreach (var found in BrowseNodes(property.Value, name, depth + 1)) yield return found;
        }
    }

    /// <summary>
    /// Up to <paramref name="limit"/> songs matching the query, in the provider's own relevance
    /// order. An empty list means the search answered and found nothing; a transport failure
    /// throws, because "we could not ask" and "the answer was none" are different answers and only
    /// the second one is safe to record as an unresolved track.
    /// </summary>
    public Task<IReadOnlyList<YouTubeMusicSearchResult>> SearchSongsAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
        => SearchAsync(query, limit, true, cancellationToken);

    /// <summary>
    /// Up to <paramref name="limit"/> videos matching the query. The reference uses this as the
    /// fallback after songs: an unofficial upload of the right recording still puts the right audio
    /// on the user's playlist, which is what an unresolved track would otherwise fail to do.
    /// </summary>
    public Task<IReadOnlyList<YouTubeMusicSearchResult>> SearchVideosAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
        => SearchAsync(query, limit, false, cancellationToken);

    private async Task<IReadOnlyList<YouTubeMusicSearchResult>> SearchAsync(
        string query,
        int limit,
        bool songs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return Array.Empty<YouTubeMusicSearchResult>();
        }

        var body = JsonSerializer.Serialize(new
        {
            context = new
            {
                client = new
                {
                    clientName = MusicClientName,
                    clientVersion = "1."
                        + DateTime.UtcNow.ToString(ClientVersionDateFormat, CultureInfo.InvariantCulture)
                        + ClientVersionSuffix,
                    hl = "en"
                },
                user = new { }
            },
            query = query.Trim(),
            @params = songs ? SongsParams : VideosParams
        }, SerializerOptions);

        var results = new List<YouTubeMusicSearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        string? continuation = null;
        for (var page = 0; page <= MaxContinuationPages; page++)
        {
            continuation = await RequestAsync(
                body,
                continuation,
                results,
                seen,
                limit,
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(continuation) || results.Count >= limit)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// One Innertube request, returning the shelf's continuation token or null when it is spent.
    /// The token travels in the query string - <c>ctoken</c> and <c>continuation</c> - with the
    /// original body repeated unchanged, which is how ytmusicapi's shelf continuation sends it.
    /// </summary>
    private async Task<string?> RequestAsync(
        string body,
        string? continuation,
        List<YouTubeMusicSearchResult> results,
        HashSet<string> seen,
        int limit,
        CancellationToken cancellationToken)
    {
        var url = continuation is null
            ? _searchUrl
            : $"{_searchUrl}&ctoken={Uri.EscapeDataString(continuation)}&continuation={Uri.EscapeDataString(continuation)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, JsonContentType)
        };
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation(OriginHeader, _origin);
        request.Headers.TryAddWithoutValidation(UserAgentHeader, UserAgent);
        // SOCS is the consent cookie Google sets for a visitor with no stored preferences. Sending
        // it reads as "already answered" instead of a consent interstitial where the payload was.
        request.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAI");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogWarning("YouTube Music search failed with HTTP {StatusCode}.", status);
            throw new HttpRequestException($"YouTube Music search failed with HTTP {status}.");
        }

        using var document = JsonDocument.Parse(text);
        if (!document.RootElement.TryGetProperty(ContentsProperty, out var contents))
        {
            // "No contents" is this endpoint's own spelling of "no results", not a failure.
            return null;
        }

        string? next = null;
        foreach (var section in FindSections(contents))
        {
            if (section.TryGetProperty("musicShelfRenderer", out var shelf))
            {
                next = ReadShelf(shelf, results, seen, limit) ?? next;
            }
            else if (section.TryGetProperty("musicCardShelfRenderer", out var card))
            {
                // The "Top result" card sits ahead of the shelves and holds the single most likely
                // match; skipping it would drop the candidate most likely to validate.
                Add(ParseCard(card), results, seen, limit);
            }
        }

        return next;
    }

    /// <summary>The section list, wherever the provider put it this week.</summary>
    private static IReadOnlyList<JsonElement> FindSections(JsonElement contents)
    {
        if (contents.TryGetProperty("tabbedSearchResultsRenderer", out var tabbed)
            && tabbed.TryGetProperty("tabs", out var tabs)
            && tabs.ValueKind == JsonValueKind.Array
            && tabs.GetArrayLength() > 0
            && tabs[0].TryGetProperty("tabRenderer", out var tab)
            && tab.TryGetProperty("content", out var tabContent))
        {
            return SectionsOf(tabContent);
        }

        return SectionsOf(contents);
    }

    private static IReadOnlyList<JsonElement> SectionsOf(JsonElement content)
    {
        if (content.TryGetProperty("sectionListRenderer", out var sections)
            && sections.TryGetProperty(ContentsProperty, out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            return list.EnumerateArray().ToList();
        }

        return Array.Empty<JsonElement>();
    }

    /// <summary>Parses one shelf's rows, then reports its continuation token.</summary>
    private static string? ReadShelf(
        JsonElement shelf,
        List<YouTubeMusicSearchResult> results,
        HashSet<string> seen,
        int limit)
    {
        if (!shelf.TryGetProperty(ContentsProperty, out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (results.Count >= limit)
            {
                break;
            }

            if (row.TryGetProperty("musicResponsiveListItemRenderer", out var item))
            {
                Add(ParseResponsiveRow(item), results, seen, limit);
            }
        }

        if (!shelf.TryGetProperty("continuations", out var continuations)
            || continuations.ValueKind != JsonValueKind.Array
            || continuations.GetArrayLength() == 0
            || !continuations[0].TryGetProperty("nextContinuationData", out var next)
            || !next.TryGetProperty("continuation", out var token))
        {
            return null;
        }

        var value = token.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void Add(
        YouTubeMusicSearchResult? parsed,
        List<YouTubeMusicSearchResult> results,
        HashSet<string> seen,
        int limit)
    {
        if (parsed is null || results.Count >= limit || !seen.Add(parsed.VideoId))
        {
            return;
        }

        results.Add(parsed);
    }

    /// <summary>A "Top result" card: title and video id live on the card itself, not in columns.</summary>
    private static YouTubeMusicSearchResult? ParseCard(JsonElement card)
    {
        var title = ReadNodeText(card, TitleProperty);
        if (string.IsNullOrWhiteSpace(title) || ReadVideoId(card) is not { } videoId)
        {
            return null;
        }

        var runs = ReadRunList(card, SubtitleProperty);
        var info = runs is null ? null : ParseMetadataRuns(SkipTypeSpecifier(runs));
        return new YouTubeMusicSearchResult(videoId, title, JoinNames(info?.Artists), info?.Album, info?.DurationSeconds);
    }

    /// <summary>
    /// One <c>musicResponsiveListItemRenderer</c> row.
    /// <para>
    /// The layout is column 0 = title, column 1 = "artist • album" (or "artist • views • duration"
    /// for a video), column 2 = duration. The reference reads them as ONE run list with an empty
    /// dummy run inserted between the two columns, so that every meaningful run lands on an even
    /// index and every separator on an odd one. Taking the columns independently instead turns a
    /// separator into an artist name, which then matches nothing.
    /// </para>
    /// </summary>
    private static YouTubeMusicSearchResult? ParseResponsiveRow(JsonElement row)
    {
        if (ReadVideoId(row) is not { } videoId)
        {
            return null;
        }

        var title = ReadColumnText(row, 0);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var runs = ReadColumnRuns(row, 1);
        if (runs is null)
        {
            return new YouTubeMusicSearchResult(videoId, title, null, null, null);
        }

        var second = ReadColumnRuns(row, 2);
        if (second is not null)
        {
            var dummy = new Dictionary<string, string>(StringComparer.Ordinal) { ["text"] = string.Empty };
            runs = runs.Append(dummy).Concat(second).ToList();
        }

        var info = ParseMetadataRuns(SkipTypeSpecifier(runs));
        return new YouTubeMusicSearchResult(videoId, title, JoinNames(info?.Artists), info?.Album, info?.DurationSeconds);
    }

    /// <summary>
    /// ytmusicapi's skip of the leading type label ("Song • ..."): an unlinked run followed by the
    /// dot separator and then something other than an album, which would otherwise be read as the
    /// artist.
    /// </summary>
    private static IReadOnlyList<Dictionary<string, string>> SkipTypeSpecifier(
        IReadOnlyList<Dictionary<string, string>> runs)
    {
        if (runs.Count <= 2
            || runs[0].ContainsKey(BrowseProperty)
            || Classify(runs[0]) != RunKind.Artist
            || !string.Equals(runs[1].TryGetValue("text", out var separator) ? separator : null, " • ", StringComparison.Ordinal)
            || Classify(runs[2]) == RunKind.Album)
        {
            return runs;
        }

        return runs.Skip(2).ToList();
    }

    private sealed record ParsedRuns(
        IReadOnlyList<string> Artists,
        string? Album,
        int? DurationSeconds);

    private enum RunKind
    {
        Artist,
        Album,
        Duration,
        Year,
        Views
    }

    private static ParsedRuns? ParseMetadataRuns(IReadOnlyList<Dictionary<string, string>> runs)
    {
        var artists = new List<string>();
        string? album = null;
        int? durationSeconds = null;

        for (var index = 0; index < runs.Count; index += 2)
        {
            var run = runs[index];
            var text = run.TryGetValue("text", out var value) ? value : string.Empty;
            switch (Classify(run))
            {
                case RunKind.Album:
                    album ??= text;
                    break;
                case RunKind.Artist:
                    artists.Add(text);
                    break;
                case RunKind.Duration:
                    durationSeconds ??= ParseDurationSeconds(text);
                    break;
                case RunKind.Views:
                case RunKind.Year:
                    // Deliberately not collected: a view count left unrecognised lands in the
                    // artist list, and an artist that reads "1.4M views" matches nothing.
                    break;
            }
        }

        if (artists.Count == 0 && album is null && durationSeconds is null)
        {
            return null;
        }

        return new ParsedRuns(artists, album, durationSeconds);
    }

    /// <summary>
    /// How one run is classified, mirroring ytmusicapi's <c>parse_song_run</c>: a linked run is an
    /// album when its browse id says album and an artist otherwise; an unlinked run is a duration,
    /// a year, a view count, or an unlinked artist - in that order.
    /// </summary>
    private static RunKind Classify(Dictionary<string, string> run)
    {
        if (run.TryGetValue(BrowseProperty, out var browseId))
        {
            return browseId.StartsWith("MPRE", StringComparison.Ordinal)
                   || browseId.Contains("release_detail", StringComparison.Ordinal)
                ? RunKind.Album
                : RunKind.Artist;
        }

        var text = run.TryGetValue("text", out var value) ? value : string.Empty;
        if (DurationRun.IsMatch(text))
        {
            return RunKind.Duration;
        }

        if (YearRun.IsMatch(text))
        {
            return RunKind.Year;
        }

        return ParseViews(text) is null ? RunKind.Artist : RunKind.Views;
    }

    /// <summary>
    /// The "views" token, or null when the run is not one. Needed because a video row reads
    /// "Artist • 1.4M views • 3:48" and an unrecognised run is treated as an artist.
    /// </summary>
    private static string? ParseViews(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var prefixed = false;
        if (!Regex.IsMatch(text, "[a-zA-Z]", RegexOptions.CultureInvariant, RegexTimeout))
        {
            var match = ViewsPrefix.Match(text);
            if (match.Success)
            {
                text = text[match.Length..];
                prefixed = true;
            }
        }

        if (text.Length == 0 || !char.IsDigit(text[0]))
        {
            return null;
        }

        // A bare ASCII token such as "2Pac" is an artist, not a count.
        if (!prefixed && IsAscii(text) && !text.Contains(' '))
        {
            return null;
        }

        var head = text.Split(' ')[0].Split('\u00A0');
        return string.Join("\u00A0", head.Take(2));
    }

    private static bool IsAscii(string value)
        => value.All(static character => character <= 127);

    private static int? ParseDurationSeconds(string text)
    {
        var parts = text.Split(':');
        if (parts.Length is < 1 or > 3)
        {
            return null;
        }

        var seconds = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var segment))
            {
                return null;
            }

            seconds = (seconds * 60) + segment;
        }

        return seconds;
    }

    private static string? JoinNames(IReadOnlyList<string>? names)
        => names is { Count: > 0 } ? string.Join(", ", names) : null;

    private static string? ReadColumnText(JsonElement row, int index)
    {
        var runs = ReadColumnRuns(row, index);
        if (runs is null || runs.Count == 0)
        {
            return null;
        }

        var text = string.Concat(runs.Select(static run =>
            run.TryGetValue("text", out var value) ? value : string.Empty));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static IReadOnlyList<Dictionary<string, string>>? ReadColumnRuns(JsonElement row, int index)
    {
        if (!row.TryGetProperty("flexColumns", out var columns) || columns.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = columns.EnumerateArray().ToList();
        if (index >= list.Count
            || !list[index].TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var column)
            || !column.TryGetProperty("text", out var text)
            || !text.TryGetProperty("runs", out var runs)
            || runs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return runs.EnumerateArray().Select(ToRun).ToList();
    }

    private static IReadOnlyList<Dictionary<string, string>>? ReadRunList(JsonElement row, string property)
        => row.TryGetProperty(property, out var node)
           && node.TryGetProperty("runs", out var runs)
           && runs.ValueKind == JsonValueKind.Array
            ? runs.EnumerateArray().Select(ToRun).ToList()
            : null;

    /// <summary>Flattens a run to the two facts a matcher needs: its text and, if linked, its id.</summary>
    private static Dictionary<string, string> ToRun(JsonElement run)
    {
        var flattened = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["text"] = run.TryGetProperty("text", out var text) ? text.ToString() : string.Empty
        };

        if (run.TryGetProperty("navigationEndpoint", out var navigation)
            && navigation.TryGetProperty("browseEndpoint", out var browse)
            && browse.TryGetProperty(BrowseIdProperty, out var browseId))
        {
            flattened[BrowseProperty] = browseId.ToString();
        }

        return flattened;
    }

    /// <summary>Reads a nested plain-text node such as <c>title.runs[0].text</c>.</summary>
    private static string? ReadNodeText(JsonElement node, string property)
        => node.TryGetProperty(property, out var value) ? ReadText(value) : null;

    private static string? ReadText(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.String)
        {
            var direct = node.ToString();
            return string.IsNullOrWhiteSpace(direct) ? null : direct;
        }

        if (node.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            var joined = string.Concat(runs.EnumerateArray().Select(ReadText));
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }

        return null;
    }

    /// <summary>
    /// The row's video id, from the playlist item payload, the play button, or - last, and only as
    /// a fallback - the first watch endpoint anywhere in the row. The first two are where the
    /// provider puts it; the third keeps a reshaped row from reading as "no id, skip this result".
    /// </summary>
    private static string? ReadVideoId(JsonElement row)
    {
        if (row.TryGetProperty("playlistItemData", out var item)
            && item.TryGetProperty(VideoIdProperty, out var direct)
            && IsUsable(direct.ToString()))
        {
            return direct.ToString();
        }

        if (row.TryGetProperty("overlay", out var overlayNode)
            && overlayNode.TryGetProperty("musicItemThumbnailOverlayRenderer", out var overlayRenderer)
            && overlayRenderer.TryGetProperty("content", out var content)
            && content.TryGetProperty("musicPlayButtonRenderer", out var playButton)
            && playButton.TryGetProperty("playNavigationEndpoint", out var playEndpoint)
            && playEndpoint.TryGetProperty("watchEndpoint", out var watch)
            && watch.TryGetProperty(VideoIdProperty, out var watched)
            && IsUsable(watched.ToString()))
        {
            return watched.ToString();
        }

        return FindWatchVideoId(row, 0);
    }

    private static bool IsUsable(string? videoId)
        => !string.IsNullOrWhiteSpace(videoId);

    private static string? FindWatchVideoId(JsonElement node, int depth)
    {
        if (depth > MaxNavigationDepth)
        {
            return null;
        }

        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (property.NameEquals("watchEndpoint")
                        && property.Value.ValueKind == JsonValueKind.Object
                        && property.Value.TryGetProperty(VideoIdProperty, out var videoId)
                        && IsUsable(videoId.ToString()))
                    {
                        return videoId.ToString();
                    }

                    var found = FindWatchVideoId(property.Value, depth + 1);
                    if (IsUsable(found))
                    {
                        return found;
                    }
                }

                return null;
            case JsonValueKind.Array:
                return FindWatchVideoIdInArray(node, depth);
            default:
                return null;
        }
    }
    private static string? FindWatchVideoIdInArray(JsonElement node, int depth)
    {
        foreach (var element in node.EnumerateArray())
        {
            var found = FindWatchVideoId(element, depth + 1);
            if (IsUsable(found)) return found;
        }
        return null;
    }

}
