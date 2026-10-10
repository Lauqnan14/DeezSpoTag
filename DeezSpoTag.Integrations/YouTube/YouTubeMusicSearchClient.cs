using System;
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

namespace DeezSpoTag.Integrations.YouTube;

/// <summary>One song YouTube Music's search returned, in the shape a matcher can score.</summary>
public sealed record YouTubeMusicSearchResult(
    string VideoId,
    string Title,
    string? Artist,
    string? Album,
    int? DurationSeconds);

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
public sealed class YouTubeMusicSearchClient
{
    /// <summary>The Innertube endpoint the web client uses. Not the Data API, and not quota'd.</summary>
    private const string SearchUrl =
        "https://music.youtube.com/youtubei/v1/search?alt=json&key=AIzaSyC9XL3ZjWddXya6X74dJoCTL-WEYFDNX30";

    private const string Origin = "https://music.youtube.com";

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

    private static readonly Regex DurationRun = new(@"^(\d+:)*\d+:\d+$", RegexOptions.Compiled);

    private static readonly Regex YearRun = new(@"^\d{4}$", RegexOptions.Compiled);

    /// <summary>A leading views word, a locale's colon, or a bidi mark, up to the first digit.</summary>
    private static readonly Regex ViewsPrefix =
        new("^\\D*?[\\s:\\uFF1A\\u200E-\\u200F\\u202A-\\u202E]", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILogger<YouTubeMusicSearchClient>? _logger;

    public YouTubeMusicSearchClient(HttpClient httpClient, ILogger<YouTubeMusicSearchClient>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
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
                    clientName = "WEB_REMIX",
                    clientVersion = "1."
                        + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                        + ".01.00",
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
            ? SearchUrl
            : $"{SearchUrl}&ctoken={Uri.EscapeDataString(continuation)}&continuation={Uri.EscapeDataString(continuation)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
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
        if (!document.RootElement.TryGetProperty("contents", out var contents))
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
            && sections.TryGetProperty("contents", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            return list.EnumerateArray().ToList();
        }

        return Array.Empty<JsonElement>();
    }

    /// <summary>Parses one shelf's rows, then reports its continuation token.</summary>
    private string? ReadShelf(
        JsonElement shelf,
        List<YouTubeMusicSearchResult> results,
        HashSet<string> seen,
        int limit)
    {
        if (!shelf.TryGetProperty("contents", out var rows) || rows.ValueKind != JsonValueKind.Array)
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
        var title = ReadNodeText(card, "title");
        if (string.IsNullOrWhiteSpace(title) || ReadVideoId(card) is not { } videoId)
        {
            return null;
        }

        var runs = ReadRunList(card, "subtitle");
        var info = runs is null ? null : ParseMetadataRuns(SkipTypeSpecifier(runs));
        return new YouTubeMusicSearchResult(videoId, title!, JoinNames(info?.Artists), info?.Album, info?.DurationSeconds);
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
            return new YouTubeMusicSearchResult(videoId, title!, null, null, null);
        }

        var second = ReadColumnRuns(row, 2);
        if (second is not null)
        {
            var dummy = new Dictionary<string, string>(StringComparer.Ordinal) { ["text"] = string.Empty };
            runs = runs.Append(dummy).Concat(second).ToList();
        }

        var info = ParseMetadataRuns(SkipTypeSpecifier(runs));
        return new YouTubeMusicSearchResult(videoId, title!, JoinNames(info?.Artists), info?.Album, info?.DurationSeconds);
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
            || runs[0].ContainsKey("browse")
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
        if (run.TryGetValue("browse", out var browseId))
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
        if (!Regex.IsMatch(text, "[a-zA-Z]"))
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
            && browse.TryGetProperty("browseId", out var browseId))
        {
            flattened["browse"] = browseId.ToString();
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
            && item.TryGetProperty("videoId", out var direct)
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
            && watch.TryGetProperty("videoId", out var watched)
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
                        && property.Value.TryGetProperty("videoId", out var videoId)
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
                foreach (var element in node.EnumerateArray())
                {
                    var found = FindWatchVideoId(element, depth + 1);
                    if (IsUsable(found))
                    {
                        return found;
                    }
                }

                return null;
            default:
                return null;
        }
    }
}
